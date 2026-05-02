#!/usr/bin/env dotnet

#:include ../../env.cs
#:include TdlUpdateHandler.cs

#:package TDLib@*
#:package tdlib.native@*
#:package tdlib.native.win-x64@*
#:package System.CommandLine@*
#:package Spectre.Console@0.55.2
#:package Spectre.Console.Ansi@0.55.2
#:package Microsoft.Extensions.Logging@*
#:package ZLogger@*
#:package YLFramework.ZLogging@1.0.3-alpha.6


using System.CommandLine;
using Framework.ZLogging;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using TdLib;
using TdLib.Bindings;
using ZLogger;

// 全局变量
ManualResetEventSlim ReadyToAuthenticate = new();
string tdlRoot = string.Empty;
DownloadTracker _downloadTracker = new();
HashSet<int> _downloadedFileIds = new HashSet<int>();
TdlUpdateHandler _updateHandler;

// 主函数
async Task Main(TdClient client, string[] args)
{
    // 初始化日志
    var logger = InitializeLogger();

    // 解析命令行参数
    var optionOutput = new Option<string?>("--output") { DefaultValueFactory = (res) => Path.Combine(Path.EntryPointFileDirectoryPath(), "data") };
    var optionLink = new Option<string>("--link") { Required = true, DefaultValueFactory = (res) => "https://t.me/fancha103/5153" };
    var optionIncludeComments = new Option<bool>("--include-comments") { DefaultValueFactory = (res) => true };
    var rootCommand = new RootCommand { optionOutput, optionLink, optionIncludeComments };
    var parseResult = rootCommand.Parse(args);
    var outputPath = parseResult.GetValue(optionOutput);
    var link = parseResult.GetValue(optionLink);
    var includeComments = parseResult.GetValue(optionIncludeComments);

    // 初始化全局环境变量
    InitializeEnvironment(logger);

    // 下载文件
    await DownloadMediaFromLink(client, link, includeComments, outputPath, logger);

    // 等待所有下载完成
    logger.ZLogInformation($"等待所有下载完成...");
    await Task.Delay(10000); // 等待10秒让所有下载完成

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

// 初始化日志
ILogger InitializeLogger()
{
    var factory = LoggerFactory.Create(logging =>
    {
        logging.SetMinimumLevel(LogLevel.Information);
        logging.AddZLoggerSpectreConsoleAndFile("tdl-group-download.log");
    });
    return factory.CreateLogger("tdl-group-download");
}

// 初始化环境
void InitializeEnvironment(ILogger logger)
{
    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    tdlRoot = Path.Combine(userProfile, ".tdl");

    if (!Directory.Exists(tdlRoot))
    {
        Directory.CreateDirectory(tdlRoot);
        logger.ZLogInformation($"创建数据根目录: {tdlRoot}");
    }
}

// 从链接下载媒体
async Task DownloadMediaFromLink(TdClient client, string link, bool includeComments, string outputPath, ILogger logger)
{
    try
    {
        _updateHandler = new TdlUpdateHandler(ReadyToAuthenticate, logger)
            .OnConfigureTdlibParameters(ConfigureTdlibParameters)
            .OnFileUpdate(HandleFileUpdate);

        client.UpdateReceived += async (_, update) => { await _updateHandler.ProcessUpdates(client, update, outputPath); };
        ReadyToAuthenticate.Wait();

        if (_updateHandler.AuthNeeded)
        {
            await HandleAuthentication(client, logger);
        }

        var currentUser = await GetCurrentUser(client);
        var fullUserName = $"{currentUser.FirstName} {currentUser.LastName}".Trim();
        logger.ZLogInformation($"成功登录为 [[{currentUser.Id}]] / [[@{currentUser.Usernames?.ActiveUsernames[0]}]] / [[{fullUserName}]]");

        // 使用 GetMessageLinkInfoAsync 解析链接
        var linkInfo = await client.GetMessageLinkInfoAsync(link);
        if (linkInfo.Message == null)
        {
            logger.ZLogError($"无法从链接获取消息: {link}");
            return;
        }

        var chatId = linkInfo.Message.ChatId;
        var messageId = linkInfo.Message.Id;

        // 获取聊天信息
        var chat = await client.GetChatAsync(chatId);

        logger.ZLogInformation($"开始下载 {chat.Title} 的媒体组...");
        logger.ZLogInformation($"包含评论: {includeComments}");

        int totalDownloaded = 0;
        var message = linkInfo.Message;

        // 如果是媒体组，下载整个媒体组
        if (message.MediaAlbumId != 0)
        {
            logger.ZLogInformation($"发现媒体组: {message.MediaAlbumId}");
            totalDownloaded += await DownloadMediaGroupByAlbumId(client, chatId, message.MediaAlbumId, messageId, outputPath, logger);
        }
        else
        {
            // 非媒体组消息，直接下载单个文件
            totalDownloaded += await DownloadMessageMedia(client, message, outputPath, logger);
        }

        // 下载评论区媒体
        if (includeComments)
        {
            logger.ZLogInformation($"开始下载评论区媒体...");
            var comments = await GetMessageCommentsAsync(client, chatId, messageId, logger);
            logger.ZLogInformation($"找到 {comments.Length} 条评论");

            int commentsDownloaded = 0;
            int commentsSkipped = 0;
            foreach (var comment in comments)
            {

                var fileId = GetFileIdFromMessage(comment);
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
                        commentsDownloaded += await DownloadMessageMedia(client, comment, outputPath, logger);
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

// 根据媒体组ID下载媒体组
async Task<int> DownloadMediaGroupByAlbumId(TdClient client, long chatId, long mediaAlbumId, long startMessageId, string outputPath, ILogger logger)
{
    int totalDownloaded = 0;

    try
    {
        logger.ZLogInformation($"开始下载媒体组 {mediaAlbumId}");

        var foundMessages = new List<TdApi.Message>();

        // 首先添加找到的第一条消息到列表中
        // 获取这条消息的详细信息
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

        // 向前搜索（更早的消息），从startMessageId往前找
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

        // 向后搜索（更新的消息），从startMessageId往后找
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

        // 下载找到的所有媒体
        foreach (var msg in foundMessages.OrderBy(m => m.Id))
        {
            var count = await DownloadMessageMedia(client, msg, outputPath, logger);
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

// 下载消息中的媒体
async Task<int> DownloadMessageMedia(TdClient client, TdApi.Message message, string outputPath, ILogger logger)
{
    int fileId = GetFileIdFromMessage(message);
    int downloadedCount = 0;

    if (fileId > 0 && !_downloadedFileIds.Contains(fileId))
    {
        _downloadedFileIds.Add(fileId);
        await client.DownloadFileAsync(fileId, 32, 0, 0, true);
        downloadedCount++;
        logger.ZLogInformation($"队列下载: FileId: {fileId}");
    }

    return downloadedCount;
}

// 从消息中提取文件 ID
int GetFileIdFromMessage(TdApi.Message message)
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


// 获取聊天的评论
async Task<TdApi.Message[]> GetMessageCommentsAsync(TdClient client, long chatId, long messageId, ILogger logger)
{
    try
    {
        var threadInfo = await client.GetMessageThreadAsync(chatId, messageId);
        if (threadInfo == null || threadInfo.Messages == null)
        {
            return Array.Empty<TdApi.Message>();
        }
        // 2. 分页获取评论内容
        var comments = await client.GetMessageThreadHistoryAsync(
            chatId: chatId,
            messageId: messageId,
            fromMessageId: 0,       // 从哪条消息开始（0 表示最新）
            offset: 0,
            limit: 50               // 每次获取的数量
        );
        return comments.Messages_;
    }
    catch (TdException ex)
    {
        logger.ZLogWarning($"获取评论失败: {ex.Error.Message}");
        return Array.Empty<TdApi.Message>();
    }
}

// 处理认证
async Task HandleAuthentication(TdClient client, ILogger logger)
{
    try
    {
        await client.ExecuteAsync(new TdApi.SetAuthenticationPhoneNumber
        {
            PhoneNumber = Environment.GetEnvironmentVariable("tdl_phone", EnvironmentVariableTarget.User)
        });

        Console.Write("输入登录验证码: ");
        var code = Console.ReadLine();

        await client.ExecuteAsync(new TdApi.CheckAuthenticationCode { Code = code });

        if (!_updateHandler.PasswordNeeded) { return; }

        Console.Write("输入密码: ");
        var password = Console.ReadLine();

        await client.ExecuteAsync(new TdApi.CheckAuthenticationPassword { Password = password });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "认证失败");
        throw;
    }
}

// 获取当前用户信息
async Task<TdApi.User> GetCurrentUser(TdClient client)
{
    return await client.ExecuteAsync(new TdApi.GetMe());
}

// 配置 TDLib 参数
async Task ConfigureTdlibParameters(TdClient client, string outputPath, ILogger logger)
{
    await client.ExecuteAsync(new TdApi.SetTdlibParameters
    {
        ApiId = Convert.ToInt32(Environment.GetEnvironmentVariable("tdl_api_id", EnvironmentVariableTarget.User)),
        ApiHash = Environment.GetEnvironmentVariable("tdl_api_hash", EnvironmentVariableTarget.User),
        DeviceModel = "PC",
        SystemLanguageCode = "en",
        ApplicationVersion = "1.0.0",
        DatabaseDirectory = Path.Combine(tdlRoot, "db"),
        FilesDirectory = Path.Combine(outputPath, "files"),
        UseFileDatabase = true,
        UseChatInfoDatabase = true,
        UseMessageDatabase = true,
    });

    logger.ZLogInformation($"正在尝试连接代理...");
    var proxy = await client.AddProxyAsync(new TdApi.Proxy() { Server = "127.0.0.1", Port = 7897, Type = new TdApi.ProxyType.ProxyTypeSocks5() }, true);
    await client.EnableProxyAsync(proxy.Id);
    logger.ZLogInformation($"代理已启用。");
}

// 处理文件更新
async Task HandleFileUpdate(TdApi.File file, string outputPath, ILogger logger)
{
    int fileId = file.Id;

    if (file.Local.IsDownloadingActive)
    {
        if (file.Local.DownloadedSize == 0)
        {
            AnsiConsole.WriteLine($"开始下载: {fileId}");
            _downloadTracker.StartDownload(fileId, fileId.ToString(), file.ExpectedSize);
        }
        else
        {
            _downloadTracker.UpdateProgress(fileId, fileId.ToString(), file.Size, file.Local.DownloadedSize);
        }
    }
    else if (file.Local.IsDownloadingCompleted)
    {
        logger.ZLogInformation($"文件 {fileId} 下载完成！本地路径: {file.Local.Path}");
        _downloadTracker.CompleteDownload(fileId, fileId.ToString(), file.Size);

        OnDownloadFinished(file, outputPath, logger);
    }
}

// 处理下载完成的文件
void OnDownloadFinished(TdApi.File file, string outputPath, ILogger logger)
{
    string sourcePath = file.Local.Path;
    if (string.IsNullOrEmpty(sourcePath)) return;

    string fileName = Path.GetFileName(sourcePath);
    string targetPath = Path.Combine(outputPath, "Downloads", fileName);

    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
        File.Copy(sourcePath, targetPath, true);
        logger.ZLogInformation($"文件已归档至: {targetPath}");
    }
    catch (Exception ex)
    {
        logger.ZLogError(ex, $"处理下载完成的文件时出错");
    }
}

// 主程序入口
using (var client = new TdClient())
{
    client.Bindings.SetLogVerbosityLevel(TdLogLevel.Fatal);
    await Main(client, args);
}

// 下载跟踪器类
public class DownloadTracker
{
    private record FileDownloadInfo(long TotalSize, string FileName, long DownloadedSize, double LastSpeed, bool IsCompleted);
    private Dictionary<int, FileDownloadInfo> _downloads = new();
    private object _lock = new();
    private HashSet<int> _completedFiles = [];
    private ProgressContext? _ctx;
    private Dictionary<int, ProgressTask> _tasks = new();
    private bool _running;

    public DownloadTracker()
    {
    }

    void EnsureStarted()
    {
        if (_ctx != null) return;

        _running = true;
        var thread = new Thread(() =>
        {
            AnsiConsole.Progress()
                .AutoRefresh(true)
                .AutoClear(false)
                .HideCompleted(true)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new DownloadedColumn(),
                    new TransferSpeedColumn(),
                    new RemainingTimeColumn())
                .Start(ctx =>
                {
                    _ctx = ctx;
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
                                    task.Description = $"[green]✓[/] [cyan]{fileId}[/] [green]下载完成[/]";
                                    task.StopTask();
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

                            var completedIds = _downloads.Where(kvp => kvp.Value.IsCompleted).Select(kvp => kvp.Key).ToList();
                            foreach (var id in completedIds)
                            {
                                _downloads.Remove(id);
                                _tasks.Remove(id);
                            }
                        }

                        Thread.Sleep(50);
                    }
                });
        });
        thread.IsBackground = true;
        thread.Start();
    }

    public void StartDownload(int fileId, string fileName, long totalSize, long downloadedSize = 0)
    {
        lock (_lock)
        {
            EnsureStarted();
            _downloads[fileId] = new FileDownloadInfo(totalSize, fileName, downloadedSize, 0, false);
        }
    }

    public void UpdateProgress(int fileId, string fileName, long totalSize, long downloadedSize)
    {
        lock (_lock)
        {
            if (_downloads.TryGetValue(fileId, out var info))
            {
                double speed = 0;
                if (info.DownloadedSize > 0 && info.LastSpeed > 0)
                {
                    speed = info.LastSpeed;
                }
                else if (downloadedSize > info.DownloadedSize)
                {
                    speed = downloadedSize - info.DownloadedSize;
                }
                _downloads[fileId] = info with { TotalSize = totalSize, DownloadedSize = downloadedSize, LastSpeed = speed };
            }
            else
            {
                EnsureStarted();
                _downloads[fileId] = new FileDownloadInfo(totalSize, fileName, downloadedSize, 0, false);
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
            AnsiConsole.MarkupLine($"[green]✓[/] [bold]{fileId}[/] 下载完成！");
        }
    }

    public int GetCompletedCount()
    {
        lock (_lock)
        {
            return _completedFiles.Count;
        }
    }

    public void Stop()
    {
        _running = false;
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
