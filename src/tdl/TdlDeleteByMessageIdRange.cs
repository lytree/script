#!/usr/bin/env dotnet run

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
    var logger = TdlEnv.CreateLogger("tdl-delete-range.log", "tdl_delete_range");

    var optFromUrl = new Option<string>("--from") { Required = true, Description = "起始消息链接 (https://t.me/c/<chat_id>/<msg_id>)" };
    var optToUrl = new Option<string>("--to") { Required = true, Description = "结束消息链接 (https://t.me/c/<chat_id>/<msg_id>)" };
    var optSilent = new Option<bool>("--silent") { DefaultValueFactory = _ => false, Description = "静默删除，不询问确认" };
    var optBatch = new Option<int>("--batch") { DefaultValueFactory = _ => 100, Description = "每批删除的消息数" };
    var optDryRun = new Option<bool>("--dry-run") { DefaultValueFactory = _ => false, Description = "只扫描, 不实际删除" };

    var root = new RootCommand("按消息 ID 区间删除 Telegram 消息 (基于 t.me/c/<chat>/<id> 链接, 自动解析为全局 message id)");
    root.Options.Add(optFromUrl);
    root.Options.Add(optToUrl);
    root.Options.Add(optSilent);
    root.Options.Add(optBatch);
    root.Options.Add(optDryRun);

    if (args.Contains("--help") || args.Contains("-h"))
    {
        AnsiConsole.WriteLine("用法: dotnet run src/tdl/TdlDeleteByMessageIdRange.cs -- --from <URL> --to <URL> [options]");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("选项:");
        foreach (var o in root.Options)
        {
            var name = $"--{o.Name}";
            AnsiConsole.MarkupLine($"  [cyan]{name,-30}[/] {o.Description}");
        }
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]示例:[/]");
        AnsiConsole.MarkupLine("  [grey]--from https://t.me/c/4416946055/2342 --to https://t.me/c/4416946055/3705 --silent[/]");
        AnsiConsole.MarkupLine("  [grey]--from <URL> --to <URL> --dry-run[/]");
        return;
    }

    var pr = root.Parse(args);
    var fromUrl = pr.GetValue(optFromUrl)!;
    var toUrl = pr.GetValue(optToUrl)!;
    var silent = pr.GetValue(optSilent);
    var batchSize = pr.GetValue(optBatch);
    var dryRun = pr.GetValue(optDryRun);

    var env = new TdlEnv(client, logger);
    env.WaitReady();
    if (env.AuthNeeded) await env.AuthenticateAsync();

    var me = await env.GetCurrentUserAsync();
    var myUserName = me.Usernames?.ActiveUsernames?.FirstOrDefault() ?? "";
    AnsiConsole.MarkupLine($"[green]已登录[/] {me.Id} @{myUserName.EscapeMarkup()}");

    // 把两个链接解析成真实全局 message id
    var liFrom = await client.GetMessageLinkInfoAsync(fromUrl);
    var liTo = await client.GetMessageLinkInfoAsync(toUrl);
    if (liFrom.Message == null || liTo.Message == null)
    {
        AnsiConsole.MarkupLine("[red]链接无法解析为消息[/]");
        Environment.Exit(2);
        return;
    }
    if (liFrom.ChatId != liTo.ChatId)
    {
        AnsiConsole.MarkupLine($"[red]两个链接属于不同的聊天: from={liFrom.ChatId}, to={liTo.ChatId}[/]");
        Environment.Exit(2);
        return;
    }

    long chatId = liFrom.ChatId;
    long fromId = Math.Min(liFrom.Message.Id, liTo.Message.Id);
    long toId = Math.Max(liFrom.Message.Id, liTo.Message.Id);

    string chatTitle;
    try
    {
        var chat = await client.GetChatAsync(chatId);
        chatTitle = chat.Title ?? $"chat_{chatId}";
    }
    catch
    {
        chatTitle = $"chat_{chatId}";
    }

    AnsiConsole.MarkupLine($"[green]目标:[/] {chatTitle.EscapeMarkup()} ({chatId})");
    AnsiConsole.MarkupLine($"[green]锚点区间:[/] [yellow]{fromId}[/] ~ [yellow]{toId}[/]  (按这两个全局 id 之间的所有真实消息进行筛选与删除)");

    // ─────────────────────────────────────────────────────
    //  步骤 1: 拉取该 chat 全量历史 (分页), 找出 [fromId, toId] 区间内的真实消息 id
    // ─────────────────────────────────────────────────────
    AnsiConsole.MarkupLine("[cyan]步骤 1: 拉取聊天历史, 找出区间内的真实消息...[/]");
    var matched = new SortedSet<long>();
    long cursor = 0; // 0 = 最新消息
    int page = 0;
    bool done = false;
    while (!done)
    {
        try
        {
            var hist = await client.GetChatHistoryAsync(chatId, cursor, 0, 200, false);
            page++;
            if (hist.Messages_ == null || hist.Messages_.Length == 0) break;

            long smallest = long.MaxValue;
            foreach (var m in hist.Messages_)
            {
                smallest = Math.Min(smallest, m.Id);
                if (m.Id < fromId)
                {
                    // 已经到达锚点之前, 可以停
                    done = true;
                    break;
                }
                if (m.Id <= toId) matched.Add(m.Id);
            }

            if (done) break;
            if (smallest == long.MaxValue) break;
            cursor = smallest;
            AnsiConsole.MarkupLine($"  [grey]page {page}[/]  scanned up to {cursor}  matched={matched.Count}");

            if (cursor <= fromId) break;

            await Task.Delay(300);
        }
        catch (TdException ex) when (ex.Error.Code == 429)
        {
            int retryAfter = TdlEnv.ParseRetryAfter(ex);
            AnsiConsole.MarkupLine($"[yellow]扫描触发频率限制, 等待 {retryAfter}s...[/]");
            await Task.Delay(retryAfter * 1000);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]扫描异常: {ex.Message.EscapeMarkup()}[/]");
            break;
        }
    }

    AnsiConsole.MarkupLine($"[green]扫描完成:[/] 命中 [yellow]{matched.Count}[/] 条真实消息 (锚点区间内的全部)");
    if (matched.Count == 0)
    {
        AnsiConsole.MarkupLine("[yellow]没有可删除的消息[/]");
        AnsiConsole.MarkupLine("按 ENTER 退出");
        Console.ReadLine();
        return;
    }

    if (dryRun)
    {
        AnsiConsole.MarkupLine("[yellow]--dry-run: 仅显示命中 id, 不删除[/]");
        var ids = matched.ToList();
        AnsiConsole.MarkupLine($"  first 10: {string.Join(", ", ids.Take(10))}");
        AnsiConsole.MarkupLine($"  last 10:  {string.Join(", ", ids.TakeLast(10))}");
        AnsiConsole.MarkupLine("按 ENTER 退出");
        Console.ReadLine();
        return;
    }

    if (!silent)
    {
        AnsiConsole.MarkupLine("[yellow]直接开始删除 (加 --silent 标志以静默模式运行; 此版本默认总是执行)[/]");
    }

    int totalDeleted = 0;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var idArr = matched.ToArray();

    for (int i = 0; i < idArr.Length; i += batchSize)
    {
        var batch = idArr.Skip(i).Take(batchSize).ToArray();
        try
        {
            await client.DeleteMessagesAsync(chatId, batch, revoke: true);
            totalDeleted += batch.Length;
            AnsiConsole.MarkupLine($"[green]已删除 {totalDeleted}/{idArr.Length}[/]");
            await Task.Delay(500);
        }
        catch (TdException ex) when (ex.Error.Code == 429)
        {
            int retryAfter = TdlEnv.ParseRetryAfter(ex);
            AnsiConsole.MarkupLine($"[yellow]触发频率限制, 等待 {retryAfter}s...[/]");
            await Task.Delay(retryAfter * 1000);
            i -= batchSize;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]批量删除失败 (跳过 {batch.Length} 条): {ex.Message.EscapeMarkup()}[/]");
        }
    }

    sw.Stop();
    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule("[bold green]完成[/]").LeftJustified());
    AnsiConsole.MarkupLine($"目标聊天:    [cyan]{chatTitle.EscapeMarkup()}[/] ({chatId})");
    AnsiConsole.MarkupLine($"锚点区间:    [yellow]{fromId}[/] ~ [yellow]{toId}[/]");
    AnsiConsole.MarkupLine($"扫描命中:    [cyan]{matched.Count}[/] 条");
    AnsiConsole.MarkupLine($"成功删除:    [green]{totalDeleted}[/]");
    AnsiConsole.MarkupLine($"总耗时:      [yellow]{sw.Elapsed:hh\\:mm\\:ss}[/]");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("按 ENTER 退出");
    Console.ReadLine();
}
