#!/usr/bin/env dotnet run
#:include Modules/*.cs
#:property TargetFramework=net10.0-windows10.0.19041.0

// ============================================================================
// trae_ui.cs — Trae Work 桌面端 UI 自动签到（HTTP 签到失败时的兜底）
//
// 移植自 iicookies/auto-checkin 的 trae_ui_checkin.py。
// 零第三方依赖：Win32 窗口枚举 + GDI 截图 + Windows.Media.Ocr（系统内置）。
//
// 实测路径：
//   1. 聚焦 TraeWork CN 窗口
//   2. 点击左下角用户信息（「用户xxxxxxxx」）
//   3. 弹出账户菜单后，点击「签到」
//   4. 成功后菜单显示「今日已签到，明天再来吧」
//
// 用法:
//   dotnet run src/auto/trae_ui.cs                    # 签到
//   dotnet run src/auto/trae_ui.cs -- --debug         # 保存截图并打印 OCR
//   dotnet run src/auto/trae_ui.cs -- --dry-run# 只定位，不点击
// ============================================================================

using System.Text;
using AutoCheckin;

try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 忽略 */ }

var logDir = AppPaths.LogDir();
Log.OpenFile(Path.Combine(logDir, "trae_ui_checkin.log"));

var debug = args.Contains("--debug");
var dryRun = args.Contains("--dry-run");
var launch = !args.Contains("--no-launch");
var timeout = ParseIntArg(args, "--timeout", 300);
var readyTimeout = ParseIntArg(args, "--ready-timeout", 120);

Log.Debug = debug;

try
{
    return Run(debug, dryRun, launch, timeout, readyTimeout);
}
catch (OperationCanceledException)
{
    Log.WriteLine("\n已中断");
    return 130;
}
catch (Exception ex)
{
    Log.WriteLine($"发生异常: {ex.Message}");
    if (debug) Log.WriteLine(ex.ToString());
    return 1;
}

static int Run(bool debug, bool dryRun, bool launch, int timeout, int readyTimeout)
{
    Log.WriteLine($"=== Trae UI 签到 {Log.Now()} ===");

    var hwnd = TraeWindow.Find();
    if (hwnd is null)
    {
        if (!launch)
        {
            Log.WriteLine("未找到 TraeWork CN 窗口。请先打开 Trae，或去掉 --no-launch。");
            return 1;
        }
        TraeWindow.Launch();
        Log.WriteLine($"  等待窗口（最多 {timeout}s）...");
        hwnd = TraeWindow.Wait(timeout);
        if (hwnd is null)
        {
            Log.WriteLine("启动后仍未出现 TraeWork CN 窗口。");
            return 1;
        }
        Log.WriteLine($"  窗口: '{Win32.WindowTitle(hwnd.Value)}'");
        Thread.Sleep(2000);
    }
    else
    {
        Log.WriteLine($"  窗口: '{Win32.WindowTitle(hwnd.Value)}'");
    }

    Win32.FocusWindow(hwnd.Value);

    using var ocr = OcrEngine.Create();
    if (ocr is null)
    {
        Log.WriteLine("  初始化系统 OCR 引擎失败，请确认 Windows 语言包已安装中文（zh-Hans）。");
        return 1;
    }

    var logDir = AppPaths.LogDir();

    (List<OcrHit> Hits, int Left, int Top, int Height) Snap(IntPtr h, string tag)
    {
        // 截图始终留档，定时任务失败时才有排查依据；逐条 OCR 文本仍只在 --debug 打印
        var path = Path.Combine(logDir, $"trae_ui_{tag}.bmp");
        var rect = GetRect(h);
        var left = Math.Max(0, rect.Left);
        var top = Math.Max(0, rect.Top);
        var width = Math.Max(1, rect.Width);
        var height = Math.Max(1, rect.Height);

        var pixels = Win32.CaptureRegion(left, top, width, height);
        Win32.SaveBmp(pixels, width, height, path);

        var hits = ocr.Recognize(pixels, width, height);
        Log.WriteLine($"  [{tag}] OCR {hits.Count} 条 -> {Path.GetFileName(path)}");
        if (debug)
        {
            foreach (var hit in hits)
                Log.WriteLine($"    {hit.X1,4},{hit.Y1,4} {hit.Score:F2} {hit.Text}");
        }
        return (hits, left, top, height);
    }

    (int Left, int Top, int Width, int Height) GetRect(IntPtr h)
    {
        var (w, ht) = Win32.WindowSize(h);
        var r = default(Win32.RECT);
        if (Win32.GetWindowRectPublic(h, out r)) return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return (0, 0, Math.Max(1, w), Math.Max(1, ht));
    }

    // 冷启动时窗口先出现、界面后渲染，等到能认出用户信息/签到入口再动手
    var (hits, left, top, height) = Snap(hwnd.Value, "before");
    var deadline = DateTime.Now.AddSeconds(readyTimeout);
    var ready = IsUiReady(hits, height);

    while (!ready && DateTime.Now < deadline)
    {
        Log.WriteLine("  界面尚未就绪，等待渲染...");
        Thread.Sleep(4000);
        Win32.FocusWindow(hwnd.Value);
        (hits, left, top, height) = Snap(hwnd.Value, "before");
        ready = IsUiReady(hits, height);
    }

    if (!ready)
    {
        Log.WriteLine($"  窗口已出现但界面 {readyTimeout}s 内未就绪（未识别到用户信息/签到入口）。");
        Log.WriteLine($"  可查看 {Path.Combine(logDir, "trae_ui_before.bmp")}");
        return 2;
    }

    var menuOpen = OcrLocator.AccountMenuOpen(hits) || OcrLocator.AlreadyCheckedIn(hits);

    if (dryRun)
    {
        var user = OcrLocator.FindUserChip(hits, height);
        if (user is null) Log.WriteLine("  [dry-run] 未识别到用户信息");
        else Log.WriteLine($"  [dry-run] 用户信息: '{user.Text}' @ ({user.Cx},{user.Cy})");

        var probeBtn = OcrLocator.FindCheckinButton(hits, height);
        if (probeBtn is not null) Log.WriteLine($"  [dry-run] 签到按钮: '{probeBtn.Text}' @ ({probeBtn.Cx},{probeBtn.Cy})");
        if (OcrLocator.AlreadyCheckedIn(hits)) Log.WriteLine("  [dry-run] 已识别到「今日已签到」");
        Log.WriteLine("dry-run 到此为止（不点击）。");
        return 0;
    }

    if (menuOpen)
    {
        Log.WriteLine("  账户菜单已打开，跳过点击用户信息");
    }
    else
    {
        var expanded = false;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ClickUser(hits, height, left, top);
            Thread.Sleep(1200);
            Win32.FocusWindow(hwnd.Value);
            (hits, left, top, height) = Snap(hwnd.Value, attempt == 1 ? "menu" : $"menu{attempt}");
            if (OcrLocator.AccountMenuOpen(hits) || OcrLocator.AlreadyCheckedIn(hits))
            {
                expanded = true;
                break;
            }
            Log.WriteLine($"  账户菜单未展开，重试点击用户信息 ({attempt}/3)");
        }

        if (!expanded)
        {
            Log.WriteLine("多次点击后仍未展开账户菜单。请确认 Trae 窗口在前台且未被遮挡。");
            return 2;
        }
    }

    var creditsBefore = OcrLocator.ParseMenuCredits(hits);
    if (creditsBefore is not null) Log.WriteLine($"  菜单积分: {creditsBefore}");

    if (OcrLocator.AlreadyCheckedIn(hits))
    {
        Log.WriteLine("  状态: 今日已签到");
        if (creditsBefore is not null) Log.WriteLine($"  积分: {creditsBefore}");
        Log.WriteLine("=== 完成 ===");
        return 0;
    }

    var btn = OcrLocator.FindCheckinButton(hits, height);
    if (btn is null)
    {
        Log.WriteLine("已打开用户菜单，但 OCR 未找到「签到」按钮。");
        Log.WriteLine("请确认 Trae 窗口未被遮挡，账户菜单已展开。");
        return 2;
    }

    var sx = left + btn.Cx;
    var sy = top + btn.Cy;
    Win32.ClickScreen(sx, sy);
    Log.WriteLine($"  已点击签到 '{btn.Text}' -> ({sx},{sy})");

    int? creditsAfter = null;
    foreach (var wait in new[] { 1.5, 2.0 })
    {
        Thread.Sleep((int)(wait * 1000));
        Win32.FocusWindow(hwnd.Value);
        (hits, left, top, height) = Snap(hwnd.Value, "after");
        creditsAfter = OcrLocator.ParseMenuCredits(hits);
        if (OcrLocator.AlreadyCheckedIn(hits) ||
            (creditsBefore is not null && creditsAfter is not null && creditsAfter > creditsBefore))
            break;
    }

    if (OcrLocator.AlreadyCheckedIn(hits))
    {
        Log.WriteLine("  状态: 领取成功（今日已签到）");
        if (creditsAfter is not null)
        {
            var extra = creditsBefore is not null && creditsAfter >= creditsBefore
                ? $" (+{creditsAfter.Value - creditsBefore.Value})" : "";
            Log.WriteLine($"  积分: {creditsAfter}{extra}");
        }
        Log.WriteLine("=== 完成 ===");
        return 0;
    }

    if (creditsBefore is not null && creditsAfter is not null && creditsAfter > creditsBefore)
    {
        Log.WriteLine("  状态: 领取成功");
        Log.WriteLine($"  积分: {creditsAfter} (+{creditsAfter.Value - creditsBefore.Value})");
        Log.WriteLine("=== 完成 ===");
        return 0;
    }

    Log.WriteLine("  状态: 已点击签到，但未识别到成功文案");
    if (creditsAfter is not null) Log.WriteLine($"  积分: {creditsAfter}");
    Log.WriteLine($"  可用 --debug 查看 {Path.Combine(logDir, "trae_ui_after.bmp")}");
    Log.WriteLine("=== 完成 ===");
    return 3;

    void ClickUser(List<OcrHit> currentHits, int imgHeight, int shotLeft, int shotTop)
    {
        var user = OcrLocator.FindUserChip(currentHits, imgHeight);
        if (user is null)
        {
            Log.WriteLine("OCR 未找到左下角用户信息，尝试点击窗口左下角兜底位置。");
            Win32.ClickScreen(shotLeft + 40, shotTop + imgHeight - 52);
            return;
        }
        var dx = Math.Min(-24, user.X1 - user.Cx - 8);
        var x = shotLeft + user.Cx + dx;
        var y = shotTop + user.Cy;
        Win32.ClickScreen(x, y);
        Log.WriteLine($"  已点击用户信息 '{user.Text}' -> ({x},{y})");
        if (debug)
            Log.WriteLine($"    box=({user.X1},{user.Y1})-({user.X2},{user.Y2})");
    }
}

static bool IsUiReady(List<OcrHit> hits, int height) =>
    OcrLocator.FindUserChip(hits, height) is not null
    || OcrLocator.FindCheckinButton(hits, height) is not null
    || OcrLocator.AlreadyCheckedIn(hits);

static int ParseIntArg(string[] argv, string name, int fallback)
{
    var idx = Array.IndexOf(argv, name);
    if (idx < 0 || idx + 1 >= argv.Length) return fallback;
    return int.TryParse(argv[idx + 1], out var v) ? v : fallback;
}