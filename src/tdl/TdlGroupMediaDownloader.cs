#!/usr/bin/env dotnet

#:include ../../env.cs

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
bool _authNeeded = false;
bool _passwordNeeded = false;
string tdlRoot = string.Empty;
DownloadTracker _downloadTracker = new();
HashSet<int> _downloadedFileIds = new HashSet<int>();

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
        client.UpdateReceived += async (_, update) => { await ProcessUpdates(client, update, outputPath, logger); };
        ReadyToAuthenticate.Wait();

        if (_authNeeded)
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

        if (!_passwordNeeded) { return; }

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

// 处理更新
async Task ProcessUpdates(TdClient client, TdApi.Update update, string outputPath, ILogger logger)
{
    switch (update)
    {
        #region UpdateAuthorizationState - 认证状态
        case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateWaitTdlibParameters }:
            await ConfigureTdlibParameters(client, outputPath, logger);
            break;
        case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateWaitPhoneNumber }:
            _authNeeded = true;
            ReadyToAuthenticate.Set();
            break;
        case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateWaitCode }:
            _authNeeded = true;
            ReadyToAuthenticate.Set();
            break;
        case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateWaitPassword }:
            _authNeeded = true;
            _passwordNeeded = true;
            ReadyToAuthenticate.Set();
            break;
        case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateWaitRegistration }:
            _authNeeded = true;
            ReadyToAuthenticate.Set();
            logger.ZLogWarning($"需要注册新账户，请先在 Telegram 客户端注册");
            break;
        case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateWaitOtherDeviceConfirmation }:
            logger.ZLogInformation($"请在其他已登录设备上确认授权（QR码扫描）");
            break;
        case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateWaitEmailAddress }:
            _authNeeded = true;
            ReadyToAuthenticate.Set();
            logger.ZLogInformation($"需要提供邮箱地址");
            break;
        case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateWaitEmailCode }:
            _authNeeded = true;
            ReadyToAuthenticate.Set();
            logger.ZLogInformation($"需要输入邮箱验证码");
            break;
        case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateWaitPremiumPurchase }:
            logger.ZLogWarning($"需要购买 Premium 才能继续操作");
            break;
        case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateReady }:
            logger.ZLogInformation($"授权成功，已登录");
            ReadyToAuthenticate.Set();
            break;
        case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateLoggingOut }:
            logger.ZLogInformation($"正在登出...");
            break;
        case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateClosing }:
            logger.ZLogInformation($"TDLib 正在关闭...");
            break;
        case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateClosed }:
            logger.ZLogInformation($"TDLib 已关闭");
            break;
        #endregion

        #region UpdateConnectionState - 连接状态
        case TdApi.Update.UpdateConnectionState { State: TdApi.ConnectionState.ConnectionStateWaitingForNetwork }:
            logger.ZLogWarning($"等待网络连接...");
            break;
        case TdApi.Update.UpdateConnectionState { State: TdApi.ConnectionState.ConnectionStateConnecting }:
            logger.ZLogInformation($"正在连接到 Telegram 服务器...");
            break;
        case TdApi.Update.UpdateConnectionState { State: TdApi.ConnectionState.ConnectionStateConnectingToProxy }:
            logger.ZLogInformation($"正在通过代理连接...");
            break;
        case TdApi.Update.UpdateConnectionState { State: TdApi.ConnectionState.ConnectionStateReady }:
            logger.ZLogInformation($"已连接到 Telegram 服务器");
            break;
        case TdApi.Update.UpdateConnectionState { State: TdApi.ConnectionState.ConnectionStateUpdating }:
            logger.ZLogInformation($"正在更新数据...");
            break;
        #endregion

        #region UpdateFile - 文件相关
        case TdApi.Update.UpdateFile updateFile:
            await HandleFileUpdate(updateFile.File, outputPath, logger);
            break;
        case TdApi.Update.UpdateFileGenerationStart ufgStart:
            logger.ZLogInformation($"文件生成开始: {ufgStart.GenerationId}, 原始路径: {ufgStart.OriginalPath}, 目标: {ufgStart.DestinationPath}");
            break;
        case TdApi.Update.UpdateFileGenerationStop ufgStop:
            logger.ZLogInformation($"文件生成结束: {ufgStop.GenerationId}");
            break;
        case TdApi.Update.UpdateFileDownload ufDownload:
            logger.ZLogTrace($"文件下载状态更新: FileId={ufDownload.FileId}, IsPaused={ufDownload.IsPaused}");
            break;
        case TdApi.Update.UpdateFileAddedToDownloads ufAdded:
            logger.ZLogTrace($"文件添加到下载队列: FileId={ufAdded.FileDownload.FileId}");
            break;
        case TdApi.Update.UpdateFileRemovedFromDownloads ufRemoved:
            logger.ZLogTrace($"文件从下载队列移除: FileId={ufRemoved.FileId}");
            break;
        case TdApi.Update.UpdateFileDownloads ufDownloads:
            logger.ZLogTrace($"下载列表更新: 总数={ufDownloads.TotalCount}, 已下载={ufDownloads.DownloadedSize}, 总计={ufDownloads.TotalSize}");
            break;
        #endregion

        #region UpdateUser - 用户相关
        case TdApi.Update.UpdateUser:
            ReadyToAuthenticate.Set();
            break;
        case TdApi.Update.UpdateUserFullInfo ufi:
            logger.ZLogTrace($"用户详细信息更新: UserId={ufi.UserId}");
            break;
        case TdApi.Update.UpdateUserStatus us:
            logger.ZLogTrace($"用户状态更新: UserId={us.UserId}");
            break;
        case TdApi.Update.UpdateUserPrivacySettingRules ups:
            logger.ZLogTrace($"用户隐私设置更新");
            break;
        #endregion

        #region UpdateOption - 选项
        case TdApi.Update.UpdateOption uo:
            logger.ZLogTrace($"选项更新: {uo.Name} = {uo.Value}");
            break;
        #endregion

        #region UpdateNewMessage / UpdateMessage - 消息相关
        case TdApi.Update.UpdateNewMessage unm:
            logger.ZLogTrace($"新消息: ChatId={unm.Message.ChatId}, MsgId={unm.Message.Id}");
            break;
        case TdApi.Update.UpdateMessageSendSucceeded umss:
            logger.ZLogTrace($"消息发送成功: MsgId={umss.Message.Id}");
            break;
        case TdApi.Update.UpdateMessageSendFailed umsf:
            logger.ZLogWarning($"消息发送失败: MsgId={umsf.Message.Id}, 错误: {umsf.Error.Message}");
            break;
        case TdApi.Update.UpdateMessageSendAcknowledged umsa:
            logger.ZLogTrace($"消息发送已确认: ChatId={umsa.ChatId}, MsgId={umsa.MessageId}");
            break;
        case TdApi.Update.UpdateMessageContent umc:
            logger.ZLogTrace($"消息内容更新: ChatId={umc.ChatId}, MsgId={umc.MessageId}");
            break;
        case TdApi.Update.UpdateMessageEdited ume:
            logger.ZLogTrace($"消息已编辑: ChatId={ume.ChatId}, MsgId={ume.MessageId}");
            break;
        case TdApi.Update.UpdateMessageIsPinned umip:
            logger.ZLogTrace($"消息置顶状态变更: ChatId={umip.ChatId}, MsgId={umip.MessageId}, Pinned={umip.IsPinned}");
            break;
        case TdApi.Update.UpdateMessageLiveLocationViewed umllv:
            logger.ZLogTrace($"实时位置消息已查看: ChatId={umllv.ChatId}, MsgId={umllv.MessageId}");
            break;
        case TdApi.Update.UpdateMessageMentionRead ummr:
            logger.ZLogTrace($"消息提及已读: ChatId={ummr.ChatId}, MsgId={ummr.MessageId}");
            break;
        case TdApi.Update.UpdateMessageContentOpened umco:
            logger.ZLogTrace($"消息内容已打开: ChatId={umco.ChatId}, MsgId={umco.MessageId}");
            break;
        case TdApi.Update.UpdateMessageInteractionInfo umii:
            logger.ZLogTrace($"消息交互信息更新: ChatId={umii.ChatId}, MsgId={umii.MessageId}");
            break;
        case TdApi.Update.UpdateMessageReaction umr:
            logger.ZLogTrace($"消息反应更新: ChatId={umr.ChatId}, MsgId={umr.MessageId}");
            break;
        case TdApi.Update.UpdateMessageReactions umrs:
            logger.ZLogTrace($"消息反应列表更新: ChatId={umrs.ChatId}, MsgId={umrs.MessageId}");
            break;
        case TdApi.Update.UpdateMessageUnreadReactions umur:
            logger.ZLogTrace($"消息未读反应更新: ChatId={umur.ChatId}, MsgId={umur.MessageId}");
            break;
        case TdApi.Update.UpdateMessageFactCheck umfc:
            logger.ZLogTrace($"消息事实核查更新: ChatId={umfc.ChatId}, MsgId={umfc.MessageId}");
            break;
        case TdApi.Update.UpdateMessageSuggestedPostInfo umspi:
            logger.ZLogTrace($"消息建议帖子信息更新: ChatId={umspi.ChatId}, MsgId={umspi.MessageId}");
            break;
        case TdApi.Update.UpdateDeleteMessages udm:
            logger.ZLogTrace($"消息删除: ChatId={udm.ChatId}, 数量={udm.MessageIds.Length}");
            break;
        case TdApi.Update.UpdatePendingTextMessage uptm:
            logger.ZLogTrace($"待发送文本消息更新: ChatId={uptm.ChatId}");
            break;
        #endregion

        #region UpdateChat - 聊天相关
        case TdApi.Update.UpdateNewChat unc:
            logger.ZLogTrace($"新聊天: ChatId={unc.Chat.Id}, Title={unc.Chat.Title}");
            break;
        case TdApi.Update.UpdateChatTitle uct:
            logger.ZLogTrace($"聊天标题更新: ChatId={uct.ChatId}, Title={uct.Title}");
            break;
        case TdApi.Update.UpdateChatPhoto ucp:
            logger.ZLogTrace($"聊天头像更新: ChatId={ucp.ChatId}");
            break;
        case TdApi.Update.UpdateChatLastMessage uclm:
            logger.ZLogTrace($"聊天最后消息更新: ChatId={uclm.ChatId}");
            break;
        case TdApi.Update.UpdateChatPosition ucpo:
            logger.ZLogTrace($"聊天位置更新: ChatId={ucpo.ChatId}");
            break;
        case TdApi.Update.UpdateChatReadInbox ucri:
            logger.ZLogTrace($"聊天已读更新: ChatId={ucri.ChatId}, Unread={ucri.UnreadCount}");
            break;
        case TdApi.Update.UpdateChatReadOutbox ucro:
            logger.ZLogTrace($"聊天已发送已读更新: ChatId={ucro.ChatId}, LastRead={ucro.LastReadOutboxMessageId}");
            break;
        case TdApi.Update.UpdateChatUnreadMentionCount ucumc:
            logger.ZLogTrace($"聊天未读提及数更新: ChatId={ucumc.ChatId}, Count={ucumc.UnreadMentionCount}");
            break;
        case TdApi.Update.UpdateChatUnreadReactionCount ucurc:
            logger.ZLogTrace($"聊天未读反应数更新: ChatId={ucurc.ChatId}, Count={ucurc.UnreadReactionCount}");
            break;
        case TdApi.Update.UpdateChatUnreadPollVoteCount ucupvc:
            logger.ZLogTrace($"聊天未读投票数更新: ChatId={ucupvc.ChatId}, Count={ucupvc.UnreadPollVoteCount}");
            break;
        case TdApi.Update.UpdateChatNotificationSettings ucns:
            logger.ZLogTrace($"聊天通知设置更新: ChatId={ucns.ChatId}");
            break;
        case TdApi.Update.UpdateChatDefaultDisableNotification ucdn:
            logger.ZLogTrace($"聊天默认静默通知更新: ChatId={ucdn.ChatId}");
            break;
        case TdApi.Update.UpdateChatMessageAutoDeleteTime ucmadt:
            logger.ZLogTrace($"聊天消息自动删除时间更新: ChatId={ucmadt.ChatId}, Time={ucmadt.MessageAutoDeleteTime}");
            break;
        case TdApi.Update.UpdateChatDraftMessage ucdm:
            logger.ZLogTrace($"聊天草稿更新: ChatId={ucdm.ChatId}");
            break;
        case TdApi.Update.UpdateChatIsMarkedAsUnread ucimu:
            logger.ZLogTrace($"聊天标记未读更新: ChatId={ucimu.ChatId}, IsUnread={ucimu.IsMarkedAsUnread}");
            break;
        case TdApi.Update.UpdateChatBlockList ucbl:
            logger.ZLogTrace($"聊天屏蔽列表更新: ChatId={ucbl.ChatId}");
            break;
        case TdApi.Update.UpdateChatHasProtectedContent uchpc:
            logger.ZLogTrace($"聊天保护内容更新: ChatId={uchpc.ChatId}, Protected={uchpc.HasProtectedContent}");
            break;
        case TdApi.Update.UpdateChatHasScheduledMessages uchsm:
            logger.ZLogTrace($"聊天定时消息更新: ChatId={uchsm.ChatId}");
            break;
        case TdApi.Update.UpdateChatIsTranslatable ucit:
            logger.ZLogTrace($"聊天可翻译状态更新: ChatId={ucit.ChatId}");
            break;
        case TdApi.Update.UpdateChatOnlineMemberCount ucomc:
            logger.ZLogTrace($"聊天在线成员数更新: ChatId={ucomc.ChatId}, Count={ucomc.OnlineMemberCount}");
            break;
        case TdApi.Update.UpdateChatPermissions ucp:
            logger.ZLogTrace($"聊天权限更新: ChatId={ucp.ChatId}");
            break;
        case TdApi.Update.UpdateChatActionBar ucab:
            logger.ZLogTrace($"聊天操作栏更新: ChatId={ucab.ChatId}");
            break;
        case TdApi.Update.UpdateChatPendingJoinRequests ucpjr:
            logger.ZLogTrace($"聊天待处理加入请求更新: ChatId={ucpjr.ChatId}");
            break;
        case TdApi.Update.UpdateChatReplyMarkup ucrm:
            logger.ZLogTrace($"聊天回复标记更新: ChatId={ucrm.ChatId}");
            break;
        case TdApi.Update.UpdateChatBackground ucb:
            logger.ZLogTrace($"聊天背景更新: ChatId={ucb.ChatId}");
            break;
        case TdApi.Update.UpdateChatTheme uct:
            logger.ZLogTrace($"聊天主题更新: ChatId={uct.ChatId}");
            break;
        case TdApi.Update.UpdateChatAvailableReactions ucar:
            logger.ZLogTrace($"聊天可用反应更新: ChatId={ucar.ChatId}");
            break;
        case TdApi.Update.UpdateChatMessageSender ucms:
            logger.ZLogTrace($"聊天消息发送者更新: ChatId={ucms.ChatId}");
            break;
        case TdApi.Update.UpdateChatVideoChat ucvc:
            logger.ZLogTrace($"聊天视频通话更新: ChatId={ucvc.ChatId}");
            break;
        case TdApi.Update.UpdateChatMember ucm:
            logger.ZLogTrace($"聊天成员更新: ChatId={ucm.ChatId}");
            break;
        case TdApi.Update.UpdateChatBoost ucb:
            logger.ZLogTrace($"聊天Boost更新: ChatId={ucb.ChatId}");
            break;
        case TdApi.Update.UpdateChatAddedToList ucatl:
            logger.ZLogTrace($"聊天添加到列表: ChatId={ucatl.ChatId}");
            break;
        case TdApi.Update.UpdateChatRemovedFromList ucrfl:
            logger.ZLogTrace($"聊天从列表移除: ChatId={ucrfl.ChatId}");
            break;
        case TdApi.Update.UpdateChatEmojiStatus uces:
            logger.ZLogTrace($"聊天Emoji状态更新: ChatId={uces.ChatId}");
            break;
        case TdApi.Update.UpdateChatAccentColors ucacc:
            logger.ZLogTrace($"聊天强调色更新: ChatId={ucacc.ChatId}");
            break;
        case TdApi.Update.UpdateChatActiveStories ucas:
            logger.ZLogTrace($"聊天活跃Stories更新: ChatId={ucas.ActiveStories.ChatId}");
            break;
        case TdApi.Update.UpdateChatBusinessBotManageBar ucbbmb:
            logger.ZLogTrace($"聊天商业机器人管理栏更新: ChatId={ucbbmb.ChatId}");
            break;
        case TdApi.Update.UpdateChatFolders ucf:
            logger.ZLogTrace($"聊天文件夹更新");
            break;
        case TdApi.Update.UpdateChatViewAsTopics ucvat:
            logger.ZLogTrace($"聊天话题视图更新: ChatId={ucvat.ChatId}");
            break;
        case TdApi.Update.UpdateChatRevenueAmount ucra:
            logger.ZLogTrace($"聊天收入更新: ChatId={ucra.ChatId}");
            break;
        #endregion

        #region UpdateBasicGroup / UpdateSupergroup / UpdateSecretChat - 群组/频道/密聊
        case TdApi.Update.UpdateBasicGroup ubg:
            logger.ZLogTrace($"基础群组更新: BasicGroupId={ubg.BasicGroup.Id}");
            break;
        case TdApi.Update.UpdateBasicGroupFullInfo ubgfi:
            logger.ZLogTrace($"基础群组详细信息更新: BasicGroupId={ubgfi.BasicGroupId}");
            break;
        case TdApi.Update.UpdateSupergroup usg:
            logger.ZLogTrace($"超级群组更新: SupergroupId={usg.Supergroup.Id}");
            break;
        case TdApi.Update.UpdateSupergroupFullInfo usgfi:
            logger.ZLogTrace($"超级群组详细信息更新: SupergroupId={usgfi.SupergroupId}");
            break;
        case TdApi.Update.UpdateSecretChat usc:
            logger.ZLogTrace($"密聊更新: SecretChatId={usc.SecretChat.Id}");
            break;
        #endregion

        #region UpdateCall / UpdateGroupCall - 通话相关
        case TdApi.Update.UpdateCall uc:
            logger.ZLogTrace($"通话更新: CallId={uc.Call.Id}");
            break;
        case TdApi.Update.UpdateGroupCall ugc:
            logger.ZLogTrace($"群通话更新: GroupCallId={ugc.GroupCall.Id}");
            break;
        case TdApi.Update.UpdateGroupCallParticipant ugcp:
            logger.ZLogTrace($"群通话参与者更新");
            break;
        case TdApi.Update.UpdateGroupCallParticipants ugcp:
            logger.ZLogTrace($"群通话参与者列表更新");
            break;
        case TdApi.Update.UpdateGroupCallMessageLevels ugcml:
            logger.ZLogTrace($"群通话消息级别更新: 级别数={ugcml.Levels.Length}");
            break;
        case TdApi.Update.UpdateGroupCallMessagesDeleted ugcmd:
            logger.ZLogTrace($"群通话消息删除: GroupCallId={ugcmd.GroupCallId}");
            break;
        case TdApi.Update.UpdateGroupCallMessageSendFailed ugmsf:
            logger.ZLogTrace($"群通话消息发送失败: GroupCallId={ugmsf.GroupCallId}");
            break;
        case TdApi.Update.UpdateGroupCallVerificationState ugcvs:
            logger.ZLogTrace($"群通话验证状态更新: GroupCallId={ugcvs.GroupCallId}");
            break;
        #endregion

        #region UpdateNotification - 通知相关
        case TdApi.Update.UpdateActiveNotifications uan:
            logger.ZLogTrace($"活跃通知更新");
            break;
        case TdApi.Update.UpdateNotification un:
            logger.ZLogTrace($"通知更新: GroupId={un.NotificationGroupId}, Type={un.Notification.Type}");
            break;
        case TdApi.Update.UpdateNotificationGroup ung:
            logger.ZLogTrace($"通知组更新: GroupId={ung.NotificationGroupId}");
            break;
        case TdApi.Update.UpdateHavePendingNotifications uhpn:
            logger.ZLogTrace($"待处理通知更新");
            break;
        case TdApi.Update.UpdateScopeNotificationSettings usns:
            logger.ZLogTrace($"范围通知设置更新");
            break;
        case TdApi.Update.UpdateReactionNotificationSettings urns:
            logger.ZLogTrace($"反应通知设置更新");
            break;
        #endregion

        #region UpdateSticker / Animation / Emoji - 贴纸/动画/表情
        case TdApi.Update.UpdateInstalledStickerSets uiss:
            logger.ZLogTrace($"已安装贴纸包更新: 类型={uiss.StickerType}");
            break;
        case TdApi.Update.UpdateTrendingStickerSets utss:
            logger.ZLogTrace($"热门贴纸包更新");
            break;
        case TdApi.Update.UpdateStickerSet uss:
            logger.ZLogTrace($"贴纸包更新: SetId={uss.StickerSet.Id}");
            break;
        case TdApi.Update.UpdateFavoriteStickers ufs:
            logger.ZLogTrace($"收藏贴纸更新");
            break;
        case TdApi.Update.UpdateRecentStickers urs:
            logger.ZLogTrace($"最近贴纸更新: 从收藏={urs.IsAttached}");
            break;
        case TdApi.Update.UpdateSavedAnimations usa:
            logger.ZLogTrace($"保存的动画更新");
            break;
        case TdApi.Update.UpdateActiveEmojiReactions uaer:
            logger.ZLogTrace($"活跃表情反应更新");
            break;
        case TdApi.Update.UpdateDiceEmojis ude:
            logger.ZLogTrace($"骰子Emoji更新");
            break;
        case TdApi.Update.UpdateAnimatedEmojiMessageClicked uaemc:
            logger.ZLogTrace($"动画Emoji消息点击: ChatId={uaemc.ChatId}, MsgId={uaemc.MessageId}");
            break;
        case TdApi.Update.UpdateAnimationSearchParameters uasp:
            logger.ZLogTrace($"动画搜索参数更新: Provider={uasp.Provider}");
            break;
        case TdApi.Update.UpdateEmojiChatThemes uect:
            logger.ZLogTrace($"Emoji聊天主题更新");
            break;
        case TdApi.Update.UpdateSavedNotificationSounds usns:
            logger.ZLogTrace($"保存的通知声音更新");
            break;
        #endregion

        #region UpdateServiceNotification / TermsOfService - 服务通知/条款
        case TdApi.Update.UpdateServiceNotification usn:
            logger.ZLogInformation($"服务通知: Type={usn.Type}");
            break;
        case TdApi.Update.UpdateTermsOfService utos:
            logger.ZLogInformation($"服务条款更新");
            break;
        #endregion

        #region UpdatePoll - 投票相关
        case TdApi.Update.UpdatePoll up:
            logger.ZLogTrace($"投票更新: PollId={up.Poll.Id}");
            break;
        case TdApi.Update.UpdatePollAnswer upa:
            logger.ZLogTrace($"投票回答更新: PollId={upa.PollId}");
            break;
        #endregion

        #region UpdateAutosaveSettings - 自动保存设置
        case TdApi.Update.UpdateAutosaveSettings uas:
            logger.ZLogTrace($"自动保存设置更新");
            break;
        #endregion

        #region UpdateAttachmentMenuBots - 附件菜单机器人
        case TdApi.Update.UpdateAttachmentMenuBots uamb:
            logger.ZLogTrace($"附件菜单机器人更新");
            break;
        #endregion

        #region UpdateWebApp / Inline / Callback - 机器人交互
        case TdApi.Update.UpdateWebAppMessageSent uwams:
            logger.ZLogTrace($"WebApp消息已发送");
            break;
        case TdApi.Update.UpdateNewInlineQuery uninq:
            logger.ZLogTrace($"新Inline查询: Id={uninq.Id}");
            break;
        case TdApi.Update.UpdateNewChosenInlineResult uncir:
            logger.ZLogTrace($"新选择的Inline结果: Query={uncir.Query}, ResultId={uncir.ResultId}");
            break;
        case TdApi.Update.UpdateNewCallbackQuery uncbq:
            logger.ZLogTrace($"新回调查询: Id={uncbq.Id}, ChatId={uncbq.ChatId}");
            break;
        case TdApi.Update.UpdateNewInlineCallbackQuery unicbq:
            logger.ZLogTrace($"新Inline回调查询: Id={unicbq.Id}");
            break;
        case TdApi.Update.UpdateNewBusinessCallbackQuery unbcbq:
            logger.ZLogTrace($"新商业回调查询: Id={unbcbq.Id}");
            break;
        case TdApi.Update.UpdateNewPreCheckoutQuery unpcq:
            logger.ZLogTrace($"新预结账查询: Id={unpcq.Id}");
            break;
        case TdApi.Update.UpdateNewShippingQuery unsq:
            logger.ZLogTrace($"新配送查询: Id={unsq.Id}");
            break;
        case TdApi.Update.UpdateNewCustomEvent unce:
            logger.ZLogTrace($"新自定义事件");
            break;
        case TdApi.Update.UpdateNewCustomQuery uncq:
            logger.ZLogTrace($"新自定义查询: Id={uncq.Id}");
            break;
        case TdApi.Update.UpdateNewOauthRequest unor:
            logger.ZLogTrace($"新OAuth请求: Domain={unor.Domain}");
            break;
        #endregion

        #region UpdateBusiness - 商业相关
        case TdApi.Update.UpdateBusinessConnection ubc:
            logger.ZLogTrace($"商业连接更新: Id={ubc.Connection.Id}");
            break;
        case TdApi.Update.UpdateNewBusinessMessage unbm:
            logger.ZLogTrace($"新商业消息: ConnectionId={unbm.ConnectionId}");
            break;
        case TdApi.Update.UpdateBusinessMessageEdited ubme:
            logger.ZLogTrace($"商业消息编辑: ConnectionId={ubme.ConnectionId}");
            break;
        case TdApi.Update.UpdateBusinessMessagesDeleted ubmd:
            logger.ZLogTrace($"商业消息删除: ConnectionId={ubmd.ConnectionId}");
            break;
        case TdApi.Update.UpdateManagedBot umb:
            logger.ZLogTrace($"托管机器人更新: BotUserId={umb.BotUserId}");
            break;
        #endregion

        #region UpdateStory - Stories 相关
        case TdApi.Update.UpdateStory us:
            logger.ZLogTrace($"Story更新: StoryId={us.Story.Id}");
            break;
        case TdApi.Update.UpdateStoryDeleted usd:
            logger.ZLogTrace($"Story删除: StoryPosterChatId={usd.StoryPosterChatId}, StoryId={usd.StoryId}");
            break;
        case TdApi.Update.UpdateStoryPostSucceeded usps:
            logger.ZLogTrace($"Story发布成功: StoryId={usps.Story.Id}");
            break;
        case TdApi.Update.UpdateStoryPostFailed uspf:
            logger.ZLogTrace($"Story发布失败: StoryId={uspf.Story.Id}, Error={uspf.Error.Message}");
            break;
        case TdApi.Update.UpdateStoryStealthMode ussm:
            logger.ZLogTrace($"Story隐身模式更新");
            break;
        case TdApi.Update.UpdateStoryListChatCount uslcc:
            logger.ZLogTrace($"Story列表聊天数更新");
            break;
        case TdApi.Update.UpdateLiveStoryTopDonors ulstd:
            logger.ZLogTrace($"直播Story顶级捐赠者更新");
            break;
        #endregion

        #region UpdateForumTopic - 论坛话题
        case TdApi.Update.UpdateForumTopic uft:
            logger.ZLogTrace($"论坛话题更新: ChatId={uft.ChatId}");
            break;
        case TdApi.Update.UpdateForumTopicInfo ufti:
            logger.ZLogTrace($"论坛话题信息更新: ChatId={ufti.Info.ChatId}, TopicId={ufti.Info.ForumTopicId}");
            break;
        #endregion

        #region UpdateQuickReply - 快速回复
        case TdApi.Update.UpdateQuickReplyShortcut uqrs:
            logger.ZLogTrace($"快速回复快捷方式更新: ShortcutId={uqrs.Shortcut.Id}");
            break;
        case TdApi.Update.UpdateQuickReplyShortcutDeleted uqrsd:
            logger.ZLogTrace($"快速回复快捷方式删除: ShortcutId={uqrsd.ShortcutId}");
            break;
        case TdApi.Update.UpdateQuickReplyShortcutMessages uqrsm:
            logger.ZLogTrace($"快速回复快捷方式消息更新: ShortcutId={uqrsm.ShortcutId}");
            break;
        case TdApi.Update.UpdateQuickReplyShortcuts uqrss:
            logger.ZLogTrace($"快速回复快捷方式列表更新");
            break;
        #endregion

        #region UpdateLanguagePack - 语言包
        case TdApi.Update.UpdateLanguagePackStrings ulps:
            logger.ZLogTrace($"语言包字符串更新: {ulps.LocalizationTarget}/{ulps.LanguagePackId}");
            break;
        #endregion

        #region UpdateUnread - 未读计数
        case TdApi.Update.UpdateUnreadChatCount uucc:
            logger.ZLogTrace($"未读聊天数更新: 列表={uucc.ChatList}, 数量={uucc.UnreadCount}");
            break;
        case TdApi.Update.UpdateUnreadMessageCount uumc:
            logger.ZLogTrace($"未读消息数更新: 数量={uumc.UnreadCount}");
            break;
        #endregion

        #region UpdateChatJoinRequest - 加入请求
        case TdApi.Update.UpdateNewChatJoinRequest uncjr:
            logger.ZLogTrace($"新聊天加入请求: ChatId={uncjr.ChatId}");
            break;
        #endregion

        #region UpdateNewCallSignalingData - 通话信令
        case TdApi.Update.UpdateNewCallSignalingData uncsd:
            logger.ZLogTrace($"新通话信令数据: CallId={uncsd.CallId}");
            break;
        #endregion

        #region UpdateNewGroupCall - 群通话消息
        case TdApi.Update.UpdateNewGroupCallMessage ungcm:
            logger.ZLogTrace($"新群通话消息: GroupCallId={ungcm.GroupCallId}");
            break;
        case TdApi.Update.UpdateNewGroupCallPaidReaction ungcpr:
            logger.ZLogTrace($"新群通话付费反应: GroupCallId={ungcpr.GroupCallId}");
            break;
        #endregion

        #region UpdateSavedMessages - 保存的消息
        case TdApi.Update.UpdateSavedMessagesTags usmt:
            logger.ZLogTrace($"保存的消息标签更新");
            break;
        case TdApi.Update.UpdateSavedMessagesTopic usmt:
            logger.ZLogTrace($"保存的消息话题更新");
            break;
        case TdApi.Update.UpdateSavedMessagesTopicCount usmtc:
            logger.ZLogTrace($"保存的消息话题计数更新");
            break;
        #endregion

        #region UpdateDefault / Profile / Accent - 默认设置/个人资料/强调色
        case TdApi.Update.UpdateDefaultReactionType udrt:
            logger.ZLogTrace($"默认反应类型更新");
            break;
        case TdApi.Update.UpdateDefaultBackground udb:
            logger.ZLogTrace($"默认背景更新");
            break;
        case TdApi.Update.UpdateDefaultPaidReactionType udprt:
            logger.ZLogTrace($"默认付费反应类型更新");
            break;
        case TdApi.Update.UpdateProfileAccentColors upac:
            logger.ZLogTrace($"个人资料强调色更新");
            break;
        case TdApi.Update.UpdateAccentColors uacc:
            logger.ZLogTrace($"强调色更新");
            break;
        #endregion

        #region UpdateAvailableMessageEffects / TextCompositionStyles
        case TdApi.Update.UpdateAvailableMessageEffects uame:
            logger.ZLogTrace($"可用消息效果更新");
            break;
        case TdApi.Update.UpdateTextCompositionStyles utcs:
            logger.ZLogTrace($"文本组合样式更新");
            break;
        #endregion

        #region UpdateContactCloseBirthdays
        case TdApi.Update.UpdateContactCloseBirthdays uccb:
            logger.ZLogTrace($"联系人近期生日更新");
            break;
        #endregion

        #region UpdateActiveLiveLocationMessages
        case TdApi.Update.UpdateActiveLiveLocationMessages uallm:
            logger.ZLogTrace($"活跃实时位置消息更新");
            break;
        #endregion

        #region UpdateApplicationVerification
        case TdApi.Update.UpdateApplicationVerificationRequired uavr:
            logger.ZLogWarning($"需要应用验证");
            break;
        case TdApi.Update.UpdateApplicationRecaptchaVerificationRequired uarvr:
            logger.ZLogWarning($"需要应用reCAPTCHA验证");
            break;
        #endregion

        #region UpdateSuggestedActions
        case TdApi.Update.UpdateSuggestedActions usa:
            logger.ZLogTrace($"建议操作更新: 新增={usa.AddedActions.Length}, 移除={usa.RemovedActions.Length}");
            break;
        #endregion

        #region UpdateUnconfirmedSession
        case TdApi.Update.UpdateUnconfirmedSession uus:
            logger.ZLogTrace($"未确认会话更新");
            break;
        #endregion

        #region UpdateSpeedLimitNotification
        case TdApi.Update.UpdateSpeedLimitNotification usln:
            logger.ZLogWarning($"速率限制通知");
            break;
        #endregion

        #region UpdateFreezeState
        case TdApi.Update.UpdateFreezeState ufs:
            logger.ZLogTrace($"冻结状态更新: IsFrozen={ufs.IsFrozen}");
            break;
        #endregion

        #region UpdateSpeechRecognitionTrial
        case TdApi.Update.UpdateSpeechRecognitionTrial usrt:
            logger.ZLogTrace($"语音识别试用更新");
            break;
        #endregion

        #region UpdateDirectMessagesChatTopic
        case TdApi.Update.UpdateDirectMessagesChatTopic udmct:
            logger.ZLogTrace($"直接消息聊天话题更新: ChatId={udmct.Topic.ChatId}, Id={udmct.Topic.Id}");
            break;
        #endregion

        #region UpdateTopicMessageCount
        case TdApi.Update.UpdateTopicMessageCount utmc:
            logger.ZLogTrace($"话题消息计数更新: ChatId={utmc.ChatId}");
            break;
        #endregion

        #region UpdateTrustedMiniAppBots
        case TdApi.Update.UpdateTrustedMiniAppBots utmab:
            logger.ZLogTrace($"信任的MiniApp机器人更新");
            break;
        #endregion

        #region UpdateOwnedStarCount / UpdateOwnedTonCount / UpdateStarRevenueStatus / UpdateTonRevenueStatus
        case TdApi.Update.UpdateOwnedStarCount uosc:
            logger.ZLogTrace($"拥有的Star数量更新");
            break;
        case TdApi.Update.UpdateOwnedTonCount uotc:
            logger.ZLogTrace($"拥有的TON数量更新");
            break;
        case TdApi.Update.UpdateStarRevenueStatus usrs:
            logger.ZLogTrace($"Star收入状态更新");
            break;
        case TdApi.Update.UpdateTonRevenueStatus utrs:
            logger.ZLogTrace($"TON收入状态更新");
            break;
        #endregion

        #region UpdatePaidMediaPurchased
        case TdApi.Update.UpdatePaidMediaPurchased ump:
            logger.ZLogTrace($"付费媒体已购买: UserId={ump.UserId}");
            break;
        #endregion

        #region UpdateGiftAuction / UpdateStakeDice
        case TdApi.Update.UpdateActiveGiftAuctions uaga:
            logger.ZLogTrace($"活跃礼物拍卖更新");
            break;
        case TdApi.Update.UpdateGiftAuctionState ugas:
            logger.ZLogTrace($"礼物拍卖状态更新");
            break;
        case TdApi.Update.UpdateStakeDiceState usds:
            logger.ZLogTrace($"质押骰子状态更新");
            break;
        #endregion

        #region UpdateVideoPublished
        case TdApi.Update.UpdateVideoPublished uvp:
            logger.ZLogTrace($"视频已发布: ChatId={uvp.ChatId}, MessageId={uvp.MessageId}");
            break;
        #endregion

        #region UpdateAgeVerificationParameters
        case TdApi.Update.UpdateAgeVerificationParameters uavp:
            logger.ZLogTrace($"年龄验证参数更新");
            break;
        #endregion

        #region default - 未识别的更新
        default:
            logger.ZLogTrace($"未处理的更新类型: {update.GetType().Name}");
            break;
        #endregion
    }
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