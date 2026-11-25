#!/usr/bin/env dotnet run
#:package OfficeIMO.Word@3.6.2
#:package OfficeIMO.Word.Pdf@3.6.2
#:include Modules/DemoData.cs

// ============================================================================
// word.cs — 用 OfficeIMO.Word 生成一份 Word 报告（不需要安装 Office / COM）
//
// 覆盖的操作：
//   标题 / 正文 / 列表 / 表格 / 内嵌图表 / 页眉 / 页脚 + 页码 / 文档属性 / 导出 PDF
//
// 用法:
//   dotnet run src/office/word.cs -- [选项]
//
// 选项:
//   --out <file>     输出 .docx 路径（默认 ./officeimo-word.docx）
//   --data <file>    数据 CSV（默认内置示例；表头 Region,Revenue,Growth）
//   --title <text>   报告标题
//   --theme <name>   modern | classic | minimal | boardroom | dark（默认 modern）
//   --pdf            同时导出同名 PDF（依赖 OfficeIMO.Word.Pdf）
//   --help, -h       显示帮助
//   --version        显示版本
//
// 示例:
//   dotnet run src/office/word.cs -- --pdf
//   dotnet run src/office/word.cs -- --data sales.csv --title "Q3 销售报告" --theme boardroom --pdf
//
// 退出码: 0 成功 / 1 运行错误 / 2 参数错误
// ============================================================================

using System.Globalization;
using OfficeIMO;
using OfficeIMO.Word;
using OfficeIMO.Word.Pdf;

const string Version = "1.0.0";
const string Help = """
word — 用 OfficeIMO.Word 生成 Word 报告

用法:
  dotnet run src/office/word.cs -- [选项]

选项:
  --out <file>     输出 .docx（默认 ./officeimo-word.docx）
  --data <file>    数据 CSV，表头 Region,Revenue,Growth（默认内置示例）
  --title <text>   报告标题（默认「区域销售报告」）
  --theme <name>   modern | classic | minimal | boardroom | dark（默认 modern）
  --pdf            同时导出 PDF
  --help, -h       显示帮助
  --version        显示版本

示例:
  dotnet run src/office/word.cs -- --pdf
  dotnet run src/office/word.cs -- --data sales.csv --title "Q3 销售报告" --theme boardroom --pdf
""";

var argv = args.ToList();
if (argv.Contains("--help") || argv.Contains("-h")) { Console.WriteLine(Help); return 0; }
if (argv.Contains("--version")) { Console.WriteLine($"word {Version}"); return 0; }

string outPath = Path.GetFullPath(GetOption(argv, "--out") ?? "officeimo-word.docx");
string title = GetOption(argv, "--title") ?? "区域销售报告";
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
var worst = rows.MinBy(r => r.Growth)!;

try
{
    using var doc = WordDocument.Create(outPath, new WordCreateOptions { Theme = theme });

    // ---- 文档属性（文件右键「详细信息」里可见）----
    doc.Properties.Title = title;
    doc.Properties.Subject = "OfficeIMO.Word 示例报告";
    doc.Properties.Creator = "script/src/office/word.cs";
    doc.Properties.Keywords = "OfficeIMO, Word, demo";

    // ---- 封面区：标题 + 副标题 + 分隔线 ----
    doc.AddHeading(title, level: 1);
    doc.AddParagraph($"生成时间 {DateTime.Now:yyyy-MM-dd HH:mm} · 数据来源 {(GetOption(argv, "--data") is { } csv ? Path.GetFileName(csv) : "内置示例")}")
       .SetItalic(true)
       .SetFontSize(OfficeLength.Points(9))
       .SetColor(OfficeColor.TextMuted);
    doc.AddHorizontalLine();

    // ---- 摘要 ----
    doc.AddHeading("摘要", level: 2);
    doc.AddParagraph(
        $"本期共覆盖 {rows.Count} 个区域，合计营收 {total:N0} 元，平均同比 {FormatPercent(avgGrowth)}。" +
        $"其中 {best.Region} 营收最高（{best.Revenue:N0} 元），{worst.Region} 同比降幅最大（{FormatPercent(worst.Growth)}）。");

    // ---- 关键指标：项目符号列表 ----
    doc.AddHeading("关键指标", level: 2);
    var bullets = doc.AddList(WordListStyle.Bulleted);
    bullets.AddItem($"总营收：{total:N0} 元", 0);
    bullets.AddItem($"平均同比：{FormatPercent(avgGrowth)}", 0);
    bullets.AddItem($"营收冠军：{best.Region}（{best.Revenue:N0} 元，占比 {(best.Revenue / total).ToString("0.0%", CultureInfo.InvariantCulture)}）", 0);
    bullets.AddItem($"增速最快：{rows.MaxBy(r => r.Growth)!.Region}（{FormatPercent(rows.Max(r => r.Growth))}）", 0);

    // ---- 明细表：表头加粗、首行跨页重复、按比例分配列宽 ----
    doc.AddHeading("区域明细", level: 2);
    var table = doc.AddTable(rows.Count + 1, 4, WordTableStyle.OfficeIMOModern);
    var headers = new[] { "区域", "营收（元）", "同比", "占比" };
    for (int c = 0; c < headers.Length; c++) table.SetCellText(0, c, headers[c], bold: true);

    for (int i = 0; i < rows.Count; i++)
    {
        var r = rows[i];
        table.SetCellText(i + 1, 0, r.Region);
        table.SetCellText(i + 1, 1, r.Revenue.ToString("N0", CultureInfo.InvariantCulture));
        table.SetCellText(i + 1, 2, FormatPercent(r.Growth));
        table.SetCellText(i + 1, 3, (r.Revenue / total).ToString("0.0%", CultureInfo.InvariantCulture));
    }

    table.RepeatHeaderRowAtTheTopOfEachPage = true;
    table.SetColumnWidthsPercentage([22, 28, 25, 25]);

    // ---- 内嵌图表（Word 里的原生 DrawingML 图表，不是图片）----
    doc.AddHeading("营收对比", level: 2);
    doc.AddChart(
        OfficeChartType.Column,
        rows,
        r => r.Region,
        new WordChartOptions
        {
            Title = "各区域营收（元）",
            Width = OfficeLength.Centimeters(16),
            Height = OfficeLength.Centimeters(8),
        },
        [new OfficeSeries("营收", rows.Select(r => (double)r.Revenue))]);

    // ---- 结论：编号列表 ----
    doc.AddHeading("结论与建议", level: 2);
    var conclusions = doc.AddList(WordListStyle.Numbered);
    conclusions.AddItem($"{best.Region} 贡献了 {(best.Revenue / total).ToString("0%", CultureInfo.InvariantCulture)} 的营收，建议优先保障其库存与交付。", 0);
    conclusions.AddItem($"{worst.Region} 同比 {FormatPercent(worst.Growth)}，需要复盘渠道与定价策略。", 0);
    conclusions.AddItem($"整体平均增速 {FormatPercent(avgGrowth)}，可把高增长区域的打法复制到低增长区域。", 0);

    // ---- 页眉 / 页脚 / 页码 ----
    doc.PageSetup.Header
       .AddParagraph("ACME · 内部资料")
       .SetAlignment(WordParagraphAlignment.Right)
       .SetFontSize(OfficeLength.Points(9))
       .SetColor(OfficeColor.TextMuted);

    doc.PageSetup.Footer
       .AddParagraph(title)
       .SetAlignment(WordParagraphAlignment.Left)
       .SetFontSize(OfficeLength.Points(9))
       .SetColor(OfficeColor.TextMuted);

    // 「第 1 / 3 页」由 Word 域生成，打开或导出 PDF 时由渲染器计算
    doc.PageSetup.Footer.AddPageNumber(includeTotalPages: true, format: null, separator: " / ")
       .SetAlignment(WordParagraphAlignment.Right);

    doc.Save();
    Console.WriteLine($"[完成] Word: {outPath} ({new FileInfo(outPath).Length / 1024.0:0.0} KB)");

    if (exportPdf)
    {
        string pdfPath = Path.ChangeExtension(outPath, ".pdf");
        doc.SaveAsPdf(pdfPath);
        Console.WriteLine($"[完成] PDF : {pdfPath} ({new FileInfo(pdfPath).Length / 1024.0:0.0} KB)");
    }

    Console.WriteLine($"[统计] 段落 {doc.Paragraphs.Count} / 表格 {doc.Tables.Count} / 图表 {doc.Charts.Count}");
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
