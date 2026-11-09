#!/usr/bin/env dotnet

#:include ../../env.cs
#:include TdlUpdateHandler.cs
#:include TdlEnv.cs

#:package TDLib@*
#:package tdlib.api@*
#:package tdlib.native@*
#:package tdlib.native.win-x64@*
#:package System.CommandLine@*
#:package Spectre.Console@*
#:package Spectre.Console.Ansi@*
#:package Microsoft.Extensions.Logging@*
#:package ZLogger@*
#:package YLFramework.ZLogging@1.0.3-alpha.7

// 临时工具: 为目标群开启 Forum 话题功能并确保指定话题存在, 打印 t.me/c 链接
// 用法: dotnet run src/tdl/TdlForumSetup.cs -- --target <邀请链接或用户名> --topic <话题名>

using System.CommandLine;
using Framework.ZLogging;
using Microsoft.Extensions.Logging;
using TdLib;
using TdLib.Bindings;
using ZLogger;

using (var client = new TdClient())
{
    client.Bindings.SetLogVerbosityLevel(TdLogLevel.Fatal);
    return await Main(client, args);
}

async Task<int> Main(TdClient client, string[] args)
{
    var logger = TdlEnv.CreateLogger("tdl-forum-setup.log", "tdl-forum-setup");
    var optTarget = new Option<string>("--target") { Required = true, Description = "目标群链接/用户名" };
    var optTopic = new Option<string>("--topic") { Required = true, Description = "话题名称" };
    var root = new RootCommand("开启 Forum 话题功能并确保话题存在");
    root.Options.Add(optTarget);
    root.Options.Add(optTopic);

    root.SetAction(async (pr, ct) =>
    {
        string target = pr.GetValue(optTarget)!;
        string topicName = pr.GetValue(optTopic)!;

        var env = new TdlEnv(client, logger);
        env.WaitReady();
        if (env.AuthNeeded) await env.AuthenticateAsync();

        long chatId = await env.ResolveChatIdAsync(target);
        if (chatId == 0)
        {
            Console.WriteLine($"无法解析目标: {target}");
            return 1;
        }

        var chat = await client.GetChatAsync(chatId);
        Console.WriteLine($"目标: {chat.Title} chatId={chatId}");

        if (chat.Type is not TdApi.ChatType.ChatTypeSupergroup sg)
        {
            Console.WriteLine("目标不是超级群, 无法开启话题");
            return 1;
        }

        var info = await client.GetSupergroupAsync(sg.SupergroupId);
        if (!info.IsForum)
        {
            Console.WriteLine("当前未开启话题, 正在开启...");
            await client.ToggleSupergroupIsForumAsync(sg.SupergroupId, true);
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(500);
                info = await client.GetSupergroupAsync(sg.SupergroupId);
                if (info.IsForum) break;
            }
            Console.WriteLine($"isForum={info.IsForum}");
        }
        else
        {
            Console.WriteLine("话题功能已开启");
        }

        if (!info.IsForum)
        {
            Console.WriteLine("开启失败");
            return 1;
        }

        var existing = await client.GetForumTopicsAsync(chatId, "", 0, 0, 0, 100);
        foreach (var t in existing.Topics)
        {
            if (string.Equals(t.Info.Name.Trim(), topicName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"TOPIC_EXISTS id={t.Info.ForumTopicId} name={t.Info.Name}");
                Console.WriteLine($"LINK=https://t.me/c/{sg.SupergroupId}/{t.Info.ForumTopicId}");
                return 0;
            }
        }

        var created = await client.CreateForumTopicAsync(
            chatId, topicName, false, new TdApi.ForumTopicIcon { Color = 0x6FB9F0, CustomEmojiId = 0 });
        Console.WriteLine($"TOPIC_CREATED id={created.ForumTopicId} name={created.Name}");
        Console.WriteLine($"LINK=https://t.me/c/{sg.SupergroupId}/{created.ForumTopicId}");
        return 0;
    });

    return await root.Parse(args).InvokeAsync();
}
