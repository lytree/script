using System.Text.RegularExpressions;

// ─────────────────────────────────────────────────────────────
// 本文件移植自 chunsou（Python）: https://github.com/Funsiooo/chunsou
// 对应原项目文件：modules/core/output.py, modules/api/ai.py
// Copyright (C) Funsiooo    Licensed under GPL-3.0（见 LICENSE）
// ─────────────────────────────────────────────────────────────

namespace Chunsou;

/// <summary>
/// 读取 modules/config/config.ini（保持与 Python 版同格式同节名）。
/// 支持环境变量覆盖：FOFA_EMAIL / FOFA_KEY / HUNTER_KEY /
/// GPT_API_KEY / DEEPSEEK_API_KEY，避免把密钥写进文件。
/// </summary>
public static partial class Config
{
    /// <summary>
    /// 资源根目录（finger.json / config.ini / wordlist.txt 所在处）。
    /// file-based app 会被编译到临时目录，因此优先用当前工作目录，
    /// 回退到程序集目录，最后回退到脚本自身所在目录。
    /// </summary>
    public static string RootDir { get; set; } = ResolveRoot();

    private static string ResolveRoot()
    {
        var probe = new[] { "finger.json" };

        var cwd = Environment.CurrentDirectory;
        if (probe.All(f => File.Exists(Path.Combine(cwd, f)))) return cwd;

        var asmDir = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)) ?? cwd;
        if (probe.All(f => File.Exists(Path.Combine(asmDir, f)))) return asmDir;

        var parent = Directory.GetParent(asmDir)?.FullName ?? cwd;
        if (probe.All(f => File.Exists(Path.Combine(parent, f)))) return parent;

        return cwd;
    }

    public static string ConfigPath => Path.Combine(RootDir, "config.ini");

    private static readonly Dictionary<string, Dictionary<string, string>> Sections = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();
    private static bool _loaded;

    [GeneratedRegex(@"^\s*\[(?<s>[^\]]+)\]\s*$")]
    private static partial Regex SectionRegex();

    [GeneratedRegex(@"^\s*(?<k>[^#;][^=]*?)\s*=\s*(?<v>.*?)\s*$")]
    private static partial Regex KeyValueRegex();

    private static void Load()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;

            if (File.Exists(ConfigPath))
            {
                string? current = null;
                foreach (var raw in File.ReadAllLines(ConfigPath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;

                    var m = SectionRegex().Match(line);
                    if (m.Success)
                    {
                        current = m.Groups["s"].Value.Trim();
                        if (!Sections.ContainsKey(current))
                            Sections[current] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        continue;
                    }

                    var kv = KeyValueRegex().Match(line);
                    if (!kv.Success || current is null) continue;

                    Sections[current][kv.Groups["k"].Value.Trim()] =
                        kv.Groups["v"].Value.Trim().Trim('"');
                }
            }

            _loaded = true;
        }
    }

    private static string Get(string section, string key, string fallback = "")
    {
        Load();
        if (Sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) && v.Length > 0)
            return v;
        return fallback;
    }

    // ── FOFA ──
    public static string FofaEmail => Env("FOFA_EMAIL") ?? Get("fofa_email", "email");
    public static string FofaKey => Env("FOFA_KEY") ?? Get("fofa_api_key", "key");
    public static string FofaSize => Get("fofa_size", "size", "100");

    // ── Hunter ──
    public static string HunterKey => Env("HUNTER_KEY") ?? Get("hunter_api_key", "key");
    public static string HunterSize => Get("hunter_size", "size", "20");

    // ── AI ──
    public static string AiProvider => Get("ai", "provider", "deepseek");
    public static string AiModel => Get("ai", "model", "gpt-5.5");
    public static int AiTimeout => int.TryParse(Get("ai", "timeout", "20"), out var t) ? t : 20;
    public static bool AiCache => Get("ai", "cache", "true").ToLowerInvariant() == "true";
    public static int AiMaxTokensAuto => int.TryParse(Get("ai", "max_output_tokens_auto", "120"), out var a) ? a : 120;
    public static int AiMaxTokensForce => int.TryParse(Get("ai", "max_output_tokens_force", "180"), out var b) ? b : 180;

    public static string GptBaseUrl => Get("gpt_api", "base_url", "https://api.openai.com/v1");
    public static string GptApiKey => Env("GPT_API_KEY") ?? Env("OPENAI_API_KEY") ?? Get("gpt_api", "api_key");

    public static string DeepSeekBaseUrl => Get("deepseek_api", "base_url", "https://api.deepseek.com");
    public static string DeepSeekApiKey => Env("DEEPSEEK_API_KEY") ?? Get("deepseek_api", "api_key");
    public static string DeepSeekModel => Get("deepseek_api", "model", "deepseek-v4-pro");

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    /// <summary>
    /// 配置文件缺失时生成一份。
    /// 优先从 config.example.ini 复制（保证与仓库里的模板同步），
    /// 找不到示例文件时才回退到内置模板。
    /// </summary>
    public static void EnsureTemplate()
    {
        if (File.Exists(ConfigPath)) return;

        Directory.CreateDirectory(RootDir);

        var example = Path.Combine(RootDir, "config.example.ini");
        if (File.Exists(example))
        {
            File.Copy(example, ConfigPath);
            return;
        }

        File.WriteAllText(ConfigPath, """
            [fofa_email]
            email = ""

            [fofa_api_key]
            key = ""

            [fofa_size]
            size = "100"

            [hunter_api_key]
            key = ""

            [hunter_size]
            size = "20"

            [ai]
            enabled = "false"
            provider = "deepseek"
            model = "gpt-5.5"
            timeout = "20"
            cache = "true"
            max_output_tokens_auto = "120"
            max_output_tokens_force = "180"

            [gpt_api]
            base_url = "https://api.openai.com/v1"
            api_key = ""

            [deepseek_api]
            base_url = "https://api.deepseek.com"
            api_key = ""
            model = "deepseek-v4-pro"

            """);
    }
}
