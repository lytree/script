using System.Net;
using System.Text;
using System.Text.RegularExpressions;

// ─────────────────────────────────────────────────────────────
// 本文件移植自 chunsou（Python）: https://github.com/Funsiooo/chunsou
// 对应原项目文件：modules/core/scan.py, modules/core/icon.py
// Copyright (C) Funsiooo    Licensed under GPL-3.0（见 LICENSE）
// ─────────────────────────────────────────────────────────────

namespace Chunsou;

public static partial class PageParser
{
    /// <summary>
    /// .NET Core 默认不含 GB18030 / Big5 等代码页，需显式注册 CodePages  Provider。
    /// 注册失败时中文站点会退化为 UTF-8 解码，不影响主流程。
    /// </summary>
    private static bool _codePagesReady;

    static PageParser() => TryRegisterCodePages();

    private static void TryRegisterCodePages()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _codePagesReady = true;
        }
        catch (Exception)
        {
            _codePagesReady = false;
        }
    }

    /// <summary>
    /// 兜底编码表。GB18030 在部分精简运行时不可用，
    /// 因此延迟解析并跳过失败的项，而不是让整个类型初始化崩掉。
    /// </summary>
    private static readonly Lazy<Encoding[]> FallbackEncodings = new(() =>
    {
        if (!_codePagesReady) TryRegisterCodePages();

        return new[]
        {
            Encoding.UTF8,
            TryGetEncoding("gb18030"),
            TryGetEncoding("big5"),
            Encoding.UTF8,
        }.Where(e => e is not null).Select(e => e!).ToArray();
    });

    private static Encoding? TryGetEncoding(string name)
    {
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (Exception)
        {
            // CodePagesEncodingProvider 未注册时中文站点会退化为 UTF-8，不影响主流程
            return null;
        }
    }

    /// <summary>
    /// 解码响应体，对应 Python 的 _decode_response_content：
    /// 优先用声明编码，再退回 UTF-8 / GB18030 逐个试，最后强制 UTF-8 忽略错误。
    /// </summary>
    public static string Decode(byte[] content, string? declaredEncoding)
    {
        if (content.Length == 0) return string.Empty;

        var candidates = new List<string>(5);
        if (!string.IsNullOrWhiteSpace(declaredEncoding))
            candidates.Add(declaredEncoding.Trim('"', '\'', ' '));

        foreach (var e in FallbackEncodings.Value)
            if (!candidates.Contains(e.WebName))
                candidates.Add(e.WebName);

        foreach (var name in candidates)
        {
            var enc = ResolveEncoding(name);
            if (enc is null) continue;

            try
            {
                var strict = (Encoding)enc.Clone();
                strict.DecoderFallback = DecoderFallback.ExceptionFallback;
                return strict.GetString(content);
            }
            catch (DecoderFallbackException) { }
            catch (ArgumentException) { }
        }

        return Encoding.UTF8.GetString(content);
    }

    private static Encoding? ResolveEncoding(string name)
    {
        // GB2312/GBK 一律升级到 GB18030，超集更宽容
        if (name.Equals("gb2312", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("gbk", StringComparison.OrdinalIgnoreCase))
            return TryGetEncoding("gb18030");

        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    [GeneratedRegex(@"<title[^>]*>(?<t>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<(script|style|noscript)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StripTagsRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"data:image/[^;]+;base64,[A-Za-z0-9+/=]+")]
    private static partial Regex Base64ImageRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"<link[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTagRegex();

    [GeneratedRegex(@"rel\s*=\s*[""']?(?<v>[^""'\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RelAttrRegex();

    [GeneratedRegex(@"href\s*=\s*[""'](?<v>[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefAttrRegex();

    /// <summary>提取 title，对应 Python 的 _extract_title</summary>
    public static string? ExtractTitle(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var m = TitleRegex().Match(html);
        if (!m.Success) return null;

        var title = HtmlEntity.Decode(TagRegex().Replace(m.Groups["t"].Value, " "))
            .Trim().Trim('\u3000');
        return title.Length > 0 ? Collapse(title) : null;
    }

    /// <summary>清洗正文：去脚本样式标签、压缩空白，对应 Python 的 _clean_page_text</summary>
    public static string CleanText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var text = StripTagsRegex().Replace(html, "\n");
        text = Base64ImageRegex().Replace(text, " ");
        text = TagRegex().Replace(text, " ");
        text = HtmlEntity.Decode(text);

        var sb = new StringBuilder(text.Length);
        foreach (var line in text.Split('\n'))
        {
            var compact = Collapse(line);
            if (compact.Length > 0) sb.Append(compact).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    public static string Collapse(string s) =>
        WhitespaceRegex().Replace(HtmlEntity.Decode(s), " ").Trim();

    public static string Truncate(string? text, int limit)
    {
        if (text is null) return string.Empty;
        var clean = Collapse(text);
        return clean.Length <= limit ? clean : clean[..limit].TrimEnd() + "...";
    }

    /// <summary>
    /// 提取 favicon 地址，对应 Python 的 get_ico_url：
    /// 找 rel 含 icon 的 link，回退 /favicon.ico。
    /// </summary>
    public static Uri? GetIconUrl(string baseUrl, string? html)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(html))
            {
                foreach (Match tag in LinkTagRegex().Matches(html))
                {
                    var rel = RelAttrRegex().Match(tag.Value);
                    if (!rel.Success || !rel.Groups["v"].Value.Contains("icon", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var href = HrefAttrRegex().Match(tag.Value);
                    if (!href.Success) continue;

                    var raw = HtmlEntity.Decode(href.Groups["v"].Value.Trim());
                    if (raw.Length == 0) continue;
                    if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                    if (Uri.TryCreate(new Uri(baseUrl), raw, out var abs)) return abs;
                }
            }

            return Uri.TryCreate(new Uri(baseUrl), "/favicon.ico", out var fallback) ? fallback : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>计算 favicon 的 mmh3 哈希，对应 Python 的 get_hash</summary>
    public static string IconHash(byte[] content) =>
        Murmur3.Hash32(StandBase64(content)).ToString();

    /// <summary>标准 base64，每 76 字符换行，末尾补换行（对齐 icon.py 的 stand_base64）</summary>
    public static byte[] StandBase64(byte[] raw)
    {
        var b64 = Convert.ToBase64String(raw);
        var buf = new byte[b64.Length + (b64.Length / 76) + 2];
        var pos = 0;

        for (var i = 0; i < b64.Length; i++)
        {
            buf[pos++] = (byte)b64[i];
            if ((i + 1) % 76 == 0) buf[pos++] = (byte)'\n';
        }

        buf[pos++] = (byte)'\n';
        Array.Resize(ref buf, pos);
        return buf;
    }

    /// <summary>取正文里适合展示的短片段，对应 Python 的 _extract_body_snippets</summary>
    public static List<string> BodySnippets(string cleanText, int limit = 4)
    {
        var result = new List<string>(limit);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in cleanText.Split('\n'))
        {
            var compact = Collapse(line);
            if (compact.Length is < 2 or > 80) continue;
            if (!seen.Add(compact)) continue;

            result.Add(compact);
            if (result.Count >= limit) break;
        }

        return result;
    }

    private static readonly HashSet<string> TextStopwords = new(StringComparer.Ordinal)
    {
        "http", "https", "www", "com", "登录", "系统", "平台", "首页", "欢迎", "请输入", "用户名",
        "密码", "验证码", "版权所有", "copyright", "admin", "index", "true", "false",
    };

    [GeneratedRegex(@"[\u4e00-\u9fffA-Za-z0-9._\-/]{2,40}")]
    private static partial Regex KeywordRegex();

    [GeneratedRegex(@"[A-Za-z0-9_\-]{2,30}")]
    private static partial Regex PathTokenRegex();

    /// <summary>抽取正文关键词，供 AI 语义分析使用，对应 Python 的 _extract_body_keywords</summary>
    public static List<string> BodyKeywords(string url, string? title, string cleanText, int limit = 12)
    {
        var pathTokens = string.Join(" ", PathTokenRegex().Matches(SafePath(url)).Select(m => m.Value));
        var head = string.Join('\n', title ?? "", pathTokens, cleanText.Length > 1000 ? cleanText[..1000] : cleanText);

        var result = new List<string>(limit);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in KeywordRegex().Matches(head))
        {
            var word = m.Value;
            if (TextStopwords.Contains(word.ToLowerInvariant())) continue;
            if (word.All(char.IsDigit)) continue;
            if (!seen.Add(word)) continue;

            result.Add(word);
            if (result.Count >= limit) break;
        }

        return result;
    }

    private static string SafePath(string url)
    {
        try { return new Uri(url).PathAndQuery; }
        catch (UriFormatException) { return url; }
    }

    [GeneratedRegex(@"<(input|textarea)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex FieldTagRegex();

    [GeneratedRegex(@"<(button)[^>]*>(?<t>.*?)</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ButtonRegex();

    [GeneratedRegex(@"\b(?:placeholder|name|id)\s*=\s*[""'](?<v>[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex AttrRegex();

    [GeneratedRegex(@"<form[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex FormRegex();

    /// <summary>DOM 特征提取（登录表单、字段标签、按钮文案），对应 Python 的 _extract_dom_features</summary>
    public static Dictionary<string, object> DomFeatures(string? html)
    {
        var result = new Dictionary<string, object>();
        if (string.IsNullOrWhiteSpace(html)) return result;

        var hasForm = FormRegex().IsMatch(html);
        result["has_login_form"] = hasForm;

        var labels = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match tag in FieldTagRegex().Matches(html))
        {
            var m = AttrRegex().Match(tag.Value);
            if (!m.Success) continue;
            var v = HtmlEntity.Decode(m.Groups["v"].Value.Trim());
            if (v.Length == 0 || !seen.Add(v)) continue;
            labels.Add(v);
            if (labels.Count >= 3) break;
        }
        result["form_field_labels"] = labels;

        var buttons = new List<string>();
        var bseen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match b in ButtonRegex().Matches(html))
        {
            var text = HtmlEntity.Decode(TagRegex().Replace(b.Groups["t"].Value, " ")).Trim();
            if (text.Length == 0) continue;
            var attr = AttrRegex().Match(b.Value);
            if (attr.Success && attr.Groups["v"].Value.Length > 0) text = HtmlEntity.Decode(attr.Groups["v"].Value);

            if (!bseen.Add(text)) continue;
            buttons.Add(text);
            if (buttons.Count >= 3) break;
        }
        result["button_texts"] = buttons;

        return result;
    }

    private static readonly string[] SummaryHeaderKeys =
        ["server", "x-powered-by", "set-cookie", "www-authenticate", "location", "via"];

    /// <summary>响应头摘要，对应 Python 的 _summarize_headers</summary>
    public static Dictionary<string, object> SummarizeHeaders(HttpHeadersView view)
    {
        var summary = new Dictionary<string, object>();

        foreach (var key in SummaryHeaderKeys)
        {
            var value = view[key];
            if (value is null) continue;

            if (key == "set-cookie")
            {
                var names = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var cookie in view.Cookies)
                {
                    var name = cookie.Split('=', 2)[0].Trim();
                    if (name.Length == 0 || !seen.Add(name)) continue;
                    names.Add(name);
                    if (names.Count >= 5) break;
                }
                summary[key] = names;
                continue;
            }

            summary[key] = PageParser.Truncate(value, 120);
        }

        return summary;
    }
}
