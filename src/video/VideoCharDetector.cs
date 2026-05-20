

// #:property TargetFramework=net10.0-windows10.0.22621.0
#:include ../../env.cs


#:package OpenCvSharp4@4.13.0.20260427
#:package OpenCvSharp4.runtime.win@4.13.0.20260302
#:package OpenCvSharp4.Extensions@4.13.0.20260427
#:package RapidOCRSharpOnnx@1.0.7
#:package Microsoft.ML.OnnxRuntime@1.26.0
// #:package Microsoft.ML.OnnxRuntime.Gpu@1.26.0
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using RapidOCRSharpOnnx;
using RapidOCRSharpOnnx.Configurations;
using RapidOCRSharpOnnx.Providers;
using RapidOCRSharpOnnx.Utils;

await Main([]);


static async Task Main(string[] args)
{


    var config = new AppConfig();
    Console.WriteLine($"✅  采样率: {config.SampleFps:F1} FPS | 防抖间隔: {config.MinCharacterIntervalSec:F1}s");


    using RapidOCRSharp ocr = new RapidOCRSharp(new ExecutionProviderCPU(new OcrConfig(@"F:\Code\Github\script\models\RapidOCR\onnx\PP-OCRv5\det\ch_PP-OCRv5_det_server.onnx", @"F:\Code\Github\script\models\RapidOCR\onnx\PP-OCRv5\rec\ch_PP-OCRv5_rec_server.onnx", LangRec.CH, OCRVersion.PPOCRV5, @"F:\Code\Github\script\models\RapidOCR\onnx\PP-OCRv5\cls\ch_PP-LCNet_x1_0_textline_ori_cls_server.onnx")));
    Console.WriteLine("🚀 OCR 引擎初始化完成\n");

    var videoFiles = Directory.GetFiles(config.VideoDirectory, "*.mp4", SearchOption.TopDirectoryOnly)
                              .OrderBy(f => f).ToArray();
    if (videoFiles.Length == 0)
    {
        Console.WriteLine("⚠️ 未找到视频文件，请将 MP4 放入 videos/ 目录");
        return;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(config.OutputCsv)!);
    // 写入 CSV 表头
    File.WriteAllText(config.OutputCsv, "VideoFile,Timestamp,Character,Confidence\n");

    Console.WriteLine($"📹 共发现 {videoFiles.Length} 个视频，开始处理...\n");
    int globalOffset = 1;
    foreach (var videoPath in videoFiles)
    {
        if (globalOffset <= 76) { globalOffset++; continue; }
        _ = await ProcessVideoAsync(videoPath, config, ocr, config.OutputCsv, globalOffset);
        globalOffset++;
    }

    Console.WriteLine($"\n✅ 全部处理完成！结果已保存至: {Path.GetFullPath(config.OutputCsv)}");
}
static async Task<double> ProcessVideoAsync(
    string videoPath,
    AppConfig config,
    RapidOCRSharp ocr,
    string csvPath,
    int globalOffset)
{
    var fileName = Path.GetFileName(videoPath);

    using var cap = new VideoCapture(videoPath);
    if (!cap.IsOpened()) return 0;

    double fps = cap.Fps;
    int totalFrames = (int)cap.FrameCount;
    int skipFrames = Math.Max(1, (int)(fps / config.SampleFps));

    HashSet<string> seen = new();
    double skipUntil = 0;

    using var frame = new Mat();

    for (int i = 1; i < totalFrames; i += skipFrames)
    {
        cap.Set(VideoCaptureProperties.PosFrames, i);
        if (!cap.Read(frame) || frame.Empty()) continue;

        double localTime = i / fps;
        double time = localTime;

        if (localTime < skipUntil)
            continue;

        string text = ExtractHandwritingChar(frame, config, ocr, fileName);
        if (string.IsNullOrEmpty(text))
            continue;

        if (!seen.Add(text))
            continue;

        skipUntil = localTime + 5;

        await File.AppendAllTextAsync(
            csvPath,
            $"{fileName},{globalOffset}#{TimeSpan.FromSeconds(time):hh\\:mm\\:ss},{text},0.00{Environment.NewLine}");

        Console.WriteLine($"命中: {text} @ {time:F1}s");
    }

    return totalFrames / fps;
}
static string ExtractHandwritingChar(Mat frame, AppConfig config, RapidOCRSharp ocr, string fileName)
{
    int h = frame.Rows, w = frame.Cols;

    // ===== 基础区域：中间 80% =====
    int rw = (int)(w * 0.8);
    int rh = (int)(h * 0.3);   // 中上区域高度

    // ===== 水平居中 =====
    int x = (w - rw) / 2;

    // ===== 关键：向上偏移 =====
    int y = (int)(h * 0.05);    // 偏上 10%

    // ===== ROI =====
    using var roi = new Mat(frame, new Rect(x, y, rw, rh));
    // mat 是你的 OpenCvSharp.Mat 对象
    using Bitmap bitmap = BitmapConverter.ToBitmap(roi);

    // 示例：保存或传递给其他 API
    bitmap.Save("output.bmp");
    // RapidOCR 直接返回识别结果列表
    var result = ocr.RecognizeTextSeq(roi);
    if (result == null || result.RecResult?.Data == null)
        return string.Empty;

    // 👇 关键：从 RecResult.Result 数组中筛选
    var recItems = result.RecResult.Data;

    var valid = recItems
        .Where(r => !string.IsNullOrEmpty(r.Label)
                 && r.Label.Trim().Length == 1          // 仅单字
                 && char.IsLetterOrDigit(r.Label.Trim()[0]) // 排除标点
                 && fileName.Contains(r.Label.Trim()))
        .OrderByDescending(r => r.Score)
        .FirstOrDefault();

    return valid?.Label?.Trim() ?? string.Empty;
}

static HashSet<string> LoadTargetChars(string path)
{
    var content = File.ReadAllText(path);
    var chars = new HashSet<string>();
    foreach (char c in content)
        if (!char.IsWhiteSpace(c)) chars.Add(c.ToString());
    return chars;
}


public class AppConfig
{
    public string VideoDirectory { get; set; } = @"F:\video\行楷7000字 逐字示范";
    public string TargetCharsFile { get; set; } = @"F:\Code\Github\script\target_chars.txt";
    public string OutputCsv { get; set; } = $"{Path.EntryPointFileDirectoryPath()}/output/detection_log.csv";
    public string ModelDirectory { get; set; } = $"{Path.EntryPointFileDirectoryPath()}\\models";
    public double SampleFps { get; set; } = 1.5;
    public double MinCharacterIntervalSec { get; set; } = 5.0;
    public double ConfidenceThreshold { get; set; } = 0.75;
    public bool UseGpu { get; set; } = false;
    public double RoiCenterPercent { get; set; } = 0.6;
}