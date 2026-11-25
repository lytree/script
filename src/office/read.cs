#!/usr/bin/env dotnet run
#:package OfficeIMO.Word@3.6.2
#:package OfficeIMO.Word.Pdf@3.6.2
#:package OfficeIMO.Excel@3.6.2
#:package OfficeIMO.Excel.Pdf@3.6.2
#:package OfficeIMO.PowerPoint@3.6.2
#:package OfficeIMO.PowerPoint.Pdf@3.6.2

// ============================================================================
// read.cs — 用 OfficeIMO 读取 Word / Excel / PowerPoint，导出文本、Markdown 或结构信息
//
// 场景：不需要装 Office，在命令行里把 .docx / .xlsx / .pptx 变成可 grep 的文本，
//       或者顺手转成 PDF（依赖对应的 OfficeIMO.*.Pdf 适配包）。
//
// 用法:
//   dotnet run src/office/read.cs -- <文件> [选项]
//
// 选项:
//   --mode <m>   text | markdown | info（默认 text）
//                  text     逐段 / 逐表 / 逐页输出纯文本
//                  markdown 保留标题层级与表格（适合喂给 AI 或写文档）
//                  info     只输出结构统计（页数、表数、图表数、文件大小）
//   --sheet <s>  Excel 只看指定工作表（可重复），默认全部
//   --out <file> 结果写入文件（默认输出到控制台）
//   --pdf        额外导出同名 PDF
//   --help, -h   显示帮助
//   --version    显示版本
//
// 示例:
//   dotnet run src/office/read.cs -- output/officeimo/word.docx --mode markdown
//   dotnet run src/office/read.cs -- report.xlsx --sheet 汇总 --mode text
//   dotnet run src/office/read.cs -- deck.pptx --pdf
//
// 退出码: 0 成功 / 1 读取失败 / 2 参数错误
// ============================================================================

using System.Globalization;
using System.Text;
using OfficeIMO.Word;
using OfficeIMO.Word.Pdf;
using OfficeIMO.Excel;
using OfficeIMO.Excel.Pdf;
using OfficeIMO.PowerPoint;
using OfficeIMO.PowerPoint.Pdf;

const string Version = "1.0.0";
const string Help = """
read — 用 OfficeIMO 读取 Office 文档

用法:
  dotnet run src/office/read.cs -- <文件> [选项]

选项:
  --mode <m>   text | markdown | info   (默认 text)
  --sheet <s>  Excel 只看指定工作表（可重复）
  --out <file> 结果写入文件
  --pdf        额外导出同名 PDF
  --help, -h   显示帮助
  --version    显示版本

示例:
  dotnet run src/office/read.cs -- word.docx --mode markdown
  dotnet run src/office/read.cs -- book.xlsx --sheet 汇总
  dotnet run src/office/read.cs -- deck.pptx --pdf
""";

var argv = args.ToList();
if (argv.Contains("--help") || argv.Contains("-h")) { Console.WriteLine(Help); return 0; }
if (argv.Contains("--version")) { Console.WriteLine($"read {Version}"); return 0; }

string? input = argv.FirstOrDefault(a => !a.StartsWith('-'));

string mode = (GetOption(argv, "--mode") ?? "text").ToLowerInvariant();
if (mode is not ("text" or "markdown" or "info"))
{
    Console.Error.WriteLine($"[参数错误] 未知模式: {mode}（可选 text|markdown|info）");
    return 2;
}
if (input is null)
{
    Console.Error.WriteLine("[参数错误] 缺少文件路径。用 --help 查看用法。");
    return 2;
}
if (!File.Exists(input))
{
    Console.Error.WriteLine($"[参数错误] 文件不存在: {input}");
    return 2;
}

string path = Path.GetFullPath(input);
string ext = Path.GetExtension(path).ToLowerInvariant();
var sheetFilter = GetOptions(argv, "--sheet");
string? outFile = GetOption(argv, "--out");
bool toPdf = argv.Contains("--pdf");

try
{
    var sb = new StringBuilder();
    string kind;

    switch (ext)
    {
        case ".docx" or ".doc":
            kind = "Word";
            ReadWord(path, mode, sb);
            break;
        case ".xlsx" or ".xls":
            kind = "Excel";
            ReadExcel(path, mode, sheetFilter, sb);
            break;
        case ".pptx" or ".ppt":
            kind = "PowerPoint";
            ReadPowerPoint(path, mode, sb);
            break;
        default:
            Console.Error.WriteLine($"[参数错误] 不支持的扩展名: {ext}（支持 .docx/.doc/.xlsx/.xls/.pptx/.ppt）");
            return 2;
    }

    if (outFile is not null)
    {
        File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"[完成] 已写入: {Path.GetFullPath(outFile)}");
    }
    else
    {
        Console.Write(sb.ToString());
    }

    if (toPdf)
    {
        string pdfPath = Path.ChangeExtension(path, ".pdf");
        ExportPdf(path, ext, pdfPath);
        Console.WriteLine($"[完成] PDF: {pdfPath} ({new FileInfo(pdfPath).Length / 1024.0:0.0} KB)");
    }

    Console.Error.WriteLine($"[信息] {kind} · {mode} · {new FileInfo(path).Length / 1024.0:0.0} KB");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[错误] 读取失败: {ex.Message}");
    return 1;
}

// ===================== Word =====================

static void ReadWord(string path, string mode, StringBuilder sb)
{
    using var doc = WordDocument.Load(path);

    if (mode == "info")
    {
        sb.AppendLine($"文件      : {Path.GetFileName(path)}");
        sb.AppendLine($"标题      : {doc.Properties.Title}");
        sb.AppendLine($"作者      : {doc.Properties.Creator}");
        sb.AppendLine($"节数      : {doc.Sections.Count}");
        sb.AppendLine($"段落      : {doc.Paragraphs.Count}");
        sb.AppendLine($"表格      : {doc.Tables.Count}");
        sb.AppendLine($"图片      : {doc.Images.Count}");
        sb.AppendLine($"图表      : {doc.Charts.Count}");
        sb.AppendLine($"超链接    : {doc.HyperLinks.Count}");
        sb.AppendLine($"页眉页脚  : {doc.Sections.Count(s => s.PageSetup.Header.Paragraphs.Count > 0 || s.PageSetup.Footer.Paragraphs.Count > 0)} 节有内容");
        return;
    }

    bool markdown = mode == "markdown";

    // 按文档顺序遍历元素（段落与表格交错），这样表格不会全部堆到末尾
    foreach (var element in doc.Elements)
    {
        switch (element)
        {
            case WordParagraph p:
                AppendParagraph(p, markdown, sb);
                break;
            case WordTable t:
                AppendTable(t, markdown, sb);
                break;
        }
    }
}

static void AppendParagraph(WordParagraph p, bool markdown, StringBuilder sb)
{
    string text = p.Text?.Trim() ?? string.Empty;
    if (text.Length == 0) return;

    if (!markdown)
    {
        sb.AppendLine(text);
        return;
    }

    sb.AppendLine(p.Style switch
    {
        WordParagraphStyles.Title => $"# {text}",
        WordParagraphStyles.Heading1 => $"# {text}",
        WordParagraphStyles.Heading2 => $"## {text}",
        WordParagraphStyles.Heading3 => $"### {text}",
        WordParagraphStyles.ListParagraph => $"- {text}",
        _ => text,
    });
    sb.AppendLine();
}

static void AppendTable(WordTable table, bool markdown, StringBuilder sb)
{
    sb.AppendLine();
    var rows = table.Rows.Select(r => r.Cells.Select(CellText).ToArray()).ToList();
    if (rows.Count == 0) return;

    if (!markdown)
    {
        sb.AppendLine("---- 表格 ----");
        foreach (var r in rows) sb.AppendLine(string.Join("\t", r));
        sb.AppendLine();
        return;
    }

    sb.AppendLine("| " + string.Join(" | ", rows[0]) + " |");
    sb.AppendLine("|" + string.Concat(rows[0].Select(_ => " --- |")));
    foreach (var r in rows.Skip(1))
        sb.AppendLine("| " + string.Join(" | ", r) + " |");
    sb.AppendLine();
}

static string CellText(WordTableCell cell) =>
    string.Join(" ", cell.Paragraphs.Select(p => p.Text?.Trim()).Where(t => !string.IsNullOrEmpty(t))).Replace("|", "\\|");

// ===================== Excel =====================

static void ReadExcel(string path, string mode, List<string> sheetFilter, StringBuilder sb)
{
    using var book = ExcelDocument.Load(path);

    if (mode == "info")
    {
        sb.AppendLine($"文件      : {Path.GetFileName(path)}");
        sb.AppendLine($"标题      : {book.Properties.Title}");
        sb.AppendLine($"工作表    : {book.Sheets.Count}");
        foreach (var s in book.Sheets)
        {
            var range = s.UsedRange;
            sb.AppendLine($"  - {s.Name,-16} 已用区域 {range.FirstRow}..{range.LastRow} 行 / {range.FirstColumn}..{range.LastColumn} 列");
        }
        return;
    }

    bool markdown = mode == "markdown";

    foreach (var sheet in book.Sheets)
    {
        if (sheetFilter.Count > 0 && !sheetFilter.Contains(sheet.Name, StringComparer.OrdinalIgnoreCase)) continue;

        var range = sheet.UsedRange;
        if (range.LastRow < range.FirstRow) continue;

        sb.AppendLine(markdown ? $"## {sheet.Name}" : $"===== 工作表: {sheet.Name} =====");
        sb.AppendLine();

        var rows = new List<string[]>();
        for (int r = range.FirstRow; r <= range.LastRow; r++)
        {
            var cells = new string[range.LastColumn - range.FirstColumn + 1];
            for (int c = range.FirstColumn; c <= range.LastColumn; c++)
                cells[c - range.FirstColumn] = sheet.Cell(r, c).GetFormattedText(CultureInfo.InvariantCulture) ?? string.Empty;
            rows.Add(cells);
        }

        if (markdown)
        {
            sb.AppendLine("| " + string.Join(" | ", rows[0].Select(Escape)) + " |");
            sb.AppendLine("|" + string.Concat(rows[0].Select(_ => " --- |")));
            foreach (var r in rows.Skip(1))
                sb.AppendLine("| " + string.Join(" | ", r.Select(Escape)) + " |");
            sb.AppendLine();
        }
        else
        {
            foreach (var r in rows) sb.AppendLine(string.Join("\t", r));
            sb.AppendLine();
        }
    }

    static string Escape(string s) => s.Replace("|", "\\|");
}

// ===================== PowerPoint =====================

static void ReadPowerPoint(string path, string mode, StringBuilder sb)
{
    using var deck = PowerPointDocument.Load(path, new PowerPointLoadOptions());

    if (mode == "info")
    {
        sb.AppendLine($"文件      : {Path.GetFileName(path)}");
        sb.AppendLine($"标题      : {deck.Properties.Title}");
        sb.AppendLine($"幻灯片    : {deck.Slides.Count}");
        sb.AppendLine($"尺寸      : {deck.SlideSize.Width} x {deck.SlideSize.Height} ({deck.SlideSize.AspectRatio:0.00})");
        foreach (var slide in deck.Slides)
            sb.AppendLine($"  - 第 {slide.Index + 1} 页：文本框 {slide.TextBoxes.Count()} / 表格 {slide.Tables.Count()} / 图表 {slide.Charts.Count()} / 备注 {(slide.HasSpeakerNotes ? "有" : "无")}");
        return;
    }

    bool markdown = mode == "markdown";

    foreach (var slide in deck.Slides)
    {
        sb.AppendLine(markdown ? $"## 第 {slide.Index + 1} 页" : $"===== 第 {slide.Index + 1} 页 =====");
        sb.AppendLine();

        foreach (var box in slide.TextBoxes)
        {
            var text = box.Text?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                sb.AppendLine(markdown ? line : line);
            sb.AppendLine();
        }

        foreach (var table in slide.Tables)
        {
            if (!markdown)
            {
                sb.AppendLine("---- 表格 ----");
                foreach (var row in table.Rows)
                    sb.AppendLine(string.Join("\t", row.Cells.Select(c => c.Text)));
                sb.AppendLine();
                continue;
            }

            for (int r = 0; r < table.RowCount; r++)
            {
                var cells = Enumerable.Range(0, table.ColumnCount).Select(c => table.GetCell(r, c).Text?.Replace("|", "\\|") ?? "");
                if (r == 0)
                {
                    sb.AppendLine("| " + string.Join(" | ", cells) + " |");
                    sb.AppendLine("|" + string.Concat(Enumerable.Range(0, table.ColumnCount).Select(_ => " --- |")));
                }
                else
                {
                    sb.AppendLine("| " + string.Join(" | ", cells) + " |");
                }
            }
            sb.AppendLine();
        }

        if (slide.HasSpeakerNotes)
        {
            sb.AppendLine(markdown ? "> 备注：" + slide.Notes.Text : "备注: " + slide.Notes.Text);
            sb.AppendLine();
        }
    }
}

// ===================== PDF =====================

static void ExportPdf(string path, string ext, string pdfPath)
{
    switch (ext)
    {
        case ".docx" or ".doc":
            using (var doc = WordDocument.Load(path)) doc.SaveAsPdf(pdfPath);
            break;
        case ".xlsx" or ".xls":
            using (var book = ExcelDocument.Load(path)) book.SaveAsPdf(pdfPath);
            break;
        case ".pptx" or ".ppt":
            using (var deck = PowerPointDocument.Load(path, new PowerPointLoadOptions())) deck.SaveAsPdf(pdfPath);
            break;
    }
}

// ===================== 辅助 =====================

static string? GetOption(List<string> list, string key)
{
    int i = list.IndexOf(key);
    return i >= 0 && i + 1 < list.Count ? list[i + 1] : null;
}

static List<string> GetOptions(List<string> list, string key)
{
    var values = new List<string>();
    for (int i = 0; i < list.Count - 1; i++)
        if (list[i] == key) values.Add(list[i + 1]);
    return values;
}
