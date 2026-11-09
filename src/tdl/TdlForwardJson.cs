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
// TdlForwardJson.cs — 读取标签导出 JSON, 将消息按相册分组转发到指定聊天/话题
//
// 数据来源: src/crawler/TelegramChannelExport.cs --split-by-tag 生成的按标签 JSON
// 转发方式: 通过 TDLib 深拷贝源频道消息 (sendCopy), 相册按 MediaAlbumId 分组,
//          可选连同评论区一起转发到目标论坛话题。
//
// 用法:
//   dotnet run src/tdl/TdlForwardJson.cs -- --json <标签JSON> [选项]
//   dotnet run src/tdl/TdlForwardJson.cs -- --dir <作者目录> [选项]
//
// 选项:
//   --json      单个标签 JSON 路径 (与 --dir 二选一)
//   --dir       按作者拆分的目录, 全部文件统一转发到 --topic 指定话题, 不存在则新建 (跳过 index.json)
//   --per-file-topic  配合 --dir, 每个文件以文件名(去扩展名)作为独立话题, 忽略 --topic
//   --target    目标链接, 默认 https://t.me/+CDfEHAp_CuNiYjQ1 (邀请链接)
//               t.me/c/<群组内部id>/<话题消息id> 形式会直接按话题消息 ID 解析
//   --topic     目标话题名称 (--json 默认用文件名; --dir 默认取目录名去掉 _by_author)
//   --comments  是否转发评论 (默认 true)
//   --dry-run   只检查分组/评论数量/话题是否存在, 不实际转发也不新建话题
//
// 示例:
//   dotnet run src/tdl/TdlForwardJson.cs -- --json output/telegram/sourpuss1988_8885_latest_by_tag/素人.json --topic 素人
//   dotnet run src/tdl/TdlForwardJson.cs -- --dir output/telegram/sourpuss1988_8885_latest_by_tag/推特_by_author --topic 推特
// ============================================================================

using System.CommandLine;
using System.Text.Json;
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
    var logger = TdlEnv.CreateLogger("tdl-forward-json.log", "tdl-forward-json");

    var optJson = new Option<string?>("--json") { Description = "单个标签导出 JSON 路径" };
    var optDir = new Option<string?>("--dir") { Description = "按作者拆分的目录, 每个文件转发到同名话题" };
    var optTarget = new Option<string>("--target")
    {
        DefaultValueFactory = _ => "https://t.me/+CDfEHAp_CuNiYjQ1",
        Description = "目标链接 (邀请链接或 t.me/c/<群组内部id>/<话题消息id>)"
    };
    var optTopic = new Option<string>("--topic")
    {
        DefaultValueFactory = _ => "",
        Description = "目标话题名称 (--json 默认用文件名; --dir 默认取目录名去掉 _by_author)"
    };
    var optComments = new Option<bool>("--comments") { DefaultValueFactory = _ => true, Description = "是否转发评论" };
    var optPerFileTopic = new Option<bool>("--per-file-topic") { DefaultValueFactory = _ => false, Description = "目录模式下每个文件用文件名作为独立话题" };
    var optDryRun = new Option<bool>("--dry-run") { DefaultValueFactory = _ => false, Description = "只检查分组/评论数量/话题是否存在, 不实际转发" };
    var optDeleteTopic = new Option<int?>("--delete-topic") { Description = "删除指定 ForumTopicId 后退出 (清理用, 不需要 --json/--dir)" };

    var root = new RootCommand("读取标签 JSON, 将消息按相册分组转发到指定聊天/话题");
    root.Options.Add(optJson);
    root.Options.Add(optDir);
    root.Options.Add(optTarget);
    root.Options.Add(optTopic);
    root.Options.Add(optComments);
    root.Options.Add(optPerFileTopic);
    root.Options.Add(optDryRun);
    root.Options.Add(optDeleteTopic);

    root.SetAction(async (parseResult, ct) =>
    {
        string? jsonPath = parseResult.GetValue(optJson);
        string? dirPath = parseResult.GetValue(optDir);
        string targetLink = parseResult.GetValue(optTarget)!;
        string topicName = parseResult.GetValue(optTopic)!;
        bool forwardComments = parseResult.GetValue(optComments);
        bool perFileTopic = parseResult.GetValue(optPerFileTopic);
        bool dryRun = parseResult.GetValue(optDryRun);
        int? deleteTopicId = parseResult.GetValue(optDeleteTopic);

        if (deleteTopicId == null && (jsonPath == null) == (dirPath == null))
        {
            logger.ZLogError($"必须且只能指定 --json 或 --dir 之一");
            return 2;
        }

        if (perFileTopic && dirPath == null)
        {
            logger.ZLogError($"--per-file-topic 只能和 --dir 一起使用");
            return 2;
        }

        var jobs = new List<(string File, string Topic)>();
        if (deleteTopicId == null && jsonPath != null)
        {
            if (!File.Exists(jsonPath))
            {
                logger.ZLogError($"JSON 文件不存在: {jsonPath}");
                return 2;
            }

            string singleTopic = string.IsNullOrWhiteSpace(topicName)
                ? Path.GetFileNameWithoutExtension(jsonPath)
                : topicName.Trim();
            jobs.Add((jsonPath, singleTopic));
        }
        else if (deleteTopicId == null)
        {
            if (!Directory.Exists(dirPath))
            {
                logger.ZLogError($"目录不存在: {dirPath}");
                return 2;
            }

            var files = Directory.GetFiles(dirPath!, "*.json")
                .Where(f => !string.Equals(Path.GetFileName(f), "index.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count == 0)
            {
                logger.ZLogError($"目录中没有 JSON 文件: {dirPath}");
                return 2;
            }

            if (perFileTopic)
            {
                // 每个文件以文件名(去扩展名)作为独立话题
                foreach (string file in files)
                {
                    jobs.Add((file, Path.GetFileNameWithoutExtension(file)));
                }
            }
            else
            {
                // 目录模式下所有文件统一转发到同一个话题
                string dirTopic = string.IsNullOrWhiteSpace(topicName)
                    ? Path.GetFileName(Path.TrimEndingDirectorySeparator(dirPath!))
                        .Replace("_by_author", "", StringComparison.OrdinalIgnoreCase)
                    : topicName.Trim();
                if (string.IsNullOrWhiteSpace(dirTopic))
                {
                    dirTopic = "推特";
                }

                foreach (string file in files)
                {
                    jobs.Add((file, dirTopic));
                }
            }
        }

        logger.ZLogInformation($"待处理 {jobs.Count} 个文件");

        string tdlRoot = TdlEnv.InitTdlRoot(logger);
        var service = new TdlForwardService(client, logger, tdlRoot);
        await service.WaitReadyAsync();

        if (service.AuthNeeded)
        {
            await service.AuthenticateAsync();
        }

        var currentUser = await service.GetCurrentUserAsync();
        logger.ZLogInformation($"成功登录为 [[{currentUser.Id}]] / [[{currentUser.FirstName}]]");

        var (targetChatId, fixedTopic) = await ResolveTargetChatAsync(service, client, logger, targetLink);
        if (targetChatId == 0)
        {
            return 1;
        }

        if (deleteTopicId is int deleteId)
        {
            await client.GetChatAsync(targetChatId);
            await client.DeleteForumTopicAsync(targetChatId, deleteId);
            logger.ZLogInformation($"已删除话题 ForumTopicId={deleteId}");
            return 0;
        }

        var topicCache = await LoadTopicsAsync(client, targetChatId, logger);
        if (topicCache == null && fixedTopic == null)
        {
            logger.ZLogError($"目标聊天不是论坛或无法读取话题列表");
            return 1;
        }

        bool singleFileMode = jsonPath != null;
        var linkCache = new Dictionary<string, (long ChatId, long MessageId)>(StringComparer.OrdinalIgnoreCase);
        string dataDir = Path.Combine(Path.EntryPointFileDirectoryPath(), "data");
        int succeeded = 0;
        int failed = 0;
        int totalForwarded = 0;

        foreach (var (file, jobTopicName) in jobs)
        {
            logger.ZLogInformation($"===== 处理 [{Path.GetFileName(file)}] → 话题 [[{jobTopicName}]] =====");

            try
            {
                var export = JsonSerializer.Deserialize<TagExport>(
                    await File.ReadAllTextAsync(file, ct),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (export?.Messages == null || export.Messages.Count == 0)
                {
                    logger.ZLogWarning($"JSON 中没有消息, 跳过: {Path.GetFileName(file)}");
                    failed++;
                    continue;
                }

                var urls = export.Messages
                    .Select(m => m.Url)
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Select(u => u!)
                    .Distinct()
                    .ToList();

                if (urls.Count == 0)
                {
                    logger.ZLogWarning($"JSON 中没有消息链接, 跳过: {Path.GetFileName(file)}");
                    failed++;
                    continue;
                }

                var (sourceChatId, sourceMessageIds) = await ResolveMessagesAsync(client, urls, linkCache, logger, ct);
                if (sourceChatId == 0 || sourceMessageIds.Count == 0)
                {
                    logger.ZLogWarning($"无法解析源消息, 跳过: {Path.GetFileName(file)}");
                    failed++;
                    continue;
                }

                var sourceChat = await client.GetChatAsync(sourceChatId);
                logger.ZLogInformation($"源频道: [[{sourceChat.Title}]] ChatId={sourceChatId} 消息 {sourceMessageIds.Count} 条");

                if (dryRun)
                {
                    var messages = await service.LoadMessagesWithAlbumsAsync(sourceChatId, sourceMessageIds);
                    PrintGroups(messages, sourceMessageIds, logger);

                    bool exists = topicCache != null
                        ? topicCache.ContainsKey(jobTopicName.Trim())
                        : fixedTopic != null;
                    logger.ZLogInformation($"话题 [[{jobTopicName}]]: {(exists ? "已存在" : "不存在(将新建)")}");
                    succeeded++;
                    continue;
                }

                TdApi.MessageTopic? topic = singleFileMode && fixedTopic != null
                    ? fixedTopic
                    : await FindOrCreateTopicAsync(client, targetChatId, jobTopicName, topicCache!, logger);

                if (topic == null)
                {
                    failed++;
                    continue;
                }

                using var db = new ForwardDbContext(BuildDbPath(dataDir, sourceChatId, jobTopicName));
                await db.Database.EnsureCreatedAsync(ct);

                int forwarded = await service.ForwardMessagesByIdsAsync(
                    db, sourceChatId, targetChatId, sourceMessageIds, topic, forwardComments);

                totalForwarded += forwarded;
                logger.ZLogInformation($"[{Path.GetFileName(file)}] 完成, 转发 {forwarded} 条消息");
                succeeded++;
            }
            catch (TdException ex)
            {
                logger.ZLogError($"[{Path.GetFileName(file)}] 失败: {ex.Error.Code} {ex.Error.Message}");
                failed++;
            }
            catch (Exception ex)
            {
                logger.ZLogError($"[{Path.GetFileName(file)}] 异常: {ex.Message}");
                failed++;
            }
        }

        logger.ZLogInformation($"全部结束: 成功 {succeeded} 个文件, 失败 {failed} 个, 共转发 {totalForwarded} 条消息 (转发评论: {forwardComments})");
        return failed == 0 ? 0 : 1;
    });

    return await root.Parse(args).InvokeAsync();
}

async Task<(long chatId, TdApi.MessageTopic? topic)> ResolveTargetChatAsync(
    TdlForwardService service, TdClient client, ILogger logger, string targetLink)
{
    var privateMatch = Regex.Match(targetLink, @"t\.me/c/(\d+)/(\d+)", RegexOptions.IgnoreCase);
    if (privateMatch.Success)
    {
        long internalId = long.Parse(privateMatch.Groups[1].Value);
        int topicMessageId = int.Parse(privateMatch.Groups[2].Value);
        long chatId = -(1_000_000_000_000L + internalId);

        var chat = await client.GetChatAsync(chatId);
        logger.ZLogInformation($"目标: [[{chat.Title}]] ChatId={chatId} 话题消息={topicMessageId}");

        // 校验话题存在, 不存在会抛 TdException
        await client.GetForumTopicAsync(chatId, topicMessageId);
        var topic = new TdApi.MessageTopic.MessageTopicForum { ForumTopicId = topicMessageId };
        return (chatId, topic);
    }

    bool isInvite = Regex.IsMatch(targetLink,
        @"(?:t\.me|telegram\.me)/(?:\+|joinchat/)", RegexOptions.IgnoreCase);
    if (isInvite)
    {
        long chatId;
        try
        {
            var invite = await client.CheckChatInviteLinkAsync(targetLink);
            chatId = invite.ChatId;
            logger.ZLogInformation($"邀请链接: [[{invite.Title}]] ChatId={chatId}");

            if (chatId == 0)
            {
                logger.ZLogInformation($"尚未加入 [[{invite.Title}]], 正在加入...");
                var joinResult = await client.JoinChatByInviteLinkAsync(targetLink);
                if (joinResult is TdApi.ChatJoinResult.ChatJoinResultSuccess joinSuccess)
                {
                    chatId = joinSuccess.ChatId;
                }
                else if (joinResult is TdApi.ChatJoinResult.ChatJoinResultRequestSent)
                {
                    logger.ZLogError($"已提交加入申请, 需管理员审批后重试: {invite.Title}");
                    return (0, null);
                }
                else
                {
                    logger.ZLogError($"加入失败: {joinResult.DataType}");
                    return (0, null);
                }
            }
        }
        catch (TdException ex)
        {
            logger.ZLogError($"邀请链接处理失败: {ex.Error.Code} {ex.Error.Message}");
            return (0, null);
        }

        var chat = await client.GetChatAsync(chatId);
        logger.ZLogInformation($"目标: [[{chat.Title}]] ChatId={chatId}");
        return (chatId, null);
    }

    long resolved = await service.ResolveTargetLinkAsync(targetLink);
    if (resolved == 0)
    {
        logger.ZLogError($"无法解析目标链接: {targetLink}");
        return (0, null);
    }

    var targetChat = await client.GetChatAsync(resolved);
    logger.ZLogInformation($"目标: [[{targetChat.Title}]] ChatId={resolved}");
    return (resolved, null);
}

async Task<(long chatId, List<long> messageIds)> ResolveMessagesAsync(
    TdClient client, List<string> urls, Dictionary<string, (long ChatId, long MessageId)> cache,
    ILogger logger, CancellationToken ct)
{
    long chatId = 0;
    var ids = new List<long>();

    foreach (string url in urls)
    {
        if (!cache.TryGetValue(url, out var hit))
        {
            try
            {
                var linkInfo = await client.GetMessageLinkInfoAsync(url);
                if (linkInfo.Message == null)
                {
                    logger.ZLogWarning($"消息链接未关联到消息: {url}");
                    continue;
                }

                hit = (linkInfo.Message.ChatId, linkInfo.Message.Id);
                cache[url] = hit;
            }
            catch (TdException ex)
            {
                logger.ZLogWarning($"解析消息链接失败: {url} ({ex.Error.Code} {ex.Error.Message})");
                continue;
            }

            await Task.Delay(200, ct);
        }

        if (chatId == 0)
        {
            chatId = hit.ChatId;
        }
        else if (hit.ChatId != chatId)
        {
            logger.ZLogWarning($"跳过其他聊天中的消息: {url}");
            continue;
        }

        ids.Add(hit.MessageId);
    }

    return (chatId, ids.Distinct().OrderBy(id => id).ToList());
}

string BuildDbPath(string dataDir, long sourceChatId, string topicName)
{
    // 按 源聊天+目标话题 隔离去重记录, 避免不同话题之间互相跳过
    string safeName = Regex.Replace(topicName, @"[\\/:*?""<>|\s]+", "_").Trim('_');
    if (string.IsNullOrEmpty(safeName)) safeName = "topic";
    return Path.Combine(dataDir, $"forward-{sourceChatId}-{safeName}.db");
}

async Task<Dictionary<string, int>?> LoadTopicsAsync(TdClient client, long chatId, ILogger logger)
{
    try
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int offsetDate = 0;
        long offsetMessageId = 0;
        int offsetTopicId = 0;
        int totalCount = 0;

        // 分页拉取全部话题，避免超过 100 个话题后重复创建
        for (int page = 0; page < 100; page++)
        {
            var result = await client.GetForumTopicsAsync(chatId, "", offsetDate, offsetMessageId, offsetTopicId, 100);
            totalCount = result.TotalCount;
            foreach (var topic in result.Topics)
            {
                map[topic.Info.Name.Trim()] = topic.Info.ForumTopicId;
            }

            if (result.Topics.Length == 0 || map.Count >= totalCount
                || (result.NextOffsetDate == offsetDate
                    && result.NextOffsetMessageId == offsetMessageId
                    && result.NextOffsetForumTopicId == offsetTopicId))
            {
                break;
            }

            offsetDate = result.NextOffsetDate;
            offsetMessageId = result.NextOffsetMessageId;
            offsetTopicId = result.NextOffsetForumTopicId;
        }

        logger.ZLogInformation($"目标论坛话题 ({totalCount}): {string.Join(" | ", map.Keys)}");
        return map;
    }
    catch (TdException ex)
    {
        logger.ZLogError($"读取论坛话题失败: {ex.Error.Code} {ex.Error.Message}");
        return null;
    }
}

async Task<TdApi.MessageTopic?> FindOrCreateTopicAsync(
    TdClient client, long chatId, string name, Dictionary<string, int> topicCache, ILogger logger)
{
    string key = name.Trim();

    if (topicCache.TryGetValue(key, out int existingId))
    {
        logger.ZLogInformation($"话题已存在: [[{key}]] ForumTopicId={existingId}");
        return new TdApi.MessageTopic.MessageTopicForum { ForumTopicId = existingId };
    }

    logger.ZLogInformation($"话题不存在, 新建: [[{key}]]");
    for (int attempt = 1; ; attempt++)
    {
        try
        {
            var created = await client.CreateForumTopicAsync(
                chatId, key, false, new TdApi.ForumTopicIcon { Color = 0x6FB9F0, CustomEmojiId = 0 });

            topicCache[key] = created.ForumTopicId;
            logger.ZLogInformation($"新建话题成功: [[{created.Name}]] ForumTopicId={created.ForumTopicId}");
            return new TdApi.MessageTopic.MessageTopicForum { ForumTopicId = created.ForumTopicId };
        }
        catch (TdException ex) when (ex.Error.Code == 429 && attempt <= 5)
        {
            int retryAfter = TdlEnv.ParseRetryAfter(ex);
            logger.ZLogWarning($"新建话题触发频率限制 (第{attempt}次)，等待 {retryAfter} 秒后重试...");
            await Task.Delay(retryAfter * 1000);
        }
    }
}

void PrintGroups(List<TdApi.Message> messages, List<long> requestedIds, ILogger logger)
{
    var foundIds = messages.Select(m => m.Id).ToHashSet();
    var missing = requestedIds.Where(id => !foundIds.Contains(id)).ToList();

    var groups = new List<List<TdApi.Message>>();
    foreach (var msg in messages.OrderBy(m => m.Id))
    {
        if (groups.Count > 0 && msg.MediaAlbumId != 0 && groups[^1][0].MediaAlbumId == msg.MediaAlbumId)
        {
            groups[^1].Add(msg);
        }
        else
        {
            groups.Add([msg]);
        }
    }

    var withReplyInfo = messages.Where(m => m.InteractionInfo?.ReplyInfo != null).ToList();
    long replyCount = withReplyInfo.Sum(m => m.InteractionInfo!.ReplyInfo.ReplyCount);

    logger.ZLogInformation($"源消息 {messages.Count} 条, 分组 {groups.Count} 组, 缺失 {missing.Count} 条");
    if (missing.Count > 0)
    {
        logger.ZLogWarning($"缺失消息 ID: {string.Join(",", missing)}");
    }
    logger.ZLogInformation($"可读取评论数的消息 {withReplyInfo.Count} 条, 评论合计 {replyCount} 条");

    foreach (var group in groups)
    {
        var label = group[0].MediaAlbumId != 0 ? $"相册:{group[0].MediaAlbumId}" : "单条";
        var replies = group.Sum(m => m.InteractionInfo?.ReplyInfo?.ReplyCount ?? 0);
        logger.ZLogInformation($"  {label} 消息 {group[0].Id}~{group[^1].Id} 数量={group.Count} 评论={replies}");
    }
}

public class TagExport
{
    public string Tag { get; set; } = "";
    public int Count { get; set; }
    public List<TagMessage> Messages { get; set; } = [];
}

public class TagMessage
{
    public long Id { get; set; }
    public string? Url { get; set; }
}
