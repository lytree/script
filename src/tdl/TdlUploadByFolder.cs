#!/usr/bin/env dotnet run

#:include ../../env.cs
#:include TdlUpdateHandler.cs
#:include TdlEnv.cs

#:package TDLib@*
#:package tdlib.native@*
#:package tdlib.native.win-x64@*
#:package System.CommandLine@*
#:package Spectre.Console@*
#:package Spectre.Console.Ansi@*
#:package Microsoft.Extensions.Logging@*
#:package ZLogger@*
#:package YLFramework.ZLogging@1.0.3-alpha.7

using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Framework.ZLogging;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using TdLib;
using TdLib.Bindings;
using ZLogger;

using (var client = new TdClient())
{
    client.Bindings.SetLogVerbosityLevel(TdLogLevel.Fatal);
    await Main(client, args).ConfigureAwait(false);
}

async Task Main(TdClient client, string[] args)
{
    var logger = TdlEnv.CreateLogger("tdl-upload-by-folder.log", "tdl_upload_by_folder");

    var optSrc = new Option<string>("--src") { Required = true, Description = "源根目录 (含多个子文件夹)" };
    var optChat = new Option<string?>("--chat") { Description = "目标聊天 (邀请链接 / @username / 数字ID, 默认: 收藏夹)" };
    var optTopic = new Option<long?>("--topic") { Description = "论坛主题 ID" };
    var optParallel = new Option<int>("--parallel") { DefaultValueFactory = _ => 1, Description = "并发上传的文件夹数 (同步上传, 该参数已忽略, 固定为 1)" };
    var optInclude = new Option<string?>("--include") { Description = "白名单扩展名 (逗号分隔)" };
    var optExclude = new Option<string?>("--exclude") { Description = "黑名单扩展名 (逗号分隔, 默认排除 _meta.json)" };
    var optSkipMeta = new Option<bool>("--skip-meta") { DefaultValueFactory = _ => false, Description = "不上传 _meta.json" };
    var optRm = new Option<bool>("--rm") { DefaultValueFactory = _ => false, Description = "上传成功后删除源文件夹" };
    var optDryRun = new Option<bool>("--dry-run") { DefaultValueFactory = _ => false, Description = "只扫描, 不实际发送" };
    var optFrom = new Option<int?>("--from") { Description = "从第 N 个文件夹开始 (1-based)" };

    var root = new RootCommand("按子文件夹分组上传到 Telegram (TdClient + TdlEnv, 按 _meta.json Files 顺序)");
    root.Options.Add(optSrc);
    root.Options.Add(optChat);
    root.Options.Add(optTopic);
    root.Options.Add(optParallel);
    root.Options.Add(optInclude);
    root.Options.Add(optExclude);
    root.Options.Add(optSkipMeta);
    root.Options.Add(optRm);
    root.Options.Add(optDryRun);
    root.Options.Add(optFrom);

    if (args.Contains("--help") || args.Contains("-h"))
    {
        AnsiConsole.WriteLine("用法: dotnet run src/tdl/TdlUploadByFolder.cs -- --src <DIR> [options]");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("选项:");
        foreach (var o in root.Options)
        {
            var name = $"--{o.Name}";
            if (o.Aliases.Any()) name += $"/{string.Join(",", o.Aliases)}";
            AnsiConsole.MarkupLine($"  [cyan]{name,-30}[/] {o.Description}");
        }
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]示例:[/]");
        AnsiConsole.MarkupLine("  [grey]--src f:/Code/Github/script/data/tdl/by-start --chat https://t.me/+XXX --dry-run[/]");
        AnsiConsole.MarkupLine("  [grey]--src f:/Code/Github/script/data/tdl/by-start --chat https://t.me/+95j9-uLbOj1mYTM1[/]");
        return;
    }

    var pr = root.Parse(args);

    var src = Path.GetFullPath(pr.GetValue(optSrc)!);
    var chatLink = pr.GetValue(optChat);
    var topicId = pr.GetValue(optTopic);
    var parallel = pr.GetValue(optParallel); // 同步上传, 此参数已忽略
    var includeExts = pr.GetValue(optInclude);
    var excludeExts = pr.GetValue(optExclude);
    var skipMeta = pr.GetValue(optSkipMeta);
    var rmAfter = pr.GetValue(optRm);
    var dryRun = pr.GetValue(optDryRun);
    var fromIdx = pr.GetValue(optFrom);

    if (!Directory.Exists(src))
    {
        AnsiConsole.MarkupLine($"[red]源目录不存在: {src}[/]");
        Environment.Exit(1);
        return;
    }

    HashSet<string>? includeSet = ParseExtSet(includeExts);
    HashSet<string> excludeSet;
    if (excludeExts == null && includeSet == null)
        excludeSet = new HashSet<string> { "json" };
    else
        excludeSet = ParseExtSet(excludeExts) ?? new HashSet<string>();

    try
    {
        var folders = Directory.GetDirectories(src)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AnsiConsole.MarkupLine($"[cyan]源:[/] {src}");
        AnsiConsole.MarkupLine($"子文件夹: [yellow]{folders.Count}[/] 个");

        int startIdx = (fromIdx ?? 1) - 1;
        if (startIdx > 0 && startIdx < folders.Count)
        {
            folders = folders.Skip(startIdx).ToList();
            AnsiConsole.MarkupLine($"从第 [yellow]{startIdx + 1}[/] 个开始, 剩余 [yellow]{folders.Count}[/] 个");
        }

        if (dryRun)
        {
            AnsiConsole.MarkupLine("[yellow]--dry-run: 列出每个文件夹的计划 (不需要登录 Telegram)[/]");
            AnsiConsole.MarkupLine("[grey]顺序来源: _meta.json 的 Files 数组 (按 MessageId)[/]");
            int totalPhoto = 0, totalVideo = 0, totalOther = 0, totalMeta = 0, totalHeader = 0;
            int shown = 0;
            foreach (var folder in folders)
            {
                var metaPath = Path.Combine(folder, "_meta.json");
                var plan = BuildFolderPlan(folder, metaPath, includeSet, excludeSet);
                var meta = !skipMeta && File.Exists(metaPath) ? 1 : 0;
                if (plan.PhotoCount == 0 && plan.VideoCount == 0 && plan.OtherCount == 0 && meta == 0) continue;
                totalPhoto += plan.PhotoCount;
                totalVideo += plan.VideoCount;
                totalOther += plan.OtherCount;
                totalMeta += meta;
                totalHeader++;
                if (shown < 30 || shown % 50 == 0)
                {
                    AnsiConsole.MarkupLine($"  [grey]{Path.GetFileName(folder)}[/]  photo=[green]{plan.PhotoCount}[/] video=[green]{plan.VideoCount}[/] other=[grey]{plan.OtherCount}[/] meta=[grey]{meta}[/]  idx=[yellow]{plan.Idx}[/]");
                    shown++;
                }
            }
            if (totalHeader > shown)
                AnsiConsole.MarkupLine($"  [grey]... 还有 {totalHeader - shown} 个未逐行显示[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[yellow]DRY 合计:[/]");
            AnsiConsole.MarkupLine($"  待处理文件夹: [green]{totalHeader}[/]");
            AnsiConsole.MarkupLine($"  预计 header 消息: [green]{totalHeader}[/] 条");
            AnsiConsole.MarkupLine($"  预计 photo album 数: 最多 [green]{(totalPhoto + 9) / 10}[/] 条 (每 album ≤10)");
            AnsiConsole.MarkupLine($"  预计 video 消息数: 最多 [green]{(totalVideo + 9) / 10}[/] 条");
            AnsiConsole.MarkupLine($"  预计 _meta.json 文档: [green]{totalMeta}[/] 条");
            AnsiConsole.MarkupLine($"  预计 other 文档: [green]{totalOther}[/] 条");
            AnsiConsole.MarkupLine($"  [bold]总消息数: 约 {totalHeader + (totalPhoto + 9) / 10 + (totalVideo + 9) / 10 + totalMeta + totalOther} 条[/]");
            return;
        }

        var env = new TdlEnv(client, logger);
        env.WaitReady();
        if (env.AuthNeeded) await env.AuthenticateAsync();

        var me = await env.GetCurrentUserAsync();
        var myUserName = me.Usernames?.ActiveUsernames?.FirstOrDefault() ?? "";
        AnsiConsole.MarkupLine($"[green]已登录[/] {me.Id} @{myUserName.EscapeMarkup()}");

        long chatId = await env.ResolveChatIdAsync(chatLink);
        if (chatId == 0)
        {
            chatId = me.Id;
            AnsiConsole.MarkupLine($"[grey]未指定 --chat, 发到收藏夹 ({chatId})[/]");
        }

        string chatTitle = $"chat_{chatId}";
        try
        {
            var chat = await client.ExecuteAsync(new TdApi.GetChat { ChatId = chatId });
            chatTitle = chat.Title ?? chatTitle;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]getChat 失败: {ex.Message.EscapeMarkup()}[/]");
        }
        AnsiConsole.MarkupLine($"[green]目标:[/] {chatTitle.EscapeMarkup()} ({chatId})");

        var totalSent = 0;
        var totalFailed = 0;
        var totalFolders = 0;
        var swTotal = Stopwatch.StartNew();

        await AnsiConsole.Progress()
            .AutoClear(false)
            .AutoRefresh(true)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn { Alignment = Justify.Left },
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn())
            .StartAsync(async ctx =>
            {
                var overall = ctx.AddTask($"[green]全部 {folders.Count} 个文件夹[/]", maxValue: folders.Count);

                // ─────────────────────────────────────────────────────
                //  同步按文件夹分组上传:
                //    1. 串行遍历 folders, 一个文件夹完全发完才进下一个
                //    2. UploadOneFolderAsync 内部也是逐条 await ExecuteAsync,
                //       每条发送请求都等 TDLib 返回后才发下一条
                //    3. 不并发, 避免 Telegram 端消息顺序错乱 / FloodWait 升级
                // ─────────────────────────────────────────────────────
                int myIdx = 0;
                foreach (var folder in folders)
                {
                    myIdx++;
                    var task = ctx.AddTask($"[cyan]#{myIdx} {Path.GetFileName(folder)}[/]");
                    try
                    {
                        var (sent, failed) = await UploadOneFolderAsync(client, chatId, topicId, folder, includeSet, excludeSet, skipMeta, task, logger);
                        totalSent += sent;
                        totalFailed += failed;
                        totalFolders++;
                        task.Description = $"[green]✓[/] [cyan]#{myIdx} {Path.GetFileName(folder)}[/]";

                        if (rmAfter && failed == 0)
                        {
                            try { Directory.Delete(folder, recursive: true); }
                            catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]删除失败: {folder.EscapeMarkup()} ({ex.Message.EscapeMarkup()})[/]"); }
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]文件夹失败: {folder.EscapeMarkup()}[/]  {ex.Message.EscapeMarkup()}");
                        totalFailed++;
                        task.Description = $"[red]✗[/] [cyan]#{myIdx} {Path.GetFileName(folder)}[/]";
                    }
                    finally
                    {
                        task.Value = task.MaxValue;
                        overall.Increment(1);
                    }
                }
            });

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold green]完成[/]").LeftJustified());
        AnsiConsole.MarkupLine($"目标:        [cyan]{chatTitle.EscapeMarkup()}[/] ({chatId})");
        AnsiConsole.MarkupLine($"完成文件夹:  [green]{totalFolders}[/] / {folders.Count}");
        AnsiConsole.MarkupLine($"消息总数:    [green]{totalSent}[/] 条, 失败 [red]{totalFailed}[/]");
        AnsiConsole.MarkupLine($"总耗时:      [yellow]{swTotal.Elapsed:hh\\:mm\\:ss}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("按 ENTER 退出");
        Console.ReadLine();
    }
    catch (Exception ex)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold red]异常退出[/]").LeftJustified());
        AnsiConsole.MarkupLine($"[red]{ex.GetType().Name}:[/] {ex.Message.EscapeMarkup()}");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteException(ex);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("按 ENTER 退出");
        Console.ReadLine();
        Environment.Exit(1);
    }
}

// ─────────────────────────────────────────────────────────────
//  单文件夹上传
// ─────────────────────────────────────────────────────────────

async Task<(int Sent, int Failed)> UploadOneFolderAsync(
    TdClient client, long chatId, long? topicId, string folder,
    HashSet<string>? includeSet, HashSet<string>? excludeSet, bool skipMeta,
    ProgressTask task, ILogger logger)
{
    var folderName = Path.GetFileName(folder);
    var metaPath = Path.Combine(folder, "_meta.json");
    var plan = BuildFolderPlan(folder, metaPath, includeSet, excludeSet);
    var (idx, itemCount, startDate) = (plan.Idx, plan.ItemCount, plan.StartDate);
    var photoEntries = plan.PhotoFiles;
    var videoEntries = plan.VideoFiles;
    var otherEntries = plan.OtherFiles;
    task.MaxValue = 100;

    if (photoEntries.Count == 0 && videoEntries.Count == 0 && otherEntries.Count == 0 && (skipMeta || !File.Exists(metaPath)))
    {
        task.Value = 100;
        return (0, 0);
    }

    var sent = 0;
    var failed = 0;

    LogPlan(folderName, plan);

    // 1. Header
    try
    {
        var headerText = $"📂 #{idx} {folderName}\n起始: {startDate} | 文件: {itemCount} | 图片: {photoEntries.Count} | 视频: {videoEntries.Count}";
        AnsiConsole.MarkupLine($"[cyan]→[/] [grey]header[/] {headerText.Replace("\n", " | ").EscapeMarkup()}");
        await client.ExecuteAsync(new TdApi.SendMessage
        {
            ChatId = chatId,
            InputMessageContent = new TdApi.InputMessageContent.InputMessageText
            {
                Text = new TdApi.FormattedText { Text = headerText }
            }
        });
        sent++;
        task.Value = 15;
    }
    catch (Exception ex)
    {
        failed++;
        task.Value = 100;
        throw new Exception($"header 失败: {ex.Message}", ex);
    }

    // 2. Photo albums (≤10 each)
    var photoAlbums = SplitIntoAlbums(photoEntries, 10);
    for (int i = 0; i < photoAlbums.Count; i++)
    {
        try
        {
            var album = photoAlbums[i];
            var names = string.Join(", ", album.Select(e => Path.GetFileName(e.Path)));
            AnsiConsole.MarkupLine($"[cyan]→[/] [grey]photo album {i + 1}/{photoAlbums.Count}[/] ({album.Count}) {names.EscapeMarkup()}");
            var inputs = album.Select(e => new TdApi.InputMessageContent.InputMessagePhoto
            {
                Photo = new TdApi.InputPhoto
                {
                    Photo = new TdApi.InputFile.InputFileLocal { Path = e.Path }
                },
                Caption = (i == 0) ? new TdApi.FormattedText { Text = $"#{idx} {folderName}" } : null
            }).ToArray();
            await client.ExecuteAsync(new TdApi.SendMessageAlbum
            {
                ChatId = chatId,
                InputMessageContents = inputs
            });
            sent++;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]photo album {i + 1}/{photoAlbums.Count} 失败: {folderName.EscapeMarkup()}[/]  {ex.Message.EscapeMarkup()}");
            failed++;
        }
        task.Value = 15 + (int)(60.0 * (i + 1) / Math.Max(photoAlbums.Count, 1));
    }

    // 3. Videos
    var videoAlbums = SplitIntoAlbums(videoEntries, 10);
    for (int i = 0; i < videoAlbums.Count; i++)
    {
        try
        {
            var album = videoAlbums[i];
            var names = string.Join(", ", album.Select(e => Path.GetFileName(e.Path)));
            if (album.Count == 1)
            {
                AnsiConsole.MarkupLine($"[cyan]→[/] [grey]video {i + 1}/{videoAlbums.Count}[/] {names.EscapeMarkup()}");
                await client.ExecuteAsync(new TdApi.SendMessage
                {
                    ChatId = chatId,
                    InputMessageContent = new TdApi.InputMessageContent.InputMessageVideo
                    {
                        Video = new TdApi.InputVideo
                        {
                            Video = new TdApi.InputFile.InputFileLocal { Path = album[0].Path }
                        },
                        Caption = new TdApi.FormattedText { Text = $"#{idx} {folderName}" }
                    }
                });
            }
            else
            {
                AnsiConsole.MarkupLine($"[cyan]→[/] [grey]video album {i + 1}/{videoAlbums.Count}[/] ({album.Count}) {names.EscapeMarkup()}");
                var inputs = album.Select(e => new TdApi.InputMessageContent.InputMessageVideo
                {
                    Video = new TdApi.InputVideo
                    {
                        Video = new TdApi.InputFile.InputFileLocal { Path = e.Path }
                    },
                    Caption = null
                }).ToArray();
                await client.ExecuteAsync(new TdApi.SendMessageAlbum
                {
                    ChatId = chatId,
                    InputMessageContents = inputs
                });
            }
            sent++;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]video {i + 1}/{videoAlbums.Count} 失败: {folderName.EscapeMarkup()}[/]  {ex.Message.EscapeMarkup()}");
            failed++;
        }
        task.Value = 75 + (int)(20.0 * (i + 1) / Math.Max(videoAlbums.Count, 1));
    }

    // 4. Other
    foreach (var e in otherEntries)
    {
        try
        {
            AnsiConsole.MarkupLine($"[cyan]→[/] [grey]other[/] {Path.GetFileName(e.Path).EscapeMarkup()}");
            await client.ExecuteAsync(new TdApi.SendMessage
            {
                ChatId = chatId,
                InputMessageContent = new TdApi.InputMessageContent.InputMessageDocument
                {
                    Document = new TdApi.InputDocument
                    {
                        Document = new TdApi.InputFile.InputFileLocal { Path = e.Path }
                    },
                    Caption = new TdApi.FormattedText { Text = $"#{idx} {folderName}" }
                }
            });
            sent++;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]other 失败: {Path.GetFileName(e.Path).EscapeMarkup()}[/]  {ex.Message.EscapeMarkup()}");
            failed++;
        }
    }

    // 5. _meta.json
    if (!skipMeta && File.Exists(metaPath))
    {
        try
        {
            AnsiConsole.MarkupLine($"[cyan]→[/] [grey]meta[/] _meta.json");
            await client.ExecuteAsync(new TdApi.SendMessage
            {
                ChatId = chatId,
                InputMessageContent = new TdApi.InputMessageContent.InputMessageDocument
                {
                    Document = new TdApi.InputDocument
                    {
                        Document = new TdApi.InputFile.InputFileLocal { Path = metaPath }
                    },
                    Caption = new TdApi.FormattedText { Text = $"{folderName} meta" }
                }
            });
            sent++;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]meta 失败: {folderName.EscapeMarkup()}[/]  {ex.Message.EscapeMarkup()}");
            failed++;
        }
    }

    task.Value = 100;
    return (sent, failed);
}

void LogPlan(string folderName, FolderPlan plan)
{
    AnsiConsole.MarkupLine($"[blue]┌─ {folderName.EscapeMarkup()} idx=[yellow]{plan.Idx}[/] itemCount=[yellow]{plan.ItemCount}[/] start=[grey]{plan.StartDate.EscapeMarkup()}[/][/]");
    foreach (var e in plan.PhotoFiles)
        AnsiConsole.MarkupLine($"[blue]│[/]  [green]photo[/]  msg=[yellow]{e.MessageId}[/] src=[grey]{e.Source}[/]  [grey]{Path.GetFileName(e.Path).EscapeMarkup()}[/]");
    foreach (var e in plan.VideoFiles)
        AnsiConsole.MarkupLine($"[blue]│[/]  [red]video[/]  msg=[yellow]{e.MessageId}[/] src=[grey]{e.Source}[/]  [grey]{Path.GetFileName(e.Path).EscapeMarkup()}[/]");
    foreach (var e in plan.OtherFiles)
        AnsiConsole.MarkupLine($"[blue]│[/]  [grey]other[/]  msg=[yellow]{e.MessageId}[/] src=[grey]{e.Source}[/]  [grey]{Path.GetFileName(e.Path).EscapeMarkup()}[/]");
    AnsiConsole.MarkupLine($"[blue]└─[/] plan total: photo=[green]{plan.PhotoCount}[/] video=[green]{plan.VideoCount}[/] other=[grey]{plan.OtherCount}[/]");
}

bool IsPhoto(string p)
{
    var ext = Path.GetExtension(p).ToLowerInvariant();
    return ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".gif";
}

bool IsVideo(string p)
{
    var ext = Path.GetExtension(p).ToLowerInvariant();
    return ext is ".mp4" or ".mov" or ".webm" or ".mkv" or ".avi";
}

List<List<T>> SplitIntoAlbums<T>(List<T> items, int size)
{
    var result = new List<List<T>>();
    for (int i = 0; i < items.Count; i += size)
        result.Add(items.Skip(i).Take(size).ToList());
    return result;
}

bool MatchesIncludeExclude(string file, HashSet<string>? includeSet, HashSet<string>? excludeSet)
{
    if ((includeSet == null || includeSet.Count == 0) && (excludeSet == null || excludeSet.Count == 0))
        return true;
    var ext = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
    if (excludeSet != null && excludeSet.Contains(ext)) return false;
    if (includeSet != null && includeSet.Count > 0 && !includeSet.Contains(ext)) return false;
    return true;
}

HashSet<string>? ParseExtSet(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return null;
    return raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(e => e.Trim().TrimStart('.').ToLowerInvariant())
        .Where(e => e.Length > 0)
        .ToHashSet();
}

(int Idx, int Cnt, string Start) TryReadMeta(string path)
{
    try
    {
        if (!File.Exists(path)) return (0, 0, "");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var idx = root.TryGetProperty("Index", out var i) ? i.GetInt32() : 0;
        var cnt = root.TryGetProperty("ItemCount", out var c) ? c.GetInt32() : 0;
        var sd = root.TryGetProperty("StartDate", out var s) ? s.GetString() ?? "" : "";
        return (idx, cnt, sd);
    }
    catch { return (0, 0, ""); }
}

FolderPlan BuildFolderPlan(string folder, string metaPath, HashSet<string>? includeSet, HashSet<string>? excludeSet)
{
    var (idx, cnt, sd) = TryReadMeta(metaPath);

    var ordered = new List<FolderPlanEntry>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    if (File.Exists(metaPath))
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            if (doc.RootElement.TryGetProperty("Files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var fe in filesEl.EnumerateArray())
                {
                    var rel = fe.TryGetProperty("NewPath", out var np) ? np.GetString() : null;
                    if (string.IsNullOrWhiteSpace(rel)) continue;

                    string full;
                    try { full = Path.IsPathRooted(rel) ? rel : Path.Combine(Path.GetDirectoryName(metaPath)!, "..", rel.Replace('/', Path.DirectorySeparatorChar)); }
                    catch { continue; }
                    full = Path.GetFullPath(full);

                    if (!File.Exists(full)) continue;
                    if (!MatchesIncludeExclude(full, includeSet, excludeSet)) continue;
                    if (!seen.Add(full)) continue;

                    var type = fe.TryGetProperty("Type", out var t) ? t.GetString() : null;
                    var kind = type?.ToLowerInvariant() switch
                    {
                        "photo" => "photo",
                        "video" or "animation" or "videonote" => "video",
                        _ => "other"
                    };
                    var msgId = fe.TryGetProperty("MessageId", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt64() : 0;
                    ordered.Add(new FolderPlanEntry(full, kind, msgId, "meta"));
                }
            }
        }
        catch { }
    }

    if (ordered.Count == 0)
    {
        foreach (var p in Directory.GetFiles(folder)
            .Where(p => !string.Equals(Path.GetFileName(p), "_meta.json", StringComparison.OrdinalIgnoreCase))
            .Where(p => MatchesIncludeExclude(p, includeSet, excludeSet))
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
        {
            if (!seen.Add(p)) continue;
            var kind = IsPhoto(p) ? "photo" : IsVideo(p) ? "video" : "other";
            ordered.Add(new FolderPlanEntry(p, kind, 0, "disk"));
        }
    }

    var photos = ordered.Where(x => x.Kind == "photo").ToList();
    var videos = ordered.Where(x => x.Kind == "video").ToList();
    var others = ordered.Where(x => x.Kind == "other").ToList();

    return new FolderPlan(idx, cnt, sd, photos, videos, others);
}

record FolderPlanEntry(string Path, string Kind, long MessageId, string Source);

record FolderPlan(
    int Idx,
    int ItemCount,
    string StartDate,
    List<FolderPlanEntry> PhotoFiles,
    List<FolderPlanEntry> VideoFiles,
    List<FolderPlanEntry> OtherFiles)
{
    public int PhotoCount => PhotoFiles.Count;
    public int VideoCount => VideoFiles.Count;
    public int OtherCount => OtherFiles.Count;
}
