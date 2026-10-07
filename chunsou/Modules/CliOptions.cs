// ─────────────────────────────────────────────────────────────
// 本文件移植自 chunsou（Python）: https://github.com/Funsiooo/chunsou
// 对应原项目文件：modules/core/args.py
// Copyright (C) Funsiooo    Licensed under GPL-3.0（见 LICENSE）
//
// 移植改动：命令行解析由手写 switch 改为 System.CommandLine 2.0，
// 换来自动帮助、POSIX 短选项组合（-et30）、非法值拦截与标准退出码。
// ─────────────────────────────────────────────────────────────

using System.CommandLine;

namespace Chunsou;

/// <summary>
/// 命令行选项定义，对应 modules/core/args.py 的 argparse 配置。
/// 选项名与默认值保持与原项目一致，便于平滑迁移。
/// </summary>
public sealed class CliSpec
{
    public const string ToolVersion = "v1.9";

    // ── target ──
    public Option<string?> Url { get; } = new("--url", "-u")
    {
        Description = "scan for a single url",
    };

    public Option<string?> File { get; } = new("--file", "-f")
    {
        Description = "specify a file for multi scanning",
    };

    // ── subdomain ──
    public Option<string?> Domain { get; } = new("--domain", "-du")
    {
        Description = "subdomain blasting of a single domain name",
    };

    public Option<string?> Domains { get; } = new("--domains", "-df")
    {
        Description = "subburst the domain name in the specified file",
    };

    // ── api ──
    public Option<string?> Fofa { get; } = new("--fofa", "-fo")
    {
        Description = "call the fofa api for asset collection",
    };

    public Option<string?> Hunter { get; } = new("--hunter", "-hu")
    {
        Description = "call the hunter api for asset collection",
    };

    /// <summary>
    /// AI 模式：auto | force。
    /// 刻意不设默认值 —— 设了 DefaultValueFactory 后裸用 <c>--ai</c> 会贪婪吞掉后一个选项
    /// （实测把 <c>--ai-provider deepseek</c> 的值吃掉）。只有 Arity=ZeroOrOne
    /// 且无默认值时，才能区分「未指定」与「显式 auto」。
    /// </summary>
    public Option<string?> Ai { get; } = new("--ai")
    {
        Description = "enable semantic analysis: auto | force（裸用等同 auto）",
    };

    public Option<string?> AiProvider { get; } = new("--ai-provider")
    {
        Description = "ai provider: gpt | deepseek",
    };

    public Option<string?> AiModel { get; } = new("--ai-model")
    {
        Description = "specify ai model",
    };

    // ── others ──
    public Option<string?> Proxy { get; } = new("--proxy", "-p")
    {
        Description = "proxy scan traffic: http / https / socks5",
    };

    public Option<int> Threads { get; } = new("--threads", "-t")
    {
        Description = "number of scanning threads",
        DefaultValueFactory = _ => 50,
    };

    public Option<string?> Output { get; } = new("--output", "-o")
    {
        Description = "specified output file (.txt / .xlsx)",
    };

    public Option<bool> ShowErrors { get; } = new("--error", "-e")
    {
        Description = "show the specific error cause of failed targets",
    };

    public Option<bool> Verbose { get; } = new("--verbose")
    {
        Description = "verbose logging",
    };

    public Option<bool> ShowTip { get; } = new("--tip")
    {
        Description = "spatial mapping search syntax reference",
    };

    /// <summary>构建命令树。Arity 与取值校验必须放在这里，不能作为属性初始化。</summary>
    public RootCommand Build()
    {
        // 允许 --ai / --ai force / --ai=force 三种写法
        Ai.Arity = ArgumentArity.ZeroOrOne;
        Ai.AcceptOnlyFromAmong("auto", "force");
        AiProvider.AcceptOnlyFromAmong("gpt", "deepseek");

        Threads.Validators.Add(result =>
        {
            // 校验器拿不到 ParseResult，直接读 token；有值才校验
            var token = result.Tokens.LastOrDefault();
            if (token is null || !int.TryParse(token.Value, out var n)) return;

            if (n is < 1 or > 2000)
                result.AddError($"线程数必须在 1-2000 之间（当前 {n}）");
        });

        var root = new RootCommand(
            "chunsou（春蒐）· 多线程 Web 指纹识别工具\n" +
            "移植自 https://github.com/Funsiooo/chunsou\n" +
            "指纹识别 / 子域名爆破 / FOFA·Hunter 资产收集 / AI 语义分析")
        {
            Url, File, Domain, Domains,
            Fofa, Hunter, Ai, AiProvider, AiModel,
            Proxy, Threads, Output, ShowErrors, Verbose, ShowTip,
        };

        return root;
    }
}

/// <summary>解析后的运行参数，供业务层使用</summary>
public sealed class CliOptions
{
    public string? Url { get; init; }
    public string? File { get; init; }
    public string? Domain { get; init; }
    public string? Domains { get; init; }
    public string? Fofa { get; init; }
    public string? Hunter { get; init; }

    /// <summary>auto / force / null（null = 未启用 AI）</summary>
    public string? Ai { get; init; }
    public string? AiProvider { get; init; }
    public string? AiModel { get; init; }

    public string? Proxy { get; init; }
    public int Threads { get; init; }
    public string? Output { get; init; }
    public bool ShowErrors { get; init; }
    public bool Verbose { get; init; }

    /// <summary>是否指定了任何操作目标</summary>
    public bool HasTarget =>
        Url is not null || File is not null || Domain is not null ||
        Domains is not null || Fofa is not null || Hunter is not null;

    /// <summary>
    /// 从 ParseResult 提取业务参数。
    /// --ai 显式出现但未带值（裸用）时视为 auto，对齐原项目 argparse 的 const='auto'。
    /// </summary>
    public static CliOptions From(ParseResult r, CliSpec spec)
    {
        // GetResult(...).Implicit 为 false 表示选项确实出现在命令行上
        var aiSpecified = r.GetResult(spec.Ai) is { Implicit: false };

        return new CliOptions
        {
            Url = r.GetValue(spec.Url),
            File = r.GetValue(spec.File),
            Domain = r.GetValue(spec.Domain),
            Domains = r.GetValue(spec.Domains),
            Fofa = r.GetValue(spec.Fofa),
            Hunter = r.GetValue(spec.Hunter),

            Ai = aiSpecified ? (r.GetValue(spec.Ai) ?? "auto") : null,
            AiProvider = r.GetValue(spec.AiProvider),
            AiModel = r.GetValue(spec.AiModel),

            Proxy = r.GetValue(spec.Proxy),
            Threads = r.GetValue(spec.Threads),
            Output = r.GetValue(spec.Output),
            ShowErrors = r.GetValue(spec.ShowErrors),
            Verbose = r.GetValue(spec.Verbose),
        };
    }
}
