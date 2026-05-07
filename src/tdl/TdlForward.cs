#!/usr/bin/env dotnet
#:include ../../env.cs
#:include TdlUpdateHandler.cs
#:include TdlForwardDbContext.cs
#:include TdlForwardService.cs

#:package TDLib@*
#:package tdlib.native@*
#:package tdlib.native.win-x64@*
#:package System.CommandLine@*
#:package Spectre.Console@*
#:package Spectre.Console.Ansi@*
#:package Microsoft.Extensions.Logging@*
#:package ZLogger@*
#:package YLFramework.ZLogging@1.0.3-alpha.7
#:package Microsoft.EntityFrameworkCore.Sqlite@*
#:package Microsoft.EntityFrameworkCore.Design@*
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
    var optionComments = new Option<bool>("--comments") { DefaultValueFactory = _ => false, Description = "是否转发评论" };

    var rootCommand = new RootCommand("将浅转发消息转换为深度Copy");
    rootCommand.Options.Add(optionSource);
    rootCommand.Options.Add(optionTarget);
    rootCommand.Options.Add(optionLimit);
    rootCommand.Options.Add(optionComments);
    var parseResult = rootCommand.Parse(args);
    var sourceLink = parseResult.GetValue(optionSource);
    var targetLink = parseResult.GetValue(optionTarget);
    var limit = parseResult.GetValue(optionLimit);
    var forwardComments = parseResult.GetValue(optionComments);
    string tdlRoot = InitializeEnvironment(logger);

    var service = new TdlForwardService(client, logger, tdlRoot);
    await service.WaitReadyAsync();

    if (service.AuthNeeded)
    {
        await service.AuthenticateAsync();
    }

    var currentUser = await service.GetCurrentUserAsync();
    var fullUserName = $"{currentUser.FirstName} {currentUser.LastName}".Trim();
    logger.ZLogInformation($"成功登录为 [[{currentUser.Id}]] / [[@{currentUser.Usernames?.ActiveUsernames[0]}]] / [[{fullUserName}]]");

    long myId = currentUser.Id;



    long sourceChatId = await ResolveChatIdAsync(client, sourceLink, logger);
    if (sourceChatId == 0)
    {
        sourceChatId = myId;
        logger.ZLogInformation($"未指定源频道，默认使用收藏夹 (ChatId={myId})");
    }


    var sourceChat = await client.GetChatAsync(sourceChatId);
    // var targetChat = await client.GetChatAsync(targetChatId);
    logger.ZLogInformation($"源: [[{sourceChat.Title}]] ChatId={sourceChatId}");
    // logger.ZLogInformation($"目标: [{targetChat.Title}] ChatId={targetChatId}");


    using var db = new ForwardDbContext(sourceChatId, Path.Combine(Path.EntryPointFileDirectoryPath(), "data"));
    await db.Database.EnsureCreatedAsync();
    logger.ZLogInformation($"数据库已就绪: forward-{sourceChatId}.db");

    int totalForwarded = await service.DeepCopyForward(db, sourceChatId, 0, sourceChatId, limit, forwardComments);



    logger.ZLogInformation($"全部完成，共深度Copy {totalForwarded} 条消息");

    Console.WriteLine("按 ENTER 键退出");
    Console.ReadLine();
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
