using System.Text;
using System.Text.Json;

// ─────────────────────────────────────────────────────────────
// 本文件移植自 chunsou（Python）: https://github.com/Funsiooo/chunsou
// 对应原项目文件：modules/api/fofa.py, modules/api/hunter.py
// Copyright (C) Funsiooo    Licensed under GPL-3.0（见 LICENSE）
// ─────────────────────────────────────────────────────────────

namespace Chunsou;

/// <summary>
/// FOFA 资产收集，对应 modules/api/fofa.py。
/// 接口：https://fofa.info/api/v1/search/all?email=&amp;key=&amp;qbase64=&amp;page=&amp;size=&amp;fields=
/// </summary>
public static class FofaApi
{
    private const string Endpoint = "https://fofa.info/api/v1/search/all";
    private const string Fields = "host,title,domain,link,ip,port,base_protocol,server";

    public static async Task<int> RunAsync(HttpClient client, CliOptions opt, CancellationToken ct = default)
    {
        var email = Config.FofaEmail;
        var key = Config.FofaKey;

        Console.WriteLine(Ansi.Info($"正在调用 FOFA API（查询：{opt.Fofa}），凭据配置于 {Path.Combine(Config.RootDir, "config.ini")}"));

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(key))
        {
            Console.Error.WriteLine(Ansi.C(Ansi.Red,
                "FOFA 调用失败：请在 config.ini 的 [fofa_email] / [fofa_api_key] 填写凭据，" +
                "或设置环境变量 FOFA_EMAIL / FOFA_KEY。"));
            return 1;
        }

        var qbase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(opt.Fofa!));
        var size = Config.FofaSize;

        var url = $"{Endpoint}?email={Uri.EscapeDataString(email)}&key={Uri.EscapeDataString(key)}" +
                  $"&qbase64={Uri.EscapeDataString(qbase64)}&page=1&size={Uri.EscapeDataString(size)}" +
                  $"&fields={Uri.EscapeDataString(Fields)}";

        var rows = new List<FofaXlsxRow>();
        try
        {
            using var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine(Ansi.C(Ansi.Red, $"FOFA API 返回 HTTP {(int)resp.StatusCode}：{PageParser.Truncate(body, 200)}"));
                return 1;
            }

            rows = ParseResults(body);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Ansi.C(Ansi.Red, $"FOFA 调用失败：{ex.Message}"));
            return 1;
        }

        foreach (var row in rows)
            Console.WriteLine(Ansi.Success(row.Host ?? ""));

        var outPath = ResultWriter.ResolveOutput(opt.Output, "fofa_result.xlsx");
        ResultWriter.WriteXlsx(outPath, rows);

        Console.WriteLine(Ansi.Info($"FOFA 结果已保存到 {Ansi.C(Ansi.Yellow, outPath)}"));
        return 0;
    }

    private static List<FofaXlsxRow> ParseResults(string body)
    {
        var list = new List<FofaXlsxRow>();

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array) continue;

            var cells = item.EnumerateArray().Select(ToStr).ToArray();

            // 字段顺序：host,title,domain,link,ip,port,base_protocol,server
            list.Add(new FofaXlsxRow
            {
                Host = At(cells, 0),
                Title = At(cells, 1),
                Domain = At(cells, 2),
                Link = At(cells, 3),
                Ip = At(cells, 4),
                Port = At(cells, 5),
                Protocol = At(cells, 6),
                Server = At(cells, 7),
            });
        }

        return list;
    }

    private static string At(string[] cells, int i) => i < cells.Length ? cells[i] : "";

    private static string ToStr(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => e.ToString(),
    };
}

/// <summary>
/// Hunter（奇安信Hunter）资产收集，对应 modules/api/hunter.py。
/// 接口：https://hunter.qianxin.com/openApi/search?api-key=&amp;search=&amp;page=&amp;page_size=&amp;is_web=
/// </summary>
public static class HunterApi
{
    private const string Endpoint = "https://hunter.qianxin.com/openApi/search";

    public static async Task<int> RunAsync(HttpClient client, CliOptions opt, CancellationToken ct = default)
    {
        Console.WriteLine(Ansi.Info($"正在调用 Hunter API（查询：{opt.Hunter}），凭据配置于 {Path.Combine(Config.RootDir, "config.ini")}"));

        var key = Config.HunterKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            Console.Error.WriteLine(Ansi.C(Ansi.Red,
                "Hunter 调用失败：请在 config.ini 的 [hunter_api_key] 填写凭据，或设置环境变量 HUNTER_KEY。"));
            return 1;
        }

        var search = Convert.ToBase64String(Encoding.UTF8.GetBytes(opt.Hunter!));
        var size = Config.HunterSize;

        var url = $"{Endpoint}?api-key={Uri.EscapeDataString(key)}&search={Uri.EscapeDataString(search)}" +
                  $"&page=1&page_size={Uri.EscapeDataString(size)}&is_web=3";

        var rows = new List<HunterXlsxRow>();
        try
        {
            using var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine(Ansi.C(Ansi.Red, $"Hunter API 返回 HTTP {(int)resp.StatusCode}：{PageParser.Truncate(body, 200)}"));
                return 1;
            }

            rows = ParseResults(body);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Ansi.C(Ansi.Red, $"Hunter 调用失败：{ex.Message}"));
            return 1;
        }

        foreach (var row in rows)
            Console.WriteLine(Ansi.Success(row.Url ?? ""));

        var outPath = ResultWriter.ResolveOutput(opt.Output, "hunter_result.xlsx");
        ResultWriter.WriteXlsx(outPath, rows);

        Console.WriteLine(Ansi.Info($"Hunter 结果已保存到 {Ansi.C(Ansi.Yellow, outPath)}"));
        return 0;
    }

    private static List<HunterXlsxRow> ParseResults(string body)
    {
        var rows = new List<HunterXlsxRow>();

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("arr", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
            return rows;

        foreach (var item in arr.EnumerateArray())
        {
            var component = item.TryGetProperty("component", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().FirstOrDefault()
                : default;

            rows.Add(new HunterXlsxRow
            {
                Url = Str(item, "url"),
                Ip = Str(item, "ip"),
                Port = Str(item, "port"),
                WebTitle = Str(item, "web_title"),
                Domain = Str(item, "domain"),
                StatusCode = Str(item, "status_code"),
                Protocol = Str(item, "protocol"),
                BaseProtocol = Str(item, "base_protocol"),
                Os = Str(item, "os"),
                Company = Str(item, "company"),
                Number = Str(item, "number"),
                Country = Str(item, "country"),
                City = Str(item, "city"),
                AppName = component.ValueKind == JsonValueKind.Undefined ? "" : Str(component, "name"),
                AppVersion = component.ValueKind == JsonValueKind.Undefined ? "" : Str(component, "version"),
            });
        }

        return rows;
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()
            : "";
}
