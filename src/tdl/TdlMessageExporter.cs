#!/usr/bin/env dotnet

#:include ../../env.cs
#:include TdlUpdateHandler.cs

#:package TDLib@*
#:package tdlib.native@*
#:package tdlib.native.win-x64@*
#:package System.CommandLine@*
#:package Spectre.Console@*
#:package Spectre.Console.Ansi@*
#:package Microsoft.Extensions.Logging@*
#:package ZLogger@*
#:package YLFramework.ZLogging@1.0.3-alpha.7

using System.CommandLine;
using System.Text.Json;
using System.Text.RegularExpressions;
using Framework.ZLogging;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using TdLib;
using TdLib.Bindings;
using ZLogger;

using (var client = new TdClient())
{
    client.Bindings.SetLogVerbosityLevel(TdLogLevel.Fatal);
    await Main(client, args);
}

async Task Main(TdClient client, string[] args)
{
    var logger = InitializeLogger();

    var optionChannel = new Option<string>("--channel") { Required = true, Description = "频道/群聊链接或用户名 (支持私有链接和公开频道)" };
    var optionOutput = new Option<string?>("--output") { Required = false, Description = "输出文件路径 (默认: data/tdl/message/{chatId}.json)" };
    var optionComments = new Option<bool>("--comments") { DefaultValueFactory = _ => false, Description = "是否导出评论" };
    var optionLimit = new Option<int>("--limit") { DefaultValueFactory = _ => 0, Description = "最大导出数量, 0=全部" };

    var rootCommand = new RootCommand("导出频道消息为JSON (支持分组和评论)");
    rootCommand.Options.Add(optionChannel);
    rootCommand.Options.Add(optionOutput);
    rootCommand.Options.Add(optionComments);
    rootCommand.Options.Add(optionLimit);

    var parseResult = rootCommand.Parse(args);
    var channelLink = parseResult.GetValue(optionChannel);
    var outputPath = parseResult.GetValue(optionOutput);
    var exportComments = parseResult.GetValue(optionComments);
    var limit = parseResult.GetValue(optionLimit);

    string tdlRoot = InitializeEnvironment(logger);

    ManualResetEventSlim ready = new();
    var handler = new TdlUpdateHandler(ready, logger)
        .OnConfigureTdlibParameters(ConfigureTdlibParameters)
        .OnFileUpdate(HandleFileUpdate);

    client.UpdateReceived += async (_, update) => { await handler.ProcessUpdates(client, update, tdlRoot); };
    ready.Wait();

    if (handler.AuthNeeded)
    {
        await HandleAuthentication(client, handler);
    }

    var currentUser = await client.ExecuteAsync(new TdApi.GetMe());
    var fullUserName = $"{currentUser.FirstName} {currentUser.LastName}".Trim();
    logger.ZLogInformation($"成功登录为 [[{currentUser.Id}]] / [[@{currentUser.Usernames?.ActiveUsernames[0]}]] / [[{fullUserName}]]");

    long chatId = await ResolveChatIdAsync(client, channelLink, logger);
    if (chatId == 0)
    {
        logger.ZLogError($"无法解析频道: {channelLink}");
        return;
    }

    var chat = await client.GetChatAsync(chatId);
    logger.ZLogInformation($"目标: [{chat.Title}] ChatId={chatId}");

    if (string.IsNullOrWhiteSpace(outputPath))
    {
        string saveDir = Path.Combine("data", "tdl", "message");
        Directory.CreateDirectory(saveDir);
        outputPath = Path.Combine(saveDir, $"{chatId}.json");
    }

    var exportResult = await ExportChannelMessages(client, chatId, exportComments, limit, logger);

    var jsonOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    string json = JsonSerializer.Serialize(exportResult, jsonOptions);

    string? dir = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
    {
        Directory.CreateDirectory(dir);
    }

    await File.WriteAllTextAsync(outputPath, json);
    logger.ZLogInformation($"导出完成，共 {exportResult.TotalMessages} 条消息，{exportResult.Groups.Count} 个分组");
    logger.ZLogInformation($"文件已保存到: {outputPath}");

    Console.WriteLine("按 ENTER 键退出");
    Console.ReadLine();
}

async Task<ChannelExport> ExportChannelMessages(TdClient client, long chatId, bool exportComments, int limit, ILogger logger)
{
    long fromMessageId = 0;
    bool hasMore = true;
    var allMessages = new List<TdApi.Message>();
    int totalCount = 0;

    logger.ZLogInformation($"开始导出频道消息...");

    while (hasMore)
    {
        try
        {
            var history = await client.GetChatHistoryAsync(chatId, fromMessageId, 0, 100, false);
            if (history.Messages_ == null || history.Messages_.Length == 0)
            {
                hasMore = false;
                break;
            }

            allMessages.AddRange(history.Messages_);
            totalCount += history.Messages_.Length;

            fromMessageId = history.Messages_.Last().Id;
            logger.ZLogInformation($"已拉取 {totalCount} 条消息，当前进度 ID: {fromMessageId}");

            if (limit > 0 && totalCount >= limit)
            {
                hasMore = false;
            }

            await Task.Delay(300);
        }
        catch (TdException ex) when (ex.Error.Code == 429)
        {
            int retryAfter = ParseRetryAfter(ex);
            logger.ZLogWarning($"触发频率限制，等待 {retryAfter} 秒后继续...");
            await Task.Delay(retryAfter * 1000);
        }
        catch (Exception ex)
        {
            logger.ZLogError(ex, $"拉取消息时发生异常");
            await Task.Delay(5000);
        }
    }

    if (limit > 0 && allMessages.Count > limit)
    {
        allMessages = allMessages.Take(limit).ToList();
    }

    var chat = await client.GetChatAsync(chatId);
    var export = new ChannelExport
    {
        ChatId = chatId,
        ChatTitle = chat.Title,
        ExportTime = DateTime.UtcNow,
        TotalMessages = allMessages.Count
    };

    var groups = GroupMessagesByAlbum(allMessages);

    foreach (var group in groups)
    {
        var exportGroup = new MessageGroup
        {
            MediaAlbumId = group.First().MediaAlbumId != 0 ? group.First().MediaAlbumId.ToString() : null,
            IsGrouped = group.Count > 1 && group.First().MediaAlbumId != 0
        };

        foreach (var msg in group)
        {
            var msgInfo = BuildMessageInfo(msg);

            if (exportComments)
            {
                try
                {
                    var comments = await client.GetMessageThreadHistoryAsync(
                        chatId: chatId,
                        messageId: msg.Id,
                        fromMessageId: 0,
                        offset: 0,
                        limit: 50
                    );

                    if (comments.Messages_ != null && comments.Messages_.Length > 0)
                    {
                        msgInfo.Comments = comments.Messages_.Select(BuildMessageInfo).ToList();
                        logger.ZLogTrace($"MsgId={msg.Id} 有 {comments.Messages_.Length} 条评论");
                    }
                }
                catch (TdException ex)
                {
                    logger.ZLogWarning($"获取评论失败: MsgId={msg.Id}, 错误: {ex.Error.Message}");
                }

                await Task.Delay(200);
            }

            exportGroup.Messages.Add(msgInfo);
        }

        export.Groups.Add(exportGroup);
        logger.ZLogInformation($"已处理分组 {export.Groups.Count}/{groups.Count} (消息数: {group.Count})");
    }

    return export;
}

MessageInfo BuildMessageInfo(TdApi.Message msg)
{
    var info = new MessageInfo
    {
        MessageId = msg.Id,
        Date = DateTimeOffset.FromUnixTimeSeconds(msg.Date).DateTime,
        EditDate = msg.EditDate != 0 ? DateTimeOffset.FromUnixTimeSeconds(msg.EditDate).DateTime : null,
        Type = GetMessageType(msg.Content),
        Text = GetText(msg.Content),
        Media = GetMediaInfo(msg.Content),
        ForwardInfo = msg.ForwardInfo != null ? new ForwardInfoExport
        {
            FromChatId = msg.ForwardInfo.Source?.ChatId ?? 0,
            FromMessageId = msg.ForwardInfo.Source?.MessageId ?? 0,
            Date = msg.ForwardInfo.Date != 0 ? DateTimeOffset.FromUnixTimeSeconds(msg.ForwardInfo.Date).DateTime : null,
            Origin = msg.ForwardInfo.Origin switch
            {
                TdApi.MessageOrigin.MessageOriginUser ou => $"User:{ou.SenderUserId}",
                TdApi.MessageOrigin.MessageOriginChannel oc => $"Channel:{oc.ChatId}:{oc.MessageId}",
                TdApi.MessageOrigin.MessageOriginHiddenUser ohu => $"Hidden:{ohu.SenderName}",
                TdApi.MessageOrigin.MessageOriginChat oc => $"Chat:{oc.SenderChatId}",
                _ => null
            }
        } : null
    };

    return info;
}

MediaInfo? GetMediaInfo(TdApi.MessageContent content)
{
    return content switch
    {
        TdApi.MessageContent.MessagePhoto p => new MediaInfo
        {
            Type = "Photo",
            FileId = p.Photo.Sizes.LastOrDefault()?.Photo.Id.ToString(),
            Width = p.Photo.Sizes.LastOrDefault()?.Width,
            Height = p.Photo.Sizes.LastOrDefault()?.Height,
            FileSize = p.Photo.Sizes.LastOrDefault()?.Photo.ExpectedSize
        },
        TdApi.MessageContent.MessageVideo v => new MediaInfo
        {
            Type = "Video",
            FileId = v.Video.Video_.Id.ToString(),
            FileName = v.Video.FileName,
            Width = v.Video.Width,
            Height = v.Video.Height,
            Duration = v.Video.Duration,
            MimeType = v.Video.MimeType,
            FileSize = v.Video.Video_.ExpectedSize
        },
        TdApi.MessageContent.MessageAudio a => new MediaInfo
        {
            Type = "Audio",
            FileId = a.Audio.Audio_.Id.ToString(),
            FileName = a.Audio.FileName,
            Duration = a.Audio.Duration,
            MimeType = a.Audio.MimeType,
            FileSize = a.Audio.Audio_.ExpectedSize
        },
        TdApi.MessageContent.MessageDocument d => new MediaInfo
        {
            Type = "Document",
            FileId = d.Document.Document_.Id.ToString(),
            FileName = d.Document.FileName,
            MimeType = d.Document.MimeType,
            FileSize = d.Document.Document_.ExpectedSize
        },
        TdApi.MessageContent.MessageVoiceNote vn => new MediaInfo
        {
            Type = "VoiceNote",
            FileId = vn.VoiceNote.Voice.Id.ToString(),
            Duration = vn.VoiceNote.Duration,
            MimeType = vn.VoiceNote.MimeType,
            FileSize = vn.VoiceNote.Voice.ExpectedSize
        },
        TdApi.MessageContent.MessageVideoNote vn => new MediaInfo
        {
            Type = "VideoNote",
            FileId = vn.VideoNote.Video.Id.ToString(),
            Duration = vn.VideoNote.Duration,
            FileSize = vn.VideoNote.Video.ExpectedSize
        },
        TdApi.MessageContent.MessageAnimation ani => new MediaInfo
        {
            Type = "Animation",
            FileId = ani.Animation.Animation_.Id.ToString(),
            FileName = ani.Animation.FileName,
            Width = ani.Animation.Width,
            Height = ani.Animation.Height,
            Duration = ani.Animation.Duration,
            MimeType = ani.Animation.MimeType,
            FileSize = ani.Animation.Animation_.ExpectedSize
        },
        TdApi.MessageContent.MessageSticker s => new MediaInfo
        {
            Type = "Sticker",
            FileId = s.Sticker.Sticker_.Id.ToString(),
            Width = s.Sticker.Width,
            Height = s.Sticker.Height,
            FileSize = s.Sticker.Sticker_.ExpectedSize
        },
        _ => null
    };
}

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
        TdApi.MessageContent.MessageStory => "Story",
        TdApi.MessageContent.MessageUnsupported => "Unsupported",
        _ => content.GetType().Name.Replace("Message", "")
    };
}

string? GetText(TdApi.MessageContent content)
{
    return content switch
    {
        TdApi.MessageContent.MessageText t => t.Text?.Text,
        TdApi.MessageContent.MessagePhoto p => p.Caption?.Text,
        TdApi.MessageContent.MessageVideo v => v.Caption?.Text,
        TdApi.MessageContent.MessageAudio a => a.Caption?.Text,
        TdApi.MessageContent.MessageDocument d => d.Caption?.Text,
        TdApi.MessageContent.MessageVoiceNote vn => vn.Caption?.Text,
        TdApi.MessageContent.MessageAnimation ani => ani.Caption?.Text,
        _ => null
    };
}

List<List<TdApi.Message>> GroupMessagesByAlbum(List<TdApi.Message> messages)
{
    var result = new List<List<TdApi.Message>>();
    if (messages.Count == 0) return result;

    var currentGroup = new List<TdApi.Message> { messages[0] };
    long currentAlbumId = messages[0].MediaAlbumId;

    for (int i = 1; i < messages.Count; i++)
    {
        if (messages[i].MediaAlbumId != 0 && messages[i].MediaAlbumId == currentAlbumId)
        {
            currentGroup.Add(messages[i]);
        }
        else
        {
            result.Add(currentGroup);
            currentGroup = new List<TdApi.Message> { messages[i] };
            currentAlbumId = messages[i].MediaAlbumId;
        }
    }

    result.Add(currentGroup);
    return result;
}

async Task HandleAuthentication(TdClient client, TdlUpdateHandler handler)
{
    await client.ExecuteAsync(new TdApi.SetAuthenticationPhoneNumber
    {
        PhoneNumber = Environment.GetEnvironmentVariable("tdl_phone", EnvironmentVariableTarget.User)
    });

    Console.Write("输入登录验证码: ");
    var code = Console.ReadLine();
    await client.ExecuteAsync(new TdApi.CheckAuthenticationCode { Code = code });

    if (!handler.PasswordNeeded) { return; }

    Console.Write("输入密码: ");
    var password = Console.ReadLine();
    await client.ExecuteAsync(new TdApi.CheckAuthenticationPassword { Password = password });
}

async Task ConfigureTdlibParameters(TdClient client, string outputPath, ILogger cbLogger)
{
    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    string tdlRoot = Path.Combine(userProfile, ".tdl");

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

    cbLogger.ZLogInformation($"正在尝试连接代理...");
    var proxy = await client.AddProxyAsync(new TdApi.Proxy() { Server = "127.0.0.1", Port = 7897, Type = new TdApi.ProxyType.ProxyTypeSocks5() }, true);
    await client.EnableProxyAsync(proxy.Id);
    cbLogger.ZLogInformation($"代理已启用。");
}

Task HandleFileUpdate(TdApi.File file, string outputPath, ILogger cbLogger)
{
    if (file.Local.IsDownloadingActive)
    {
        double percent = (double)file.Local.DownloadedSize / file.ExpectedSize * 100;
        cbLogger.ZLogTrace($"文件 {file.Id} 进度: {percent:F1}%");
    }
    else if (file.Local.IsDownloadingCompleted)
    {
        cbLogger.ZLogInformation($"文件下载完成！本地路径: {file.Local.Path}");
    }
    return Task.CompletedTask;
}

async Task<long> ResolveChatIdAsync(TdClient client, string? link, ILogger logger)
{
    if (string.IsNullOrWhiteSpace(link)) return 0;

    try
    {
        var linkInfo = await client.GetMessageLinkInfoAsync(link);
        if (linkInfo.Message != null)
        {
            return linkInfo.Message.ChatId;
        }
    }
    catch (TdException) { }

    try
    {
        if (IsInviteLink(link))
        {
            var inviteInfo = await client.CheckChatInviteLinkAsync(link);
            if (inviteInfo.ChatId != 0)
            {
                logger.ZLogInformation($"邀请链接已关联到 ChatId: {inviteInfo.ChatId}");
                return inviteInfo.ChatId;
            }
            return 0;
        }
    }
    catch (TdException) { }

    try
    {
        var username = ExtractUsername(link);
        if (!string.IsNullOrEmpty(username))
        {
            var chat = await client.SearchPublicChatAsync(username);
            if (chat != null)
            {
                return chat.Id;
            }
        }
    }
    catch (TdException) { }

    if (long.TryParse(link.Trim(), out long chatId))
    {
        return chatId;
    }

    try
    {
        var chatIds = await client.GetChatsAsync(limit: 200);
        if (chatIds?.ChatIds != null)
        {
            foreach (var id in chatIds.ChatIds)
            {
                try
                {
                    var chat = await client.GetChatAsync(id);
                    if (chat.Title.Contains(link, StringComparison.OrdinalIgnoreCase))
                    {
                        logger.ZLogInformation($"找到匹配聊天: [{chat.Title}] ChatId={chat.Id}");
                        return chat.Id;
                    }
                }
                catch { }
            }
        }
    }
    catch { }

    return 0;
}

bool IsInviteLink(string input)
{
    if (string.IsNullOrWhiteSpace(input)) return false;
    input = input.Trim();
    if (input.StartsWith("https://t.me/+", StringComparison.OrdinalIgnoreCase)) return true;
    if (input.StartsWith("https://t.me/joinchat/", StringComparison.OrdinalIgnoreCase)) return true;
    if (input.StartsWith("https://telegram.me/+", StringComparison.OrdinalIgnoreCase)) return true;
    if (input.StartsWith("https://telegram.me/joinchat/", StringComparison.OrdinalIgnoreCase)) return true;
    return false;
}

string? ExtractUsername(string input)
{
    if (string.IsNullOrWhiteSpace(input)) return null;
    input = input.Trim();
    if (input.StartsWith("@")) return input.Substring(1);
    if (!input.Contains("/")) return null;

    var match = Regex.Match(input,
        @"(?:https?:\/\/)?(?:t\.me|telegram\.me)\/(?<name>[^\/\?\#]+)",
        RegexOptions.IgnoreCase);

    if (!match.Success) return null;
    var name = match.Groups["name"].Value;
    if (name.StartsWith("+")) return null;
    return name;
}

int ParseRetryAfter(TdException ex)
{
    if (ex.Error?.Message != null)
    {
        var match = Regex.Match(ex.Error.Message, @"(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int seconds) && seconds > 0)
        {
            return Math.Min(seconds + 2, 300);
        }
    }
    return 15;
}

ILogger InitializeLogger()
{
    var factory = LoggerFactory.Create(logging =>
    {
        logging.SetMinimumLevel(LogLevel.Information);
        logging.AddZLoggerSpectreConsoleAndFile("tdl-export.log");
    });
    return factory.CreateLogger("tdl-export");
}

string InitializeEnvironment(ILogger logger)
{
    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    string tdlRoot = Path.Combine(userProfile, ".tdl");
    if (!Directory.Exists(tdlRoot))
    {
        Directory.CreateDirectory(tdlRoot);
        logger.ZLogInformation($"创建数据根目录: {tdlRoot}");
    }
    return tdlRoot;
}

public class ChannelExport
{
    public long ChatId { get; set; }
    public string ChatTitle { get; set; }
    public DateTime ExportTime { get; set; }
    public int TotalMessages { get; set; }
    public List<MessageGroup> Groups { get; set; } = new();
}

public class MessageGroup
{
    public string? MediaAlbumId { get; set; }
    public bool IsGrouped { get; set; }
    public List<MessageInfo> Messages { get; set; } = new();
}

public class MessageInfo
{
    public long MessageId { get; set; }
    public DateTime Date { get; set; }
    public DateTime? EditDate { get; set; }
    public string Type { get; set; }
    public string? Text { get; set; }
    public MediaInfo? Media { get; set; }
    public ForwardInfoExport? ForwardInfo { get; set; }
    public List<MessageInfo>? Comments { get; set; }
}

public class MediaInfo
{
    public string Type { get; set; }
    public string? FileId { get; set; }
    public string? FileName { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? Duration { get; set; }
    public string? MimeType { get; set; }
    public long? FileSize { get; set; }
}

public class ForwardInfoExport
{
    public long FromChatId { get; set; }
    public long FromMessageId { get; set; }
    public DateTime? Date { get; set; }
    public string? Origin { get; set; }
}
