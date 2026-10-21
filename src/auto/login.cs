#!/usr/bin/env dotnet run
#:include Modules/*.cs

// ============================================================================
// login.cs — 首次 OAuth 登录，获取签到凭证
//
// 移植自 iicookies/auto-checkin 的 login.py。
// 工作原理：
//   1. WorkBuddy 走设备授权：拿 state → 开浏览器 → 轮询换 token
//      Trae 走本地回调：开浏览器 → 监听 127.0.0.1:18080 捕获 refreshToken
//   2. Trae 再用 refreshToken 换一次 accessToken，拿到完整凭证
//   3. 存盘到 auths/{product}-{name}.json
//
// 用法:
//   dotnet run src/auto/login.cs -- workbuddy    # 登录 WorkBuddy (CodeBuddy 国内版)
//   dotnet run src/auto/login.cs -- trae          # 登录 Trae
//   dotnet run src/auto/login.cs -- trae --debug  # 打印调试信息
//
// 注意:
//   登录过程中浏览器会跳转到 127.0.0.1，那是正常的——本脚本就在监听该地址捕获 token。
// ============================================================================

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AutoCheckin;

// 控制台统一 UTF-8，避免中文/emoji 在 GBK 终端抛异常
try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 忽略 */ }

// 凭证落在脚本所在目录（file-based app 编译产物在系统临时目录，不能用 BaseDirectory）
CredentialStore.CredDir = AppPaths.CredDir();
Directory.CreateDirectory(CredentialStore.CredDir);

var product = args.FirstOrDefault()?.ToLowerInvariant();
Log.Debug = args.Contains("--debug");

if (product is null || product is not ("workbuddy" or "trae"))
{
    Console.Error.WriteLine("用法: login.cs <workbuddy|trae> [--debug]");
    return 2;
}

Log.WriteLine($"\n=== {product} 登录 ===\n");

try
{
    return product == "workbuddy" ? await LoginWorkbuddyAsync() : await LoginTraeAsync();
}
catch (OperationCanceledException)
{
    Log.WriteLine("\n  已取消。");
    return 130;
}
catch (Exception ex)
{
    Log.WriteLine($"\n  ❌ 登录失败: {ex.Message}");
    if (Log.Debug) Log.WriteLine(ex.ToString());
    return 1;
}

// ---------------------------------------------------------------------------
// WorkBuddy：OAuth 设备授权流程
// ---------------------------------------------------------------------------
static async Task<int> LoginWorkbuddyAsync()
{
    const string authBase = "https://copilot.tencent.com";

    Log.WriteLine("  请求授权 state...");
    var stateResp = Http.Request("POST", $"{authBase}/v2/plugin/auth/state?platform=CLI",
        new Dictionary<string, string>
        {
            ["User-Agent"] = WorkBuddyClient.UserAgent,
            ["Content-Type"] = "application/json",
        }, new JsonObject());

    var stateData = stateResp.O("data");
    var state = stateData.S("state");
    var authUrl = stateData.S("authUrl");

    if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(authUrl))
    {
        Log.WriteLine($"  无法获取授权 state，返回: {stateResp.Dump()}");
        return 1;
    }

    Log.WriteLine($"  授权 state: {state[..Math.Min(8, state.Length)]}...");
    Log.WriteLine("\n  即将打开浏览器，请在浏览器里完成腾讯账号登录。");
    Log.WriteLine($"  登录地址: {authUrl}");
    OpenBrowser(authUrl);

    Log.WriteLine("\n  轮询等待登录完成（最多 5 分钟）...");
    var headers = new Dictionary<string, string> { ["User-Agent"] = WorkBuddyClient.UserAgent };

    for (var i = 0; i < 60; i++)  // 每 5 秒轮询一次，最多 5 分钟
    {
        await Task.Delay(5000);

        JsonObject tokenResp;
        try
        {
            tokenResp = Http.Request("GET", $"{authBase}/v2/plugin/auth/token?state={state}", headers);
        }
        catch (CheckinRequestException ex)
        {
            Log.Dbg($"轮询异常: {ex.Message}");
            continue;
        }

        var tokenData = tokenResp.O("data");
        var accessToken = tokenData.S("accessToken", "access_token");
        if (string.IsNullOrEmpty(accessToken)) continue;

        var refreshToken = tokenData.S("refreshToken", "refresh_token");
        var expiresIn = tokenData.D("expiresIn", "expires_in") ?? 0;
        var domain = tokenData.S("domain") ?? "codebuddy.cn";

        // 从 token 里简单提取 uid（JWT payload 段）
        var uid = Util.JwtPayloadValue(accessToken, "preferred_username", "uid", "userId", "sub") ?? "workbuddy";

        var expiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (expiresIn > 0 ? expiresIn : 7 * 24 * 3600);
        var cred = new JsonObject
        {
            ["uid"] = uid,
            ["accessToken"] = accessToken,
            ["refreshToken"] = refreshToken,
            ["expiresAt"] = expiresAt,
            ["domain"] = domain,
        };

        var path = CredentialStore.CredPath(uid, "workbuddy");
        cred.WritePretty(path);

        Log.WriteLine("\n  ✅ WorkBuddy 登录成功");
        Log.WriteLine($"  UID: {uid}");
        Log.WriteLine($"  域: {domain}");
        Log.WriteLine($"  过期: {Log.Stamp(expiresAt)}");
        Log.WriteLine($"  凭证已保存: {path}");
        return 0;
    }

    Log.WriteLine("\n  超时：5 分钟内未完成登录，请重试。");
    return 1;
}

// ---------------------------------------------------------------------------
// Trae：OAuth 浏览器回调流程
// ---------------------------------------------------------------------------
static async Task<int> LoginTraeAsync()
{
    const int port = 18080;
    const string authBase = "https://api.trae.com.cn";

    var deviceId = Util.GenDeviceId();
    var machineId = Util.GenDeviceId();

    var loginUrl =
        "https://www.trae.cn/authorization?client_id=en1oxy7wnw8j9n" +
        "&auth_from=solo&login_channel=native_ide&plugin_version=2.3.62834" +
        $"&auth_callback_url={Uri.EscapeDataString($"http://127.0.0.1:{port}/authorize")}" +
        "&x_app_version=0.1.43&x_app_type=stable" +
        $"&device_id={deviceId}&machine_id={machineId}";

    Log.WriteLine("\n  即将打开浏览器，请在浏览器里完成 Trae 账号登录（手机号+验证码）。");
    Log.WriteLine($"  登录地址: {loginUrl}");
    Log.WriteLine($"  本地回调: http://127.0.0.1:{port}/authorize");
    Log.WriteLine("  等待回调（最多 5 分钟）...\n");

    // 启动服务器并打开浏览器
    var browserTask = Task.Run(() => OpenBrowser(loginUrl));

    var result = await CallbackServer.WaitAsync(port, TimeSpan.FromMinutes(5));
    if (result is null)
    {
        Log.WriteLine("  超时：5 分钟内未收到登录回调，请重试。");
        return 1;
    }

    Log.Dbg($"回调参数: {result.Dump()}");

    // 回调 query 格式（实测）：含 refreshToken / data(同refreshToken) / refreshExpireAt / host / userRegion
    // 部分版本也含 userInfo / userJwt（URL编码的 JSON）。统一从 refreshToken 换 accessToken。
    var refreshToken = result.S("refreshToken", "refresh_token", "data");
    var userInfo = ParseNestedJson(result.S("userInfo"));
    var userJwt = ParseNestedJson(result.S("userJwt"));

    // 先尝试直接从回调的 userJwt 取 token（部分版本有）
    var accessToken = userJwt.S("Token", "token", "AccessToken");
    var userId = userInfo.S("UserID") ?? userJwt.S("UserID") ?? "";
    var screenName = userInfo.S("ScreenName", "screenName") ?? "";
    var boundDeviceId = deviceId;  // 默认用登录时生成的
    double? expireAt = null;

    // 用 refreshToken 换 accessToken（统一走，拿完整的 token/userId/deviceId）
    if (string.IsNullOrEmpty(accessToken) && !string.IsNullOrEmpty(refreshToken))
    {
        Log.WriteLine("  用 refreshToken 换 accessToken...");
        var exch = Http.Request("POST", $"{authBase}/cloudide/api/v3/trae/oauth/ExchangeToken",
            new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            new JsonObject
            {
                ["ClientID"] = "en1oxy7wnw8j9n",
                ["RefreshToken"] = refreshToken,
                ["ClientSecret"] = "-",
                ["UserID"] = userId,
            });

        var exchResult = exch.O("Result") ?? exch.O("data");
        accessToken = exchResult.S("Token", "AccessToken");
        var newRefresh = exchResult.S("RefreshToken");
        if (!string.IsNullOrEmpty(newRefresh)) refreshToken = newRefresh;
        // BoundDeviceID 是服务端绑定到该 refreshToken 的设备 ID
        boundDeviceId = exchResult.S("BoundDeviceID") ?? boundDeviceId;
        expireAt = exchResult.D("TokenExpireAt");
        Log.Dbg($"ExchangeToken 结果: {exch.Dump()}");

        if (string.IsNullOrEmpty(accessToken))
            Log.WriteLine($"  ⚠️ ExchangeToken 未返回 Token，完整响应: {exch.Dump(400)}");
    }
    else if (!string.IsNullOrEmpty(accessToken))
    {
        // 已从回调直接拿到 token，估算过期
        expireAt = userJwt.D("TokenExpireAt") ?? result.D("refreshExpireAt");
    }

    if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
    {
        Log.WriteLine("  ❌ 无法从回调中提取 token。");
        Log.WriteLine($"  回调参数: {result.Dump()}");
        return 1;
    }

    // 计算 expiresAt（TokenExpireAt / refreshExpireAt 通常是毫秒时间戳）
    var ts = expireAt ?? 0;
    var expiresAt = ts <= 0
        ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600
        : (ts > 1e12 ? ts / 1000 : ts);

    var name = !string.IsNullOrEmpty(screenName) ? screenName
        : !string.IsNullOrEmpty(userId) ? userId : "trae";

    var cred = new JsonObject
    {
        ["userId"] = userId,
        ["accessToken"] = accessToken,
        ["refreshToken"] = refreshToken,
        ["expiresAt"] = expiresAt,
        ["deviceId"] = boundDeviceId,
        ["screenName"] = screenName,
        ["machineId"] = machineId,
    };
    var path = CredentialStore.CredPath(name, "trae");
    cred.WritePretty(path);

    Log.WriteLine("\n  ✅ Trae 登录成功");
    Log.WriteLine($"  用户: {(string.IsNullOrEmpty(screenName) ? userId : screenName)}");
    Log.WriteLine($"  过期: {Log.Stamp(expiresAt)}");
    Log.WriteLine($"  凭证已保存: {path}");
    Log.WriteLine($"\n  提示: deviceId = {boundDeviceId}");
    Log.WriteLine("  （Trae 按设备去重，请勿改动此值，否则可能影响签到）");
    return 0;
}

// ---------------------------------------------------------------------------
// 辅助
// ---------------------------------------------------------------------------

/// <summary>解析回调里 URL 编码的 JSON 字段（如 userInfo / userJwt）。</summary>
static JsonObject? ParseNestedJson(string? raw)
{
    if (string.IsNullOrEmpty(raw)) return null;
    try
    {
        var unquoted = Uri.UnescapeDataString(raw);
        return JsonNode.Parse(unquoted) as JsonObject;
    }
    catch (Exception ex)
    {
        Log.Dbg($"嵌套 JSON 解析失败: {raw} ({ex.Message})");
        return null;
    }
}

static void OpenBrowser(string url)
{
    try
    {
        // Windows 用 ShellExecute，避免 start 命令的转义问题
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
    }
    catch (Exception ex)
    {
        Log.Dbg($"打开浏览器失败: {ex.Message}");
        Log.WriteLine($"  请手动在浏览器打开: {url}");
    }
}

/// <summary>本地 OAuth 回调服务器：接收一次回调即返回。</summary>
static class CallbackServer
{
    private const string SuccessHtml =
        "<html><body style='font-family:sans-serif;text-align:center;padding:40px'>" +
        "<h2>✅ 登录成功</h2>" +
        "<p>已捕获登录凭证，现在可以关闭此页面并回到命令行。</p>" +
        "</body></html>";

    /// <summary>解析 URL 查询串为 key/value（同名参数后者覆盖前者）。</summary>
    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (query.StartsWith('?')) query = query[1..];
        if (query.Length == 0) return dict;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            var key = idx < 0 ? pair : pair[..idx];
            var value = idx < 0 ? "" : pair[(idx + 1)..];
            dict[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        return dict;
    }

    public static async Task<JsonObject?> WaitAsync(int port, TimeSpan timeout)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try { listener.Start(); }
        catch (HttpListenerException ex)
        {
            Log.WriteLine($"  启动本地回调服务器失败（端口 {port} 可能被占用）: {ex.Message}");
            return null;
        }

        Log.WriteLine($"  本地回调服务器监听 http://127.0.0.1:{port}/ ...");

        var cts = new CancellationTokenSource(timeout);
        try
        {
            var context = await listener.GetContextAsync().WaitAsync(cts.Token);

            var query = ParseQueryString(context.Request.Url?.Query ?? string.Empty);
            var result = new JsonObject();
            foreach (var (key, value) in query)
                result[key] = value;

            Log.Dbg($"收到回调: {context.Request.Url?.PathAndQuery}");

            var bytes = Encoding.UTF8.GetBytes(SuccessHtml);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
            return result;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            listener.Stop();
        }
    }
}