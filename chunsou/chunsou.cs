#!/usr/bin/env dotnet run
#:package Spectre.Console@*
#:package MiniExcel@*
#:package System.CommandLine@2.0.12
#:include ./Modules/Ansi.cs
#:include ./Modules/CliOptions.cs
#:include ./Modules/Config.cs
#:include ./Modules/Murmur3.cs
#:include ./Modules/AhoCorasick.cs
#:include ./Modules/FingerRules.cs
#:include ./Modules/TechStack.cs
#:include ./Modules/PageParser.cs
#:include ./Modules/Scanner.cs
#:include ./Modules/AiAnalyzer.cs
#:include ./Modules/ResultWriter.cs
#:include ./Modules/AssetApis.cs
#:include ./Modules/SubdomainScanner.cs
#:include ./Modules/SearchTips.cs

using System.CommandLine;
using System.Net;
using Chunsou;
using Spectre.Console;

// ─────────────────────────────────────────────────────────────────────────────
// chunsou（春蒐）· C# 移植版
//
// 移植自：https://github.com/Funsiooo/chunsou
// 原项目作者：Funsiooo    原项目语言：Python 3    原项目许可：GPL-3.0
// 原项目 commit：ab720c0f8b04348b79e6e0aaa1f044a489ff706d
//
// 本文件是原项目的修改版本（modified version），按 GPL-3.0 发布。
// 版权与许可归属原作者 Funsiooo，完整许可证见同目录 LICENSE。
//
// 能力：多线程 Web 指纹识别 + FOFA/Hunter 资产收集 + AI 语义分析 + 子域名爆破
// ─────────────────────────────────────────────────────────────────────────────

// ── banner ──
// 仅在真正执行扫描时打印；--help / --version 由 System.CommandLine 自行处理，不打扰输出
void PrintBanner()
{
    AnsiConsole.Write(new FigletText("chunsou").Color(Color.Orange1));
    AnsiConsole.MarkupLine(
        $"[grey]  多线程 Web 指纹识别工具 · C# 移植版  {CliSpec.ToolVersion} · " +
        "移植自 github.com/Funsiooo/chunsou[/]");
    Console.WriteLine();
}

var spec = new CliSpec();
var root = spec.Build();

// 业务动作挂在命令上：--help / --version / 解析失败由 System.CommandLine 内部短路，
// 不会进入这里，因此 banner 不会污染帮助输出。
root.SetAction(async (parseResult, ct) =>
{
    if (parseResult.GetValue(spec.ShowTip))
    {
        SearchTips.Show();
        return 0;
    }

    var opt = CliOptions.From(parseResult, spec);

    if (!opt.HasTarget)
    {
        Console.Error.WriteLine(Ansi.C(Ansi.Red, "error: 未指定任何操作目标，使用 --help 查看用法"));
        return 2;
    }

    return await RunAsync(opt);
});

try
{
    // System.CommandLine 内置的 --version 会输出程序集版本（1.0.0+commit），
    // 且其 CommandLineAction 构造函数为 internal，无法替换或继承。
    // 因此在 Invoke 之前拦截，直接输出工具版本。
    if (args.Contains("--version"))
    {
        Console.WriteLine($"chunsou {CliSpec.ToolVersion}");
        Console.WriteLine("移植自 https://github.com/Funsiooo/chunsou （原项目作者 Funsiooo，GPL-3.0）");
        return 0;
    }

    // 解析错误统一走 stderr + 退出码 2
    var parsed = root.Parse(args);
    if (parsed.Errors.Count > 0)
    {
        foreach (var e in parsed.Errors)
            Console.Error.WriteLine(Ansi.C(Ansi.Red, $"error: {e.Message}"));
        Console.Error.WriteLine(Ansi.C(Ansi.BrightWhite, "使用 --help 查看用法"));
        return 2;
    }

    return await parsed.InvokeAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine(Ansi.C(Ansi.Red, $"[-] 运行出错：{ex.Message}"));
    if (args.Contains("--verbose"))
        Console.Error.WriteLine(ex.ToString());
    return 1;
}

// ── 实际执行：需要 HttpClient 的扫描 / API / 爆破 ──

async Task<int> RunAsync(CliOptions opt)
{
    Config.EnsureTemplate();
    PrintBanner();

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine();
        Console.WriteLine(Ansi.C(Ansi.Yellow, "[!] 收到中断信号，正在收尾..."));
        cts.Cancel();
    };

    // 代理配置
    HttpClientHandler handler;
    if (!string.IsNullOrWhiteSpace(opt.Proxy))
    {
        var uri = new Uri(opt.Proxy.Contains("://") ? opt.Proxy : "http://" + opt.Proxy);
        handler = new HttpClientHandler
        {
            Proxy = new WebProxy(uri),
            UseProxy = true,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
    }
    else
    {
        handler = new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
    }

    // 连接池调大，避免高并发下连接排队
    handler.MaxConnectionsPerServer = Math.Max(16, opt.Threads * 2);

    using var client = new HttpClient(handler)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    // 每个请求随机一个 UA，对应 Python 版 modules/core/agent.py 的 User_Agent()
    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", RandomUserAgent());
    client.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
        "application/x-shockwave-flash, image/gif, image/x-xbitmap, image/jpeg, image/pjpeg, " +
        "application/vnd.ms-excel, application/vnd.ms-powerpoint, application/msword, */*");
    client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
    client.DefaultRequestHeaders.TryAddWithoutValidation("Connection", "close");

    if (opt.Fofa is not null)
        return await FofaApi.RunAsync(client, opt, cts.Token);

    if (opt.Hunter is not null)
        return await HunterApi.RunAsync(client, opt, cts.Token);

    if (opt.Domain is not null)
        return await SubdomainScanner.RunAsync(opt.Domain, opt, cts.Token);

    if (opt.Domains is not null)
        return await SubdomainScanner.RunListAsync(opt.Domains, opt, cts.Token);

    var scanner = new Scanner(client, new ScanOptions(Timeout: 5));
    var ai = new AiAnalyzer(client);

    return opt.Url is not null
        ? await ScanSingleAsync(opt, scanner, ai, cts.Token)
        : await ScanListAsync(opt, scanner, ai, cts.Token);
}

// ────────────────────────────────  单目标扫描 ────────────────────────────────

static async Task<int> ScanSingleAsync(CliOptions opt, Scanner scanner, AiAnalyzer ai, CancellationToken ct)
{
    var outPath = ResultWriter.ResolveOutput(opt.Output, "results.txt");
    var isXlsx = outPath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);

    Announce_Time();
    Announce(opt, outPath, 1);

    var result = await scanner.ScanAsync(opt.Url!, ct).ConfigureAwait(false);
    await AiPipeline.ApplyAsync(result, opt, ai, ct).ConfigureAwait(false);

    var forceMode = result.IsForceMode;
    var failed = result.HasError && !forceMode;

    if (failed)
    {
        Console.WriteLine(RenderError(result));
        if (isXlsx)
            ResultWriter.WriteXlsx(outPath, [ResultWriter.ToXlsxRow(result)]);
        else
            ResultWriter.WriteText(outPath, [ResultWriter.ErrorFileLine(result)]);
    }
    else
    {
        Console.WriteLine(RenderSuccess(result));
        if (isXlsx)
        {
            if (forceMode)
                ResultWriter.WriteXlsx(outPath, [ResultWriter.ToAiXlsxRow(result)]);
            else
                ResultWriter.WriteXlsx(outPath, [ResultWriter.ToXlsxRow(result)]);
        }
        else
        {
            ResultWriter.WriteText(outPath, [ResultWriter.StatusLine(result)]);
        }
    }

    Footer(outPath);
    return 0;
}

// ────────────────────────────────  多目标扫描 ────────────────────────────────

static async Task<int> ScanListAsync(CliOptions opt, Scanner scanner, AiAnalyzer ai, CancellationToken ct)
{
    var urls = ExpandTargets(opt.File!);
    if (urls.Count == 0)
    {
        Console.Error.WriteLine(Ansi.C(Ansi.Red, $"error: {opt.File} 中没有有效目标"));
        return 2;
    }

    var outPath = ResultWriter.ResolveOutput(opt.Output, "results.txt");
    var isXlsx = outPath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);

    Announce_Time();
    Announce(opt, outPath, urls.Count);

    if (!isXlsx) ResultWriter.WriteText(outPath, []);

    var done = 0;
    var okCount = 0;
    var errCount = 0;
    var cursor = 0;
    var startAt = DateTime.Now;
    var rows = new List<ScanXlsxRow>(urls.Count);
    var aiRows = new List<AiXlsxRow>(urls.Count);
    var textLines = new List<string>(urls.Count);
    var writeGate = new Lock();

    await AnsiConsole.Progress()
        .AutoClear(false)
        .Columns(
            new TaskDescriptionColumn(),
            new ProgressBarColumn(),
            new PercentageColumn(),
            new SpinnerColumn())
        .StartAsync(async ctx =>
        {
            var task = ctx.AddTask($"[green]扫描中[/] 共 {urls.Count} 个目标", maxValue: urls.Count);

            var workers = Enumerable.Range(0, opt.Threads).Select(async _ =>
            {
                while (!ct.IsCancellationRequested)
                {
                    var index = Interlocked.Increment(ref cursor) - 1;
                    if (index >= urls.Count) return;

                    var url = urls[index];
                    ScanResult result;

                    try
                    {
                        result = await scanner.ScanAsync(url, ct).ConfigureAwait(false);
                        await AiPipeline.ApplyAsync(result, opt, ai, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    var forceMode = result.IsForceMode;
                    var failed = result.HasError && !forceMode;

                    if (failed)
                    {
                        Interlocked.Increment(ref errCount);
                        if (opt.ShowErrors)
                        {
                            Console.WriteLine(RenderError(result));
                            lock (writeGate)
                            {
                                if (!isXlsx) textLines.Add(ResultWriter.ErrorFileLine(result));
                                else rows.Add(ResultWriter.ToXlsxRow(result));
                            }
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref okCount);
                        Console.WriteLine(RenderSuccess(result));
                        lock (writeGate)
                        {
                            if (isXlsx)
                            {
                                // AI force 模式列数与常规不同，分桶收集以便最终选型
                                if (result.IsForceMode) aiRows.Add(ResultWriter.ToAiXlsxRow(result));
                                else rows.Add(ResultWriter.ToXlsxRow(result));
                            }
                            else
                            {
                                textLines.Add(ResultWriter.StatusLine(result));
                            }
                        }
                    }

                    var n = Interlocked.Increment(ref done);
                    if (n % 25 == 0 || n == urls.Count) Flush();
                    task.Increment(1);
                }
            });

            await Task.WhenAll(workers).ConfigureAwait(false);
            task.Increment(0);
        }).ConfigureAwait(false);

    // 最终落盘
    if (isXlsx)
    {
        if (aiRows.Count > 0)
        {
            // force 模式统一用 2 列表头（与 Python 版一致）
            ResultWriter.WriteXlsx(outPath, aiRows);
        }
        else if (rows.Count > 0)
        {
            ResultWriter.WriteXlsx(outPath, rows);
        }
    }
    else
    {
        ResultWriter.WriteText(outPath, textLines);
    }

    Console.WriteLine(Ansi.Info(
        $"扫描完成  成功 {Ansi.C(Ansi.Green, okCount.ToString())} / 失败 {Ansi.C(Ansi.Red, errCount.ToString())}" +
        $" / 总计 {urls.Count}  用时 {DateTime.Now - startAt:mm\\:ss}"));
    Footer(outPath);
    return 0;

    void Flush()
    {
        if (isXlsx) return;
        lock (writeGate)
        {
            if (textLines.Count == 0) return;
            ResultWriter.AppendText(outPath, textLines);
            textLines.Clear();
        }
    }
}

// ────────────────────────────────  目标展开 ────────────────────────────────

/// <summary>
/// 读取目标文件并展开网段 / IP 段 / 补协议，对应 Python 的 lists_filename。
/// </summary>
static List<string> ExpandTargets(string file)
{
    if (!File.Exists(file))
        throw new FileNotFoundException($"目标文件不存在：{file}", file);

    var urls = new List<string>();

    foreach (var raw in File.ReadAllLines(file))
    {
        var target = raw.Trim();
        if (target.Length == 0 || target.StartsWith('#')) continue;

        // CIDR 网段
        if (target.Contains('/'))
        {
            if (TryExpandCidr(target, out var cidrUrls)) { urls.AddRange(cidrUrls); continue; }
            urls.Add(target);
            continue;
        }

        // IP 范围 a.b.c.d-e.f.g.h
        if (target.Contains('-'))
        {
            if (TryExpandRange(target, out var rangeUrls)) { urls.AddRange(rangeUrls); continue; }
            urls.Add(target);
            continue;
        }

        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            urls.Add(target);
        }
        else
        {
            urls.Add($"http://{target}");
            urls.Add($"https://{target}");
        }
    }

    return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

static bool TryExpandCidr(string cidr, out List<string> urls)
{
    urls = [];
    var parts = cidr.Split('/');
    if (parts.Length != 2 || !int.TryParse(parts[1], out var prefix)) return false;
    if (!IPAddress.TryParse(parts[0], out var addr)) return false;
    if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
    if (prefix is < 0 or > 32) return false;

    var baseBytes = addr.GetAddressBytes();
    var value = ((uint)baseBytes[0] << 24) | ((uint)baseBytes[1] << 16) |
                ((uint)baseBytes[2] << 8) | baseBytes[3];

    var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
    var network = value & mask;
    var size = prefix >= 31 ? prefix == 32 ? 1u : 2u : 1u << (32 - prefix);

    // 超过 65536 个地址时不做展开，避免一次性拉起数十万请求
    if (size > 65536)
    {
        Console.WriteLine(Ansi.C(Ansi.Yellow, $"[!] 网段 {cidr} 含 {size} 个地址，过大，已跳过展开"));
        return true;
    }

    for (var i = 0u; i < size; i++)
    {
        var ip = network + i;
        var b = new[]
        {
            (byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip,
        };
        var text = new IPAddress(b).ToString();
        urls.Add($"http://{text}");
        urls.Add($"https://{text}");
    }

    return true;
}

static bool TryExpandRange(string range, out List<string> urls)
{
    urls = [];
    var parts = range.Split('-');
    if (parts.Length != 2) return false;

    if (!IPAddress.TryParse(parts[0].Trim(), out var start) ||
        !IPAddress.TryParse(parts[1].Trim(), out var end))
        return false;

    if (start.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
        end.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        return false;

    var startValue = ToUInt(start);
    var endValue = ToUInt(end);
    if (endValue < startValue) (startValue, endValue) = (endValue, startValue);

    var count = endValue - startValue + 1;
    if (count > 65536)
    {
        Console.WriteLine(Ansi.C(Ansi.Yellow, $"[!] IP 范围 {range} 含 {count} 个地址，过大，已跳过展开"));
        return true;
    }

    for (var i = startValue; i <= endValue; i++)
    {
        var text = new IPAddress(
        [
            (byte)(i >> 24), (byte)(i >> 16), (byte)(i >> 8), (byte)i,
        ]).ToString();
        urls.Add($"http://{text}");
        urls.Add($"https://{text}");
    }

    return true;

    static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }
}

// ────────────────────────────────  控制台渲染 ────────────────────────────────

/// <summary>随机 User-Agent 池，对应 Python 版 modules/core/agent.py</summary>
static string RandomUserAgent()
{
    string[] pool =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:89.0) Gecko/20100101 Firefox/89.0",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Version/14.1 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 YaBrowser/21.6.0.615 Yowser/2.5 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Edge/91.0.864.59",
        "Mozilla/5.0 (Linux; Android 11; SM-G991U Build/RP1A.200720.012) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.120 Mobile Safari/537.36",
        "Mozilla/5.0 (iPhone; CPU iPhone OS 14_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/14.1.1 Mobile/15E148 Safari/604.1",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36 Edg/91.0.864.59",
        "Mozilla/5.0 (Windows; U; Win98; en-US; rv:1.8.1) Gecko/20061010 Firefox/2.0",
        "Mozilla/5.0 (Windows; U; Windows NT 5.1 ; x64; en-US; rv:1.9.1b2pre) Gecko/20081026 Firefox/3.1b2pre",
        "Opera/10.60 (Windows NT 5.1; U; zh-cn) Presto/2.6.30 Version/10.60",
        "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/57.0.2987.133 Safari/537.36",
        "Mozilla/5.0 (Windows; U; Windows NT 6.0; fr-FR) AppleWebKit/528.16 (KHTML, like Gecko) Version/4.0 Safari/528.16",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_4) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/112.0.5660.225 Safari/537.36",
    ];

    return pool[Random.Shared.Next(pool.Length)];
}

static void Announce_Time() => Console.WriteLine(Ansi.C(Ansi.BrightWhite, $"[*] Starting @ {Clock.Now()}"));

static void Announce(CliOptions opt, string outPath, int count)
{
    var target = count == 1 ? "1 个目标" : $"{count} 个目标";

    if (!string.IsNullOrEmpty(opt.Ai))
    {
        var provider = opt.AiProvider ?? Config.AiProvider;
        var model = opt.AiModel ?? (provider == "deepseek" ? Config.DeepSeekModel : Config.AiModel);
        var hasKey = provider == "deepseek" ? Config.DeepSeekApiKey.Length > 0 : Config.GptApiKey.Length > 0;

        Console.WriteLine(Ansi.Info(
            $"指纹识别已就绪（{target}，AI 语义分析 {Ansi.C(Ansi.YellowB, opt.Ai)} 模式" +
            $" · {Ansi.C(Ansi.YellowB, provider)}/{Ansi.C(Ansi.YellowB, model)}" +
            $"{(hasKey ? "" : Ansi.C(Ansi.Yellow, "，未配置 API Key，AI 分析将跳过"))}）"));
    }
    else
    {
        Console.WriteLine(Ansi.Info($"指纹识别已就绪（{target}）"));
    }

    var rules = FingerRules.Instance;
    if (opt.Verbose)
    {
        Console.WriteLine(Ansi.C(Ansi.BrightWhite,
            $"    规则库 {rules.Total} 条（body {rules.BodyCount} / title {rules.TitleCount} / " +
            $"header {rules.HeaderCount} / icon {rules.IconCount}）"));
        Console.WriteLine(Ansi.C(Ansi.BrightWhite,
            $"    并发 {opt.Threads} 线程，输出 {Ansi.C(Ansi.Yellow, outPath)}"));
    }

    Console.WriteLine();
}

static void Footer(string outPath)
{
    Console.WriteLine(Ansi.Info(
        $"扫描完成，结果已保存到 {Ansi.C(Ansi.Yellow, outPath)}，结束时间 {Ansi.C(Ansi.Yellow, Clock.Now())}"));
}

static string RenderSuccess(ScanResult r)
{
    var status = Ansi.C(Ansi.Green, r.StatusCode.ToString());
    var url = Ansi.C(Ansi.YellowB, r.Url);

    if (r.IsForceMode)
        return Ansi.C(Ansi.BrightWhite, $"[{Clock.Stamp()}] ") + Ansi.C(Ansi.Green, $"[{status}] ") +
               url + " " + Ansi.C(Ansi.White, "|") + " " + Ansi.C(Ansi.YellowB, ResultWriter.ForceAnalysisText(r));

    var extra = new List<string>();
    if (!string.IsNullOrEmpty(r.AiProvider)) extra.Add($"{r.AiProvider}/{r.AiModel}");
    if (!string.IsNullOrEmpty(r.AiError)) extra.Add("AI 失败: " + PageParser.Truncate(r.AiError, 60));
    var tail = extra.Count > 0 ? " " + Ansi.C(Ansi.Yellow, "[" + string.Join("; ", extra) + "]") : "";

    return Ansi.C(Ansi.BrightWhite, $"[{Clock.Stamp()}] ") + Ansi.C(Ansi.Green, $"[{status}] ") +
           url + " " + Ansi.C(Ansi.White, "|") + " " + Ansi.C(Ansi.YellowB, ResultWriter.FingerText(r)) +
           " " + Ansi.C(Ansi.White, "|") + " " + Ansi.C(Ansi.YellowB, ResultWriter.Display(r.Title)) +
           " " + Ansi.C(Ansi.White, "|") + " " + Ansi.C(Ansi.YellowB, ResultWriter.Display(r.Stack)) + tail;
}

static string RenderError(ScanResult r) =>
    Ansi.C(Ansi.BrightWhite, $"[{Clock.Stamp()}] ") + Ansi.C(Ansi.Red, "[-] ") +
    Ansi.C(Ansi.YellowB, r.Url) + " " + Ansi.C(Ansi.Red, $"[{r.Error}]");
