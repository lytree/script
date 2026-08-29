using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using TdLib;
using ZLogger;
using Framework.ZLogging;

public class DownloadTracker
{
    private record FileDownloadInfo(long TotalSize, string FileName, long DownloadedSize, double LastSpeed, long LastUpdateTick, bool IsCompleted);

    private readonly Dictionary<int, FileDownloadInfo> _downloads = [];
    private readonly object _lock = new();
    private readonly HashSet<int> _completedFiles = [];
    private readonly Dictionary<int, ProgressTask> _tasks = [];
    private volatile bool _running;
    private Thread? _thread;

    public void StartDownload(int fileId, string fileName, long totalSize, long downloadedSize = 0)
    {
        EnsureStarted();
        lock (_lock)
        {
            _downloads[fileId] = new FileDownloadInfo(totalSize, fileName, downloadedSize, 0, Stopwatch.GetTimestamp(), false);
        }
    }

    public void UpdateProgress(int fileId, string fileName, long totalSize, long downloadedSize)
    {
        lock (_lock)
        {
            if (_downloads.TryGetValue(fileId, out var info))
            {
                long nowTick = Stopwatch.GetTimestamp();
                double seconds = (nowTick - info.LastUpdateTick) / (double)Stopwatch.Frequency;
                double speed = seconds > 0 && downloadedSize > info.DownloadedSize
                    ? (downloadedSize - info.DownloadedSize) / seconds
                    : 0;
                _downloads[fileId] = info with { TotalSize = totalSize, DownloadedSize = downloadedSize, LastSpeed = speed, LastUpdateTick = nowTick };
            }
            else
            {
                _downloads[fileId] = new FileDownloadInfo(totalSize, fileName, downloadedSize, 0, Stopwatch.GetTimestamp(), false);
            }
        }
    }

    public void CompleteDownload(int fileId, string fileName, long size)
    {
        lock (_lock)
        {
            if (_downloads.TryGetValue(fileId, out var info))
            {
                _downloads[fileId] = info with { DownloadedSize = size, TotalSize = size, IsCompleted = true };
            }
            _completedFiles.Add(fileId);
        }
    }

    public int GetCompletedCount()
    {
        lock (_lock)
        {
            return _completedFiles.Count;
        }
    }

    public void Stop() => _running = false;

    private void EnsureStarted()
    {
        if (_thread != null) return;

        _running = true;
        _thread = new Thread(() =>
        {
            AnsiConsole.Progress()
                .AutoRefresh(true)
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new DownloadedColumn(),
                    new TransferSpeedColumn(),
                    new RemainingTimeColumn())
                .Start(ctx =>
                {
                    while (_running)
                    {
                        lock (_lock)
                        {
                            foreach (var (fileId, info) in _downloads)
                            {
                                if (!_tasks.TryGetValue(fileId, out var task))
                                {
                                    task = ctx.AddTask($"[cyan]{fileId}[/]");
                                    task.MaxValue = info.TotalSize > 0 ? info.TotalSize : 1;
                                    task.Value = info.DownloadedSize;
                                    task.StartTask();
                                    _tasks[fileId] = task;
                                }

                                if (info.IsCompleted)
                                {
                                    task.Value = task.MaxValue;
                                    task.Description = $"[green]\u2713[/] [cyan]{fileId}[/] [green]\u4e0b\u8f7d\u5b8c\u6210[/]";
                                    task.StopTask();
                                    AnsiConsole.WriteLine();
                                }
                                else
                                {
                                    double percent = info.TotalSize > 0 ? (double)info.DownloadedSize / info.TotalSize * 100 : 0;
                                    string downloadedStr = FormatSize(info.DownloadedSize);
                                    string totalStr = FormatSize(info.TotalSize);
                                    string speedStr = info.LastSpeed > 0 ? $"{FormatSize((long)info.LastSpeed)}/s" : "";
                                    task.MaxValue = info.TotalSize > 0 ? info.TotalSize : 1;
                                    task.Value = info.DownloadedSize;
                                    task.Description = $"[cyan]{fileId}[/] [[{percent:F1}%]] {downloadedStr} / {totalStr} {speedStr}";
                                }
                            }

                            var completedIds = _downloads
                                .Where(kvp => kvp.Value.IsCompleted)
                                .Select(kvp => kvp.Key)
                                .ToList();
                            foreach (var id in completedIds)
                            {
                                _downloads.Remove(id);
                                _tasks.Remove(id);
                                _completedFiles.Add(id);
                            }
                        }

                        Thread.Sleep(100);
                    }
                });
        })
        { IsBackground = true };
        _thread.Start();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] sizes = { "B", "KB", "MB", "GB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:F1} {sizes[order]}";
    }
}

public static class TdlMediaHelper
{
    public static int GetFileIdFromMessage(TdApi.Message message)
    {
        return message.Content switch
        {
            TdApi.MessageContent.MessageDocument d => d.Document.Document_.Id,
            TdApi.MessageContent.MessageVideo v => v.Video.Video_.Id,
            TdApi.MessageContent.MessagePhoto p => p.Photo.Sizes.LastOrDefault()?.Photo.Id ?? 0,
            TdApi.MessageContent.MessageAudio a => a.Audio.Audio_.Id,
            TdApi.MessageContent.MessageAnimation ani => ani.Animation.Animation_.Id,
            TdApi.MessageContent.MessageVideoNote vn => vn.VideoNote.Video.Id,
            TdApi.MessageContent.MessageVoiceNote vce => vce.VoiceNote.Voice.Id,
            _ => 0
        };
    }

    public static void OnDownloadFinished(
        TdApi.File file,
        IReadOnlyDictionary<int, long> fileIdToAlbumId,
        string outputPath,
        ILogger logger)
    {
        string sourcePath = file.Local.Path;
        if (string.IsNullOrEmpty(sourcePath)) return;

        string fileName = Path.GetFileName(sourcePath);
        string albumSubPath = fileIdToAlbumId.TryGetValue(file.Id, out long albumId) && albumId != 0
            ? Path.Combine("Downloads", albumId.ToString())
            : "Downloads";

        string targetPath = Path.Combine(outputPath, albumSubPath, fileName);

        try
        {
            if (File.Exists(sourcePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Move(sourcePath, targetPath, true);
                logger.ZLogInformation($"\u6587\u4ef6\u5df2\u5f52\u6863\u81f3: {sourcePath} -> {targetPath}");
            }
            else
            {
                logger.ZLogInformation($"\u6587\u4ef6\u4e0d\u5b58\u5728: {sourcePath}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理下载完成的文件时出错");
        }
    }
}

