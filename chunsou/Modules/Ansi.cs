// ─────────────────────────────────────────────────────────────
// 本文件移植自 chunsou（Python）: https://github.com/Funsiooo/chunsou
// 对应原项目文件：modules/core/color.py
// Copyright (C) Funsiooo    Licensed under GPL-3.0（见 LICENSE）
// ─────────────────────────────────────────────────────────────

namespace Chunsou;

/// <summary>
/// 终端配色，对应 Python 版的 modules/core/color.py
/// </summary>
public static class Ansi
{
    public const string Reset = "\u001b[0m";
    public const string Cyan = "\u001b[38;5;67m";
    public const string Green = "\u001b[01;38;5;42m";
    public const string Red = "\u001b[01;31m";
    public const string White = "\u001b[37m";
    public const string BrightWhite = "\u001b[01;37m";
    public const string Yellow = "\u001b[33m";
    public const string YellowB = "\u001b[93m";
    public const string Orange = "\u001b[01;38;2;252;166;82m";

    /// <summary>非 TTY（重定向 / 管道）时返回空串，避免污染日志文件。</summary>
    public static bool Enabled { get; set; } =
        !Console.IsOutputRedirected &&
        Environment.GetEnvironmentVariable("NO_COLOR") is null;

    private static string Wrap(string code, string text) => Enabled ? code + text + Reset : text;

    public static string C(string code, string text) => Wrap(code, text);

    /// <summary>
    /// 输出一条 INFO 日志（自动带时间戳）。若 text 已含 ANSI 颜色码则原样输出。
    /// </summary>
    public static string Info(string text) => Enabled
        ? $"{White}[{Reset}{Cyan}{Clock.Stamp()}{Reset}{White}]{Reset} {White}[{Reset}{Green}*{Reset}{White}]{Reset} " +
          $"{White}[{Reset}{Cyan}INFO{Reset}{White}]{Reset} {White}{text}{Reset}"
        : $"[{Clock.Stamp()}] [INFO] {StripAnsi(text)}";

    public static string Success(string text) => Enabled
        ? $"{White}[{Reset}{Cyan}{Clock.Stamp()}{Reset}{White}]{Reset} {White}[{Reset}{Green}+{Reset}{White}]{Reset} {White}{text}{Reset}"
        : $"[+] {StripAnsi(text)}";

    public static string Failure(string text) => Enabled
        ? $"{White}[{Reset}{Cyan}{Clock.Stamp()}{Reset}{White}]{Reset} {White}[{Reset}{Red}-{Reset}{White}]{Reset} {White}{text}{Reset}"
        : $"[-] {StripAnsi(text)}";

    private static string StripAnsi(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\u001b')
            {
                while (i < s.Length && s[i] != 'm') i++;
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}

/// <summary>
/// 时间格式化，对应 modules/core/time.py
/// </summary>
public static class Clock
{
    public static string Stamp() => DateTime.Now.ToString("HH:mm:ss");

    public static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
}
