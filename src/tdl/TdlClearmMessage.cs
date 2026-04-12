#:package TDLib@*
#:package tdlib.native@*
#:package tdlib.native.win-x64@*
#:package Spectre.Console@*
#:package Spectre.Console.Ansi@*
#:package Microsoft.Extensions.Logging@*
#:package ZLogger@*
#:package YLFramework.ZLogging@1.0.3-alpha.3
using Framework.ZLogging;
using Microsoft.Extensions.Logging;
using TdLib;
using TdLib.Bindings;
using ZLogger;

/// <summary>
/// 清理收藏的失效信息
/// </summary>
/// <value></value>

using var factory = LoggerFactory.Create(logging =>
{
    logging.SetMinimumLevel(LogLevel.Trace);

    // Add ZLogger provider to ILoggingBuilder
    logging.AddZLoggerSpectreConsole();

    logging.AddZLoggerFile("tdl.log", (options) =>
    {
        options.UsePlainTextFormatter((formatter) =>
        {
            formatter.SetPrefixFormatter($"{0:utc-datetime}|{1:short}|{2}|",
               (in template, in i) =>
               {
                   template.Format(
                               i.Timestamp,
                               i.LogLevel,
                               i.Category);
               });
            formatter.SetExceptionFormatter((writer, ex) => Utf8StringInterpolation.Utf8String.Format(writer, $"{ex.Message}"));
        });
    });
});
var logger = factory.CreateLogger("tdl");



// 获取用户主目录，例如 C:\Users\Administrator
string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

// 拼接 .tdl 目录
string tdlRoot = Path.Combine(userProfile, ".tdl");

// 如果需要区分账号（可选），可以再加一层子目录
string databasePath = Path.Combine(tdlRoot, "db");
string filesPath = Path.Combine(tdlRoot, "files");

// 确保目录存在
if (!Directory.Exists(tdlRoot))
{
    Directory.CreateDirectory(tdlRoot);
    logger.ZLogInformation($"创建数据根目录: {tdlRoot}");
}


ManualResetEventSlim ReadyToAuthenticate = new();
bool _authNeeded = false;
bool _passwordNeeded = false;
using (var client = new TdClient())
{
    client.Bindings.SetLogVerbosityLevel(TdLogLevel.Fatal);



    try
    {
        // Subscribing to all events
        client.UpdateReceived += async (_, update) => { await ProcessUpdates(client, update); };

        // Waiting until we get enough events to be in 'authentication ready' state
        ReadyToAuthenticate.Wait();
        // We may not need to authenticate since TdLib persists session in 'td.binlog' file.
        // See 'TdlibParameters' class for more information, or:
        // https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1tdlib_parameters.html
        if (_authNeeded)
        {
            // Interactively handling authentication
            await HandleAuthentication(client);
        }

        // Querying info about current user and some channels
        var currentUser = await GetCurrentUser(client);

        var fullUserName = $"{currentUser.FirstName} {currentUser.LastName}".Trim();
        logger.ZLogInformation($"Successfully logged in as [{currentUser.Id}] / [@{currentUser.Usernames?.ActiveUsernames[0]}] / [{fullUserName}]");

        await CleanSavedMessages(client);

        Console.WriteLine("Press ENTER to exit from application");
        Console.ReadLine();
    }
    catch (Exception e)
    {
        Console.WriteLine(e);
    }
}
async Task HandleAuthentication(TdClient client)
{
    // Setting phone number
    await client.ExecuteAsync(new TdApi.SetAuthenticationPhoneNumber
    {
        PhoneNumber = Environment.GetEnvironmentVariable("tdl_phone", EnvironmentVariableTarget.Process)
    });

    // Telegram servers will send code to us
    Console.Write("Insert the login code: ");
    var code = Console.ReadLine();

    await client.ExecuteAsync(new TdApi.CheckAuthenticationCode
    {
        Code = code
    });

    if (!_passwordNeeded) { return; }


    // 2FA may be enabled. Cloud password is required in that case.
    Console.Write("Insert the password: ");
    var password = Console.ReadLine();

    await client.ExecuteAsync(new TdApi.CheckAuthenticationPassword
    {
        Password = password
    });
}

async Task ProcessUpdates(TdClient client, TdApi.Update update)
{
    // Since Tdlib was made to be used in GUI application we need to struggle a bit and catch required events to determine our state.
    // Below you can find example of simple authentication handling.
    // Please note that AuthorizationStateWaitOtherDeviceConfirmation is not implemented.

    switch (update)
    {
        case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateWaitTdlibParameters }:
            // TdLib creates database in the current directory.
            // so create separate directory and switch to that dir.
            await client.ExecuteAsync(new TdApi.SetTdlibParameters
            {
                ApiId = Convert.ToInt32(Environment.GetEnvironmentVariable("tdl_api_id", EnvironmentVariableTarget.Process)),
                ApiHash = Environment.GetEnvironmentVariable("tdl_api_hash", EnvironmentVariableTarget.Process),
                DeviceModel = "PC",
                SystemLanguageCode = "en",
                ApplicationVersion = "1.0.0",
                // 数据库放在用户目录下的 .tdl/db
                DatabaseDirectory = Path.Combine(tdlRoot, "db"),

                // 下载的文件放在用户目录下的 .tdl/files
                FilesDirectory = Path.Combine(tdlRoot, "files"),
                UseFileDatabase = true,
                UseChatInfoDatabase = true,
                UseMessageDatabase = true,
            });
            logger.ZLogInformation($"正在尝试连接代理...");
            var proxyType = new TdApi.ProxyType.ProxyTypeSocks5
            {
            };
            // 参数说明：服务器地址, 端口, 是否启用
            var proxy = await client.AddProxyAsync("127.0.0.1", 10808, true, proxyType);

            // 启用该代理
            await client.EnableProxyAsync(proxy.Id);
            logger.ZLogInformation($"代理已启用。");
            break;

        case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateWaitPhoneNumber }:
        case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateWaitCode }:
            _authNeeded = true;
            ReadyToAuthenticate.Set();
            break;

        case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateWaitPassword }:
            _authNeeded = true;
            _passwordNeeded = true;
            ReadyToAuthenticate.Set();
            break;

        case TdApi.Update.UpdateUser:
            ReadyToAuthenticate.Set();
            break;

        case TdApi.Update.UpdateConnectionState { State: TdApi.ConnectionState.ConnectionStateReady }:
            // You may trigger additional event on connection state change
            break;
        // 核心：处理文件状态更新
        case TdApi.Update.UpdateFile updateFile:
            var file = updateFile.File;

            if (file.Local.IsDownloadingActive)
            {
                // 记录下载进度
                double percent = (double)file.Local.DownloadedSize / file.ExpectedSize * 100;
                logger.ZLogInformation($"文件 {file.Id} 进度: {percent:F1}%");
            }
            else if (file.Local.IsDownloadingCompleted)
            {
                // 下载完成，Path 就是磁盘上的绝对路径
                logger.ZLogInformation($"文件下载完成！本地路径: {file.Local.Path}");

                // 这里可以触发你自己的业务逻辑，比如“文件下载后的自动处理”
                // OnDownloadFinished(file);
            }
            break;
        default:
            // ReSharper disable once EmptyStatement
            ;
            // Add a breakpoint here to see other events
            break;
    }
}

async Task<TdApi.User> GetCurrentUser(TdClient client)
{
    return await client.ExecuteAsync(new TdApi.GetMe());
}

async IAsyncEnumerable<TdApi.Chat> GetChannels(TdClient client, int limit)
{
    var chats = await client.ExecuteAsync(new TdApi.GetChats
    {
        Limit = limit
    });

    foreach (var chatId in chats.ChatIds)
    {
        var chat = await client.ExecuteAsync(new TdApi.GetChat
        {
            ChatId = chatId
        });

        if (chat.Type is TdApi.ChatType.ChatTypeSupergroup or TdApi.ChatType.ChatTypeBasicGroup or TdApi.ChatType.ChatTypePrivate)
        {
            yield return chat;
        }
    }
}
async Task CleanSavedMessages(TdClient client)
{

    var clearn = "This channel can’t be displayed";

    var me = await client.GetMeAsync();

    long chatId = me.Id;

    long lastMessageId = 0;
    int totalDeleted = 0;
    List<long> charts = [];
    logger.ZLogInformation($"开始扫描收藏夹...");
    TdApi.Chat savedMessagesChat = await client.CreatePrivateChatAsync(chatId, false);
    while (true)
    {
        var history = await client.GetChatHistoryAsync(chatId, lastMessageId, 0, 500, false);
        Console.WriteLine($"本次拉取数量: {history.Messages_.Length}");
        var toDelete = new List<long>();

        foreach (var msg in history.Messages_)
        {
            lastMessageId = msg.Id; // 更新偏移量
            if (msg.Content is TdLib.TdApi.MessageContent.MessageText text && text.Text is TdLib.TdApi.FormattedText form)
            {

                logger.ZLogInformation($"{msg.Id} {msg.ChatId} {form.Text} {form.Text.Contains(clearn)}");
                if (form.Text.Contains(clearn))
                {
                    toDelete.Add(msg.Id);
                }

            }


            // 3. 识别失效消息（通常是转发自已被封禁的频道）

        }

        // 4. 执行批量删除
        if (toDelete.Count > 0)
        {
            await client.DeleteMessagesAsync(chatId, [.. toDelete], true);
            totalDeleted += toDelete.Count;
            logger.ZLogInformation($"本轮清理了 {toDelete.Count} 条，累计：{totalDeleted}");
        }

        // 如果获取到的消息少于请求数，说明处理完了
        // if (history.Messages_.Length <= 0) break;

        // 适当延迟防止被电报限流
        await Task.Delay(200);
    }

}

async Task ConvertForwardToCopy(TdClient client)
{


    var me = await client.GetMeAsync();

    long chatId = me.Id;

    long lastMessageId = 0;
    int totalDeleted = 0;
    List<long> charts = [];
    logger.ZLogInformation($"开始扫描收藏夹...");
    TdApi.Chat savedMessagesChat = await client.CreatePrivateChatAsync(chatId, false);
    while (true)
    {
        var history = await client.GetChatHistoryAsync(chatId, lastMessageId, 0, 100, false);
        Console.WriteLine($"本次拉取数量: {history.Messages_.Length}");

        foreach (var msg in history.Messages_)
        {
            try
            {
                lastMessageId = msg.Id; // 更新偏移量
                                        // 只有转发的消息才有必要转换
                if (msg.ForwardInfo == null) continue;

                // 调用转发接口，但开启 SendCopy
                var result = await client.ForwardMessagesAsync(
                    chatId: chatId,              // 目标：还是收藏夹
                    fromChatId: chatId,          // 来源：从收藏夹里读
                    messageIds: [msg.Id],
                    sendCopy: true,            // 关键：剥离来源信息，实现深度拷贝
                    removeCaption: false
                );

                if (result.Messages_.Length > 0)
                {
                    if (result.Messages_[0].SendingState is TdApi.MessageSendingState.MessageSendingStateFailed)
                    {
                        logger.ZLogWarning($"转换失败：服务器拒绝了该消息的 SendCopy (ID: {msg.Id})");
                        // 这种情况下不要删除原消息
                        continue;
                    }

                    // 进阶判断：检查内容是否有效
                    // 如果源频道炸了，SendCopy 出来的消息内容往往是 MessageUnsupported 或内容为空
                    if (result.Messages_[0].Content is TdApi.MessageContent.MessageUnsupported)
                    {
                        logger.ZLogWarning($"转换失败：内容不受支持或已丢失 (ID: {msg.Id})");
                        // 这种占位符没用，删掉它

                        continue;
                    }
                    await Task.Delay(2000);
                    // 拷贝成功后，删除原有的转发版本
                    await client.DeleteMessagesAsync(chatId, [msg.Id], true);

                    logger.ZLogInformation($"成功转换消息 {msg.Id} 为深度拷贝版本。新 ID: {result.Messages_[0].Id}");
                }
            }
            catch (TdException ex)
            {
                // 如果原频道已经炸了，ForwardInfo 虽然在，但内容可能读不到了
                logger.ZLogError(ex, $"无法转换消息 {msg.Id}，原频道可能已完全封禁或内容受限。");
            }
        }

        // 如果获取到的消息少于请求数，说明处理完了
        // if (history.Messages_.Length <= 0) break;

        // 适当延迟防止被电报限流
        await Task.Delay(200);




    }

}

void OnDownloadFinished(TdApi.File file)
{
    string sourcePath = file.Local.Path;
    string fileName = Path.GetFileName(sourcePath);
    string targetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonVideos), "Downloads", fileName);

    try
    {
        // 确保目标目录存在
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath));

        // 移动或复制文件到你的业务文件夹
        File.Copy(sourcePath, targetPath, true);

        logger.ZLogInformation($"【业务处理】文件已归档至: {targetPath}");
    }
    catch (Exception ex)
    {
        logger.ZLogError(ex, $"处理下载完成的文件时出错");
    }
}
/// <summary>
/// 解析 Telegram 链接并提取其中的核心 FileId
/// </summary>
/// <param name="link">例如 https://t.me/R_E_STUDIO/21221</param>
/// <returns>返回 FileId，如果未找到则返回 0</returns>
async Task<int> GetFileIdFromLinkAsync(TdClient client, string link)
{
    try
    {
        // 1. 调用 TDLib 内置的链接解析器
        var linkInfo = await client.GetMessageLinkInfoAsync(link);

        if (linkInfo.Message == null)
        {
            logger.ZLogWarning($"链接解析成功，但未找到对应的消息内容: {link}");
            return 0;
        }

        var message = linkInfo.Message;

        // 2. 提取 FileId (根据内容类型)
        int fileId = message.Content switch
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

        if (fileId == 0)
        {
            logger.ZLogWarning($"消息 ID {message.Id} 中不包含可下载的文件。类型: {message.Content.GetType().Name}");
        }

        return fileId;
    }
    catch (TdException ex)
    {
        logger.ZLogError(ex, $"解析链接时发生 TDLib 错误: {link}");
        return 0;
    }
}