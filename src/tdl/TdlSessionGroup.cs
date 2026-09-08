#!/usr/bin/env dotnet

#:package TDLib@*
#:package tdlib.native@*
#:package tdlib.native.win-x64@*
#:package Spectre.Console@*
#:package Spectre.Console.Ansi@*
#:package System.CommandLine@*

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Spectre.Console;
using TdLib;
using TdLib.Bindings;

string LogInfo(string msg) { Console.WriteLine($"[INFO ] {msg}"); return msg; }
string LogWarn(string msg) { Console.WriteLine($"[WARN ] {msg}"); return msg; }
string LogErr(string msg)  { Console.Error.WriteLine($"[ERROR] {msg}"); return msg; }

long ReadLong(JsonElement el) =>
    el.ValueKind == JsonValueKind.Number ? el.GetInt64() :
    el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var n) ? n : 0;
int ReadInt(JsonElement el) =>
    el.ValueKind == JsonValueKind.Number ? el.GetInt32() :
    el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var n) ? n : 0;

// ─────────────────────────────────────────────────────────────
// CLI
// ─────────────────────────────────────────────────────────────

var optIndex = new Option<string>("--index") { Required = true, Description = "媒体索引 JSON" };
var optDownloadDir = new Option<string>("--download-dir") { Required = true, Description = "当前下载目录" };
var optOutput = new Option<string>("--output") { Required = true, Description = "输出目录 (按 /start 切分)" };
var optChatId = new Option<long>("--chat-id") { Required = true, Description = "聊天 ID (用于搜索 /start 标记)" };
var optDryRun = new Option<bool>("--dry-run") { Description = "只生成方案，不实际挪移" };
var optLimit = new Option<int>("--limit") { DefaultValueFactory = _ => 0, Description = "限制处理的会话数 (0=全部)" };
var optStartFrom = new Option<int>("--start") { DefaultValueFactory = _ => 0, Description = "从第几个会话开始" };
var optNoMeta = new Option<bool>("--no-meta") { Description = "不写 _meta.json / _summary.json" };
var optRemoveEmpty = new Option<bool>("--remove-empty") { Description = "删除空的原 Photo/Video 月份目录" };

var root = new RootCommand("按 /start 命令消息把媒体分组到不同目录, 直接挪移文件");
root.Options.Add(optIndex);
root.Options.Add(optDownloadDir);
root.Options.Add(optOutput);
root.Options.Add(optChatId);
root.Options.Add(optDryRun);
root.Options.Add(optLimit);
root.Options.Add(optStartFrom);
root.Options.Add(optNoMeta);
root.Options.Add(optRemoveEmpty);

var p = root.Parse(args);
var indexPath = p.GetValue(optIndex)!;
var downloadDir = p.GetValue(optDownloadDir)!;
var outputRoot = p.GetValue(optOutput)!;
var chatId = p.GetValue(optChatId);
var dryRun = p.GetValue(optDryRun);
var limit = p.GetValue(optLimit);
var startFrom = Math.Max(0, p.GetValue(optStartFrom));
var noMeta = p.GetValue(optNoMeta);
var removeEmpty = p.GetValue(optRemoveEmpty);

if (!File.Exists(indexPath)) { LogErr("索引不存在: " + indexPath); Environment.Exit(1); return; }
if (!Directory.Exists(downloadDir)) { LogErr("下载目录不存在: " + downloadDir); Environment.Exit(1); return; }

if (!dryRun)
{
    if (Directory.Exists(outputRoot))
    {
        LogWarn("输出目录已存在, 继续. 如要清空请先手动删除: " + outputRoot);
    }
    Directory.CreateDirectory(outputRoot);
}

// ─────────────────────────────────────────────────────────────
// 1. 加载媒体索引
// ─────────────────────────────────────────────────────────────

using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath));
var rootEl = doc.RootElement;
var chatTitle = rootEl.GetProperty("ChatTitle").GetString() ?? "unknown";
var mediaList = rootEl.GetProperty("MediaMessages").EnumerateArray().ToList();

var items = mediaList.Select(m => new MediaItem
{
    MessageId = ReadLong(m.GetProperty("MessageId")),
    Date = DateTime.Parse(m.GetProperty("Date").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
    Type = m.GetProperty("Type").GetString() ?? "Other",
    Caption = m.TryGetProperty("Caption", out var c) ? c.GetString() : "",
    MediaAlbumId = m.TryGetProperty("MediaAlbumId", out var ma) ? ReadLong(ma) : 0L,
    Size = m.TryGetProperty("Media", out var me) && me.TryGetProperty("Size", out var sz) ? ReadLong(sz) : 0,
    MimeType = m.TryGetProperty("Media", out var me2) && me2.TryGetProperty("MimeType", out var mt) ? mt.GetString() : "",
    Width = m.TryGetProperty("Media", out var me3) && me3.TryGetProperty("Width", out var w) ? ReadInt(w) : 0,
    Height = m.TryGetProperty("Media", out var me4) && me4.TryGetProperty("Height", out var h) ? ReadInt(h) : 0,
    Duration = m.TryGetProperty("Media", out var me5) && me5.TryGetProperty("Duration", out var du) ? ReadInt(du) : 0,
    RelativePath = BuildRelativePath(downloadDir, m)
}).OrderBy(i => i.Date).ToList();

LogInfo($"聊天: {chatTitle} ({chatId})");
LogInfo($"媒体消息: {items.Count}");

// ─────────────────────────────────────────────────────────────
// 2. 通过 TDLib 搜索所有 /start 命令消息
// ─────────────────────────────────────────────────────────────

LogInfo("搜索 /start 命令消息...");
var startMarkers = await SearchAllStartCommandsAsync(chatId);
LogInfo($"找到 {startMarkers.Count} 条 /start 标记");

if (startMarkers.Count == 0)
{
    LogErr("未找到任何 /start 命令, 退出");
    Environment.Exit(1);
    return;
}

// 按时间正序
startMarkers = startMarkers.OrderBy(m => m.Date).ToList();

// ─────────────────────────────────────────────────────────────
// 3. 按 /start 切分媒体流
// ─────────────────────────────────────────────────────────────

// 区间定义: [start_marker_i.Date, start_marker_(i+1).Date)
// 第一段: 起点 = 最早媒体时间; 最后一段: 终点 = +∞
// /start 标记本身可能没有对应媒体消息, 但其时间戳就是分割点

var segments = new List<Segment>();
for (int i = 0; i < startMarkers.Count; i++)
{
    var from = startMarkers[i].Date;
    var to = i + 1 < startMarkers.Count ? startMarkers[i + 1].Date : DateTime.MaxValue;
    var segItems = items.Where(it => it.Date >= from && it.Date < to).ToList();
    segments.Add(new Segment
    {
        Index = i + 1,
        StartDate = from,
        EndDate = to,
        StartMessageId = startMarkers[i].MessageId,
        ItemCount = segItems.Count,
        Photos = segItems.Count(it => it.Type == "Photo"),
        Videos = segItems.Count(it => it.Type == "Video"),
        TotalBytes = segItems.Sum(it => it.Size),
        Items = segItems
    });
}

var beforeFirst = items.Where(it => it.Date < startMarkers[0].Date).ToList();
if (beforeFirst.Count > 0)
{
    LogWarn($"在第一个 /start 之前还有 {beforeFirst.Count} 条媒体, 归为 pre_start 段");
    segments.Insert(0, new Segment
    {
        Index = 0,
        StartDate = DateTime.MinValue,
        EndDate = startMarkers[0].Date,
        StartMessageId = 0,
        ItemCount = beforeFirst.Count,
        Photos = beforeFirst.Count(it => it.Type == "Photo"),
        Videos = beforeFirst.Count(it => it.Type == "Video"),
        TotalBytes = beforeFirst.Sum(it => it.Size),
        Items = beforeFirst,
        Label = "pre_start"
    });
}

// 重新编号
for (int i = 0; i < segments.Count; i++) segments[i].Index = i;

// 应用 start/limit
if (startFrom > 0 || limit > 0)
{
    segments = segments.Skip(startFrom).ToList();
    if (limit > 0) segments = segments.Take(limit).ToList();
}

LogInfo($"切分结果: {segments.Count} 个会话段 (startFrom={startFrom}, limit={limit})");
LogInfo($"  空段: {segments.Count(s => s.ItemCount == 0)}");
LogInfo($"  非空段: {segments.Count(s => s.ItemCount > 0)}");
LogInfo($"  总文件: {segments.Sum(s => s.ItemCount)}");
LogInfo($"  中位段大小: {Median(segments.Select(s => s.ItemCount)):F1} 文件/段");

// ─────────────────────────────────────────────────────────────
// 4. 显示切分预览
// ─────────────────────────────────────────────────────────────

var previewTable = new Table().Border(TableBorder.Rounded)
    .Title("[bold cyan]按 /start 切分预览[/]")
    .AddColumn(new TableColumn("[bold]#[/]").RightAligned())
    .AddColumn(new TableColumn("[bold]时间[/]"))
    .AddColumn(new TableColumn("[bold]P/V[/]").RightAligned())
    .AddColumn(new TableColumn("[bold]大小[/]").RightAligned())
    .AddColumn(new TableColumn("[bold]内容[/]"));

int shown = 0;
foreach (var s in segments)
{
    if (s.ItemCount == 0) continue;
    var firstCaption = s.Items.FirstOrDefault()?.Caption ?? "";
    if (firstCaption.Length > 40) firstCaption = firstCaption.Substring(0, 40) + "...";
    previewTable.AddRow(
        s.Index.ToString(),
        s.StartDate.ToString("yyyy-MM-dd HH:mm"),
        $"{s.Photos}/{s.Videos}",
        FormatBytes(s.TotalBytes),
        firstCaption.Replace("\n", " ")
    );
    shown++;
    if (shown >= 30) break;
}
AnsiConsole.Write(previewTable);
if (segments.Count(s => s.ItemCount > 0) > 30)
    AnsiConsole.MarkupLine($"[grey]... 仅显示前 30 个非空段 (共 {segments.Count(s => s.ItemCount > 0)} 个)[/]");

// ─────────────────────────────────────────────────────────────
// 5. 实际挪移
// ─────────────────────────────────────────────────────────────

if (dryRun)
{
    AnsiConsole.MarkupLine("\n[yellow]Dry-run 模式, 未实际挪移文件[/]");
    return;
}

LogInfo("开始挪移文件...");
int moved = 0, missing = 0, errors = 0;
long bytesMoved = 0;
var sw = System.Diagnostics.Stopwatch.StartNew();
var movedRecords = new List<MovedRecord>();

foreach (var seg in segments)
{
    if (seg.ItemCount == 0) continue;
    var label = seg.Label ?? $"start_{seg.StartDate:yyyyMMdd_HHmmss}";
    var segDir = Path.Combine(outputRoot, $"{seg.Index:D4}_{label}");
    Directory.CreateDirectory(segDir);

    var filesRel = new List<object>();
    int idx = 0;
    foreach (var it in seg.Items)
    {
        idx++;
        var src = Path.GetFullPath(Path.Combine(downloadDir, it.RelativePath));
        var dstName = $"{idx:D3}_{Path.GetFileName(it.RelativePath)}";
        var dst = Path.Combine(segDir, dstName);

        if (!File.Exists(src))
        {
            missing++;
            continue;
        }
        try
        {
            File.Move(src, dst);
            moved++;
            bytesMoved += it.Size;
            filesRel.Add(new
            {
                it.MessageId, it.Date, it.Type, it.Size, it.MimeType,
                it.Width, it.Height, it.Duration, it.Caption,
                NewPath = $"{seg.Index:D4}_{label}/{dstName}"
            });
        }
        catch (Exception ex)
        {
            // 跨盘 Move 会失败, 改用 Copy + Delete
            try
            {
                File.Copy(src, dst, true);
                File.Delete(src);
                moved++;
                bytesMoved += it.Size;
                filesRel.Add(new
                {
                    it.MessageId, it.Date, it.Type, it.Size, it.MimeType,
                    it.Width, it.Height, it.Duration, it.Caption,
                    NewPath = $"{seg.Index:D4}_{label}/{dstName}"
                });
            }
            catch
            {
                LogErr($"挪移失败: {src} -> {dst}: {ex.Message}");
                errors++;
            }
        }
    }

    if (!noMeta)
    {
        var meta = new
        {
            seg.Index, seg.Label, seg.StartDate, seg.EndDate, seg.StartMessageId,
            seg.ItemCount, seg.Photos, seg.Videos, seg.TotalBytes,
            Files = filesRel
        };
        await File.WriteAllTextAsync(
            Path.Combine(segDir, "_meta.json"),
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
        );
    }
}

sw.Stop();

// 清理空的原月份目录
int removedDirs = 0;
if (removeEmpty)
{
    foreach (var typeDir in new[] { "Photo", "Video" })
    {
        var baseDir = Path.Combine(downloadDir, typeDir);
        if (!Directory.Exists(baseDir)) continue;
        foreach (var monthDir in Directory.EnumerateDirectories(baseDir))
        {
            if (!Directory.EnumerateFileSystemEntries(monthDir).Any())
            {
                try { Directory.Delete(monthDir); removedDirs++; }
                catch { }
            }
        }
        if (!Directory.EnumerateFileSystemEntries(baseDir).Any())
        {
            try { Directory.Delete(baseDir); removedDirs++; }
            catch { }
        }
    }
}

if (!noMeta)
{
    var summary = new
    {
        ChatId = chatId,
        ChatTitle = chatTitle,
        GeneratedAt = DateTime.UtcNow,
        SourceIndex = indexPath,
        SourceDownloadDir = Path.GetFullPath(downloadDir),
        OutputDir = Path.GetFullPath(outputRoot),
        StartMarkers = startMarkers.Count,
        TotalSegments = segments.Count,
        EmptySegments = segments.Count(s => s.ItemCount == 0),
        NonEmptySegments = segments.Count(s => s.ItemCount > 0),
        MovedFiles = moved,
        MissingFiles = missing,
        Errors = errors,
        RemovedEmptyDirs = removedDirs,
        BytesMoved = bytesMoved,
        ElapsedSec = sw.Elapsed.TotalSeconds
    };
    await File.WriteAllTextAsync(
        Path.Combine(outputRoot, "_summary.json"),
        JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true })
    );
}

AnsiConsole.WriteLine();
AnsiConsole.Write(new Rule("[bold green]挪移完成[/]").LeftJustified());
AnsiConsole.MarkupLine($"聊天:        [cyan]{chatTitle.EscapeMarkup()}[/] ({chatId})");
AnsiConsole.MarkupLine($"/start 标记:  [yellow]{startMarkers.Count}[/]");
AnsiConsole.MarkupLine($"会话段:      [green]{segments.Count(s => s.ItemCount > 0)}[/] 非空 / [grey]{segments.Count(s => s.ItemCount == 0)}[/] 空");
AnsiConsole.MarkupLine($"挪移文件:    [green]{moved}[/] / [yellow]{missing}[/] 缺失 / [red]{errors}[/] 失败");
AnsiConsole.MarkupLine($"清理空目录:  [grey]{removedDirs}[/]");
AnsiConsole.MarkupLine($"数据量:      [yellow]{FormatBytes(bytesMoved)}[/]");
AnsiConsole.MarkupLine($"耗时:        [grey]{sw.Elapsed.ToString(@"hh\:mm\:ss")}[/]");
AnsiConsole.MarkupLine($"输出目录:    [green]{Path.GetFullPath(outputRoot)}[/]");
AnsiConsole.WriteLine();

// ─────────────────────────────────────────────────────────────
// 帮助函数
// ─────────────────────────────────────────────────────────────

static double Median(IEnumerable<int> xs)
{
    var a = xs.OrderBy(x => x).ToList();
    if (a.Count == 0) return 0;
    return a.Count % 2 == 1 ? a[a.Count/2] : (a[a.Count/2 - 1] + a[a.Count/2]) / 2.0;
}

static string FormatBytes(long bytes)
{
    if (bytes < 1024) return bytes + " B";
    if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
    if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("F1") + " MB";
    return (bytes / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
}

string BuildRelativePath(string rootDir, JsonElement msg)
{
    var msgId = ReadLong(msg.GetProperty("MessageId"));
    var dateStr = msg.GetProperty("Date").GetString();
    var date = DateTime.Parse(dateStr!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    var type = msg.GetProperty("Type").GetString() ?? "Other";
    var ext = msg.TryGetProperty("Media", out var me) && me.TryGetProperty("MimeType", out var mt) ? mt.GetString() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "video/quicktime" => ".mov",
        _ => ".bin"
    } : ".bin";
    var fileName = date.ToString("yyyyMMdd_HHmmss") + "_" + msgId + ext;
    return Path.Combine(type, date.ToString("yyyy-MM"), fileName);
}

async Task<List<StartMarker>> SearchAllStartCommandsAsync(long chatIdVal)
{
    var apiId = Environment.GetEnvironmentVariable("tdl_api_id", EnvironmentVariableTarget.User);
    var apiHash = Environment.GetEnvironmentVariable("tdl_api_hash", EnvironmentVariableTarget.User);
    if (string.IsNullOrWhiteSpace(apiId) || string.IsNullOrWhiteSpace(apiHash))
    {
        LogErr("缺少 tdl_api_id 或 tdl_api_hash 环境变量");
        Environment.Exit(2);
        return new List<StartMarker>();
    }

    using var client = new TdJsonClient();
    client.Send("{\"@type\":\"setLogVerbosityLevel\",\"new_verbosity_level\":0}");

    var tdlPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tdl").Replace("\\", "/");
    var loop = new EventLoop(client);
    loop.Start();

    loop.Send("{\"@type\":\"setTdlibParameters\",\"api_id\":\"" + apiId + "\",\"api_hash\":\"" + apiHash + "\",\"device_model\":\"PC\",\"system_language_code\":\"en\",\"application_version\":\"1.0.0\",\"database_directory\":\"" + tdlPath + "/db\",\"files_directory\":\"" + tdlPath + "/files\",\"use_file_database\":true,\"use_chat_info_database\":true,\"use_message_database\":true}");

    if (!await WaitFor(loop, "Ready", 60))
    {
        LogErr("TDLib 未就绪");
        Environment.Exit(1);
        return new List<StartMarker>();
    }

    var markers = new List<StartMarker>();
    long fromMessageId = 0;
    bool hasMore = true;
    int batch = 0;
    while (hasMore)
    {
        var req = "{\"@type\":\"searchChatMessages\",\"chat_id\":" + chatIdVal + ",\"query\":\"/start\",\"from_message_id\":" + fromMessageId + ",\"offset\":0,\"limit\":100,\"filter\":null}";
        var resp = await loop.RpcAsync(req, 30);
        using var d = JsonDocument.Parse(resp);
        if (d.RootElement.GetProperty("@type").GetString() == "error")
        {
            LogErr("searchChatMessages 失败: " + resp);
            break;
        }
        if (!d.RootElement.TryGetProperty("messages", out var msgs)) break;
        var arr = msgs.EnumerateArray().ToList();
        if (arr.Count == 0) break;

        int added = 0;
        foreach (var m in arr)
        {
            var id = ReadLong(m.GetProperty("id"));
            var date = ReadLong(m.GetProperty("date"));
            var dt = DateTimeOffset.FromUnixTimeSeconds(date).UtcDateTime;
            markers.Add(new StartMarker { MessageId = id, Date = dt });
            added++;
        }
        batch++;

        long nextId = 0;
        if (d.RootElement.TryGetProperty("next_from_message_id", out var nfm))
            nextId = ReadLong(nfm);
        if (nextId == 0 || nextId == fromMessageId)
        {
            if (arr.Count > 0)
                nextId = ReadLong(arr[arr.Count - 1].GetProperty("id"));
        }
        if (nextId == fromMessageId || arr.Count < 100) break;
        fromMessageId = nextId;

        await Task.Delay(300);
    }

    loop.Stop();
    return markers;
}

async Task<bool> WaitFor(EventLoop loop, string target, int timeoutSec)
{
    var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
    while (DateTime.UtcNow < deadline)
    {
        if (loop.GetAuthState() == target) return true;
        await Task.Delay(200);
    }
    return false;
}

// ─────────────────────────────────────────────────────────────
// 类型
// ─────────────────────────────────────────────────────────────

public class MediaItem
{
    public long MessageId { get; set; }
    public DateTime Date { get; set; }
    public string Type { get; set; } = "";
    public string Caption { get; set; } = "";
    public long MediaAlbumId { get; set; }
    public long Size { get; set; }
    public string MimeType { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Duration { get; set; }
    public string RelativePath { get; set; } = "";
}

public class Segment
{
    public int Index { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public long StartMessageId { get; set; }
    public int ItemCount { get; set; }
    public int Photos { get; set; }
    public int Videos { get; set; }
    public long TotalBytes { get; set; }
    public string? Label { get; set; }
    public List<MediaItem> Items { get; set; } = new();
}

public class StartMarker
{
    public long MessageId { get; set; }
    public DateTime Date { get; set; }
}

public class MovedRecord
{
    public long MessageId { get; set; }
    public string NewPath { get; set; } = "";
}

public class EventLoop
{
    private readonly TdJsonClient _client;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();
    private string _authState = "Init";
    private int _counter;
    private System.Threading.CancellationTokenSource _cts = new();
    private Task? _loopTask;

    public EventLoop(TdJsonClient client) { _client = client; }
    public void Start() { _loopTask = Task.Run(Loop); }
    public void Stop() { _cts.Cancel(); try { _loopTask?.Wait(2000); } catch { } }
    public string GetAuthState() => _authState;

    public void Send(string jsonWithoutExtra) { _client.Send(jsonWithoutExtra); }

    public Task<string> RpcAsync(string jsonWithoutExtra, double timeoutSec)
    {
        var key = "r-" + Interlocked.Increment(ref _counter);
        var injected = jsonWithoutExtra.Insert(jsonWithoutExtra.Length - 1, ",\"@extra\":\"" + key + "\"");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;
        _client.Send(injected);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(timeoutSec));
            if (_pending.TryRemove(key, out var p)) p.TrySetResult("{\"@type\":\"error\",\"code\":408,\"message\":\"timeout\"}");
        });
        return tcs.Task;
    }

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var u = _client.Receive(1.0);
                if (string.IsNullOrEmpty(u)) continue;
                using var doc = JsonDocument.Parse(u);
                var root = doc.RootElement;
                if (root.TryGetProperty("@extra", out var extra))
                {
                    var key = extra.GetString();
                    if (key != null && _pending.TryRemove(key, out var tcs)) tcs.TrySetResult(u);
                }
                else if (root.TryGetProperty("@type", out var t) && t.GetString() == "updateAuthorizationState" && root.TryGetProperty("authorization_state", out var auth))
                {
                    _authState = (auth.GetProperty("@type").GetString() ?? "") switch
                    {
                        "authorizationStateReady" => "Ready",
                        "authorizationStateClosed" => "Closed",
                        var n => n
                    };
                }
            }
            catch { if (_cts.IsCancellationRequested) break; Thread.Sleep(50); }
        }
    }
}
