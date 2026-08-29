#!/usr/bin/env dotnet

#:include ../../env.cs
#:include TdlUpdateHandler.cs
#:include TdlEnv.cs
#:include TdlDownloadTracker.cs

#:package TDLib@*
#:package tdlib.native@*
#:package tdlib.native.win-x64@*
#:package System.CommandLine@*
#:package Spectre.Console@0.55.2
#:package Spectre.Console.Ansi@0.55.2
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

string tdlRoot = string.Empty;
DownloadTracker _downloadTracker = new();
HashSet<int> _downloadedFileIds = [];
Dictionary<int, long> _fileIdToAlbumId = [];
TdlEnv _env = null!;

async Task Main(TdClient client, string[] args)
{
    var logger = TdlEnv.CreateLogger("tdl-group-download.log", "tdl-group-download");

    var optionOutput = new Option<string?>("--output") { DefaultValueFactory = (res) => Path.Combine(Path.EntryPointFileDirectoryPath(), "data") };
    var optionLink = new Option<string[]>("--link") { Required = true, DefaultValueFactory = (res) => ["https://t.me/xqxayjrl/695792"] };
    var optionIncludeComments = new Option<bool>("--include-comments") { DefaultValueFactory = (res) => true };
    var rootCommand = new RootCommand { optionOutput, optionLink, optionIncludeComments };
    var parseResult = rootCommand.Parse(args);
    var outputPath = parseResult.GetValue(optionOutput);
    var links = parseResult.GetValue(optionLink);
    var includeComments = parseResult.GetValue(optionIncludeComments);

    tdlRoot = TdlEnv.InitTdlRoot(logger);

    _env = new TdlEnv(client, logger, filesDir: outputPath, onFileUpdate: HandleFileUpdate);
    _env.WaitReady();

    if (_env.AuthNeeded)
    {
        await _env.AuthenticateAsync();
    }

    var currentUser = await _env.GetCurrentUserAsync();
    var fullUserName = $"{currentUser.FirstName} {currentUser.LastName}".Trim();
    logger.ZLogInformation($"成功登录为 [[{currentUser.Id}]] / [[@{currentUser.Usernames?.ActiveUsernames[0]}]] / [[{fullUserName}]]");

    foreach (var link in links)
    {
        logger.ZLogInformation($"开始处理链接: {link}");
        await DownloadMediaFromLink(client, link, includeComments, outputPath, logger);
    }

    logger.ZLogInformation($"等待所有下载完成...");
    await Task.Delay(10000);

    int completedCount = _downloadTracker.GetCompletedCount();
    int queuedCount = _downloadedFileIds.Count;
    AnsiConsole.WriteLine();
    AnsiConsole.WriteLine("==================");
    AnsiConsole.WriteLine("全部下载完毕！");
    AnsiConsole.WriteLine("已下载文件数: " + queuedCount);
    AnsiConsole.WriteLine("==================");

    AnsiConsole.WriteLine("按 ENTER 键退出应用");
    Console.ReadLine();
}

async Task DownloadMediaFromLink(TdClient client, string link, bool includeComments, string outputPath, ILogger logger)
{
    try
    {
        var linkInfo = await client.GetMessageLinkInfoAsync(link);
        if (linkInfo.Message == null)
        {
            logger.ZLogError($"无法从链接获取消息: {link}");
            return;
        }

        var chatId = linkInfo.Message.ChatId;
        var messageId = linkInfo.Message.Id;
        var chat = await client.GetChatAsync(chatId);

        logger.ZLogInformation($"开始下载 {chat.Title} 的媒体组...");
        logger.ZLogInformation($"包含评论: {includeComments}");

        int totalDownloaded = 0;
        var message = linkInfo.Message;

        if (message.MediaAlbumId != 0)
        {
            logger.ZLogInformation($"发现媒体组: {message.MediaAlbumId}");
            totalDownloaded += await DownloadMediaGroupByAlbumId(client, chatId, message.MediaAlbumId, messageId, outputPath, messageId, logger);
        }
        else
        {
            totalDownloaded += await DownloadMessageMedia(client, message, outputPath, messageId, logger);
        }

        if (includeComments)
        {
            logger.ZLogInformation($"开始下载评论区媒体...");
            var comments = await GetMessageCommentsAsync(client, chatId, messageId, logger);
            logger.ZLogInformation($"找到 {comments.Length} 条评论");

            int commentsDownloaded = 0;
            int commentsSkipped = 0;
            foreach (var comment in comments)
            {
                var fileId = TdlMediaHelper.GetFileIdFromMessage(comment);
                if (fileId > 0)
                {
                    if (_downloadedFileIds.Contains(fileId))
                    {
                        logger.ZLogInformation($"评论 {comment.Id} 的媒体(FileId: {fileId})已在主消息中下载，跳过");
                        commentsSkipped++;
                    }
                    else
                    {
                        logger.ZLogInformation($"评论 {comment.Id} 包含媒体，FileId: {fileId}");
                        commentsDownloaded += await DownloadMessageMedia(client, comment, outputPath, messageId, logger);
                    }
                }
            }

            logger.ZLogInformation($"评论区下载完成，共 {commentsDownloaded} 个新文件，{commentsSkipped} 个已跳过");
            totalDownloaded += commentsDownloaded;
        }

        logger.ZLogInformation($"下载完成！共下载 {totalDownloaded} 个媒体文件");
    }
    catch (Exception e)
    {
        logger.LogError(e, "下载过程中发生错误");
    }
}

async Task<int> DownloadMediaGroupByAlbumId(TdClient client, long chatId, long mediaAlbumId, long startMessageId, string outputPath, long messageId, ILogger logger)
{
    int totalDownloaded = 0;

    try
    {
        logger.ZLogInformation($"开始下载媒体组 {mediaAlbumId}");

        var foundMessages = new List<TdApi.Message>();

        try
        {
            var firstMessage = await client.GetMessageAsync(chatId, startMessageId);
            if (firstMessage != null && !foundMessages.Any(m => m.Id == firstMessage.Id))
            {
                foundMessages.Add(firstMessage);
                logger.ZLogInformation($"添加初始消息: {firstMessage.Id}, MediaAlbumId: {firstMessage.MediaAlbumId}");
            }
        }
        catch (Exception ex)
        {
            logger.ZLogWarning(ex, $"获取初始消息失败");
        }

        long searchBackwardId = startMessageId;
        int backwardAttempts = 0;
        while (backwardAttempts < 5)
        {
            try
            {
                var messages = await client.GetChatHistoryAsync(chatId, searchBackwardId, 0, 50, false);
                if (messages.Messages_ == null || messages.Messages_.Length == 0)
                {
                    break;
                }

                bool foundMore = false;
                foreach (var msg in messages.Messages_)
                {
                    if (msg.MediaAlbumId == mediaAlbumId && !foundMessages.Any(m => m.Id == msg.Id))
                    {
                        foundMessages.Add(msg);
                        foundMore = true;
                        logger.ZLogInformation($"找到媒体组消息: {msg.Id}, MediaAlbumId: {msg.MediaAlbumId}");
                    }
                    searchBackwardId = msg.Id;
                }

                if (!foundMore)
                {
                    backwardAttempts++;
                }
                else
                {
                    backwardAttempts = 0;
                }

                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                logger.ZLogWarning(ex, $"搜索媒体组消息失败");
                break;
            }
        }

        try
        {
            var initialMessages = await client.GetChatHistoryAsync(chatId, startMessageId, -20, 40, false);
            if (initialMessages.Messages_ != null && initialMessages.Messages_.Length > 0)
            {
                foreach (var msg in initialMessages.Messages_)
                {
                    if (msg.MediaAlbumId == mediaAlbumId && !foundMessages.Any(m => m.Id == msg.Id))
                    {
                        foundMessages.Add(msg);
                        logger.ZLogInformation($"找到后续媒体组消息: {msg.Id}, MediaAlbumId: {msg.MediaAlbumId}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.ZLogWarning($"搜索后续媒体组消息失败: {ex.Message}");
        }

        logger.ZLogInformation($"媒体组搜索完成，共找到 {foundMessages.Count} 条消息");

        foreach (var msg in foundMessages.OrderBy(m => m.Id))
        {
            var count = await DownloadMessageMedia(client, msg, outputPath, messageId, logger);
            totalDownloaded += count;
        }

        logger.ZLogInformation($"媒体组 {mediaAlbumId} 下载完成，共 {totalDownloaded} 个文件");
    }
    catch (TdException ex)
    {
        logger.ZLogWarning(ex, $"下载媒体组失败");
    }

    return totalDownloaded;
}

async Task<int> DownloadMessageMedia(TdClient client, TdApi.Message message, string outputPath, long messageId, ILogger logger)
{
    int fileId = TdlMediaHelper.GetFileIdFromMessage(message);
    int downloadedCount = 0;

    if (fileId > 0 && !_downloadedFileIds.Contains(fileId))
    {
        _downloadedFileIds.Add(fileId);
        _fileIdToAlbumId[fileId] = messageId;
        await client.DownloadFileAsync(fileId, 32, 0, 0, true);
        downloadedCount++;
        logger.ZLogInformation($"队列下载: FileId: {fileId},LinkId: {messageId} , MediaAlbumId: {message.MediaAlbumId}");
    }

    return downloadedCount;
}

async Task<TdApi.Message[]> GetMessageCommentsAsync(TdClient client, long chatId, long messageId, ILogger logger)
{
    try
    {
        var threadInfo = await client.GetMessageThreadAsync(chatId, messageId);
        if (threadInfo == null || threadInfo.Messages == null)
        {
            return Array.Empty<TdApi.Message>();
        }
        var comments = await client.GetMessageThreadHistoryAsync(
            chatId: chatId,
            messageId: messageId,
            fromMessageId: 0,
            offset: 0,
            limit: 50
        );
        return comments.Messages_;
    }
    catch (TdException ex)
    {
        logger.ZLogWarning($"获取评论失败: {ex.Error.Message}");
        return Array.Empty<TdApi.Message>();
    }
}

async Task HandleFileUpdate(TdApi.File file, string outputPath, ILogger cbLogger)
{
    int fileId = file.Id;

    if (file.Local.IsDownloadingActive)
    {
        if (file.Local.DownloadedSize == 0)
        {
            _downloadTracker.StartDownload(fileId, fileId.ToString(), file.ExpectedSize);
        }
        else
        {
            _downloadTracker.UpdateProgress(fileId, fileId.ToString(), file.Size, file.Local.DownloadedSize);
        }
    }
    else if (file.Local.IsDownloadingCompleted)
    {
        _downloadTracker.CompleteDownload(fileId, fileId.ToString(), file.Size);
        TdlMediaHelper.OnDownloadFinished(file, _fileIdToAlbumId, outputPath, cbLogger);
    }
}

using (var client = new TdClient())
{
    client.Bindings.SetLogVerbosityLevel(TdLogLevel.Fatal);
    await Main(client, args);
}
