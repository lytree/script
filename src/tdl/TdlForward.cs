#!/usr/bin/env dotnet
#:include ../../env.cs
#:include TdlUpdateHandler.cs
#:include TdlEnv.cs
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
    var logger = TdlEnv.CreateLogger("tdl-forward.log", "tdl-forward");

    var optionSource = new Option<string?>("--source") { Required = false, Description = "源频道/群聊链接或用户名 (默认: 收藏夹)" };
    var optionLimit = new Option<int>("--limit") { DefaultValueFactory = _ => 0, Description = "最大处理数量, 0=全部" };
    var optionComments = new Option<bool>("--comments") { DefaultValueFactory = _ => true, Description = "是否转发评论" };

    var rootCommand = new RootCommand("将浅转发消息转换为深度Copy");
    rootCommand.Options.Add(optionSource);
    rootCommand.Options.Add(optionLimit);
    rootCommand.Options.Add(optionComments);

    var parseResult = rootCommand.Parse(args);
    var sourceLink = parseResult.GetValue(optionSource);
    var limit = parseResult.GetValue(optionLimit);
    var forwardComments = parseResult.GetValue(optionComments);

    string tdlRoot = TdlEnv.InitTdlRoot(logger);
    var service = new TdlForwardService(client, logger, tdlRoot);
    await service.WaitReadyAsync();

    if (service.AuthNeeded)
    {
        await service.AuthenticateAsync();
    }

    var currentUser = await service.GetCurrentUserAsync();
    var fullUserName = $"{currentUser.FirstName} {currentUser.LastName}".Trim();
    logger.ZLogInformation($"成功登录为 [[{currentUser.Id}]] / [[@{currentUser.Usernames?.ActiveUsernames[0]}]] / [[{fullUserName}]]");

    long sourceChatId = string.IsNullOrWhiteSpace(sourceLink)
        ? 0
        : await service.ResolveTargetLinkAsync(sourceLink);
    if (sourceChatId == 0)
    {
        sourceChatId = currentUser.Id;
        logger.ZLogInformation($"未指定源频道，默认使用收藏夹 (ChatId={currentUser.Id})");
    }

    var sourceChat = await client.GetChatAsync(sourceChatId);
    logger.ZLogInformation($"源: [[{sourceChat.Title}]] ChatId={sourceChatId}");

    using var db = new ForwardDbContext(sourceChatId, Path.Combine(Path.EntryPointFileDirectoryPath(), "data"));
    await db.Database.EnsureCreatedAsync();
    logger.ZLogInformation($"数据库已就绪: forward-{sourceChatId}.db");

    int totalForwarded = await service.DeepCopyForward(db, sourceChatId, 0, sourceChatId, limit, forwardComments);

    logger.ZLogInformation($"全部完成，共深度Copy {totalForwarded} 条消息");

    Console.WriteLine("按 ENTER 键退出");
    Console.ReadLine();
}
