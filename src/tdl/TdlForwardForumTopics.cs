#!/usr/bin/env dotnet

#:include ../../env.cs
#:include TdlUpdateHandler.cs
#:include TdlEnv.cs
#:include TdlForwardDbContext.cs
#:include TdlForwardService.cs

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
#:package Microsoft.EntityFrameworkCore.Sqlite@*
#:package Microsoft.EntityFrameworkCore.Design@*

// ============================================================================
// TdlForwardForumTopics.cs — 将一个论坛群组的全部话题逐话题转发到另一个论坛群组
//
// 行为:
//   1. 读取源群组的全部话题 (自动分页, 超过 100 个也不会遗漏)
//   2. 在目标群组按同名话题匹配; 不存在时自动创建 (沿用源话题图标)
//   3. 将源话题内的消息按时间正序、相册分组后深拷贝到目标对应话题
//   4. 通过 SQLite 记录已转发消息, 重复执行不会重复转发
//
// 用法:
//   dotnet run src/tdl/TdlForwardForumTopics.cs -- [选项]
//
// 选项:
//   --source     源群组链接 (默认 https://t.me/gs1d3g1s5)
//   --target     目标群组链接/邀请链接 (默认 https://t.me/+_Y2bXsGZzswzMDFl)
//   --topic      只处理指定名称的话题 (默认全部话题)
//   --limit      每个话题最多转发最近 N 条消息, 0 表示全部 (默认 0)
//   --comments   是否同时转发话题消息下的评论 (默认 false)
//   --dry-run    只打印话题映射和消息数量, 不创建话题、不转发
//
// 示例:
//   dotnet run src/tdl/TdlForwardForumTopics.cs -- --dry-run
//   dotnet run src/tdl/TdlForwardForumTopics.cs -- --topic 素人
// ============================================================================

using System.CommandLine;
using System.Text.RegularExpressions;
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
    var logger = TdlEnv.CreateLogger("tdl-forward-forum-topics.log", "tdl-forward-forum-topics");

    var optSource = new Option<string>("--source")
    {
        DefaultValueFactory = _ => "https://t.me/gs1d3g1s5",
        Description = "源群组链接"
    };
    var optTarget = new Option<string>("--target")
    {
        DefaultValueFactory = _ => "https://t.me/+_Y2bXsGZzswzMDFl",
        Description = "目标群组链接/邀请链接"
    };
    var optTopic = new Option<string?>("--topic") { Description = "只处理指定名称的话题" };
    var optLimit = new Option<int>("--limit") { DefaultValueFactory = _ => 0, Description = "每个话题最多转发最近 N 条消息 (0=全部)" };
    var optComments = new Option<bool>("--comments") { DefaultValueFactory = _ => false, Description = "是否同时转发评论" };
    var optDryRun = new Option<bool>("--dry-run") { DefaultValueFactory = _ => false, Description = "只检查不执行" };

    var root = new RootCommand("将源论坛群组的全部话题逐话题转发到目标论坛群组, 目标缺失话题自动创建");
    root.Options.Add(optSource);
    root.Options.Add(optTarget);
    root.Options.Add(optTopic);
    root.Options.Add(optLimit);
    root.Options.Add(optComments);
    root.Options.Add(optDryRun);

    root.SetAction(async (parseResult, ct) =>
    {
        string sourceLink = parseResult.GetValue(optSource)!;
        string targetLink = parseResult.GetValue(optTarget)!;
        string? topicFilter = parseResult.GetValue(optTopic);
        int perTopicLimit = Math.Max(0, parseResult.GetValue(optLimit));
        bool forwardComments = parseResult.GetValue(optComments);
        bool dryRun = parseResult.GetValue(optDryRun);

        string tdlRoot = TdlEnv.InitTdlRoot(logger);
        var service = new TdlForwardService(client, logger, tdlRoot);
        await service.WaitReadyAsync();

        if (service.AuthNeeded)
        {
            await service.AuthenticateAsync();
        }

        var me = await service.GetCurrentUserAsync();
        logger.ZLogInformation($"登录账号: [[{me.FirstName}]] Id={me.Id}");

        // ── 解析源群组 ──
        long sourceChatId = await ResolveChatAsync(client, service, logger, sourceLink, allowJoin: false);
        if (sourceChatId == 0)
        {
            logger.ZLogError($"无法解析源群组: {sourceLink}");
            return 1;
        }

        var sourceChat = await client.GetChatAsync(sourceChatId);
        if (sourceChat.Type is not TdApi.ChatType.ChatTypeSupergroup sourceSg)
        {
            logger.ZLogError($"源群组不是超级群组, 无法读取话题: {sourceChat.Title}");
            return 1;
        }

        var sourceInfo = await client.GetSupergroupAsync(sourceSg.SupergroupId);
        if (!sourceInfo.IsForum)
        {
            logger.ZLogError($"源群组未开启话题功能, 没有可转发话题: {sourceChat.Title}");
            return 1;
        }

        logger.ZLogInformation($"源群组: [[{sourceChat.Title}]] ChatId={sourceChatId}");

        // ── 解析目标群组 (dry-run 不会加入新群组) ──
        long targetChatId = await ResolveChatAsync(client, service, logger, targetLink, allowJoin: !dryRun);
        var targetTopics = new List<TdApi.ForumTopicInfo>();

        if (targetChatId != 0)
        {
            var targetChat = await client.GetChatAsync(targetChatId);
            if (targetChat.Type is not TdApi.ChatType.ChatTypeSupergroup targetSg)
            {
                logger.ZLogError($"目标不是超级群组: {targetChat.Title}");
                return 1;
            }

            var targetInfo = await client.GetSupergroupAsync(targetSg.SupergroupId);
            logger.ZLogInformation($"目标群组: [[{targetChat.Title}]] ChatId={targetChatId}");

            if (!targetInfo.IsForum)
            {
                if (dryRun)
                {
                    logger.ZLogWarning($"目标群组未开启话题, 实际执行时会自动开启");
                }
                else
                {
                    logger.ZLogInformation($"目标群组未开启话题, 正在开启...");
                    await client.ToggleSupergroupIsForumAsync(targetSg.SupergroupId, true);
                    for (int i = 0; i < 20; i++)
                    {
                        await Task.Delay(500);
                        targetInfo = await client.GetSupergroupAsync(targetSg.SupergroupId);
                        if (targetInfo.IsForum) break;
                    }

                    if (!targetInfo.IsForum)
                    {
                        logger.ZLogError($"目标群组开启话题失败 (需要管理员权限)");
                        return 1;
                    }
                }
            }

            if (targetInfo.IsForum)
            {
                targetTopics = await LoadTopicsAsync(client, targetChatId, logger);
                logger.ZLogInformation($"目标已有话题 {targetTopics.Count} 个");
            }
        }
        else if (dryRun)
        {
            logger.ZLogWarning($"目标群组未解析 (dry-run 不会加入新群组): {targetLink}");
        }
        else
        {
            logger.ZLogError($"无法解析或加入目标群组: {targetLink}");
            return 1;
        }

        // ── 读取源话题列表 ──
        var sourceTopics = await LoadTopicsAsync(client, sourceChatId, logger);
        logger.ZLogInformation($"源话题共 {sourceTopics.Count} 个");

        // 目标话题缓存: 普通话题按名称匹配; General 话题按 IsGeneral 匹配
        var topicCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int? targetGeneralId = null;
        foreach (var info in targetTopics)
        {
            if (info.IsGeneral)
            {
                targetGeneralId = info.ForumTopicId;
            }
            else
            {
                topicCache[info.Name.Trim()] = info.ForumTopicId;
            }
        }

        // 按 源群组+目标群组 隔离去重记录
        string dbPath = Path.Combine(tdlRoot, $"forward-{sourceChatId}-{targetChatId}.db");
        using var db = dryRun ? null : new ForwardDbContext(dbPath);
        if (db != null)
        {
            await db.Database.EnsureCreatedAsync(ct);
        }

        int topicSucceeded = 0;
        int topicFailed = 0;
        int topicSkipped = 0;
        int totalForwarded = 0;

        foreach (var sourceTopic in sourceTopics)
        {
            string name = sourceTopic.Name.Trim();
            if (topicFilter != null && !string.Equals(name, topicFilter.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            logger.ZLogInformation($"===== 话题 [[{name}]] (ForumTopicId={sourceTopic.ForumTopicId}) =====");

            try
            {
                // ── 收集源话题消息 ID (时间正序, 跳过服务消息与不可转发消息) ──
                var messageIds = await CollectTopicMessageIdsAsync(
                    client, sourceChatId, sourceTopic.ForumTopicId, perTopicLimit, logger, ct);

                if (messageIds.Count == 0)
                {
                    logger.ZLogInformation($"话题 [[{name}]] 没有可转发消息, 跳过");
                    topicSkipped++;
                    continue;
                }

                if (dryRun)
                {
                    bool exists = sourceTopic.IsGeneral
                        ? targetGeneralId != null
                        : topicCache.ContainsKey(name);
                    logger.ZLogInformation($"话题 [[{name}]]: 待转发 {messageIds.Count} 条, " +
                        $"目标{(exists ? "已存在" : "不存在(将新建)")}");
                    topicSucceeded++;
                    continue;
                }

                // ── 定位或创建目标话题 ──
                int? targetTopicId = await FindOrCreateTopicAsync(
                    client, targetChatId, sourceTopic, name, topicCache, targetGeneralId, logger);

                if (targetTopicId == null)
                {
                    topicFailed++;
                    continue;
                }

                var topic = new TdApi.MessageTopic.MessageTopicForum { ForumTopicId = targetTopicId.Value };
                int forwarded = await service.ForwardMessagesByIdsAsync(
                    db!, sourceChatId, targetChatId, messageIds, topic, forwardComments);

                totalForwarded += forwarded;
                logger.ZLogInformation($"[{name}] 完成, 本次转发 {forwarded} 条");
                topicSucceeded++;
            }
            catch (TdException ex)
            {
                logger.ZLogError($"[{name}] 失败: {ex.Error.Code} {ex.Error.Message}");
                topicFailed++;
            }
            catch (Exception ex)
            {
                logger.ZLogError(ex, $"[{name}] 异常");
                topicFailed++;
            }

            await Task.Delay(2000, ct);
        }

        logger.ZLogInformation(
            $"全部结束: 话题成功 {topicSucceeded} 个, 跳过 {topicSkipped} 个, 失败 {topicFailed} 个, 共转发 {totalForwarded} 条消息");
        return topicFailed == 0 ? 0 : 1;
    });

    return await root.Parse(args).InvokeAsync();
}

/// <summary>
/// 解析聊天链接: 已加入的邀请链接直接返回 ChatId; allowJoin 为 true 时自动加入未加入的邀请链接.
/// </summary>
async Task<long> ResolveChatAsync(
    TdClient client, TdlForwardService service, ILogger logger, string link, bool allowJoin)
{
    long chatId = await service.ResolveTargetLinkAsync(link);
    if (chatId != 0) return chatId;

    if (!IsInviteLink(link)) return 0;

    try
    {
        var invite = await client.CheckChatInviteLinkAsync(link);
        logger.ZLogInformation($"邀请链接指向: [[{invite.Title}]] 成员 {invite.MemberCount} 人");

        if (invite.ChatId != 0) return invite.ChatId;

        if (!allowJoin)
        {
            logger.ZLogWarning($"尚未加入 [[{invite.Title}]], 需要先加入再执行");
            return 0;
        }

        if (invite.CreatesJoinRequest)
        {
            logger.ZLogError($"[[{invite.Title}]] 需要管理员审批才能加入");
            return 0;
        }

        logger.ZLogInformation($"正在加入 [[{invite.Title}]]...");
        var joinResult = await client.JoinChatByInviteLinkAsync(link);
        if (joinResult is TdApi.ChatJoinResult.ChatJoinResultSuccess success)
        {
            return success.ChatId;
        }

        logger.ZLogError($"加入 [[{invite.Title}]] 失败: {joinResult.DataType}");
        return 0;
    }
    catch (TdException ex)
    {
        logger.ZLogError($"邀请链接处理失败: {ex.Error.Code} {ex.Error.Message}");
        return 0;
    }
}

bool IsInviteLink(string input)
{
    if (string.IsNullOrWhiteSpace(input)) return false;
    var trimmed = input.Trim();
    return Regex.IsMatch(trimmed, @"(?:t\.me|telegram\.me)/(?:\+|joinchat/)", RegexOptions.IgnoreCase);
}

/// <summary>
/// 分页读取群组全部话题.
/// </summary>
async Task<List<TdApi.ForumTopicInfo>> LoadTopicsAsync(TdClient client, long chatId, ILogger logger)
{
    var result = new List<TdApi.ForumTopicInfo>();
    int offsetDate = 0;
    long offsetMessageId = 0;
    int offsetTopicId = 0;

    for (int page = 0; page < 100; page++)
    {
        var chunk = await client.GetForumTopicsAsync(chatId, "", offsetDate, offsetMessageId, offsetTopicId, 100);
        foreach (var topic in chunk.Topics)
        {
            result.Add(topic.Info);
        }

        if (chunk.Topics.Length == 0 || result.Count >= chunk.TotalCount)
        {
            break;
        }

        if (chunk.NextOffsetDate == offsetDate
            && chunk.NextOffsetMessageId == offsetMessageId
            && chunk.NextOffsetForumTopicId == offsetTopicId)
        {
            break;
        }

        offsetDate = chunk.NextOffsetDate;
        offsetMessageId = chunk.NextOffsetMessageId;
        offsetTopicId = chunk.NextOffsetForumTopicId;
    }

    return result;
}

/// <summary>
/// 分页拉取某话题的消息 ID, 返回按消息 ID 升序 (即时间正序) 的列表.
/// limit > 0 时只保留最近 limit 条.
/// </summary>
async Task<List<long>> CollectTopicMessageIdsAsync(
    TdClient client, long chatId, int forumTopicId, int limit, ILogger logger, CancellationToken ct)
{
    var ids = new List<long>();
    long fromMessageId = 0;
    int skippedUnforwardable = 0;

    while (true)
    {
        TdApi.Messages page;
        try
        {
            page = await client.GetForumTopicHistoryAsync(chatId, forumTopicId, fromMessageId, 0, 100);
        }
        catch (TdException ex) when (ex.Error.Code == 400)
        {
            logger.ZLogWarning($"读取话题历史失败 (ForumTopicId={forumTopicId}): {ex.Error.Message}");
            break;
        }

        var messages = page.Messages_ ?? [];
        if (messages.Length == 0) break;

        long oldestId = messages.Min(m => m.Id);
        foreach (var message in messages)
        {
            // Content 为 null 表示服务消息; CanBeSaved=false 的消息无法深拷贝
            if (message.Content == null || !message.CanBeSaved)
            {
                skippedUnforwardable++;
                continue;
            }

            ids.Add(message.Id);
        }

        if (limit > 0 && ids.Count >= limit) break;
        if (messages.Length < 100) break;
        if (oldestId == fromMessageId || oldestId <= 1) break; // 无进展时兜底, 防止死循环

        // 频道/论坛消息 ID 不连续, 不能使用 oldestId-1;
        // 用 oldestId 作为下一页起点, 重复消息由最终 Distinct 去重
        fromMessageId = oldestId;
        await Task.Delay(500, ct);
    }

    if (skippedUnforwardable > 0)
    {
        logger.ZLogInformation($"跳过不可转发消息 {skippedUnforwardable} 条 (服务消息/禁止保存)");
    }

    var ordered = ids.Distinct().OrderBy(id => id).ToList();
    if (limit > 0 && ordered.Count > limit)
    {
        ordered = ordered.Skip(ordered.Count - limit).ToList();
    }

    return ordered;
}

/// <summary>
/// 找到同名目标话题, 不存在则创建. General 话题按 IsGeneral 匹配.
/// 返回 null 表示创建失败.
/// </summary>
async Task<int?> FindOrCreateTopicAsync(
    TdClient client, long chatId, TdApi.ForumTopicInfo sourceTopic, string name,
    Dictionary<string, int> topicCache, int? targetGeneralId, ILogger logger)
{
    if (sourceTopic.IsGeneral)
    {
        if (targetGeneralId != null)
        {
            logger.ZLogInformation($"General 话题已存在: ForumTopicId={targetGeneralId}");
            return targetGeneralId;
        }

        logger.ZLogWarning($"目标群组没有可用的 General 话题, 跳过 [[{name}]]");
        return null;
    }

    if (topicCache.TryGetValue(name, out int existingId))
    {
        logger.ZLogInformation($"话题已存在: [[{name}]] ForumTopicId={existingId}");
        return existingId;
    }

    logger.ZLogInformation($"话题不存在, 新建: [[{name}]]");
    for (int attempt = 1; attempt <= 10; attempt++)
    {
        try
        {
            var icon = new TdApi.ForumTopicIcon
            {
                Color = sourceTopic.Icon?.Color ?? 0x6FB9F0,
                CustomEmojiId = sourceTopic.Icon?.CustomEmojiId ?? 0,
            };

            var created = await client.CreateForumTopicAsync(chatId, name, false, icon);
            topicCache[name] = created.ForumTopicId;
            logger.ZLogInformation($"新建话题成功: [[{created.Name}]] ForumTopicId={created.ForumTopicId}");
            return created.ForumTopicId;
        }
        catch (TdException ex) when (ex.Error.Code == 429 && attempt < 10)
        {
            int retryAfter = TdlEnv.ParseRetryAfter(ex);
            logger.ZLogWarning($"新建话题触发频率限制 (第{attempt}次), 等待 {retryAfter} 秒...");
            await Task.Delay(retryAfter * 1000);
        }
        catch (TdException ex)
        {
            logger.ZLogError($"新建话题 [[{name}]] 失败: {ex.Error.Code} {ex.Error.Message}");
            return null;
        }
    }

    return null;
}
