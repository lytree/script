#:package TDLib@1.8.60
#:package tdlib.native@1.8.60
#:package tdlib.native.win-x64@1.8.60
#:package ZLogger@2.5.10

using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic;
using Newtonsoft.Json;
using TdLib;
using TdLib.Bindings;
using ZLogger;
using var factory = LoggerFactory.Create(logging =>
{
    logging.SetMinimumLevel(LogLevel.Trace);

    // Add ZLogger provider to ILoggingBuilder
    logging.AddZLoggerConsole(options =>
    {
        options.CaptureThreadInfo = true;
        options.UsePlainTextFormatter(formatter =>
   {
       formatter.SetPrefixFormatter($"{0} | {1:short} | ({2}) |", (in MessageTemplate template, in LogInfo info) => template.Format(info.Timestamp, info.LogLevel, info.Category));
       //    formatter.SetSuffixFormatter($" ({0})", (in MessageTemplate template, in LogInfo info) => template.Format(info.Category));
       formatter.SetExceptionFormatter((writer, ex) => Utf8StringInterpolation.Utf8String.Format(writer, $"{ex.Message}"));
   });
    });
    logging.AddZLoggerFile("tdl.log", options =>
    {
        options.UsePlainTextFormatter(formatter =>
    {
        formatter.SetPrefixFormatter($"{0}|{1}|", (in MessageTemplate template, in LogInfo info) => template.Format(info.Timestamp, info.LogLevel));
        formatter.SetSuffixFormatter($" ({0})", (in MessageTemplate template, in LogInfo info) => template.Format(info.Category));
        formatter.SetExceptionFormatter((writer, ex) => Utf8StringInterpolation.Utf8String.Format(writer, $"{ex.Message}"));
    });
    });
    // Output Structured Logging, setup options
    // logging.AddZLoggerConsole(options => options.UseJsonFormatter());
});
var logger = factory.CreateLogger("tdl");

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

        await ConvertForwardToCopy(client);

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
        PhoneNumber = Environment.GetEnvironmentVariable("phone")
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
            var filesLocation = Path.Combine(AppContext.BaseDirectory, "db");
            await client.ExecuteAsync(new TdApi.SetTdlibParameters
            {
                ApiId = Convert.ToInt32(Environment.GetEnvironmentVariable("tdl_api_id", EnvironmentVariableTarget.User)),
                ApiHash = Environment.GetEnvironmentVariable("tdl_api_hash", EnvironmentVariableTarget.User),
                DeviceModel = "PC",
                SystemLanguageCode = "en",
                ApplicationVersion = "1.0.0",
                DatabaseDirectory = filesLocation,
                FilesDirectory = filesLocation,
                // More parameters available!
            });
            logger.ZLogInformation($"正在尝试连接代理...");
            var proxyType = new TdApi.ProxyType.ProxyTypeSocks5
            {
            };
            // 参数说明：服务器地址, 端口, 是否启用
            var proxy = await client.AddProxyAsync("127.0.0.1", 7897, true, proxyType);

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

    logger.ZLogInformation($"清理完成！总共移除违规消息: {totalDeleted} 条");
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