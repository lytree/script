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
using Microsoft.VisualBasic;
using Newtonsoft.Json;
using TdLib;
using TdLib.Bindings;
using ZLogger;

/// <summary>
/// 将转发消息转换为深度copy
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
        var chatId = await GetChatIdFromLinkAsync(client, "https://t.me/atsJoe/19361");
        await ForwardEverythingUntilTheEnd(client, chatId);


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
        PhoneNumber = Environment.GetEnvironmentVariable("tdl_phone", EnvironmentVariableTarget.User)
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
                ApiId = Convert.ToInt32(Environment.GetEnvironmentVariable("tdl_api_id", EnvironmentVariableTarget.User)),
                ApiHash = Environment.GetEnvironmentVariable("tdl_api_hash", EnvironmentVariableTarget.User),
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
            var proxy = await client.AddProxyAsync(new TdApi.Proxy() { Server = "127.0.0.1", Port = 7897, Type = new TdApi.ProxyType.ProxyTypeSocks5() }, true);

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
                logger.ZLogTrace($"文件 {file.Id} 进度: {percent:F1}%");
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
/// <summary>
/// 通过 Telegram 链接获取对应的 ChatId
/// </summary>
/// <param name="link">例如 https://t.me/R_E_STUDIO/21221</param>
/// <returns>返回 ChatId，失败返回 0</returns>
async Task<long> GetChatIdFromLinkAsync(TdClient client, string link)
{
    try
    {
        // 1. 调用内置的链接解析器
        // 它会自动处理用户名(R_E_STUDIO)并查找对应的 Chat
        var linkInfo = await client.GetMessageLinkInfoAsync(link);

        if (linkInfo.Message != null)
        {
            // 如果链接指向具体的一条消息
            return linkInfo.Message.ChatId;
        }

        // 注意：有时链接可能只指向频道本身，而不包含消息 ID
        // 此时需要检查 linkInfo 的其他字段（取决于 TDLib 版本）
        // 或者如果 linkInfo 返回空，尝试搜索公共 Chat
        logger.ZLogWarning($"链接解析成功，但未直接关联到消息。");

    }
    catch (TdException ex)
    {
        logger.ZLogError(ex, $"无法解析链接: {link}");

        // // 备选方案：如果是简单的公开链接，尝试手动提取用户名搜索
        // return await TrySearchPublicChat(client, link);
    }
    return 0;
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


async Task ForwardEverythingUntilTheEnd(TdClient client, long chatId)
{
    var me = await client.GetMeAsync();
    long myId = me.Id;

    // 从最新消息开始 (0 代表最新)
    long lastMessageId = 0;
    int totalForwarded = 0;
    bool hasMore = true;

    logger.ZLogInformation($"开始全量备份频道 {chatId} 到收藏夹...");

    while (hasMore)
    {
        try
        {
            // 1. 获取历史消息 (每次取 100 条)
            // offset = 0, from_message_id = lastMessageId
            var history = await client.GetChatHistoryAsync(chatId, lastMessageId, 0, 100, false);

            if (history.Messages_ == null || history.Messages_.Length == 0)
            {
                logger.ZLogInformation($"已到达频道的最久远的一条消息。备份结束！");
                hasMore = false;
                break;
            }

            // 2. 筛选并排序消息 ID
            // 如果你只想转发视频，保留 .Where(...)；如果转发全部，去掉 .Where(...)
            var idsToForward = history.Messages_
                .Where(m => m.Content is TdApi.MessageContent.MessageVideo) // 只选视频
                .Select(m => m.Id)
                .OrderBy(id => id) // 必须升序，解决你之前的报错
                .ToArray();

            // 3. 执行深度转发
            if (idsToForward.Length > 0)
            {
                var result = await client.ForwardMessagesAsync(
                    chatId: myId,              // 目标：还是收藏夹
                    fromChatId: chatId,          // 来源：从收藏夹里读
                    messageIds: [.. idsToForward],
                    sendCopy: true,            // 关键：剥离来源信息，实现深度拷贝
                    removeCaption: false
                );

                totalForwarded += idsToForward.Length;
                logger.ZLogInformation($"已转发 {totalForwarded} 条消息，当前进度 ID: {lastMessageId}");
            }

            // 5. 更新 lastMessageId，指向这一批里最旧的一条，为下一轮抓取做准备
            lastMessageId = history.Messages_.Last().Id;

            // 6. 防封限速：全量操作务必控制频率
            await Task.Delay(1500);
        }
        catch (TdException ex) when (ex.Error.Code == 429) // 处理 Flood Wait
        {
            int retryAfter = 10; // 默认等10秒
            // 如果报错里包含等待秒数，可以解析出来
            logger.ZLogWarning($"触发频率限制，等待 {0} 秒后继续...", retryAfter);
            await Task.Delay(retryAfter * 1000);
        }
        catch (Exception ex)
        {
            logger.ZLogError(ex, $"全量备份循环中发生异常");
            // 发生未知错误时，建议稍微停顿一下再重试，或者跳过这一批
            await Task.Delay(5000);
        }
    }

    logger.ZLogInformation($"全量任务执行完毕，共转发 {totalForwarded} 条视频。");
}