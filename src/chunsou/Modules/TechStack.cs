using System.Text.RegularExpressions;

// ─────────────────────────────────────────────────────────────
// 本文件移植自 chunsou（Python）: https://github.com/Funsiooo/chunsou
// 对应原项目文件：（移植版新增，替代 python-Wappalyzer）
// Copyright (C) Funsiooo    Licensed under GPL-3.0（见 LICENSE）
// ─────────────────────────────────────────────────────────────

namespace Chunsou;

/// <summary>
/// 技术栈识别。Python 版依赖 python-Wappalyzer（重量级、需下载 Wappalyzer 数据集），
/// 这里用内置轻量规则表覆盖主流服务端 / 前端框架与中间件，
/// 同样只取前 5 个（对齐 Python 版 keys[:5] 的输出）。
/// </summary>
public static partial class TechStack
{
    private sealed record Rule(string Name, string Pattern, string Target);

    private static readonly Rule[] Rules =
    [
        // ── 响应头 ──
        new("Nginx",        @"^nginx(?:/([\d.]+))?",                  "header:server"),
        new("Apache",       @"^Apache(?:/([\d.]+))?",                  "header:server"),
        new("IIS",          @"^Microsoft-IIS(?:/([\d.]+))?",           "header:server"),
        new("OpenResty",    @"^openresty(?:/([\d.]+))?",               "header:server"),
        new("Caddy",        @"^Caddy",                                 "header:server"),
        new("LiteSpeed",    @"^LiteSpeed",                             "header:server"),
        new("Kong",         @"^kong(?:/([\d.]+))?",                    "header:server"),
        new("Tomcat",       @"^Apache-Coyote(?:/([\d.]+))?",           "header:server"),
        new("PHP",          @"^PHP(?:/([\d.]+))?",                     "header:x-powered-by"),
        new("ASP.NET",      @"^ASP\.NET",                              "header:x-powered-by"),
        new("Express",      @"^Express",                               "header:x-powered-by"),
        new("Rails",        @"^Rails[\d.]*",                           "header:x-powered-by"),
        new("Django",       @"^Django[\d.]*",                          "header:x-powered-by"),
        new("Spring Boot",  @"^Spring Boot",                           "header:x-powered-by"),

        // ── 专用头部 ──
        new("ThinkPHP",     @"^v\d[\d.]*",                             "header:x-powered-by"),
        new("ASP.NET Core", @"^Kestrel",                               "header:server"),
        new("Cloudflare",   @"^cloudflare",                            "header:server"),
        new("WordPress",    @"^wp-",                                   "header:x-pingback"),
        new("Drupal",       @"^Drupal",                                "header:x-generator"),
        new("Joomla",       @"^\d",                                    "header:x-generator"),

        // ── Cookie ──
        new("ThinkPHP",     @"^thinkphp(?:_info|_id|_s)?",             "cookie"),
        new("WordPress",    @"wordpress_logged_in",                    "cookie"),
        new("Drupal",       @"^SESS[a-f0-9]{32}",                      "cookie"),
        new("Joomla",       @"^[a-f0-9]{32}",                          "cookie"),
        new("ASP.NET Session", @"^ASP\.NET_SessionId",                 "cookie"),
        new("JSESSIONID",   @"^JSESSIONID",                            "cookie"),
        new("PHPSESSID",    @"^PHPSESSID",                             "cookie"),
        new(".NET",         @"^\\.NET[A-Za-z]*SessionId",              "cookie"),
        new("Java",         @"^JSESSIONID|^jsessionid",                "cookie"),

        // ── HTML meta generator ──
        new("WordPress",    @"^WordPress\s*([\d.]+)?",                 "meta:generator"),
        new("Drupal",       @"^Drupal\s*([\d.]+)?",                    "meta:generator"),
        new("Joomla",       @"^Joomla\s*([\d.]+)?",                    "meta:generator"),
        new("Vue.js",       @"^Vue\.js",                               "meta:generator"),
        new("Nuxt.js",      @"^Nuxt\.js",                              "meta:generator"),
        new("Gatsby",       @"^Gatsby\s*([\d.]+)?",                    "meta:generator"),
        new("Hugo",         @"^Hugo\s*([\d.]+)?",                      "meta:generator"),
        new("Ghost",        @"^Ghost\s*([\d.]+)?",                     "meta:generator"),

        // ── 脚本 / 静态资源 ──
        new("jQuery",       @"/jquery[.\-/]",                          "body"),
        new("jQuery UI",    @"jquery-ui",                              "body"),
        new("Vue.js",       @"(?:/vue\.js|/vue\.min\.js|vue@[\d.]+)", "body"),
        new("React",        @"(?:react(?:-dom)?(?:\.production)?(?:\.min)?\.js|react@[\d.]+)", "body"),
        new("Angular",      @"ng-version=|angular(?:\.min)?\.js",      "body"),
        new("Bootstrap",    @"bootstrap(?:\.min)?\.(?:css|js)",        "body"),
        new("Layui",        @"/layui(?:\.min)?\.(?:css|js)",          "body"),
        new("Element UI",   @"element-ui",                             "body"),
        new("Ant Design",   @"antd(?:esign)?(?:\.min)?\.(?:css|js)",  "body"),
        new("ECharts",      @"echarts(?:\.min)?\.js",                  "body"),
        new("Swiper",       @"swiper(?:\.min)?\.(?:css|js)",           "body"),
        new("jQuery EasyUI",@"easyui(?:\.min)?\.js",                    "body"),
        new("ExtJS",        @"ext-all(?:\.debug)?\.js|extjs",          "body"),
        new("Druid",        @"druid(?:/index\.html)?",                 "body"),
        new("KindEditor",   @"kindeditor",                             "body"),
        new("UEditor",      @"ueditor",                                "body"),
        new("wangEditor",   @"wangeditor",                             "body"),
        new("WebSocket",    @"ws://|wss://",                           "body"),
        new("Vant",         @"vant(?:@[\d.]+)?(?:\.min)?\.(?:js|css)", "body"),
    ];

    [GeneratedRegex(@"<meta[^>]+name\s*=\s*[""']generator[""'][^>]*content\s*=\s*[""'](?<v>[^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex GeneratorRegex();

    [GeneratedRegex(@"<meta[^>]+content\s*=\s*[""'](?<v>[^""']*)[""'][^>]*name\s*=\s*[""']generator[""']", RegexOptions.IgnoreCase)]
    private static partial Regex GeneratorRegexAlt();

    /// <summary>
    /// 从 HTML meta 标签、响应头、脚本引用中识别技术栈，返回去重后的列表。
    /// </summary>
    public static List<string> Detect(string html, HttpHeadersView headers)
    {
        var found = new List<string>(8);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string name)
        {
            if (name.Length > 0 && seen.Add(name)) found.Add(name);
        }

        // meta generator
        var gen = GeneratorRegex().Match(html);
        if (!gen.Success) gen = GeneratorRegexAlt().Match(html);
        if (gen.Success)
        {
            var value = HtmlEntity.Decode(gen.Groups["v"].Value).Trim();
            foreach (var r in Rules)
                if (r.Target == "meta:generator" && Regex.IsMatch(value, r.Pattern, RegexOptions.IgnoreCase))
                    Add(r.Name);
        }

        // 响应头 / Cookie
        foreach (var (key, value) in headers.Pairs)
        {
            foreach (var r in Rules)
            {
                if (r.Target != "header:server" && r.Target != "header:x-powered-by" &&
                    r.Target != "header:x-pingback" && r.Target != "header:x-generator")
                    continue;
                if (!key.Equals(r.Target[7..], StringComparison.OrdinalIgnoreCase)) continue;
                if (Regex.IsMatch(value, r.Pattern, RegexOptions.IgnoreCase))
                    Add(r.Name);
            }
        }

        foreach (var cookie in headers.Cookies)
        {
            foreach (var r in Rules)
            {
                if (r.Target != "cookie") continue;
                if (Regex.IsMatch(cookie, r.Pattern, RegexOptions.IgnoreCase))
                    Add(r.Name);
            }
        }

        // HTML 正文
        foreach (var r in Rules)
        {
            if (r.Target != "body") continue;
            if (html.Contains(r.Pattern.Replace(@"\", ""), StringComparison.OrdinalIgnoreCase))
                Add(r.Name);
        }

        // 常见 meta 提示
        if (html.Contains("__NEXT_DATA__", StringComparison.Ordinal)) Add("Next.js");
        if (html.Contains("window.__NUXT__", StringComparison.Ordinal)) Add("Nuxt.js");
        if (html.Contains("_nuxt/", StringComparison.Ordinal)) Add("Nuxt.js");
        if (html.Contains("data-reactroot", StringComparison.Ordinal)) Add("React");
        if (html.Contains("ng-version", StringComparison.Ordinal)) Add("Angular");
        if (html.Contains("wp-content", StringComparison.Ordinal) || html.Contains("wp-includes", StringComparison.Ordinal)) Add("WordPress");
        if (html.Contains("/sites/default/files", StringComparison.Ordinal)) Add("Drupal");
        if (html.Contains("templates/", StringComparison.Ordinal) && html.Contains("Joomla", StringComparison.OrdinalIgnoreCase)) Add("Joomla");

        return found;
    }
}

/// <summary>响应头轻量视图，避免到处传递 HttpResponseMessage</summary>
public sealed class HttpHeadersView
{
    public required IReadOnlyList<KeyValuePair<string, string>> Pairs { get; init; }
    public required IReadOnlyList<string> Cookies { get; init; }

    public string? this[string key]
    {
        get
        {
            foreach (var (k, v) in Pairs)
                if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return v;
            return null;
        }
    }
}

public static partial class HtmlEntity
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        ["amp"] = "&", ["lt"] = "<", ["gt"] = ">", ["quot"] = "\"", ["apos"] = "'",
        ["nbsp"] = " ", ["#39"] = "'", ["#34"] = "\"", ["#38"] = "&",
    };

    [GeneratedRegex(@"&(?<n>#?[a-zA-Z0-9]+);")]
    private static partial Regex EntityRegex();

    public static string Decode(string input)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains('&')) return input;

        return EntityRegex().Replace(input, m =>
        {
            var name = m.Groups["n"].Value;
            return Map.TryGetValue(name, out var v) ? v : m.Value;
        });
    }
}
