#:package TDLib@*
#:package tdlib.native@*
#:package tdlib.native.win-x64@*
#:package Spectre.Console@*
#:package Spectre.Console.Ansi@*
#:package Microsoft.Extensions.Logging@*
#:package ZLogger@*
#:package YLFramework.ZLogging@1.0.3-alpha.6
#:include TdlUpdateHandler.cs
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

    logging.AddZLoggerSpectreConsoleAndFile("tdl.log");
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
TdlUpdateHandler _updateHandler;
using (var client = new TdClient())
{
    client.Bindings.SetLogVerbosityLevel(TdLogLevel.Fatal);



    try
    {
        _updateHandler = new TdlUpdateHandler(ReadyToAuthenticate, logger)
            .OnConfigureTdlibParameters(ConfigureTdlibParameters)
            .OnFileUpdate(HandleFileUpdate);

        client.UpdateReceived += async (_, update) => { await _updateHandler.ProcessUpdates(client, update, tdlRoot); };

        // Waiting until we get enough events to be in 'authentication ready' state
        ReadyToAuthenticate.Wait();
        // We may not need to authenticate since TdLib persists session in 'td.binlog' file.
        // See 'TdlibParameters' class for more information, or:
        // https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1tdlib_parameters.html
        if (_updateHandler.AuthNeeded)
        {
            // Interactively handling authentication
            await HandleAuthentication(client);
        }

        // Querying info about current user and some channels
        var currentUser = await GetCurrentUser(client);

        var fullUserName = $"{currentUser.FirstName} {currentUser.LastName}".Trim();
        logger.ZLogInformation($"成功登录为 [[{currentUser.Id}]] / [[@{currentUser.Usernames?.ActiveUsernames[0]}]] / [[{fullUserName}]]");

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

    if (!_updateHandler.PasswordNeeded) { return; }


    // 2FA may be enabled. Cloud password is required in that case.
    Console.Write("Insert the password: ");
    var password = Console.ReadLine();

    await client.ExecuteAsync(new TdApi.CheckAuthenticationPassword
    {
        Password = password
    });
}

async Task ConfigureTdlibParameters(TdClient client, string outputPath, ILogger cbLogger)
{
    await client.ExecuteAsync(new TdApi.SetTdlibParameters
    {
        ApiId = Convert.ToInt32(Environment.GetEnvironmentVariable("tdl_api_id", EnvironmentVariableTarget.User)),
        ApiHash = Environment.GetEnvironmentVariable("tdl_api_hash", EnvironmentVariableTarget.User),
        DeviceModel = "PC",
        SystemLanguageCode = "en",
        ApplicationVersion = "1.0.0",
        DatabaseDirectory = Path.Combine(tdlRoot, "db"),
        FilesDirectory = Path.Combine(tdlRoot, "files"),
        UseFileDatabase = true,
        UseChatInfoDatabase = true,
        UseMessageDatabase = true,
    });
    logger.ZLogInformation($"正在尝试连接代理...");
    var proxy = await client.AddProxyAsync(new TdApi.Proxy() { Server = "127.0.0.1", Port = 7897, Type = new TdApi.ProxyType.ProxyTypeSocks5() }, true);
    await client.EnableProxyAsync(proxy.Id);
    logger.ZLogInformation($"代理已启用。");
}

async Task HandleFileUpdate(TdApi.File file, string outputPath, ILogger cbLogger)
{
    if (file.Local.IsDownloadingActive)
    {
        double percent = (double)file.Local.DownloadedSize / file.ExpectedSize * 100;
        logger.ZLogTrace($"文件 {file.Id} 进度: {percent:F1}%");
    }
    else if (file.Local.IsDownloadingCompleted)
    {
        logger.ZLogInformation($"文件下载完成！本地路径: {file.Local.Path}");
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
    // 获取当前账号的"收藏夹"ChatId (通常是自己的 UserID)
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

            // 2. 执行"深度 Copy"到收藏夹
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
