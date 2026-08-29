#!/usr/bin/env dotnet

#:include ../../env.cs
#:include TdlUpdateHandler.cs
#:include TdlEnv.cs

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
    var logger = TdlEnv.CreateLogger("tdl-clear.log", "tdl-clear");

    var optionChannel = new Option<string?>("--channel") { Required = false, Description = "频道/群聊链接或用户名 (默认: 收藏夹)" };
    var optionContains = new Option<string>("--contains") { DefaultValueFactory = _ => "This channel can't be displayed", Description = "匹配消息中包含的文本内容" };
    var optionSilent = new Option<bool>("--silent") { DefaultValueFactory = _ => false, Description = "静默删除，不询问确认" };
    var optionLimit = new Option<int>("--limit") { DefaultValueFactory = _ => 0, Description = "最大处理数量, 0=全部" };

    var rootCommand = new RootCommand("清理频道中包含指定内容的消息");
    rootCommand.Options.Add(optionChannel);
    rootCommand.Options.Add(optionContains);
    rootCommand.Options.Add(optionSilent);
    rootCommand.Options.Add(optionLimit);

    var parseResult = rootCommand.Parse(args);
    var channelLink = parseResult.GetValue(optionChannel);
    var containsText = parseResult.GetValue(optionContains);
    var silent = parseResult.GetValue(optionSilent);
    var limit = parseResult.GetValue(optionLimit);

    var env = new TdlEnv(client, logger, onFileUpdate: HandleFileUpdate);
    env.WaitReady();

    if (env.AuthNeeded)
    {
        await env.AuthenticateAsync();
    }

    var currentUser = await env.GetCurrentUserAsync();
    var fullUserName = $"{currentUser.FirstName} {currentUser.LastName}".Trim();
    logger.ZLogInformation($"成功登录为 [[{currentUser.Id}]] / [[@{currentUser.Usernames?.ActiveUsernames[0]}]] / [[{fullUserName}]]");

    long chatId = await env.ResolveChatIdAsync(channelLink);
    if (chatId == 0)
    {
        chatId = currentUser.Id;
        logger.ZLogInformation($"未指定频道，默认使用收藏夹 (ChatId={chatId})");
    }

    var chat = await client.GetChatAsync(chatId);
    logger.ZLogInformation($"目标: [{chat.Title}] ChatId={chatId}");
    logger.ZLogInformation($"匹配内容: \"{containsText}\"");
    logger.ZLogInformation($"删除模式: {(silent ? "静默删除" : "交互确认")}");

    int totalDeleted = await CleanMessages(client, chatId, containsText, silent, limit, logger);

    logger.ZLogInformation($"清理完成，共删除 {totalDeleted} 条消息");

    Console.WriteLine("按 ENTER 键退出");
    Console.ReadLine();
}

async Task<int> CleanMessages(TdClient client, long chatId, string containsText, bool silent, int limit, ILogger logger)
{
    int totalDeleted = 0;
    long fromMessageId = 0;
    bool hasMore = true;
    var matchedMessages = new List<(long MsgId, string Text)>();

    logger.ZLogInformation($"开始扫描消息...");

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

            foreach (var msg in history.Messages_)
            {
                string? text = ExtractMessageText(msg);
                if (text != null && text.Contains(containsText, StringComparison.OrdinalIgnoreCase))
                {
                    matchedMessages.Add((msg.Id, text.Length > 80 ? text[..80] + "..." : text));
                }

                if (limit > 0 && matchedMessages.Count >= limit)
                {
                    hasMore = false;
                    break;
                }
            }

            fromMessageId = history.Messages_.Last().Id;
            await Task.Delay(300);
        }
        catch (TdException ex) when (ex.Error.Code == 429)
        {
            int retryAfter = TdlEnv.ParseRetryAfter(ex);
            logger.ZLogWarning($"触发频率限制，等待 {retryAfter} 秒后继续...");
            await Task.Delay(retryAfter * 1000);
        }
        catch (Exception ex)
        {
            logger.ZLogError(ex, $"扫描消息时发生异常");
            await Task.Delay(5000);
        }
    }

    if (matchedMessages.Count == 0)
    {
        logger.ZLogInformation($"未找到包含 \"{containsText}\" 的消息");
        return 0;
    }

    logger.ZLogInformation($"共找到 {matchedMessages.Count} 条匹配消息");

    if (!silent)
    {
        var table = new Table();
        table.AddColumn("序号");
        table.AddColumn("消息ID");
        table.AddColumn("内容预览");
        for (int i = 0; i < Math.Min(matchedMessages.Count, 50); i++)
        {
            table.AddRow((i + 1).ToString(), matchedMessages[i].MsgId.ToString(), matchedMessages[i].Text.EscapeMarkup());
        }
        if (matchedMessages.Count > 50)
        {
            table.AddRow("...", $"...共{matchedMessages.Count}条", "...");
        }
        AnsiConsole.Write(table);

        if (!AnsiConsole.Confirm($"确认删除以上 {matchedMessages.Count} 条消息?"))
        {
            logger.ZLogInformation($"用户取消删除操作");
            return 0;
        }
    }

    int batchSize = 100;
    for (int i = 0; i < matchedMessages.Count; i += batchSize)
    {
        var batch = matchedMessages.Skip(i).Take(batchSize).Select(m => m.MsgId).ToArray();
        try
        {
            await client.DeleteMessagesAsync(chatId, batch, revoke: true);
            totalDeleted += batch.Length;
            logger.ZLogInformation($"已删除 {totalDeleted}/{matchedMessages.Count} 条消息");
            await Task.Delay(500);
        }
        catch (TdException ex) when (ex.Error.Code == 429)
        {
            int retryAfter = TdlEnv.ParseRetryAfter(ex);
            logger.ZLogWarning($"触发频率限制，等待 {retryAfter} 秒后继续...");
            await Task.Delay(retryAfter * 1000);
            i -= batchSize;
        }
        catch (Exception ex)
        {
            logger.ZLogError(ex, $"批量删除消息时发生异常");
        }
    }

    return totalDeleted;
}

string? ExtractMessageText(TdApi.Message msg)
{
    return msg.Content switch
    {
        TdApi.MessageContent.MessageText t => t.Text?.Text,
        TdApi.MessageContent.MessagePhoto p => p.Caption?.Text,
        TdApi.MessageContent.MessageVideo v => v.Caption?.Text,
        TdApi.MessageContent.MessageAudio a => a.Caption?.Text,
        TdApi.MessageContent.MessageDocument d => d.Caption?.Text,
        TdApi.MessageContent.MessageVoiceNote vn => vn.Caption?.Text,
        TdApi.MessageContent.MessageAnimation ani => ani.Caption?.Text,
        TdApi.MessageContent.MessagePinMessage pm => $"[PinMessage] MsgId={pm.MessageId}",
        TdApi.MessageContent.MessageUnsupported u => "This channel can't be displayed",
        _ => null
    };
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
