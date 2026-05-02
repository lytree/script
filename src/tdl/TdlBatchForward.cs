#!/usr/bin/env dotnet

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
using System.Text.RegularExpressions;
using Framework.ZLogging;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using TdLib;
using TdLib.Bindings;
using ZLogger;

ManualResetEventSlim ReadyToAuthenticate = new();
string tdlRoot = string.Empty;
TdlUpdateHandler _updateHandler;

using (var client = new TdClient())
{
    client.Bindings.SetLogVerbosityLevel(TdLogLevel.Fatal);
    await Main(client, args);
}

async Task Main(TdClient client, string[] args)
{
    var logger = InitializeLogger();

    var optionSource = new Option<string>("--source") { Required = true, Description = "源频道/群聊消息链接" };
    var optionTarget = new Option<string>("--target") { Required = true, Description = "目标频道/群聊链接或用户名" };
    var optionOlder = new Option<bool>("--older") { DefaultValueFactory = _ => true, Description = "方向: true=向旧消息转发, false=向新消息转发" };
    var optionLimit = new Option<int>("--limit") { DefaultValueFactory = _ => 0, Description = "最大转发数量, 0=全部" };

    var rootCommand = new RootCommand("批量深度转发消息");
    rootCommand.Options.Add(optionSource);
    rootCommand.Options.Add(optionTarget);
    rootCommand.Options.Add(optionOlder);
    rootCommand.Options.Add(optionLimit);

    var parseResult = rootCommand.Parse(args);
    var sourceLink = parseResult.GetValue(optionSource);
    var targetLink = parseResult.GetValue(optionTarget);
    var directionOlder = parseResult.GetValue(optionOlder);
    var limit = parseResult.GetValue(optionLimit);

    InitializeEnvironment(logger);

    _updateHandler = new TdlUpdateHandler(ReadyToAuthenticate, logger)
        .OnConfigureTdlibParameters(ConfigureTdlibParameters)
        .OnFileUpdate(HandleFileUpdate);

    client.UpdateReceived += async (_, update) => { await _updateHandler.ProcessUpdates(client, update, tdlRoot); };
    ReadyToAuthenticate.Wait();

    if (_updateHandler.AuthNeeded)
    {
        await HandleAuthentication(client, logger);
    }

    var currentUser = await GetCurrentUser(client);
    var fullUserName = $"{currentUser.FirstName} {currentUser.LastName}".Trim();
    logger.ZLogInformation($"成功登录为 [[{currentUser.Id}]] / [[@{currentUser.Usernames?.ActiveUsernames[0]}]] / [[{fullUserName}]]");

    var (sourceChatId, startMessageId) = await ResolveSourceLink(client, sourceLink, logger);
    if (sourceChatId == 0)
    {
        logger.ZLogError($"无法解析源链接: {sourceLink}");
        return;
    }

    var targetChatId = await ResolveTargetLink(client, targetLink, logger);
    if (targetChatId == 0)
    {
        logger.ZLogError($"无法解析目标链接: {targetLink}");
        return;
    }

    var sourceChat = await client.GetChatAsync(sourceChatId);
    var targetChat = await client.GetChatAsync(targetChatId);
    logger.ZLogInformation($"源: [{sourceChat.Title}] ChatId={sourceChatId}, StartMsgId={startMessageId}");
    logger.ZLogInformation($"目标: [{targetChat.Title}] ChatId={targetChatId}");
    logger.ZLogInformation($"方向: {(directionOlder ? "向旧消息" : "向新消息")}, 限制: {(limit > 0 ? limit.ToString() : "无限制")}");

    int totalForwarded;
    if (directionOlder)
    {
        totalForwarded = await ForwardOlderDirection(client, sourceChatId, startMessageId, targetChatId, limit, logger);
    }
    else
    {
        totalForwarded = await ForwardNewerDirection(client, sourceChatId, startMessageId, targetChatId, limit, logger);
    }

    logger.ZLogInformation($"全部转发完成，共转发 {totalForwarded} 条消息");

    Console.WriteLine("按 ENTER 键退出");
    Console.ReadLine();
}

ILogger InitializeLogger()
{
    var factory = LoggerFactory.Create(logging =>
    {
        logging.SetMinimumLevel(LogLevel.Information);
        logging.AddZLoggerSpectreConsoleAndFile("tdl-batch-forward.log");
    });
    return factory.CreateLogger("tdl-batch-forward");
}

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

async Task HandleAuthentication(TdClient client, ILogger logger)
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

    cbLogger.ZLogInformation($"正在尝试连接代理...");
    var proxy = await client.AddProxyAsync(new TdApi.Proxy() { Server = "127.0.0.1", Port = 7897, Type = new TdApi.ProxyType.ProxyTypeSocks5() }, true);
    await client.EnableProxyAsync(proxy.Id);
    cbLogger.ZLogInformation($"代理已启用。");
}

async Task HandleFileUpdate(TdApi.File file, string outputPath, ILogger cbLogger)
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
}

async Task<TdApi.User> GetCurrentUser(TdClient client)
{
    return await client.ExecuteAsync(new TdApi.GetMe());
}

async Task<(long chatId, long messageId)> ResolveSourceLink(TdClient client, string link, ILogger logger)
{
    try
    {
        var linkInfo = await client.GetMessageLinkInfoAsync(link);
        if (linkInfo.Message != null)
        {
            return (linkInfo.Message.ChatId, linkInfo.Message.Id);
        }
        logger.ZLogWarning($"源链接未关联到消息: {link}");
    }
    catch (TdException ex)
    {
        logger.ZLogError(ex, $"无法解析源链接: {link}");
    }
    return (0, 0);
}

async Task<long> ResolveTargetLink(TdClient client, string link, ILogger logger)
{
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
            logger.ZLogWarning($"邀请链接有效但未关联到已有聊天，可能需要先加入: {link}");
            return 0;
        }
    }
    catch (TdException ex)
    {
        logger.ZLogError(ex, $"无法解析邀请链接: {link}");
        return 0;
    }

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
        var foundChatId = await SearchChatByTitle(client, link, logger);
        if (foundChatId != 0)
        {
            return foundChatId;
        }
    }
    catch (TdException) { }

    logger.ZLogWarning($"目标链接未关联到聊天: {link}");
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

async Task<long> SearchChatByTitle(TdClient client, string keyword, ILogger logger)
{
    logger.ZLogInformation($"在聊天列表中搜索: {keyword}");
    var chatIds = await client.GetChatsAsync(limit: 200);
    if (chatIds?.ChatIds == null) return 0;

    foreach (var id in chatIds.ChatIds)
    {
        try
        {
            var chat = await client.GetChatAsync(id);
            if (chat.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                logger.ZLogInformation($"找到匹配聊天: [{chat.Title}] ChatId={chat.Id}");
                return chat.Id;
            }
        }
        catch { }
    }

    return 0;
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

async Task<int> ForwardOlderDirection(TdClient client, long sourceChatId, long startMessageId, long targetChatId, int limit, ILogger logger)
{
    int totalForwarded = 0;
    long fromMessageId = startMessageId;
    List<TdApi.Message>? pendingGroup = null;
    bool hasMore = true;

    logger.ZLogInformation($"开始向旧消息方向转发...");

    while (hasMore)
    {
        try
        {
            var history = await client.GetChatHistoryAsync(sourceChatId, fromMessageId, 0, 100, false);
            if (history.Messages_ == null || history.Messages_.Length == 0)
            {
                hasMore = false;
                break;
            }

            var messages = history.Messages_
                .Where(m => m.Id <= startMessageId)
                .OrderBy(m => m.Id)
                .ToList();

            if (messages.Count == 0)
            {
                fromMessageId = history.Messages_.Last().Id;
                continue;
            }

            if (pendingGroup != null && pendingGroup.Count > 0)
            {
                messages = [.. pendingGroup, .. messages];
                pendingGroup = null;
            }

            var (toProcess, pending) = ExtractPendingMediaGroup(messages);
            if (pending != null && pending.Count > 0)
            {
                pendingGroup = pending;
            }

            totalForwarded += await ForwardGroupedMessages(client, toProcess, sourceChatId, targetChatId, logger);

            if (limit > 0 && totalForwarded >= limit)
            {
                logger.ZLogInformation($"已达到转发限制 {limit}");
                break;
            }

            fromMessageId = history.Messages_.Last().Id;
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
            logger.ZLogError(ex, $"转发过程中发生异常");
            await Task.Delay(5000);
        }
    }

    if (pendingGroup != null && pendingGroup.Count > 0)
    {
        totalForwarded += await ForwardGroupedMessages(client, pendingGroup, sourceChatId, targetChatId, logger);
    }

    return totalForwarded;
}

async Task<int> ForwardNewerDirection(TdClient client, long sourceChatId, long startMessageId, long targetChatId, int limit, ILogger logger)
{
    var newerMessages = new List<TdApi.Message>();
    long fromMessageId = 0;
    bool foundStart = false;

    logger.ZLogInformation($"开始向新消息方向转发（从最新消息往回搜索）...");

    while (!foundStart)
    {
        try
        {
            var history = await client.GetChatHistoryAsync(sourceChatId, fromMessageId, 0, 100, false);
            if (history.Messages_ == null || history.Messages_.Length == 0)
            {
                break;
            }

            foreach (var msg in history.Messages_)
            {
                if (msg.Id >= startMessageId)
                {
                    newerMessages.Add(msg);
                    if (limit > 0 && newerMessages.Count >= limit)
                    {
                        foundStart = true;
                        break;
                    }
                }
                else
                {
                    foundStart = true;
                    break;
                }
            }

            fromMessageId = history.Messages_.Last().Id;
            await Task.Delay(500);
        }
        catch (Exception ex)
        {
            logger.ZLogError(ex, $"搜索新消息时发生异常");
            break;
        }
    }

    newerMessages = newerMessages.OrderBy(m => m.Id).ToList();
    logger.ZLogInformation($"找到 {newerMessages.Count} 条消息，开始转发...");

    return await ForwardGroupedMessages(client, newerMessages, sourceChatId, targetChatId, logger);
}

(List<TdApi.Message> toProcess, List<TdApi.Message>? pending) ExtractPendingMediaGroup(List<TdApi.Message> messages)
{
    if (messages.Count == 0) return (messages, null);

    var lastMsg = messages[^1];
    if (lastMsg.MediaAlbumId == 0) return (messages, null);

    var pending = new List<TdApi.Message>();
    for (int i = messages.Count - 1; i >= 0; i--)
    {
        if (messages[i].MediaAlbumId == lastMsg.MediaAlbumId)
        {
            pending.Insert(0, messages[i]);
        }
        else
        {
            break;
        }
    }

    var toProcess = messages.Take(messages.Count - pending.Count).ToList();
    return (toProcess, pending);
}

async Task<int> ForwardGroupedMessages(TdClient client, List<TdApi.Message> messages, long sourceChatId, long targetChatId, ILogger logger)
{
    if (messages.Count == 0) return 0;

    int totalForwarded = 0;
    var groups = GroupMessagesByAlbum(messages);

    foreach (var group in groups)
    {
        try
        {
            var ids = group.Select(m => m.Id).OrderBy(id => id).ToArray();

            var result = await client.ForwardMessagesAsync(
                chatId: targetChatId,
                fromChatId: sourceChatId,
                messageIds: ids,
                sendCopy: true,
                removeCaption: false
            );

            totalForwarded += ids.Length;
            var albumLabel = group.First().MediaAlbumId != 0 ? $"分组:{group.First().MediaAlbumId}" : "独立消息";
            logger.ZLogInformation($"已转发 {totalForwarded} 条消息 ({albumLabel}, 数量: {ids.Length})");

            await Task.Delay(1000);
        }
        catch (TdException ex) when (ex.Error.Code == 429)
        {
            int retryAfter = 10;
            logger.ZLogWarning($"触发频率限制，等待 {retryAfter} 秒后继续...");
            await Task.Delay(retryAfter * 1000);
        }
        catch (Exception ex)
        {
            logger.ZLogError(ex, $"转发消息组时出错");
            await Task.Delay(3000);
        }
    }

    return totalForwarded;
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
            currentGroup = [messages[i]];
            currentAlbumId = messages[i].MediaAlbumId;
        }
    }

    result.Add(currentGroup);
    return result;
}
