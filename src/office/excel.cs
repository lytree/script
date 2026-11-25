#!/usr/bin/env dotnet run
#:package OfficeIMO.Excel@3.6.2
#:package OfficeIMO.Excel.Pdf@3.6.2
#:include Modules/DemoData.cs

// ============================================================================
// excel.cs — 用 OfficeIMO.Excel 生成一份 Excel 报表（不需要安装 Office / COM）
//
// 覆盖的操作：
//   多工作表 / 单元格写入与样式（加粗、填充、字体颜色、边框、对齐）/ 数字格式 /
//   跨表公式 / 冻结窗格 / 自动筛选 / 自动列宽 / 柱状图 / 工作表标签色 / 打印页眉 / 导出 PDF
//
// 用法:
//   dotnet run src/office/excel.cs -- [选项]
//
// 选项:
//   --out <file>     输出 .xlsx 路径（默认 ./officeimo-excel.xlsx）
//   --data <file>    数据 CSV（默认内置示例；表头 Region,Revenue,Growth）
//   --theme <name>   modern | classic | minimal | boardroom | dark（默认 modern）
//   --pdf            同时导出同名 PDF（依赖 OfficeIMO.Excel.Pdf）
//   --help, -h       显示帮助
//   --version        显示版本
//
// 示例:
//   dotnet run src/office/excel.cs -- --pdf
//   dotnet run src/office/excel.cs -- --data sales.csv --theme classic
//
// 退出码: 0 成功 / 1 运行错误 / 2 参数错误
// ============================================================================

using System.Globalization;
using OfficeIMO;
using OfficeIMO.Excel;
using OfficeIMO.Excel.Pdf;

const string Version = "1.0.0";
const string Help = """
excel — 用 OfficeIMO.Excel 生成 Excel 报表

用法:
  dotnet run src/office/excel.cs -- [选项]

选项:
  --out <file>     输出 .xlsx（默认 ./officeimo-excel.xlsx）
  --data <file>    数据 CSV，表头 Region,Revenue,Growth（默认内置示例）
  --theme <name>   modern | classic | minimal | boardroom | dark（默认 modern）
  --pdf            同时导出 PDF
  --help, -h       显示帮助
  --version        显示版本

工作表:
  销售明细  表头样式 + 数字格式 + 合计公式 + 冻结窗格 + 自动筛选 + 柱状图
  汇总      合并标题 + 跨表公式（SUM / AVERAGE / INDEX+MATCH）+ 打印页眉
""";

var argv = args.ToList();
if (argv.Contains("--help") || argv.Contains("-h")) { Console.WriteLine(Help); return 0; }
if (argv.Contains("--version")) { Console.WriteLine($"excel {Version}"); return 0; }

string outPath = Path.GetFullPath(GetOption(argv, "--out") ?? "officeimo-excel.xlsx");
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
int headerRow = 1;
int firstDataRow = 2;
int lastDataRow = firstDataRow + rows.Count - 1;
int totalRow = lastDataRow + 1;

try
{
    using var book = ExcelDocument.Create(outPath, new ExcelCreateOptions { Theme = theme });
    book.Properties.Title = "区域销售报表";
    book.Properties.Creator = "script/src/office/excel.cs";

    // ===================== 工作表 1：销售明细 =====================
    var sheet = book.AddSheet("销售明细");
    sheet.SetTabColor(OfficeColor.Accent1);

    // 表头：加粗 + 主题色填充 + 白字 + 居中 + 下边框
    var headers = new[] { "区域", "营收（元）", "同比", "占比" };
    for (int c = 1; c <= headers.Length; c++)
    {
        sheet.Cell(headerRow, c)
             .SetValue(headers[c - 1])
             .SetBold(true)
             .SetFillColor(OfficeColor.Accent1)
             .SetColor(OfficeColor.White)
             .SetHorizontalAlignment(ExcelHorizontalAlignment.Center);
    }

    // 数据行：每列各自的数字格式
    for (int i = 0; i < rows.Count; i++)
    {
        int r = firstDataRow + i;
        var row = rows[i];

        sheet.Cell(r, 1).SetValue(row.Region);
        sheet.Cell(r, 2).SetValue(row.Revenue).SetNumberFormat("#,##0");
        sheet.Cell(r, 3).SetValue(row.Growth).SetNumberFormat("0.0%;-0.0%;0.0%");
        sheet.Cell(r, 4).SetValue((double)(row.Revenue / total)).SetNumberFormat("0.0%");
    }

    // 合计行：公式由 Excel 计算，这里顺手算一遍缓存值，导出 PDF 时也能看到结果
    sheet.Cell(totalRow, 1).SetValue("合计").SetBold(true);
    sheet.Cell(totalRow, 2).SetFormula($"SUM(B{firstDataRow}:B{lastDataRow})").SetBold(true).SetNumberFormat("#,##0");
    sheet.Cell(totalRow, 3).SetFormula($"AVERAGE(C{firstDataRow}:C{lastDataRow})").SetBold(true).SetNumberFormat("0.0%");
    sheet.Cell(totalRow, 4).SetFormula($"SUM(D{firstDataRow}:D{lastDataRow})").SetBold(true).SetNumberFormat("0.0%");

    // 细边框 + 冻结首行首列 + 自动筛选（不含合计行）
    sheet.Range($"A{headerRow}:D{totalRow}").SetBorder(ExcelBorderStyle.Thin, OfficeColor.LightGray);
    sheet.View.Freeze(topRows: 1, leftColumns: 1);
    sheet.Range($"A{headerRow}:D{lastDataRow}").AutoFilter();

    // 原生柱状图（锚定在合计行下方）
    sheet.AddChart(
        OfficeChartType.Column,
        $"A{headerRow}:B{lastDataRow}",
        new ExcelChartOptions
        {
            Title = "各区域营收（元）",
            Row = totalRow + 2,
            Column = 1,
            Width = OfficeLength.Centimeters(16),
            Height = OfficeLength.Centimeters(8),
        });

    // ===================== 工作表 2：汇总 =====================
    var summary = book.AddSheet("汇总");
    summary.SetTabColor(OfficeColor.Accent2);

    summary.Cell(1, 1).SetValue("区域销售汇总").SetBold(true).SetFontSize(OfficeLength.Points(14));
    summary.Range("A1:B1").Merge();   // 合并标题行

    summary.Cell(2, 1).SetValue("指标").SetBold(true).SetFillColor(OfficeColor.LightGray);
    summary.Cell(2, 2).SetValue("值").SetBold(true).SetFillColor(OfficeColor.LightGray);

    // 跨工作表引用：SUM / AVERAGE / INDEX+MATCH
    summary.Cell(3, 1).SetValue("总营收");
    summary.Cell(3, 2).SetFormula($"SUM(销售明细!B{firstDataRow}:B{lastDataRow})").SetNumberFormat("#,##0");
    summary.Cell(4, 1).SetValue("平均同比");
    summary.Cell(4, 2).SetFormula($"AVERAGE(销售明细!C{firstDataRow}:C{lastDataRow})").SetNumberFormat("0.0%");
    summary.Cell(5, 1).SetValue("营收最高区域");
    summary.Cell(5, 2).SetFormula(
        $"INDEX(销售明细!A{firstDataRow}:A{lastDataRow},MATCH(MAX(销售明细!B{firstDataRow}:B{lastDataRow}),销售明细!B{firstDataRow}:B{lastDataRow},0))");
    summary.Cell(6, 1).SetValue("区域数量");
    summary.Cell(6, 2).SetValue(rows.Count);

    summary.Range("A2:B6").SetBorder(ExcelBorderStyle.Thin, OfficeColor.LightGray);

    // 打印设置：一页宽、居中、页眉
    summary.PageSetup.FitToWidth = 1;
    summary.PageSetup.PrintArea = "A1:B6";
    summary.PageSetup.HorizontalCentered = true;
    summary.PageSetup.Header.Center.Text = "ACME 销售汇总";

    // 先算公式、再自动列宽：否则公式单元格还没有缓存值，列宽会按空内容算，PDF 里会显示成 ##
    int calculated = book.Calculate();
    sheet.AutoFitColumns();
    summary.AutoFitColumns();
    sheet.SetColumnWidth(2, ExcelColumnWidth.Characters(14));   // 营收列再放宽一点

    book.Save();
    Console.WriteLine($"[完成] Excel: {outPath} ({new FileInfo(outPath).Length / 1024.0:0.0} KB)");
    Console.WriteLine($"[统计] 工作表 {book.Sheets.Count} 个 / 公式预计算 {calculated} 个单元格");

    if (exportPdf)
    {
        string pdfPath = Path.ChangeExtension(outPath, ".pdf");
        book.SaveAsPdf(pdfPath);
        Console.WriteLine($"[完成] PDF  : {pdfPath} ({new FileInfo(pdfPath).Length / 1024.0:0.0} KB)");
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[错误] 生成失败: {ex.Message}");
    return 1;
}

// ===================== 辅助 =====================

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
