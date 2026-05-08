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

    var optionSource = new Option<string>("--source") { Required = true, Description = "源频道/群聊消息链接" ,DefaultValueFactory = _ => "https://t.me/sourpuss1988/177" };
    var optionSourceId = new Option<long?>("--sourceId") { Required = false, Description = "源频道/群聊消息Id"   };
    var optionTarget = new Option<string>("--target") { Required = true, Description = "目标频道/群聊链接或用户名" ,DefaultValueFactory = _ => "https://t.me/lytree_tubao" };
    var optionOlder = new Option<bool>("--older") { DefaultValueFactory = _ => true, Description = "方向: true=向旧消息转发, false=向新消息转发" };
    var optionLimit = new Option<int>("--limit") { DefaultValueFactory = _ => 0, Description = "最大转发数量, 0=全部" };
    var optionComments = new Option<bool>("--comments") { DefaultValueFactory = _ => true, Description = "是否转发评论" };

    var rootCommand = new RootCommand("批量深度转发消息");
    rootCommand.Options.Add(optionSource);
    rootCommand.Options.Add(optionTarget);
    rootCommand.Options.Add(optionOlder);
    rootCommand.Options.Add(optionLimit);
    rootCommand.Options.Add(optionComments);

    var parseResult = rootCommand.Parse(args);
    var sourceLink = parseResult.GetValue(optionSource);
    var sourceLinkId = parseResult.GetValue(optionSourceId);
    var targetLink = parseResult.GetValue(optionTarget);
    var directionOlder = parseResult.GetValue(optionOlder);
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

    var (sourceChatId, startMessageId) = await service.ResolveSourceLinkAsync(sourceLink);
    if (sourceChatId == 0)
    {
        logger.ZLogError($"无法解析源链接: {sourceLink}");
        return;
    }
    if (sourceLinkId != null)
    {
        startMessageId = sourceLinkId.Value;
    }

    var targetChatId = await service.ResolveTargetLinkAsync(targetLink);
    if (targetChatId == 0)
    {
        logger.ZLogError($"无法解析目标链接: {targetLink}");
        return;
    }

    var sourceChat = await client.GetChatAsync(sourceChatId);
    var targetChat = await client.GetChatAsync(targetChatId);
    logger.ZLogInformation($"源: [{sourceChat.Title}] ChatId={sourceChatId}, StartMsgId={startMessageId}");
    logger.ZLogInformation($"目标: [{targetChat.Title}] ChatId={targetChatId}");
    logger.ZLogInformation($"方向: {(directionOlder ? "向旧消息" : "向新消息")}, 限制: {(limit > 0 ? limit.ToString() : "无限制")}, 评论: {(forwardComments ? "是" : "否")}");

    using var db = new ForwardDbContext(sourceChatId, Path.Combine(Path.EntryPointFileDirectoryPath(),"data" ));
    await db.Database.EnsureCreatedAsync();
    logger.ZLogInformation($"数据库已就绪: forward-{sourceChatId}.db");

    int totalForwarded;
    if (directionOlder)
    {
        totalForwarded = await service.ForwardOlderDirection(db, sourceChatId, startMessageId, targetChatId, limit, forwardComments);
    }
    else
    {
        totalForwarded = await service.ForwardNewerDirection(db, sourceChatId, startMessageId, targetChatId, limit, forwardComments);
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
