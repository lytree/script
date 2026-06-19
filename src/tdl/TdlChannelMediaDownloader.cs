#!/usr/bin/env dotnet

#:include ../../env.cs
#:include TdlUpdateHandler.cs

#:package TDLib@*
#:package tdlib.native@*
#:package tdlib.native.win-x64@*
#:package System.CommandLine@*
#:package Spectre.Console@0.55.2
#:package Spectre.Console.Ansi@0.55.2
#:package Microsoft.Extensions.Logging@*
#:package ZLogger@*
#:package YLFramework.ZLogging@1.0.3-alpha.7
#:package Microsoft.EntityFrameworkCore.Sqlite@*


using System.CommandLine;
using Framework.ZLogging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using TdLib;
using TdLib.Bindings;
using ZLogger;

ManualResetEventSlim ReadyToAuthenticate = new();
string tdlRoot = string.Empty;
DownloadTracker _downloadTracker = new();
HashSet<int> _downloadedFileIds = new();
Dictionary<int, long> _fileIdToAlbumId = new();
TdlUpdateHandler _updateHandler;
IDbContextFactory<DownloadDbContext> _dbFactory = null!;

async Task Main(TdClient client, string[] args)
{
    var logger = InitializeLogger();

    var optionOutput = new Option<string?>("--output") { DefaultValueFactory = (res) => Path.Combine(Path.EntryPointFileDirectoryPath(), "data") };
    var optionLink = new Option<string>("--link") { Description = "频道链接，如 https://t.me/channel_name" };
    var optionLimit = new Option<int>("--limit") { DefaultValueFactory = (res) => 0, Description = "获取消息数量，0=全部" };
    var optionDelay = new Option<int>("--delay") { DefaultValueFactory = (res) => 5000, Description = "每组下载间隔(ms)" };
    var rootCommand = new RootCommand { optionOutput, optionLink, optionLimit, optionDelay };
    var parseResult = rootCommand.Parse(args);
    var outputPath = parseResult.GetValue(optionOutput);
    var link = parseResult.GetValue(optionLink);
    var limit = parseResult.GetValue(optionLimit);
    var delay = parseResult.GetValue(optionDelay);

    InitializeEnvironment(logger);

    _updateHandler = new TdlUpdateHandler(ReadyToAuthenticate, logger)
        .OnConfigureTdlibParameters(ConfigureTdlibParameters)
        .OnFileUpdate(HandleFileUpdate);

    client.UpdateReceived += async (_, update) => { await _updateHandler.ProcessUpdates(client, update, outputPath); };
    ReadyToAuthenticate.Wait();

    if (_updateHandler.AuthNeeded)
    {
        await HandleAuthentication(client, logger);
    }

    var currentUser = await GetCurrentUser(client);
    var fullUserName = $"{currentUser.FirstName} {currentUser.LastName}".Trim();
    logger.ZLogInformation($"成功登录为 [[{currentUser.Id}]] / [[@{currentUser.Usernames?.ActiveUsernames[0]}]] / [[{fullUserName}]]");

    var chat = await ResolveChannelLink(client, link, logger);
    var chatId = chat.Id;
    logger.ZLogInformation($"开始下载频道 [{chat.Title}] 的媒体文件，ChatId: {chatId}");

    var channelName = new Uri(link).AbsolutePath.TrimStart('/');
    var channelOutputPath = Path.Combine(outputPath, channelName);
    await InitDatabase(channelOutputPath, logger);

    await DownloadAllMediaFromChannel(client, chatId, limit, delay, channelOutputPath, logger);

    logger.ZLogInformation($"等待所有下载完成...");
    await Task.Delay(10000);

    int completedCount = _downloadTracker.GetCompletedCount();
    int queuedCount = _downloadedFileIds.Count;
    AnsiConsole.WriteLine();
    AnsiConsole.WriteLine("==================");
    AnsiConsole.WriteLine("全部下载完毕！");
    AnsiConsole.WriteLine("已下载文件数: " + queuedCount);
    AnsiConsole.WriteLine("==================");

    AnsiConsole.WriteLine("按 ENTER 键退出应用");
    Console.ReadLine();
}

ILogger InitializeLogger()
{
    var factory = LoggerFactory.Create(logging =>
    {
        logging.SetMinimumLevel(LogLevel.Information);
        logging.AddZLoggerSpectreConsoleAndFile("tdl-channel-download.log");
    });
    return factory.CreateLogger("tdl-channel-download");
}

void InitializeEnvironment(ILogger logger)
{
    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    tdlRoot = Path.Combine(userProfile, ".tdl");

    if (!Directory.Exists(tdlRoot))
    {
        Directory.CreateDirectory(tdlRoot);
        logger.ZLogInformation($"创建数据根目录: {tdlRoot}");
    }
}

async Task InitDatabase(string outputPath, ILogger logger)
{
    Directory.CreateDirectory(outputPath);
    var dbPath = Path.Combine(outputPath, "download_record.db");

    _dbFactory = new DbContextFactory<DownloadDbContext>(() =>
        new DownloadDbContext(dbPath));

    await using var ctx = await _dbFactory.CreateDbContextAsync();
    await ctx.Database.EnsureCreatedAsync();

    var existing = await ctx.DownloadedFiles.Select(f => f.FileId).ToListAsync();
    foreach (var fileId in existing)
    {
        _downloadedFileIds.Add(fileId);
    }

    logger.ZLogInformation($"已加载 {_downloadedFileIds.Count} 条下载记录");
}

async Task RecordDownload(int fileId, long albumId)
{
    await using var ctx = await _dbFactory.CreateDbContextAsync();
    ctx.DownloadedFiles.Add(new DownloadedFile { FileId = fileId, AlbumId = albumId, DownloadedAt = DateTime.UtcNow });
    await ctx.SaveChangesAsync();
}

async Task<TdApi.Chat> ResolveChannelLink(TdClient client, string link, ILogger logger)
{
    var uri = new Uri(link);
    var username = uri.AbsolutePath.TrimStart('/');
    logger.ZLogInformation($"解析频道链接，用户名: @{username}");
    return await client.SearchPublicChatAsync(username);
}

async Task DownloadAllMediaFromChannel(TdClient client, long chatId, int limit, int delay, string outputPath, ILogger logger)
{
    var mediaGroups = new Dictionary<long, List<TdApi.Message>>();
    var standaloneMedia = new List<TdApi.Message>();

    long fromMessageId = 0;
    int fetched = 0;

    logger.ZLogInformation($"开始扫描频道消息{(limit > 0 ? $"（最多 {limit} 条）" : "（全部）")}...");

    while (limit <= 0 || fetched < limit)
    {
        int batchSize = limit > 0 ? Math.Min(limit - fetched, 100) : 100;

        var messages = await client.GetChatHistoryAsync(chatId, fromMessageId, 0, batchSize, false);
        if (messages.Messages_ == null || messages.Messages_.Length == 0)
            break;

        foreach (var msg in messages.Messages_)
        {
            int fileId = GetFileIdFromMessage(msg);
            if (fileId <= 0) continue;

            if (msg.MediaAlbumId != 0)
            {
                if (!mediaGroups.ContainsKey(msg.MediaAlbumId))
                    mediaGroups[msg.MediaAlbumId] = new List<TdApi.Message>();

                if (!mediaGroups[msg.MediaAlbumId].Any(m => m.Id == msg.Id))
                    mediaGroups[msg.MediaAlbumId].Add(msg);
            }
            else
            {
                standaloneMedia.Add(msg);
            }
        }

        fromMessageId = messages.Messages_[^1].Id;
        fetched += messages.Messages_.Length;

        if (messages.Messages_.Length < batchSize)
            break;

        await Task.Delay(300);
    }

    logger.ZLogInformation($"扫描完成，共发现 {mediaGroups.Count} 个媒体组，{standaloneMedia.Count} 个独立媒体");

    int groupIndex = 0;
    int totalGroups = mediaGroups.Count + standaloneMedia.Count;

    foreach (var (albumId, msgs) in mediaGroups.OrderBy(g => g.Value.Min(m => m.Id)))
    {
        groupIndex++;
        logger.ZLogInformation($"[{groupIndex}/{totalGroups}] 下载媒体组 {albumId}（{msgs.Count} 个文件）");

        foreach (var msg in msgs.OrderBy(m => m.Id))
        {
            await DownloadMessageMedia(client, msg, outputPath, albumId, logger);
        }

        if (groupIndex < totalGroups)
        {
            logger.ZLogInformation($"等待 {delay}ms 后继续下一组...");
            await Task.Delay(delay);
        }
    }

    foreach (var msg in standaloneMedia.OrderBy(m => m.Id))
    {
        groupIndex++;
        logger.ZLogInformation($"[{groupIndex}/{totalGroups}] 下载独立媒体（消息 {msg.Id}）");

        await DownloadMessageMedia(client, msg, outputPath, msg.Id, logger);

        if (groupIndex < totalGroups)
        {
            logger.ZLogInformation($"等待 {delay}ms 后继续下一组...");
            await Task.Delay(delay);
        }
    }
}

async Task<int> DownloadMessageMedia(TdClient client, TdApi.Message message, string outputPath, long albumId, ILogger logger)
{
    int fileId = GetFileIdFromMessage(message);
    int downloadedCount = 0;

    if (fileId > 0 && !_downloadedFileIds.Contains(fileId))
    {
        _downloadedFileIds.Add(fileId);
        _fileIdToAlbumId[fileId] = albumId;
        await RecordDownload(fileId, albumId);
        await client.DownloadFileAsync(fileId, 32, 0, 0, true);
        downloadedCount++;
        logger.ZLogInformation($"队列下载: FileId: {fileId}, AlbumId: {albumId}, MediaAlbumId: {message.MediaAlbumId}");
    }
    else if (fileId > 0)
    {
        logger.ZLogInformation($"跳过已下载: FileId: {fileId}");
    }

    return downloadedCount;
}

int GetFileIdFromMessage(TdApi.Message message)
{
    return message.Content switch
    {
        TdApi.MessageContent.MessageDocument d => d.Document.Document_.Id,
        TdApi.MessageContent.MessageVideo v => v.Video.Video_.Id,
        TdApi.MessageContent.MessagePhoto p => p.Photo.Sizes.LastOrDefault()?.Photo.Id ?? 0,
        TdApi.MessageContent.MessageAudio a => a.Audio.Audio_.Id,
        TdApi.MessageContent.MessageAnimation ani => ani.Animation.Animation_.Id,
        TdApi.MessageContent.MessageVideoNote vn => vn.VideoNote.Video.Id,
        TdApi.MessageContent.MessageVoiceNote vce => vce.VoiceNote.Voice.Id,
        _ => 0
    };
}

async Task HandleAuthentication(TdClient client, ILogger logger)
{
    try
    {
        await client.ExecuteAsync(new TdApi.SetAuthenticationPhoneNumber
        {
            PhoneNumber = Environment.GetEnvironmentVariable("tdl_phone", EnvironmentVariableTarget.User)
        });

        Console.Write("输入登录验证码: ");
        var code = Console.ReadLine();

        await client.ExecuteAsync(new TdApi.CheckAuthenticationCode { Code = code });

        if (!_updateHandler.PasswordNeeded) { return; }

        Console.Write("输入密码: ");
        var password = Console.ReadLine();

        await client.ExecuteAsync(new TdApi.CheckAuthenticationPassword { Password = password });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "认证失败");
        throw;
    }
}

async Task<TdApi.User> GetCurrentUser(TdClient client)
{
    return await client.ExecuteAsync(new TdApi.GetMe());
}

async Task ConfigureTdlibParameters(TdClient client, string outputPath, ILogger logger)
{
    await client.ExecuteAsync(new TdApi.SetTdlibParameters
    {
        ApiId = Convert.ToInt32(Environment.GetEnvironmentVariable("tdl_api_id", EnvironmentVariableTarget.User)),
        ApiHash = Environment.GetEnvironmentVariable("tdl_api_hash", EnvironmentVariableTarget.User),
        DeviceModel = "PC",
        SystemLanguageCode = "en",
        ApplicationVersion = "1.0.0",
        DatabaseDirectory = Path.Combine(tdlRoot, "db"),
        FilesDirectory = Path.Combine(outputPath, "files"),
        UseFileDatabase = true,
        UseChatInfoDatabase = true,
        UseMessageDatabase = true,
    });

    logger.ZLogInformation($"正在尝试连接代理...");
    var proxy = await client.AddProxyAsync(new TdApi.Proxy() { Server = "127.0.0.1", Port = 7897, Type = new TdApi.ProxyType.ProxyTypeSocks5() }, true);
    await client.EnableProxyAsync(proxy.Id);
    logger.ZLogInformation($"代理已启用。");
}

async Task HandleFileUpdate(TdApi.File file, string outputPath, ILogger logger)
{
    int fileId = file.Id;

    if (file.Local.IsDownloadingActive)
    {
        if (file.Local.DownloadedSize == 0)
        {
            _downloadTracker.StartDownload(fileId, fileId.ToString(), file.ExpectedSize);
        }
        else
        {
            _downloadTracker.UpdateProgress(fileId, fileId.ToString(), file.Size, file.Local.DownloadedSize);
        }
    }
    else if (file.Local.IsDownloadingCompleted)
    {
        _downloadTracker.CompleteDownload(fileId, fileId.ToString(), file.Size);
        OnDownloadFinished(file, outputPath, logger);
    }
}

void OnDownloadFinished(TdApi.File file, string outputPath, ILogger logger)
{
    string sourcePath = file.Local.Path;
    if (string.IsNullOrEmpty(sourcePath)) return;

    string fileName = Path.GetFileName(sourcePath);

    string albumSubPath;
    if (_fileIdToAlbumId.TryGetValue(file.Id, out long albumId) && albumId != 0)
    {
        albumSubPath = Path.Combine("Downloads", albumId.ToString());
    }
    else
    {
        albumSubPath = "Downloads";
    }

    string targetPath = Path.Combine(outputPath, albumSubPath, fileName);

    try
    {
        if (File.Exists(sourcePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
            File.Move(sourcePath, targetPath, true);
            logger.ZLogInformation(@$"文件已归档至: {sourcePath}  {targetPath}");
        }
        else
        {
            logger.ZLogInformation(@$"文件不存在: {sourcePath} ");
        }
    }
    catch (Exception ex)
    {
        logger.ZLogError(ex, $"处理下载完成的文件时出错");
    }
}

using (var client = new TdClient())
{
    client.Bindings.SetLogVerbosityLevel(TdLogLevel.Fatal);
    await Main(client, args);
}

public class DownloadTracker
{
    private record FileDownloadInfo(long TotalSize, string FileName, long DownloadedSize, double LastSpeed, bool IsCompleted);
    private Dictionary<int, FileDownloadInfo> _downloads = new();
    private object _lock = new();
    private HashSet<int> _completedFiles = [];
    private ProgressContext? _ctx;
    private Dictionary<int, ProgressTask> _tasks = new();
    private bool _running;

    public DownloadTracker()
    {
    }

    void EnsureStarted()
    {
        if (_ctx != null) return;

        _running = true;
        var thread = new Thread(() =>
        {
            AnsiConsole.Progress()
                .AutoRefresh(true)
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new DownloadedColumn(),
                    new TransferSpeedColumn(),
                    new RemainingTimeColumn())
                .Start(ctx =>
                {
                    _ctx = ctx;
                    while (_running)
                    {
                        lock (_lock)
                        {
                            foreach (var (fileId, info) in _downloads)
                            {
                                if (!_tasks.TryGetValue(fileId, out var task))
                                {
                                    task = ctx.AddTask($"[cyan]{fileId}[/]");
                                    task.MaxValue = info.TotalSize > 0 ? info.TotalSize : 1;
                                    task.Value = info.DownloadedSize;
                                    task.StartTask();
                                    _tasks[fileId] = task;
                                }

                                if (info.IsCompleted)
                                {
                                    task.Value = task.MaxValue;
                                    task.Description = $"[green]✓[/] [cyan]{fileId}[/] [green]下载完成[/] \n";
                                    task.StopTask();
                                    AnsiConsole.WriteLine();
                                }
                                else
                                {
                                    double percent = info.TotalSize > 0 ? (double)info.DownloadedSize / info.TotalSize * 100 : 0;
                                    string downloadedStr = FormatSize(info.DownloadedSize);
                                    string totalStr = FormatSize(info.TotalSize);
                                    string speedStr = info.LastSpeed > 0 ? $"{FormatSize((long)info.LastSpeed)}/s" : "";
                                    task.MaxValue = info.TotalSize > 0 ? info.TotalSize : 1;
                                    task.Value = info.DownloadedSize;
                                    task.Description = $"[cyan]{fileId}[/] [[{percent:F1}%]] {downloadedStr} / {totalStr} {speedStr}";
                                }
                            }

                            var completedIds = _downloads.Where(kvp => kvp.Value.IsCompleted).Select(kvp => kvp.Key).ToList();
                            foreach (var id in completedIds)
                            {
                                _downloads.Remove(id);
                                _completedFiles.Add(id);
                            }
                        }

                        Thread.Sleep(100);
                    }
                });
        })
        { IsBackground = true };
        thread.Start();
    }

    public void StartDownload(int fileId, string fileName, long totalSize)
    {
        EnsureStarted();
        lock (_lock)
        {
            _downloads[fileId] = new FileDownloadInfo(totalSize, fileName, 0, 0, false);
        }
    }

    public void UpdateProgress(int fileId, string fileName, long totalSize, long downloadedSize)
    {
        lock (_lock)
        {
            if (_downloads.TryGetValue(fileId, out var info))
            {
                double speed = 0;
                if (info.DownloadedSize > 0 && info.DownloadedSize != downloadedSize)
                {
                    speed = (downloadedSize - info.DownloadedSize) / 0.1;
                }
                _downloads[fileId] = info with { DownloadedSize = downloadedSize, TotalSize = totalSize, LastSpeed = speed };
            }
        }
    }

    public void CompleteDownload(int fileId, string fileName, long totalSize)
    {
        lock (_lock)
        {
            if (_downloads.TryGetValue(fileId, out var info))
            {
                _downloads[fileId] = info with { IsCompleted = true, DownloadedSize = totalSize };
            }
        }
    }

    public int GetCompletedCount()
    {
        lock (_lock)
        {
            return _completedFiles.Count;
        }
    }

    static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return $"{size:F2} {units[unitIndex]}";
    }
}

public class DownloadedFile
{
    public int Id { get; set; }
    public int FileId { get; set; }
    public long AlbumId { get; set; }
    public DateTime DownloadedAt { get; set; }
}

public class DownloadDbContext : DbContext
{
    private readonly string _dbPath;

    public DownloadDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    public DbSet<DownloadedFile> DownloadedFiles => Set<DownloadedFile>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DownloadedFile>(entity =>
        {
            entity.HasIndex(e => e.FileId).IsUnique();
        });
    }
}

public class DbContextFactory<T> : IDbContextFactory<T> where T : DbContext
{
    private readonly Func<T> _factory;

    public DbContextFactory(Func<T> factory)
    {
        _factory = factory;
    }

    public T CreateDbContext() => _factory();
}
