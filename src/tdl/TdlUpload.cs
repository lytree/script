#!/usr/bin/env dotnet

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
using Framework.ZLogging;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using TdLib;
using TdLib.Bindings;
using ZLogger;

using (var client = new TdClient())
{
    client.Bindings.SetLogVerbosityLevel(TdLogLevel.Fatal);
    await Main(client, args);
}

async Task Main(TdClient client, string[] args)
{
    var logger = TdlEnv.CreateLogger("tdl-upload.log", "tdl-upload");

    var optionPath = new Option<string[]>("--path") { Required = true, Description = "文件或目录路径 (可多次指定)" };
    var optionChat = new Option<string?>("--chat") { Required = false, Description = "目标聊天 (默认: 收藏夹)" };
    var optionTopic = new Option<long?>("--topic") { Required = false, Description = "论坛主题 ID" };
    var optionThreads = new Option<int>("--threads") { DefaultValueFactory = _ => 8, Description = "每任务线程数" };
    var optionLimit = new Option<int>("--limit") { DefaultValueFactory = _ => 4, Description = "并发任务数" };
    var optionCaption = new Option<string?>("--caption") { Required = false, Description = "自定义标题" };
    var optionInclude = new Option<string?>("--include") { Required = false, Description = "白名单扩展名 (逗号分隔, 如 jpg,png)" };
    var optionExclude = new Option<string?>("--exclude") { Required = false, Description = "黑名单扩展名 (逗号分隔, 如 mp4,flv)" };
    var optionRm = new Option<bool>("--rm") { DefaultValueFactory = _ => false, Description = "上传成功后删除文件" };
    var optionPhoto = new Option<bool>("--photo") { DefaultValueFactory = _ => false, Description = "将图片作为照片发送" };

    var rootCommand = new RootCommand("上传文件到 Telegram");
    rootCommand.Options.Add(optionPath);
    rootCommand.Options.Add(optionChat);
    rootCommand.Options.Add(optionTopic);
    rootCommand.Options.Add(optionThreads);
    rootCommand.Options.Add(optionLimit);
    rootCommand.Options.Add(optionCaption);
    rootCommand.Options.Add(optionInclude);
    rootCommand.Options.Add(optionExclude);
    rootCommand.Options.Add(optionRm);
    rootCommand.Options.Add(optionPhoto);

    var parseResult = rootCommand.Parse(args);
    var paths = parseResult.GetValue(optionPath);
    var chatLink = parseResult.GetValue(optionChat);
    var topicId = parseResult.GetValue(optionTopic);
    var threads = parseResult.GetValue(optionThreads);
    var concurrency = parseResult.GetValue(optionLimit);
    var caption = parseResult.GetValue(optionCaption);
    var includeExts = parseResult.GetValue(optionInclude);
    var excludeExts = parseResult.GetValue(optionExclude);
    var rmAfterUpload = parseResult.GetValue(optionRm);
    var asPhoto = parseResult.GetValue(optionPhoto);

    var env = new TdlEnv(client, logger, onFileUpdate: HandleFileUpdate);
    env.WaitReady();

    if (env.AuthNeeded)
    {
        await env.AuthenticateAsync();
    }

    var currentUser = await env.GetCurrentUserAsync();
    var fullUserName = $"{currentUser.FirstName} {currentUser.LastName}".Trim();
    logger.ZLogInformation($"成功登录为 [[{currentUser.Id}]] / [[@{currentUser.Usernames?.ActiveUsernames[0]}]] / [[{fullUserName}]]");

    long chatId = await env.ResolveChatIdAsync(chatLink);
    if (chatId == 0)
    {
        chatId = currentUser.Id;
        logger.ZLogInformation($"未指定目标聊天，默认使用收藏夹 (ChatId={chatId})");
    }

    var chat = await client.GetChatAsync(chatId);
    logger.ZLogInformation($"目标: [{chat.Title}] ChatId={chatId}");

    var files = CollectFiles(paths, includeExts, excludeExts, logger);
    if (files.Count == 0)
    {
        logger.ZLogWarning($"未找到符合条件的文件");
        return;
    }

    logger.ZLogInformation($"共 {files.Count} 个文件待上传");

    int uploaded = 0;
    int failed = 0;

    await AnsiConsole.Progress()
        .AutoRefresh(true)
        .AutoClear(false)
        .Columns(
            new TaskDescriptionColumn(),
            new ProgressBarColumn(),
            new PercentageColumn(),
            new TransferSpeedColumn(),
            new RemainingTimeColumn())
        .StartAsync(async ctx =>
        {
            var tasks = files.Select(f => ctx.AddTask($"[cyan]{Path.GetFileName(f)}[/]")).ToList();
            var semaphore = new SemaphoreSlim(concurrency);

            var uploadTasks = files.Select(async (file, index) =>
            {
                await semaphore.WaitAsync();
                var task = tasks[index];
                try
                {
                    await UploadFileAsync(client, chatId, topicId, file, caption, asPhoto, threads, task, logger);
                    task.Description = $"[green]✓[/] [cyan]{Path.GetFileName(file)}[/]";
                    task.Value = task.MaxValue;
                    Interlocked.Increment(ref uploaded);

                    if (rmAfterUpload)
                    {
                        try { File.Delete(file); }
                        catch (Exception ex) { logger.ZLogWarning(ex, $"删除文件失败: {file}"); }
                    }
                }
                catch (Exception ex)
                {
                    logger.ZLogError(ex, $"上传失败: {file}");
                    task.Description = $"[red]✗[/] [cyan]{Path.GetFileName(file)}[/]";
                    Interlocked.Increment(ref failed);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(uploadTasks);
        });

    AnsiConsole.WriteLine();
    AnsiConsole.WriteLine("==================");
    AnsiConsole.WriteLine($"上传完成！成功: {uploaded}, 失败: {failed}");
    AnsiConsole.WriteLine("==================");

    Console.WriteLine("按 ENTER 键退出");
    Console.ReadLine();
}

List<string> CollectFiles(string[] paths, string? includeExts, string? excludeExts, ILogger logger)
{
    var files = new HashSet<string>();
    var includeSet = includeExts?.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(e => e.Trim().ToLowerInvariant()).ToHashSet();
    var excludeSet = excludeExts?.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(e => e.Trim().ToLowerInvariant()).ToHashSet();

    foreach (var p in paths)
    {
        if (File.Exists(p))
        {
            if (ShouldInclude(p, includeSet, excludeSet))
                files.Add(Path.GetFullPath(p));
        }
        else if (Directory.Exists(p))
        {
            foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
            {
                if (ShouldInclude(f, includeSet, excludeSet))
                    files.Add(Path.GetFullPath(f));
            }
        }
        else
        {
            logger.ZLogWarning($"路径不存在: {p}");
        }
    }

    return files.OrderBy(f => f).ToList();
}

bool ShouldInclude(string file, HashSet<string>? includeSet, HashSet<string>? excludeSet)
{
    var ext = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
    if (excludeSet != null && excludeSet.Contains(ext)) return false;
    if (includeSet != null && !includeSet.Contains(ext)) return false;
    return true;
}

async Task UploadFileAsync(TdClient client, long chatId, long? topicId, string filePath, string? caption, bool asPhoto, int threads, ProgressTask task, ILogger logger)
{
    var fileName = Path.GetFileName(filePath);
    var fileInfo = new FileInfo(filePath);
    task.MaxValue = fileInfo.Length;

    var inputFile = await client.ExecuteAsync(new TdApi.ReadFile
    {
        Path = filePath
    });

    TdApi.InputMessageContent content;
    if (asPhoto && IsImageFile(filePath))
    {
        content = new TdApi.InputMessageContent.InputMessagePhoto
        {
            Photo = new TdApi.InputFile.InputFileId { Id = inputFile.Id },
            Thumbnail = null,
            AddedStickerFileIds = null,
            Width = 0,
            Height = 0,
            Caption = BuildFormattedText(caption ?? fileName),
            SelfDestructType = null,
            HasSpoiler = false
        };
    }
    else
    {
        content = new TdApi.InputMessageContent.InputMessageDocument
        {
            Document = new TdApi.InputFile.InputFileId { Id = inputFile.Id },
            Thumbnail = null,
            DisableContentTypeDetection = false,
            Caption = BuildFormattedText(caption ?? fileName)
        };
    }

    var sendArgs = new TdApi.SendMessageArgs
    {
        ChatId = chatId,
        MessageThreadId = topicId ?? 0,
        ReplyTo = null,
        Options = null,
        ReplyMarkup = null,
        InputMessageContent = content
    };

    var result = await client.SendMessageAsync(sendArgs);
    task.Value = task.MaxValue;
    logger.ZLogInformation($"已上传: {fileName} -> MsgId={result.Id}");
}

TdApi.FormattedText BuildFormattedText(string text)
{
    return new TdApi.FormattedText { Text = text, Entities = null };
}

bool IsImageFile(string filePath)
{
    var ext = Path.GetExtension(filePath).ToLowerInvariant();
    return ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp";
}

Task HandleFileUpdate(TdApi.File file, string outputPath, ILogger cbLogger)
{
    if (file.Local.IsDownloadingActive)
    {
        double percent = (double)file.Local.DownloadedSize / file.ExpectedSize * 100;
        cbLogger.ZLogTrace($"文件 {file.Id} 上传进度: {percent:F1}%");
    }
    else if (file.Local.IsDownloadingCompleted)
    {
        cbLogger.ZLogInformation($"文件上传完成！本地路径: {file.Local.Path}");
    }
    return Task.CompletedTask;
}
