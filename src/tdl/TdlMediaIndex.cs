#!/usr/bin/env dotnet

#:package TDLib@*
#:package tdlib.native@*
#:package tdlib.native.win-x64@*
#:package System.CommandLine@*
#:package Spectre.Console@*
#:package Spectre.Console.Ansi@*

using System.Collections.Concurrent;
using System.CommandLine;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;
using TdLib;
using TdLib.Bindings;

string LogInfo(string msg)  { Console.WriteLine($"[INFO ] {msg}"); return msg; }
string LogWarn(string msg)  { Console.WriteLine($"[WARN ] {msg}"); return msg; }
string LogErr(string msg)   { Console.Error.WriteLine($"[ERROR] {msg}"); return msg; }

long ReadLong(JsonElement el)
{
    if (el.ValueKind == JsonValueKind.Number) return el.GetInt64();
    if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var n)) return n;
    return 0;
}

int ReadInt(JsonElement el)
{
    if (el.ValueKind == JsonValueKind.Number) return el.GetInt32();
    if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var n)) return n;
    return 0;
}

bool ReadBool(JsonElement el)
{
    if (el.ValueKind == JsonValueKind.True) return true;
    if (el.ValueKind == JsonValueKind.False) return false;
    return false;
}

using (var client = new TdJsonClient())
{
    client.Send("{\"@type\":\"setLogVerbosityLevel\",\"new_verbosity_level\":2}");
    await Main(client, args);
}

async Task Main(TdJsonClient client, string[] args)
{
    var optionChat = new Option<string>("--chat") { Required = true, Description = "Bot username (如 @jingji0213_bot) 或 ChatId" };
    var optionOutput = new Option<string?>("--output") { Required = false, Description = "输出 JSON 文件路径 (默认: data/tdl/media-index/<chat_id>.json)" };
    var optionBatch = new Option<int>("--batch") { DefaultValueFactory = _ => 100, Description = "单次拉取的消息数 (1-100)" };
    var optionMax = new Option<int>("--max-messages") { DefaultValueFactory = _ => 0, Description = "最多索引的消息数, 0=全部" };
    var optionOnlyMedia = new Option<bool>("--only-media") { DefaultValueFactory = _ => true, Description = "只保留含媒体的消息" };

    var rootCommand = new RootCommand("索引指定 Telegram 聊天的全部消息 (或仅媒体)，输出 JSON 索引文件");
    rootCommand.Options.Add(optionChat);
    rootCommand.Options.Add(optionOutput);
    rootCommand.Options.Add(optionBatch);
    rootCommand.Options.Add(optionMax);
    rootCommand.Options.Add(optionOnlyMedia);

    var parseResult = rootCommand.Parse(args);
    var chatArg = parseResult.GetValue(optionChat)!;
    var outputArg = parseResult.GetValue(optionOutput);
    var batchSize = Math.Clamp(parseResult.GetValue(optionBatch), 1, 100);
    var maxMessages = Math.Max(0, parseResult.GetValue(optionMax));
    var onlyMedia = parseResult.GetValue(optionOnlyMedia);

    // 1. 启动后台事件循环（单线程 Receive）
    var state = new EventLoop(client);
    state.Start();

    try
    {
        // 2. 初始化 TDLib
        if (!InitTdlib(state))
            return;

        // 3. 等待 Ready
        var authState = await WaitForReadyAsync(state);
        if (authState != "Ready")
        {
            LogErr("未能进入 Ready 状态: " + authState);
            Environment.Exit(1);
            return;
        }

        // 4. 解析 chat
        var resolved = await ResolveChatAsync(state, chatArg);
        if (resolved == null)
        {
            LogErr("无法解析聊天: " + chatArg);
            Environment.Exit(1);
            return;
        }

        if (string.IsNullOrWhiteSpace(outputArg))
        {
            var saveDir = Path.Combine("data", "tdl", "media-index");
            Directory.CreateDirectory(saveDir);
            outputArg = Path.Combine(saveDir, resolved.Id + ".json");
        }

        // 5. 索引消息
        var indexed = await IndexMediaAsync(state, resolved.Id, batchSize, maxMessages, onlyMedia);

        long totalBytes = 0;
        foreach (var m in indexed.Media)
            if (m.Media?.Size.HasValue == true) totalBytes += m.Media.Size.Value;

        var export = new MediaIndexExport
        {
            ChatId = resolved.Id,
            ChatTitle = resolved.Title,
            ChatUsername = resolved.Username,
            ExportTime = DateTime.UtcNow,
            TotalMessagesScanned = indexed.TotalScanned,
            TotalMediaMessages = indexed.Media.Count,
            MediaTypeBreakdown = indexed.Media.GroupBy(m => m.Type).ToDictionary(g => g.Key, g => g.Count()),
            TotalBytes = totalBytes,
            MediaMessages = indexed.Media
        };

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        var json = JsonSerializer.Serialize(export, jsonOptions);

        var dir = Path.GetDirectoryName(outputArg);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(outputArg, json);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold green]索引完成[/]").LeftJustified());
        AnsiConsole.MarkupLine("聊天:       [cyan]" + resolved.Title + "[/] (" + resolved.Id + ")");
        if (!string.IsNullOrEmpty(resolved.Username))
            AnsiConsole.MarkupLine("用户名:     [cyan]@" + resolved.Username + "[/]");
        AnsiConsole.MarkupLine("扫描消息:   [yellow]" + indexed.TotalScanned + "[/]");
        AnsiConsole.MarkupLine("媒体消息:   [yellow]" + indexed.Media.Count + "[/]");
        AnsiConsole.MarkupLine("总大小:     [yellow]" + FormatBytes(totalBytes) + "[/]");
        AnsiConsole.MarkupLine("输出文件:   [green]" + outputArg + "[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]媒体类型分布:[/]");
        foreach (var kv in export.MediaTypeBreakdown.OrderByDescending(p => p.Value))
        {
            var bar = new string('█', Math.Min(40, kv.Value));
            AnsiConsole.MarkupLine("  [blue]" + kv.Key.PadRight(14) + "[/] " + bar + " [yellow]" + kv.Value + "[/]");
        }
        AnsiConsole.WriteLine();
    }
    finally
    {
        state.Stop();
    }
}

bool InitTdlib(EventLoop state)
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
    var tdlPath = tdlRoot.Replace("\\", "/");

    state.Send("{\"@type\":\"setTdlibParameters\",\"api_id\":\"" + apiId + "\",\"api_hash\":\"" + apiHash + "\",\"device_model\":\"PC\",\"system_language_code\":\"en\",\"application_version\":\"1.0.0\",\"database_directory\":\"" + tdlPath + "/db\",\"files_directory\":\"" + tdlPath + "/files\",\"use_file_database\":true,\"use_chat_info_database\":true,\"use_message_database\":true}");
    LogInfo("TDLib 参数已下发, 数据库目录: " + tdlRoot);
    return true;
}

async Task<string> WaitForReadyAsync(EventLoop state)
{
    var deadline = DateTime.UtcNow.AddSeconds(60);
    while (DateTime.UtcNow < deadline)
    {
        var s = state.GetAuthState();
        if (s == "Ready") return "Ready";
        if (s == "Phone" || s == "QR" || s == "Closed" || s == "Key" || s == "Password") return s;
        await Task.Delay(200);
    }
    return "Timeout";
}

async Task<ResolvedChat?> ResolveChatAsync(EventLoop state, string chatArg)
{
    long chatId;
    if (long.TryParse(chatArg.Trim(), out var id))
    {
        chatId = id;
    }
    else
    {
        var name = chatArg.Trim().TrimStart('@');
        var searchJson = "{\"@type\":\"searchPublicChat\",\"username\":\"" + name + "\"}";
        var searchResult = await state.RpcAsync(searchJson, 30);
        using var sd = JsonDocument.Parse(searchResult);
        if (sd.RootElement.GetProperty("@type").GetString() == "error")
        {
            LogErr("搜索用户失败: " + searchResult);
            return null;
        }
        chatId = ReadLong(sd.RootElement.GetProperty("id"));
    }

    var chatInfo = await state.RpcAsync("{\"@type\":\"getChat\",\"chat_id\":" + chatId + "}", 30);
    using var cd = JsonDocument.Parse(chatInfo);
    var title = cd.RootElement.GetProperty("title").GetString() ?? "(无标题)";

    string? username = null;
    if (cd.RootElement.TryGetProperty("usernames", out var names) &&
        names.TryGetProperty("active_usernames", out var aus) &&
        aus.GetArrayLength() > 0)
    {
        username = aus[0].GetString();
    }

    LogInfo("目标聊天: [" + title.EscapeMarkup() + "] ChatId=" + chatId + " Username=@" + (username?.EscapeMarkup() ?? "(none)"));
    return new ResolvedChat { Id = chatId, Title = title, Username = username };
}

async Task<(int TotalScanned, List<MediaMessageIndex> Media)> IndexMediaAsync(
    EventLoop state, long chatId, int batchSize, int maxMessages, bool onlyMedia)
{
    var mediaList = new List<MediaMessageIndex>();
    int totalScanned = 0;
    long offset = 0;
    int batches = 0;
    bool hasMore = true;
    var mediaTypes = new HashSet<string>
    {
        "messagePhoto","messageVideo","messageDocument","messageAudio",
        "messageVoiceNote","messageVideoNote","messageAnimation","messageSticker"
    };

    long fromMessageId = 0;
    var seenIds = new HashSet<long>();
    int totalExpected = -1;
    while (hasMore)
    {
        // 注意: TDLib searchChatMessages 的 offset 必须为非正数, 分页应使用 from_message_id
        var reqJson = "{\"@type\":\"searchChatMessages\",\"chat_id\":" + chatId +
                      ",\"query\":\"\",\"from_message_id\":" + fromMessageId +
                      ",\"offset\":0,\"limit\":" + batchSize +
                      ",\"filter\":null,\"message_thread_id\":0}";

        var searchResult = await state.RpcAsync(reqJson, 60);
        using var doc = JsonDocument.Parse(searchResult);
        if (doc.RootElement.GetProperty("@type").GetString() == "error")
        {
            LogErr("searchChatMessages 失败: " + searchResult);
            break;
        }

        if (!doc.RootElement.TryGetProperty("messages", out var msgs))
            break;

        var arr = msgs.EnumerateArray().ToList();
        if (arr.Count == 0) break;
        batches++;

        int newInBatch = 0;
        foreach (var m in arr)
        {
            var msgId = ReadLong(m.GetProperty("id"));
            if (!seenIds.Add(msgId)) continue; // 去重
            totalScanned++;
            newInBatch++;
            var date = ReadLong(m.GetProperty("date"));
            var content = m.GetProperty("content");
            var type = content.GetProperty("@type").GetString() ?? "";

            if (onlyMedia && !mediaTypes.Contains(type)) continue;

            var media = ExtractMedia(content);
            if (media == null)
            {
                if (!mediaTypes.Contains(type))
                    continue; // 类型不在白名单但也不应被解析
                // 类型在白名单但解析失败 - 跳过但记录
                continue;
            }

            mediaList.Add(new MediaMessageIndex
            {
                MessageId = msgId,
                Date = DateTimeOffset.FromUnixTimeSeconds(date).DateTime,
                Type = type.StartsWith("message") ? type.Substring("message".Length) : type,
                Caption = ExtractCaption(content),
                Media = media,
                IsOutgoing = m.TryGetProperty("is_outgoing", out var io) && ReadBool(io),
                SenderId = ExtractSenderId(m),
                MediaAlbumId = m.TryGetProperty("media_album_id", out var mai) ? ReadLong(mai) : 0,
                HasTimestampedMedia = m.TryGetProperty("has_timestamped_media", out var htm) && ReadBool(htm)
            });
        }

        if (doc.RootElement.TryGetProperty("total_count", out var tcEl))
            totalExpected = ReadInt(tcEl);
        var totalStr = totalExpected > 0 ? "/" + totalExpected : "";
        AnsiConsole.MarkupLine("  [grey]Batch " + batches + ": 新增 " + newInBatch + " (累计 " + totalScanned + totalStr + ") → 媒体 " + mediaList.Count + "[/]");

        // 终止条件
        if (totalExpected > 0 && totalScanned >= totalExpected) hasMore = false;
        if (newInBatch == 0) hasMore = false;                  // TDLib 数据库空缓存时全重复
        if (arr.Count < batchSize) hasMore = false;
        if (maxMessages > 0 && totalScanned >= maxMessages) hasMore = false;

        if (!hasMore) break;

        long nextId = 0;
        if (doc.RootElement.TryGetProperty("next_from_message_id", out var nfm))
            nextId = ReadLong(nfm);
        if (nextId == 0 || nextId == fromMessageId)
            nextId = ReadLong(arr[arr.Count - 1].GetProperty("id"));
        if (nextId == fromMessageId) break;
        fromMessageId = nextId;

        await Task.Delay(300);
    }

    return (totalScanned, mediaList);
}

MediaDetails? ExtractMedia(JsonElement content)
{
    try
    {
        return ExtractMediaInner(content);
    }
    catch (Exception ex)
    {
        var type = content.TryGetProperty("@type", out var t) ? t.GetString() : "?";
        LogErr("ExtractMedia(" + type + ") 失败: " + ex.Message);
        return null;
    }
}

MediaDetails? ExtractMediaInner(JsonElement content)
{
    var type = content.GetProperty("@type").GetString() ?? "";
    switch (type)
    {
        case "messagePhoto":
        {
            var photo = content.GetProperty("photo");
            var sizes = photo.GetProperty("sizes").EnumerateArray().ToList();
            var largest = sizes.OrderByDescending(s => s.TryGetProperty("width", out var w) ? ReadInt(w) : 0).FirstOrDefault();
            if (largest.ValueKind == JsonValueKind.Undefined) return null;
            var photoFile = largest.GetProperty("photo");
            var remote = photoFile.GetProperty("remote");
            return new MediaDetails
            {
                FileId = ReadInt(photoFile.GetProperty("id")).ToString(),
                RemoteId = remote.GetProperty("id").GetString(),
                UniqueId = remote.GetProperty("unique_id").GetString(),
                Width = largest.TryGetProperty("width", out var w) ? ReadInt(w) : null,
                Height = largest.TryGetProperty("height", out var h) ? ReadInt(h) : null,
                Size = photoFile.TryGetProperty("expected_size", out var es) ? ReadLong(es) : null,
                SizeCount = sizes.Count,
                MimeType = "image/jpeg"
            };
        }
        case "messageVideo":
        {
            // video 可能是 {video: file, ...} 或 file 本身
            var v = content.GetProperty("video");
            JsonElement videoFile = v.TryGetProperty("video", out var vf) ? vf : v;
            JsonElement remote = videoFile.TryGetProperty("remote", out var r) ? r : default;
            JsonElement idEl = v.TryGetProperty("id", out var id2) ? id2 : videoFile.GetProperty("id");
            return new MediaDetails
            {
                FileId = ReadLong(idEl).ToString(),
                RemoteId = remote.ValueKind != JsonValueKind.Undefined ? remote.GetProperty("id").GetString() : null,
                UniqueId = remote.ValueKind != JsonValueKind.Undefined ? remote.GetProperty("unique_id").GetString() : null,
                FileName = v.TryGetProperty("file_name", out var fn) ? fn.GetString() : null,
                MimeType = v.TryGetProperty("mime_type", out var mt) ? mt.GetString() : null,
                Width = v.TryGetProperty("width", out var w) ? ReadInt(w) : null,
                Height = v.TryGetProperty("height", out var h) ? ReadInt(h) : null,
                Duration = v.TryGetProperty("duration", out var d) ? ReadInt(d) : null,
                Size = videoFile.TryGetProperty("expected_size", out var es) ? ReadLong(es) : null,
                SupportsStreaming = v.TryGetProperty("supports_streaming", out var ss) && ReadBool(ss)
            };
        }
        case "messageDocument":
        {
            var d = content.GetProperty("document");
            var docFile = d.GetProperty("document");
            var remote = docFile.GetProperty("remote");
            return new MediaDetails
            {
                FileId = d.GetProperty("id").GetInt64().ToString(),
                RemoteId = remote.GetProperty("id").GetString(),
                UniqueId = remote.GetProperty("unique_id").GetString(),
                FileName = d.TryGetProperty("file_name", out var fn) ? fn.GetString() : null,
                MimeType = d.TryGetProperty("mime_type", out var mt) ? mt.GetString() : null,
                Size = docFile.TryGetProperty("expected_size", out var es) ? ReadLong(es) : null
            };
        }
        case "messageAudio":
        {
            var a = content.GetProperty("audio");
            var audioFile = a.GetProperty("audio");
            var remote = audioFile.GetProperty("remote");
            return new MediaDetails
            {
                FileId = a.GetProperty("id").GetInt64().ToString(),
                RemoteId = remote.GetProperty("id").GetString(),
                UniqueId = remote.GetProperty("unique_id").GetString(),
                FileName = a.TryGetProperty("file_name", out var fn) ? fn.GetString() : null,
                MimeType = a.TryGetProperty("mime_type", out var mt) ? mt.GetString() : null,
                Duration = a.TryGetProperty("duration", out var d) ? ReadInt(d) : null,
                Size = audioFile.TryGetProperty("expected_size", out var es) ? ReadLong(es) : null,
                Title = a.TryGetProperty("title", out var t) ? t.GetString() : null,
                Performer = a.TryGetProperty("performer", out var p) ? p.GetString() : null
            };
        }
        case "messageVoiceNote":
        {
            var vn = content.GetProperty("voice_note");
            var voiceFile = vn.GetProperty("voice");
            var remote = voiceFile.GetProperty("remote");
            return new MediaDetails
            {
                FileId = vn.GetProperty("id").GetInt64().ToString(),
                RemoteId = remote.GetProperty("id").GetString(),
                UniqueId = remote.GetProperty("unique_id").GetString(),
                MimeType = vn.TryGetProperty("mime_type", out var mt) ? mt.GetString() : null,
                Duration = vn.TryGetProperty("duration", out var d) ? ReadInt(d) : null,
                Size = voiceFile.TryGetProperty("expected_size", out var es) ? ReadLong(es) : null,
                Waveform = vn.TryGetProperty("waveform", out var wf) ? wf.GetString() : null
            };
        }
        case "messageVideoNote":
        {
            var vn = content.GetProperty("video_note");
            var videoFile = vn.GetProperty("video");
            var remote = videoFile.GetProperty("remote");
            return new MediaDetails
            {
                FileId = vn.GetProperty("id").GetInt64().ToString(),
                RemoteId = remote.GetProperty("id").GetString(),
                UniqueId = remote.GetProperty("unique_id").GetString(),
                Duration = vn.TryGetProperty("duration", out var d) ? ReadInt(d) : null,
                Size = videoFile.TryGetProperty("expected_size", out var es) ? ReadLong(es) : null,
                Length = vn.TryGetProperty("length", out var l) ? ReadInt(l) : null
            };
        }
        case "messageAnimation":
        {
            var ani = content.GetProperty("animation");
            var aniFile = ani.GetProperty("animation");
            var remote = aniFile.GetProperty("remote");
            return new MediaDetails
            {
                FileId = ani.GetProperty("id").GetInt64().ToString(),
                RemoteId = remote.GetProperty("id").GetString(),
                UniqueId = remote.GetProperty("unique_id").GetString(),
                FileName = ani.TryGetProperty("file_name", out var fn) ? fn.GetString() : null,
                MimeType = ani.TryGetProperty("mime_type", out var mt) ? mt.GetString() : null,
                Width = ani.TryGetProperty("width", out var w) ? ReadInt(w) : null,
                Height = ani.TryGetProperty("height", out var h) ? ReadInt(h) : null,
                Duration = ani.TryGetProperty("duration", out var d) ? ReadInt(d) : null,
                Size = aniFile.TryGetProperty("expected_size", out var es) ? ReadLong(es) : null
            };
        }
        case "messageSticker":
        {
            var s = content.GetProperty("sticker");
            var stickerFile = s.GetProperty("sticker");
            var remote = stickerFile.GetProperty("remote");
            return new MediaDetails
            {
                FileId = s.GetProperty("id").GetInt64().ToString(),
                RemoteId = remote.GetProperty("id").GetString(),
                UniqueId = remote.GetProperty("unique_id").GetString(),
                Width = s.TryGetProperty("width", out var w) ? ReadInt(w) : null,
                Height = s.TryGetProperty("height", out var h) ? ReadInt(h) : null,
                Size = stickerFile.TryGetProperty("expected_size", out var es) ? ReadLong(es) : null,
                Emoji = s.TryGetProperty("emoji", out var e) ? e.GetString() : null,
                SetId = s.TryGetProperty("set_id", out var sid) ? sid.GetInt64().ToString() : null
            };
        }
        default:
            return null;
    }
}

string? ExtractCaption(JsonElement content)
{
    if (content.TryGetProperty("caption", out var cap) && cap.ValueKind == JsonValueKind.Object)
        return cap.GetProperty("text").GetString();
    return null;
}

string? ExtractSenderId(JsonElement msg)
{
    if (!msg.TryGetProperty("sender_id", out var sid)) return null;
    var t = sid.GetProperty("@type").GetString();
    return t switch
    {
        "messageSenderUser" => "user:" + ReadLong(sid.GetProperty("user_id")),
        "messageSenderChat" => "chat:" + ReadLong(sid.GetProperty("chat_id")),
        "messageSenderHiddenUser" => "hidden:" + sid.GetProperty("sender_name").GetString(),
        _ => t
    };
}

string FormatBytes(long bytes)
{
    if (bytes < 1024) return bytes + " B";
    if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
    if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("F1") + " MB";
    return (bytes / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
}

// ─────────────────────────────────────────────────────────────
// 单线程事件循环：负责 Receive + 关联 @extra + 跟踪授权状态
// ─────────────────────────────────────────────────────────────

public class EventLoop
{
    private readonly TdJsonClient _client;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();
    private string _authState = "Init";
    private string? _qrLink;
    private int _counter;
    private CancellationTokenSource _cts = new();
    private Task? _loopTask;

    public EventLoop(TdJsonClient client)
    {
        _client = client;
    }

    public void Start()
    {
        _loopTask = Task.Run(Loop);
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _loopTask?.Wait(2000); } catch { }
    }

    public string GetAuthState()
    {
        if (_authState == "Init" && !string.IsNullOrEmpty(_qrLink)) return "QR";
        return _authState;
    }

    public string? QrLink => _qrLink;

    public void Send(string jsonWithoutExtra)
    {
        _client.Send(jsonWithoutExtra);
    }

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
            catch (Exception)
            {
                if (_cts.IsCancellationRequested) break;
                // 其他异常忽略，重试
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

            // 1. RPC 响应（有 @extra）
            if (root.TryGetProperty("@extra", out var extra))
            {
                var key = extra.GetString();
                if (key != null && _pending.TryRemove(key, out var tcs))
                    tcs.TrySetResult(u);
                return;
            }

            // 2. 授权状态
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
                    "authorizationStateLoggingOut" => _authState, // 保持
                    "authorizationStateClosing" => _authState,
                    _ => authType
                };

                // 抓 QR 链接
                if (authType == "authorizationStateWaitOtherDeviceConfirmation")
                {
                    var linkMatch = Regex.Match(u, "tg://login\\?token=[A-Za-z0-9_\\-]+");
                    if (linkMatch.Success) _qrLink = linkMatch.Value;
                }
                return;
            }
        }
        catch
        {
            // 忽略解析错误
        }
    }
}

public class ResolvedChat
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string? Username { get; set; }
}

public class MediaIndexExport
{
    public long ChatId { get; set; }
    public string ChatTitle { get; set; } = "";
    public string? ChatUsername { get; set; }
    public DateTime ExportTime { get; set; }
    public int TotalMessagesScanned { get; set; }
    public int TotalMediaMessages { get; set; }
    public Dictionary<string, int> MediaTypeBreakdown { get; set; } = new();
    public long TotalBytes { get; set; }
    public List<MediaMessageIndex> MediaMessages { get; set; } = new();
}

public class MediaMessageIndex
{
    public long MessageId { get; set; }
    public DateTime Date { get; set; }
    public string Type { get; set; } = "";
    public string? Caption { get; set; }
    public MediaDetails? Media { get; set; }
    public bool IsOutgoing { get; set; }
    public string? SenderId { get; set; }
    public long MediaAlbumId { get; set; }
    public bool HasTimestampedMedia { get; set; }
}

public class MediaDetails
{
    public string? FileId { get; set; }
    public string? RemoteId { get; set; }
    public string? UniqueId { get; set; }
    public string? FileName { get; set; }
    public string? MimeType { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? Duration { get; set; }
    public int? Length { get; set; }
    public long? Size { get; set; }
    public int? SizeCount { get; set; }
    public bool? SupportsStreaming { get; set; }
    public string? Title { get; set; }
    public string? Performer { get; set; }
    public string? Waveform { get; set; }
    public string? Emoji { get; set; }
    public string? SetId { get; set; }
}
