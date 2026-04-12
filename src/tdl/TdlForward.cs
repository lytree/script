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
/// 将链接转发消息转换为深度copy
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
               (in MessageTemplate template, in LogInfo i) =>
               {
                   template.Format(
                               i.Timestamp,
                               i.LogLevel,
                               i.Category);
               });
        });
    });
});
var logger = factory.CreateLogger("tdl");


Console.WriteLine(Environment.CurrentDirectory);
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

        await ProcessLinkQueue(client, ["https://t.me/atsJoe"]);


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
async Task ProcessLinkQueue(TdClient client, List<string> links)
{
    // 获取当前账号的“收藏夹”ChatId (通常是自己的 UserID)
    var me = await client.GetMeAsync();
    long myId = me.Id;

    foreach (var link in links)
    {
        try
        {
            logger.ZLogInformation($"正在处理链接: {link}");

            // 1. 解析链接获取原始消息
            var linkInfo = await client.GetMessageLinkInfoAsync(link);
            if (linkInfo.Message == null) continue;

            var msg = linkInfo.Message;

            // 2. 执行“深度 Copy”到收藏夹
            var result = await client.ForwardMessagesAsync(
                chatId: myId,              // 目标：还是收藏夹
                fromChatId: msg.ChatId,          // 来源：从收藏夹里读
                messageIds: [msg.Id],
                sendCopy: true,            // 关键：剥离来源信息，实现深度拷贝
                removeCaption: false
            );

            logger.ZLogInformation($"已深度 Copy 到收藏夹。");

        }
        catch (Exception ex)
        {
            logger.ZLogError(ex, $"处理链接 {link} 时出错");
        }
    }
}