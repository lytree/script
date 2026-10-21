using System.Drawing;
using System.Text;
using MiniExcelLibs;
using MiniExcelLibs.Attributes;
using MiniExcelLibs.OpenXml;

// ─────────────────────────────────────────────────────────────
// 本文件移植自 chunsou（Python）: https://github.com/Funsiooo/chunsou
// 对应原项目文件：modules/core/output.py
// Copyright (C) Funsiooo    Licensed under GPL-3.0（见 LICENSE）
//
// 移植改动：xlsx 输出由 ClosedXML 换为 MiniExcel（流式写出，内存占用从 GB 级降到 MB 级）。
// ─────────────────────────────────────────────────────────────

namespace Chunsou;

/// <summary>xlsx 行模型：常规扫描（对应 Python 的 5 列表头）</summary>
public sealed class ScanXlsxRow
{
    [ExcelColumn(Name = "状态码")] public int StatusCode { get; set; }
    [ExcelColumn(Name = "网页URL")] public string? Url { get; set; }
    [ExcelColumn(Name = "网页标题")] public string? Title { get; set; }
    [ExcelColumn(Name = "网站技术栈")] public string? Stack { get; set; }
    [ExcelColumn(Name = "指纹结果")] public string? Fingerprint { get; set; }
}

/// <summary>xlsx 行模型：AI force 模式（2 列）</summary>
public sealed class AiXlsxRow
{
    [ExcelColumn(Name = "网页URL")] public string? Url { get; set; }
    [ExcelColumn(Name = "AI分析结果")] public string? Analysis { get; set; }
}

/// <summary>FOFA 结果行（对应 fofa.py 的 8 列表头）</summary>
public sealed class FofaXlsxRow
{
    [ExcelColumn(Name = "Host")] public string? Host { get; set; }
    [ExcelColumn(Name = "网页标题")] public string? Title { get; set; }
    [ExcelColumn(Name = "域名")] public string? Domain { get; set; }
    [ExcelColumn(Name = "Link")] public string? Link { get; set; }
    [ExcelColumn(Name = "IP")] public string? Ip { get; set; }
    [ExcelColumn(Name = "Port")] public string? Port { get; set; }
    [ExcelColumn(Name = "Protocol")] public string? Protocol { get; set; }
    [ExcelColumn(Name = "Server")] public string? Server { get; set; }
}

/// <summary>Hunter 结果行（对应 hunter.py 的 15 列表头）</summary>
public sealed class HunterXlsxRow
{
    [ExcelColumn(Name = "网址")] public string? Url { get; set; }
    [ExcelColumn(Name = "IP")] public string? Ip { get; set; }
    [ExcelColumn(Name = "端口")] public string? Port { get; set; }
    [ExcelColumn(Name = "网站标题")] public string? WebTitle { get; set; }
    [ExcelColumn(Name = "域名")] public string? Domain { get; set; }
    [ExcelColumn(Name = "状态码")] public string? StatusCode { get; set; }
    [ExcelColumn(Name = "协议名称")] public string? Protocol { get; set; }
    [ExcelColumn(Name = "基础协议")] public string? BaseProtocol { get; set; }
    [ExcelColumn(Name = "系统名称")] public string? Os { get; set; }
    [ExcelColumn(Name = "公司名称")] public string? Company { get; set; }
    [ExcelColumn(Name = "备案号")] public string? Number { get; set; }
    [ExcelColumn(Name = "国家")] public string? Country { get; set; }
    [ExcelColumn(Name = "省份")] public string? City { get; set; }
    [ExcelColumn(Name = "应用名称")] public string? AppName { get; set; }
    [ExcelColumn(Name = "应用版本")] public string? AppVersion { get; set; }
}

/// <summary>
/// 结果输出，对应 modules/core/output.py（txt / xlsx 双格式）。
/// </summary>
public static class ResultWriter
{
    public static string ResultsDir => Path.Combine(Config.RootDir, "results");

    /// <summary>解析输出路径：-o 指定优先，否则用默认文件名</summary>
    public static string ResolveOutput(string? output, string defaultFile)
    {
        var path = string.IsNullOrWhiteSpace(output)
            ? Path.Combine(ResultsDir, defaultFile)
            : output;

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".xlsx" or ".txt" => path,
            _ => throw new ArgumentException($"不支持的输出格式：{Path.GetExtension(path)}（仅支持 .txt / .xlsx）"),
        };
    }

    // ── 控制台格式化（对齐 Python 的 _format_* 系列）──

    /// <summary>指纹结果文本（带颜色，供控制台使用）</summary>
    public static string FingerText(ScanResult r) => FingerForFile(r);

    /// <summary>指纹结果文本（纯文本，供文件使用）</summary>
    public static string FingerForFile(ScanResult r) =>
        r.AiMode == "auto" && !string.IsNullOrEmpty(r.AiFingerprint)
            ? $"AI识别结果：{r.AiFingerprint}"
            : Display(r.DetectedCms);

    public static string ForceAnalysisText(ScanResult r) =>
        $"AI分析结果：{r.AiAnalysis ?? "当前页面特征不足，建议人工复核"}";

    public static string SuccessLine(ScanResult r) => r.IsForceMode
        ? $"{Ansi.C(Ansi.BrightWhite, r.Url)} {Ansi.C(Ansi.White, "|")} {Ansi.C(Ansi.YellowB, ForceAnalysisText(r))}"
        : $"{Ansi.C(Ansi.BrightWhite, r.Url)} {Ansi.C(Ansi.White, "|")} {Ansi.C(Ansi.YellowB, FingerText(r))} " +
          $"{Ansi.C(Ansi.White, "|")} {Ansi.C(Ansi.YellowB, Display(r.Title))} " +
          $"{Ansi.C(Ansi.White, "|")} {Ansi.C(Ansi.YellowB, Display(r.Stack))}";

    public static string ErrorLine(ScanResult r) => $"{r.Url} [{r.Error}]";

    public static string StatusLine(ScanResult r) => r.IsForceMode
        ? $"{r.Url} | {ForceAnalysisText(r)}"
        : $"[+] [{r.StatusCode}] {r.Url} | {FingerForFile(r)} | {Display(r.Title)} | {Display(r.Stack)}";

    public static string ErrorFileLine(ScanResult r) => $"[-] [{r.StatusCode}] {r.Url} [{r.Error}]";

    public static string Display(string? v) => string.IsNullOrEmpty(v) ? "None" : v;

    /// <summary>构造扫描结果的 xlsx 行。失败目标沿用 5 列结构，错误写入末列（对齐 Python 版）。</summary>
    public static ScanXlsxRow ToXlsxRow(ScanResult r) => new()
    {
        StatusCode = r.StatusCode,
        Url = r.Url,
        Title = r.HasError ? " " : Display(r.Title),
        Stack = r.HasError ? " " : Display(r.Stack),
        Fingerprint = r.HasError ? r.Error : FingerForFile(r),
    };

    /// <summary>构造 AI force 模式的 xlsx 行</summary>
    public static AiXlsxRow ToAiXlsxRow(ScanResult r) => new()
    {
        Url = r.Url,
        Analysis = ForceAnalysisText(r),
    };

    // ── 落盘 ──

    public static void EnsureDir(string filePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    /// <summary>追加写入 txt 结果（多目标模式用，边扫边写）</summary>
    public static void AppendText(string path, IEnumerable<string> lines)
    {
        EnsureDir(path);
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var sw = new StreamWriter(fs, new UTF8Encoding(false));
        foreach (var line in lines) sw.WriteLine(line);
    }

    public static void WriteText(string path, IEnumerable<string> lines)
    {
        EnsureDir(path);
        File.WriteAllText(path, string.Join(Environment.NewLine, lines), new UTF8Encoding(false));
    }

    /// <summary>
    /// xlsx 通用写出配置：
    /// FastMode 必须开启（AutoWidth 依赖它），表头灰底居中，首行冻结，列宽自适应。
    /// </summary>
    private static OpenXmlConfiguration XlsxConfig() => new()
    {
        FastMode = true,
        EnableAutoWidth = true,
        MinWidth = 8,
        MaxWidth = 60,
        FreezeRowCount = 1,
        StyleOptions = new OpenXmlStyleOptions
        {
            HeaderStyle = new OpenXmlHeaderStyle
            {
                BackgroundColor = Color.FromArgb(0xC0, 0xC0, 0xC0),
                HorizontalAlignment = HorizontalCellAlignment.Center,
            },
        },
    };

    /// <summary>写出 xlsx。行模型上的 [ExcelColumn] 决定表头与列序。</summary>
    public static void WriteXlsx<T>(string path, IEnumerable<T> rows) where T : class
    {
        EnsureDir(path);

        var list = rows as IList<T> ?? rows.ToList();
        if (list.Count == 0) return;

        // overwriteFile: MiniExcel 默认拒绝覆盖已存在文件
        MiniExcel.SaveAs(path, list, configuration: XlsxConfig(), overwriteFile: true);
    }

    /// <summary>空数据时也产出一个只有表头的 xlsx</summary>
    public static void WriteEmptyXlsx<T>(string path) where T : class
    {
        EnsureDir(path);
        MiniExcel.SaveAs(path, new List<T>(), configuration: XlsxConfig(), overwriteFile: true);
    }
}
