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
    var logger = TdlEnv.CreateLogger("tdl-chat-users.log", "tdl-chat-users");

    var optionChat = new Option<string?>("--chat") { Required = false, Description = "聊天链接或用户名 (默认: 收藏夹)" };
    var optionOutput = new Option<string?>("--output") { Required = false, Description = "输出文件路径 (默认: tdl-users.json)" };
    var optionRaw = new Option<bool>("--raw") { DefaultValueFactory = _ => false, Description = "导出原始 MTProto 数据" };
    var optionLimit = new Option<int>("--limit") { DefaultValueFactory = _ => 0, Description = "最大导出数量, 0=全部" };

    var rootCommand = new RootCommand("导出聊天成员/订阅者");
    rootCommand.Options.Add(optionChat);
    rootCommand.Options.Add(optionOutput);
    rootCommand.Options.Add(optionRaw);
    rootCommand.Options.Add(optionLimit);

    var parseResult = rootCommand.Parse(args);
    var chatLink = parseResult.GetValue(optionChat);
    var outputPath = parseResult.GetValue(optionOutput);
    var raw = parseResult.GetValue(optionRaw);
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

    long chatId = await env.ResolveChatIdAsync(chatLink);
    if (chatId == 0)
    {
        chatId = currentUser.Id;
        logger.ZLogInformation($"未指定聊天，默认使用收藏夹 (ChatId={chatId})");
    }

    var chat = await client.GetChatAsync(chatId);
    logger.ZLogInformation($"目标: [{chat.Title}] ChatId={chatId}");

    if (string.IsNullOrWhiteSpace(outputPath))
    {
        outputPath = "tdl-users.json";
    }

    var members = await ExportChatMembersAsync(client, chatId, limit, raw, logger);

    var jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    var json = JsonSerializer.Serialize(members, jsonOptions);

    string? dir = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
    {
        Directory.CreateDirectory(dir);
    }

    await File.WriteAllTextAsync(outputPath, json);
    logger.ZLogInformation($"导出完成，共 {members.Count} 个成员");
    logger.ZLogInformation($"文件已保存到: {outputPath}");

    PrintMembersTable(members);

    Console.WriteLine("按 ENTER 键退出");
    Console.ReadLine();
}

async Task<List<MemberInfo>> ExportChatMembersAsync(TdClient client, long chatId, int limit, bool raw, ILogger logger)
{
    var result = new List<MemberInfo>();
    long offset = 0;
    int batchSize = 200;
    bool hasMore = true;

    logger.ZLogInformation($"开始导出聊天成员...");

    while (hasMore)
    {
        try
        {
            TdApi.ChatMembers members;
            try
            {
                members = await client.GetChatAdministratorsAsync(chatId);
                if (offset == 0 && members.TotalCount > 0)
                {
                    logger.ZLogInformation($"管理员数量: {members.TotalCount}");
                }
            }
            catch
            {
                members = new TdApi.ChatMembers { TotalCount = 0, Members = Array.Empty<TdApi.ChatMember>() };
            }

            if (offset == 0)
            {
                foreach (var member in members.Members ?? Array.Empty<TdApi.ChatMember>())
                {
                    var info = await BuildMemberInfo(client, member, raw, logger);
                    if (info != null) result.Add(info);
                }
            }

            var chatMemberIds = await client.SearchChatMembersAsync(
                chatId: chatId,
                query: "",
                limit: batchSize,
                filter: null
            );

            if (chatMemberIds?.MemberIds == null || chatMemberIds.MemberIds.Length == 0)
            {
                hasMore = false;
                break;
            }

            foreach (var memberId in chatMemberIds.MemberIds)
            {
                if (result.Any(r => r.UserId == memberId))
                    continue;

                try
                {
                    var user = await client.GetUserAsync(memberId);
                    var info = new MemberInfo
                    {
                        UserId = user.Id,
                        FirstName = user.FirstName,
                        LastName = user.LastName,
                        Username = user.Usernames?.ActiveUsernames?.FirstOrDefault(),
                        PhoneNumber = user.PhoneNumber,
                        IsBot = user.IsBot,
                        Status = GetUserStatus(user.Status),
                        MemberType = "Member"
                    };

                    if (raw)
                    {
                        info.RawData = JsonSerializer.Serialize(user, new JsonSerializerOptions
                        {
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                        });
                    }

                    result.Add(info);
                }
                catch (TdException ex)
                {
                    logger.ZLogWarning($"获取用户 {memberId} 失败: {ex.Error.Message}");
                }

                if (limit > 0 && result.Count >= limit)
                {
                    hasMore = false;
                    break;
                }
            }

            offset += chatMemberIds.MemberIds.Length;

            if (chatMemberIds.MemberIds.Length < batchSize)
            {
                hasMore = false;
            }

            logger.ZLogInformation($"已导出 {result.Count} 个成员...");
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
            logger.ZLogError(ex, $"导出成员时发生异常");
            hasMore = false;
        }
    }

    return result;
}

async Task<MemberInfo?> BuildMemberInfo(TdClient client, TdApi.ChatMember member, bool raw, ILogger logger)
{
    try
    {
        var user = await client.GetUserAsync(member.MemberId);
        var info = new MemberInfo
        {
            UserId = user.Id,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Username = user.Usernames?.ActiveUsernames?.FirstOrDefault(),
            PhoneNumber = user.PhoneNumber,
            IsBot = user.IsBot,
            Status = GetUserStatus(user.Status),
            MemberType = member.Status switch
            {
                TdApi.ChatMemberStatus.ChatMemberStatusCreator => "Creator",
                TdApi.ChatMemberStatus.ChatMemberStatusAdministrator => "Administrator",
                TdApi.ChatMemberStatus.ChatMemberStatusMember => "Member",
                TdApi.ChatMemberStatus.ChatMemberStatusRestricted => "Restricted",
                TdApi.ChatMemberStatus.ChatMemberStatusLeft => "Left",
                TdApi.ChatMemberStatus.ChatMemberStatusBanned => "Banned",
                _ => "Unknown"
            }
        };

        if (raw)
        {
            info.RawData = JsonSerializer.Serialize(user, new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }

        return info;
    }
    catch (Exception ex)
    {
        logger.ZLogWarning(ex, $"构建成员信息失败: UserId={member.MemberId}");
        return null;
    }
}

string GetUserStatus(TdApi.UserStatus status)
{
    return status switch
    {
        TdApi.UserStatus.UserStatusEmpty => "Empty",
        TdApi.UserStatus.UserStatusOnline => "Online",
        TdApi.UserStatus.UserStatusOffline => "Offline",
        TdApi.UserStatus.UserStatusRecently => "Recently",
        TdApi.UserStatus.UserStatusLastWeek => "LastWeek",
        TdApi.UserStatus.UserStatusLastMonth => "LastMonth",
        _ => "Unknown"
    };
}

void PrintMembersTable(List<MemberInfo> members)
{
    if (members.Count == 0) return;

    var table = new Table();
    table.Title = new TableTitle("[bold]聊天成员列表[/]");
    table.AddColumn("ID");
    table.AddColumn("名称");
    table.AddColumn("用户名");
    table.AddColumn("类型");
    table.AddColumn("状态");
    table.AddColumn("Bot");

    foreach (var m in members.Take(100))
    {
        var fullName = $"{m.FirstName} {m.LastName}".Trim();
        var typeColor = m.MemberType switch
        {
            "Creator" => "[yellow]",
            "Administrator" => "[blue]",
            "Member" => "[green]",
            "Restricted" => "[red]",
            "Banned" => "[red]",
            _ => "[grey]"
        };

        table.AddRow(
            m.UserId.ToString(),
            fullName.EscapeMarkup(),
            m.Username?.EscapeMarkup() ?? "-",
            $"{typeColor}{m.MemberType}[/]",
            m.Status,
            m.IsBot ? "[red]是[/]" : "否"
        );
    }

    if (members.Count > 100)
    {
        table.AddRow("...", $"...共{members.Count}人", "...", "...", "...", "...");
    }

    AnsiConsole.Write(table);
}

public class MemberInfo
{
    public long UserId { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? Username { get; set; }
    public string? PhoneNumber { get; set; }
    public bool IsBot { get; set; }
    public string Status { get; set; } = "";
    public string MemberType { get; set; } = "";
    public string? RawData { get; set; }
}
