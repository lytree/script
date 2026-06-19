using System.Text.RegularExpressions;
using Framework.ZLogging;
using Microsoft.Extensions.Logging;
using TdLib;
using ZLogger;

/// <summary>
/// 共享的 Telegram 环境初始化与登录辅助类。
/// 封装 TDLib 参数配置、代理连接、认证、聊天解析等通用逻辑。
/// </summary>
public class TdlEnv
{
    readonly TdClient _client;
    readonly ILogger _logger;
    readonly string _tdlRoot;
    readonly string _filesDir;
    readonly TdlUpdateHandler _updateHandler;
    readonly ManualResetEventSlim _ready;

    /// <summary>
    /// QR 码登录时收到的 tg://login?token=xxx 链接。
    /// </summary>
    public string? QrCodeLink { get; private set; }

    public bool AuthNeeded => _updateHandler.AuthNeeded;
    public bool PasswordNeeded => _updateHandler.PasswordNeeded;
    public string TdlRoot => _tdlRoot;
    public TdlUpdateHandler UpdateHandler => _updateHandler;

    /// <param name="client">TDLib 客户端实例</param>
    /// <param name="logger">日志记录器</param>
    /// <param name="filesDir">文件存储目录 (用于 FilesDirectory 和文件更新回调)</param>
    /// <param name="onFileUpdate">可选的文件更新回调</param>
    public TdlEnv(
        TdClient client,
        ILogger logger,
        string? filesDir = null,
        Func<TdApi.File, string, ILogger, Task>? onFileUpdate = null)
    {
        _client = client;
        _logger = logger;
        _tdlRoot = InitTdlRoot(logger);
        _filesDir = filesDir ?? _tdlRoot;

        _ready = new ManualResetEventSlim();

        _updateHandler = new TdlUpdateHandler(_ready, logger)
            .OnConfigureTdlibParameters(ConfigureTdlibParameters)
            .OnAuthWaitOtherDeviceConfirmation(OnQrCodeReceived);

        if (onFileUpdate != null)
        {
            _updateHandler = _updateHandler.OnFileUpdate(onFileUpdate);
        }

        _client.UpdateReceived += async (_, update) =>
        {
            await _updateHandler.ProcessUpdates(_client, update, _filesDir);
        };
    }

    /// <summary>
    /// 等待 TDLib 初始化完成。
    /// </summary>
    public void WaitReady() => _ready.Wait();

    /// <summary>
    /// 默认登录认证。优先使用环境变量 tdl_phone 的手机号登录，否则使用 QR 码登录。
    /// </summary>
    public async Task AuthenticateAsync()
    {
        var phone = Environment.GetEnvironmentVariable("tdl_phone", EnvironmentVariableTarget.User);
        var botToken = Environment.GetEnvironmentVariable("tdl_bot_token", EnvironmentVariableTarget.User);

        if (!string.IsNullOrWhiteSpace(botToken))
        {
            await AuthenticateWithBotTokenAsync(botToken);
            return;
        }

        if (!string.IsNullOrWhiteSpace(phone))
        {
            await AuthenticateWithPhoneAsync(phone);
            return;
        }

        await AuthenticateWithQrCodeAsync();
    }

    /// <summary>
    /// 使用手机号 + 验证码 + 可选两步验证密码登录。
    /// </summary>
    /// <param name="phoneNumber">手机号 (国际格式，如 +8613800138000)。为空时从 tdl_phone 环境变量读取。</param>
    public async Task AuthenticateWithPhoneAsync(string? phoneNumber = null)
    {
        phoneNumber ??= Environment.GetEnvironmentVariable("tdl_phone", EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            Console.Write("输入手机号 (国际格式，如 +8613800138000): ");
            phoneNumber = Console.ReadLine();
        }

        _logger.ZLogInformation($"正在使用手机号登录: {phoneNumber}");
        await _client.ExecuteAsync(new TdApi.SetAuthenticationPhoneNumber
        {
            PhoneNumber = phoneNumber
        });

        Console.Write("输入登录验证码: ");
        var code = Console.ReadLine();
        await _client.ExecuteAsync(new TdApi.CheckAuthenticationCode { Code = code });

        if (!PasswordNeeded) { return; }

        Console.Write("输入两步验证密码: ");
        var password = ReadPassword();
        await _client.ExecuteAsync(new TdApi.CheckAuthenticationPassword { Password = password });
    }

    /// <summary>
    /// 使用 Bot Token 登录。
    /// </summary>
    /// <param name="botToken">Bot Token (从 @BotFather 获取)。为空时从 tdl_bot_token 环境变量读取。</param>
    public async Task AuthenticateWithBotTokenAsync(string? botToken = null)
    {
        botToken ??= Environment.GetEnvironmentVariable("tdl_bot_token", EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(botToken))
        {
            Console.Write("输入 Bot Token: ");
            botToken = Console.ReadLine();
        }

        _logger.ZLogInformation($"正在使用 Bot Token 登录...");
        await _client.ExecuteAsync(new TdApi.CheckAuthenticationBotToken
        {
            Token = botToken
        });
    }

    /// <summary>
    /// 使用 QR 码登录。需要在已登录的 Telegram 客户端扫描二维码。
    /// </summary>
    public async Task AuthenticateWithQrCodeAsync()
    {
        _logger.ZLogInformation($"正在请求 QR 码登录...");

        await _client.ExecuteAsync(new TdApi.RequestQrCodeAuthentication());

        // 等待 QR 链接到达
        var timeout = TimeSpan.FromSeconds(30);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (string.IsNullOrEmpty(QrCodeLink) && sw.Elapsed < timeout)
        {
            await Task.Delay(200);
        }

        if (string.IsNullOrEmpty(QrCodeLink))
        {
            _logger.ZLogError($"未收到 QR 码数据，请重试");
            return;
        }

        _logger.ZLogInformation($"QR 码登录链接: {QrCodeLink}");
        Console.WriteLine();
        Console.WriteLine("请在已登录的 Telegram 客户端中扫描以下二维码，或手动输入链接:");
        Console.WriteLine($"  {QrCodeLink}");
        Console.WriteLine();
        PrintQrCode(QrCodeLink);
        Console.WriteLine();
        Console.WriteLine("等待扫码确认... (在手机 Telegram: 设置 -> 设备 -> 扫描二维码)");

        // 等待授权完成 (AuthorizationStateReady 会由 UpdateHandler 处理)
        // 这里等待直到不再需要认证
        var readyTimeout = TimeSpan.FromMinutes(5);
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        while (AuthNeeded && sw2.Elapsed < readyTimeout)
        {
            await Task.Delay(500);
        }

        if (AuthNeeded)
        {
            _logger.ZLogWarning($"QR 码登录超时");
        }
        else
        {
            _logger.ZLogInformation($"QR 码登录成功");
        }
    }

    /// <summary>
    /// 使用邮箱地址登录 (用于需要邮箱验证的账户)。
    /// </summary>
    public async Task AuthenticateWithEmailAsync(string? emailAddress = null)
    {
        if (string.IsNullOrWhiteSpace(emailAddress))
        {
            Console.Write("输入邮箱地址: ");
            emailAddress = Console.ReadLine();
        }

        _logger.ZLogInformation($"正在使用邮箱登录: {emailAddress}");
        await _client.ExecuteAsync(new TdApi.SetAuthenticationEmailAddress
        {
            EmailAddress = emailAddress
        });

        Console.Write("输入邮箱验证码: ");
        var code = Console.ReadLine();
        await _client.ExecuteAsync(new TdApi.CheckAuthenticationEmailCode
        {
            Code = new TdApi.EmailCodeAuthenticationCode { Code = code }
        });

        if (!PasswordNeeded) { return; }

        Console.Write("输入两步验证密码: ");
        var password = ReadPassword();
        await _client.ExecuteAsync(new TdApi.CheckAuthenticationPassword { Password = password });
    }

    /// <summary>
    /// 获取当前登录用户。
    /// </summary>
    public async Task<TdApi.User> GetCurrentUserAsync()
    {
        return await _client.ExecuteAsync(new TdApi.GetMe());
    }

    /// <summary>
    /// 解析聊天链接/用户名/ID 为 ChatId。
    /// 支持: t.me 链接、邀请链接、@username、纯数字 ID、标题模糊匹配。
    /// </summary>
    public async Task<long> ResolveChatIdAsync(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return 0;

        // 1. 尝试消息链接
        try
        {
            var linkInfo = await _client.GetMessageLinkInfoAsync(link);
            if (linkInfo.Message != null)
            {
                return linkInfo.Message.ChatId;
            }
        }
        catch (TdException) { }

        // 2. 尝试邀请链接
        try
        {
            if (IsInviteLink(link))
            {
                var inviteInfo = await _client.CheckChatInviteLinkAsync(link);
                if (inviteInfo.ChatId != 0)
                {
                    _logger.ZLogInformation($"邀请链接已关联到 ChatId: {inviteInfo.ChatId}");
                    return inviteInfo.ChatId;
                }
                _logger.ZLogWarning($"邀请链接有效但未关联到已有聊天，可能需要先加入: {link}");
                return 0;
            }
        }
        catch (TdException ex)
        {
            _logger.ZLogError(ex, $"无法解析邀请链接: {link}");
            return 0;
        }

        // 3. 尝试用户名搜索
        try
        {
            var username = ExtractUsername(link);
            if (!string.IsNullOrEmpty(username))
            {
                var chat = await _client.SearchPublicChatAsync(username);
                if (chat != null)
                {
                    return chat.Id;
                }
            }
        }
        catch (TdException) { }

        // 4. 尝试纯数字 ID
        if (long.TryParse(link.Trim(), out long chatId))
        {
            return chatId;
        }

        // 5. 在聊天列表中模糊匹配标题
        try
        {
            var foundChatId = await SearchChatByTitleAsync(link);
            if (foundChatId != 0)
            {
                return foundChatId;
            }
        }
        catch (TdException) { }

        _logger.ZLogWarning($"目标链接未关联到聊天: {link}");
        return 0;
    }

    // ──────────────────────────────────────────────
    //  静态工厂方法
    // ──────────────────────────────────────────────

    /// <summary>
    /// 创建日志记录器，同时输出到 Spectre.Console 和文件。
    /// </summary>
    public static ILogger CreateLogger(string logFileName, string loggerName)
    {
        var factory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddZLoggerSpectreConsoleAndFile(logFileName);
        });
        return factory.CreateLogger(loggerName);
    }

    /// <summary>
    /// 初始化 ~/.tdl 根目录。
    /// </summary>
    public static string InitTdlRoot(ILogger logger)
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string tdlRoot = Path.Combine(userProfile, ".tdl");

        if (!Directory.Exists(tdlRoot))
        {
            Directory.CreateDirectory(tdlRoot);
            logger.ZLogInformation($"创建数据根目录: {tdlRoot}");
        }

        return tdlRoot;
    }

    /// <summary>
    /// 从 TdException 中解析 "Too Many Requests" 的重试等待秒数。
    /// </summary>
    public static int ParseRetryAfter(TdException ex)
    {
        if (ex.Error?.Message != null)
        {
            var match = Regex.Match(ex.Error.Message, @"(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int seconds) && seconds > 0)
            {
                return Math.Min(seconds + 2, 300);
            }
        }
        return 15;
    }

    // ──────────────────────────────────────────────
    //  私有方法
    // ──────────────────────────────────────────────

    void OnQrCodeReceived(string link)
    {
        QrCodeLink = link;
        _logger.ZLogInformation($"已收到 QR 码登录链接");
    }

    /// <summary>
    /// 安全读取密码 (不回显)。
    /// </summary>
    static string ReadPassword()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }
            if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
            {
                sb.Remove(sb.Length - 1, 1);
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
                Console.Write("*");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 在终端显示 QR 码登录链接。
    /// </summary>
    static void PrintQrCode(string data)
    {
        Console.WriteLine("┌──────────────────────────────────┐");
        Console.WriteLine("│  请在手机 Telegram 扫描或输入链接  │");
        Console.WriteLine("└──────────────────────────────────┘");
        Console.WriteLine();
        Console.WriteLine($"  {data}");
        Console.WriteLine();
        Console.WriteLine("  操作步骤:");
        Console.WriteLine("  1. 打开手机 Telegram");
        Console.WriteLine("  2. 设置 -> 设备 -> 扫描二维码");
        Console.WriteLine("  3. 扫描上方链接或手动输入");
    }

    async Task ConfigureTdlibParameters(TdClient client, string outputPath, ILogger cbLogger)
    {
        await client.ExecuteAsync(new TdApi.SetTdlibParameters
        {
            ApiId = Convert.ToInt32(Environment.GetEnvironmentVariable("tdl_api_id", EnvironmentVariableTarget.User)),
            ApiHash = Environment.GetEnvironmentVariable("tdl_api_hash", EnvironmentVariableTarget.User),
            DeviceModel = "PC",
            SystemLanguageCode = "en",
            ApplicationVersion = "1.0.0",
            DatabaseDirectory = Path.Combine(_tdlRoot, "db"),
            FilesDirectory = Path.Combine(_filesDir, "files"),
            UseFileDatabase = true,
            UseChatInfoDatabase = true,
            UseMessageDatabase = true,
        });

        cbLogger.ZLogInformation($"正在尝试连接代理...");
        var proxy = await client.AddProxyAsync(
            new TdApi.Proxy { Server = "127.0.0.1", Port = 7897, Type = new TdApi.ProxyType.ProxyTypeSocks5() },
            true);
        await client.EnableProxyAsync(proxy.Id);
        cbLogger.ZLogInformation($"代理已启用。");
    }

    async Task<long> SearchChatByTitleAsync(string keyword)
    {
        _logger.ZLogInformation($"在聊天列表中搜索: {keyword}");
        var chatIds = await _client.GetChatsAsync(limit: 200);
        if (chatIds?.ChatIds == null) return 0;

        foreach (var id in chatIds.ChatIds)
        {
            try
            {
                var chat = await _client.GetChatAsync(id);
                if (chat.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.ZLogInformation($"找到匹配聊天: [{chat.Title}] ChatId={chat.Id}");
                    return chat.Id;
                }
            }
            catch { }
        }

        return 0;
    }

    static bool IsInviteLink(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        input = input.Trim();
        if (input.StartsWith("https://t.me/+", StringComparison.OrdinalIgnoreCase)) return true;
        if (input.StartsWith("https://t.me/joinchat/", StringComparison.OrdinalIgnoreCase)) return true;
        if (input.StartsWith("https://telegram.me/+", StringComparison.OrdinalIgnoreCase)) return true;
        if (input.StartsWith("https://telegram.me/joinchat/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static string? ExtractUsername(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim();
        if (input.StartsWith("@")) return input.Substring(1);
        if (!input.Contains("/")) return null;

        var match = Regex.Match(input,
            @"(?:https?:\/\/)?(?:t\.me|telegram\.me)\/(?<name>[^\/\?\#]+)",
            RegexOptions.IgnoreCase);

        if (!match.Success) return null;
        var name = match.Groups["name"].Value;
        if (name.StartsWith("+")) return null;
        return name;
    }
}
