#!/usr/bin/env dotnet run
#:package OfficeIMO.PowerPoint@3.6.2
#:package OfficeIMO.PowerPoint.Pdf@3.6.2
#:include Modules/DemoData.cs

// ============================================================================
// ppt.cs — 用 OfficeIMO.PowerPoint 生成一份 PPT（不需要安装 Office / COM）
//
// 覆盖的操作：
//   多页幻灯片 / 标题 / 项目符号 / 文本框 / 表格（表头着色、列宽比例）/ 原生图表 /
//   演讲者备注 / 版式内容区（GetContentBox）/ 导出 PDF
//
// 用法:
//   dotnet run src/office/ppt.cs -- [选项]
//
// 选项:
//   --out <file>     输出 .pptx 路径（默认 ./officeimo-ppt.pptx）
//   --data <file>    数据 CSV（默认内置示例；表头 Region,Revenue,Growth）
//   --title <text>   封面标题
//   --theme <name>   modern | classic | minimal | boardroom | dark（默认 modern）
//   --pdf            同时导出同名 PDF（依赖 OfficeIMO.PowerPoint.Pdf）
//   --help, -h       显示帮助
//   --version        显示版本
//
// 示例:
//   dotnet run src/office/ppt.cs -- --pdf
//   dotnet run src/office/ppt.cs -- --data sales.csv --title "Q3 经营分析" --theme dark
//
// 退出码: 0 成功 / 1 运行错误 / 2 参数错误
// ============================================================================

using System.Globalization;
using OfficeIMO;
using OfficeIMO.PowerPoint;
using OfficeIMO.PowerPoint.Pdf;

const string Version = "1.0.0";
const string Help = """
ppt — 用 OfficeIMO.PowerPoint 生成 PPT

用法:
  dotnet run src/office/ppt.cs -- [选项]

选项:
  --out <file>     输出 .pptx（默认 ./officeimo-ppt.pptx）
  --data <file>    数据 CSV，表头 Region,Revenue,Growth（默认内置示例）
  --title <text>   封面标题（默认「区域销售回顾」）
  --theme <name>   modern | classic | minimal | boardroom | dark（默认 modern）
  --pdf            同时导出 PDF
  --help, -h       显示帮助
  --version        显示版本

页面:
  1 封面   标题 + 副标题 + 日期
  2 要点   项目符号文本框 + 备注
  3 明细   表格（表头着色、列宽比例）
  4 图表   原生柱状图 + 结论说明
""";

var argv = args.ToList();
if (argv.Contains("--help") || argv.Contains("-h")) { Console.WriteLine(Help); return 0; }
if (argv.Contains("--version")) { Console.WriteLine($"ppt {Version}"); return 0; }

string outPath = Path.GetFullPath(GetOption(argv, "--out") ?? "officeimo-ppt.pptx");
string title = GetOption(argv, "--title") ?? "区域销售回顾";
bool exportPdf = argv.Contains("--pdf");

OfficeTheme theme;
try { theme = ParseTheme(GetOption(argv, "--theme")); }
catch (ArgumentException ex) { Console.Error.WriteLine($"[参数错误] {ex.Message}"); return 2; }

List<SalesRow> rows;
try
{
    rows = DemoData.Load(GetOption(argv, "--data"));
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[参数错误] {ex.Message}");
    return 2;
}

Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

decimal total = rows.Sum(r => r.Revenue);
double avgGrowth = rows.Average(r => r.Growth);
var best = rows.MaxBy(r => r.Revenue)!;
var fastest = rows.MaxBy(r => r.Growth)!;

try
{
    using var deck = PowerPointDocument.Create(outPath, new PowerPointCreateOptions { Theme = theme });
    deck.Properties.Title = title;
    deck.Properties.Creator = "script/src/office/ppt.cs";

    // 内容区：幻灯片尺寸减去四周留白，后续所有元素的坐标都基于它算，避免写死绝对值
    var content = deck.SlideSize.GetContentBox(OfficeLength.Centimeters(1.5));
    var half = content.TakeTop(OfficeLength.Centimeters(5));
    var body = content.TakeBottom(OfficeLength.Centimeters(11.8));   // 标题下方的正文区

    // ===================== 第 1 页：封面 =====================
    var cover = deck.AddSlide();
    cover.AddTitle(title, half);

    cover.AddTextBox(
            $"生成于 {DateTime.Now:yyyy-MM-dd} · 共 {rows.Count} 个区域",
            content.Left,
            OfficeLength.Centimeters(7.4),
            content.Width,
            OfficeLength.Centimeters(1.6))
         .SetColor(OfficeColor.TextMuted);

    cover.Notes.SetParagraphs(["开场：本页说明汇报范围与数据口径。"]);

    // ===================== 第 2 页：核心要点 =====================
    var points = deck.AddSlide();
    points.AddTitle("核心要点", half);

    var bullets = points.AddTextBox(
        string.Empty,
        body.Left,
        body.Top,
        body.Width,
        body.Height);

    bullets.AddBullets(
    [
        $"总营收 {total:N0} 元，平均同比 {FormatPercent(avgGrowth)}",
        $"{best.Region} 贡献最高，占整体 {(best.Revenue / total).ToString("0%", CultureInfo.InvariantCulture)}",
        $"{fastest.Region} 增速最快（{FormatPercent(fastest.Growth)}）",
        "建议把高增长区域的做法复制到低增长区域",
    ], level: 0, bulletChar: '•', configure: null);

    points.Notes.SetParagraphs(["逐条念要点，第 2 条可展开讲渠道结构。"]);

    // ===================== 第 3 页：明细表 =====================
    var tableSlide = deck.AddSlide();
    tableSlide.AddTitle("区域明细", half);

    var table = tableSlide.AddTable(rows.Count + 1, 4, body);
    table.SetColumnWidthsByRatio([26, 28, 22, 24]);

    string[] headers = ["区域", "营收（元）", "同比", "占比"];
    for (int c = 0; c < headers.Length; c++)
    {
        var cell = table.GetCell(0, c);
        cell.Text = headers[c];
        cell.Bold = true;
        cell.FillColor = OfficeColor.Accent1;
        cell.Color = OfficeColor.White;
    }

    for (int i = 0; i < rows.Count; i++)
    {
        var r = rows[i];
        table.GetCell(i + 1, 0).Text = r.Region;
        table.GetCell(i + 1, 1).Text = r.Revenue.ToString("N0", CultureInfo.InvariantCulture);
        table.GetCell(i + 1, 2).Text = FormatPercent(r.Growth);
        table.GetCell(i + 1, 3).Text = (r.Revenue / total).ToString("0.0%", CultureInfo.InvariantCulture);
    }

    tableSlide.Notes.SetParagraphs(["表格页：重点指出同比为负的区域。"]);

    // ===================== 第 4 页：图表 =====================
    var chartSlide = deck.AddSlide();
    chartSlide.AddTitle("营收结构", half);

    chartSlide.AddChart(
        OfficeChartType.Column,
        rows,
        r => r.Region,
        new PowerPointChartOptions
        {
            Title = "各区域营收（元）",
            Left = content.Left,
            Top = body.Top,
            Width = content.Width,
            Height = body.Height,
        },
        [new OfficeSeries("营收", rows.Select(r => (double)r.Revenue))]);

    chartSlide.Notes.SetParagraphs(["图表页：说明柱状图的排序与口径（含税 / 不含税）。"]);

    deck.Save();
    Console.WriteLine($"[完成] PPT : {outPath} ({new FileInfo(outPath).Length / 1024.0:0.0} KB)");
    Console.WriteLine($"[统计] 幻灯片 {deck.Slides.Count} 页 / 尺寸 {deck.SlideSize.Width} x {deck.SlideSize.Height}");

    if (exportPdf)
    {
        string pdfPath = Path.ChangeExtension(outPath, ".pdf");
        deck.SaveAsPdf(pdfPath);
        Console.WriteLine($"[完成] PDF : {pdfPath} ({new FileInfo(pdfPath).Length / 1024.0:0.0} KB)");
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[错误] 生成失败: {ex.Message}");
    return 1;
}

// ===================== 辅助 =====================

static string FormatPercent(double v) => v.ToString("+0.0%;-0.0%;0.0%", CultureInfo.InvariantCulture);

static string? GetOption(List<string> list, string key)
{
    int i = list.IndexOf(key);
    return i >= 0 && i + 1 < list.Count ? list[i + 1] : null;
}

static OfficeTheme ParseTheme(string? name) => name?.ToLowerInvariant() switch
{
    null or "modern" => OfficeTheme.Modern,
    "classic" => OfficeTheme.Classic,
    "minimal" => OfficeTheme.Minimal,
    "boardroom" => OfficeTheme.Boardroom,
    "dark" => OfficeTheme.Dark,
    _ => throw new ArgumentException($"未知主题: {name}（可选 modern|classic|minimal|boardroom|dark）"),
};
