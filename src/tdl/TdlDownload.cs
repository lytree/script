#:package TDLib@1.8.60
#:package tdlib.native@1.8.60
#:package tdlib.native.win-x64@1.8.60
#:package ZLogger@2.5.10
#:package YLFramework.ZLogging@1.0.1
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
    logging.AddZLoggerConsoleWithColors((b) => { b.LogVerbosity = LogVerbosity.DataTimeUtcLogLevelCategory; });

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

        var fileId = await GetFileIdFromLinkAsync(client, "https://t.me/R_E_STUDIO/21233");

        await client.DownloadFileAsync(fileId, 12, 0, 0, false);

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
async Task<TdApi.User> GetCurrentUser(TdClient client)
{
    return await client.ExecuteAsync(new TdApi.GetMe());
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