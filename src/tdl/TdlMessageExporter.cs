#!/usr/bin/env dotnet
#:package TDLib@*
#:package tdlib.native@*
#:package tdlib.native.win-x64@*
#:package Spectre.Console@*
#:package Spectre.Console.Ansi@*
#:package Microsoft.Extensions.Logging@*
#:package ZLogger@*
#:package YLFramework.ZLogging@1.0.3-alpha.3
#:package Newtonsoft.Json@*

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Framework.ZLogging;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using TdLib;
using TdLib.Bindings;
using ZLogger;

// 全局变量
ManualResetEventSlim ReadyToAuthenticate = new();
bool _authNeeded = false;
bool _passwordNeeded = false;
string tdlRoot = string.Empty;

// 主程序入口
using (var client = new TdClient())
{
    client.Bindings.SetLogVerbosityLevel(TdLogLevel.Fatal);
    await Main(client, args);
}

// 主函数
async Task Main(TdClient client, string[] args)
{
    // 初始化环境
    InitializeEnvironment();

    // 初始化日志
    var logger = InitializeLogger();


    client.Bindings.SetLogVerbosityLevel(TdLogLevel.Fatal);

    try
    {
        // 订阅所有事件
        client.UpdateReceived += async (_, update) => { await ProcessUpdates(client, update, logger); };

        // 等待认证就绪
        ReadyToAuthenticate.Wait();

        // 处理认证
        if (_authNeeded)
        {
            await HandleAuthentication(client, logger);
        }

        // 获取当前用户信息
        var currentUser = await GetCurrentUser(client);
        var fullUserName = $"{currentUser.FirstName} {currentUser.LastName}".Trim();
        logger.ZLogInformation($"成功登录为 [{currentUser.Id}] / [@{currentUser.Usernames?.ActiveUsernames[0]}] / [{fullUserName}]");

        long chatId = await GetChatIdFromUsernameAsync(client, ExtractUsername("https://t.me/atsJoe"), logger);

        if (chatId == 0)
        {
            logger.ZLogError($"无法获取频道 ID，请检查链接是否正确");
            return;
        }

        // 导出频道消息
        await ExportChannelMessages(client, chatId, logger);

        logger.ZLogInformation($"导出完成，请按 ENTER 退出应用");
        Console.ReadLine();
    }
    catch (Exception ex)
    {
        logger.ZLogError(ex, $"发生异常");
    }

}

// 初始化环境
void InitializeEnvironment()
{
    // 获取用户主目录
    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // 拼接 .tdl 目录
    tdlRoot = Path.Combine(userProfile, ".tdl");

    // 确保目录存在
    if (!Directory.Exists(tdlRoot))
    {
        Directory.CreateDirectory(tdlRoot);
    }
}

// 初始化日志
ILogger InitializeLogger()
{
    var factory = LoggerFactory.Create(logging =>
    {
        logging.SetMinimumLevel(LogLevel.Trace);
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
    return factory.CreateLogger("tdl");
}

// 处理认证
async Task HandleAuthentication(TdClient client, ILogger logger)
{
    // 设置电话号码
    await client.ExecuteAsync(new TdApi.SetAuthenticationPhoneNumber
    {
        PhoneNumber = Environment.GetEnvironmentVariable("tdl_phone", EnvironmentVariableTarget.User)
    });

    // 输入验证码
    Console.Write("请输入登录验证码: ");
    var code = Console.ReadLine();

    await client.ExecuteAsync(new TdApi.CheckAuthenticationCode
    {
        Code = code
    });

    if (!_passwordNeeded) { return; }

    // 输入 2FA 密码
    Console.Write("请输入密码: ");
    var password = Console.ReadLine();

    await client.ExecuteAsync(new TdApi.CheckAuthenticationPassword
    {
        Password = password
    });
}

// 处理更新
async Task ProcessUpdates(TdClient client, TdApi.Update update, ILogger logger)
{
    switch (update)
    {
        case TdApi.Update.UpdateAuthorizationState { AuthorizationState: TdApi.AuthorizationState.AuthorizationStateWaitTdlibParameters }:
            // 设置 TDLib 参数
            await client.ExecuteAsync(new TdApi.SetTdlibParameters
            {
                ApiId = Convert.ToInt32(Environment.GetEnvironmentVariable("tdl_api_id", EnvironmentVariableTarget.User)),
                ApiHash = Environment.GetEnvironmentVariable("tdl_api_hash", EnvironmentVariableTarget.User),
                DeviceModel = "PC",
                SystemLanguageCode = "zh-CN",
                ApplicationVersion = "1.0.0",
                DatabaseDirectory = Path.Combine(tdlRoot, "db"),
                FilesDirectory = Path.Combine(tdlRoot, "files"),
                UseFileDatabase = true,
                UseChatInfoDatabase = true,
                UseMessageDatabase = true,
            });

            // 启用代理
            logger.ZLogInformation($"正在尝试连接代理...");
            var proxy = await client.AddProxyAsync(new TdApi.Proxy() { Server = "127.0.0.1", Port = 7897, Type = new TdApi.ProxyType.ProxyTypeSocks5() }, true);
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
            // 连接状态更新
            break;

        default:
            // 其他更新
            break;
    }
}

// 获取当前用户
async Task<TdApi.User> GetCurrentUser(TdClient client)
{
    return await client.ExecuteAsync(new TdApi.GetMe());
}

// 通过链接获取聊天 ID
async Task<long> GetChatIdFromUsernameAsync(TdClient client, string username, ILogger logger)
{
    try
    {
        var linkInfo = await client.SearchPublicChatAsync(username);

        if (linkInfo != null)
        {
            return linkInfo.Id;
        }

        logger.ZLogWarning($"链接解析成功，但未直接关联到消息。");
    }
    catch (TdException ex)
    {
        logger.ZLogError(ex, $"无法解析链接: {username}");
    }
    return 0;
}

// 导出频道消息
async Task ExportChannelMessages(TdClient client, long chatId, ILogger logger)
{
    // 从最新消息开始
    long lastMessageId = 0;
    bool hasMore = true;
    List<MessageInfo> messages = new List<MessageInfo>();

    logger.ZLogInformation($"开始导出频道 {chatId} 的所有消息...");

    while (hasMore)
    {
        try
        {
            // 获取历史消息
            var history = await client.GetChatHistoryAsync(chatId, lastMessageId, 0, 100, false);

            if (history.Messages_ == null || history.Messages_.Length == 0)
            {
                logger.ZLogInformation($"已到达频道的最久远的一条消息。导出结束！");
                hasMore = false;
                break;
            }

            // 处理每条消息
            foreach (var message in history.Messages_)
            {
                var messageInfo = CreateMessageInfo(message);
                messages.Add(messageInfo);
            }

            // 更新 lastMessageId
            lastMessageId = history.Messages_.Last().Id;

            logger.ZLogInformation($"已处理 {messages.Count} 条消息，当前进度 ID: {lastMessageId}");

            // 防封限速
            await Task.Delay(1500);
        }
        catch (TdException ex) when (ex.Error.Code == 429)
        {
            int retryAfter = 10;
            logger.ZLogWarning($"触发频率限制，等待 {retryAfter} 秒后继续...");
            await Task.Delay(retryAfter * 1000);
        }
        catch (Exception ex)
        {
            logger.ZLogError(ex, $"导出过程中发生异常");
            await Task.Delay(5000);
        }
    }

    // 保存为 JSON 文件
    SaveMessagesToJson(messages, chatId, logger);

    logger.ZLogInformation($"导出完成，共导出 {messages.Count} 条消息");
}
string? ExtractUsername(string input)
{
    if (string.IsNullOrWhiteSpace(input))
        return null;

    input = input.Trim();

    // 1️⃣ @username 形式
    if (input.StartsWith("@"))
        return input.Substring(1);

    // 2️⃣ 直接 username（无 URL）
    if (!input.Contains("/"))
        return input;

    // 3️⃣ 统一处理 URL
    // 支持 t.me / telegram.me
    var match = Regex.Match(input,
        @"(?:https?:\/\/)?(?:t\.me|telegram\.me)\/(?<name>[^\/\?\#]+)",
        RegexOptions.IgnoreCase);

    if (!match.Success)
        return null;

    var name = match.Groups["name"].Value;

    // 4️⃣ 过滤邀请码（+xxxx）
    if (name.StartsWith("+"))
        return null;

    return name;
}
// 创建消息信息对象
MessageInfo CreateMessageInfo(TdApi.Message message)
{
    var messageInfo = new MessageInfo
    {
        MessageId = message.Id,
        Date = DateTimeOffset.FromUnixTimeSeconds(message.Date).DateTime,
        Type = GetMessageType(message.Content),
        FileId = GetFileId(message.Content),
        FileName = GetFileName(message.Content),
        Text = GetText(message.Content),
        Width = GetWidth(message.Content),
        Height = GetHeight(message.Content),
        Duration = GetDuration(message.Content),
        MimeType = GetMimeType(message.Content),
        FileSize = GetFileSize(message.Content)
    };

    return messageInfo;
}

// 获取消息类型
string GetMessageType(TdApi.MessageContent content)
{
    return content switch
    {
        TdApi.MessageContent.MessageText => "Text",
        TdApi.MessageContent.MessagePhoto => "Photo",
        TdApi.MessageContent.MessageVideo => "Video",
        TdApi.MessageContent.MessageAudio => "Audio",
        TdApi.MessageContent.MessageDocument => "Document",
        TdApi.MessageContent.MessageVoiceNote => "VoiceNote",
        TdApi.MessageContent.MessageVideoNote => "VideoNote",
        TdApi.MessageContent.MessageSticker => "Sticker",
        TdApi.MessageContent.MessageAnimation => "Animation",
        TdApi.MessageContent.MessageContact => "Contact",
        TdApi.MessageContent.MessageLocation => "Location",
        TdApi.MessageContent.MessageVenue => "Venue",
        TdApi.MessageContent.MessagePoll => "Poll",
        TdApi.MessageContent.MessageDice => "Dice",
        TdApi.MessageContent.MessageGame => "Game",
        TdApi.MessageContent.MessageInvoice => "Invoice",
        TdApi.MessageContent.MessageCall => "Call",
        TdApi.MessageContent.MessagePinMessage => "PinMessage",
        TdApi.MessageContent.MessageScreenshotTaken => "ScreenshotTaken",
        TdApi.MessageContent.MessageChatChangePhoto => "ChatChangePhoto",
        TdApi.MessageContent.MessageChatChangeTitle => "ChatChangeTitle",
        TdApi.MessageContent.MessageChatDeletePhoto => "ChatDeletePhoto",
        TdApi.MessageContent.MessageChatAddMembers => "ChatAddMembers",
        TdApi.MessageContent.MessageChatJoinByLink => "ChatJoinByLink",
        TdApi.MessageContent.MessageChatJoinByRequest => "ChatJoinByRequest",
        TdApi.MessageContent.MessageChatDeleteMember => "ChatDeleteMember",
        TdApi.MessageContent.MessageCustomServiceAction => "CustomServiceAction",
        TdApi.MessageContent.MessageGiftedPremium => "GiftedPremium",
        TdApi.MessageContent.MessageStory => "Story",
        _ => "Unknown"
    };
}

// 获取文件 ID
long? GetFileId(TdApi.MessageContent content)
{
    return content switch
    {
        TdApi.MessageContent.MessagePhoto photo => photo.Photo.Sizes.LastOrDefault()?.Photo.Id,
        TdApi.MessageContent.MessageVideo video => video.Video.Video_.Id,
        TdApi.MessageContent.MessageAudio audio => audio.Audio.Audio_.Id,
        TdApi.MessageContent.MessageDocument document => document.Document.Document_.Id,
        TdApi.MessageContent.MessageVoiceNote voiceNote => voiceNote.VoiceNote.Voice.Id,
        TdApi.MessageContent.MessageVideoNote videoNote => videoNote.VideoNote.Video.Id,
        TdApi.MessageContent.MessageAnimation animation => animation.Animation.Animation_.Id,
        _ => null
    };
}

// 获取文件名
string GetFileName(TdApi.MessageContent content)
{
    return content switch
    {
        TdApi.MessageContent.MessageDocument document => document.Document.FileName,
        TdApi.MessageContent.MessageVideo video => video.Video.FileName,
        TdApi.MessageContent.MessageAudio audio => audio.Audio.FileName,
        TdApi.MessageContent.MessageAnimation animation => animation.Animation.FileName,
        _ => null
    };
}

// 获取文本内容
string GetText(TdApi.MessageContent content)
{
    return content switch
    {
        TdApi.MessageContent.MessageText text => text.Text.Text,
        TdApi.MessageContent.MessagePhoto photo => photo.Caption?.Text,
        TdApi.MessageContent.MessageVideo video => video.Caption?.Text,
        TdApi.MessageContent.MessageAudio audio => audio.Caption?.Text,
        TdApi.MessageContent.MessageDocument document => document.Caption?.Text,
        TdApi.MessageContent.MessageVoiceNote voiceNote => voiceNote.Caption?.Text,
        TdApi.MessageContent.MessageAnimation animation => animation.Caption?.Text,
        _ => null
    };
}

// 获取宽度
int? GetWidth(TdApi.MessageContent content)
{
    return content switch
    {
        TdApi.MessageContent.MessageVideo video => video.Video.Width,
        TdApi.MessageContent.MessageAnimation animation => animation.Animation.Width,
        _ => null
    };
}

// 获取高度
int? GetHeight(TdApi.MessageContent content)
{
    return content switch
    {
        TdApi.MessageContent.MessageVideo video => video.Video.Height,
        TdApi.MessageContent.MessageAnimation animation => animation.Animation.Height,
        _ => null
    };
}

// 获取持续时间
int? GetDuration(TdApi.MessageContent content)
{
    return content switch
    {
        TdApi.MessageContent.MessageVideo video => video.Video.Duration,
        TdApi.MessageContent.MessageAudio audio => audio.Audio.Duration,
        TdApi.MessageContent.MessageVoiceNote voiceNote => voiceNote.VoiceNote.Duration,
        TdApi.MessageContent.MessageVideoNote videoNote => videoNote.VideoNote.Duration,
        TdApi.MessageContent.MessageAnimation animation => animation.Animation.Duration,
        _ => null
    };
}

// 获取 MIME 类型
string GetMimeType(TdApi.MessageContent content)
{
    return content switch
    {
        TdApi.MessageContent.MessageVideo video => video.Video.MimeType,
        TdApi.MessageContent.MessageAudio audio => audio.Audio.MimeType,
        TdApi.MessageContent.MessageDocument document => document.Document.MimeType,
        TdApi.MessageContent.MessageVoiceNote voiceNote => voiceNote.VoiceNote.MimeType,
        TdApi.MessageContent.MessageAnimation animation => animation.Animation.MimeType,
        _ => null
    };
}

// 获取文件大小
long? GetFileSize(TdApi.MessageContent content)
{
    return null;
}

// 保存消息到 JSON 文件
void SaveMessagesToJson(List<MessageInfo> messages, long chatId, ILogger logger)
{
    // 构建存储路径
    string savePath = Path.Combine("data", "tdl", "message");
    string fileName = $"{chatId}.json";
    string fullPath = Path.Combine(savePath, fileName);

    // 确保目录存在
    if (!Directory.Exists(savePath))
    {
        Directory.CreateDirectory(savePath);
        logger.ZLogInformation($"创建存储目录: {savePath}");
    }

    // 序列化并保存
    string json = JsonConvert.SerializeObject(messages, Formatting.Indented);
    File.WriteAllText(fullPath, json);

    logger.ZLogInformation($"消息已保存到: {fullPath}");
}

// 消息信息类
public class MessageInfo
{
    public long MessageId { get; set; }
    public DateTime Date { get; set; }
    public string Type { get; set; }
    public long? FileId { get; set; }
    public string FileName { get; set; }
    public ForwardInfo ForwardInfo { get; set; }
    public string Text { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? Duration { get; set; }
    public string MimeType { get; set; }
    public long? FileSize { get; set; }
}

// 转发信息类
public class ForwardInfo
{
    public long FromChatId { get; set; }
    public long FromMessageId { get; set; }
    public DateTime Date { get; set; }
}
