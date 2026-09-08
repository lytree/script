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

string LogInfo(string msg) { Console.WriteLine($"[INFO ] {msg}"); return msg; }
string LogWarn(string msg) { Console.WriteLine($"[WARN ] {msg}"); return msg; }
string LogErr(string msg)  { Console.Error.WriteLine($"[ERROR] {msg}"); return msg; }

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

using (var client = new TdJsonClient())
{
    client.Send("{\"@type\":\"setLogVerbosityLevel\",\"new_verbosity_level\":2}");
    await Main(client, args);
}

async Task Main(TdJsonClient client, string[] args)
{
    var optIndex = new Option<string>("--index") { Required = true, Description = "媒体索引 JSON 文件 (如 data/tdl/media-index/8682502640.json)" };
    var optOutput = new Option<string?>("--output") { Required = false, Description = "下载根目录 (默认 data/tdl/downloads/<chatId>)" };
    var optConcurrency = new Option<int>("--concurrency") { DefaultValueFactory = _ => 4, Description = "并发下载数" };
    var optType = new Option<string?>("--type") { Required = false, Description = "只下载指定类型: Photo 或 Video" };
    var optMax = new Option<int>("--max") { DefaultValueFactory = _ => 0, Description = "最多下载条数 (0=全部)" };
    var optLimit = new Option<int>("--limit") { DefaultValueFactory = _ => 0, Description = "只下载前 N 条 (0=全部)" };
    var optSkipExisting = new Option<bool>("--skip-existing") { DefaultValueFactory = _ => true, Description = "跳过已下载的文件 (按目标路径检查)" };
    var optPriority = new Option<int>("--priority") { DefaultValueFactory = _ => 16, Description = "TDLib 下载优先级 (1=最低, 32=最高)" };
    var optStartFrom = new Option<int>("--start") { DefaultValueFactory = _ => 0, Description = "从第 N 条开始 (用于分段)" };

    var root = new RootCommand("根据 TdlMediaIndex 生成的 JSON 索引，下载所有照片和视频到本地");
    root.Options.Add(optIndex);
    root.Options.Add(optOutput);
    root.Options.Add(optConcurrency);
    root.Options.Add(optType);
    root.Options.Add(optMax);
    root.Options.Add(optLimit);
    root.Options.Add(optSkipExisting);
    root.Options.Add(optPriority);
    root.Options.Add(optStartFrom);

    var p = root.Parse(args);
    var indexPath = p.GetValue(optIndex)!;
    var outputRoot = p.GetValue(optOutput);
    var concurrency = Math.Clamp(p.GetValue(optConcurrency), 1, 16);
    var typeFilter = p.GetValue(optType);
    var maxN = Math.Max(p.GetValue(optMax), p.GetValue(optLimit));
    var skipExisting = p.GetValue(optSkipExisting);
    var priority = Math.Clamp(p.GetValue(optPriority), 1, 32);
    var startFrom = Math.Max(0, p.GetValue(optStartFrom));

    if (!File.Exists(indexPath))
    {
        LogErr("索引文件不存在: " + indexPath);
        Environment.Exit(1);
        return;
    }

    // 读取索引
    LogInfo("读取索引: " + indexPath);
    var json = await File.ReadAllTextAsync(indexPath);
    using var doc = JsonDocument.Parse(json);
    var chatId = doc.RootElement.GetProperty("ChatId").GetInt64();
    var chatTitle = doc.RootElement.GetProperty("ChatTitle").GetString() ?? "unknown";
    var mediaList = doc.RootElement.GetProperty("MediaMessages").EnumerateArray().ToList();

    outputRoot ??= Path.Combine("data", "tdl", "downloads", chatId.ToString());
    Directory.CreateDirectory(outputRoot);

    LogInfo("聊天: " + chatTitle + " (ChatId=" + chatId + ")");
    LogInfo("媒体消息总数: " + mediaList.Count);

    // 类型过滤
    var filtered = mediaList.AsEnumerable();
    if (!string.IsNullOrWhiteSpace(typeFilter))
    {
        filtered = filtered.Where(m => string.Equals(m.GetProperty("Type").GetString(), typeFilter, StringComparison.OrdinalIgnoreCase));
        LogInfo("按类型过滤: " + typeFilter);
    }
    if (startFrom > 0)
        filtered = filtered.Skip(startFrom);
    if (maxN > 0)
        filtered = filtered.Take(maxN);

    var list = filtered.ToList();
    LogInfo("待下载条数: " + list.Count);

    // 启动 TDLib
    var state = new EventLoop(client);
    state.Start();

    try
    {
        if (!InitTdlib(state)) return;
        var authState = await WaitForReadyAsync(state);
        if (authState != "Ready")
        {
            LogErr("未能进入 Ready 状态: " + authState);
            Environment.Exit(1);
            return;
        }
        LogInfo("TDLib Ready，开始下载");

        // 进度统计
        int completed = 0, failed = 0, skipped = 0;
        long bytesDownloaded = 0;
        var lockObj = new object();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await AnsiConsole.Progress()
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn { CompletedStyle = new Style(foreground: Color.Green), FinishedStyle = new Style(foreground: Color.Green) },
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn(Spinner.Known.Dots))
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"[green]下载 ({concurrency}并发)[/]", maxValue: list.Count);

                using var sem = new SemaphoreSlim(concurrency, concurrency);
                var tasks = new List<Task>();

                foreach (var msg in list)
                {
                    await sem.WaitAsync();
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var result = await DownloadOneAsync(state, msg, chatId, outputRoot, skipExisting, priority);
                            lock (lockObj)
                            {
                                switch (result.Status)
                                {
                                    case DownloadStatus.Completed: completed++; if (result.Bytes > 0) bytesDownloaded += result.Bytes; break;
                                    case DownloadStatus.Skipped: skipped++; break;
                                    case DownloadStatus.Failed: failed++; break;
                                }
                            }
                        }
                        finally
                        {
                            sem.Release();
                            task.Increment(1);
                        }
                    }));
                }

                await Task.WhenAll(tasks);
                task.Value = list.Count;
            });

        sw.Stop();
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold green]下载完成[/]").LeftJustified());
        AnsiConsole.MarkupLine("聊天:       [cyan]" + chatTitle.EscapeMarkup() + "[/] (" + chatId + ")");
        AnsiConsole.MarkupLine("完成:       [green]" + completed + "[/]");
        AnsiConsole.MarkupLine("跳过:       [yellow]" + skipped + "[/]");
        AnsiConsole.MarkupLine("失败:       [red]" + failed + "[/]");
        AnsiConsole.MarkupLine("总大小:     [yellow]" + FormatBytes(bytesDownloaded) + "[/]");
        AnsiConsole.MarkupLine("耗时:       [grey]" + sw.Elapsed.ToString(@"hh\:mm\:ss") + "[/]");
        AnsiConsole.MarkupLine("输出目录:   [green]" + outputRoot + "[/]");
        AnsiConsole.WriteLine();

        if (failed > 0) Environment.Exit(1);
    }
    finally
    {
        state.Stop();
    }
}

// ─────────────────────────────────────────────────────────────
// 下载单条媒体消息
// ─────────────────────────────────────────────────────────────

async Task<DownloadResult> DownloadOneAsync(
    EventLoop state, JsonElement msg, long chatId, string outputRoot,
    bool skipExisting, int priority)
{
    try
    {
        var msgId = ReadLong(msg.GetProperty("MessageId"));
        var type = msg.GetProperty("Type").GetString() ?? "Other";
        var dateStr = msg.GetProperty("Date").GetString() ?? "";
        var media = msg.GetProperty("Media");

        // 目标路径: <output>/<type>/<yyyy-MM>/<yyyyMMdd>_<msgId>.<ext>
        var date = DateTime.TryParse(dateStr, out var d) ? d : DateTime.UtcNow;
        var typeDir = Path.Combine(outputRoot, type);
        var monthDir = Path.Combine(typeDir, date.ToString("yyyy-MM"));
        Directory.CreateDirectory(monthDir);

        var ext = GetExtension(media);
        var fileName = date.ToString("yyyyMMdd_HHmmss") + "_" + msgId + ext;
        var targetPath = Path.Combine(monthDir, fileName);

        if (skipExisting && File.Exists(targetPath))
            return new DownloadResult(DownloadStatus.Skipped, 0, targetPath);

        // 调 getMessage 拿完整结构，定位目标 file_id
        int? targetFileId = null;
        var msgResult = await state.RpcAsync(
            "{\"@type\":\"getMessage\",\"chat_id\":" + chatId + ",\"message_id\":" + msgId + "}", 30);
        using (var md = JsonDocument.Parse(msgResult))
        {
            if (md.RootElement.GetProperty("@type").GetString() != "error")
            {
                targetFileId = PickBestFileId(md.RootElement, type);
            }
        }

        if (targetFileId == null)
        {
            // 退化：直接用索引里的 FileId
            if (int.TryParse(media.GetProperty("FileId").GetString(), out var fid))
                targetFileId = fid;
        }

        if (targetFileId == null)
        {
            LogErr("[" + msgId + "] 无法定位 file_id");
            return new DownloadResult(DownloadStatus.Failed, 0, "");
        }

        // 发起下载
        var dlReq = "{\"@type\":\"downloadFile\",\"file_id\":" + targetFileId.Value +
                    ",\"priority\":" + priority + ",\"offset\":0,\"limit\":0,\"synchronous\":false}";
        var dlResult = await state.RpcAsync(dlReq, 15);
        using (var dd = JsonDocument.Parse(dlResult))
        {
            if (dd.RootElement.GetProperty("@type").GetString() == "error")
            {
                LogErr("[" + msgId + "] downloadFile 失败: " + dlResult);
                return new DownloadResult(DownloadStatus.Failed, 0, "");
            }
        }

        // 轮询直到下载完成
        var localPath = await PollDownloadAsync(state, targetFileId.Value, msgId);
        if (string.IsNullOrEmpty(localPath))
        {
            LogErr("[" + msgId + "] 下载超时或失败 (file_id=" + targetFileId + ")");
            return new DownloadResult(DownloadStatus.Failed, 0, "");
        }

        // 移动到目标路径
        try
        {
            if (File.Exists(targetPath))
                File.Delete(targetPath);
            File.Move(localPath, targetPath);
        }
        catch (Exception ex)
        {
            // 跨盘或被占用 → 复制
            File.Copy(localPath, targetPath, true);
        }

        var size = new FileInfo(targetPath).Length;
        return new DownloadResult(DownloadStatus.Completed, size, targetPath);
    }
    catch (Exception ex)
    {
        LogErr("[" + msg.GetProperty("MessageId") + "] 异常: " + ex.Message);
        return new DownloadResult(DownloadStatus.Failed, 0, "");
    }
}

int? PickBestFileId(JsonElement message, string type)
{
    if (!message.TryGetProperty("content", out var content)) return null;
    var contentType = content.GetProperty("@type").GetString();
    if (contentType != "message" + type) return null;

    return type switch
    {
        "Photo" => BestPhotoFileId(content),
        "Video" => BestVideoFileId(content),
        "Animation" => BestAnimationFileId(content),
        "Document" => BestDocumentFileId(content),
        _ => null
    };
}

int? BestPhotoFileId(JsonElement content)
{
    if (!content.TryGetProperty("photo", out var photo)) return null;
    if (!photo.TryGetProperty("sizes", out var sizes)) return null;
    int bestId = 0;
    long bestArea = 0;
    foreach (var s in sizes.EnumerateArray())
    {
        if (!s.TryGetProperty("photo", out var pf)) continue;
        var w = ReadInt(s.GetProperty("width"));
        var h = ReadInt(s.GetProperty("height"));
        var area = (long)w * h;
        var id = ReadInt(pf.GetProperty("id"));
        if (area > bestArea) { bestArea = area; bestId = id; }
    }
    return bestId > 0 ? bestId : null;
}

int? BestVideoFileId(JsonElement content)
{
    if (!content.TryGetProperty("video", out var video)) return null;
    if (!video.TryGetProperty("video", out var vf)) return null;
    return ReadInt(vf.GetProperty("id"));
}

int? BestAnimationFileId(JsonElement content)
{
    if (!content.TryGetProperty("animation", out var ani)) return null;
    if (!ani.TryGetProperty("animation", out var af)) return null;
    return ReadInt(af.GetProperty("id"));
}

int? BestDocumentFileId(JsonElement content)
{
    if (!content.TryGetProperty("document", out var doc)) return null;
    if (!doc.TryGetProperty("document", out var df)) return null;
    return ReadInt(df.GetProperty("id"));
}

async Task<string?> PollDownloadAsync(EventLoop state, int fileId, long msgId, int timeoutSec = 600)
{
    var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
    string? lastPath = null;
    while (DateTime.UtcNow < deadline)
    {
        var resp = await state.RpcAsync(
            "{\"@type\":\"getFile\",\"file_id\":" + fileId + "}", 15);
        try
        {
            using var doc = JsonDocument.Parse(resp);
            if (doc.RootElement.GetProperty("@type").GetString() == "error")
                return null;

            var file = doc.RootElement;
            if (file.TryGetProperty("local", out var local))
            {
                var isCompleted = local.TryGetProperty("is_downloading_completed", out var ic) && ic.ValueKind == JsonValueKind.True;
                if (isCompleted)
                {
                    if (local.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String)
                    {
                        var path = pathEl.GetString();
                        if (!string.IsNullOrEmpty(path) && File.Exists(path))
                            return path;
                    }
                    // completed 但 path 为空 (罕见，文件可能被清理) → 再等一轮
                    if (local.TryGetProperty("downloaded_size", out var ds) && ds.GetInt64() > 0)
                        return null;
                }
                if (local.TryGetProperty("path", out var pathEl2) && pathEl2.ValueKind == JsonValueKind.String)
                    lastPath = pathEl2.GetString();
            }
        }
        catch { }
        await Task.Delay(1500);
    }
    LogWarn("[" + msgId + "] file_id=" + fileId + " 下载超时 (lastPath=" + (lastPath ?? "null") + ")");
    return null;
}

string GetExtension(JsonElement media)
{
    var mime = media.TryGetProperty("MimeType", out var m) ? m.GetString() : null;
    if (string.IsNullOrEmpty(mime)) return ".bin";
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
        _ => ".bin"
    };
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

string FormatBytes(long bytes)
{
    if (bytes < 1024) return bytes + " B";
    if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
    if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("F1") + " MB";
    return (bytes / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
}

// ─────────────────────────────────────────────────────────────
// 单线程事件循环
// ─────────────────────────────────────────────────────────────

enum DownloadStatus { Completed, Skipped, Failed }

record DownloadResult(DownloadStatus Status, long Bytes, string Path);

public class EventLoop
{
    private readonly TdJsonClient _client;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();
    private string _authState = "Init";
    private string? _qrLink;
    private int _counter;
    private CancellationTokenSource _cts = new();
    private Task? _loopTask;

    public EventLoop(TdJsonClient client) { _client = client; }

    public void Start() { _loopTask = Task.Run(Loop); }
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
            catch (Exception)
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

            // 1. RPC 响应
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
                    _ => authType
                };

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
