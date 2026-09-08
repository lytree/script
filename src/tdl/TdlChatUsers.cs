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

    // 新版 TDLib: Supergroup/Channel 才能用 GetSupergroupMembersAsync 拉成员
    if (chat.Type is not TdApi.ChatType.ChatTypeSupergroup sg)
    {
        logger.ZLogError($"聊天类型 {chat.Type.GetType().Name} 不支持导出成员,需要 Supergroup/Channel。");
        return;
    }

    var members = await ExportChatMembersAsync(client, sg.SupergroupId, limit, raw, logger);

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

async Task<List<MemberInfo>> ExportChatMembersAsync(TdClient client, long supergroupId, int limit, bool raw, ILogger logger)
{
    var result = new List<MemberInfo>();
    int batchSize = 200;

    logger.ZLogInformation($"开始导出聊天成员...");

    // 1) 管理员列表 — 新版 TDLib 改用 GetSupergroupMembersAsync + SupergroupMembersFilter.Administrators
    try
    {
        var admins = await client.GetSupergroupMembersAsync(
            supergroupId: supergroupId,
            filter: new TdApi.SupergroupMembersFilter.SupergroupMembersFilterAdministrators(),
            offset: 0,
            limit: batchSize);
        if (admins?.Members is { Length: > 0 })
        {
            logger.ZLogInformation($"管理员数量: {admins.TotalCount}");
            foreach (var member in admins.Members)
            {
                if (limit > 0 && result.Count >= limit) break;
                var info = await BuildMemberInfo(client, member, raw, logger);
                if (info != null) result.Add(info);
            }
        }
    }
    catch (TdException ex)
    {
        logger.ZLogWarning($"获取管理员失败: {ex.Error.Message}");
    }

    // 2) 普通成员 — SearchChatMembersAsync 返回 ChatMembers(成员数组,不是 ID 数组)
    int offset = 0;
    bool hasMore = true;
    while (hasMore)
    {
        try
        {
            var chatMembers = await client.SearchChatMembersAsync(
                chatId: supergroupId,    // 1.8+ 直接接受 SupergroupId
                query: "",
                limit: batchSize,
                filter: new TdApi.ChatMembersFilter.ChatMembersFilterMembers());

            var members = chatMembers?.Members;
            if (members == null || members.Length == 0)
            {
                hasMore = false;
                break;
            }

            foreach (var cm in members)
            {
                // 新版 TDLib: 成员 ID 来自 member.MemberId (MessageSender),需要拆出 userId
                if (cm.MemberId is not TdApi.MessageSender.MessageSenderUser msu) continue;
                if (result.Any(r => r.UserId == msu.UserId)) continue;
                if (limit > 0 && result.Count >= limit) { hasMore = false; break; }

                try
                {
                    var user = await client.GetUserAsync(msu.UserId);
                    var info = new MemberInfo
                    {
                        UserId = user.Id,
                        FirstName = user.FirstName,
                        LastName = user.LastName,
                        Username = user.Usernames?.ActiveUsernames?.FirstOrDefault(),
                        PhoneNumber = user.PhoneNumber,
                        IsBot = user.Type is TdApi.UserType.UserTypeBot,
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
                    logger.ZLogWarning($"获取用户 {msu.UserId} 失败: {ex.Error.Message}");
                }
            }

            offset += members.Length;
            if (members.Length < batchSize) hasMore = false;
            logger.ZLogInformation($"已导出 {result.Count} 个成员...");
            await Task.Delay(300);
        }
        catch (TdException ex) when (ex.Error.Code == 429)
        {
            int retryAfter = TdlEnv.ParseRetryAfter(ex);
            logger.ZLogWarning($"触发频率限制,等待 {retryAfter} 秒后继续...");
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
    // 新版 TDLib: MemberId 是 MessageSender,需要拆出 UserId
    if (member.MemberId is not TdApi.MessageSender.MessageSenderUser msu) return null;
    long userId = msu.UserId;

    try
    {
        var user = await client.GetUserAsync(userId);
        var info = new MemberInfo
        {
            UserId = user.Id,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Username = user.Usernames?.ActiveUsernames?.FirstOrDefault(),
            PhoneNumber = user.PhoneNumber,
            IsBot = user.Type is TdApi.UserType.UserTypeBot,
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
        logger.ZLogWarning(ex, $"构建成员信息失败: UserId={userId}");
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
