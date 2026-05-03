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

    var optionSource = new Option<string?>("--source") { Required = false, Description = "源频道/群聊链接或用户名 (默认: 收藏夹)" };
    var optionTarget = new Option<string?>("--target") { Required = false, Description = "目标频道/群聊链接或用户名 (默认: 收藏夹)" };
    var optionLimit = new Option<int>("--limit") { DefaultValueFactory = _ => 0, Description = "最大处理数量, 0=全部" };

    var rootCommand = new RootCommand("将浅转发消息转换为深度Copy");
    rootCommand.Options.Add(optionSource);
    rootCommand.Options.Add(optionTarget);
    rootCommand.Options.Add(optionLimit);

    var parseResult = rootCommand.Parse(args);
    var sourceLink = parseResult.GetValue(optionSource);
    var targetLink = parseResult.GetValue(optionTarget);
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

    long myId = currentUser.Id;

    long sourceChatId = await ResolveChatIdAsync(client, sourceLink, logger);
    if (sourceChatId == 0)
    {
        sourceChatId = myId;
        logger.ZLogInformation($"未指定源频道，默认使用收藏夹 (ChatId={myId})");
    }

    long targetChatId = await ResolveChatIdAsync(client, targetLink, logger);
    if (targetChatId == 0)
    {
        targetChatId = myId;
        logger.ZLogInformation($"未指定目标频道，默认使用收藏夹 (ChatId={myId})");
    }

    var sourceChat = await client.GetChatAsync(sourceChatId);
    var targetChat = await client.GetChatAsync(targetChatId);
    logger.ZLogInformation($"源: [{sourceChat.Title}] ChatId={sourceChatId}");
    logger.ZLogInformation($"目标: [{targetChat.Title}] ChatId={targetChatId}");

    int totalProcessed = await ProcessDeepCopy(client, sourceChatId, targetChatId, limit, logger);

    logger.ZLogInformation($"全部完成，共深度Copy {totalProcessed} 条消息");

    Console.WriteLine("按 ENTER 键退出");
    Console.ReadLine();
}

async Task<int> ProcessDeepCopy(TdClient client, long sourceChatId, long targetChatId, int limit, ILogger logger)
{
    int totalProcessed = 0;
    long fromMessageId = 0;
    bool hasMore = true;

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

            var messages = history.Messages_.ToList();

            foreach (var msg in messages)
            {
                if (msg.ForwardInfo != null)
                {
                    try
                    {
                        var result = await client.ForwardMessagesAsync(
                            chatId: targetChatId,
                            fromChatId: sourceChatId,
                            messageIds: [msg.Id],
                            sendCopy: true,
                            removeCaption: false
                        );

                        totalProcessed++;
                        logger.ZLogInformation($"深度Copy: MsgId={msg.Id} -> 新MsgId={result.Messages_?[0].Id}, 累计={totalProcessed}");

                        await client.DeleteMessagesAsync(sourceChatId, [msg.Id], revoke: true);
                        logger.ZLogTrace($"已删除原浅转发消息: MsgId={msg.Id}");

                        await Task.Delay(500);
                    }
                    catch (TdException ex)
                    {
                        logger.ZLogWarning($"深度Copy失败: MsgId={msg.Id}, 错误: {ex.Error.Message}");
                    }
                }

                if (limit > 0 && totalProcessed >= limit)
                {
                    logger.ZLogInformation($"已达到处理限制 {limit}");
                    return totalProcessed;
                }
            }

            fromMessageId = history.Messages_.Last().Id;
            await Task.Delay(1000);
        }
        catch (TdException ex) when (ex.Error.Code == 429)
        {
            int retryAfter = ParseRetryAfter(ex);
            logger.ZLogWarning($"触发频率限制，等待 {retryAfter} 秒后继续...");
            await Task.Delay(retryAfter * 1000);
        }
        catch (Exception ex)
        {
            logger.ZLogError(ex, $"处理过程中发生异常");
            await Task.Delay(5000);
        }
    }

    return totalProcessed;
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
        logging.AddZLoggerSpectreConsoleAndFile("tdl-forward.log");
    });
    return factory.CreateLogger("tdl-forward");
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
