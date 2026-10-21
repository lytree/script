// ─────────────────────────────────────────────────────────────
// 本文件移植自 chunsou（Python）: https://github.com/Funsiooo/chunsou
// 对应原项目文件：modules/core/scan.py
// Copyright (C) Funsiooo    Licensed under GPL-3.0（见 LICENSE）
// ─────────────────────────────────────────────────────────────

namespace Chunsou;

/// <summary>单个目标的扫描结果</summary>
public sealed class ScanResult
{
    public required string Url { get; init; }
    public string? FinalUrl { get; set; }
    public int StatusCode { get; set; }
    public string? Title { get; set; }
    public string? DetectedCms { get; set; }
    public string? IconHash { get; set; }
    public string? Stack { get; set; }
    public List<string> StackList { get; set; } = [];
    public Dictionary<string, object> HeadersSummary { get; set; } = [];
    public List<string> BodyKeywords { get; set; } = [];
    public List<string> BodySnippets { get; set; } = [];
    public Dictionary<string, object> DomFeatures { get; set; } = [];

    public string? Error { get; set; }

    // AI 相关
    public string? AiMode { get; set; }
    public string? AiProvider { get; set; }
    public string? AiModel { get; set; }
    public string? AiFingerprint { get; set; }
    public double? AiConfidence { get; set; }
    public List<string> AiEvidence { get; set; } = [];
    public string? AiAnalysis { get; set; }
    public string? AiError { get; set; }

    public bool IsForceMode => AiMode == "force";
    public bool HasError => Error is not null;
}

/// <summary>扫描参数：目前只需超时秒数，代理由 HttpClientHandler 统一处理</summary>
public sealed record ScanOptions(int Timeout);

/// <summary>
/// 扫描核心，对应 modules/core/scan.py
/// </summary>
public sealed class Scanner(HttpClient client, ScanOptions options)
{
    private readonly SemaphoreSlim _iconGate = new(8);

    /// <summary>扫描单个目标。任何异常都被收敛为 Error 字段，不中断批量扫描。</summary>
    public async Task<ScanResult> ScanAsync(string url, CancellationToken ct = default)
    {
        var result = new ScanResult { Url = url };

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(options.Timeout));

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // 与 Python 版一致：不跟随重定向，以便拿到原始状态码
            var handlerResp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            using (handlerResp)
            {
                result.StatusCode = (int)handlerResp.StatusCode;
                result.FinalUrl = handlerResp.RequestMessage?.RequestUri?.ToString() ?? url;

                var content = await handlerResp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                var declared = handlerResp.Content.Headers.ContentType?.CharSet;
                var html = PageParser.Decode(content, declared);

                var view = BuildHeaderView(handlerResp);
                var title = PageParser.ExtractTitle(html);

                var iconHash = await FetchIconHashAsync(url, html, view, ct).ConfigureAwait(false);

                // headerString 拼接所有响应头，保持 Python 的 str(response.headers) 语义
                var headerString = string.Join(" ", view.Pairs.Select(p => $"'{p.Key}': '{p.Value}'"));

                result.Title = title;
                result.IconHash = iconHash;
                result.DetectedCms = FingerRules.Instance.Match(html, title, headerString, iconHash);
                result.HeadersSummary = PageParser.SummarizeHeaders(view);

                var cleanText = PageParser.CleanText(html);
                result.BodySnippets = PageParser.BodySnippets(cleanText);
                result.BodyKeywords = PageParser.BodyKeywords(url, title, cleanText);

                var stack = TechStack.Detect(html, view);
                result.StackList = stack;
                result.Stack = stack.Count > 0 ? string.Join(", ", stack.Take(5)) : null;

                if (!string.IsNullOrEmpty(html)) result.DomFeatures = PageParser.DomFeatures(html);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result.Error = "timeout";
            result.StatusCode = 0;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            result.StatusCode = 0;
        }

        return result;
    }

    /// <summary>抓取 favicon 并计算 mmh3 哈希</summary>
    private async Task<string?> FetchIconHashAsync(string url, string html, HttpHeadersView view, CancellationToken ct)
    {
        try
        {
            var iconUrl = PageParser.GetIconUrl(url, html);
            if (iconUrl is null) return null;

            await _iconGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(options.Timeout));

                using var resp = await client
                    .GetAsync(iconUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode) return null;

                var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                return bytes.Length == 0 ? null : PageParser.IconHash(bytes);
            }
            finally
            {
                _iconGate.Release();
            }
        }
        catch (Exception)
        {
            // favicon 取不到不影响主流程（与 Python 版一致）
            return null;
        }
    }

    private static HttpHeadersView BuildHeaderView(HttpResponseMessage resp)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var (k, v) in resp.Headers)
            pairs.Add(new(k, string.Join(", ", v)));

        foreach (var (k, v) in resp.Content.Headers)
            pairs.Add(new(k, string.Join(", ", v)));

        var cookies = new List<string>();
        foreach (var (k, v) in resp.Headers)
        {
            if (!k.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var raw in v)
            {
                var semi = raw.IndexOf(';');
                cookies.Add(semi > 0 ? raw[..semi] : raw);
            }
        }

        return new HttpHeadersView { Pairs = pairs, Cookies = cookies };
    }
}
