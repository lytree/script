// ============================================================================
// Modules/CheckinCore.cs — auto-checkin 公共基础设施
//
// 由 login.cs / checkin.cs / trae_ui.cs 通过 #:include 复用，勿单独运行。
// 移植自 iicookies/auto-checkin（Python 标准库实现），保持接口端点与解析逻辑一致。
// ============================================================================

using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AutoCheckin;

/// <summary>同时写控制台和日志文件，保证计划任务无控制台时也有记录（对应 Python 的 _Tee）。</summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private static StreamWriter? _file;

    /// <summary>--debug 开关。</summary>
    public static bool Debug { get; set; }

    public static void OpenFile(string path)
    {
        lock (Gate)
        {
            if (_file is not null) return;
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch
            {
                // 日志文件打不开不应让主流程失败，退化成只输出到控制台
                _file = null;
            }
        }
    }

    public static void WriteLine(string line = "")
    {
        lock (Gate)
        {
            Console.Out.WriteLine(line);
            try { _file?.WriteLine(line); } catch { /* 忽略 */ }
        }
    }

    /// <summary>仅 --debug 时输出。</summary>
    public static void Dbg(string message)
    {
        if (Debug) WriteLine($"    [debug] {message}");
    }

    public static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    public static string Stamp(double unixSeconds)
    {
        try { return DateTimeOffset.FromUnixTimeSeconds((long)unixSeconds).LocalDateTime.ToString("yyyy-MM-dd HH:mm"); }
        catch { return unixSeconds.ToString("0.##"); }
    }
}

/// <summary>
/// 运行时路径解析。file-based app 编译产物在系统临时目录，
/// 凭证与日志必须落在脚本所在目录，否则每次运行都会「找不到凭证」。
/// </summary>
public static class AppPaths
{
    /// <summary>取调用方脚本文件所在目录（用 CallerFilePath 定位源码路径）。</summary>
    public static string ScriptDir([CallerFilePath] string filePath = "")
        => Path.GetDirectoryName(filePath) is { Length: > 0 } dir ? dir : AppContext.BaseDirectory;

    /// <summary>凭证目录。</summary>
    public static string CredDir([CallerFilePath] string filePath = "")
        => Path.Combine(ScriptDir(filePath), "auths");

    /// <summary>日志目录。</summary>
    public static string LogDir([CallerFilePath] string filePath = "")
        => Path.Combine(ScriptDir(filePath), "logs");
}

/// <summary>JSON 便捷访问扩展，用 JsonNode 模拟 Python 的 dict 语义。</summary>
public static class JsonExtensions
{
    /// <summary>取子节点，不存在返回 null。</summary>
    public static JsonNode? N(this JsonNode? node, string key)
        => node is JsonObject obj && obj.TryGetPropertyValue(key, out var v) && v is not null ? v : null;

    /// <summary>取子节点并转字符串，支持多个候选字段名（等价 Python 的 a or b）。</summary>
    public static string? S(this JsonNode? node, params string[] keys)
    {
        foreach (var k in keys)
        {
            var v = node.N(k);
            if (v is null) continue;
            var s = v.GetValue<string>();
            if (!string.IsNullOrEmpty(s)) return s;
        }
        return null;
    }

    /// <summary>取数值，支持多个候选字段名。</summary>
    public static double? D(this JsonNode? node, params string[] keys)
    {
        foreach (var k in keys)
        {
            var v = node.N(k);
            if (v is null) continue;
            try { return v.GetValue<double>(); } catch { /* 试下一个 */ }
        }
        return null;
    }

    /// <summary>取对象节点（不存在时返回 null）。</summary>
    public static JsonNode? O(this JsonNode? node, string key) => node.N(key);

    /// <summary>取数组节点。</summary>
    public static JsonArray? A(this JsonNode? node, string key) => node.N(key) as JsonArray;

    /// <summary>对象序列化回字符串（对应 Python 的 json.dumps(..., ensure_ascii=False)）。</summary>
    public static string Dump(this JsonNode? node, int maxLength = int.MaxValue)
    {
        if (node is null) return "{}";
        var s = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        return s.Length <= maxLength ? s : s[..maxLength];
    }

    /// <summary>写出凭证文件，保留缩进便于人工查看。</summary>
    public static void WritePretty(this JsonNode node, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>安全读取并解析 JSON。</summary>
    public static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text); } catch { return null; }
    }
}

/// <summary>极简 HTTP 封装，对应 Python 的 urllib 封装。网络异常统一抛 CheckinRequestException。</summary>
public static class Http
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// 发请求并把响应解析成 JSON 对象。
    /// HTTP 错误状态不抛异常（等价 Python 捕获 HTTPError 后返回响应体）。
    /// </summary>
    public static JsonObject Request(string method, string url, Dictionary<string, string> headers,
                                     JsonNode? body = null, int timeoutSeconds = 30)
    {
        var payload = body?.ToJsonString();
        using var req = new HttpRequestMessage(new HttpMethod(method), url);
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);

        if (payload is not null)
        {
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        }

        Log.Dbg($"{method} {url}");
        if (body is not null) Log.Dbg($"  body: {body.Dump()}");

        HttpResponseMessage resp;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            resp = Client.Send(req, cts.Token);
        }
        catch (Exception ex)
        {
            throw new CheckinRequestException($"网络请求失败: {ex.GetBaseException().Message}", ex);
        }

        using (resp)
        {
            var raw = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Log.Dbg($"  status: {(int)resp.StatusCode}");
            Log.Dbg($"  resp:  {raw[..Math.Min(500, raw.Length)]}");

            if (string.IsNullOrWhiteSpace(raw)) return [];
            var parsed = JsonNode.Parse(raw);
            if (parsed is not null) return parsed.AsObject();
            // 非 JSON 响应：保留原文与状态码，等价 Python 的 {"_raw":..., "_status":...}
            return new JsonObject { ["_raw"] = raw, ["_status"] = (int)resp.StatusCode };
        }
    }
}

/// <summary>网络请求失败（对齐 Python 版统一抛 RuntimeError 的行为）。</summary>
public sealed class CheckinRequestException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>凭证读写，对应 Python 的 cred_path / persist / load_creds。</summary>
public static class CredentialStore
{
    public static string CredDir { get; set; } = "auths";

    public static string CredPath(string name, string product) => Path.Combine(CredDir, $"{product}-{name}.json");

    public static void Persist(string name, string product, JsonObject cred) => cred.WritePretty(CredPath(name, product));

    /// <summary>扫描凭证目录，返回 [(product, cred)] 列表。</summary>
    public static List<(string Product, JsonObject Cred)> LoadAll()
    {
        var items = new List<(string, JsonObject)>();
        if (!Directory.Exists(CredDir)) return items;

        foreach (var path in Directory.GetFiles(CredDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            JsonNode? data;
            try { data = JsonNode.Parse(File.ReadAllText(path)); }
            catch (Exception ex)
            {
                Log.WriteLine($"  跳过无法解析的凭证 {name}: {ex.Message}");
                continue;
            }
            if (data is not JsonObject obj)
            {
                Log.WriteLine($"  跳过无法解析的凭证 {name}");
                continue;
            }

            var stem = Path.GetFileNameWithoutExtension(name);
            if (stem.StartsWith("workbuddy-", StringComparison.Ordinal)) items.Add(("workbuddy", obj));
            else if (stem.StartsWith("trae-", StringComparison.Ordinal)) items.Add(("trae", obj));
            else Log.WriteLine($"  跳过未知凭证文件 {name}");
        }
        return items;
    }

    /// <summary>重新扫描凭证目录，返回最新一份 Trae 凭证。</summary>
    public static JsonObject? LoadTraeCred()
    {
        foreach (var (product, cred) in LoadAll())
            if (product == "trae") return cred;
        return null;
    }
}

/// <summary>通用工具：Base64Url 解码、设备号生成。</summary>
public static class Util
{
    /// <summary>Base64Url 解码（JWT payload 用，缺 padding 自动补）。</summary>
    public static string DecodeBase64Url(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + ((4 - s.Length % 4) % 4), '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    /// <summary>生成 32 位 hex 设备 ID。</summary>
    public static string GenDeviceId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>从 JWT 载荷里按候选键取值。</summary>
    public static string? JwtPayloadValue(string? token, params string[] keys)
    {
        if (string.IsNullOrEmpty(token) || !token.Contains('.')) return null;
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;
            var payload = JsonNode.Parse(DecodeBase64Url(parts[1]));
            foreach (var k in keys)
            {
                var v = payload.S(k);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }
        catch { return null; }
    }
}