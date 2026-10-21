#!/usr/bin/env dotnet run
#:include Modules/*.cs

// ============================================================================
// checkin.cs — 每日自动签到：WorkBuddy (CodeBuddy 国内版) + Trae (Trae Work 积分)
//
// 移植自 iicookies/auto-checkin 的 checkin.py，接口端点与解析逻辑保持一致。
// 凭证由 login.cs 一次性 OAuth 登录获取并存盘，本脚本负责：
//   1. 读盘凭证  2. token 临期自动刷新  3. 调签到接口 + 查询积分  4. 打印结果
//
// 用法:
//   dotnet run src/auto/checkin.cs# 签到所有已配置账号
//   dotnet run src/auto/checkin.cs -- --debug        # 打印详细 HTTP 调试信息
//   dotnet run src/auto/checkin.cs -- --ui-fallback  # Trae 失败时走桌面端 UI 兜底
// ============================================================================

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AutoCheckin;

try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 忽略 */ }

//凭证/日志落在脚本所在目录（file-based app 编译产物在系统临时目录，不能用 BaseDirectory）
CredentialStore.CredDir = AppPaths.CredDir();
Directory.CreateDirectory(CredentialStore.CredDir);

Log.OpenFile(Path.Combine(AppPaths.LogDir(), "checkin.log"));

Log.Debug = args.Contains("--debug");
var uiFallbackEnabled = args.Contains("--ui-fallback");

Log.WriteLine($"=== 自动签到 {Log.Now()} ===");
if (uiFallbackEnabled) Log.WriteLine("（已启用 Trae UI 签到兜底）");

var creds = CredentialStore.LoadAll();
if (creds.Count == 0)
{
    Log.WriteLine("未找到任何凭证。请先运行: dotnet run src/auto/login.cs -- workbuddy");
    Log.WriteLine($"凭证目录: {CredentialStore.CredDir}");
    return 1;
}

var overallOk = true;

foreach (var (product, cred) in creds)
{
    Log.WriteLine($"\n[{product}]");
    CheckinResult res;
    try
    {
        res = product == "workbuddy"
            ? new WorkBuddyClient(cred).Run()
            : new TraeClient(cred).Run();
    }
    catch (Exception ex)
    {
        Log.WriteLine($"  发生异常: {ex.Message}");
        if (Log.Debug) Log.WriteLine(ex.ToString());
        overallOk = false;
        continue;
    }

    var st = res.Status ?? "";

    // Trae token 彻底失效：提示重新登录后用新凭证再签一次
    if (product == "trae" && st.Contains("token 失效", StringComparison.Ordinal))
    {
        Log.WriteLine("  [Trae] token 已失效，请手动重新登录后重试:");
        Log.WriteLine("           dotnet run src/auto/login.cs -- trae");
    }

    Log.WriteLine($"  状态: {res.Status ?? "未知"}");
    if (!string.IsNullOrEmpty(res.Credits)) Log.WriteLine($"  积分: {res.Credits}");

    // 明确的失败状态：token 失效、认证失败、限流未领到
    var failKeywords = new List<string> { "token 失效", "认证失败", "服务端限流，领取失败" };
    if (uiFallbackEnabled) failKeywords.AddRange(["UI 兜底失败", "兜底超时", "兜底异常"]);
    if (failKeywords.Any(st.Contains)) overallOk = false;
}

Log.WriteLine("\n=== 完成 ===");
return overallOk ? 0 : 2;