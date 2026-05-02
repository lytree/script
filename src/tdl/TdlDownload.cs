#!/usr/bin/env dotnet
#:package TDLib@*
#:package tdlib.native@*
#:package tdlib.native.win-x64@*
#:package System.CommandLine@*
#:package Spectre.Console@0.55.2
#:package Spectre.Console.Ansi@0.55.2
#:package Microsoft.Extensions.Logging@*
#:package ZLogger@*
#:package YLFramework.ZLogging@1.0.3-alpha.6
#:include TdlUpdateHandler.cs

using System;
using System.CommandLine;
using System.IO;
using System.Threading;
using Framework.ZLogging;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using TdLib;
using TdLib.Bindings;
using ZLogger;

// 全局变量
ManualResetEventSlim ReadyToAuthenticate = new();
string tdlRoot = string.Empty;
DownloadProgressBarManager _progressBarManager;
TdlUpdateHandler _updateHandler;

// 主函数
async Task Main(TdClient client, string[] args)
{
    // 初始化进度条管理器
    _progressBarManager = new DownloadProgressBarManager();
    
    // 初始化日志
    var logger = InitializeLogger();

    // 解析命令行参数
    var optionOutput = new Option<string?>("--output") { DefaultValueFactory = (res) => Path.Combine(Environment.CurrentDirectory, "data") };
    var optionsUrls = new Option<string[]>("--urls")
    {
        Required = true,
        DefaultValueFactory = (res) => ["https://t.me/atsJoe/19342"]
    };
    var rootCommand = new RootCommand { optionOutput, optionsUrls };
    var parseResult = rootCommand.Parse(args);
    var outputPath = parseResult.GetValue(optionOutput);

    // 初始化全局环境变量
    InitializeEnvironment(logger);

    // 下载文件
    await DownloadFiles(client, parseResult, optionsUrls, outputPath, logger);

    logger.WriteLine("Press ENTER to exit from application");
    Console.ReadLine();
}

// 初始化日志
ILogger InitializeLogger()
{
    var factory = LoggerFactory.Create(logging =>
    {
        logging.SetMinimumLevel(LogLevel.Trace);
        logging.AddZLoggerSpectreConsoleAndFile("tdl.log");
    });
    return factory.CreateLogger("tdl");
}

// 初始化环境
void InitializeEnvironment(ILogger logger)
{
    // 获取用户主目录，例如 C:\Users\Administrator
    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // 拼接 .tdl 目录
    tdlRoot = Path.Combine(userProfile, ".tdl");

    // 确保目录存在
    if (!Directory.Exists(tdlRoot))
    {
        Directory.CreateDirectory(tdlRoot);
        logger.ZLogInformation($"创建数据根目录: {tdlRoot}");
    }
}

// 下载文件
async Task DownloadFiles(TdClient client, ParseResult parseResult, Option<string[]> optionsUrls, string outputPath, ILogger logger)
{
    try
    {
        _updateHandler = new TdlUpdateHandler(ReadyToAuthenticate, logger)
            .OnConfigureTdlibParameters(ConfigureTdlibParameters)
            .OnFileUpdate(HandleFileUpdate);

        client.UpdateReceived += async (_, update) => { await _updateHandler.ProcessUpdates(client, update, outputPath); };

        // Waiting until we get enough events to be in 'authentication ready' state
        ReadyToAuthenticate.Wait();

        // We may not need to authenticate since TdLib persists session in 'td.binlog' file.
        if (_updateHandler.AuthNeeded)
        {
            // Interactively handling authentication
            await HandleAuthentication(client, logger);
        }

        // Querying info about current user
        var currentUser = await GetCurrentUser(client);
        var fullUserName = $"{currentUser.FirstName} {currentUser.LastName}".Trim();
        logger.ZLogInformation($"成功登录为 [[{currentUser.Id}]] / [[@{currentUser.Usernames?.ActiveUsernames[0]}]] / [[{fullUserName}]]");

        // 处理每个 URL
        foreach (var url in parseResult.GetValue(optionsUrls))
        {
            var fileId = await GetFileIdFromLinkAsync(client, url, logger);
            if (fileId > 0)
            {
                await client.DownloadFileAsync(fileId, 12, 0, 0, false);
            }
        }
    }
    catch (Exception e)
    {
        logger.LogError(e, "An error occurred during download");
    }
}

// 处理认证
async Task HandleAuthentication(TdClient client, ILogger logger)
{
    try
    {
        // Setting phone number
        await client.ExecuteAsync(new TdApi.SetAuthenticationPhoneNumber
        {
            PhoneNumber = Environment.GetEnvironmentVariable("tdl_phone", EnvironmentVariableTarget.User)
        });

        // Telegram servers will send code to us
        Console.Write("Insert the login code: ");
        var code = Console.ReadLine();

        await client.ExecuteAsync(new TdApi.CheckAuthenticationCode
        {
            Code = code
        });

        if (!_updateHandler.PasswordNeeded) { return; }

        // 2FA may be enabled. Cloud password is required in that case.
        Console.Write("Insert the password: ");
        var password = Console.ReadLine();

        await client.ExecuteAsync(new TdApi.CheckAuthenticationPassword
        {
            Password = password
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Authentication failed");
        throw;
    }
}

// 获取当前用户信息
async Task<TdApi.User> GetCurrentUser(TdClient client)
{
    return await client.ExecuteAsync(new TdApi.GetMe());
}

// 配置 TDLib参数
async Task ConfigureTdlibParameters(TdClient client, string outputPath, ILogger logger)
{
    // TdLib creates database in the current directory.
    // so create separate directory and switch to that dir.
    await client.ExecuteAsync(new TdApi.SetTdlibParameters
    {
        ApiId = Convert.ToInt32(Environment.GetEnvironmentVariable("tdl_api_id", EnvironmentVariableTarget.User)),
        ApiHash = Environment.GetEnvironmentVariable("tdl_api_hash", EnvironmentVariableTarget.User),
        DeviceModel = "PC",
        SystemLanguageCode = "en",
        ApplicationVersion = "1.0.0",
        // 数据库放在用户目录下的 .tdl/db
        DatabaseDirectory = Path.Combine(tdlRoot, "db"),
        // 下载的文件放在用户目录下的 .tdl/files
        FilesDirectory = Path.Combine(outputPath, "files"),
        UseFileDatabase = true,
        UseChatInfoDatabase = true,
        UseMessageDatabase = true,
    });

    logger.ZLogInformation($"正在尝试连接代理...");
    // 参数说明：服务器地址, 端口, 是否启用
    var proxy = await client.AddProxyAsync(new TdApi.Proxy() { Server = "127.0.0.1", Port = 7897, Type = new TdApi.ProxyType.ProxyTypeSocks5() }, true);
    // 启用该代理
    await client.EnableProxyAsync(proxy.Id);
    logger.ZLogInformation($"代理已启用。");
}



// 处理文件更新
async Task HandleFileUpdate(TdApi.File file, string outputPath, ILogger logger)
{
    // 将文件 ID 转换为字符串作为键
    string fileKey = file.Id.ToString();
    
    if (file.Local.IsDownloadingActive)
    {
        // 更新下载进度
        _progressBarManager.UpdateProgress(fileKey, file.Local.DownloadedSize);
        
        // 如果是第一次更新，启动进度条
        if (file.Local.DownloadedSize == 0)
        {
            AnsiConsole.WriteLine($"开始下载文件: {file.Id}");
            _progressBarManager.StartProgressBar(fileKey, file.ExpectedSize);
        }
    }
    else if (file.Local.IsDownloadingCompleted)
    {
        // 下载完成，Path 就是磁盘上的绝对路径
        logger.ZLogInformation($"文件下载完成！本地路径: {file.Local.Path}");

        // 清理进度条
        _progressBarManager.CompleteDownload(fileKey);

        // 触发文件下载完成后的处理
        // OnDownloadFinished(file, logger);
    }
}

// 处理下载完成的文件
void OnDownloadFinished(TdApi.File file, ILogger logger)
{
    string sourcePath = file.Local.Path;
    string fileName = Path.GetFileName(sourcePath);
    string targetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonVideos), "Downloads", fileName);

    try
    {
        // 确保目标目录存在
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
        // 移动或复制文件到你的业务文件夹
        File.Copy(sourcePath, targetPath, true);
        logger.ZLogInformation($"【业务处理】文件已归档至: {targetPath}");
    }
    catch (Exception ex)
    {
        logger.ZLogError(ex, $"处理下载完成的文件时出错");
    }
}



/// <summary>
/// 解析 Telegram 链接并提取其中的核心 FileId
/// </summary>
/// <param name="link">例如 https://t.me/R_E_STUDIO/21221</param>
/// <returns>返回 FileId，如果未找到则返回 0</returns>
async Task<int> GetFileIdFromLinkAsync(TdClient client, string link, ILogger logger)
{
    try
    {
        // 1. 调用 TDLib 内置的链接解析器
        var linkInfo = await client.GetMessageLinkInfoAsync(link);

        if (linkInfo.Message == null)
        {
            logger.ZLogWarning($"链接解析成功，但未找到对应的消息内容: {link}");
            return 0;
        }

        var message = linkInfo.Message;

        // 2. 提取 FileId (根据内容类型)
        int fileId = message.Content switch
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

        if (fileId == 0)
        {
            logger.ZLogWarning($"消息 ID {message.Id} 中不包含可下载的文件。类型: {message.Content.GetType().Name}");
        }

        return fileId;
    }
    catch (TdException ex)
    {
        logger.ZLogError(ex, $"解析链接时发生 TDLib 错误: {link}");
        return 0;
    }
}

// 主程序入口
using (var client = new TdClient())
{
    client.Bindings.SetLogVerbosityLevel(TdLogLevel.Fatal);
    await Main(client, args);
}

// 下载进度条管理器类
public class DownloadProgressBarManager
{
    // 存储进度条任务
    private Dictionary<string, ProgressTask> _progressBars = new Dictionary<string, ProgressTask>();
    
    // 存储文件下载信息
    private Dictionary<string, long> _fileDownloadedSize = new Dictionary<string, long>();
    private Dictionary<string, long> _fileExpectedSize = new Dictionary<string, long>();
    
    // 线程安全锁
    private object _progressLock = new object();
    
    /// <summary>
    /// 启动进度条显示
    /// </summary>
    /// <param name="key">文件标识（字符串形式）</param>
    /// <param name="fileSize">文件大小（字节）</param>
    /// <param name="description">进度条描述</param>
    public void StartProgressBar(string key, long fileSize, string description = null)
    {
        // 存储文件大小信息
        lock (_progressLock)
        {
            _fileExpectedSize[key] = fileSize;
        }
        
        Task.Run(() =>
        {
            // 配置并启动进度条
            ConfigureAndStartProgressBar(key, fileSize, description ?? $"下载文件 {key}");
        });
    }
    
    /// <summary>
    /// 更新下载进度
    /// </summary>
    /// <param name="key">文件标识（字符串形式）</param>
    /// <param name="downloadedSize">已下载大小（字节）</param>
    public void UpdateProgress(string key, long downloadedSize)
    {
        lock (_progressLock)
        {
            _fileDownloadedSize[key] = downloadedSize;
        }
    }
    
    /// <summary>
    /// 完成下载
    /// </summary>
    /// <param name="key">文件标识（字符串形式）</param>
    public void CompleteDownload(string key)
    {
        lock (_progressLock)
        {
            if (_fileDownloadedSize.ContainsKey(key))
            {
                _fileDownloadedSize.Remove(key);
            }
            if (_fileExpectedSize.ContainsKey(key))
            {
                _fileExpectedSize.Remove(key);
            }
        }
    }
    
    /// <summary>
    /// 配置并启动进度条
    /// </summary>
    private void ConfigureAndStartProgressBar(string key, long fileSize, string description)
    {
        AnsiConsole.Progress()
            .AutoRefresh(true)
            .AutoClear(false)
            .HideCompleted(true)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new DownloadedColumn(),
                new TransferSpeedColumn(),
                new RemainingTimeColumn()
            )
            .Start(ctx =>
            {
                var progressTask = ctx.AddTask(description, maxValue: fileSize);
                
                lock (_progressLock)
                {
                    _progressBars[key] = progressTask;
                }
                
                // 持续更新进度直到下载完成
                UpdateProgressBarUntilComplete(key, progressTask);
                
                // 完成后清理
                CleanupProgressBar(key);
            });
    }
    
    /// <summary>
    /// 持续更新进度条直到下载完成
    /// </summary>
    private void UpdateProgressBarUntilComplete(string key, ProgressTask progressTask)
    {
        while (true)
        {
            long downloadedSize;
            bool fileExists;
            
            lock (_progressLock)
            {
                fileExists = _fileDownloadedSize.ContainsKey(key);
                downloadedSize = fileExists ? _fileDownloadedSize[key] : 0;
            }
            
            if (!fileExists)
            {
                break;
            }
            
            // 更新进度条值（使用已下载大小）
            progressTask.Value = downloadedSize;
            
            Thread.Sleep(100); // 避免过于频繁的更新
        }
    }
    
    /// <summary>
    /// 清理进度条资源
    /// </summary>
    private void CleanupProgressBar(string key)
    {
        lock (_progressLock)
        {
            if (_progressBars.ContainsKey(key))
            {
                _progressBars.Remove(key);
            }
        }
    }
}