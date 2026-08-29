#!/usr/bin/env dotnet run
#:package System.CommandLine@*
#:package Spectre.Console@*
#:package Spectre.Console.Ansi@*
// ============================================================================
// btsou.cs — BTSOU_Plus(PC936 BT 资源聚合搜索)命令行版
//
// 依据 C:\Users\hiyan\WorkBuddy\2026-08-24-19-20-24\BTSOU_Plus_dnSpy 反编译
// 代码重写为单文件 .NET File-Based App,保留原程序核心搜索管线:
//   ResPool.txt 远程配置 -> 关键词编码 -> 搜索 URL 模板 -> HTML/API 解析
//   -> 详情页磁链补全 -> 翻页 -> 过滤(HKW/屏蔽词/智能滤/正则/大小/去重)
//   -> 排序 -> 多格式输出
//
// 与原 GUI 的差异(有意简化):
//   * WebView2 渲染抓取改为纯 HTTP(当前配置内所有库均为 HTTP 直抓模式)
//   * Clash 订阅/Roaming 代理联动不实现,改由 --proxy 直接指定 HTTP 代理
//   * 收藏夹/举报等本地 GUI 交互功能不移植
//
// 用法:
//   dotnet run src/btsou.cs -- list
//   dotnet run src/btsou.cs -- search <关键词> [--lib 名称|--pages N|--sort ...]
//   dotnet run src/btsou.cs -- sync       # 拉取远程搜索源解析后存储本地并记录地址
//   dotnet run src/btsou.cs -- update
//
// 命令解析使用 System.CommandLine 2.x(Option/Argument/Command/SetAction),
// 选项定义见 CmdDefs;--help/--version 由库自动提供。
// 控制台输出使用 Spectre.Console:表格(list/search 结果)、进度条(并发搜索,
// 走 stderr 不污染 stdout 的 JSON/磁链/CSV 机器可读输出)、Markup 高亮。
// 搜索源:联网加载 ResPool.txt/ApiMapping.txt 后自动解析存储为本地快照
//   data/btsou/sources.json(结构化,含来源地址)+ sources.txt(地址清单);
//   离线时优先回退该快照,其次原始 txt 缓存。sync 命令可手动同步。
// ============================================================================

using System.CommandLine;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Spectre.Console;

try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 部分平台不支持 */ }

// ============================ 顶层入口 =====================================
// 命令解析由 System.CommandLine 2.x 完成(见 CmdDefs),退出码经返回值传递
return await CmdDefs.InvokeAsync(args);

// ============================ 类型与实现 ===================================
// (C# 顶层语句程序要求类型声明位于所有顶层语句之后)

// ---------------------------------------------------------------------------
// System.CommandLine 2.x 命令定义(list / search / update,共享全部选项)
// 注:2.0.11 为重构后的新 API — Option<T>(name, params aliases) + Description
// 属性,默认值经 DefaultValueFactory 或手工兜底;入口 root.Parse(args) 后
// ParseResult.InvokeAsync(new InvocationConfiguration(), ct)
// ---------------------------------------------------------------------------

static class CmdDefs
{
    public static readonly Option<string> OptLib = new("--lib") { Description = "仅搜索指定资源库(默认搜索全部启用库)" };
    public static readonly Option<int> OptPages = new("--pages") { Description = "每个库最多翻页数(默认 1)" };
    public static readonly Option<int> OptTop = new("--top") { Description = "仅显示前 N 条(表格/磁链输出;JSON/CSV 导出全部)" };
    public static readonly Option<string> OptSort = new("--sort") { Description = "排序字段: size|date|hot|title" };
    public static readonly Option<bool> OptDesc = new("--desc") { Description = "降序(默认升序)" };
    public static readonly Option<string> OptMinSize = new("--min-size") { Description = "过滤小于该大小的结果,如 500MB / 2GB" };
    public static readonly Option<string> OptRegex = new("--regex") { Description = "标题正则过滤" };
    public static readonly Option<bool> OptPrecise = new("--precise") { Description = "标题必须包含关键词" };
    public static readonly Option<bool> OptNoHkw = new("--no-hkw") { Description = "关闭广告词清理" };
    public static readonly Option<bool> OptNoIntel = new("--no-intel") { Description = "关闭智能滤词" };
    public static readonly Option<bool> OptNoDedupe = new("--no-dedupe") { Description = "关闭按磁链去重" };
    public static readonly Option<bool> OptNoReport = new("--no-report") { Description = "忽略本地 Report.dll 举报哈希" };
    public static readonly Option<bool> OptMagnet = new("--magnet") { Description = "仅输出磁链(可配合 --torrent 附种子链接)" };
    public static readonly Option<bool> OptJson = new("--json") { Description = "JSON 输出" };
    public static readonly Option<string> OptCsv = new("--csv") { Description = "导出 CSV 文件路径(Excel 可直接打开)" };
    public static readonly Option<bool> OptTorrent = new("--torrent") { Description = "输出里附种子下载地址" };
    public static readonly Option<int> OptTimeout = new("--timeout") { Description = "HTTP 超时(秒,默认 30)" };
    public static readonly Option<int> OptConcurrency = new("--concurrency") { Description = "并发库数(默认 8)" };
    public static readonly Option<string> OptProxy = new("--proxy") { Description = "HTTP 代理,如 http://127.0.0.1:7890" };
    public static readonly Option<string> OptConfig = new("--config") { Description = "指定 ResPool.txt 来源(URL 或路径)" };
    public static readonly Option<string> OptApiMapping = new("--api-mapping") { Description = "指定 ApiMapping.txt 来源(URL 或路径)" };
    public static readonly Option<bool> OptOffline = new("--offline") { Description = "仅使用本地缓存配置(data/btsou/)" };
    public static readonly Option<bool> OptVerbose = new("--verbose") { Description = "输出调试信息" };

    public static readonly Argument<string> ArgKeyword = new("关键词") { Description = "要搜索的关键词" };

    static readonly Option[] SharedOptions =
    {
        OptLib, OptPages, OptTop, OptSort, OptDesc, OptMinSize, OptRegex, OptPrecise,
        OptNoHkw, OptNoIntel, OptNoDedupe, OptNoReport, OptMagnet, OptJson, OptCsv,
        OptTorrent, OptTimeout, OptConcurrency, OptProxy, OptConfig, OptApiMapping,
        OptOffline, OptVerbose,
    };

    public static async Task<int> InvokeAsync(string[] args)
    {
        var pr = BuildRootCommand().Parse(args);
        if (pr.Errors.Count > 0) return 1;   // 未识别命令/参数等解析错误
        return await pr.InvokeAsync(new InvocationConfiguration(), CancellationToken.None);
    }

    public static RootCommand BuildRootCommand()
    {
        var listCmd = new Command("list", "列出资源库");
        var searchCmd = new Command("search", "搜索资源")
        {
            ArgKeyword
        };
        var syncCmd = new Command("sync", "拉取并解析搜索源,存储本地(sources.json/txt)并记录地址");
        var updateCmd = new Command("update", "检查版本更新");

        // 全部子命令共享全部选项
        foreach (var cmd in new[] { listCmd, searchCmd, syncCmd, updateCmd })
            foreach (var opt in SharedOptions)
                cmd.Add(opt);

        listCmd.SetAction(pr => RunCheckedAsync(BuildOptions(pr, "list", null)));
        searchCmd.SetAction(pr => RunCheckedAsync(BuildOptions(pr, "search", pr.GetValue(ArgKeyword))));
        syncCmd.SetAction(pr => RunCheckedAsync(BuildOptions(pr, "sync", null)));
        updateCmd.SetAction(pr => RunCheckedAsync(BuildOptions(pr, "update", null)));

        var root = new RootCommand("BTSOU_Plus 命令行版 — BT 资源聚合搜索(单文件脚本)")
        {
            listCmd,
            searchCmd,
            syncCmd,
            updateCmd
        };
        root.SetAction(pr => { CliHelp.Print(); return 0; });   // 无子命令时打印说明
        return root;
    }

    static Task<int> RunCheckedAsync(CliOptions o)
        => o.Help ? FailHelpAsync() : BtsouApp.RunAsync(o);

    static Task<int> FailHelpAsync()
    {
        CliHelp.Print();
        return Task.FromResult(1);
    }

    static CliOptions BuildOptions(ParseResult pr, string command, string? keyword)
    {
        int pages = pr.GetValue(OptPages);
        int top = pr.GetValue(OptTop);
        int timeout = pr.GetValue(OptTimeout);
        int cc = pr.GetValue(OptConcurrency);

        var o = new CliOptions
        {
            Command = command,
            Keyword = keyword,
            Lib = pr.GetValue(OptLib),
            Pages = pages <= 0 ? 1 : pages,                       // 未提供时兜底默认值
            Top = top < 0 ? 0 : top,
            Desc = pr.GetValue(OptDesc),
            Json = pr.GetValue(OptJson),
            Csv = pr.GetValue(OptCsv),
            MagnetOnly = pr.GetValue(OptMagnet),
            ShowTorrent = pr.GetValue(OptTorrent),
            Regex = pr.GetValue(OptRegex),
            Precise = pr.GetValue(OptPrecise),
            NoHkw = pr.GetValue(OptNoHkw),
            NoDedupe = pr.GetValue(OptNoDedupe),
            NoIntel = pr.GetValue(OptNoIntel),
            NoReport = pr.GetValue(OptNoReport),
            TimeoutSec = timeout <= 0 ? 30 : Math.Max(5, timeout),
            Concurrency = cc <= 0 ? 8 : Math.Clamp(cc, 1, 32),
            ConfigSource = pr.GetValue(OptConfig),
            ApiMappingSource = pr.GetValue(OptApiMapping),
            Offline = pr.GetValue(OptOffline),
            Proxy = pr.GetValue(OptProxy),
            Verbose = pr.GetValue(OptVerbose),
        };

        string? sort = pr.GetValue(OptSort);
        if (!string.IsNullOrEmpty(sort))
        {
            sort = sort.ToLower();
            if (sort is not ("size" or "date" or "hot" or "title"))
            {
                Console.Error.WriteLine($"错误: --sort 可选 size|date|hot|title,收到「{sort}」");
                o.Help = true;
            }
            o.Sort = sort;
        }

        string ms = (pr.GetValue(OptMinSize) ?? "").Trim().ToUpper();
        if (ms.Length > 0)
        {
            Match mm = Regex.Match(ms, "([\\d.]+)([KMGT]?B?)");
            if (!mm.Success)
            {
                Console.Error.WriteLine("错误: --min-size 格式如 500MB / 2GB");
                o.Help = true;
            }
            else
            {
                double v = double.Parse(mm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                string u = mm.Groups[2].Value;
                long mult = u.StartsWith("T") ? 1099511627776L : u.StartsWith("G") ? 1073741824L
                         : u.StartsWith("M") ? 1048576L : u.StartsWith("K") ? 1024L : 1L;
                o.MinSizeBytes = (long)(v * mult);
            }
        }
        return o;
    }
}

static class CliHelp
{
    public static void Print()
    {
        Console.WriteLine("""
        BTSOU_Plus 命令行版 — BT 资源聚合搜索(单文件脚本)

        用法:
          dotnet run src/btsou.cs -- list                      列出资源库
          dotnet run src/btsou.cs -- search <关键词> [选项]    搜索资源
          dotnet run src/btsou.cs -- sync                      拉取并解析搜索源,存储本地(sources.json/txt)
          dotnet run src/btsou.cs -- update                    检查版本更新

        search 选项:
          --lib <名称>        仅搜索指定资源库(默认搜索全部启用库)
          --pages <N>         每个库最多翻页数(默认 1)
          --top <N>           仅显示前 N 条(表格/磁链输出;JSON/CSV 导出全部)
          --sort size|date|hot|title   排序字段(默认按抓取顺序)
          --desc              降序(默认升序)
          --min-size <大小>   过滤小于该大小的结果,如 500MB / 2GB
          --regex <模式>      标题正则过滤
          --precise           标题必须包含关键词
          --no-hkw            关闭广告词清理
          --no-intel          关闭智能滤词
          --no-dedupe         关闭按磁链去重
          --no-report         忽略本地 Report.dll 举报哈希
          --magnet            仅输出磁链(可配合 --torrent 附种子链接)
          --json              JSON 输出
          --csv <文件>        导出 CSV(Excel 可直接打开)
          --torrent           输出里附种子下载地址
          --timeout <秒>      HTTP 超时(默认 30)
          --concurrency <N>   并发库数(默认 8)
          --proxy <URL>       HTTP 代理,如 http://127.0.0.1:7890
          --config <URL|路径> 指定 ResPool.txt 来源
          --offline           仅使用本地缓存配置(data/btsou/)
          --verbose           输出调试信息

        示例:
          dotnet run src/btsou.cs -- search 星际穿越 --sort size --desc --top 10
          dotnet run src/btsou.cs -- search ubuntu --lib 1024BT --pages 2 --min-size 1GB
          dotnet run src/btsou.cs -- search debian --magnet > mags.txt
        """);
    }
}

sealed class CliOptions
{
    public string? Command;              // list | search | update
    public string? Keyword;
    public string? Lib;                  // --lib 指定资源库
    public int Pages = 1;                // --pages 每库最多翻页数
    public int Top;                      // --top 仅打印前 N 条
    public string? Sort;                 // --sort size|date|hot|title
    public bool Desc;                    // --desc 降序
    public bool Json;                    // --json
    public string? Csv;                  // --csv <file>
    public bool MagnetOnly;              // --magnet
    public bool ShowTorrent;             // --torrent 追加种子下载链接
    public long MinSizeBytes;            // --min-size 如 500MB / 2GB
    public string? Regex;                // --regex 标题正则
    public bool Precise;                 // --precise 精确匹配
    public bool NoHkw;                   // --no-hkw 关闭广告词清理
    public bool NoDedupe;                // --no-dedupe 关闭按磁链去重
    public bool NoIntel;                 // --no-intel 关闭智能滤词
    public bool NoReport;                // --no-report 忽略本地举报哈希
    public int TimeoutSec = 30;
    public int Concurrency = 8;
    public string? ConfigSource;         // --config <url|路径>
    public string? ApiMappingSource;     // --api-mapping <url|路径>
    public bool Offline;                 // --offline 仅用本地缓存配置
    public string? Proxy;                // --proxy http://host:port
    public bool Verbose;                 // --verbose
    public bool Help;
}

sealed class SearchResultItem
{
    public string Title = "";
    public string MagnetLink = "";
    public string Size = "";
    public string UpdateTime = "";
    public string Popularity = "";
    public string DetailUrl = "";
    public string Library = "";   // 来源资源库(脚本扩展字段)
}

sealed class ResourceConfig
{
    public string Name = "";
    public bool Enabled;
    public bool NeedProxy;
    public string SearchUrlTemplate = "";
    public string ResultSeparator = "";
    public string NextPageIndicator = "";
    public string UseWebView2Raw = "";   // 形如 "True/False[/True]"
    public string EncodeType = "UrlEncode";
    public bool IsApi;
    public string NavigationPage = "";
    public bool FixedRules;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool UseWebView2Enabled =>
        !string.IsNullOrEmpty(UseWebView2Raw) &&
        UseWebView2Raw.Split('/')[0].Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
}

sealed class ApiMapping
{
    public string Name = "";
    public string ListPath = "";
    public string TitleField = "";
    public string SizeField = "";
    public string HashField = "";
    public string DateField = "";
    public string TotalField = "";
    public string CodeField = "";
    public string SuccessValue = "";
    public int PageSize = 20;
    public string IdField = "";
    public string DetailUrlTemplate = "";
}

// ---------------------------------------------------------------------------
// 搜索源本地快照(解析后的结构化配置,sync/自动同步时写入 data/btsou/)
// ---------------------------------------------------------------------------

sealed class SourcesData
{
    public string Version = "";            // 配置版本号(ResPool 版本=)
    public string ResPoolUrl = "";         // 记录来源地址
    public string ApiMappingUrl = "";
    public string UpdateUrl = "";
    public string FetchedAt = "";          // 抓取时间 yyyy-MM-dd HH:mm:ss
    public string QrUrlPrefix = "";
    public string TorrentUrlPrefix = "";
    public string PreviewUrlPrefix = "";
    public string SubscribeUrl = "";
    public List<string> BlockedWords = [];
    public List<string> AdWords = [];
    public List<string> IntelWords = [];
    public List<ResourceConfig> Resources = [];
    public List<ApiMapping> ApiMappings = [];
}

// ---------------------------------------------------------------------------
// 正则模式(与 RegexPatterns.cs 一致)
// ---------------------------------------------------------------------------

static class Patterns
{
    public static readonly Regex ATag = new("<a[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    public static readonly Regex Magnet = new("(magnet:\\?xt=urn:btih:[a-fA-F0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static readonly Regex Hash = new("[a-fA-F0-9]{40}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static readonly Regex Size = new("(?:大小|文档大小|文件大小|length|size)\\s*[:：]\\s*(?:<[^>]+>)?([\\d.]+\\s*(?:TB|T|GB|G|MB|M|KB|K))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static readonly Regex SizeTd = new("<td[^>]*>\\s*([\\d.,]+)\\s*(GB|MB|KB|TB)\\s*</td>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static readonly Regex SizeCenter = new("<td\\s+class=\"text-center\">([\\d,]+(?:\\.\\d+)?)\\s*(GB|MB|KB|TB)</td>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static readonly Regex Time = new("(?:添加时间|添加時間|收录时间|收錄時間|创建时间|創建時間|时间|date|发布日期)\\s*[:：]\\s*(?:<[^>]+>)?((?:\\d+)\\s*(?:个月前|年前|天前|小时前|分钟前|\\d{4}-\\d{2}-\\d{2}))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static readonly Regex DateColl = new("<td[^>]*class=\"[^\"]*coll-date[^\"]*\"[^>]*>\\s*(.*?)\\s*</td>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    public static readonly Regex DateFallback = new("([A-Za-z]{3}\\.\\s*\\d{1,2}(?:st|nd|rd|th)?\\s*'\\d{2})", RegexOptions.Compiled);
    public static readonly Regex DateYmd = new("(\\d{4}-\\d{2}-\\d{2})", RegexOptions.Compiled);
    public static readonly Regex Hot = new("(?:热度|熱度|人气|人氣|点击|hits|views|clicks)\\s*[:：]\\s*(?:<[^>]+>)?(\\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static readonly Regex InputMagnet = new("<input[^>]*id=\"(?:input-magnet|magnet|m_link)\"[^>]*value=\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static readonly Regex AMagnet = new("<a\\s+href=\"(magnet:[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static readonly Regex SizeDetail = new("(?:种子大小|大小)\\s*[:：]\\s*(?:<[^>]+>)*\\s*([\\d,.]+)\\s*(TB|T|GB|G|MB|M|KB|K)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static readonly Regex DateDetail = new("(?:收录时间|发布日期|创建时间)\\s*[:：]\\s*(\\d{4}-\\d{2}-\\d{2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static readonly Regex TitleAttr = new("title=\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}

// ---------------------------------------------------------------------------
// 核心实现
// ---------------------------------------------------------------------------

static class BtsouApp
{
    public const string ResPoolUrl = "https://www.pc936.com/u/UpData/BTSOU_Plus/ResPool.txt";
    public const string ApiMappingUrl = "https://www.pc936.com/u/UpData/BTSOU_Plus/ApiMapping.txt";
    public const string UpdateInfoUrl = "http://www.pc936.com/u/Updata/BTSOU_Plus/UpData.txt";

    // ---- 状态(由配置加载填充) ----
    static readonly List<ResourceConfig> Resources = [];
    static readonly Dictionary<string, ApiMapping> ApiMappings = new(StringComparer.OrdinalIgnoreCase);
    static readonly List<string> BlockedWords = [];
    static readonly List<string> AdWords = [];
    static readonly List<string> IntelWords = [];
    static string QrUrlPrefix = "";
    static string TorrentUrlPrefix = "";
    static string PreviewUrlPrefix = "";
    static string SubscribeUrl = "";
    static string ConfigVersion = "";

    static HttpClient? _http;
    static readonly HashSet<string> ReportedHashes = new(StringComparer.OrdinalIgnoreCase);

    // Spectre 绑定到 stderr 的控制台:进度条/日志走它,避免污染 stdout 的 JSON/磁链/CSV 机器输出
    static readonly IAnsiConsole Err = AnsiConsole.Create(new AnsiConsoleSettings
    {
        Out = new AnsiConsoleOutput(Console.Error),
    });

    public static async Task<int> RunAsync(CliOptions o)
    {
        _http = CreateHttpClient(o);
        LoadReportedHashes();   // 本地 Report.dll(存在才生效)

        switch (o.Command)
        {
            case "list":
                if (!await LoadConfigAsync(o)) return 1;
                PrintList();
                return 0;
            case "sync":
                return await SyncSourcesAsync(o);
            case "update":
                await CheckUpdateAsync(o);
                return 0;
            case "search":
                if (string.IsNullOrEmpty(o.Keyword)) { Console.Error.WriteLine("错误: search 需要关键词,如: search 星际穿越"); return 1; }
                if (!await LoadConfigAsync(o)) return 1;
                return await SearchAsync(o);
            default:
                Console.Error.WriteLine($"未知命令: {o.Command}");
                CliHelp.Print();
                return 1;
        }
    }

    // ======================================================================
    // 配置加载:远程优先 -> 本地缓存回退 -> 显式 --config
    // ======================================================================
    static async Task<bool> LoadConfigAsync(CliOptions o)
    {
        string resContent = "", apiContent = "";
        bool fromRemote = false;

        // 1) 显式指定来源
        if (!string.IsNullOrEmpty(o.ConfigSource))
        {
            resContent = await ReadSourceAsync(o.ConfigSource, o.TimeoutSec) ?? "";
            if (resContent.Length == 0) { Console.Error.WriteLine($"错误: 无法读取 --config 指定来源: {o.ConfigSource}"); return false; }
        }
        else if (!o.Offline)
        {
            // 2) 远程(短超时)
            resContent = await ReadSourceAsync(ResPoolUrl, Math.Min(10, o.TimeoutSec)) ?? "";
            if (resContent.Length > 0)
            {
                fromRemote = true;
                TryWriteCache("ResPool.txt", resContent);   // 顺手刷新原始缓存
            }
        }
        if (resContent.Length == 0)
        {
            // 3) 本地回退:优先已解析快照 sources.json,其次原始 ResPool.txt
            string? snapshot = FindCacheFile("sources.json");
            if (snapshot != null)
            {
                if (LoadFromSourcesFile(snapshot))
                {
                    if (o.Verbose) Console.Error.WriteLine($"[配置] 使用本地解析快照: {snapshot}");
                    return true;
                }
            }
            string? cache = FindCacheFile("ResPool.txt");
            if (cache == null) { Console.Error.WriteLine("错误: 无法获取 ResPool.txt 配置(网络不可用且无本地缓存)。可用 --offline 或 --config 指定。"); return false; }
            resContent = File.ReadAllText(cache);
            if (o.Verbose) Console.Error.WriteLine($"[配置] 使用本地缓存: {cache}");
        }

        // API 映射(可选,仅 API 库需要)
        if (!string.IsNullOrEmpty(o.ApiMappingSource))
            apiContent = await ReadSourceAsync(o.ApiMappingSource, o.TimeoutSec) ?? "";
        else if (!o.Offline)
        {
            apiContent = await ReadSourceAsync(ApiMappingUrl, Math.Min(10, o.TimeoutSec)) ?? "";
            if (apiContent.Length > 0) TryWriteCache("ApiMapping.txt", apiContent);
        }
        if (apiContent.Length == 0)
        {
            string? c = FindCacheFile("ApiMapping.txt");
            if (c != null) apiContent = File.ReadAllText(c);
        }

        ParseResPool(resContent);
        ParseApiMapping(apiContent);

        // 远程(或 --config)加载成功 → 解析结果直接存储到本地并记录地址
        if (fromRemote || o.ConfigSource != null || o.ApiMappingSource != null)
            TrySaveSources(SnapshotSources());

        if (o.Verbose)
            Console.Error.WriteLine($"[配置] 版本={ConfigVersion},资源库 {Resources.Count} 个,屏蔽词 {BlockedWords.Count} 个,API 映射 {ApiMappings.Count} 个");
        return true;
    }

    // ======================================================================
    // 搜索源本地快照:解析结果持久化 + 地址清单
    // ======================================================================
    static SourcesData SnapshotSources() => new()
    {
        Version = ConfigVersion,
        ResPoolUrl = ResPoolUrl,
        ApiMappingUrl = ApiMappingUrl,
        UpdateUrl = UpdateInfoUrl,
        FetchedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        QrUrlPrefix = QrUrlPrefix,
        TorrentUrlPrefix = TorrentUrlPrefix,
        PreviewUrlPrefix = PreviewUrlPrefix,
        SubscribeUrl = SubscribeUrl,
        BlockedWords = BlockedWords.ToList(),
        AdWords = AdWords.ToList(),
        IntelWords = IntelWords.ToList(),
        Resources = Resources.ToList(),
        ApiMappings = ApiMappings.Values.ToList(),
    };

    static readonly JsonSerializerOptions SourceJsonOpts = new()
    {
        WriteIndented = true,
        IncludeFields = true,   // ResourceConfig/ApiMapping 使用公共字段
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static bool TrySaveSources(SourcesData data)
    {
        try
        {
            string? dir = FindOrCreateCacheDir();
            if (dir == null) return false;
            File.WriteAllText(Path.Combine(dir, "sources.json"), JsonSerializer.Serialize(data, SourceJsonOpts), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "sources.txt"), BuildSourcesTxt(data), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"提示: 搜索源快照写入失败(不影响本次运行): {ex.Message}");
            return false;
        }
    }

    static bool LoadFromSourcesFile(string path)
    {
        try
        {
            var data = JsonSerializer.Deserialize<SourcesData>(File.ReadAllText(path), SourceJsonOpts);
            if (data == null) return false;
            ConfigVersion = data.Version;
            QrUrlPrefix = data.QrUrlPrefix; TorrentUrlPrefix = data.TorrentUrlPrefix;
            PreviewUrlPrefix = data.PreviewUrlPrefix; SubscribeUrl = data.SubscribeUrl;
            BlockedWords.Clear(); BlockedWords.AddRange(data.BlockedWords);
            AdWords.Clear(); AdWords.AddRange(data.AdWords);
            IntelWords.Clear(); IntelWords.AddRange(data.IntelWords);
            Resources.Clear(); Resources.AddRange(data.Resources);
            ApiMappings.Clear();
            foreach (var m in data.ApiMappings) ApiMappings[m.Name] = m;
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[配置] sources.json 解析失败({ex.Message}),回退原始缓存");
            return false;
        }
    }

    static string BuildSourcesTxt(SourcesData d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BTSOU_Plus 搜索源清单(本地解析快照)");
        sb.AppendLine($"配置版本: {d.Version}");
        sb.AppendLine($"抓取时间: {d.FetchedAt}");
        sb.AppendLine("来源地址:");
        sb.AppendLine($"  ResPool    : {d.ResPoolUrl}");
        sb.AppendLine($"  ApiMapping : {d.ApiMappingUrl}");
        sb.AppendLine($"  更新检测   : {d.UpdateUrl}");
        sb.AppendLine();
        sb.AppendLine($"资源库 {d.Resources.Count} 个:");
        sb.AppendLine();
        int i = 0;
        foreach (var r in d.Resources)
        {
            i++;
            sb.AppendLine($"[{i}] {r.Name}{(r.Enabled ? "  启用" : "  停用")}{(r.NeedProxy ? "  需代理" : "")}{(r.IsApi ? "  API" : "")}");
            sb.AppendLine($"    搜索地址: {r.SearchUrlTemplate.Replace("{keyword}", "<关键词>").Replace("{page}", "<页码>")}");
            if (r.NavigationPage.Length > 0) sb.AppendLine($"    导航页  : {r.NavigationPage}");
        }
        if (d.TorrentUrlPrefix.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"种子地址前缀: {d.TorrentUrlPrefix}");
        }
        if (d.BlockedWords.Count > 0)
            sb.AppendLine($"屏蔽词 {d.BlockedWords.Count} 个、广告词 {d.AdWords.Count} 个、智能滤词 {d.IntelWords.Count} 个(详见 sources.json)");
        return sb.ToString();
    }

    // 输出"记录地址"摘要(sync 命令 / --verbose 使用)
    static void PrintSourcesSummary(SourcesData d, string dir)
    {
        Err.MarkupLine($"[bold]搜索源已解析并存储到本地[/]");
        Err.MarkupLine($"  配置版本: [yellow]{d.Version}[/]  抓取时间: [cyan]{d.FetchedAt}[/]");
        Err.MarkupLine($"  来源地址: [blue]{d.ResPoolUrl}[/]");
        if (d.Resources.Count > 0)
        {
            Err.MarkupLine($"  资源库 {d.Resources.Count} 个:");
            int i = 0;
            foreach (var r in d.Resources)
            {
                i++;
                string addr = r.SearchUrlTemplate.Replace("{keyword}", "<关键词>").Replace("{page}", "<页码>");
                Err.MarkupLine($"    [[{i,2}]] [green]{Markup.Escape(r.Name)}[/] {(r.Enabled ? "" : "[grey](停用)[/] ")}{Markup.Escape(addr)}");
            }
        }
        Err.MarkupLine($"  文件: [green]{Path.Combine(dir, "sources.json")}[/] / [green]sources.txt[/]");
    }

    static async Task<string?> ReadSourceAsync(string urlOrPath, int timeoutSec)
    {
        if (urlOrPath.StartsWith("http://") || urlOrPath.StartsWith("https://"))
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
                return await _http!.GetStringAsync(urlOrPath, cts.Token);
            }
            catch { return null; }
        }
        return File.Exists(urlOrPath) ? File.ReadAllText(urlOrPath) : null;
    }

    static void ParseResPool(string content)
    {
        Resources.Clear(); BlockedWords.Clear(); AdWords.Clear(); IntelWords.Clear();
        QrUrlPrefix = ""; TorrentUrlPrefix = ""; PreviewUrlPrefix = ""; SubscribeUrl = ""; ConfigVersion = "";

        bool inLibrary = false;
        foreach (string rawLine in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.TrimEnd('\t').Trim();
            if (line == "库=") { inLibrary = true; continue; }

            if (!inLibrary)
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim();
                string val = line[(eq + 1)..].Trim();
                switch (key)
                {
                    case "版本": ConfigVersion = val; break;
                    case "二维码": QrUrlPrefix = val; break;
                    case "种子": TorrentUrlPrefix = val; break;
                    case "预览": PreviewUrlPrefix = val; break;
                    case "订阅": SubscribeUrl = val; break;
                    case "广告词": SplitWords(val, AdWords); break;
                    case "屏蔽词": SplitWords(val, BlockedWords); break;
                    case "智能滤": SplitWords(val, IntelWords); break;
                    case "女优": SplitWords(val, IntelWords); break;   // 与原程序一致:并入智能滤词
                }
            }
            else
            {
                string[] p = rawLine.Split('\t');
                if (p.Length < 5) continue;
                Resources.Add(new ResourceConfig
                {
                    Name = p[0].Trim(),
                    Enabled = p[1].Trim().Equals("True", StringComparison.OrdinalIgnoreCase),
                    NeedProxy = p[2].Trim().Equals("True", StringComparison.OrdinalIgnoreCase),
                    SearchUrlTemplate = p[3].Trim(),
                    ResultSeparator = p[4].Trim(),
                    NextPageIndicator = p.Length > 5 ? p[5].Trim() : "",
                    UseWebView2Raw = p.Length > 6 ? p[6].Trim() : "",
                    EncodeType = p.Length > 7 ? p[7].Trim() : "UrlEncode",
                    IsApi = p.Length > 8 && p[8].Trim().Equals("True", StringComparison.OrdinalIgnoreCase),
                    NavigationPage = p.Length > 9 ? p[9].Trim() : "",
                    FixedRules = p.Length > 10 && p[10].Trim().Equals("TRUE", StringComparison.OrdinalIgnoreCase),
                });
            }
        }
    }

    static void SplitWords(string val, List<string> target)
    {
        foreach (string w in val.Split(','))
        {
            string t = w.Trim();
            if (t.Length > 0) target.Add(t);
        }
    }

    static void ParseApiMapping(string content)
    {
        ApiMappings.Clear();
        if (string.IsNullOrEmpty(content)) return;
        foreach (string line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) continue;
            string[] p = line.Split('\t');
            if (p.Length < 12) continue;
            if (p[0].Trim() == "Name" || p[0].TrimStart().StartsWith("对应")) continue;   // 跳过表头行
            ApiMappings[p[0].Trim()] = new ApiMapping
            {
                Name = p[0].Trim(),
                ListPath = p[1].Trim(),
                TitleField = p[2].Trim(),
                SizeField = p[3].Trim(),
                HashField = p[4].Trim(),
                DateField = p[5].Trim(),
                TotalField = p[6].Trim(),
                CodeField = p[7].Trim(),
                SuccessValue = p[8].Trim(),
                PageSize = int.TryParse(p[9].Trim(), out int ps) ? ps : 20,
                IdField = p[10].Trim(),
                DetailUrlTemplate = p[11].Trim(),
            };
        }
    }

    static void LoadReportedHashes()
    {
        if (!File.Exists("Report.dll")) return;
        try
        {
            foreach (string line in File.ReadAllLines("Report.dll", Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] p = line.Split('\t');
                if (p.Length >= 4)
                {
                    string h = p[3].Trim();
                    if (h.Length > 0) ReportedHashes.Add(h);
                }
            }
        }
        catch { /* 本地举报文件损坏则忽略 */ }
    }

    // ======================================================================
    // list
    // ======================================================================
    static void PrintList()
    {
        // 非交互(管道/重定向):纯文本,避免 Spectre 表格在窄宽度下错位
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            Console.WriteLine($"BTSOU_Plus 资源库配置 (版本={ConfigVersion})  共 {Resources.Count} 个库");
            Console.WriteLine();
            Console.WriteLine($"{"名称",-10}{"启用",-5}{"代理",-5}{"API",-5}{"编码",-13}{"下一页指示",-12}URL 模板");
            foreach (var r in Resources.OrderByDescending(x => x.Enabled).ThenBy(x => x.Name))
            {
                Console.WriteLine($"{r.Name,-10}{(r.Enabled ? "✓" : "✗"),-5}{(r.NeedProxy ? "✓" : "-"),-5}{(r.IsApi ? "✓" : "-"),-5}{r.EncodeType,-13}{r.NextPageIndicator,-12}{r.SearchUrlTemplate}");
            }
            Console.WriteLine();
            Console.WriteLine("提示: search 默认搜索全部启用库;用 --lib <名称> 指定单个库;--pages N 控制翻页。");
            return;
        }

        AnsiConsole.MarkupLine($"[bold]BTSOU_Plus 资源库配置[/] [grey](版本={ConfigVersion})[/]  共 [yellow]{Resources.Count}[/] 个库");
        Console.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("资源库")
            .AddColumn(new TableColumn("名称").NoWrap())
            .AddColumn(new TableColumn("启用").Centered())
            .AddColumn(new TableColumn("代理").Centered())
            .AddColumn(new TableColumn("API").Centered())
            .AddColumn(new TableColumn("编码").NoWrap())
            .AddColumn(new TableColumn("下一页指示").NoWrap())
            .AddColumn(new TableColumn("URL 模板").NoWrap());
        foreach (var r in Resources.OrderByDescending(x => x.Enabled).ThenBy(x => x.Name))
        {
            table.AddRow(
                Markup.Escape(r.Name),
                r.Enabled ? "[green]✓[/]" : "[grey]✗[/]",
                r.NeedProxy ? "[yellow]✓[/]" : "[grey]-[/]",
                r.IsApi ? "[blue]✓[/]" : "[grey]-[/]",
                Markup.Escape(r.EncodeType),
                Markup.Escape(r.NextPageIndicator),
                Markup.Escape(r.SearchUrlTemplate));
        }
        AnsiConsole.Write(table);
        Console.WriteLine();
        AnsiConsole.MarkupLine("[grey]提示: search 默认搜索全部启用库;用 --lib <名称> 指定单个库;--pages N 控制翻页。[/]");
    }

    // ======================================================================
    // sync:拉取远程搜索源 -> 解析 -> 存储本地(sources.json/txt)并记录地址
    // ======================================================================
    static async Task<int> SyncSourcesAsync(CliOptions o)
    {
        if (o.Offline)
            Console.Error.WriteLine("提示: --offline 下 sync 仅加载本地快照并打印地址清单,不联网。");

        bool ok = await LoadConfigAsync(o);
        if (!ok) return 1;

        // 无论远程/本地来源,都确保快照落盘(--offline 时本地已存在则无需重写,但重写也无害)
        bool saved = false;
        if (!o.Offline || FindCacheFile("sources.json") == null)
            saved = TrySaveSources(SnapshotSources());
        else
            saved = true;

        string? dir = FindCacheDir() ?? FindOrCreateCacheDir() ?? ".";
        if (saved)
        {
            PrintSourcesSummary(SnapshotSources(), dir ?? ".");
            Console.Error.WriteLine($"地址清单: {Path.Combine(dir ?? ".", "sources.txt")}");
        }
        else
        {
            Console.Error.WriteLine("错误: 搜索源快照写入失败。");
            return 1;
        }
        return 0;
    }

    // ======================================================================
    // update(检查更新,脚本不自替换,仅输出信息)
    // ======================================================================
    static async Task CheckUpdateAsync(CliOptions o)
    {
        string content = await ReadSourceAsync(UpdateInfoUrl, o.TimeoutSec) ?? "";
        if (content.Length == 0) { Console.WriteLine("检查更新失败:无法获取更新信息。"); return; }
        string version = MidStrEx(content, "版本号=", "\r\n");
        string url = MidStrEx(content, "链接=", "\r\n");
        if (version.Length == 0 || url.Length == 0)
        {
            foreach (string line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("版本号=")) version = line["版本号=".Length..].Trim();
                else if (line.StartsWith("链接=")) url = line["链接=".Length..].Trim();
            }
        }
        Console.WriteLine($"远程版本: {version}");
        Console.WriteLine($"下载地址: {url}");
        Console.WriteLine("本脚本不参与自更新,请自行下载新版程序。");
    }

    static string MidStrEx(string source, string start, string end)
    {
        if (string.IsNullOrEmpty(source)) return "";
        int i = source.IndexOf(start, StringComparison.Ordinal);
        if (i == -1) return "";
        i += start.Length;
        int j = source.IndexOf(end, i, StringComparison.Ordinal);
        return j == -1 ? source[i..] : source[i..j];
    }

    // ======================================================================
    // search
    // ======================================================================
    static async Task<int> SearchAsync(CliOptions o)
    {
        string keyword = o.Keyword!.Trim();

        // 1) 关键词屏蔽词检查(与原程序一致,无开关)
        string? blockedHit = BlockedWords.FirstOrDefault(w => w.Length > 0 && keyword.ToLower().Contains(w.ToLower()));
        if (blockedHit != null)
        {
            Console.Error.WriteLine($"警告: 关键词包含违规词「{blockedHit}」,已拒绝搜索。请更换关键词。");
            return 1;
        }

        // 2) 确定搜索目标库
        List<ResourceConfig> targets;
        if (!string.IsNullOrEmpty(o.Lib))
        {
            var lib = Resources.FirstOrDefault(r => r.Name.Equals(o.Lib, StringComparison.OrdinalIgnoreCase));
            if (lib == null) { Console.Error.WriteLine($"错误: 资源库「{o.Lib}」不存在。可用 list 查看。"); return 1; }
            if (!lib.Enabled) Console.Error.WriteLine($"提示: 资源库「{lib.Name}」未启用,仍将尝试搜索。");
            targets = [lib];
        }
        else
        {
            targets = Resources.Where(r => r.Enabled).ToList();
            if (targets.Count == 0) { Console.Error.WriteLine("错误: 没有启用的资源库。"); return 1; }
        }

        var webViewLibs = targets.Where(r => r.UseWebView2Enabled).ToList();
        var httpLibs = targets.Where(r => !r.UseWebView2Enabled).ToList();
        foreach (var w in webViewLibs)
            Console.Error.WriteLine($"提示: 资源库「{w.Name}」标记为 WebView2 渲染,脚本不支持,已跳过。");

        if (o.Verbose)
            Console.Error.WriteLine($"正在搜索「{keyword}」: {httpLibs.Count} 个资源库 × 最多 {o.Pages} 页(并发 {o.Concurrency})");

        // 3) 并发搜索(Spectre 进度条走 stderr,stdout 留给结果/机器可读输出)
        var results = new ConcurrentBag<SearchResultItem>();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = o.Concurrency, CancellationToken = cts.Token };
            if (AnsiConsole.Profile.Capabilities.Interactive && httpLibs.Count > 0)
            {
                // 交互终端:Spectre 进度条(stderr)
                var progress = Err.Progress()
                    .AutoClear(false)
                    .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new ElapsedTimeColumn());
                await progress.StartAsync(async ctx =>
                {
                    int total = httpLibs.Count;
                    var progressTask = ctx.AddTask($"[green]搜索 {total} 个资源库[/]");
                    progressTask.MaxValue = total;
                    int completed = 0;
                    await Parallel.ForEachAsync(httpLibs, parallelOptions, async (lib, ct) =>
                    {
                        await SearchLibAsync(lib, keyword, o, results, ct);
                        int done = Interlocked.Increment(ref completed);
                        progressTask.Value = done;
                        progressTask.Description = $"[green]{done}/{total}[/] 个资源库已完成";
                    });
                    progressTask.StopTask();
                });
            }
            else
            {
                // 非交互(管道/重定向):不渲染进度,直接并发抓取
                await Parallel.ForEachAsync(httpLibs, parallelOptions,
                    async (lib, ct) => await SearchLibAsync(lib, keyword, o, results, ct));
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("已取消。");
        }
        sw.Stop();

        var all = results.ToList();
        Err.MarkupLine($"抓取完成: [bold]原始 {all.Count} 条[/],耗时 [cyan]{sw.Elapsed.TotalSeconds:0.0}s[/]");

        // 4) 过滤
        var filtered = ApplyFilters(all, keyword, o);

        // 5) 排序
        filtered = SortResults(filtered, o);

        // 6) 输出
        if (o.Csv != null) WriteCsv(filtered, o.Csv);
        if (o.Json) WriteJson(filtered);
        if (o.MagnetOnly) WriteMagnets(filtered, o);
        else if (!o.Json) WriteTable(filtered, o);

        Err.MarkupLine($"最终结果: [green]{filtered.Count} 条[/]");
        return 0;
    }

    // ---- 单库搜索:翻页直到下一页指示消失 / 结果为空 / 达到 --pages ----
    static async Task SearchLibAsync(ResourceConfig lib, string keyword, CliOptions o,
        ConcurrentBag<SearchResultItem> results, CancellationToken ct)
    {
        int page = 1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (page > o.Pages) break;

            string url = BuildSearchUrl(lib, keyword, page);
            try
            {
                string? content = await FetchHtmlAsync(url);
                if (content == null) { Console.Error.WriteLine($"[{lib.Name}] 第{page}页 请求失败,停止。"); break; }

                List<SearchResultItem> items;
                bool hasNext = false;

                if (lib.IsApi)
                {
                    if (!ApiMappings.TryGetValue(lib.Name, out var m))
                    { Console.Error.WriteLine($"[{lib.Name}] 缺少 API 映射,跳过。"); break; }
                    (items, hasNext) = ParseApiResults(content, m, page, lib.Name);
                    if (o.Verbose) Console.Error.WriteLine($"[{lib.Name}] API 第{page}页 解析 {items.Count} 条,hasNext={hasNext}");
                }
                else
                {
                    items = ParseResults(content, lib.ResultSeparator, url, lib.Name);
                    bool anyMagnet = items.Any(i => i.MagnetLink.Length > 0);
                    if (anyMagnet)
                        items = items.Where(i => i.MagnetLink.Length > 0).ToList();
                    else if (items.Count > 0)
                        items = await FetchDetailsAsync(items);   // 列表页无磁链 -> 详情页补全

                    if (lib.NextPageIndicator.Length > 0)
                    {
                        string decoded = WebUtility.HtmlDecode(lib.NextPageIndicator);
                        hasNext = content.Contains(lib.NextPageIndicator, StringComparison.OrdinalIgnoreCase)
                               || content.Contains(decoded, StringComparison.OrdinalIgnoreCase);
                    }
                    if (o.Verbose) Console.Error.WriteLine($"[{lib.Name}] 第{page}页 解析 {items.Count} 条,hasNext={hasNext}");
                }

                if (items.Count == 0) break;
                foreach (var it in items) { it.Library = lib.Name; results.Add(it); }
                if (!hasNext) break;
                page++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{lib.Name}] 第{page}页 出错: {ex.Message}");
                break;
            }
        }
    }

    // ======================================================================
    // URL 构造 / 关键词编码
    // ======================================================================
    static string EncodeKeyword(string keyword, string encodeType)
    {
        if (string.IsNullOrEmpty(encodeType) || encodeType == "UrlEncode")
            return Uri.EscapeDataString(keyword);
        if (encodeType == "Base64UrlSafe")
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(keyword)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        if (encodeType == "Hex")
            return Convert.ToHexString(Encoding.UTF8.GetBytes(keyword)).ToLowerInvariant();
        if (encodeType == "Raw")
            return keyword;
        return Uri.EscapeDataString(keyword);
    }

    static string BuildSearchUrl(ResourceConfig config, string keyword, int page)
        => config.SearchUrlTemplate.Replace("{keyword}", EncodeKeyword(keyword, config.EncodeType))
                                   .Replace("{page}", page.ToString());

    // ======================================================================
    // HTML 列表解析(与 ParseResults 一致)
    // ======================================================================
    public static List<SearchResultItem> ParseResults(string html, string separator, string baseUrl, string resourceName)
    {
        var list = new List<SearchResultItem>();
        if (string.IsNullOrEmpty(html) || string.IsNullOrEmpty(separator)) return list;

        var categorySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "XXX", "Video", "Hentai", "Pictures", "Doujinshi", "Games", "Lossless", "Audio" };

        foreach (string chunk in html.Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries))
        {
            string text = chunk.Trim();
            if (text.Length == 0 || text == "</tr>") continue;

            var item = new SearchResultItem();
            Match m = Patterns.ATag.Match(text);
            string? href = null, linkText = null;
            while (m.Success)
            {
                href = m.Groups[1].Value;
                linkText = Regex.Replace(m.Groups[2].Value, "<[^>]+>", "").Trim();
                if (!string.IsNullOrEmpty(linkText) && !categorySet.Contains(linkText)) break;
                m = m.NextMatch();
            }
            if (string.IsNullOrEmpty(href) && string.IsNullOrEmpty(linkText)) continue;

            Match m2 = Patterns.TitleAttr.Match(m.Value);
            string titleAttr = m2.Success ? m2.Groups[1].Value.Trim() : "";
            linkText = WebUtility.HtmlDecode(linkText);
            titleAttr = WebUtility.HtmlDecode(titleAttr);
            item.Title = resourceName == "U3C3" ? linkText
                        : (!string.IsNullOrEmpty(titleAttr) ? titleAttr : linkText);

            if (!string.IsNullOrEmpty(href))
            {
                if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    if (href.StartsWith("//")) href = "https:" + href;
                    else href = new Uri(new Uri(baseUrl), href).ToString();
                }
                item.DetailUrl = href;
            }

            Match mm = Patterns.Magnet.Match(text);
            if (mm.Success)
                item.MagnetLink = WebUtility.HtmlDecode(mm.Groups[1].Value);   // 解码 &amp; 等实体,保证磁链可用
            else
            {
                Match hm = Patterns.Hash.Match(text);
                if (hm.Success)
                {
                    int idx = hm.Index;
                    bool ok = false;
                    if (idx > 0 && (text[idx - 1] == ':' || text[idx - 1] == '/') && idx + 40 < text.Length)
                    {
                        char c = text[idx + 40];
                        if (c == '&' || c == '"' || c == '.' || c == '>') ok = true;
                    }
                    if (ok) item.MagnetLink = "magnet:?xt=urn:btih:" + hm.Value.ToUpper();
                }
            }

            string size = "";
            Match sm = Patterns.Size.Match(text);
            if (sm.Success) size = sm.Groups[1].Value.Trim();
            else
            {
                Match st = Patterns.SizeTd.Match(text);
                if (st.Success) size = st.Groups[1].Value.Replace(",", "") + " " + st.Groups[2].Value.ToUpper();
                else
                {
                    Match sc = Patterns.SizeCenter.Match(text);
                    if (sc.Success) size = sc.Groups[1].Value.Replace(",", "") + " " + sc.Groups[2].Value.ToUpper();
                }
            }
            if (size.Length > 0) item.Size = size;

            string time = "";
            Match tm = Patterns.Time.Match(text);
            if (tm.Success) time = ConvertToAbsoluteDate(tm.Groups[1].Value.Trim());
            else
            {
                Match dc = Patterns.DateColl.Match(text);
                if (dc.Success) time = ConvertEnglishDate(dc.Groups[1].Value.Trim());
                else
                {
                    Match df = Patterns.DateFallback.Match(text);
                    if (df.Success) time = ConvertEnglishDate(df.Groups[1].Value.Trim());
                    else
                    {
                        Match dy = Patterns.DateYmd.Match(text);
                        if (dy.Success) time = dy.Groups[1].Value;
                    }
                }
            }
            item.UpdateTime = time;

            Match hot = Patterns.Hot.Match(text);
            if (hot.Success) item.Popularity = hot.Groups[1].Value.Trim();

            if (item.Title.Length > 0 || item.MagnetLink.Length > 0 || item.DetailUrl.Length > 0)
                list.Add(item);
        }
        return list;
    }

    // ======================================================================
    // API JSON 解析(点分隔路径)
    // ======================================================================
    static (List<SearchResultItem>, bool) ParseApiResults(string json, ApiMapping m, int page, string libName)
    {
        var list = new List<SearchResultItem>();
        try
        {
            JsonNode? root = JsonNode.Parse(json);
            if (root == null) return (list, false);

            JsonNode? codeNode = GetJsonNode(root, m.CodeField);
            if (codeNode?.ToString() != m.SuccessValue) return (list, false);

            JsonNode? listNode = GetJsonNode(root, m.ListPath);
            if (listNode is not JsonArray arr) return (list, false);

            foreach (JsonNode? node in arr)
            {
                if (node is not JsonObject o) continue;
                string hash = GetStr(o, m.HashField);
                string id = GetStr(o, m.IdField);
                long bytes = 0;
                JsonNode? sz = GetJsonNode(o, m.SizeField);
                if (sz != null)
                {
                    if (sz is JsonValue v)
                    {
                        if (!v.TryGetValue<long>(out bytes)) long.TryParse(sz.ToString(), out bytes);
                    }
                    else long.TryParse(sz.ToString(), out bytes);
                }
                string date = GetStr(o, m.DateField).Split(new[] { ' ', 'T' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                list.Add(new SearchResultItem
                {
                    Title = CleanTitle(GetStr(o, m.TitleField)),
                    Size = FormatFileSize(bytes),
                    UpdateTime = date,
                    MagnetLink = hash.Length > 0 ? "magnet:?xt=urn:btih:" + hash : "",
                    DetailUrl = m.DetailUrlTemplate.Length > 0 && id.Length > 0 ? m.DetailUrlTemplate.Replace("{id}", id) : "",
                    Library = libName,
                });
            }

            int total = 0;
            JsonNode? totalNode = GetJsonNode(root, m.TotalField);
            if (totalNode != null) int.TryParse(totalNode.ToString(), out total);
            int pageSize = m.PageSize > 0 ? m.PageSize : 20;
            return (list, page * pageSize < total);
        }
        catch
        {
            return (list, false);
        }
    }

    static JsonNode? GetJsonNode(JsonNode? node, string? path)
    {
        if (string.IsNullOrEmpty(path) || node == null) return null;
        foreach (string seg in path.Split('.'))
        {
            if (node is not JsonObject o || !o.TryGetPropertyValue(seg, out JsonNode? child)) return null;
            node = child;
        }
        return node;
    }

    static string GetStr(JsonObject o, string field)
        => o.TryGetPropertyValue(field, out JsonNode? v) && v != null ? v.ToString() : "";

    static string CleanTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        title = WebUtility.HtmlDecode(title);
        title = Regex.Replace(title, "<[^>]+>", "");
        return title.Trim();
    }

    // ======================================================================
    // 详情页磁链补全(并发 10,与 FetchDetailsAsync 一致)
    // ======================================================================
    static async Task<List<SearchResultItem>> FetchDetailsAsync(List<SearchResultItem> items)
    {
        var valid = new List<SearchResultItem>();
        using var sem = new SemaphoreSlim(10);
        var tasks = items.Select(async item =>
        {
            await sem.WaitAsync();
            try
            {
                if (string.IsNullOrEmpty(item.DetailUrl)) return;
                string? html = await FetchHtmlAsync(item.DetailUrl);
                if (html == null) return;

                Match m = Patterns.InputMagnet.Match(html);
                if (!m.Success) m = Patterns.AMagnet.Match(html);
                if (!m.Success) m = Patterns.Magnet.Match(html);
                if (!m.Success) return;

                item.MagnetLink = WebUtility.HtmlDecode(m.Groups[1].Value);

                Match sm = Patterns.SizeDetail.Match(html);
                if (sm.Success)
                    item.Size = sm.Groups[1].Value.Replace(",", "") + " " + sm.Groups[2].Value.ToUpper();

                string ut = "";
                Match dm = Patterns.DateDetail.Match(html);
                if (dm.Success) ut = dm.Groups[1].Value;
                else
                {
                    Match fb = Patterns.DateYmd.Match(html);
                    if (fb.Success) ut = fb.Groups[1].Value;
                }
                if (ut.Length > 0) item.UpdateTime = ut;

                lock (valid) valid.Add(item);
            }
            catch { /* 单条详情失败忽略 */ }
            finally { sem.Release(); }
        }).ToList();
        await Task.WhenAll(tasks);
        return valid;
    }

    // ======================================================================
    // 日期转换
    // ======================================================================
    static string ConvertEnglishDate(string dateStr)
    {
        if (string.IsNullOrEmpty(dateStr)) return "";
        Match m = Regex.Match(dateStr, "([A-Za-z]+)\\.?\\s*(\\d{1,2})(?:st|nd|rd|th)?\\s*'(\\d{2})");
        if (m.Success)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "Jan",1 },{ "Feb",2 },{ "Mar",3 },{ "Apr",4 },{ "May",5 },{ "Jun",6 },
                { "Jul",7 },{ "Aug",8 },{ "Sep",9 },{ "Oct",10 },{ "Nov",11 },{ "Dec",12 },
            };
            if (map.TryGetValue(m.Groups[1].Value, out int month))
            {
                try
                {
                    var dt = new DateTime(2000 + int.Parse(m.Groups[3].Value), month, int.Parse(m.Groups[2].Value));
                    return dt.ToString("yyyy-MM-dd");
                }
                catch { }
            }
        }
        foreach (string fmt in new[] { "MMM. dd 'yy", "MMM dd 'yy", "MMM. dd yy", "MMM dd yy" })
        {
            if (DateTime.TryParseExact(dateStr, fmt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out DateTime d))
                return d.ToString("yyyy-MM-dd");
        }
        return "";
    }

    static string ConvertToAbsoluteDate(string timeStr)
    {
        if (string.IsNullOrEmpty(timeStr)) return "";
        if (DateTime.TryParseExact(timeStr, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out DateTime d))
            return d.ToString("yyyy-MM-dd");

        Match m = Regex.Match(timeStr, "(\\d+)\\s*(年前|个月前|天前|小时前|分钟前)");
        if (m.Success)
        {
            int n = int.Parse(m.Groups[1].Value);
            DateTime now = DateTime.Now;
            DateTime r = m.Groups[2].Value switch
            {
                "年前" => now.AddYears(-n),
                "个月前" => now.AddMonths(-n),
                "天前" => now.AddDays(-n),
                "小时前" => now.AddHours(-n),
                "分钟前" => now.AddMinutes(-n),
                _ => now,
            };
            return r.ToString("yyyy-MM-dd");
        }
        return timeStr;
    }

    // ======================================================================
    // 大小 / 热度
    // ======================================================================
    static string FormatFileSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        double v = bytes;
        while (v >= 1024 && i < units.Length - 1) { i++; v /= 1024; }
        return $"{v:0.##} {units[i]}";
    }

    static long ParseSizeToBytes(string sizeStr)
    {
        if (string.IsNullOrEmpty(sizeStr)) return 0;
        Match m = Regex.Match(sizeStr, "([\\d.]+)\\s*([TtGgMmKk]?B?)");
        if (!m.Success) return 0;
        double v = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        string u = m.Groups[2].Value.ToUpper();
        long mult = u.StartsWith("T") ? 1099511627776L
                  : u.StartsWith("G") ? 1073741824L
                  : u.StartsWith("M") ? 1048576L
                  : u.StartsWith("K") ? 1024L : 1L;
        return (long)(v * mult);
    }

    static int CompareSize(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2)) return 0;
        if (string.IsNullOrEmpty(s1)) return 1;
        if (string.IsNullOrEmpty(s2)) return -1;
        return ParseSizeToBytes(s1).CompareTo(ParseSizeToBytes(s2));
    }

    static int ComparePopularity(string p1, string p2)
    {
        if (string.IsNullOrEmpty(p1) && string.IsNullOrEmpty(p2)) return 0;
        if (string.IsNullOrEmpty(p1)) return 1;
        if (string.IsNullOrEmpty(p2)) return -1;
        if (int.TryParse(p1, out int a) && int.TryParse(p2, out int b)) return a.CompareTo(b);
        return string.Compare(p1, p2, StringComparison.Ordinal);
    }

    static string ExtractHash(string magnet)
    {
        if (string.IsNullOrEmpty(magnet)) return "";
        Match m = Regex.Match(magnet, "btih:([a-fA-F0-9]{40})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    static string TorrentUrl(string magnet)
        => TorrentUrlPrefix.Length > 0 && ExtractHash(magnet).Length > 0
            ? TorrentUrlPrefix + ExtractHash(magnet) + ".torrent" : "";

    // ======================================================================
    // 过滤(与 ApplyFilters 顺序一致)
    // ======================================================================
    static List<SearchResultItem> ApplyFilters(List<SearchResultItem> source, string keyword, CliOptions o)
    {
        var list = source.ToList();

        // 1) HKW 广告词清理
        if (!o.NoHkw)
        {
            const string domainPattern =
                @"\b[a-zA-Z0-9][a-zA-Z0-9.-]*\.(com|net|org|cn|tv|cc|xyz|top|club|site|vip|wang|ren|shop|online|tech|live|video|fun|art|space|host|cloud|app|dev|io|me|co|uk|de|jp|fr|ru|au|ca|in|id|ph|sg|hk|tw|kr|nl|se|no|fi|dk|pl|it|es|pt|br|mx|ar|cl|nz|za|ng|eg|sa|ae|tr|il|my|th|vn)\b";
            var brackets = new (string, string)[] {
                (@"\(", @"\)"), (@"\[", @"\]"), (@"\{", @"\}"), ("<", ">"),
                ("（", "）"), ("【", "】"), ("《", "》"),
            };
            foreach (var item in list)
            {
                if (string.IsNullOrEmpty(item.Title)) continue;
                string t = item.Title;
                foreach (var (open, close) in brackets)
                {
                    bool replaced;
                    do
                    {
                        replaced = false;
                        t = Regex.Replace(t, open + "(.*?)" + close, mm =>
                        {
                            string inner = mm.Groups[1].Value;
                            if (string.IsNullOrWhiteSpace(inner) || Regex.IsMatch(inner, domainPattern, RegexOptions.IgnoreCase))
                            { replaced = true; return ""; }
                            return mm.Value;
                        }, RegexOptions.IgnoreCase);
                    } while (replaced);
                }
                t = Regex.Replace(t, domainPattern, "", RegexOptions.IgnoreCase);
                foreach (string w in AdWords)
                    if (w.Length > 0) t = Regex.Replace(t, Regex.Escape(w), "", RegexOptions.IgnoreCase);
                t = Regex.Replace(t, @"\s+", " ").Trim();
                item.Title = t;
            }
        }

        // 2) 屏蔽词(标题过滤,与原程序一致,无开关)
        if (BlockedWords.Count > 0)
        {
            list = list.Where(item =>
            {
                if (string.IsNullOrEmpty(item.Title)) return true;
                string lower = item.Title.ToLower();
                return !BlockedWords.Any(w => w.Length > 0 && lower.Contains(w.ToLower()));
            }).ToList();
        }

        // 3) 精确匹配
        if (o.Precise && keyword.Length > 0)
            list = list.Where(item => item.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

        // 4) 智能滤词
        if (!o.NoIntel && IntelWords.Count > 0)
        {
            list = list.Where(item => string.IsNullOrEmpty(item.Title) ||
                !IntelWords.Any(w => w.Length > 0 && item.Title.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        }

        // 5) 自定义正则
        if (!string.IsNullOrEmpty(o.Regex))
        {
            try
            {
                var re = new Regex(o.Regex);
                list = list.Where(item => re.IsMatch(item.Title)).ToList();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"警告: 正则表达式错误,已忽略正则过滤: {ex.Message}");
            }
        }

        // 6) 最小大小
        if (o.MinSizeBytes > 0)
            list = list.Where(item => ParseSizeToBytes(item.Size) >= o.MinSizeBytes).ToList();

        // 7) 按磁链去重
        if (!o.NoDedupe)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            list = list.Where(item =>
            {
                if (string.IsNullOrEmpty(item.MagnetLink)) return true;
                return seen.Add(item.MagnetLink);
            }).ToList();
        }

        // 8) 本地举报哈希
        if (ReportedHashes.Count > 0 && !o.NoReport)
            list = list.Where(item => string.IsNullOrEmpty(item.MagnetLink) ||
                !ReportedHashes.Contains(ExtractHash(item.MagnetLink))).ToList();

        return list;
    }

    // ======================================================================
    // 排序
    // ======================================================================
    static List<SearchResultItem> SortResults(List<SearchResultItem> source, CliOptions o)
    {
        if (string.IsNullOrEmpty(o.Sort)) return source;
        var list = source.ToList();
        Comparison<SearchResultItem> cmp = o.Sort switch
        {
            "size" => (a, b) => CompareSize(a.Size, b.Size),
            "date" => (a, b) => string.Compare(a.UpdateTime ?? "", b.UpdateTime ?? "", StringComparison.Ordinal),
            "hot" => (a, b) => ComparePopularity(a.Popularity, b.Popularity),
            "title" => (a, b) => string.Compare(a.Title ?? "", b.Title ?? "", StringComparison.Ordinal),
            _ => (a, b) => 0,
        };
        list.Sort(cmp);
        if (o.Desc) list.Reverse();
        return list;
    }

    // ======================================================================
    // 输出
    // ======================================================================
    static void WriteTable(List<SearchResultItem> items, CliOptions o)
    {
        if (items.Count == 0) { Console.WriteLine("没有匹配结果。"); return; }

        // 非交互(管道/重定向):纯文本
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            int shown = 0;
            foreach (var it in items)
            {
                if (o.Top > 0 && shown >= o.Top) break;
                shown++;
                string torrent = o.ShowTorrent && TorrentUrl(it.MagnetLink).Length > 0
                    ? $"\n      种子: {TorrentUrl(it.MagnetLink)}" : "";
                Console.WriteLine($"{shown,3}. [{it.Library}] {it.Title}");
                Console.WriteLine($"      大小: {(it.Size.Length > 0 ? it.Size : "-"),-10}日期: {(it.UpdateTime.Length > 0 ? it.UpdateTime : "-"),-12}热度: {(it.Popularity.Length > 0 ? it.Popularity : "-")}");
                if (it.MagnetLink.Length > 0) Console.WriteLine($"      磁链: {it.MagnetLink}{torrent}");
                else if (it.DetailUrl.Length > 0) Console.WriteLine($"      详情: {it.DetailUrl}");
            }
            Console.WriteLine();
            Console.WriteLine($"共 {items.Count} 条结果" + (o.Top > 0 && shown < items.Count ? $",显示前 {shown} 条" : "") + "。");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]搜索结果[/]")
            .AddColumn(new TableColumn("#").RightAligned())
            .AddColumn(new TableColumn("库").NoWrap())
            .AddColumn(new TableColumn("大小").RightAligned())
            .AddColumn(new TableColumn("日期").NoWrap())
            .AddColumn(new TableColumn("热度").RightAligned())
            .AddColumn(new TableColumn("标题").Width(70))
            .AddColumn(new TableColumn("磁链 / 详情").NoWrap());
        int row = 0;
        foreach (var it in items)
        {
            if (o.Top > 0 && row >= o.Top) break;
            row++;
            string torrent = o.ShowTorrent && TorrentUrl(it.MagnetLink).Length > 0
                ? $"\n种子: {TorrentUrl(it.MagnetLink)}" : "";
            string link = it.MagnetLink.Length > 0 ? it.MagnetLink : it.DetailUrl;
            table.AddRow(
                row.ToString(),
                Markup.Escape(it.Library),
                Markup.Escape(it.Size.Length > 0 ? it.Size : "-"),
                Markup.Escape(it.UpdateTime.Length > 0 ? it.UpdateTime : "-"),
                Markup.Escape(it.Popularity.Length > 0 ? it.Popularity : "-"),
                Markup.Escape(it.Title),
                Markup.Escape(link.Length > 0 ? link + torrent : "-"));
        }
        AnsiConsole.Write(table);
        Console.WriteLine();
        AnsiConsole.MarkupLine(o.Top > 0 && row < items.Count
            ? $"[grey]共 [bold]{items.Count}[/] 条结果,显示前 {row} 条。[/]"
            : $"[green]共 [bold]{items.Count}[/] 条结果。[/]");
    }

    static void WriteJson(List<SearchResultItem> items)
    {
        var arr = items.Select(it => new
        {
            library = it.Library, title = it.Title, size = it.Size,
            updateTime = it.UpdateTime, popularity = it.Popularity,
            magnet = it.MagnetLink, detailUrl = it.DetailUrl,
            torrent = TorrentUrl(it.MagnetLink),
        }).ToList();
        Console.WriteLine(JsonSerializer.Serialize(arr, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    static void WriteMagnets(List<SearchResultItem> items, CliOptions o)
    {
        int shown = 0;
        foreach (var it in items)
        {
            if (o.Top > 0 && shown >= o.Top) break;
            if (it.MagnetLink.Length == 0) continue;
            shown++;
            Console.WriteLine(it.MagnetLink);
            if (o.ShowTorrent && TorrentUrl(it.MagnetLink).Length > 0)
                Console.WriteLine(TorrentUrl(it.MagnetLink));
        }
    }

    static void WriteCsv(List<SearchResultItem> items, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("大小,更新时间,热度,标题,磁链");   // 与原程序导出列一致
        foreach (var it in items)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                Csv(it.Size), Csv(it.UpdateTime), Csv(it.Popularity), Csv(it.Title), Csv(it.MagnetLink),
            }));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));   // 带 BOM,Excel 中文不乱码
        Console.Error.WriteLine($"已导出 CSV: {path} ({items.Count} 条)");
    }

    static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    // ======================================================================
    // HTTP
    // ======================================================================
    static HttpClient CreateHttpClient(CliOptions o)
    {
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseProxy = !string.IsNullOrEmpty(o.Proxy),
        };
        if (!string.IsNullOrEmpty(o.Proxy))
        {
            try { handler.Proxy = new WebProxy(o.Proxy); }
            catch (Exception ex) { Console.Error.WriteLine($"警告: 代理地址无效({ex.Message}),将直连。"); handler.UseProxy = false; }
        }
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(o.TimeoutSec) };
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        return client;
    }

    static async Task<string?> FetchHtmlAsync(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var u = new Uri(url);
        req.Headers.Referrer = new Uri(u.Scheme + "://" + u.Host + "/");
        using var resp = await _http!.SendAsync(req, HttpCompletionOption.ResponseContentRead);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    // ======================================================================
    // 本地缓存(data/btsou/,向上查找)
    // ======================================================================
    static string? FindCacheFile(string name)
    {
        string dir = Directory.GetCurrentDirectory();
        for (int i = 0; i < 6; i++)
        {
            string candidate = Path.Combine(dir, "data", "btsou", name);
            if (File.Exists(candidate)) return candidate;
            string parent = Directory.GetParent(dir)?.FullName ?? "";
            if (parent.Length == 0 || parent == dir) break;
            dir = parent;
        }
        return null;
    }

    static void TryWriteCache(string name, string content)
    {
        try
        {
            string? dir = FindCacheDir();
            if (dir != null)
                File.WriteAllText(Path.Combine(dir, name), content, new UTF8Encoding(false));
        }
        catch { /* 缓存写入失败不影响运行 */ }
    }

    // 定位 data/btsou/(从 cwd 向上找,最多 6 层)
    static string? FindCacheDir()
    {
        string dir = Directory.GetCurrentDirectory();
        for (int i = 0; i < 6; i++)
        {
            string candidate = Path.Combine(dir, "data", "btsou");
            if (Directory.Exists(candidate)) return candidate;
            string parent = Directory.GetParent(dir)?.FullName ?? "";
            if (parent.Length == 0 || parent == dir) break;
            dir = parent;
        }
        return null;
    }

    // 定位或创建 data/btsou/(优先已有目录,否则在 cwd 下创建)
    static string? FindOrCreateCacheDir()
    {
        string? existing = FindCacheDir();
        if (existing != null) return existing;
        try
        {
            string path = Path.Combine(Directory.GetCurrentDirectory(), "data", "btsou");
            Directory.CreateDirectory(path);
            return path;
        }
        catch { return null; }
    }
}
