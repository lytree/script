#!/usr/bin/env dotnet

#:package Spectre.Console@*
#:package Spectre.Console.Ansi@*
#:package System.CommandLine@*

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Spectre.Console;

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

var optIndex = new Option<string>("--index") { Required = true, Description = "媒体索引 JSON 路径" };
var optDownloadDir = new Option<string>("--download-dir") { Required = true, Description = "已下载的目录 (含 Photo/Video)" };
var optOutput = new Option<string?>("--output") { Description = "分组输出根目录 (默认 data/tdl/grouped/<chatId>)" };
var optSessionGap = new Option<int>("--session-gap") { DefaultValueFactory = _ => 30, Description = "会话分段间隔(分钟) - 间隔大于此值算新会话" };
var optNoJunction = new Option<bool>("--no-junction") { DefaultValueFactory = _ => false, Description = "不创建目录链接，只输出 JSON 清单" };
var optClean = new Option<bool>("--clean") { DefaultValueFactory = _ => false, Description = "先清空输出目录" };
var optMode = new Option<string>("--mode") { DefaultValueFactory = _ => "all", Description = "分组模式: album | session | both | all (默认 both)" };

var root = new RootCommand("基于 Telegram Album ID + 时间聚类 对图片视频分组");
root.Options.Add(optIndex);
root.Options.Add(optDownloadDir);
root.Options.Add(optOutput);
root.Options.Add(optSessionGap);
root.Options.Add(optNoJunction);
root.Options.Add(optClean);
root.Options.Add(optMode);

var p = root.Parse(args);
var indexPath = p.GetValue(optIndex)!;
var downloadDir = p.GetValue(optDownloadDir)!;
var outputRoot = p.GetValue(optOutput);
var sessionGapMin = Math.Max(1, p.GetValue(optSessionGap));
var noJunction = p.GetValue(optNoJunction);
var clean = p.GetValue(optClean);
var mode = p.GetValue(optMode)!.ToLowerInvariant();

if (!File.Exists(indexPath)) { LogErr("索引不存在: " + indexPath); Environment.Exit(1); return; }
if (!Directory.Exists(downloadDir)) { LogErr("下载目录不存在: " + downloadDir); Environment.Exit(1); return; }

using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath));
var chatId = doc.RootElement.GetProperty("ChatId").GetInt64();
var chatTitle = doc.RootElement.GetProperty("ChatTitle").GetString() ?? "unknown";
var msgs = doc.RootElement.GetProperty("MediaMessages").EnumerateArray().ToList();

outputRoot ??= Path.Combine("data", "tdl", "grouped", chatId.ToString());

if (clean && Directory.Exists(outputRoot))
{
    LogInfo("清理输出目录: " + outputRoot);
    Directory.Delete(outputRoot, true);
}
Directory.CreateDirectory(outputRoot);

// ─────────────────────────────────────────────────────────────
// 加载消息
// ─────────────────────────────────────────────────────────────

var items = msgs.Select(m => new MediaItem
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
    RelativePath = BuildRelativePath(downloadDir, m, ReadLong(m.GetProperty("MessageId")), m.GetProperty("Date").GetString(), m.GetProperty("Type").GetString())
}).OrderBy(i => i.Date).ToList();

LogInfo($"聊天: {chatTitle} ({chatId})");
LogInfo($"媒体消息: {items.Count}");

// 校验文件存在
int missing = 0;
foreach (var it in items)
{
    var abs = Path.GetFullPath(Path.Combine(downloadDir, it.RelativePath));
    if (!File.Exists(abs))
    {
        missing++;
        it.Absent = true;
    }
}
if (missing > 0) LogWarn($"{missing} 条索引对应的文件不存在 (跳过)");

// ─────────────────────────────────────────────────────────────
// 分组 1: Album 级 (Telegram 原生媒体组)
// ─────────────────────────────────────────────────────────────

List<Group> albumGroups = new();
if (mode == "album" || mode == "both" || mode == "all")
{
    var byAlbum = items.Where(i => i.MediaAlbumId != 0 && !i.Absent)
        .GroupBy(i => i.MediaAlbumId)
        .OrderBy(g => g.Min(i => i.Date))
        .ToList();

    int idx = 1;
    foreach (var g in byAlbum)
    {
        var sorted = g.OrderBy(i => i.MessageId).ToList();
        var first = sorted.First();
        albumGroups.Add(new Group
        {
            Id = $"album_{idx:D4}",
            Kind = "album",
            Start = sorted.Min(i => i.Date),
            End = sorted.Max(i => i.Date),
            ItemCount = sorted.Count,
            Photos = sorted.Count(i => i.Type == "Photo"),
            Videos = sorted.Count(i => i.Type == "Video"),
            TotalBytes = sorted.Sum(i => i.Size),
            MediaAlbumId = g.Key,
            Items = sorted
        });
        idx++;
    }

    // 单条消息也作为独立组
    var singles = items.Where(i => i.MediaAlbumId == 0 && !i.Absent)
        .OrderBy(i => i.Date).ToList();
    foreach (var s in singles)
    {
        albumGroups.Add(new Group
        {
            Id = $"solo_{s.MessageId}",
            Kind = "solo",
            Start = s.Date,
            End = s.Date,
            ItemCount = 1,
            Photos = s.Type == "Photo" ? 1 : 0,
            Videos = s.Type == "Video" ? 1 : 0,
            TotalBytes = s.Size,
            MediaAlbumId = 0,
            Items = new List<MediaItem> { s }
        });
    }

    albumGroups = albumGroups.OrderBy(g => g.Start).ToList();
    // 重新编号 (按 start 排序后)
    int ai = 1;
    foreach (var g in albumGroups.Where(g => g.Kind == "album"))
    {
        g.Id = $"album_{ai:D4}";
        ai++;
    }

    LogInfo($"Album 分组: {albumGroups.Count(g => g.Kind == "album")} 个原生 album + {albumGroups.Count(g => g.Kind == "solo")} 个单条");

    await WriteGroupsAsync(Path.Combine(outputRoot, "by-album"), albumGroups, "by-album", noJunction, clean);
}

// ─────────────────────────────────────────────────────────────
// 分组 2: Session 级 (按时间聚类)
// ─────────────────────────────────────────────────────────────

List<Group> sessionGroups = new();
if (mode == "session" || mode == "both" || mode == "all")
{
    // 把每个 album 视为一个"块"，相邻 album/solo 之间间隔 > 阈值就分新 session
    var blocks = albumGroups
        .OrderBy(g => g.Start)
        .Select(g => new { Start = g.Start, End = g.End, Group = g })
        .ToList();

    var gap = TimeSpan.FromMinutes(sessionGapMin);
    var currentSession = new List<Group>();
    DateTime sessionEnd = DateTime.MinValue;

    foreach (var b in blocks)
    {
        if (currentSession.Count == 0)
        {
            currentSession.Add(b.Group);
            sessionEnd = b.End;
            continue;
        }
        if ((b.Start - sessionEnd) <= gap)
        {
            currentSession.Add(b.Group);
            if (b.End > sessionEnd) sessionEnd = b.End;
        }
        else
        {
            sessionGroups.Add(BuildSessionGroup(currentSession, sessionEnd));
            currentSession = new List<Group> { b.Group };
            sessionEnd = b.End;
        }
    }
    if (currentSession.Count > 0)
        sessionGroups.Add(BuildSessionGroup(currentSession, sessionEnd));

    LogInfo($"Session 分组 (gap={sessionGapMin}min): {sessionGroups.Count} 个会话段");
    LogInfo($"  含 album: {sessionGroups.Count(g => g.ItemCount > 1)}");
    LogInfo($"  单文件: {sessionGroups.Count(g => g.ItemCount == 1)}");
    LogInfo($"  中位大小: {Median(sessionGroups.Select(g => g.ItemCount)):F1} 文件/段");

    await WriteGroupsAsync(Path.Combine(outputRoot, "by-session"), sessionGroups, "by-session", noJunction, clean);
}

// ─────────────────────────────────────────────────────────────
// 总体报告
// ─────────────────────────────────────────────────────────────

var report = new
{
    ChatId = chatId,
    ChatTitle = chatTitle,
    GeneratedAt = DateTime.UtcNow,
    TotalItems = items.Count,
    PresentItems = items.Count(i => !i.Absent),
    MissingItems = items.Count(i => i.Absent),
    AlbumGroups = albumGroups.Count(g => g.Kind == "album"),
    SoloGroups = albumGroups.Count(g => g.Kind == "solo"),
    SessionGapMin = sessionGapMin,
    SessionGroups = sessionGroups.Count,
    OutputRoot = Path.GetFullPath(outputRoot)
};

await File.WriteAllTextAsync(
    Path.Combine(outputRoot, "_summary.json"),
    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })
);

AnsiConsole.WriteLine();
AnsiConsole.Write(new Rule("[bold green]分组完成[/]").LeftJustified());
AnsiConsole.MarkupLine($"聊天:       [cyan]{chatTitle.EscapeMarkup()}[/] ({chatId})");
AnsiConsole.MarkupLine($"媒体总数:   [yellow]{items.Count}[/] (文件存在: [green]{report.PresentItems}[/], 缺失: [red]{items.Count - report.PresentItems}[/])");
if (albumGroups.Count > 0)
{
    AnsiConsole.MarkupLine($"Album 分组: [green]{albumGroups.Count(g => g.Kind == "album")}[/] 个原生 album (含 {albumGroups.Where(g => g.Kind == "album").Sum(g => g.ItemCount)} 条) + [grey]{albumGroups.Count(g => g.Kind == "solo")}[/] 个单条");
}
if (sessionGroups.Count > 0)
{
    AnsiConsole.MarkupLine($"Session 分组: [green]{sessionGroups.Count}[/] 个会话段 (gap={sessionGapMin}min)");
    AnsiConsole.MarkupLine($"  含多个文件的段: [yellow]{sessionGroups.Count(g => g.ItemCount > 1)}[/] (合并 {sessionGroups.Where(g => g.ItemCount > 1).Sum(g => g.ItemCount)} 条)");
    AnsiConsole.MarkupLine($"  单文件段:       [grey]{sessionGroups.Count(g => g.ItemCount == 1)}[/]");
}
AnsiConsole.MarkupLine($"输出目录:   [green]{outputRoot}[/]");
AnsiConsole.MarkupLine($"  ├─ _summary.json (整体报告)");
if (albumGroups.Count > 0) AnsiConsole.MarkupLine($"  ├─ by-album/ ({albumGroups.Count} 个组)");
if (sessionGroups.Count > 0) AnsiConsole.MarkupLine($"  └─ by-session/ ({sessionGroups.Count} 个段)");
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

Group BuildSessionGroup(List<Group> innerGroups, DateTime sessionEnd)
{
    var allItems = innerGroups.SelectMany(g => g.Items).OrderBy(i => i.Date).ToList();
    return new Group
    {
        Id = $"session_{innerGroups.First().Start:yyyyMMdd_HHmmss}",
        Kind = "session",
        Start = innerGroups.First().Start,
        End = sessionEnd,
        ItemCount = allItems.Count,
        Photos = allItems.Count(i => i.Type == "Photo"),
        Videos = allItems.Count(i => i.Type == "Video"),
        TotalBytes = allItems.Sum(i => i.Size),
        SubGroups = innerGroups.Select(g => g.Id).ToList(),
        Items = allItems
    };
}

async Task WriteGroupsAsync(string dir, List<Group> groups, string modeLabel, bool noLink, bool doClean)
{
    Directory.CreateDirectory(dir);

    // 写主索引 JSON
    var jsonGroups = groups.Select(g => new
    {
        g.Id, g.Kind, g.Start, g.End, g.ItemCount, g.Photos, g.Videos, g.TotalBytes,
        g.MediaAlbumId, g.SubGroups,
        Items = g.Items.Select(i => new {
            i.MessageId, Date = i.Date, i.Type, i.Size, i.MimeType, i.Width, i.Height, i.Duration,
            i.Caption, Path = i.RelativePath.Replace('\\', '/')
        }).ToList()
    }).ToList();
    await File.WriteAllTextAsync(
        Path.Combine(dir, "_groups.json"),
        JsonSerializer.Serialize(jsonGroups, new JsonSerializerOptions { WriteIndented = true })
    );

    // 创建每组的子目录 + 链接文件
    int created = 0, linked = 0;
    foreach (var g in groups)
    {
        var groupDir = Path.Combine(dir, g.Id);
        if (Directory.Exists(groupDir)) Directory.Delete(groupDir, true);
        Directory.CreateDirectory(groupDir);

        // 写元数据
        var meta = new
        {
            g.Id, g.Kind, g.Start, g.End, g.ItemCount, g.Photos, g.Videos, g.TotalBytes,
            g.MediaAlbumId, g.SubGroups
        };
        await File.WriteAllTextAsync(
            Path.Combine(groupDir, "_meta.json"),
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true })
        );

        // 链接/复制文件
        foreach (var it in g.Items)
        {
            var src = Path.GetFullPath(Path.Combine(downloadDir, it.RelativePath));
            var dst = Path.Combine(groupDir, Path.GetFileName(it.RelativePath));
            if (!File.Exists(src)) continue;
            if (noLink)
            {
                File.Copy(src, dst);
            }
            else
            {
                TryHardLink(src, dst);
            }
            linked++;
        }
        created++;
    }
    LogInfo($"  {modeLabel}: 写入 {created} 个组目录, {linked} 个文件链接");
}

bool TryHardLink(string src, string dst)
{
    try
    {
        File.CreateSymbolicLink(dst, src);
        return true;
    }
    catch
    {
        try
        {
            File.Copy(src, dst);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

string BuildRelativePath(string rootDir, JsonElement msg, long msgId, string? dateStr, string? type)
{
    var date = DateTime.Parse(dateStr!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
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
    return Path.Combine(type ?? "Other", date.ToString("yyyy-MM"), fileName);
}

// ─────────────────────────────────────────────────────────────
// 数据类型
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
    public bool Absent { get; set; }
}

public class Group
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public int ItemCount { get; set; }
    public int Photos { get; set; }
    public int Videos { get; set; }
    public long TotalBytes { get; set; }
    public long MediaAlbumId { get; set; }
    public List<string>? SubGroups { get; set; }
    public List<MediaItem> Items { get; set; } = new();
}
