using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// ─────────────────────────────────────────────────────────────
// 本文件移植自 chunsou（Python）: https://github.com/Funsiooo/chunsou
// 对应原项目文件：modules/api/ai.py
// Copyright (C) Funsiooo    Licensed under GPL-3.0（见 LICENSE）
// ─────────────────────────────────────────────────────────────

namespace Chunsou;

public sealed record AiSettings(
    string Provider,
    string Model,
    string BaseUrl,
    string ApiKey,
    int Timeout,
    bool CacheEnabled,
    int MaxTokensAuto,
    int MaxTokensForce);

public sealed record AiOutcome(
    string? Fingerprint,
    double Confidence,
    List<string> Evidence,
    string? Analysis,
    string? Error,
    bool CacheHit,
    string Provider,
    string Model);

/// <summary>
/// AI 语义分析，对应 modules/api/ai.py。
/// 两家都走 OpenAI 兼容的 chat/completions 接口（DeepSeek 与 OpenAI 均为该协议），
/// 提示词与返回结构保持与 Python 版一致。
/// </summary>
public sealed class AiAnalyzer(HttpClient client)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string CachePath => Path.Combine(Config.RootDir, "results", "ai_cache.json");

    public AiSettings LoadSettings(string? provider, string? model)
    {
        var selected = provider ?? Config.AiProvider;
        if (selected != "deepseek") selected = "gpt";

        return selected == "deepseek"
            ? new AiSettings("deepseek", model ?? Config.DeepSeekModel, Config.DeepSeekBaseUrl,
                Config.DeepSeekApiKey, Config.AiTimeout, Config.AiCache, Config.AiMaxTokensAuto, Config.AiMaxTokensForce)
            : new AiSettings("gpt", model ?? Config.AiModel, Config.GptBaseUrl,
                Config.GptApiKey, Config.AiTimeout, Config.AiCache, Config.AiMaxTokensAuto, Config.AiMaxTokensForce);
    }

    public async Task<AiOutcome> AnalyzeAsync(ScanResult result, string mode, string? provider, string? model, CancellationToken ct = default)
    {
        var s = LoadSettings(provider, model);

        if (string.IsNullOrWhiteSpace(s.ApiKey))
            return Fail($"{s.Provider} api key is not configured", s);

        var evidence = mode == "force" ? BuildForceEvidence(result) : BuildAutoEvidence(result);

        var cacheKey = BuildCacheKey(mode, s, evidence);
        if (s.CacheEnabled)
        {
            var cached = LoadCache();
            if (cached.TryGetValue(cacheKey, out var hit))
                return hit with { CacheHit = true };
        }

        try
        {
            var (systemPrompt, userPrompt) = BuildPrompts(evidence, mode);
            var maxTokens = mode == "force" ? s.MaxTokensForce : s.MaxTokensAuto;

            var payload = new
            {
                model = s.Model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt },
                },
                max_tokens = maxTokens,
                temperature = 0.2,
                stream = false,
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint(s.BaseUrl))
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ApiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(s.Timeout));

            using var resp = await client.SendAsync(req, cts.Token).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return Fail($"HTTP {(int)resp.StatusCode}: {PageParser.Truncate(body, 160)}", s);

            var content = ExtractContent(body);
            var outcome = Parse(content, mode, s);

            if (s.CacheEnabled)
            {
                var cache = LoadCache();
                cache[cacheKey] = outcome;
                SaveCache(cache);
            }

            return outcome;
        }
        catch (OperationCanceledException)
        {
            return Fail("ai request timeout", s);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, s);
        }
    }

    private static string Endpoint(string baseUrl)
    {
        var b = baseUrl.TrimEnd('/');
        return b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? b : b + "/chat/completions";
    }

    private static AiOutcome Fail(string error, AiSettings s) =>
        new(null, 0, [], null, error, false, s.Provider, s.Model);

    private static Dictionary<string, object> BuildAutoEvidence(ScanResult r) => new()
    {
        ["mode"] = "auto",
        ["url"] = r.Url,
        ["final_url"] = r.FinalUrl ?? r.Url,
        ["status_code"] = r.StatusCode,
        ["title"] = r.Title ?? "",
        ["favicon_hash"] = r.IconHash ?? "",
        ["tech_stack"] = r.StackList,
        ["headers_summary"] = r.HeadersSummary,
        ["body_keywords"] = r.BodyKeywords.Take(8).ToList(),
        ["local_fingerprint"] = r.DetectedCms ?? "",
        ["local_candidates"] = r.DetectedCms is null ? new List<string>() : new List<string> { r.DetectedCms },
    };

    private static Dictionary<string, object> BuildForceEvidence(ScanResult r) => new()
    {
        ["mode"] = "force",
        ["url"] = r.Url,
        ["final_url"] = r.FinalUrl ?? r.Url,
        ["status_code"] = r.StatusCode,
        ["title"] = r.Title ?? "",
        ["favicon_hash"] = r.IconHash ?? "",
        ["tech_stack"] = r.StackList,
        ["headers_summary"] = r.HeadersSummary,
        ["body_keywords"] = r.BodyKeywords.Take(12).ToList(),
        ["body_snippets"] = r.BodySnippets.Take(4).ToList(),
        ["dom_features"] = r.DomFeatures,
        ["local_fingerprint"] = r.DetectedCms ?? "",
        ["local_candidates"] = r.DetectedCms is null ? new List<string>() : new List<string> { r.DetectedCms },
    };

    private const string AutoSystemPrompt = """
        你是一个 Web 指纹识别助手，擅长根据网页标题、响应头、favicon hash、技术栈和少量正文关键词判断最可能的产品指纹。
        任务要求：
        1. 仅根据当前页面摘要证据判断最可能的产品名称。
        2. 如果证据不足，返回 unknown。
        3. 不要输出长解释。
        4. 只返回 JSON。
        5. fingerprint 必须简洁，例如：致远OA、Nacos、禅道、帆软报表。
        6. confidence 取值范围为 0 到 1。
        7. evidence 最多返回 3 条。
        """;

    private const string ForceSystemPrompt = """
        你是一个 Web 页面分析助手。
        你只能根据当前 URL 返回的这个页面本身进行分析。
        允许将当前输入 URL 与最终落地 URL 的路径特征作为判断依据。
        不得根据页面中出现的其他 URL、接口地址、跳转链接、iframe 目标地址或未访问页面内容进行推断。
        不得扩展分析整个站点，只能分析当前页面。
        任务要求：
        1. 对当前页面做整体语义分析。
        2. 输出一条简短中文分析结果，不超过 80 个汉字。
        3. 如果证据不足，也要给出保守分析结果。
        4. 只返回 JSON。
        5. analysis 必须是可直接展示给用户的结果。
        """;

    private static (string System, string User) BuildPrompts(Dictionary<string, object> evidence, string mode)
    {
        var json = JsonSerializer.Serialize(evidence, Json);

        if (mode == "force")
        {
            var user = "请仅基于当前 URL 返回页面本身的证据进行分析，不要参考页面中的其他 URL 或未访问页面。\n\n" +
                       "输入：\n" + json + "\n\n" +
                       "输出要求：\n只返回以下 JSON，不要输出任何额外内容：\n" +
                       "{\n  \"analysis\": \"一句简短中文分析结果，不超过80字\"\n}";
            return (ForceSystemPrompt, user);
        }

        var autoUser = "请根据以下当前页面摘要证据判断最可能的产品指纹。\n\n" +
                       "输入：\n" + json + "\n\n" +
                       "输出要求：\n只返回以下 JSON，不要输出任何额外内容：\n" +
                       "{\n  \"fingerprint\": \"产品名称或unknown\",\n  \"confidence\": 0.00,\n  \"evidence\": [\"最多3条简短证据\"]\n}";
        return (AutoSystemPrompt, autoUser);
    }

    /// <summary>从 chat/completions 响应里取 choices[0].message.content</summary>
    private static string ExtractContent(string body)
    {
        using var doc = JsonDocument.Parse(body);

        if (doc.RootElement.TryGetProperty("choices", out var choices) &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var msg) &&
            msg.TryGetProperty("content", out var content))
        {
            return content.GetString() ?? "";
        }

        return "";
    }

    private static AiOutcome Parse(string content, string mode, AiSettings s)
    {
        var json = StripJsonFence(content).Trim();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (mode == "force")
            {
                var analysis = root.TryGetProperty("analysis", out var a) ? a.GetString()?.Trim() ?? "" : "";
                return new AiOutcome(null, 0, [], analysis.Length > 0 ? analysis : "当前页面特征不足，建议人工复核",
                    null, false, s.Provider, s.Model);
            }

            var fp = root.TryGetProperty("fingerprint", out var f) ? f.GetString()?.Trim() ?? "" : "";
            var conf = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0;

            var evidence = new List<string>();
            if (root.TryGetProperty("evidence", out var e) && e.ValueKind == JsonValueKind.Array)
                foreach (var item in e.EnumerateArray().Take(3))
                    if (item.GetString() is { Length: > 0 } s2)
                        evidence.Add(s2);

            return new AiOutcome(fp, conf, evidence, null, null, false, s.Provider, s.Model);
        }
        catch (JsonException)
        {
            // 非 JSON 输出时降级：取第一行
            if (mode == "force")
            {
                var text = json.Length > 80 ? json[..80] : json;
                return new AiOutcome(null, 0, [], text.Length > 0 ? text : "当前页面特征不足，建议人工复核",
                    null, false, s.Provider, s.Model);
            }

            var line = json.Split('\n').FirstOrDefault()?.Trim() ?? "";
            return new AiOutcome(line.Length > 0 ? line[..Math.Min(60, line.Length)] : "unknown", 0, [],
                null, null, false, s.Provider, s.Model);
        }
    }

    private static string StripJsonFence(string raw)
    {
        var s = raw.Trim();
        if (!s.StartsWith("```")) return s;

        s = s.Trim('`');
        if (s.StartsWith("json", StringComparison.OrdinalIgnoreCase)) s = s[4..].Trim();

        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s[start..(end + 1)] : s;
    }

    private static string BuildCacheKey(string mode, AiSettings s, Dictionary<string, object> evidence)
    {
        var payload = JsonSerializer.Serialize(new { mode, provider = s.Provider, model = s.Model, evidence }, Json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static Dictionary<string, AiOutcome> LoadCache()
    {
        if (!File.Exists(CachePath)) return [];

        try
        {
            var json = File.ReadAllText(CachePath);
            var raw = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
            if (raw is null) return [];

            return raw.ToDictionary(
                p => p.Key,
                p => new AiOutcome(p.Value.Fingerprint, p.Value.Confidence, p.Value.Evidence,
                    p.Value.Analysis, p.Value.Error, false, p.Value.Provider, p.Value.Model));
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void SaveCache(Dictionary<string, AiOutcome> cache)
    {
        try
        {
            var dir = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var payload = cache.ToDictionary(
                p => p.Key,
                p => new CacheEntry(p.Value.Fingerprint, p.Value.Confidence, p.Value.Evidence,
                    p.Value.Analysis, p.Value.Error, p.Value.Provider, p.Value.Model));

            File.WriteAllText(CachePath, JsonSerializer.Serialize(payload, Json));
        }
        catch (Exception)
        {
            // 缓存写失败不应影响主流程
        }
    }

    private sealed record CacheEntry(
        string? Fingerprint, double Confidence, List<string> Evidence,
        string? Analysis, string? Error, string Provider, string Model);
}

/// <summary>把 AI 结果合并进扫描结果，对应 Python 的 _apply_ai_analysis</summary>
public static class AiPipeline
{
    public static async Task ApplyAsync(ScanResult result, CliOptions opt, AiAnalyzer ai, CancellationToken ct = default)
    {
        var mode = opt.Ai;
        if (string.IsNullOrEmpty(mode)) return;

        // auto 模式下本地已识别出指纹就不再调 AI，省 token
        if (mode == "auto" && !string.IsNullOrEmpty(result.DetectedCms)) return;

        var outcome = await ai.AnalyzeAsync(result, mode, opt.AiProvider, opt.AiModel, ct).ConfigureAwait(false);

        result.AiMode = mode;
        result.AiProvider = outcome.Provider;
        result.AiModel = outcome.Model;

        if (outcome.Error is not null)
        {
            result.AiError = outcome.Error;
            if (mode == "force")
                result.AiAnalysis = PageParser.Truncate($"AI分析失败：{outcome.Error}", 160);
            return;
        }

        if (mode == "force")
        {
            result.AiAnalysis = PageParser.Truncate(outcome.Analysis ?? "当前页面特征不足，建议人工复核", 80);
            return;
        }

        var fp = outcome.Fingerprint?.Trim();
        if (!string.IsNullOrEmpty(fp) && !fp.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            result.AiFingerprint = fp;
            result.AiConfidence = outcome.Confidence;
            result.AiEvidence = outcome.Evidence.Take(3).ToList();
        }
    }
}
