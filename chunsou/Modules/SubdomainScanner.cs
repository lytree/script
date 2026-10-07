using System.Net;
using System.Net.Sockets;

// ─────────────────────────────────────────────────────────────
// 本文件移植自 chunsou（Python）: https://github.com/Funsiooo/chunsou
// 对应原项目文件：modules/core/domain.py（改为原生 DNS，替代 OneForAll）
// Copyright (C) Funsiooo    Licensed under GPL-3.0（见 LICENSE）
// ─────────────────────────────────────────────────────────────

namespace Chunsou;

/// <summary>
/// 子域名爆破。
///
/// Python 版是 shell out 调用 OneForAll（要求本机有 Python 环境 + 一堆依赖）。
/// 这里改为原生 DNS 解析实现：读字典 → 并发解析 → 过滤通配符 → 输出结果。
/// 无外部依赖，跨平台，且能直接产出后续可扫描的 URL 列表。
/// </summary>
public static class SubdomainScanner
{
    public static string WordlistPath => Path.Combine(Config.RootDir, "wordlist.txt");

    private static readonly string[] DefaultWords =
    [
        "www", "mail", "ftp", "webmail", "smtp", "pop", "imap", "ns1", "ns2", "api", "m", "mobile",
        "test", "dev", "staging", "admin", "portal", "shop", "blog", "cdn", "static", "img", "image",
        "download", "vpn", "git", "gitlab", "jenkins", "docker", "k8s", "db", "mysql", "redis",
        "oa", "erp", "crm", "sso", "auth", "login", "cms", "gitweb", "wiki", "docs", "demo", "uat",
    ];

    private static IReadOnlyList<string> LoadWords()
    {
        if (!File.Exists(WordlistPath)) return DefaultWords;

        var words = new List<string>(DefaultWords.Length + 1024);
        foreach (var line in File.ReadLines(WordlistPath))
        {
            var w = line.Trim();
            if (w.Length == 0 || w.StartsWith('#')) continue;
            words.Add(w);
        }

        // 字典过大时截断，DNS 爆破的收益在几千条后急剧下降
        const int cap = 20000;
        return words.Count > cap ? words.GetRange(0, cap) : words;
    }

    private static async Task<bool> ResolvesAsync(string host, CancellationToken ct)
    {
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addrs.Length > 0;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<List<string>> BruteAsync(string domain, int threads, Action<string> onHit, CancellationToken ct)
    {
        var words = LoadWords();
        var found = new List<string>();
        var gate = new SemaphoreSlim(threads);
        var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 通配符检测：随机子域名若能解析，说明该域有泛解析，结果不可信
        var wildcardProbe = $"{Guid.NewGuid():N}"[..12];
        var wildcard = await ResolvesAsync($"{wildcardProbe}.{domain}", ct).ConfigureAwait(false);
        if (wildcard)
            Console.WriteLine(Ansi.C(Ansi.Yellow, $"[!] {domain} 存在泛解析（DNS wildcard），结果可能包含误报"));

        var tasks = new List<Task>(words.Count);

        foreach (var word in words)
        {
            if (ct.IsCancellationRequested) break;

            var sub = $"{word}.{domain}";
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (!await ResolvesAsync(sub, ct).ConfigureAwait(false)) return;
                    if (wildcard) return;
                    if (!dedupe.Add(sub)) return;

                    lock (found) found.Add(sub);
                    onHit(sub);
                }
                catch (OperationCanceledException) { }
                finally
                {
                    gate.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return found;
    }

    private static string OutputPath(string? output) =>
        ResultWriter.ResolveOutput(output, "subdomains.txt");

    public static async Task<int> RunAsync(string domain, CliOptions opt, CancellationToken ct = default)
    {
        domain = domain.Trim();
        if (domain.Length == 0)
        {
            Console.Error.WriteLine(Ansi.C(Ansi.Red, "error: 域名为空"));
            return 2;
        }

        Console.WriteLine(Ansi.Info($"开始对 {Ansi.C(Ansi.Yellow, domain)} 进行子域名爆破（字典 {LoadWords().Count} 条，{opt.Threads} 线程）"));

        var hits = new List<string>();
        try
        {
            hits = await BruteAsync(domain, opt.Threads, sub =>
                Console.WriteLine(Ansi.Success(sub)), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine(Ansi.C(Ansi.Yellow, "[!] 已取消"));
        }

        var outPath = OutputPath(opt.Output);
        // 与 Python 版一致：每条子域名同时产出 http/https 两个 URL
        var lines = new List<string>(hits.Count * 2);
        foreach (var sub in hits)
        {
            lines.Add($"http://{sub}");
            lines.Add($"https://{sub}");
        }

        ResultWriter.WriteText(outPath, lines);
        Console.WriteLine(Ansi.Info($"爆破完成，命中 {hits.Count} 个子域名，结果保存到 {Ansi.C(Ansi.Yellow, outPath)}"));

        return 0;
    }

    public static async Task<int> RunListAsync(string file, CliOptions opt, CancellationToken ct = default)
    {
        if (!File.Exists(file))
        {
            Console.Error.WriteLine(Ansi.C(Ansi.Red, $"error: 文件不存在 {file}"));
            return 2;
        }

        var domains = File.ReadAllLines(file)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (domains.Count == 0)
        {
            Console.Error.WriteLine(Ansi.C(Ansi.Red, "error: 文件内没有有效域名"));
            return 2;
        }

        var all = new List<string>();

        foreach (var domain in domains)
        {
            if (ct.IsCancellationRequested) break;

            Console.WriteLine(Ansi.Info($"开始对 {Ansi.C(Ansi.Yellow, domain)} 进行子域名爆破"));

            var hits = await BruteAsync(domain, opt.Threads, sub =>
                Console.WriteLine(Ansi.Success(sub)), ct).ConfigureAwait(false);

            lock (all) all.AddRange(hits);
        }

        all = all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var outPath = OutputPath(opt.Output);
        var lines = new List<string>(all.Count * 2);
        foreach (var sub in all)
        {
            lines.Add($"http://{sub}");
            lines.Add($"https://{sub}");
        }

        ResultWriter.WriteText(outPath, lines);
        Console.WriteLine(Ansi.Info($"爆破完成，共命中 {all.Count} 个子域名，结果保存到 {Ansi.C(Ansi.Yellow, outPath)}"));

        return 0;
    }
}
