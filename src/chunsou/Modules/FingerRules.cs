using System.Text.Json;
using System.Text.Json.Serialization;

// ─────────────────────────────────────────────────────────────
// 本文件移植自 chunsou（Python）: https://github.com/Funsiooo/chunsou
// 对应原项目文件：modules/core/scan.py
// Copyright (C) Funsiooo    Licensed under GPL-3.0（见 LICENSE）
// ─────────────────────────────────────────────────────────────

namespace Chunsou;

public sealed class FingerRule
{
    [JsonPropertyName("cms")] public string Cms { get; set; } = "";
    [JsonPropertyName("method")] public string Method { get; set; } = "";
    [JsonPropertyName("location")] public string Location { get; set; } = "";
    [JsonPropertyName("keyword")] public string[] Keyword { get; set; } = [];
}

/// <summary>
/// 指纹规则库，对应 modules/config/finger.json（约 11129 条）。
///
/// Python 版对每个目标逐条遍历 1 万条规则做 substring 查找，开销极大。
/// 这里把 body / title / header 三组的关键词统一灌进 Aho-Corasick 自动机，
/// 一次扫描页面即可知道"哪些关键词出现过"，再按规则原始顺序返回首个命中的 CMS，
/// 语义与 Python 版一致，复杂度从 O(规则数 × 页面大小) 降到 O(页面大小)。
/// icon_hash 组关键词是数字子串，规则数也不多，保留直接匹配。
/// </summary>
public sealed class FingerRules
{
    private static readonly Lazy<FingerRules> Lazy = new(() => new FingerRules());

    public static FingerRules Instance => Lazy.Value;

    public static string FingerPath => Path.Combine(Config.RootDir, "finger.json");

    /// <summary>规则 + 其关键词在自动机中的 ID</summary>
    private readonly record struct Entry(FingerRule Rule, int[] KeywordIds);

    /// <summary>三组各自独立保序（finger.json 中 body/title/header 是交错存放的）</summary>
    private readonly List<Entry> _body = [];
    private readonly List<Entry> _title = [];
    private readonly List<Entry> _header = [];
    private readonly List<FingerRule> _icon = [];

    private readonly AhoCorasick _automaton;

    public int Total { get; private set; }
    public int BodyCount => _body.Count;
    public int TitleCount => _title.Count;
    public int HeaderCount => _header.Count;
    public int IconCount => _icon.Count;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private FingerRules()
    {
        var path = FingerPath;
        if (!File.Exists(path))
            throw new FileNotFoundException($"指纹库 finger.json 未找到：{path}", path);

        var patterns = new List<string>();

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("fingerprint", out var arr))
            throw new InvalidDataException("finger.json 缺少 fingerprint 字段");

        foreach (var item in arr.EnumerateArray())
        {
            var rule = item.Deserialize<FingerRule>(JsonOpts);
            if (rule is null || rule.Keyword.Length == 0 || string.IsNullOrEmpty(rule.Cms))
                continue;
            if (rule.Keyword.Any(string.IsNullOrEmpty))
                continue;

            Total++;

            var method = rule.Method.ToLowerInvariant();
            var location = rule.Location.ToLowerInvariant();

            if (method == "icon_hash")
            {
                _icon.Add(rule);
                continue;
            }

            if (method != "keyword") continue;

            switch (location)
            {
                case "body": _body.Add(Build(rule, patterns)); break;
                case "title": _title.Add(Build(rule, patterns)); break;
                case "header":
                case "banner": _header.Add(Build(rule, patterns)); break;
            }
        }

        _automaton = new AhoCorasick(patterns);
    }

    private static Entry Build(FingerRule rule, List<string> patterns)
    {
        var start = patterns.Count;
        patterns.AddRange(rule.Keyword);

        var ids = new int[rule.Keyword.Length];
        for (var i = 0; i < ids.Length; i++) ids[i] = start + i;

        return new Entry(rule, ids);
    }

    /// <summary>
    /// 匹配指纹。语义与 Python 的 _match_fingerprint 一致：
    /// body(AND) → title(AND) → header(OR) → icon_hash(AND)，各组内按规则顺序取首个命中。
    /// </summary>
    public string? Match(string html, string? title, string headerString, string? icoHash) =>
        MatchBody(html) ?? MatchTitle(title) ?? MatchHeader(headerString) ?? MatchIcon(icoHash);

    // 每组独立持有一份 scratch，避免 body 的扫描结果污染 title/header 的判定
    [ThreadStatic] private static HashSet<int>? _scratchBody;
    [ThreadStatic] private static HashSet<int>? _scratchTitle;
    [ThreadStatic] private static HashSet<int>? _scratchHeader;

    private string? MatchBody(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;

        var hits = _scratchBody ??= [];
        _automaton.Scan(html, hits);
        if (hits.Count == 0) return null;

        foreach (var (rule, ids) in _body)
            if (AllHit(ids, hits))
                return rule.Cms;

        return null;
    }

    private string? MatchTitle(string? title)
    {
        if (string.IsNullOrEmpty(title)) return null;

        var hits = _scratchTitle ??= [];
        _automaton.Scan(title, hits);
        if (hits.Count == 0) return null;

        foreach (var (rule, ids) in _title)
            if (AllHit(ids, hits))
                return rule.Cms;

        return null;
    }

    private string? MatchHeader(string? headerString)
    {
        if (string.IsNullOrEmpty(headerString)) return null;

        var hits = _scratchHeader ??= [];
        _automaton.Scan(headerString, hits);
        if (hits.Count == 0) return null;

        foreach (var (rule, ids) in _header)
        {
            // header 组为 OR 语义：任一关键词命中即算命中
            foreach (var id in ids)
                if (hits.Contains(id))
                    return rule.Cms;
        }

        return null;
    }

    private string? MatchIcon(string? icoHash)
    {
        if (string.IsNullOrEmpty(icoHash)) return null;

        foreach (var rule in _icon)
        {
            var all = true;
            foreach (var kw in rule.Keyword)
            {
                if (!icoHash.Contains(kw, StringComparison.Ordinal))
                {
                    all = false;
                    break;
                }
            }
            if (all) return rule.Cms;
        }

        return null;
    }

    private static bool AllHit(int[] ids, HashSet<int> hits)
    {
        foreach (var id in ids)
            if (!hits.Contains(id))
                return false;
        return true;
    }
}
