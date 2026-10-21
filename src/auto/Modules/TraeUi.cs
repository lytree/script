// ============================================================================
// Modules/TraeUi.cs — Trae 桌面端 UI 自动签到
//
// 由 trae_ui.cs 通过 #:include 复用，勿单独运行。
// 移植自 iicookies/auto-checkin 的 trae_ui_checkin.py。
// 原 Python 依赖 pywinauto / mss / pillow / rapidocr-onnxruntime，
// 此处用Windows 原生能力替代，零第三方依赖：
//   pywinauto uia枚举  -> Win32 EnumWindows（更快，且能解决定时任务窗口发现失败）
//   mss 截图          -> GDI BitBlt + CreateDIBSection
//   pillow 图片保存   -> 直接写 BMP 文件
//   rapidocr OCR      -> Windows.Media.Ocr（系统内置，支持 zh-Hans）
//
// Trae 是 Electron 应用，签到按钮在Chromium 页面里，UIA 看不到，
// 必须：截图 → OCR → 鼠标点击。
// ============================================================================

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace AutoCheckin;

/// <summary>OCR 命中的一条文本及其位置。</summary>
public sealed class OcrHit
{
    public required string Text { get; init; }
    public double Score { get; init; } = 1.0;
    public required int X1 { get; init; }
    public required int Y1 { get; init; }
    public required int X2 { get; init; }
    public required int Y2 { get; init; }

    public int Cx => (X1 + X2) / 2;
    public int Cy => (Y1 + Y2) / 2;

    /// <summary>去掉全部空白，便于做「签到」这类精确匹配。</summary>
    public string Compact => Text.Replace(" ", "").Replace("\t", "").Replace("\n", "").Replace("\r", "");
}

/// <summary>窗口截图结果。</summary>
public sealed class WindowShot
{
    public required int Left { get; init; }
    public required int Top { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

/// <summary>OCR 命中识别与定位逻辑（对应 Python 的 find_* / parse_* 函数）。</summary>
public static class OcrLocator
{
    private static readonly Regex UserRe = new(@"用户\s*\d+", RegexOptions.Compiled);
    private static readonly Regex CreditRe = new(@"\+?\s*([\d,]+)", RegexOptions.Compiled);

    /// <summary>左下角用户信息：底部区域里带「用户」的条目，取最靠下的一条。</summary>
    public static OcrHit? FindUserChip(List<OcrHit> hits, int imgHeight)
    {
        var floor = (int)(imgHeight * 0.82);
        var cands = hits.Where(h => h.Y1 >= floor &&
            (h.Text.Contains("用户") || UserRe.IsMatch(h.Text))).ToList();
        if (cands.Count == 0) cands = hits.Where(h => UserRe.IsMatch(h.Text)).ToList();
        return cands.Count == 0 ? null : cands.MaxBy(h => h.Y1);
    }

    /// <summary>左侧说明文案：每日签到领…积分（OCR 可能带前缀噪声，如「白」）。</summary>
    private static bool IsDailyCheckinLabel(string compact) =>
        (compact.Contains("每日") && compact.Contains("签到") && compact.Contains("积分")) ||
        (compact.Contains("签到领") && compact.Contains("积分"));

    /// <summary>
    /// 账户菜单里的「签到」按钮。
    /// 要排除两类干扰：左侧说明文字「每日签到领200积分」，以及左下角浮层里的
    /// 签到按钮（点它不会有可识别的结果反馈）。
    /// </summary>
    public static OcrHit? FindCheckinButton(List<OcrHit> hits, int imgHeight)
    {
        var floor = (int)(imgHeight * 0.85);  // 左下角浮层区域，不要点
        var cands = hits.Where(h => h.Y2 < floor).ToList();

        var exact = cands.Where(h => h.Compact == "签到").ToList();
        if (exact.Count > 0) return exact.MaxBy(h => h.X1);

        var loose = cands.Where(h =>
            h.Compact.EndsWith("签到") &&
            !h.Compact.Contains("每日") &&
            !h.Compact.Contains("今日") &&
            h.Compact.Length <= 4).ToList();
        if (loose.Count > 0) return loose.MaxBy(h => h.X1);

        // OCR 漏检独立「签到」：从「每日签到领…积分」说明行推算右侧按钮
        var labels = cands.Where(h => IsDailyCheckinLabel(h.Compact)).ToList();
        if (labels.Count == 0) return null;

        var label = labels.MaxBy(h => h.Y1)!;
        // 实测说明文字右缘(x=216)到按钮左缘(x=285)间距约 69px，按钮宽约 37px
        const int btnW = 40, gap = 65;
        var x1 = label.X2 + gap;
        Log.WriteLine($"  OCR 未单独识别「签到」，按说明文案 '{label.Text}' " +
                      $"推算按钮 @ ({(x1 + btnW / 2)},{(label.Y1 + label.Y2) / 2})");
        return new OcrHit
        {
            Text = "签到", Score = 0.5,
            X1 = x1, Y1 = label.Y1, X2 = x1 + btnW, Y2 = label.Y2
        };
    }

    /// <summary>账户菜单是否展开：靠「管理账户 / 退出登录」这类菜单专属项判断。</summary>
    public static bool AccountMenuOpen(List<OcrHit> hits)
    {
        var blob = string.Concat(hits.Select(h => h.Compact));
        return new[] { "管理账户", "退出登录", "报告问题" }.Any(blob.Contains);
    }

    public static bool AlreadyCheckedIn(List<OcrHit> hits)
    {
        var blob = string.Concat(hits.Select(h => h.Compact));
        return new[] { "今日已签", "明天再来", "已领取" }.Any(blob.Contains);
    }

    /// <summary>账户菜单标题右侧形如 +3,852 / + 4,052。</summary>
    public static int? ParseMenuCredits(List<OcrHit> hits)
    {
        foreach (var h in hits)
        {
            if (h.Y1 < 350 || h.Y1 > 480) continue;
            if (!h.Text.Contains('+')) continue;
            var m = CreditRe.Match(h.Text);
            if (!m.Success) continue;
            if (int.TryParse(m.Groups[1].Value.Replace(",", ""), out var v)) return v;
        }
        return null;
    }
}

/// <summary>Win32 窗口枚举与操作。</summary>
public static class Win32
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    // 注意：biPlanes / biBitCount 是 2 字节的 WORD，整个头共 40 字节。
    // 若误写成 int，头部会变成 44 字节导致 CreateDIBSection 失败。
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, char[] s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, char[] s, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, char[] buf, ref int size);

    private const int SRCCOPY = 0x00CC0020;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>枚举可见顶层窗口。</summary>
    public static List<(IntPtr Hwnd, string Title, string Class, uint Pid)> VisibleTopWindows()
    {
        var found = new List<(IntPtr, string, string, uint)>();
        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hwnd)) return true;
                var title = new char[512];
                var cls = new char[256];
                GetWindowTextW(hwnd, title, title.Length);
                GetClassNameW(hwnd, cls, cls.Length);
                GetWindowThreadProcessId(hwnd, out var pid);
                found.Add((hwnd, new string(title).TrimEnd('\0'), new string(cls).TrimEnd('\0'), pid));
            }
            catch { /* 单个窗口失败不影响枚举 */ }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string WindowTitle(IntPtr hwnd)
    {
        var buf = new char[512];
        GetWindowTextW(hwnd, buf, buf.Length);
        return new string(buf).TrimEnd('\0');
    }

    public static (int W, int H) WindowSize(IntPtr hwnd)
        => GetWindowRect(hwnd, out var r) ? (r.Right - r.Left, r.Bottom - r.Top) : (0, 0);

    /// <summary>取窗口矩形（截图定位用）。</summary>
    public static bool GetWindowRectPublic(IntPtr hwnd, out RECT rect) => GetWindowRect(hwnd, out rect);

    /// <summary>取进程可执行文件路径。</summary>
    public static string ProcessExe(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var buf = new char[1024];
            var size = buf.Length;
            return QueryFullProcessImageNameW(h, 0, buf, ref size) ? new string(buf, 0, size) : "";
        }
        finally { CloseHandle(h); }
    }

    public static bool WindowAlive(IntPtr hwnd) => IsWindow(hwnd) && IsWindowVisible(hwnd);

    /// <summary>置前窗口；最小化时先还原。</summary>
    public static void FocusWindow(IntPtr hwnd)
    {
        try
        {
            ShowWindow(hwnd, 9 /* SW_RESTORE */);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        catch { /* 前台失败不致命，后续 OCR 仍可尝试 */ }
        Thread.Sleep(400);
    }

    /// <summary>截取屏幕指定区域，返回 BGRA 像素。</summary>
    public static byte[] CaptureRegion(int left, int top, int width, int height)
    {
        var hdcScreen = GetDC(IntPtr.Zero);
        var hdcMem = CreateCompatibleDC(hdcScreen);
        var bi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = 40, biWidth = width, biHeight = -height,
                biPlanes = 1, biBitCount = 32, biCompression = 0
            }
        };

        var hbm = CreateDIBSection(hdcScreen, ref bi, 0, out var bits, IntPtr.Zero, 0);
        if (hbm == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);
            throw new InvalidOperationException("创建屏幕位图失败");
        }

        try
        {
            var old = SelectObject(hdcMem, hbm);
            BitBlt(hdcMem, 0, 0, width, height, hdcScreen, left, top, SRCCOPY);
            SelectObject(hdcMem, old);

            var data = new byte[width * height * 4];
            Marshal.Copy(bits, data, 0, data.Length);
            return data;
        }
        finally
        {
            DeleteObject(hbm);
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    /// <summary>把 BGRA 像素写成 32bpp BMP 文件（替代 Python 版 pillow 的截图保存）。</summary>
    public static void SaveBmp(byte[] bgra, int width, int height, string path)
    {
        const int offset = 14 + 124;  // BITMAPFILEHEADER + BITMAPV4HEADER
        int stride = width * 4;
        int imgSize = stride * height;
        var buf = new byte[offset + imgSize];
        using var ms = new MemoryStream(buf);
        using var w = new BinaryWriter(ms);

        w.Write((byte)'B'); w.Write((byte)'M');
        w.Write(offset + imgSize);
        w.Write(0); w.Write(offset);
        w.Write(124);                    // BITMAPV4HEADER
        w.Write(width); w.Write(-height);  // 负高度 = 自上而下
        w.Write((short)1); w.Write((short)32);
        w.Write(3);                      // BI_BITFIELDS
        w.Write(imgSize);
        w.Write(2835); w.Write(2835);
        w.Write(0); w.Write(0);
        w.Write(0x00FF0000u);            // 通道掩码 R
        w.Write(0x0000FF00u);            // G
        w.Write(0x000000FFu);            // B
        w.Write(0xFF000000u);            // A
        w.Write(0x57696E20u);            // LCS_sRGB
        for (var i = 0; i < 9; i++) w.Write(0);   // CIENDPOINTS(36 字节)
        for (var i = 0; i < 3; i++) w.Write(0);   // Gamma R/G/B
        w.Write(0);                                // 保留

        for (var y = 0; y < height; y++) ms.Write(bgra, y * stride, stride);
        w.Flush();

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, buf);
    }

    public static void ClickScreen(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(60);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>取桌面窗口尺寸，用于测试。</summary>
    public static (int W, int H) DesktopSize()
    {
        GetWindowRect(GetDesktopWindow(), out var r);
        return (r.Right - r.Left, r.Bottom - r.Top);
    }
}

/// <summary>OCR 引擎：包装 Windows.Media.Ocr。</summary>
public sealed class OcrEngine : IDisposable
{
    private readonly Windows.Media.Ocr.OcrEngine _engine;

    private OcrEngine(Windows.Media.Ocr.OcrEngine engine) => _engine = engine;

    public static OcrEngine? Create()
    {
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
        return engine is null ? null : new OcrEngine(engine);
    }

    /// <summary>识别 BGRA 像素，返回带坐标的命中列表。</summary>
    public List<OcrHit> Recognize(byte[] bgra, int width, int height)
    {
        // 包装成 BMP 交给 WinRT 解码，避免直接构造 SoftwareBitmap 的投影差异
        var bmp = BuildBmpBytes(bgra, width, height);

        using var stream = new InMemoryRandomAccessStream();
        using (var dw = new DataWriter(stream))
        {
            dw.WriteBytes(bmp);
            dw.StoreAsync().AsTask().GetAwaiter().GetResult();
            dw.DetachStream();
        }
        stream.Seek(0);

        var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
        using var sb = decoder
            .GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight)
            .AsTask().GetAwaiter().GetResult();

        var result = _engine.RecognizeAsync(sb).AsTask().GetAwaiter().GetResult();

        var hits = new List<OcrHit>();
        foreach (var line in result.Lines)
        {
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                hits.Add(new OcrHit
                {
                    Text = word.Text,
                    Score = 1.0,
                    X1 = (int)r.X, Y1 = (int)r.Y,
                    X2 = (int)(r.X + r.Width), Y2 = (int)(r.Y + r.Height)
                });
            }
        }
        return hits;
    }

    /// <summary>构造 32bpp BGRA 的 BMP 文件字节（BITMAPV4HEADER）。</summary>
    private static byte[] BuildBmpBytes(byte[] bgra, int width, int height)
    {
        const int offset = 14 + 124;
        int stride = width * 4;
        int imgSize = stride * height;
        var buf = new byte[offset + imgSize];
        using var ms = new MemoryStream(buf);
        using var w = new BinaryWriter(ms);

        w.Write((byte)'B'); w.Write((byte)'M');
        w.Write(offset + imgSize);
        w.Write(0); w.Write(offset);
        w.Write(124);
        w.Write(width); w.Write(-height);
        w.Write((short)1); w.Write((short)32);
        w.Write(3);
        w.Write(imgSize);
        w.Write(2835); w.Write(2835);
        w.Write(0); w.Write(0);
        w.Write(0x00FF0000u); w.Write(0x0000FF00u); w.Write(0x000000FFu); w.Write(0xFF000000u);
        w.Write(0x57696E20u);
        for (var i = 0; i < 9; i++) w.Write(0);
        for (var i = 0; i < 3; i++) w.Write(0);
        w.Write(0);

        for (var y = 0; y < height; y++) ms.Write(bgra, y * stride, stride);
        w.Flush();
        return buf;
    }

    public void Dispose() { }
}

/// <summary>Trae 窗口查找与启动。</summary>
public static class TraeWindow
{
    private static readonly string[] WindowTitles = ["TraeWork CN", "TRAE SOLO CN", "Trae CN"];
    private static readonly string[] ExeNames = ["TRAE SOLO CN.exe", "TraeWork CN.exe", "Trae CN.exe"];

    /// <summary>候选安装路径（对应 Python 的 candidate_exes）。</summary>
    public static List<string> CandidateExes()
    {
        var roots = new List<string>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) roots.Add(Path.Combine(local, "Programs"));
        roots.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "Local", "Programs"));
        // 部分机器把 LocalAppData 重定向到别的盘
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var profileName = Path.GetFileName(home.TrimEnd(Path.DirectorySeparatorChar));
        roots.Add(Path.Combine("D:\\Users", profileName, "AppData", "Local", "Programs"));

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;

            // 固定子目录 TRAE SOLO CN
            foreach (var name in ExeNames)
            {
                var p = Path.Combine(root, "TRAE SOLO CN", name);
                if (File.Exists(p) && seen.Add(p)) found.Add(p);
            }

            // 通配 TRAE* 目录下的 exe
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root, "TRAE*"))
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories))
                    {
                        if (!ExeNames.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)) continue;
                        if (seen.Add(file)) found.Add(file);
                    }
                }
            }
            catch (UnauthorizedAccessException) { /*跳过无权限目录 */ }
        }
        return found;
    }

    private static bool IsTraeCnExe(string exe)
    {
        if (string.IsNullOrEmpty(exe)) return false;
        var name = Path.GetFileName(exe);
        if (ExeNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        var parent = Path.GetFileName(Path.GetDirectoryName(exe) ?? "");
        return parent.Contains("trae solo cn", StringComparison.OrdinalIgnoreCase)
            || name.Contains("traework", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>是否为安装器/更新器窗口（避免在安装界面上误点击）。</summary>
    public static bool IsInstallerTitle(string title)
    {
        var t = (title ?? "").ToLowerInvariant();
        var markers = new[] { "setup", "installer", "installing", "install - ", " - install" };
        if (markers.Any(m => t.Contains(m, StringComparison.Ordinal))) return true;
        var stripped = (title ?? "").Trim();
        return stripped.StartsWith("安装") || stripped.StartsWith("正在安装") || stripped.StartsWith("卸载");
    }

    /// <summary>查找 Trae 主窗口句柄，找不到返回 null。</summary>
    public static IntPtr? Find()
    {
        IntPtr? fallback = null;

        foreach (var (hwnd, title, cls, pid) in Win32.VisibleTopWindows())
        {
            // 跳过安装器/更新器窗口
            if (IsInstallerTitle(title)) continue;

            if (WindowTitles.Any(t => title.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                var exe = Win32.ProcessExe(pid);
                if (!string.IsNullOrEmpty(exe) && !IsTraeCnExe(exe)) continue;
                return hwnd;
            }

            // 启动早期主窗口可能还没标题，用进程 + 窗口类兜底（排除 Electron 辅助小窗）
            if (fallback is null && cls.Contains("Chrome_WidgetWin", StringComparison.Ordinal)
                && IsTraeCnExe(Win32.ProcessExe(pid)))
            {
                var (w, h) = Win32.WindowSize(hwnd);
                if (w >= 600 && h >= 400) fallback = hwnd;
            }
        }
        return fallback;
    }

    public static bool HasInstallerWindow()
        => Win32.VisibleTopWindows().Any(w => IsInstallerTitle(w.Title));

    /// <summary>启动 Trae（已在运行则跳过）。</summary>
    public static void Launch()
    {
        if (Find() is not null) return;
        var exes = CandidateExes();
        if (exes.Count == 0) throw new InvalidOperationException("未找到 Trae 安装路径（TRAE SOLO CN.exe）");

        var exe = exes[0];
        Log.WriteLine($"  启动: {exe}");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        })?.Dispose();
    }

    /// <summary>
    /// 等待 Trae 主窗口。
    /// 启动可能先触发自动更新的安装器（实测要几分钟），期间主窗口不存在；
    /// 只要安装器还在，就把截止时间往后顶。
    /// </summary>
    public static IntPtr? Wait(int timeoutSeconds, double installerGrace = 420)
    {
        var start = DateTime.Now;
        var deadline = start.AddSeconds(timeoutSeconds);
        var hardDeadline = deadline.AddSeconds(installerGrace);
        var notified = false;
        var nextTick = start.AddSeconds(30);

        while (DateTime.Now < deadline)
        {
            var win = Find();
            if (win is not null && Win32.WindowAlive(win.Value)) return win;

            if (HasInstallerWindow())
            {
                if (!notified)
                {
                    Log.WriteLine("  检测到 Trae 安装/更新窗口，等待安装完成后再找主窗口...");
                    notified = true;
                }
                deadline = DateTime.Now.AddSeconds(60) < hardDeadline
                    ? DateTime.Now.AddSeconds(60) : hardDeadline;
            }

            if (DateTime.Now >= nextTick)
            {
                var elapsed = (int)(DateTime.Now - start).TotalSeconds;
                Log.WriteLine($"  等待中... 已 {elapsed}s，当前可见顶层窗口 {Win32.VisibleTopWindows().Count} 个");
                nextTick = DateTime.Now.AddSeconds(30);
            }
            Thread.Sleep(1000);
        }
        return null;
    }
}