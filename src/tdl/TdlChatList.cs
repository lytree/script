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
using System.Text.Json;
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
    var logger = TdlEnv.CreateLogger("tdl-chat-ls.log", "tdl-chat-ls");

    var optionOutput = new Option<string>("--output") { DefaultValueFactory = _ => "table", Description = "输出格式: table 或 json" };
    var optionFilter = new Option<string?>("--filter") { Required = false, Description = "过滤条件 (如: type=channel, name=Telegram)" };
    var optionLimit = new Option<int>("--limit") { DefaultValueFactory = _ => 200, Description = "最大列出数量" };

    var rootCommand = new RootCommand("列出所有 Telegram 聊天");
    rootCommand.Options.Add(optionOutput);
    rootCommand.Options.Add(optionFilter);
    rootCommand.Options.Add(optionLimit);

    var parseResult = rootCommand.Parse(args);
    var outputFormat = parseResult.GetValue(optionOutput);
    var filter = parseResult.GetValue(optionFilter);
    var limit = parseResult.GetValue(optionLimit);

    var env = new TdlEnv(client, logger);
    env.WaitReady();

    if (env.AuthNeeded)
    {
        await env.AuthenticateAsync();
    }

    var currentUser = await env.GetCurrentUserAsync();
    var fullUserName = $"{currentUser.FirstName} {currentUser.LastName}".Trim();
    logger.ZLogInformation($"成功登录为 [[{currentUser.Id}]] / [[@{currentUser.Usernames?.ActiveUsernames[0]}]] / [[{fullUserName}]]");

    var chats = await ListChatsAsync(client, limit, logger);
    var filtered = ApplyFilter(chats, filter);

    logger.ZLogInformation($"共 {chats.Count} 个聊天，过滤后 {filtered.Count} 个");

    if (outputFormat.Equals("json", StringComparison.OrdinalIgnoreCase))
    {
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var json = JsonSerializer.Serialize(filtered, jsonOptions);
        Console.WriteLine(json);
    }
    else
    {
        PrintChatTable(filtered);
    }

    Console.WriteLine("按 ENTER 键退出");
    Console.ReadLine();
}

async Task<List<ChatInfo>> ListChatsAsync(TdClient client, int limit, ILogger logger)
{
    var result = new List<ChatInfo>();
    logger.ZLogInformation($"正在获取聊天列表...");

    var chatList = await client.GetChatsAsync(limit: limit);
    if (chatList?.ChatIds == null) return result;

    foreach (var chatId in chatList.ChatIds)
    {
        try
        {
            var chat = await client.GetChatAsync(chatId);
            var info = new ChatInfo
            {
                Id = chat.Id,
                Title = chat.Title,
                Type = GetChatType(chat),
                UnreadCount = chat.UnreadCount,
                LastMessageDate = chat.LastMessage?.Date ?? 0,
                IsVerified = chat.IsVerified,
                HasProtectedContent = chat.HasProtectedContent,
                MemberCount = 0
            };

            try
            {
                var info2 = await client.GetChatInfoAsync(chatId);
                if (info2 is TdLib.Bindings.TdApi.ChatInfo.ChatInfoPrivate priv)
                {
                    info.MemberCount = 0;
                }
                else if (info2 is TdLib.Bindings.TdApi.ChatInfo.ChatInfoBasicGroup bg)
                {
                    info.MemberCount = bg.BasicGroup?.MemberCount ?? 0;
                }
                else if (info2 is TdLib.Bindings.TdApi.ChatInfo.ChatInfoSupergroup sg)
                {
                    info.MemberCount = sg.Supergroup?.MemberCount ?? 0;
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(chat.Usernames?.ActiveUsernames?[0]))
            {
                info.Username = chat.Usernames.ActiveUsernames[0];
            }

            result.Add(info);
        }
        catch (TdException ex)
        {
            logger.ZLogWarning($"获取聊天 {chatId} 失败: {ex.Error.Message}");
        }
        await Task.Delay(50);
    }

    return result;
}

string GetChatType(TdApi.Chat chat)
{
    return chat.Type switch
    {
        TdApi.ChatType.ChatTypePrivate => "Private",
        TdApi.ChatType.ChatTypeBasicGroup => "BasicGroup",
        TdApi.ChatType.ChatTypeSupergroup sg => sg.IsChannel ? "Channel" : "Supergroup",
        TdApi.ChatType.ChatTypeSecret => "Secret",
        _ => "Unknown"
    };
}

List<ChatInfo> ApplyFilter(List<ChatInfo> chats, string? filter)
{
    if (string.IsNullOrWhiteSpace(filter)) return chats;

    var result = chats.AsEnumerable();

    var parts = filter.Split(';', StringSplitOptions.RemoveEmptyEntries);
    foreach (var part in parts)
    {
        var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
        if (kv.Length != 2) continue;

        var key = kv[0].ToLowerInvariant();
        var value = kv[1];

        result = key switch
        {
            "type" => result.Where(c => c.Type.Contains(value, StringComparison.OrdinalIgnoreCase)),
            "name" => result.Where(c => c.Title.Contains(value, StringComparison.OrdinalIgnoreCase)),
            "username" => result.Where(c => c.Username?.Contains(value, StringComparison.OrdinalIgnoreCase) == true),
            "verified" => result.Where(c => c.IsVerified),
            "protected" => result.Where(c => c.HasProtectedContent),
            _ => result
        };
    }

    return result.ToList();
}

void PrintChatTable(List<ChatInfo> chats)
{
    var table = new Table();
    table.Title = new TableTitle("[bold]Telegram 聊天列表[/]");
    table.AddColumn("ID");
    table.AddColumn("标题");
    table.AddColumn("类型");
    table.AddColumn("用户名");
    table.AddColumn("成员数");
    table.AddColumn("未读");

    foreach (var c in chats)
    {
        var typeColor = c.Type switch
        {
            "Channel" => "[blue]",
            "Supergroup" => "[green]",
            "BasicGroup" => "[yellow]",
            "Private" => "[white]",
            "Secret" => "[red]",
            _ => "[grey]"
        };

        table.AddRow(
            c.Id.ToString(),
            c.Title.EscapeMarkup(),
            $"{typeColor}{c.Type}[/]",
            c.Username?.EscapeMarkup() ?? "-",
            c.MemberCount > 0 ? c.MemberCount.ToString() : "-",
            c.UnreadCount > 0 ? $"[yellow]{c.UnreadCount}[/]" : "0"
        );
    }

    AnsiConsole.Write(table);
}

public class ChatInfo
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Username { get; set; }
    public int UnreadCount { get; set; }
    public int LastMessageDate { get; set; }
    public bool IsVerified { get; set; }
    public bool HasProtectedContent { get; set; }
    public int MemberCount { get; set; }
}
