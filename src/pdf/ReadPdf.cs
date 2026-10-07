#!/usr/bin/env dotnet run

#:package PdfPig@*

// ============================================================
// ReadPdf.cs — 用 PdfPig 直接读取 PDF，提取文字与版式（坐标 / 文本块）
//
// 用法:
//   dotnet run ReadPdf.cs -- <pdf路径> [--mode text|words|letters|layout] [--page N] [--password P] [--out 文件] [--help] [--version]
//
// 参数:
//   <pdf路径>       必填，PDF 文件路径
//   --mode          提取模式，默认 text
//                     text    整页纯文本（不保证阅读顺序）
//                     words   按「词」提取 + 包围盒(x,y,w,h) 与字号
//                     letters 字母级坐标（最细粒度，适合字段/印章定位）
//                     layout  版面分析：按阅读顺序输出文本块
//   --page N        只处理第 N 页（1 基），不指定则处理全部页
//   --password P    加密 PDF 的密码（可选）
//   --out 文件      结果写入文件（默认输出到控制台）
//   --help  / -h    显示帮助
//   --version       显示版本
//
// 退出码: 0 成功 / 1 一般错误 / 2 参数错误
//
// 坐标说明: PDF 用户空间原点在页面左下角，y 轴向上，单位 point（1pt = 1/72 英寸）。
//           在 WPF/WinForms 里画高亮框时，需用「页面高度 - PDF的Y」翻转为屏幕坐标。
// ============================================================

using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;

const string Version = "1.0.0";
const string Help = """
ReadPdf — 用 PdfPig 读取 PDF，提取文字与版式

用法:
  dotnet run ReadPdf.cs -- <pdf路径> [选项]

选项:
  --mode <m>     text | words | letters | layout   (默认 text)
  --page <N>     只处理第 N 页（1 基）
  --password <p> 加密 PDF 的密码
  --out <file>   结果写入文件而非控制台
  --help, -h     显示本帮助
  --version      显示版本

坐标体系: 原点在页面左下角，y 向上，单位 pt(1/72 英寸)。
""";

// ---- 解析命令行参数 ----
var argsList = args.ToList();
if (argsList.Contains("--help") || argsList.Contains("-h"))
{
    Console.WriteLine(Help);
    return;
}
if (argsList.Contains("--version"))
{
    Console.WriteLine($"ReadPdf {Version}");
    return;
}

string? pdfPath = argsList.FirstOrDefault(a => !a.StartsWith("-"));
if (pdfPath is null)
{
    Console.Error.WriteLine("[参数错误] 缺少 PDF 文件路径。用 --help 查看用法。");
    Environment.Exit(2);
}
if (!File.Exists(pdfPath))
{
    Console.Error.WriteLine($"[参数错误] 文件不存在: {pdfPath}");
    Environment.Exit(2);
}

string mode = GetOption(argsList, "--mode") ?? "text";
string? outPath = GetOption(argsList, "--out");
string? password = GetOption(argsList, "--password");
int? pageNum = GetOption(argsList, "--page") is { } p && int.TryParse(p, out var n) ? n : null;

// ---- 读取 PDF ----
var options = new ParsingOptions();
if (password is not null) options.Password = password;

var sb = new StringBuilder();
try
{
    using var doc = PdfDocument.Open(pdfPath, options);
    int start = pageNum ?? 1;
    int end = pageNum ?? doc.NumberOfPages;

    if (pageNum is null)
        Console.Error.WriteLine($"[信息] 共 {doc.NumberOfPages} 页，正在处理全部页…");

    for (int i = start; i <= end; i++)
    {
        var page = doc.GetPage(i);
        sb.AppendLine($"===== 第 {i} 页 (宽 {page.Width:0.0} x 高 {page.Height:0.0} pt) =====");
        switch (mode)
        {
            case "text":    ExtractText(page, sb); break;
            case "words":   ExtractWords(page, sb); break;
            case "letters": ExtractLetters(page, sb); break;
            case "layout":  ExtractLayout(page, sb); break;
            default:
                Console.Error.WriteLine($"[参数错误] 未知模式: {mode}（可选 text|words|letters|layout）");
                Environment.Exit(2);
                break;
        }
        sb.AppendLine();
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[错误] 读取失败: {ex.Message}");
    Environment.Exit(1);
}

// ---- 输出 ----
string result = sb.ToString();
if (outPath is not null)
{
    File.WriteAllText(outPath, result);
    Console.WriteLine($"[完成] 已写入: {outPath}");
}
else
{
    Console.Write(result);
}

// ===================== 提取函数 =====================

// 整页纯文本
void ExtractText(Page page, StringBuilder writer)
{
    writer.AppendLine(page.Text);
}

// 按「词」提取 + 包围盒与字号（坐标来自 PDF 用户空间）
void ExtractWords(Page page, StringBuilder writer)
{
    foreach (var word in page.GetWords())
    {
        var b = word.BoundingBox;
        writer.AppendLine(
            $"词={word.Text}\t" +
            $"x={b.BottomLeft.X:0.0} y={b.BottomLeft.Y:0.0}\t" +
            $"w={b.Width:0.0} h={b.Height:0.0}\t" +
            $"字号≈{b.Height:0.0}pt");
    }
}

// 字母级坐标（最细粒度）
void ExtractLetters(Page page, StringBuilder writer)
{
    foreach (var letter in page.Letters)
    {
        writer.AppendLine(
            $"{letter.Value}\t" +
            $"x={letter.StartX:0.0} y={letter.StartY:0.0}\t" +
            $"w={letter.GlyphRectangle.Width:0.0} h={letter.GlyphRectangle.Height:0.0}");
    }
}

// 版面分析：字母→词→文本块→阅读顺序
void ExtractLayout(Page page, StringBuilder writer)
{
    var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);
    var blocks = RecursiveXYCut.Instance.GetBlocks(words);
    var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks);
    foreach (var block in ordered)
    {
        writer.AppendLine($"[块 {block.ReadingOrder}] {block.Text}");
    }
}

// ===================== 辅助 =====================
string? GetOption(List<string> list, string key)
{
    int idx = list.IndexOf(key);
    return idx >= 0 && idx + 1 < list.Count ? list[idx + 1] : null;
}
