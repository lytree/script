#!/usr/bin/env dotnet

#:package TDLib@*
#:package tdlib.native@*
#:package tdlib.native.win-x64@*
#:package System.CommandLine@*
#:package Spectre.Console@*
#:package Spectre.Console.Ansi@*

// ============================================================================
// TdlBotMediaByStart.cs — 下载指定 bot / 聊天的全部媒体, 并按 /start 命令分组落盘
//
// 一次性完成三件事 (不需要 TdlMediaIndex + TdlMediaDownloader + TdlSessionGroup 三连):
//   1. 拉取聊天全部历史消息 (getChatHistory, 走服务端, 不依赖本地消息库)
//   2. 以「我方发出的 /start」为切分点, 把消息流切成语义会话段
//   3. 按段下载媒体, 直接写入 <out>/0001_start_20261125_215500/ 之类的目录
//
// 用法:
//   dotnet run src/tdl/TdlBotMediaByStart.cs -- --chat @haoren004_bot --dry-run
//   dotnet run src/tdl/TdlBotMediaByStart.cs -- --chat @haoren004_bot
//
// 示例:
//   dotnet run src/tdl/TdlBotMediaByStart.cs -- --chat https://t.me/haoren004_bot --concurrency 6
//   dotnet run src/tdl/TdlBotMediaByStart.cs -- --chat @haoren004_bot --types Photo,Video
//   dotnet run src/tdl/TdlBotMediaByStart.cs -- --chat @haoren004_bot --no-proxy
//
// 运行时 HTTP API (多进程共享同一个 TDLib 会话, 避免多个进程抢 ~/.tdl/db):
//   dotnet run src/tdl/TdlBotMediaByStart.cs -- --serve --port 8788 [--max-jobs 4]
//   curl http://127.0.0.1:8788/health
//   curl "http://127.0.0.1:8788/chats?q=jijizhiijia_bot"
//   curl -X POST http://127.0.0.1:8788/preview -d "{\"chat\":\"@haoren004_bot\"}"
//   curl -X POST http://127.0.0.1:8788/jobs -d "{\"chat\":\"@haoren004_bot\",\"types\":\"Photo,Video\"}"
//   同一聊天想更快: 按段切片提交多个任务, 它们会并行跑
//   curl -X POST http://127.0.0.1:8788/jobs -d "{\"chat\":\"@x_bot\",\"segmentFrom\":0,\"segmentTo\":120}"
//   curl http://127.0.0.1:8788/jobs/<jobId>
//   curl http://127.0.0.1:8788/jobs/<jobId>/files
// ============================================================================

using System.Collections.Concurrent;
using System.CommandLine;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using TdLib;
using TdLib.Bindings;
using static TdlJsonHelper;

string LogInfo(string msg) { Console.WriteLine($"[INFO ] {msg}"); return msg; }
string LogWarn(string msg) { Console.WriteLine($"[WARN ] {msg}"); return msg; }
string LogErr(string msg) { Console.Error.WriteLine($"[ERROR] {msg}"); return msg; }

// 是否启用本地消息库 (env tdl_use_message_db=1/true 开启)。本脚本只走服务端接口, 无需开启。
bool UseMessageDb() =>
    Environment.GetEnvironmentVariable("tdl_use_message_db", EnvironmentVariableTarget.User) is "1" or "true";

await Main(args);

async Task Main(string[] args)
{
    // System.CommandLine 会把 "@xxx" 当成响应文件, 这里先把 @username 转成 t.me 链接形式
    args = args.Select(a => a.Length > 1 && a[0] == '@' ? "https://t.me/" + a[1..] : a).ToArray();
    var optChat = new Option<string?>("--chat") { Description = "目标: t.me 链接 / @username / ChatId (--serve 模式下可省略)" };
    var optServe = new Option<bool>("--serve") { Description = "启动常驻 HTTP 运行时 API, 供多个进程提交/查询下载任务" };
    var optPort = new Option<int>("--port") { DefaultValueFactory = _ => 8788, Description = "HTTP API 监听端口 (仅绑定 127.0.0.1)" };
    var optMaxJobs = new Option<int>("--max-jobs") { DefaultValueFactory = _ => 4, Description = "同时执行的下载任务数上限 (并行度)" };
    var optOutput = new Option<string?>("--output") { Description = "输出根目录 (默认 data/tdl/by-start/<chatId>)" };
    var optConcurrency = new Option<int>("--concurrency") { DefaultValueFactory = _ => 4, Description = "并发下载数 (1-16)" };
    var optTypes = new Option<string?>("--types") { Description = "只下载指定类型, 逗号分隔 (Photo,Video,Document,Animation,Audio,VoiceNote,VideoNote); 默认全部" };
    var optMax = new Option<int>("--max") { DefaultValueFactory = _ => 0, Description = "最多下载 N 个文件 (0=全部)" };
    var optMaxMessages = new Option<int>("--max-messages") { DefaultValueFactory = _ => 0, Description = "最多拉取 N 条历史消息 (0=全部)" };
    var optDryRun = new Option<bool>("--dry-run") { Description = "只拉取并列出分组方案, 不下载" };
    var optOverwrite = new Option<bool>("--overwrite") { Description = "已存在的目标文件也重新下载" };
    var optPriority = new Option<int>("--priority") { DefaultValueFactory = _ => 16, Description = "TDLib 下载优先级 (1-32)" };
    var optNoProxy = new Option<bool>("--no-proxy") { Description = "不使用代理, 直连 Telegram" };
    var optProxy = new Option<string?>("--proxy") { Description = "代理 host:port (默认取 env tdl_proxy, 否则 127.0.0.1:7897)" };
    var optNoMeta = new Option<bool>("--no-meta") { Description = "不写 _meta.json / _summary.json" };

    var root = new RootCommand("下载 bot/聊天的全部媒体, 并按 /start 命令分组到独立目录");
    root.Options.Add(optChat);
    root.Options.Add(optServe);
    root.Options.Add(optPort);
    root.Options.Add(optMaxJobs);
    root.Options.Add(optOutput);
    root.Options.Add(optConcurrency);
    root.Options.Add(optTypes);
    root.Options.Add(optMax);
    root.Options.Add(optMaxMessages);
    root.Options.Add(optDryRun);
    root.Options.Add(optOverwrite);
    root.Options.Add(optPriority);
    root.Options.Add(optNoProxy);
    root.Options.Add(optProxy);
    root.Options.Add(optNoMeta);

    if (args.Length == 0 || args.Any(a => a is "-h" or "--help" or "-?" or "/?" or "help"))
    {
        PrintUsage(root);
        return;
    }

    var p = root.Parse(args);
    if (p.Errors.Count > 0)
    {
        foreach (var e in p.Errors) LogErr(e.Message);
        PrintUsage(root);
        Environment.Exit(2);
        return;
    }

    var chatArg = p.GetValue(optChat);
    var serve = p.GetValue(optServe);
    var port = Math.Clamp(p.GetValue(optPort), 1, 65535);
    var maxJobs = Math.Clamp(p.GetValue(optMaxJobs), 1, 16);
    var outputArg = p.GetValue(optOutput);
    var concurrency = Math.Clamp(p.GetValue(optConcurrency), 1, 16);
    var typeFilter = ParseTypes(p.GetValue(optTypes));
    var maxFiles = Math.Max(0, p.GetValue(optMax));
    var maxMessages = Math.Max(0, p.GetValue(optMaxMessages));
    var dryRun = p.GetValue(optDryRun);
    var overwrite = p.GetValue(optOverwrite);
    var priority = Math.Clamp(p.GetValue(optPriority), 1, 32);
    var noProxy = p.GetValue(optNoProxy);
    var proxyArg = p.GetValue(optProxy);
    var noMeta = p.GetValue(optNoMeta);

    if (!serve && string.IsNullOrWhiteSpace(chatArg))
    {
        LogErr("缺少 --chat (或使用 --serve 启动 HTTP API)");
        PrintUsage(root);
        Environment.Exit(2);
        return;
    }

    if (typeFilter != null)
        LogInfo("类型过滤: " + string.Join(", ", typeFilter));

    // ── 1. 启动 TDLib ────────────────────────────────────────────
    using var client = new TdJsonClient();
    client.Send("{\"@type\":\"setLogVerbosityLevel\",\"new_verbosity_level\":0}");

    var loop = new EventLoop(client);
    loop.Start();

    try
    {
        if (!InitTdlib(loop, noProxy, proxyArg)) return;

        // ── 1.5 HTTP 运行时 API 模式 ─────────────────────────────
        // 不要求事先登录: 服务常驻后由 /auth/* 端点自行完成登录 (手机号 / 验证码 / 二步密码 / QR / Bot Token),
        // 这样多进程调用方永远不需要碰 ~/.tdl/db, 也不会因互踢会话导致状态丢失。
        if (serve)
        {
            await ServeAsync(loop, port, outputArg, concurrency, overwrite, priority, noMeta, maxJobs);
            return;
        }

        var authState = await WaitForReadyAsync(loop, 90);
        if (authState != "Ready")
        {
            LogErr("未能进入 Ready 状态: " + authState);
            LogErr("会话可能已失效, 请先跑一次带交互登录的脚本 (如 TdlChatList.cs) 完成登录");
            Environment.Exit(1);
            return;
        }

        var me = await loop.RpcAsync("{\"@type\":\"getMe\"}", 20);
        using (var md = JsonDocument.Parse(me))
        {
            if (md.RootElement.TryGetProperty("id", out var idEl))
                LogInfo($"已登录: {md.RootElement.GetProperty("first_name").GetString()} (@{TryStr(md.RootElement, "usernames")}) id={ReadLong(idEl)}");
        }

        // ── 2. 解析聊天 ──────────────────────────────────────────
        var (chatId, chatTitle) = await ResolveChatAsync(loop, chatArg!);
        if (chatId == 0)
        {
            LogErr("无法解析聊天: " + chatArg);
            Environment.Exit(1);
            return;
        }
        LogInfo($"目标聊天: {chatTitle} (ChatId={chatId})");

        // ── 3. 拉取全部历史消息 ──────────────────────────────────
        LogInfo("拉取历史消息 (getChatHistory)...");
        var messages = await FetchHistoryAsync(loop, chatId, maxMessages);
        LogInfo($"共拉取消息: {messages.Count} 条 (媒体 {messages.Count(m => m.Media != null)} 条)");

        if (messages.Count == 0)
        {
            LogErr("聊天内没有任何消息 (可能从未与该 bot 交互过)");
            Environment.Exit(1);
            return;
        }

        // ── 4. 按 /start 切段 ────────────────────────────────────
        var startTexts = messages.Where(m => m.IsOutgoing && m.Text.TrimStart().StartsWith("/start", StringComparison.OrdinalIgnoreCase)).ToList();

        if (startTexts.Count == 0)
        {
            LogWarn("未找到 /start 命令, 全部媒体归入 single 段");
            startTexts.Add(new MessageInfo { Id = 0, Date = DateTime.MinValue, Text = "/start (implicit)" });
        }
        LogInfo($"找到 /start 命令: {startTexts.Count} 条");

        var segments = BuildSegments(messages, startTexts, typeFilter);

        var totalFiles = segments.Sum(s => s.Items.Count);
        var totalBytes = segments.Sum(s => s.Items.Sum(i => i.Media!.Size));
        LogInfo($"切分结果: {segments.Count} 段 (空段 {segments.Count(s => s.Items.Count == 0)} 个), 待下载 {totalFiles} 个文件 / {FormatBytes(totalBytes)}");

        // ── 5. 预览 ──────────────────────────────────────────────
        PrintPreview(chatTitle, chatId, segments);

        if (dryRun)
        {
            AnsiConsole.MarkupLine("[yellow]Dry-run 模式, 未下载任何文件[/]");
            return;
        }

        if (totalFiles == 0)
        {
            LogWarn("没有可下载的文件, 退出");
            return;
        }

        // ── 6. 下载 ──────────────────────────────────────────────
        var outputRoot = outputArg ?? Path.Combine("data", "tdl", "by-start", chatId.ToString());
        Directory.CreateDirectory(outputRoot);
        LogInfo("输出目录: " + Path.GetFullPath(outputRoot));

        if (maxFiles > 0)
        {
            // 只保留前 N 个文件 (按段顺序)
            int remain = maxFiles;
            foreach (var s in segments)
            {
                if (remain <= 0) { s.Items.Clear(); continue; }
                if (s.Items.Count > remain) s.Items = s.Items.Take(remain).ToList();
                remain -= s.Items.Count;
            }
            totalFiles = segments.Sum(s => s.Items.Count);
            LogInfo("按 --max 截断后: " + totalFiles + " 个文件");
        }

        var stats = await DownloadAllAsync(loop, segments, outputRoot, concurrency, overwrite, priority, totalFiles);

        // ── 7. 元数据 ────────────────────────────────────────────
        if (!noMeta)
            await WriteMetaAsync(outputRoot, chatId, chatTitle, chatArg!, segments, stats);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold green]完成[/]").LeftJustified());
        AnsiConsole.MarkupLine("聊天:       [cyan]" + chatTitle.EscapeMarkup() + "[/] (" + chatId + ")");
        AnsiConsole.MarkupLine("会话段:     [green]" + segments.Count(s => s.Items.Count > 0) + "[/] 非空 / [grey]" +
                               segments.Count(s => s.Items.Count == 0) + "[/] 空");
        AnsiConsole.MarkupLine("下载成功:   [green]" + stats.Completed + "[/]");
        AnsiConsole.MarkupLine("已存在跳过: [yellow]" + stats.Skipped + "[/]");
        AnsiConsole.MarkupLine("失败:       [red]" + stats.Failed + "[/]");
        AnsiConsole.MarkupLine("总大小:     [yellow]" + FormatBytes(stats.Bytes) + "[/]");
        AnsiConsole.MarkupLine("耗时:       [grey]" + stats.Elapsed.ToString(@"hh\:mm\:ss") + "[/]");
        AnsiConsole.MarkupLine("输出目录:   [green]" + Path.GetFullPath(outputRoot) + "[/]");
        AnsiConsole.WriteLine();

        if (stats.Failed > 0) Environment.Exit(1);
    }
    finally
    {
        loop.Stop();
    }
}

// ─────────────────────────────────────────────────────────────
// TDLib 初始化 / 认证
// ─────────────────────────────────────────────────────────────

bool InitTdlib(EventLoop loop, bool noProxy, string? proxyArg)
{
    var apiId = Environment.GetEnvironmentVariable("tdl_api_id", EnvironmentVariableTarget.User);
    var apiHash = Environment.GetEnvironmentVariable("tdl_api_hash", EnvironmentVariableTarget.User);
    if (string.IsNullOrWhiteSpace(apiId) || string.IsNullOrWhiteSpace(apiHash))
    {
        LogErr("缺少 tdl_api_id 或 tdl_api_hash 环境变量 (User 级别)");
        Environment.Exit(2);
        return false;
    }

    var tdlRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tdl");
    Directory.CreateDirectory(Path.Combine(tdlRoot, "db"));
    Directory.CreateDirectory(Path.Combine(tdlRoot, "files"));
    var tdlPath = tdlRoot.Replace("\\", "/");

    loop.Send("{\"@type\":\"setTdlibParameters\",\"api_id\":\"" + apiId + "\",\"api_hash\":\"" + apiHash +
              "\",\"device_model\":\"PC\",\"system_version\":\"Windows\",\"system_language_code\":\"en\",\"application_version\":\"1.0.0\"" +
              ",\"database_directory\":\"" + tdlPath + "/db\",\"files_directory\":\"" + tdlPath + "/files\"" +
              ",\"use_file_database\":true,\"use_chat_info_database\":true,\"use_message_database\":" + (UseMessageDb() ? "true" : "false") + "}");

    if (!noProxy)
    {
        var (host, port) = GetProxy(proxyArg);
        LogInfo($"启用代理 {host}:{port}");
        loop.Send("{\"@type\":\"addProxy\",\"server\":\"" + host + "\",\"port\":" + port +
                  ",\"type\":{\"@type\":\"proxyTypeSocks5\",\"username\":\"\",\"password\":\"\"},\"enable\":true}");
    }
    else
    {
        LogInfo("直连模式 (未使用代理)");
    }

    return true;
}

(string Host, int Port) GetProxy(string? explicitProxy)
{
    var raw = explicitProxy;
    if (string.IsNullOrWhiteSpace(raw))
        raw = Environment.GetEnvironmentVariable("tdl_proxy", EnvironmentVariableTarget.User);
    if (string.IsNullOrWhiteSpace(raw))
        return ("127.0.0.1", 7897);

    var idx = raw.LastIndexOf(':');
    if (idx < 0 || !int.TryParse(raw[(idx + 1)..], out var port))
        return ("127.0.0.1", 7897);
    return (raw[..idx], port);
}

async Task<string> WaitForReadyAsync(EventLoop loop, int timeoutSec)
{
    var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
    while (DateTime.UtcNow < deadline)
    {
        var s = loop.GetAuthState();
        if (s == "Ready") return "Ready";
        if (s is "Phone" or "QR" or "Code" or "Password" or "Closed" or "Key") return s;
        await Task.Delay(200);
    }
    return "Timeout";
}

async Task<(long Id, string Title)> ResolveChatAsync(EventLoop loop, string input)
{
    input = input.Trim();

    // 1. 纯数字 → ChatId
    if (long.TryParse(input, out var directId))
    {
        var chat = await loop.RpcAsync("{\"@type\":\"getChat\",\"chat_id\":" + directId + "}", 20);
        using var cd0 = JsonDocument.Parse(chat);
        if (cd0.RootElement.GetProperty("@type").GetString() != "error")
            return (directId, TryStr(cd0.RootElement, "title") ?? "unknown");
    }

    // 2. 提取 username → searchPublicChat
    var username = ExtractUsername(input);
    if (!string.IsNullOrEmpty(username))
    {
        var resp = await loop.RpcAsync("{\"@type\":\"searchPublicChat\",\"username\":\"" + username + "\"}", 30);
        using var d = JsonDocument.Parse(resp);
        if (d.RootElement.GetProperty("@type").GetString() != "error")
        {
            var id = ReadLong(d.RootElement.GetProperty("id"));
            var title = TryStr(d.RootElement, "title") ?? username;
            return (id, title);
        }
        LogWarn($"searchPublicChat 失败: {resp}");
    }

    // 3. 消息链接 (t.me/xxx/123)
    var li = await loop.RpcAsync("{\"@type\":\"getMessageLinkInfo\",\"url\":\"" + input.Replace("\"", "") + "\"}", 30);
    using (var d2 = JsonDocument.Parse(li))
    {
        if (d2.RootElement.GetProperty("@type").GetString() != "error" &&
            d2.RootElement.TryGetProperty("message", out var msg) &&
            msg.ValueKind == JsonValueKind.Object)
        {
            var cid = ReadLong(msg.GetProperty("chat_id"));
            var chat = await loop.RpcAsync("{\"@type\":\"getChat\",\"chat_id\":" + cid + "}", 20);
            using var cd = JsonDocument.Parse(chat);
            return (cid, TryStr(cd.RootElement, "title") ?? input);
        }
    }

    return (0, "");
}

static string? ExtractUsername(string input)
{
    if (string.IsNullOrWhiteSpace(input)) return null;
    if (input.StartsWith("@")) return input[1..].Trim();

    // 裸用户名 (HTTP API 里常这么传): jijizhiijia_bot
    if (!input.Contains('/') && System.Text.RegularExpressions.Regex.IsMatch(input, @"^[A-Za-z][A-Za-z0-9_]{3,31}$"))
        return input.Trim();

    var m = System.Text.RegularExpressions.Regex.Match(input,
        @"(?:https?:\/\/)?(?:t\.me|telegram\.me)\/(?<name>[^\/\?#]+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    if (!m.Success) return null;
    var name = m.Groups["name"].Value;
    if (name.StartsWith("+") || name.Equals("joinchat", StringComparison.OrdinalIgnoreCase)) return null;
    return name;
}

// ─────────────────────────────────────────────────────────────
// 历史消息拉取 (服务端, newest → oldest)
// ─────────────────────────────────────────────────────────────

async Task<List<MessageInfo>> FetchHistoryAsync(EventLoop loop, long chatId, int maxMessages)
{
    var all = new List<MessageInfo>();
    long fromId = 0;
    int round = 0;

    while (true)
    {
        var req = "{\"@type\":\"getChatHistory\",\"chat_id\":" + chatId +
                  ",\"from_message_id\":" + fromId + ",\"offset\":0,\"limit\":100,\"only_local\":false}";
        var resp = await loop.RpcAsync(req, 60);

        using var doc = JsonDocument.Parse(resp);
        var rootEl = doc.RootElement;
        if (rootEl.GetProperty("@type").GetString() == "error")
        {
            LogErr("getChatHistory 失败: " + resp);
            break;
        }
        if (!rootEl.TryGetProperty("messages", out var arrEl)) break;

        var batch = arrEl.EnumerateArray().ToList();
        if (batch.Count == 0) break;

        foreach (var m in batch)
        {
            var mi = ParseMessage(m);
            if (mi != null) all.Add(mi);
        }
        round++;

        if (round % 10 == 0)
            AnsiConsole.MarkupLine($"  [grey]... 已拉取 {all.Count} 条[/]");

        if (maxMessages > 0 && all.Count >= maxMessages)
        {
            all = all.Take(maxMessages).ToList();
            break;
        }

        long lastId = batch[^1].TryGetProperty("id", out var lid) ? ReadLong(lid) : 0;
        if (lastId == 0 || lastId == fromId) break;
        fromId = lastId;

        await Task.Delay(120);
    }

    // 按时间正序 (同秒按 id)
    return all.OrderBy(m => m.Date).ThenBy(m => m.Id).ToList();
}

MessageInfo? ParseMessage(JsonElement m)
{
    if (!m.TryGetProperty("id", out var idEl)) return null;

    var mi = new MessageInfo
    {
        Id = ReadLong(idEl),
        Date = DateTimeOffset.FromUnixTimeSeconds(ReadLong(m.GetProperty("date"))).UtcDateTime,
        IsOutgoing = m.TryGetProperty("is_outgoing", out var io) && io.ValueKind == JsonValueKind.True,
    };

    // 发送者 (新旧字段兼容)
    if (m.TryGetProperty("sender_id", out var sid) && sid.ValueKind == JsonValueKind.Object)
        mi.SenderUserId = sid.TryGetProperty("user_id", out var uid) ? ReadLong(uid) : 0;
    else if (m.TryGetProperty("sender_user_id", out var suid))
        mi.SenderUserId = ReadLong(suid);

    if (!m.TryGetProperty("content", out var content)) return mi;

    var contentType = content.GetProperty("@type").GetString() ?? "";

    if (contentType == "messageText")
    {
        mi.Text = GetFormattedText(content, "text");
        return mi;
    }

    mi.Media = ParseMedia(content, contentType);
    if (mi.Media != null)
        mi.Text = GetFormattedText(content, "caption");

    return mi;
}

MediaInfo? ParseMedia(JsonElement content, string contentType)
{
    try
    {
        switch (contentType)
        {
            case "messagePhoto":
            {
                var photo = content.GetProperty("photo");
                if (!photo.TryGetProperty("sizes", out var sizes)) return null;
                JsonElement best = default;
                long bestArea = -1;
                foreach (var s in sizes.EnumerateArray())
                {
                    if (!s.TryGetProperty("photo", out _)) continue;
                    var area = (long)ReadInt(s, "width") * ReadInt(s, "height");
                    if (area > bestArea) { bestArea = area; best = s.Clone(); }
                }
                if (bestArea < 0) return null;
                var pf = best.GetProperty("photo");
                return new MediaInfo
                {
                    Type = "Photo",
                    FileId = ReadInt(pf, "id"),
                    Size = ReadLong(pf, "size"),
                    Mime = "image/jpeg",
                    Ext = ".jpg",
                    Width = ReadInt(best, "width"),
                    Height = ReadInt(best, "height"),
                };
            }
            case "messageVideo":
            {
                var video = content.GetProperty("video");
                var vf = video.GetProperty("video");
                var mime = TryStr(vf, "mime_type") ?? "video/mp4";
                return new MediaInfo
                {
                    Type = "Video",
                    FileId = ReadInt(vf, "id"),
                    Size = ReadLong(vf, "size"),
                    Mime = mime,
                    Ext = PickExt(TryStr(vf, "file_name"), mime, ".mp4"),
                    FileName = TryStr(vf, "file_name"),
                    Width = ReadInt(video, "width"),
                    Height = ReadInt(video, "height"),
                    Duration = ReadInt(video, "duration"),
                };
            }
            case "messageDocument":
            {
                var document = content.GetProperty("document");
                var df = document.GetProperty("document");
                var mime = TryStr(df, "mime_type") ?? "application/octet-stream";
                return new MediaInfo
                {
                    Type = "Document",
                    FileId = ReadInt(df, "id"),
                    Size = ReadLong(df, "size"),
                    Mime = mime,
                    Ext = PickExt(TryStr(df, "file_name"), mime, ".bin"),
                    FileName = TryStr(df, "file_name"),
                };
            }
            case "messageAnimation":
            {
                var ani = content.GetProperty("animation");
                var af = ani.GetProperty("animation");
                var mime = TryStr(af, "mime_type") ?? "video/mp4";
                return new MediaInfo
                {
                    Type = "Animation",
                    FileId = ReadInt(af, "id"),
                    Size = ReadLong(af, "size"),
                    Mime = mime,
                    Ext = PickExt(TryStr(af, "file_name"), mime, ".mp4"),
                    FileName = TryStr(af, "file_name"),
                    Width = ReadInt(ani, "width"),
                    Height = ReadInt(ani, "height"),
                    Duration = ReadInt(ani, "duration"),
                };
            }
            case "messageAudio":
            {
                var audio = content.GetProperty("audio");
                var af = audio.GetProperty("audio");
                var mime = TryStr(af, "mime_type") ?? "audio/mpeg";
                return new MediaInfo
                {
                    Type = "Audio",
                    FileId = ReadInt(af, "id"),
                    Size = ReadLong(af, "size"),
                    Mime = mime,
                    Ext = PickExt(TryStr(af, "file_name"), mime, ".mp3"),
                    FileName = TryStr(af, "file_name"),
                    Duration = ReadInt(audio, "duration"),
                };
            }
            case "messageVoiceNote":
            {
                var vn = content.GetProperty("voice_note");
                var vf = vn.GetProperty("voice_note");
                return new MediaInfo
                {
                    Type = "VoiceNote",
                    FileId = ReadInt(vf, "id"),
                    Size = ReadLong(vf, "size"),
                    Mime = "audio/ogg",
                    Ext = ".ogg",
                    Duration = ReadInt(vn, "duration"),
                };
            }
            case "messageVideoNote":
            {
                var vn = content.GetProperty("video_note");
                var vf = vn.GetProperty("video_note");
                return new MediaInfo
                {
                    Type = "VideoNote",
                    FileId = ReadInt(vf, "id"),
                    Size = ReadLong(vf, "size"),
                    Mime = "video/mp4",
                    Ext = ".mp4",
                    Duration = ReadInt(vn, "duration"),
                    Width = ReadInt(vn, "length"),
                    Height = ReadInt(vn, "length"),
                };
            }
            default:
                return null;
        }
    }
    catch
    {
        return null;
    }
}

string GetFormattedText(JsonElement content, string field)
{
    if (!content.TryGetProperty(field, out var ft)) return "";
    if (ft.ValueKind != JsonValueKind.Object) return "";
    return ft.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
}

// ─────────────────────────────────────────────────────────────
// 分段
// ─────────────────────────────────────────────────────────────

List<Segment> BuildSegments(List<MessageInfo> messages, List<MessageInfo> starts, HashSet<string>? typeFilter)
{
    var segs = new List<Segment>();

    for (int i = 0; i < starts.Count; i++)
    {
        var from = starts[i].Date;
        var to = i + 1 < starts.Count ? starts[i + 1].Date : DateTime.MaxValue;

        var items = messages
            .Where(m => m.Media != null &&
                        m.Date >= from && m.Date < to &&
                        (typeFilter == null || typeFilter.Contains(m.Media.Type)))
            .ToList();

        segs.Add(new Segment
        {
            Index = i + 1,
            StartDate = from,
            EndDate = to,
            StartMessageId = starts[i].Id,
            StartArgs = ExtractStartArgs(starts[i].Text),
            Items = items,
        });
    }

    // 第一个 /start 之前的媒体
    if (starts.Count > 0 && starts[0].Date > DateTime.MinValue)
    {
        var before = messages
            .Where(m => m.Media != null && m.Date < starts[0].Date &&
                        (typeFilter == null || typeFilter.Contains(m.Media.Type)))
            .ToList();
        if (before.Count > 0)
        {
            LogWarn($"第一个 /start 之前还有 {before.Count} 条媒体, 归为 pre_start 段");
            segs.Insert(0, new Segment
            {
                Index = 0,
                StartDate = DateTime.MinValue,
                EndDate = starts[0].Date,
                StartMessageId = 0,
                Label = "pre_start",
                Items = before,
            });
        }
    }

    for (int i = 0; i < segs.Count; i++) segs[i].Index = i;
    return segs;
}

static string ExtractStartArgs(string text)
{
    var t = text.Trim();
    if (t.StartsWith("/start", StringComparison.OrdinalIgnoreCase)) t = t[6..].Trim();
    // 去掉 /start@botname 形式
    if (t.StartsWith("@")) { var sp = t.IndexOf(' '); t = sp < 0 ? "" : t[(sp + 1)..].Trim(); }
    return t;
}

void PrintPreview(string chatTitle, long chatId, List<Segment> segments)
{
    var table = new Table().Border(TableBorder.Rounded)
        .Title($"[bold cyan]{chatTitle.EscapeMarkup()}[/] 按 /start 分组预览")
        .AddColumn(new TableColumn("[bold]#[/]").RightAligned())
        .AddColumn(new TableColumn("[bold]目录[/]"))
        .AddColumn(new TableColumn("[bold]起始时间[/]"))
        .AddColumn(new TableColumn("[bold]文件[/]").RightAligned())
        .AddColumn(new TableColumn("[bold]大小[/]").RightAligned())
        .AddColumn(new TableColumn("[bold]首条说明[/]"));

    int shown = 0;
    foreach (var s in segments)
    {
        if (s.Items.Count == 0) continue;
        var firstCaption = s.Items.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Text))?.Text ?? "";
        firstCaption = firstCaption.Replace("\n", " ").Replace("\r", " ");
        if (firstCaption.Length > 32) firstCaption = firstCaption[..32] + "…";
        if (s.StartArgs.Length > 0) firstCaption = $"[{s.StartArgs}] " + firstCaption;

        table.AddRow(
            s.Index.ToString(),
            DirName(s).EscapeMarkup(),
            s.StartDate == DateTime.MinValue ? "-" : s.StartDate.ToString("yyyy-MM-dd HH:mm"),
            s.Items.Count.ToString(),
            FormatBytes(s.Items.Sum(i => i.Media!.Size)),
            firstCaption.EscapeMarkup());
        shown++;
        if (shown >= 40) break;
    }

    AnsiConsole.Write(table);

    var nonEmpty = segments.Count(s => s.Items.Count > 0);
    if (nonEmpty > 40) AnsiConsole.MarkupLine($"[grey]... 省略其余 {nonEmpty - 40} 个非空段[/]");
    if (segments.Count(s => s.Items.Count == 0) > 0)
        AnsiConsole.MarkupLine($"[grey]另有 {segments.Count(s => s.Items.Count == 0)} 个空段 (该次 /start 后没有媒体)[/]");
}

static string DirName(Segment s)
{
    var label = s.Label ?? $"start_{s.StartDate:yyyyMMdd_HHmmss}";
    return $"{s.Index:D4}_{label}";
}

// ─────────────────────────────────────────────────────────────
// 下载
// ─────────────────────────────────────────────────────────────

List<DownloadJob> BuildJobs(List<Segment> segments, string outputRoot)
{
    var jobs = new List<DownloadJob>();
    foreach (var seg in segments)
    {
        var segDir = Path.Combine(outputRoot, DirName(seg));
        int seq = 0;
        foreach (var it in seg.Items)
        {
            seq++;
            var ext = string.IsNullOrEmpty(it.Media!.Ext) ? ".bin" : it.Media.Ext;
            var name = $"{seq:D3}_{it.Date.ToLocalTime():yyyyMMdd_HHmmss}_{it.Id}{ext}";
            jobs.Add(new DownloadJob(Path.Combine(segDir, name), it.Media, it.Id, it.Date));
        }
    }
    return jobs;
}

async Task<DownloadStats> DownloadAllAsync(
    EventLoop loop, List<Segment> segments, string outputRoot,
    int concurrency, bool overwrite, int priority, int totalFiles)
{
    var stats = new DownloadStats { Total = totalFiles };
    var sw = System.Diagnostics.Stopwatch.StartNew();

    var jobs = BuildJobs(segments, outputRoot);
    stats.PlannedBytes = jobs.Sum(j => j.Media.Size);

    await AnsiConsole.Progress()
        .AutoRefresh(false)
        .Columns(
            new TaskDescriptionColumn(),
            new ProgressBarColumn { CompletedStyle = new Style(foreground: Color.Green) },
            new PercentageColumn(),
            new RemainingTimeColumn(),
            new SpinnerColumn(Spinner.Known.Dots))
        .StartAsync(async ctx =>
        {
            var task = ctx.AddTask($"[green]下载 ({concurrency} 并发)[/]", maxValue: Math.Max(1, jobs.Count));
            using var reporterCts = new CancellationTokenSource();

            // 进度刷新协程 (由 reporterCts 结束, 不依赖 ctx.IsFinished 以免死锁)
            var reporter = Task.Run(async () =>
            {
                while (!reporterCts.IsCancellationRequested)
                {
                    var b = stats.Bytes;
                    task.Description = $"[green]下载[/] {FormatBytes(b)} / {FormatBytes(stats.PlannedBytes)}";
                    try { ctx.Refresh(); } catch { break; }
                    try { await Task.Delay(500, reporterCts.Token); } catch { break; }
                }
            });

            await RunJobsAsync(loop, jobs, concurrency, overwrite, priority, stats, () => task.Increment(1));

            reporterCts.Cancel();
            try { await reporter; } catch { }
            task.Value = task.MaxValue;
            ctx.Refresh();
        });

    sw.Stop();
    stats.Elapsed = sw.Elapsed;
    return stats;
}

/// <summary>
/// 下载核心: 按并发度跑完所有 job, 每完成一个回调一次 onEachDone。
/// CLI 与 HTTP 服务共用, 保证两条路径行为一致。
/// </summary>
async Task RunJobsAsync(
    EventLoop loop, List<DownloadJob> jobs, int concurrency, bool overwrite, int priority,
    DownloadStats stats, Action? onEachDone = null, CancellationToken ct = default)
{
    var gate = new object();
    using var sem = new SemaphoreSlim(concurrency, concurrency);
    var tasks = new List<Task>();
    long bytesAcc = 0;

    foreach (var job in jobs)
    {
        ct.ThrowIfCancellationRequested();
        await sem.WaitAsync(ct);
        tasks.Add(Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(job.TargetPath)!);
                var (status, bytes) = await DownloadOneAsync(loop, job.Media, job.TargetPath, overwrite, priority, job.MessageId, ct);
                lock (gate)
                {
                    switch (status)
                    {
                        case DownloadStatus.Completed: stats.Completed++; break;
                        case DownloadStatus.Skipped: stats.Skipped++; break;
                        case DownloadStatus.Failed: stats.Failed++; break;
                    }
                }
                if (status == DownloadStatus.Completed) Interlocked.Add(ref bytesAcc, bytes);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                lock (gate) stats.Failed++;
                LogErr($"[{job.MessageId}] 任务异常: {ex.Message}");
            }
            finally
            {
                stats.Bytes = Interlocked.Read(ref bytesAcc);
                sem.Release();
                onEachDone?.Invoke();
            }
        }, CancellationToken.None));
    }

    try
    {
        await Task.WhenAll(tasks);
    }
    catch (OperationCanceledException) { }

    stats.Bytes = Interlocked.Read(ref bytesAcc);
}

async Task<(DownloadStatus, long)> DownloadOneAsync(
    EventLoop loop, MediaInfo media, string targetPath, bool overwrite, int priority, long messageId,
    CancellationToken ct = default)
{
    if (!overwrite && File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
        return (DownloadStatus.Skipped, 0);

    ct.ThrowIfCancellationRequested();

    if (media.FileId <= 0)
    {
        LogErr($"[{messageId}] 无有效 file_id");
        return (DownloadStatus.Failed, 0);
    }

    // 发起下载 (带 429 重试)
    for (int attempt = 0; attempt < 5; attempt++)
    {
        var req = "{\"@type\":\"downloadFile\",\"file_id\":" + media.FileId +
                  ",\"priority\":" + priority + ",\"offset\":0,\"limit\":0,\"synchronous\":false}";
        var resp = await loop.RpcAsync(req, 30);
        using var d = JsonDocument.Parse(resp);
        if (d.RootElement.GetProperty("@type").GetString() != "error")
            break;

        var code = d.RootElement.TryGetProperty("code", out var c) ? ReadInt(c) : 0;
        if (code == 429)
        {
            var wait = d.RootElement.TryGetProperty("retry_after", out var ra) ? ReadInt(ra) : 10;
            LogWarn($"[{messageId}] 触发限流, {wait}s 后重试");
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait + 2, 120)), ct);
            continue;
        }
        if (code == 400 && resp.Contains("file_id", StringComparison.OrdinalIgnoreCase))
        {
            // file_id 失效 (文件被 Telegram 回收) — 无法恢复
            LogErr($"[{messageId}] file_id 失效: {resp}");
            return (DownloadStatus.Failed, 0);
        }
        await Task.Delay(2000, ct);
    }

    // 轮询直到本地完成
    var deadline = DateTime.UtcNow.AddMinutes(20);
    while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
    {
        var resp = await loop.RpcAsync("{\"@type\":\"getFile\",\"file_id\":" + media.FileId + "}", 20);
        try
        {
            using var d = JsonDocument.Parse(resp);
            if (d.RootElement.GetProperty("@type").GetString() == "error")
                return (DownloadStatus.Failed, 0);

            if (d.RootElement.TryGetProperty("local", out var local))
            {
                var done = local.TryGetProperty("is_downloading_completed", out var ic) && ic.ValueKind == JsonValueKind.True;
                var path = local.TryGetProperty("path", out var pe) && pe.ValueKind == JsonValueKind.String ? pe.GetString() : null;

                if (done && !string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    long size = new FileInfo(path).Length;
                    try
                    {
                        if (File.Exists(targetPath)) File.Delete(targetPath);
                        File.Move(path, targetPath);
                    }
                    catch
                    {
                        File.Copy(path, targetPath, true);
                    }
                    return (DownloadStatus.Completed, size);
                }

                var hasError = local.TryGetProperty("is_downloading_active", out var act) && act.ValueKind == JsonValueKind.False
                               && (!local.TryGetProperty("downloaded_size", out var ds) || ReadLong(ds) == 0);
                if (hasError && done == false && local.TryGetProperty("downloaded_prefix_size", out _))
                {
                    // 下载已中止且无进度
                    return (DownloadStatus.Failed, 0);
                }
            }
        }
        catch { }
        await Task.Delay(1200, ct);
    }

    ct.ThrowIfCancellationRequested();
    LogErr($"[{messageId}] 下载超时 (file_id={media.FileId})");
    return (DownloadStatus.Failed, 0);
}

// ─────────────────────────────────────────────────────────────
// 元数据
// ─────────────────────────────────────────────────────────────

async Task WriteMetaAsync(string outputRoot, long chatId, string chatTitle, string chatArg,
    List<Segment> segments, DownloadStats stats)
{
    var opts = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    foreach (var seg in segments)
    {
        if (seg.Items.Count == 0) continue;
        var segDir = Path.Combine(outputRoot, DirName(seg));
        if (!Directory.Exists(segDir)) continue;

        var meta = new
        {
            seg.Index,
            Label = seg.Label ?? "start",
            StartMessageId = seg.StartMessageId,
            StartArgs = seg.StartArgs,
            StartDate = seg.StartDate == DateTime.MinValue ? (DateTime?)null : seg.StartDate,
            EndDate = seg.EndDate == DateTime.MaxValue ? (DateTime?)null : seg.EndDate,
            FileCount = seg.Items.Count,
            Photos = seg.Items.Count(i => i.Media!.Type == "Photo"),
            Videos = seg.Items.Count(i => i.Media!.Type == "Video"),
            TotalBytes = seg.Items.Sum(i => i.Media!.Size),
            Files = seg.Items.Select((i, n) => new
            {
                Seq = n + 1,
                MessageId = i.Id,
                i.Date,
                Type = i.Media!.Type,
                Size = i.Media.Size,
                MimeType = i.Media.Mime,
                FileName = i.Media.FileName,
                i.Text,
            }),
        };
        await File.WriteAllTextAsync(Path.Combine(segDir, "_meta.json"), JsonSerializer.Serialize(meta, opts));
    }

    var summary = new
    {
        ChatId = chatId,
        ChatTitle = chatTitle,
        Source = chatArg,
        GeneratedAt = DateTimeOffset.Now,
        TotalSegments = segments.Count,
        NonEmptySegments = segments.Count(s => s.Items.Count > 0),
        EmptySegments = segments.Count(s => s.Items.Count == 0),
        TotalFiles = segments.Sum(s => s.Items.Count),
        PlannedBytes = segments.Sum(s => s.Items.Sum(i => i.Media!.Size)),
        stats.Completed,
        stats.Skipped,
        stats.Failed,
        DownloadedBytes = stats.Bytes,
        ElapsedSec = Math.Round(stats.Elapsed.TotalSeconds, 1),
    };
    await File.WriteAllTextAsync(Path.Combine(outputRoot, "_summary.json"), JsonSerializer.Serialize(summary, opts));
}

// ─────────────────────────────────────────────────────────────
// 运行时 HTTP API — 常驻进程, 多客户端共享同一个 TDLib 会话
//
// 目的: TDLib 的本地数据库 (~/.tdl/db) 同一时刻只允许一个进程持有,
//       起多个 CLI 进程并发下载会互相锁库 / 抢占会话。这里把 TDLib
//       收进一个常驻进程, 其它进程 (脚本 / curl / 另一个 Agent) 只发 HTTP 请求。
// ─────────────────────────────────────────────────────────────

async Task ServeAsync(EventLoop loop, int port, string? defaultOutput,
    int defConcurrency, bool defOverwrite, int defPriority, bool noMeta, int maxJobs)
{
    var jobs = new ConcurrentDictionary<string, ServerJob>(StringComparer.OrdinalIgnoreCase);
    // 多任务并行: 允许 maxJobs 个下载任务同时跑 (每个任务内部还有自己的 concurrency),
    // TDLib 只有一个客户端, 但 RpcAsync 是按 @extra 分发响应的, 多线程并发请求是安全的。
    var queue = new SemaphoreSlim(maxJobs, maxJobs);
    var startedAt = DateTime.Now;
    var jsonOpts = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    var listener = new TcpListener(IPAddress.Loopback, port);
    try { listener.Start(); }
    catch (Exception ex)
    {
        LogErr($"HTTP API 端口 {port} 监听失败: {ex.Message}");
        return;
    }

    LogInfo($"HTTP API 已启动: http://127.0.0.1:{port}/   (Ctrl+C 停止)");
    LogInfo($"当前登录状态: {loop.GetAuthState()}" + (loop.GetAuthState() == "Ready" ? " (可直接使用)" : " (请先调用 /auth/* 完成登录)"));
    LogInfo($"并发策略: 最多 {maxJobs} 个任务并行, 每个任务默认 {defConcurrency} 路下载");
    LogInfo("  GET  /health        健康检查 (含登录状态)");
    LogInfo("  GET  /auth/state    登录状态 / QR 链接");
    LogInfo("  POST /auth/phone    {\"phone\":\"+8613800138000\"} 发起手机号登录");
    LogInfo("  POST /auth/code     {\"code\":\"12345\"} 提交验证码");
    LogInfo("  POST /auth/password {\"password\":\"...\"} 提交两步验证密码");
    LogInfo("  POST /auth/qr       申请扫码登录, 返回 tg://login 链接");
    LogInfo("  POST /auth/bot      {\"token\":\"...\"} 用 Bot Token 登录");
    LogInfo("  GET  /me            当前登录账号");
    LogInfo("  GET  /chats?q=@bot  解析聊天 → chatId/title");
    LogInfo("  POST /preview       拉历史 + 按 /start 切段预览 (不下载)");
    LogInfo("  POST /jobs          创建下载任务 (异步执行, 返回 jobId)");
    LogInfo("  GET  /jobs          任务列表");
    LogInfo("  GET  /jobs/{id}     任务详情 (进度随下载刷新)");
    LogInfo("  POST /jobs/{id}/cancel  取消任务");
    LogInfo("  GET  /jobs/{id}/files   已落盘文件清单");

    var shutdown = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.TrySetResult(); };

    var acceptTask = Task.Run(async () =>
    {
        while (!shutdown.Task.IsCompleted)
        {
            TcpClient conn;
            try { conn = await listener.AcceptTcpClientAsync(); }
            catch { break; }
            _ = Task.Run(async () =>
            {
                using (conn)
                {
                    try { await HandleAsync(conn.GetStream()); }
                    catch (Exception ex) { LogWarn("HTTP 连接处理异常: " + ex.Message); }
                }
            });
        }
    });

    await shutdown.Task;
    listener.Stop();
    foreach (var j in jobs.Values)
        if (j.Status is "queued" or "running") j.Cts?.Cancel();
    LogInfo("HTTP API 已停止");

    // ── 路由 ────────────────────────────────────────────────
    async Task HandleAsync(NetworkStream stream)
    {
        var parsed = await ReadRequestAsync(stream);
        if (parsed == null) return;
        var (method, path, query, body) = parsed.Value;

        var segs = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var route = segs.Length == 0 ? "" : segs[0].ToLowerInvariant();

        try
        {
            switch (route)
            {
                case "":
                    await WriteJsonAsync(stream, 200, new
                    {
                        name = "TdlBotMediaByStart HTTP API",
                        endpoints = new[]
                        {
                            "GET /health", "GET /auth/state", "POST /auth/phone", "POST /auth/code",
                            "POST /auth/password", "POST /auth/qr", "POST /auth/bot",
                            "GET /me", "GET /chats?q=", "POST /preview",
                            "POST /jobs", "GET /jobs", "GET /jobs/{id}", "POST /jobs/{id}/cancel",
                            "GET /jobs/{id}/files",
                        },
                        example = "POST /jobs {\"chat\":\"@haoren004_bot\",\"types\":\"Photo,Video\"}",
                    });
                    return;

                case "health":
                    await WriteJsonAsync(stream, 200, new
                    {
                        ok = true,
                        ready = loop.GetAuthState() == "Ready",
                        authState = loop.GetAuthState(),
                        pid = Environment.ProcessId,
                        uptimeSec = (int)(DateTime.Now - startedAt).TotalSeconds,
                        jobs = jobs.Count,
                        running = jobs.Values.Count(j => j.Status == "running"),
                    });
                    return;

                case "auth":
                {
                    var action = segs.Length >= 2 ? segs[1].ToLowerInvariant() : "state";

                    if (action == "state")
                    {
                        await WriteJsonAsync(stream, 200, new
                        {
                            authState = loop.GetAuthState(),
                            ready = loop.GetAuthState() == "Ready",
                            qrLink = loop.QrLink,
                            hint = AuthHint(loop.GetAuthState()),
                        });
                        return;
                    }

                    if (method != "POST")
                    {
                        await WriteJsonAsync(stream, 405, new { error = "登录操作请用 POST" });
                        return;
                    }

                    using var doc = ParseBody(body);
                    switch (action)
                    {
                        case "phone":
                        {
                            var phone = Str(doc.RootElement, "phone") ?? Q(query, "phone");
                            if (string.IsNullOrWhiteSpace(phone))
                            {
                                await WriteJsonAsync(stream, 400, new { error = "缺少 phone, 例: {\"phone\":\"+8613800138000\"}" });
                                return;
                            }
                            await SendAuthAsync(stream, "{\"@type\":\"setAuthenticationPhoneNumber\",\"phone_number\":" +
                                                         JsonSerializer.Serialize(phone) + "}");
                            return;
                        }
                        case "code":
                        {
                            var code = Str(doc.RootElement, "code") ?? Q(query, "code");
                            if (string.IsNullOrWhiteSpace(code))
                            {
                                await WriteJsonAsync(stream, 400, new { error = "缺少 code" });
                                return;
                            }
                            await SendAuthAsync(stream, "{\"@type\":\"checkAuthenticationCode\",\"code\":" +
                                                         JsonSerializer.Serialize(code) + "}");
                            return;
                        }
                        case "password":
                        {
                            var pwd = Str(doc.RootElement, "password") ?? Q(query, "password");
                            if (string.IsNullOrWhiteSpace(pwd))
                            {
                                await WriteJsonAsync(stream, 400, new { error = "缺少 password" });
                                return;
                            }
                            await SendAuthAsync(stream, "{\"@type\":\"checkAuthenticationPassword\",\"password\":" +
                                                         JsonSerializer.Serialize(pwd) + "}");
                            return;
                        }
                        case "bot":
                        {
                            var token = Str(doc.RootElement, "token") ?? Q(query, "token");
                            if (string.IsNullOrWhiteSpace(token))
                            {
                                await WriteJsonAsync(stream, 400, new { error = "缺少 token" });
                                return;
                            }
                            await SendAuthAsync(stream, "{\"@type\":\"checkAuthenticationBotToken\",\"token\":" +
                                                         JsonSerializer.Serialize(token) + "}");
                            return;
                        }
                        case "qr":
                        {
                            var resp = await loop.RpcAsync("{\"@type\":\"requestQrCodeAuthentication\"}", 30);
                            using (var d = JsonDocument.Parse(resp))
                            {
                                if (d.RootElement.GetProperty("@type").GetString() == "error")
                                {
                                    await WriteJsonAsync(stream, 400, new { error = TryStr(d.RootElement, "message") ?? resp });
                                    return;
                                }
                            }
                            for (int i = 0; i < 24 && string.IsNullOrEmpty(loop.QrLink); i++)
                                await Task.Delay(250);

                            await WriteJsonAsync(stream, 200, new
                            {
                                ok = true,
                                authState = loop.GetAuthState(),
                                qrLink = loop.QrLink,
                                tip = "在手机 Telegram: 设置 → 设备 → 扫描二维码, 或直接打开上面的 tg:// 链接",
                            });
                            return;
                        }
                        default:
                            await WriteJsonAsync(stream, 404, new { error = "未知登录操作: " + action });
                            return;
                    }
                }

                case "me":
                {
                    if (!await EnsureReadyAsync(stream)) return;
                    var meJson = await loop.RpcAsync("{\"@type\":\"getMe\"}", 20);
                    using var doc = JsonDocument.Parse(meJson);
                    var root = doc.RootElement;
                    if (root.GetProperty("@type").GetString() == "error")
                    {
                        await WriteJsonAsync(stream, 502, new { error = meJson });
                        return;
                    }
                    await WriteJsonAsync(stream, 200, new
                    {
                        id = ReadLong(root, "id"),
                        firstName = TryStr(root, "first_name"),
                        lastName = TryStr(root, "last_name"),
                        username = TryStr(root, "usernames"),
                        phone = TryStr(root, "phone_number"),
                    });
                    return;
                }

                case "chats":
                {
                    if (!await EnsureReadyAsync(stream)) return;
                    var q = Q(query, "q");
                    if (string.IsNullOrWhiteSpace(q))
                    {
                        await WriteJsonAsync(stream, 400, new { error = "缺少 q 参数, 例如 /chats?q=@haoren004_bot" });
                        return;
                    }
                    var (cid, title) = await ResolveChatAsync(loop, q);
                    if (cid == 0)
                    {
                        await WriteJsonAsync(stream, 404, new { error = "无法解析聊天: " + q });
                        return;
                    }
                    await WriteJsonAsync(stream, 200, new { chatId = cid, title });
                    return;
                }

                case "preview":
                {
                    if (!await EnsureReadyAsync(stream)) return;
                    using var doc = ParseBody(body);
                    var chat = Str(doc.RootElement, "chat") ?? Q(query, "chat");
                    if (string.IsNullOrWhiteSpace(chat))
                    {
                        await WriteJsonAsync(stream, 400, new { error = "缺少 chat" });
                        return;
                    }
                    var types = ParseTypes(Str(doc.RootElement, "types"));
                    var maxMsgs = Math.Max(0, Int(doc.RootElement, "maxMessages") ?? 0);

                    var (cid, title) = await ResolveChatAsync(loop, chat);
                    if (cid == 0)
                    {
                        await WriteJsonAsync(stream, 404, new { error = "无法解析聊天: " + chat });
                        return;
                    }

                    var messages = await FetchHistoryAsync(loop, cid, maxMsgs);
                    var starts = StartMessages(messages);
                    var segments = BuildSegments(messages, starts, types);

                    await WriteJsonAsync(stream, 200, new
                    {
                        chatId = cid,
                        chatTitle = title,
                        messages = messages.Count,
                        mediaMessages = messages.Count(m => m.Media != null),
                        startCommands = starts.Count,
                        segments = segments.Count,
                        nonEmptySegments = segments.Count(s => s.Items.Count > 0),
                        totalFiles = segments.Sum(s => s.Items.Count),
                        totalBytes = segments.Sum(s => s.Items.Sum(i => i.Media!.Size)),
                        items = segments.Where(s => s.Items.Count > 0).Select(s => new
                        {
                            s.Index,
                            dir = DirName(s),
                            startDate = s.StartDate == DateTime.MinValue ? (DateTime?)null : s.StartDate,
                            files = s.Items.Count,
                            bytes = s.Items.Sum(i => i.Media!.Size),
                            photos = s.Items.Count(i => i.Media!.Type == "Photo"),
                            videos = s.Items.Count(i => i.Media!.Type == "Video"),
                        }),
                    });
                    return;
                }

                case "jobs":
                {
                    // GET /jobs            列表
                    // GET /jobs/{id}       详情
                    // GET /jobs/{id}/files 文件清单
                    // POST /jobs           创建
                    // POST /jobs/{id}/cancel 取消
                    if (segs.Length == 1)
                    {
                        if (method == "GET")
                        {
                            await WriteJsonAsync(stream, 200, new
                            {
                                total = jobs.Count,
                                jobs = jobs.Values.OrderByDescending(j => j.CreatedAt).Select(Snapshot).ToList(),
                            });
                            return;
                        }
                        if (method != "POST")
                        {
                            await WriteJsonAsync(stream, 405, new { error = "只支持 GET / POST" });
                            return;
                        }

                        if (!await EnsureReadyAsync(stream)) return;

                        using var doc = ParseBody(body);
                        var root = doc.RootElement;
                        var chat = Str(root, "chat") ?? Q(query, "chat");
                        if (string.IsNullOrWhiteSpace(chat))
                        {
                            await WriteJsonAsync(stream, 400, new { error = "缺少 chat" });
                            return;
                        }

                        var job = new ServerJob
                        {
                            Id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4],
                            Chat = chat,
                            Output = Str(root, "output") ?? defaultOutput ?? "",
                            Types = Str(root, "types"),
                            Status = "queued",
                            CreatedAt = DateTime.Now,
                            DryRun = Bool(root, "dryRun") ?? false,
                        };
                        var jobConcurrency = Math.Clamp(Int(root, "concurrency") ?? defConcurrency, 1, 16);
                        var jobOverwrite = Bool(root, "overwrite") ?? defOverwrite;
                        var jobPriority = Math.Clamp(Int(root, "priority") ?? defPriority, 1, 32);
                        var jobMax = Math.Max(0, Int(root, "max") ?? 0);
                        var jobMaxMsgs = Math.Max(0, Int(root, "maxMessages") ?? 0);
                        var jobSegFrom = Math.Max(0, Int(root, "segmentFrom") ?? 0);
                        var jobSegTo = Math.Max(0, Int(root, "segmentTo") ?? 0);   // 0 = 到末尾

                        jobs[job.Id] = job;
                        _ = Task.Run(() => RunJobAsync(job, jobConcurrency, jobOverwrite, jobPriority, jobMax, jobMaxMsgs, jobSegFrom, jobSegTo));

                        await WriteJsonAsync(stream, 202, new
                        {
                            jobId = job.Id,
                            status = job.Status,
                            poll = $"/jobs/{job.Id}",
                        });
                        return;
                    }

                    if (!jobs.TryGetValue(segs[1], out var target))
                    {
                        await WriteJsonAsync(stream, 404, new { error = "任务不存在: " + segs[1] });
                        return;
                    }

                    if (segs.Length >= 3 && segs[2].Equals("files", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteJsonAsync(stream, 200, ListFiles(target));
                        return;
                    }

                    if (segs.Length >= 3 && segs[2].Equals("cancel", StringComparison.OrdinalIgnoreCase))
                    {
                        if (method != "POST")
                        {
                            await WriteJsonAsync(stream, 405, new { error = "请用 POST" });
                            return;
                        }
                        if (target.Status is "queued" or "running")
                        {
                            target.Cts?.Cancel();
                            target.Status = "cancelled";
                            Persist(target);
                        }
                        await WriteJsonAsync(stream, 200, new { jobId = target.Id, status = target.Status });
                        return;
                    }

                    await WriteJsonAsync(stream, 200, Snapshot(target));
                    return;
                }

                default:
                    await WriteJsonAsync(stream, 404, new { error = "未知路由: " + path });
                    return;
            }
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(stream, 500, new { error = ex.Message });
        }
    }

    // ── 任务执行 ────────────────────────────────────────────
    async Task RunJobAsync(ServerJob job, int concurrency, bool overwrite, int priority, int maxFiles, int maxMessages,
        int segmentFrom, int segmentTo)
    {
        await queue.WaitAsync();
        job.Cts = new CancellationTokenSource();
        var ct = job.Cts.Token;
        try
        {
            job.Status = "running";
            job.StartedAt = DateTime.Now;
            Persist(job);

            var (cid, title) = await ResolveChatAsync(loop, job.Chat);
            if (cid == 0) throw new InvalidOperationException("无法解析聊天: " + job.Chat);
            job.ChatId = cid;
            job.ChatTitle = title;
            if (string.IsNullOrWhiteSpace(job.Output))
                job.Output = Path.Combine("data", "tdl", "by-start", cid.ToString());
            job.Output = Path.GetFullPath(job.Output);
            Directory.CreateDirectory(job.Output);
            Persist(job);

            var messages = await FetchHistoryAsync(loop, cid, maxMessages);
            if (messages.Count == 0) throw new InvalidOperationException("聊天内没有任何消息");

            var segments = BuildSegments(messages, StartMessages(messages), ParseTypes(job.Types));

            // 段区间切片: 同一聊天拆成多个任务并行下载 (segmentTo=0 表示到末尾)
            if (segmentFrom > 0 || segmentTo > 0)
            {
                var keep = segments.Where(s => s.Index >= segmentFrom && (segmentTo == 0 || s.Index <= segmentTo)).ToList();
                LogInfo($"[job {job.Id}] 段切片 {segmentFrom}~{(segmentTo == 0 ? "end" : segmentTo.ToString())}: " +
                        $"{keep.Count}/{segments.Count} 段");
                segments = keep;
            }

            if (maxFiles > 0)
            {
                int remain = maxFiles;
                foreach (var s in segments)
                {
                    if (remain <= 0) { s.Items.Clear(); continue; }
                    if (s.Items.Count > remain) s.Items = s.Items.Take(remain).ToList();
                    remain -= s.Items.Count;
                }
            }

            job.Segments = segments.Count;
            job.NonEmptySegments = segments.Count(s => s.Items.Count > 0);
            job.TotalFiles = segments.Sum(s => s.Items.Count);
            job.PlannedBytes = segments.Sum(s => s.Items.Sum(i => i.Media!.Size));
            job.SegmentList = segments.Where(s => s.Items.Count > 0).Select(s => new JobSegmentInfo
            {
                Index = s.Index,
                Dir = DirName(s),
                StartDate = s.StartDate == DateTime.MinValue ? null : s.StartDate,
                Files = s.Items.Count,
                Bytes = s.Items.Sum(i => i.Media!.Size),
            }).ToList();
            Persist(job);

            if (job.DryRun || job.TotalFiles == 0)
            {
                job.Status = "done";
                return;
            }

            var stats = new DownloadStats { Total = job.TotalFiles, PlannedBytes = job.PlannedBytes };
            var jobList = BuildJobs(segments, job.Output);

            using var repCts = new CancellationTokenSource();
            var reporter = Task.Run(async () =>
            {
                while (!repCts.IsCancellationRequested)
                {
                    job.Completed = stats.Completed;
                    job.Skipped = stats.Skipped;
                    job.Failed = stats.Failed;
                    job.Bytes = stats.Bytes;
                    Persist(job);
                    try { await Task.Delay(2000, repCts.Token); } catch { break; }
                }
            });

            await RunJobsAsync(loop, jobList, concurrency, overwrite, priority, stats, null, ct);

            repCts.Cancel();
            try { await reporter; } catch { }

            job.Completed = stats.Completed;
            job.Skipped = stats.Skipped;
            job.Failed = stats.Failed;
            job.Bytes = stats.Bytes;

            if (!noMeta)
                await WriteMetaAsync(job.Output, cid, title, job.Chat, segments, stats);

            job.Status = stats.Failed > 0 ? "failed" : "done";
            if (stats.Failed > 0) job.Error = $"{stats.Failed} 个文件下载失败";
        }
        catch (OperationCanceledException)
        {
            job.Status = "cancelled";
        }
        catch (Exception ex)
        {
            job.Status = "failed";
            job.Error = ex.Message;
            LogErr($"[job {job.Id}] {ex.Message}");
        }
        finally
        {
            job.FinishedAt = DateTime.Now;
            Persist(job);
            job.Cts?.Dispose();
            job.Cts = null;
            queue.Release();
        }
    }

    // ── 响应快照 / 持久化 / 文件清单 ─────────────────────────
    object Snapshot(ServerJob j) => new
    {
        j.Id, j.Chat, j.ChatId, j.ChatTitle, j.Output, j.Status, j.DryRun,
        j.CreatedAt, j.StartedAt, j.FinishedAt,
        j.TotalFiles, j.Completed, j.Skipped, j.Failed, j.Bytes, j.PlannedBytes,
        j.Segments, j.NonEmptySegments, j.Error, j.SegmentList,
    };

    void Persist(ServerJob j)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(j.Output)) return;
            Directory.CreateDirectory(j.Output);
            var path = Path.Combine(j.Output, "_job.json");
            File.WriteAllText(path, JsonSerializer.Serialize(Snapshot(j), jsonOpts));
        }
        catch { /* 落盘失败不影响任务 */ }
    }

    object ListFiles(ServerJob j)
    {
        if (string.IsNullOrWhiteSpace(j.Output) || !Directory.Exists(j.Output))
            return new { jobId = j.Id, total = 0, files = Array.Empty<object>() };

        var dir = Path.GetFullPath(j.Output);
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(p => !Path.GetFileName(p).StartsWith("_", StringComparison.Ordinal))
            .Select(p => new FileInfo(p))
            .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .Take(20000)
            .Select(f => new
            {
                rel = Path.GetRelativePath(dir, f.FullName).Replace('\\', '/'),
                size = f.Length,
                mtime = f.LastWriteTime,
            })
            .ToList();

        return new
        {
            jobId = j.Id,
            root = dir,
            total = files.Count,
            bytes = files.Sum(f => f.size),
            files,
        };
    }

    // ── 登录辅助 ────────────────────────────────────────────
    async Task SendAuthAsync(NetworkStream stream, string requestJson)
    {
        var resp = await loop.RpcAsync(requestJson, 40);
        using var d = JsonDocument.Parse(resp);
        if (d.RootElement.GetProperty("@type").GetString() == "error")
        {
            var code = ReadInt(d.RootElement, "code");
            var msg = TryStr(d.RootElement, "message") ?? resp;
            LogWarn($"登录请求失败: {msg}");
            await WriteJsonAsync(stream, 400, new
            {
                error = msg,
                code,
                authState = loop.GetAuthState(),
                hint = code == 420 ? "触发限流, 请等待后重试" : AuthHint(loop.GetAuthState()),
            });
            return;
        }

        await WriteJsonAsync(stream, 200, new
        {
            ok = true,
            authState = loop.GetAuthState(),
            ready = loop.GetAuthState() == "Ready",
            hint = AuthHint(loop.GetAuthState()),
        });
    }

    async Task<bool> EnsureReadyAsync(NetworkStream stream)
    {
        if (loop.GetAuthState() == "Ready") return true;
        await WriteJsonAsync(stream, 409, new
        {
            error = "尚未登录",
            authState = loop.GetAuthState(),
            hint = AuthHint(loop.GetAuthState()),
        });
        return false;
    }

    static string AuthHint(string state) => state switch
    {
        "Ready" => "已登录",
        "Init" or "Key" => "TDLib 正在启动, 稍后重试",
        "Phone" => "POST /auth/phone {\"phone\":\"+86...\"} 发起登录, 或 POST /auth/qr 扫码",
        "Code" => "POST /auth/code {\"code\":\"...\"} 提交验证码",
        "Password" => "POST /auth/password {\"password\":\"...\"} 提交两步验证密码",
        "QR" => "GET /auth/state 查看 qrLink, 或 POST /auth/qr 重新申请",
        "Email" => "该账号需要邮箱验证, 请改用 TdlChatList.cs 交互式登录",
        "Closed" => "TDLib 已关闭会话, 需重启服务",
        _ => "登录状态: " + state,
    };

    // ── HTTP 编解码 ────────────────────────────────────────
    async Task WriteJsonAsync(NetworkStream ns, int status, object payload)
    {
        var json = JsonSerializer.Serialize(payload, jsonOpts);
        var bodyBytes = Encoding.UTF8.GetBytes(json);
        var head = $"HTTP/1.1 {status} {StatusText(status)}\r\n" +
                   "Content-Type: application/json; charset=utf-8\r\n" +
                   $"Content-Length: {bodyBytes.Length}\r\n" +
                   "Cache-Control: no-store\r\n" +
                   "Connection: close\r\n\r\n";
        await ns.WriteAsync(Encoding.ASCII.GetBytes(head));
        await ns.WriteAsync(bodyBytes);
        await ns.FlushAsync();
    }

    static async Task<(string Method, string Path, string Query, string Body)?> ReadRequestAsync(NetworkStream ns)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        int headerEnd = -1;

        while (headerEnd < 0)
        {
            int n = await ns.ReadAsync(buffer, 0, buffer.Length);
            if (n <= 0) return null;
            ms.Write(buffer, 0, n);
            headerEnd = IndexOfHeaderEnd(ms.GetBuffer(), (int)ms.Length);
            if (ms.Length > 1024 * 1024) return null;
        }

        var all = ms.ToArray();
        var headerText = Encoding.UTF8.GetString(all, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;

        var method = first[0].ToUpperInvariant();
        var target = first[1];
        var qIdx = target.IndexOf('?');
        var path = qIdx >= 0 ? target[..qIdx] : target;
        var query = qIdx >= 0 ? target[(qIdx + 1)..] : "";

        int contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var ci = line.IndexOf(':');
            if (ci <= 0) continue;
            if (line[..ci].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[(ci + 1)..].Trim(), out var cl))
                contentLength = Math.Max(0, cl);
        }

        using var bodyMs = new MemoryStream();
        var bodyStart = headerEnd + 4;
        if (all.Length > bodyStart) bodyMs.Write(all, bodyStart, all.Length - bodyStart);
        while (bodyMs.Length < contentLength)
        {
            int n = await ns.ReadAsync(buffer, 0, buffer.Length);
            if (n <= 0) break;
            bodyMs.Write(buffer, 0, n);
        }

        var bodyLen = contentLength > 0 ? (int)Math.Min(bodyMs.Length, contentLength) : (int)bodyMs.Length;
        var body = bodyLen > 0 ? Encoding.UTF8.GetString(bodyMs.ToArray(), 0, bodyLen) : "";
        return (method, Uri.UnescapeDataString(path), query, body);
    }

    static int IndexOfHeaderEnd(byte[] buf, int len)
    {
        for (int i = 3; i < len; i++)
        {
            if (buf[i - 3] == 13 && buf[i - 2] == 10 && buf[i - 1] == 13 && buf[i] == 10)
                return i - 3;
        }
        return -1;
    }

    static string Q(string query, string key)
    {
        foreach (var kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = kv.IndexOf('=');
            var k = i < 0 ? kv : kv[..i];
            if (!k.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return i < 0 ? "" : Uri.UnescapeDataString(kv[(i + 1)..].Replace('+', ' '));
        }
        return "";
    }

    static JsonDocument ParseBody(string body) =>
        JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);

    static string? Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    static int? Int(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var m)) return m;
        return null;
    }

    static bool? Bool(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : null,
            _ => null,
        };
    }

    static string StatusText(int code) => code switch
    {
        200 => "OK",
        202 => "Accepted",
        400 => "Bad Request",
        404 => "Not Found",
        405 => "Method Not Allowed",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        _ => "OK",
    };
}

/// <summary>
/// 把消息流里的「我方 /start」抽出来, 没有则造一个隐式起点。
/// </summary>
List<MessageInfo> StartMessages(List<MessageInfo> messages)
{
    var starts = messages
        .Where(m => m.IsOutgoing && m.Text.TrimStart().StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        .ToList();
    if (starts.Count == 0)
        starts.Add(new MessageInfo { Id = 0, Date = DateTime.MinValue, Text = "/start (implicit)" });
    return starts;
}

// ─────────────────────────────────────────────────────────────
// 工具函数
// ─────────────────────────────────────────────────────────────

void PrintUsage(RootCommand cmd)
{
    Console.WriteLine("下载 bot / 聊天的全部媒体, 并按 /start 命令分组到独立目录");
    Console.WriteLine();
    Console.WriteLine("用法:");
    Console.WriteLine("  dotnet run src/tdl/TdlBotMediaByStart.cs -- --chat <@username|t.me链接|ChatId> [选项]");
    Console.WriteLine("  dotnet run src/tdl/TdlBotMediaByStart.cs -- --serve [--port 8788]   # 常驻 HTTP API");
    Console.WriteLine();
    Console.WriteLine("示例:");
    Console.WriteLine("  dotnet run src/tdl/TdlBotMediaByStart.cs -- --chat @haoren004_bot --dry-run");
    Console.WriteLine("  dotnet run src/tdl/TdlBotMediaByStart.cs -- --chat https://t.me/haoren004_bot --concurrency 6");
    Console.WriteLine("  dotnet run src/tdl/TdlBotMediaByStart.cs -- --serve --port 8788");
    Console.WriteLine("  curl -X POST http://127.0.0.1:8788/jobs -d \"{\\\"chat\\\":\\\"@haoren004_bot\\\"}\"");
    Console.WriteLine();
    Console.WriteLine("选项:");
    foreach (var o in cmd.Options)
        Console.WriteLine($"  {string.Join(", ", o.Aliases),-16} {o.Description ?? ""}");
}

HashSet<string>? ParseTypes(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return null;
    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var t in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        // 兼容 messagePhoto 写法
        var name = t.StartsWith("message", StringComparison.OrdinalIgnoreCase) ? t[7..] : t;
        set.Add(name);
    }
    return set;
}

static string PickExt(string? fileName, string mime, string fallback)
{
    if (!string.IsNullOrWhiteSpace(fileName))
    {
        var e = Path.GetExtension(fileName);
        if (!string.IsNullOrEmpty(e) && e.Length <= 6) return e;
    }
    return mime switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "video/quicktime" => ".mov",
        "audio/mpeg" => ".mp3",
        "audio/ogg" => ".ogg",
        "audio/mp4" => ".m4a",
        "application/pdf" => ".pdf",
        "application/zip" => ".zip",
        _ => fallback,
    };
}

static string? TryStr(JsonElement el, string prop)
{
    if (!el.TryGetProperty(prop, out var v)) return null;
    if (v.ValueKind == JsonValueKind.String) return v.GetString();
    if (v.ValueKind == JsonValueKind.Object)
    {
        if (v.TryGetProperty("active_usernames", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var first = arr.EnumerateArray().FirstOrDefault();
            return first.ValueKind == JsonValueKind.String ? first.GetString() : null;
        }
        if (v.TryGetProperty("editable_username", out var eu)) return eu.GetString();
    }
    return null;
}

static string FormatBytes(long bytes)
{
    if (bytes < 1024) return bytes + " B";
    if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
    if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("F1") + " MB";
    return (bytes / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
}

// ─────────────────────────────────────────────────────────────
// JSON 取值辅助 (TDLib 的 int64 会以字符串下发, 需要兼容两种形态)
// 放在静态类里以便重载, 通过文件头的 using static 直接调用
// ─────────────────────────────────────────────────────────────

public static class TdlJsonHelper
{
    public static long ReadLong(JsonElement el) =>
        el.ValueKind == JsonValueKind.Number ? el.GetInt64() :
        el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var n) ? n : 0;

    public static int ReadInt(JsonElement el) =>
        el.ValueKind == JsonValueKind.Number ? el.GetInt32() :
        el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var n) ? n : 0;

    public static long ReadLong(JsonElement parent, string prop) =>
        parent.TryGetProperty(prop, out var el) ? ReadLong(el) : 0;

    public static int ReadInt(JsonElement parent, string prop) =>
        parent.TryGetProperty(prop, out var el) ? ReadInt(el) : 0;
}

// ─────────────────────────────────────────────────────────────
// 类型
// ─────────────────────────────────────────────────────────────

enum DownloadStatus { Completed, Skipped, Failed }

/// <summary>一条待下载的媒体任务 (CLI 与服务端共用)。</summary>
public record DownloadJob(string TargetPath, MediaInfo Media, long MessageId, DateTime Date);

/// <summary>HTTP API 中的一个下载任务。</summary>
public class ServerJob
{
    public string Id { get; set; } = "";
    public string Chat { get; set; } = "";
    public long ChatId { get; set; }
    public string ChatTitle { get; set; } = "";
    public string Output { get; set; } = "";
    public string? Types { get; set; }
    public string Status { get; set; } = "queued";   // queued | running | done | failed | cancelled
    public bool DryRun { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int TotalFiles { get; set; }
    public int Completed { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public long Bytes { get; set; }
    public long PlannedBytes { get; set; }
    public int Segments { get; set; }
    public int NonEmptySegments { get; set; }
    public string? Error { get; set; }
    public List<JobSegmentInfo> SegmentList { get; set; } = new();

    [JsonIgnore]
    public CancellationTokenSource? Cts { get; set; }
}

public class JobSegmentInfo
{
    public int Index { get; set; }
    public string Dir { get; set; } = "";
    public DateTime? StartDate { get; set; }
    public int Files { get; set; }
    public long Bytes { get; set; }
}

public class MessageInfo
{
    public long Id { get; set; }
    public DateTime Date { get; set; }
    public bool IsOutgoing { get; set; }
    public long SenderUserId { get; set; }
    public string Text { get; set; } = "";
    public MediaInfo? Media { get; set; }
}

public class MediaInfo
{
    public string Type { get; set; } = "";
    public int FileId { get; set; }
    public long Size { get; set; }
    public string Mime { get; set; } = "";
    public string Ext { get; set; } = "";
    public string? FileName { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Duration { get; set; }
}

public class Segment
{
    public int Index { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public long StartMessageId { get; set; }
    public string StartArgs { get; set; } = "";
    public string? Label { get; set; }
    public List<MessageInfo> Items { get; set; } = new();
}

public class DownloadStats
{
    public int Total { get; set; }
    public int Completed { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public long Bytes { get; set; }
    public long PlannedBytes { get; set; }
    public TimeSpan Elapsed { get; set; }
}

// ─────────────────────────────────────────────────────────────
// 单线程事件循环 (TdJsonClient 只能从同一线程 Receive)
// ─────────────────────────────────────────────────────────────

public class EventLoop
{
    private readonly TdJsonClient _client;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();
    private string _authState = "Init";
    private string? _qrLink;
    private int _counter;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    public EventLoop(TdJsonClient client) { _client = client; }

    public void Start() { _loopTask = Task.Run(Loop); }

    public void Stop()
    {
        _cts.Cancel();
        try { _loopTask?.Wait(3000); } catch { }
    }

    public string GetAuthState() => _authState;

    /// <summary>QR 登录链接 (tg://login?token=xxx), 仅在 QR 状态下有值。</summary>
    public string? QrLink => _qrLink;

    public void Send(string jsonWithoutExtra) { _client.Send(jsonWithoutExtra); }

    public Task<string> RpcAsync(string jsonWithoutExtra, double timeoutSec)
    {
        var key = "r-" + Interlocked.Increment(ref _counter);
        var injected = jsonWithoutExtra.Insert(jsonWithoutExtra.Length - 1, ",\"@extra\":\"" + key + "\"");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;
        _client.Send(injected);

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(timeoutSec));
            if (_pending.TryRemove(key, out var pending))
                pending.TrySetResult("{\"@type\":\"error\",\"code\":408,\"message\":\"timeout\"}");
        });

        return tcs.Task;
    }

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var u = _client.Receive(1.0);
                if (string.IsNullOrEmpty(u)) continue;
                ProcessUpdate(u);
            }
            catch
            {
                if (_cts.IsCancellationRequested) break;
                Thread.Sleep(50);
            }
        }
    }

    private void ProcessUpdate(string u)
    {
        try
        {
            using var doc = JsonDocument.Parse(u);
            var root = doc.RootElement;

            if (root.TryGetProperty("@extra", out var extra))
            {
                var key = extra.GetString();
                if (key != null && _pending.TryRemove(key, out var tcs))
                    tcs.TrySetResult(u);
                return;
            }

            if (root.TryGetProperty("@type", out var typeEl) &&
                typeEl.GetString() == "updateAuthorizationState" &&
                root.TryGetProperty("authorization_state", out var authStateEl))
            {
                var authType = authStateEl.GetProperty("@type").GetString() ?? "";
                _authState = authType switch
                {
                    "authorizationStateReady" => "Ready",
                    "authorizationStateWaitPhoneNumber" => "Phone",
                    "authorizationStateWaitCode" => "Code",
                    "authorizationStateWaitPassword" => "Password",
                    "authorizationStateWaitRegistration" => "Registration",
                    "authorizationStateWaitEncryptionKey" => "Key",
                    "authorizationStateWaitOtherDeviceConfirmation" => "QR",
                    "authorizationStateWaitEmailAddress" => "Email",
                    "authorizationStateWaitEmailCode" => "EmailCode",
                    "authorizationStateClosed" => "Closed",
                    _ => authType,
                };

                if (authType == "authorizationStateWaitOtherDeviceConfirmation")
                {
                    var m = System.Text.RegularExpressions.Regex.Match(u, "tg://login\\?token=[A-Za-z0-9_\\-]+");
                    if (m.Success) _qrLink = m.Value;
                }
                else if (authType == "authorizationStateReady")
                {
                    _qrLink = null;
                }
            }
        }
        catch
        {
            // 忽略不关心的 update
        }
    }
}
