#!/usr/bin/env dotnet

#:include ../../env.cs
#:include TdlUpdateHandler.cs
#:include TdlEnv.cs
#:include TdlDownloadTracker.cs

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

string tdlRoot = string.Empty;
DownloadTracker _downloadTracker = new();
HashSet<int> _downloadedFileIds = [];
Dictionary<int, long> _fileIdToAlbumId = [];
TdlEnv _env = null!;
IDbContextFactory<DownloadDbContext> _dbFactory = null!;

async Task Main(TdClient client, string[] args)
{
    var logger = TdlEnv.CreateLogger("tdl-channel-download.log", "tdl-channel-download");

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

    tdlRoot = TdlEnv.InitTdlRoot(logger);

    _env = new TdlEnv(client, logger, filesDir: outputPath, onFileUpdate: HandleFileUpdate);
    _env.WaitReady();

    if (_env.AuthNeeded)
    {
        await _env.AuthenticateAsync();
    }

    var currentUser = await _env.GetCurrentUserAsync();
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
            int fileId = TdlMediaHelper.GetFileIdFromMessage(msg);
            if (fileId <= 0) continue;

            if (msg.MediaAlbumId != 0)
            {
                if (!mediaGroups.ContainsKey(msg.MediaAlbumId))
                    mediaGroups[msg.MediaAlbumId] = [];

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
    int fileId = TdlMediaHelper.GetFileIdFromMessage(message);
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

async Task HandleFileUpdate(TdApi.File file, string outputPath, ILogger cbLogger)
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
        TdlMediaHelper.OnDownloadFinished(file, _fileIdToAlbumId, outputPath, cbLogger);
    }
}

using (var client = new TdClient())
{
    client.Bindings.SetLogVerbosityLevel(TdLogLevel.Fatal);
    await Main(client, args);
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
