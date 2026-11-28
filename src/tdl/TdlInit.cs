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
#:package YLFramework.ZLogging@2.0.0-alpha.1

// ============================================================================
// TdlInit.cs — Telegram (TDLib) 运行环境初始化与登录脚本
//
// 功能:
//   1. 检查并补齐 tdl 系列脚本依赖的环境变量 (用户级持久化):
//        tdl_api_id / tdl_api_hash  (必填, 从 https://my.telegram.org 获取)
//        tdl_phone / tdl_bot_token  (登录方式, 二选一)
//        tdl_proxy                  (代理, 格式 host:port, 默认 127.0.0.1:7897)
//        tdl_use_message_db         (可选, 是否启用本地消息库)
//   2. 初始化 TDLib 会话 (~/.tdl), 完成登录并验证。
// api_id

// 用法:
//   dotnet run src/tdl/TdlInit.cs -- [选项]
//
// 示例:
//   dotnet run src/tdl/TdlInit.cs                          # 交互式初始化并登录
//   dotnet run src/tdl/TdlInit.cs -- --check               # 只检查环境, 不修改
//   dotnet run src/tdl/TdlInit.cs -- --api-id 1234 --api-hash abcdef
//   dotnet run src/tdl/TdlInit.cs -- --phone +8613800138000
//   dotnet run src/tdl/TdlInit.cs -- --proxy 127.0.0.1:7890
// ============================================================================

using System.CommandLine;
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

async Task<int> Main(TdClient client, string[] args)
{
    var logger = TdlEnv.CreateLogger("tdl-init.log", "tdl-init");

    var optionCheck = new Option<bool>("--check") { Description = "只检查当前环境与登录状态, 不修改配置" };
    var optionApiId = new Option<int?>("--api-id") { Description = "Telegram API ID (保存为用户级环境变量 tdl_api_id)" };
    var optionApiHash = new Option<string?>("--api-hash") { Description = "Telegram API Hash (保存为 tdl_api_hash)" };
    var optionPhone = new Option<string?>("--phone") { Description = "手机号 (国际格式, 保存为 tdl_phone)" };
    var optionBotToken = new Option<string?>("--bot-token") { Description = "Bot Token (保存为 tdl_bot_token)" };
    var optionProxy = new Option<string?>("--proxy") { Description = "代理地址 host:port (保存为 tdl_proxy)" };
    var optionMessageDb = new Option<bool?>("--use-message-db") { Description = "启用本地消息库 (保存为 tdl_use_message_db=1)" };
    var optionQr = new Option<bool>("--qr") { Description = "本次登录强制使用 QR 码 (不影响已保存的配置)" };

    var rootCommand = new RootCommand("初始化 Telegram (TDLib) 运行环境并登录");
    rootCommand.Options.Add(optionCheck);
    rootCommand.Options.Add(optionApiId);
    rootCommand.Options.Add(optionApiHash);
    rootCommand.Options.Add(optionPhone);
    rootCommand.Options.Add(optionBotToken);
    rootCommand.Options.Add(optionProxy);
    rootCommand.Options.Add(optionMessageDb);
    rootCommand.Options.Add(optionQr);

    var parseResult = rootCommand.Parse(args);
    if (parseResult.Errors.Count > 0)
    {
        foreach (var err in parseResult.Errors)
        {
            Console.Error.WriteLine(err.Message);
        }
        rootCommand.Parse("--help").Invoke();
        return 2;
    }

    var checkOnly = parseResult.GetValue(optionCheck);
    var apiIdArg = parseResult.GetValue(optionApiId);
    var apiHashArg = parseResult.GetValue(optionApiHash);
    var phoneArg = parseResult.GetValue(optionPhone);
    var botTokenArg = parseResult.GetValue(optionBotToken);
    var proxyArg = parseResult.GetValue(optionProxy);
    var messageDbArg = parseResult.GetValue(optionMessageDb);
    var useQr = parseResult.GetValue(optionQr);

    try
    {
        // ──────────────────────────────────────────────
        // 1. 环境变量检查与补齐
        // ──────────────────────────────────────────────
        var (apiId, apiHash) = await EnsureApiCredentialsAsync(logger, checkOnly, apiIdArg, apiHashArg);
        var authMethod = await EnsureAuthMethodAsync(logger, checkOnly, phoneArg, botTokenArg, useQr);
        var (proxyHost, proxyPort) = EnsureProxyAsync(logger, checkOnly, proxyArg);
        var useMessageDb = EnsureMessageDbAsync(logger, checkOnly, messageDbArg);

        PrintConfigSummary(apiId, apiHash, authMethod, proxyHost, proxyPort, useMessageDb);

        if (checkOnly)
        {
            logger.ZLogInformation($"检查模式结束, 未做任何修改");
            return 0;
        }

        // ──────────────────────────────────────────────
        // 2. 初始化 TDLib 会话并登录
        // ──────────────────────────────────────────────
        logger.ZLogInformation($"正在初始化 TDLib 会话...");
        var env = new TdlEnv(client, logger);
        env.WaitReady();

        if (env.AuthNeeded)
        {
            if (authMethod == "qr")
            {
                await env.AuthenticateWithQrCodeAsync();
            }
            else
            {
                await env.AuthenticateAsync();
            }
        }

        // 等待授权状态落定 (最多 10 秒)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (env.AuthNeeded && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(200);
        }

        if (env.AuthNeeded)
        {
            logger.ZLogError($"登录未完成, 当前状态: {env.UpdateHandler.CurrentState}");
            return 1;
        }

        var me = await env.GetCurrentUserAsync();
        var fullName = $"{me.FirstName} {me.LastName}".Trim();
        var username = me.Usernames?.ActiveUsernames is { Length: > 0 } names ? $"@{names[0]}" : "-";
        logger.ZLogInformation($"登录成功: [[{me.Id}]] / [[{username}]] / [[{fullName}]]");

        var tdlRoot = TdlEnv.InitTdlRoot(logger);
        logger.ZLogInformation($"初始化完成, 数据目录: {tdlRoot}");
        return 0;
    }
    catch (Exception ex)
    {
        logger.ZLogError(ex, $"初始化失败: {ex.Message}");
        Console.Error.WriteLine($"初始化失败: {ex.Message}");
        return 1;
    }
}

// ──────────────────────────────────────────────
//  环境变量补齐
// ──────────────────────────────────────────────

async Task<(int?, string?)> EnsureApiCredentialsAsync(
    ILogger logger, bool checkOnly, int? apiIdArg, string? apiHashArg)
{
    var apiId = apiIdArg ?? TryParseInt(GetUserEnv("tdl_api_id"));
    var apiHash = apiHashArg ?? GetUserEnv("tdl_api_hash");

    if (apiIdArg.HasValue)
    {
        SetUserEnv("tdl_api_id", apiIdArg.Value.ToString());
        logger.ZLogInformation($"已保存 tdl_api_id = {apiIdArg.Value}");
    }
    if (!string.IsNullOrWhiteSpace(apiHashArg))
    {
        SetUserEnv("tdl_api_hash", apiHashArg);
        logger.ZLogInformation($"已保存 tdl_api_hash");
    }

    if (apiId.HasValue && !string.IsNullOrWhiteSpace(apiHash))
    {
        return (apiId, apiHash);
    }

    if (checkOnly)
    {
        logger.ZLogWarning($"缺少 API 凭据: 请先到 https://my.telegram.org 创建应用, 然后:");
        logger.ZLogWarning($"  dotnet run src/tdl/TdlInit.cs -- --api-id <id> --api-hash <hash>");
        return (apiId, apiHash);
    }

    AnsiConsole.MarkupLine("[yellow]未配置 Telegram API 凭据 (tdl_api_id / tdl_api_hash)。[/]");
    AnsiConsole.MarkupLine("请到 [link=https://my.telegram.org]https://my.telegram.org[/] -> API development tools 创建应用后填入。");

    Console.Write("输入 API ID (留空跳过): ");
    var idInput = Console.ReadLine()?.Trim();
    if (int.TryParse(idInput, out var parsedId))
    {
        SetUserEnv("tdl_api_id", idInput);
        apiId = parsedId;
        logger.ZLogInformation($"已保存 tdl_api_id = {parsedId}");
    }

    Console.Write("输入 API Hash (留空跳过): ");
    var hashInput = Console.ReadLine()?.Trim();
    if (!string.IsNullOrWhiteSpace(hashInput))
    {
        SetUserEnv("tdl_api_hash", hashInput);
        apiHash = hashInput;
        logger.ZLogInformation($"已保存 tdl_api_hash");
    }

    if (!apiId.HasValue || string.IsNullOrWhiteSpace(apiHash))
    {
        logger.ZLogError($"API 凭据缺失, TDLib 无法连接。请稍后运行: dotnet run src/tdl/TdlInit.cs -- --api-id <id> --api-hash <hash>");
        throw new InvalidOperationException("缺少 tdl_api_id / tdl_api_hash 环境变量");
    }

    await Task.CompletedTask;
    return (apiId, apiHash);
}

async Task<string> EnsureAuthMethodAsync(
    ILogger logger, bool checkOnly, string? phoneArg, string? botTokenArg, bool useQr)
{
    var phone = phoneArg ?? GetUserEnv("tdl_phone");
    var botToken = botTokenArg;

    if (!string.IsNullOrWhiteSpace(botTokenArg))
    {
        SetUserEnv("tdl_bot_token", botTokenArg);
        logger.ZLogInformation($"已保存 tdl_bot_token");
        return "bot";
    }
    if (!string.IsNullOrWhiteSpace(phoneArg))
    {
        SetUserEnv("tdl_phone", phoneArg);
        logger.ZLogInformation($"已保存 tdl_phone = {phoneArg}");
        return "phone";
    }

    // QR 强制登录: 不改配置, 直接返回
    if (useQr)
    {
        return "qr";
    }

    if (!string.IsNullOrWhiteSpace(botToken = GetUserEnv("tdl_bot_token")))
    {
        return "bot";
    }
    if (!string.IsNullOrWhiteSpace(phone))
    {
        return "phone";
    }

    if (checkOnly)
    {
        logger.ZLogWarning($"未配置登录方式: 可通过 --phone / --bot-token 保存, 或运行时用 --qr 扫码登录");
        return "none";
    }

    // 交互式选择登录方式并持久化
    var choice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("选择默认登录方式 (保存为用户级环境变量):")
            .AddChoices(new[] { "手机号 + 验证码", "Bot Token", "仅本次 QR 码登录 (不保存)" }));

    switch (choice)
    {
        case "手机号 + 验证码":
            Console.Write("输入手机号 (国际格式, 如 +8613800138000): ");
            var phoneInput = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(phoneInput))
            {
                throw new InvalidOperationException("手机号不能为空");
            }
            SetUserEnv("tdl_phone", phoneInput);
            logger.ZLogInformation($"已保存 tdl_phone = {phoneInput}");
            return "phone";
        case "Bot Token":
            Console.Write("输入 Bot Token: ");
            var tokenInput = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(tokenInput))
            {
                throw new InvalidOperationException("Bot Token 不能为空");
            }
            SetUserEnv("tdl_bot_token", tokenInput);
            logger.ZLogInformation($"已保存 tdl_bot_token");
            return "bot";
        default:
            return "qr";
    }
}

(string Host, int Port) EnsureProxyAsync(ILogger logger, bool checkOnly, string? proxyArg)
{
    if (!string.IsNullOrWhiteSpace(proxyArg))
    {
        var idx = proxyArg.LastIndexOf(':');
        if (idx < 0 || !int.TryParse(proxyArg[(idx + 1)..], out var port))
        {
            throw new ArgumentException($"代理格式错误, 应为 host:port: {proxyArg}");
        }
        SetUserEnv("tdl_proxy", proxyArg);
        logger.ZLogInformation($"已保存 tdl_proxy = {proxyArg}");
        return (proxyArg[..idx], port);
    }

    return TdlEnv.GetProxyConfig();
}

bool EnsureMessageDbAsync(ILogger logger, bool checkOnly, bool? messageDbArg)
{
    if (messageDbArg.HasValue)
    {
        var flag = messageDbArg.Value ? "1" : "0";
        SetUserEnv("tdl_use_message_db", flag);
        logger.ZLogInformation($"已保存 tdl_use_message_db = {flag}");
        return messageDbArg.Value;
    }

    var current = TdlEnv.UseMessageDb;
    if (current)
    {
        logger.ZLogInformation($"本地消息库已启用 (tdl_use_message_db=1), 注意 ~/.tdl/db 会持续增长");
    }
    return current;
}

void PrintConfigSummary(int? apiId, string? apiHash, string authMethod, string proxyHost, int proxyPort, bool useMessageDb)
{
    var table = new Table();
    table.Title = new TableTitle("[bold]Telegram 环境配置[/]");
    table.AddColumn("配置项");
    table.AddColumn("值");

    table.AddRow("tdl_api_id", apiId?.ToString() ?? "[red]未配置[/]");
    table.AddRow("tdl_api_hash", string.IsNullOrWhiteSpace(apiHash) ? "[red]未配置[/]" : "已配置");
    table.AddRow("登录方式", authMethod switch
    {
        "phone" => "手机号 + 验证码",
        "bot" => "Bot Token",
        "qr" => "QR 码 (仅本次)",
        _ => "[red]未配置[/]"
    });
    table.AddRow("tdl_proxy", $"{proxyHost}:{proxyPort}");
    table.AddRow("tdl_use_message_db", useMessageDb ? "1 (启用)" : "0 (关闭)");

    AnsiConsole.Write(table);
}

// ──────────────────────────────────────────────
//  用户级环境变量辅助
// ──────────────────────────────────────────────

string? GetUserEnv(string name) =>
    Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);

void SetUserEnv(string name, string value) =>
    Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);

int? TryParseInt(string? value) =>
    int.TryParse(value, out var n) ? n : null;
