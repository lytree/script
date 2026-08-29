#:package SixLabors.ImageSharp@3.1.5

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;

// --- 配置 ---
string targetDir = @"H:\Pictures\";
if (!Directory.Exists(targetDir))
{
    Console.WriteLine($"[错误] 目录不存在: {targetDir}");
    return;
}

var allFiles = Directory.GetFiles(targetDir, "*.*", SearchOption.AllDirectories);
var tasks = new List<(string FullPath, string RealExt)>();

Console.WriteLine($"--- 第一步：扫描中 ---");

foreach (var file in allFiles)
{
    try
    {
        string realExt = null;

        // 关键点：使用 using 确保 stream 在检测完后立即关闭并释放锁定
        using (var stream = File.OpenRead(file))
        {
            var format = Image.DetectFormat(stream);
            if (format != null)
            {
                realExt = "." + format.FileExtensions.First().ToLower();
            }
        } // <--- stream 在这里被关闭

        if (realExt != null)
        {
            string currentExt = Path.GetExtension(file).ToLower();
            if (!IsCompatible(currentExt, realExt))
            {
                tasks.Add((Path.GetFullPath(file), realExt));
                Console.WriteLine($"[发现] {Path.GetFileName(file)} ({currentExt} -> {realExt})");
            }
        }
    }
    catch
    {
        // 忽略无法读取的文件
    }
}

if (tasks.Count == 0)
{
    Console.WriteLine("没有发现需要修正的文件。");
    return;
}

// --- 第二步：交互确认 ---
Console.WriteLine($"\n扫描完毕。共发现 {tasks.Count} 个后缀错误。");
Console.Write("确认执行物理重命名吗？ (y/n): ");
if (Console.ReadLine()?.ToLower() != "y")
{
    Console.WriteLine("操作已取消。");
    return;
}

// --- 第三步：执行修正 ---
Console.WriteLine($"\n--- 第二步：执行修正 ---");
int successCount = 0;

foreach (var task in tasks)
{
    try
    {
        string newPath = Path.ChangeExtension(task.FullPath, task.RealExt);

        // 处理重名冲突
        if (File.Exists(newPath))
        {
            string dir = Path.GetDirectoryName(task.FullPath);
            string fileName = Path.GetFileNameWithoutExtension(task.FullPath);
            newPath = Path.Combine(dir, $"{fileName}_fixed{task.RealExt}");
        }

        File.Move(task.FullPath, newPath);
        Console.WriteLine($"[完成] 重命名为: {Path.GetFileName(newPath)}");
        successCount++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[失败] 无法重命名 {Path.GetFileName(task.FullPath)}: {ex.Message}");
    }
}

Console.WriteLine($"\n任务结束。成功修正 {successCount} 个文件。");

static bool IsCompatible(string current, string real)
{
    if (current == real) return true;
    var pairs = new[] { (".jpg", ".jpeg"), (".jpeg", ".jpg"), (".tif", ".tiff"), (".tiff", ".tif"), (".heif", ".heic"), (".heic", ".heif") };
    return pairs.Any(p => current == p.Item1 && real == p.Item2);
}