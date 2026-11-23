#Requires -Version 7.0
<#
.SYNOPSIS
    安装 FFmpeg 到 E:\ffmpeg，并把 bin 目录加入用户级 PATH。

.DESCRIPTION
    为什么不用 winget：
      winget 的 --proxy 选项默认被管理员禁用；即使设置 HTTP_PROXY 等环境变量，
      其自带下载器在代理环境下仍会挂起（实测卡死 20 分钟无进展）。
      所以这里直接用 curl 走代理下载 zip 再解压 —— 实测 33 秒完成 245MB。

    脚本会先探测本机代理端口，探测到就用，没探测到就直连。
    已安装且版本号一致时会跳过（幂等）。

.PARAMETER Proxy
    代理地址，如 http://127.0.0.1:7897。留空则自动探测本机常见代理端口。

.PARAMETER InstallDir
    安装目录，默认 E:\ffmpeg。

.PARAMETER Version
    要安装的 FFmpeg 版本，默认 9.0.2。

.PARAMETER Force
    即使已安装也强制重新下载安装。

.EXAMPLE
    pwsh -File .\install-ffmpeg.ps1

.EXAMPLE
    pwsh -File .\install-ffmpeg.ps1 -Proxy http://127.0.0.1:7897
#>
[CmdletBinding()]
param(
    [string] $Proxy = '',
    [string] $InstallDir = 'E:\ffmpeg',
    [string] $Version = '9.0.2',
    [switch] $Force,
    [switch] $NoPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ----------------------------------------------------------------------------------
# 日志
# ----------------------------------------------------------------------------------
$logFile = Join-Path $env:TEMP 'install-ffmpeg.log'
$script:StepNo = 0
$script:StepTotal = 6

function L {
    param(
        [string] $Message,
        [ValidateSet('Info', 'Ok', 'Warn', 'Err', 'Step', 'Cmd', 'Debug')][string] $Level = 'Info'
    )
    $color = switch ($Level) {
        'Ok'    { 'Green' }
        'Warn'  { 'Yellow' }
        'Err'   { 'Red' }
        'Step'  { 'Cyan' }
        'Cmd'   { 'DarkGray' }
        'Debug' { 'DarkGray' }
        default { 'Gray' }
    }
    $ts = (Get-Date -Format 'HH:mm:ss')
    $prefix = switch ($Level) {
        'Step' { '=== ' }
        'Warn' { '!  ' }
        'Err'  { 'x  ' }
        'Ok'   { 'OK ' }
        'Cmd'  { '>  ' }
        default { '   ' }
    }
    $line = "[$ts] $prefix$Message"
    if ($Level -eq 'Debug') { Write-Verbose $line } else { Write-Host $line -ForegroundColor $color }
    try { Add-Content -LiteralPath $logFile -Value $line -Encoding utf8 -ErrorAction Stop } catch { }
}

function Step { param([string] $Title) $script:StepNo++; L ("步骤 {0}/{1} · {2}" -f $script:StepNo, $script:StepTotal, $Title) -Level Step }

# ----------------------------------------------------------------------------------
# 代理
# ----------------------------------------------------------------------------------
# 常见代理软件默认端口：Clash / Mihomo 系多在 7897，v2rayN 在 10809
$commonPorts = @(7897, 7890, 10809, 10808, 1080, 7891, 1081, 2080, 8888, 8080)

function Test-ProxyUsable {
    param([string] $Candidate, [int] $TimeoutSec = 8)
    try {
        $r = Invoke-WebRequest -Uri 'https://github.com' -Proxy $Candidate -UseBasicParsing `
            -TimeoutSec $TimeoutSec -Method Head
        return ($r.StatusCode -ge 200 -and $r.StatusCode -lt 400)
    } catch {
        return $false
    }
}

function Resolve-Proxy {
    param([string] $Explicit)

    if ($Explicit) {
        L "使用指定代理：$Explicit" -Level Info
        if (-not (Test-ProxyUsable -Candidate $Explicit -TimeoutSec 10)) {
            L "指定代理不可用，将尝试直连" -Level Warn
            return ''
        }
        return $Explicit
    }

    L '未指定代理，探测本机常见代理端口…' -Level Info
    foreach ($port in $commonPorts) {
        $cand = "http://127.0.0.1:$port"
        $open = Test-NetConnection -ComputerName 127.0.0.1 -Port $port -WarningAction SilentlyContinue -InformationLevel Quiet
        if (-not $open) { continue }
        L "  端口 $port 开放，验证是否为 HTTP 代理…" -Level Debug
        if (Test-ProxyUsable -Candidate $cand) {
            L "发现可用代理：$cand" -Level Ok
            return $cand
        }
        L "  端口 $port 开放但不是可用 HTTP 代理，跳过" -Level Debug
    }

    L '未发现可用代理，将直连下载' -Level Info
    return ''
}

# ----------------------------------------------------------------------------------
# 已安装检测
# ----------------------------------------------------------------------------------
function Get-InstalledVersion {
    $exe = Join-Path $InstallDir 'bin\ffmpeg.exe'
    if (-not (Test-Path -LiteralPath $exe)) { return '' }
    try {
        # 注意: ffmpeg 把版本信息写到 stderr，必须合并流并显式取单行字符串。
        # 若保留数组形态, `-match` 只返回布尔值且不会填充 $Matches。
        $line = (& $exe -version 2>&1 | Select-Object -First 1)
        if ($null -eq $line) { return '' }
        $text = [string]$line
        if ($text -match 'version\s+(?:n)?(\d+\.\d+(?:\.\d+)?)') { return $Matches[1] }
    } catch { }
    return ''
}

# ----------------------------------------------------------------------------------
# 主流程
# ----------------------------------------------------------------------------------
L "FFmpeg 安装程序 · PowerShell $($PSVersionTable.PSVersion)" -Level Info
L "日志：$logFile" -Level Info

$ffmpegExe = Join-Path $InstallDir 'bin\ffmpeg.exe'

# --- 1. 现状 ---
Step '检查现有安装'
$installedVer = Get-InstalledVersion
$skipInstall = $false

if ($installedVer -and -not $Force) {
    if ($installedVer -eq $Version) {
        L "FFmpeg $installedVer 已安装于 $InstallDir，跳过安装" -Level Ok
        L '如需强制重装请加 -Force' -Level Debug
        $skipInstall = $true
    } else {
        L "已安装版本 $installedVer，目标 $Version，继续更新" -Level Info
    }
} elseif ($installedVer -and $Force) {
    L "已安装 $installedVer，-Force 指定，重新下载" -Level Info
} else {
    L "未检测到 FFmpeg（$InstallDir），准备安装" -Level Info
}

# 已装且版本一致：仍要把 PATH 补齐并做验证，但不重新下载
if ($skipInstall) {
    Step '配置 PATH'
    $binDirSkip = Join-Path $InstallDir 'bin'
    if ($NoPath) {
        L '-NoPath 指定，跳过 PATH 配置' -Level Info
    } else {
        $up = [Environment]::GetEnvironmentVariable('Path', 'User')
        $upItems = @()
        if ($up) { $upItems = @($up -split ';' | Where-Object { $_ -and $_.Trim() }) }
        $inPath = $upItems | Where-Object { $_.Trim().TrimEnd('\').ToLowerInvariant() -eq $binDirSkip.ToLowerInvariant() }
        if ($inPath) {
            L "用户 PATH 已包含 $binDirSkip，跳过" -Level Ok
        } else {
            [Environment]::SetEnvironmentVariable('Path', ((@($upItems) + @($binDirSkip)) -join ';'), 'User')
            L "已加入用户级 PATH：$binDirSkip" -Level Ok
        }
    }

    Step '验证'
    $v = Get-InstalledVersion
    if ($v) { L "  ffmpeg 版本：$v" -Level Ok } else { L '  ffmpeg 无法执行' -Level Err }
    L ''
    L '无需重新安装。新开一个终端即可使用 ffmpeg / ffprobe / ffplay。' -Level Ok
    L "日志：$logFile" -Level Info
    exit 0
}

# --- 2. 代理 ---
Step '解析代理'
$proxy = Resolve-Proxy -Explicit $Proxy
if ($proxy) {
    $env:HTTP_PROXY = $proxy
    $env:HTTPS_PROXY = $proxy
    $env:ALL_PROXY = $proxy
    L "HTTP_PROXY / HTTPS_PROXY / ALL_PROXY 已设置" -Level Debug
} else {
    Remove-Item Env:HTTP_PROXY, Env:HTTPS_PROXY, Env:ALL_PROXY -ErrorAction SilentlyContinue
}

# --- 3. 下载 ---
Step "下载 FFmpeg $Version"
$url = "https://github.com/GyanD/codexffmpeg/releases/download/$Version/ffmpeg-$Version-full_build.zip"
$zipPath = Join-Path $env:TEMP "ffmpeg-$Version.zip"

L "地址：$url" -Level Info

# 代理链路偶发 reset(curl 35)，这里做多轮重试而非单次
$curlArgs = @('-L', '--connect-timeout', '20', '--max-time', '1800')
if ($proxy) { $curlArgs += @('--proxy', $proxy) }
$curlArgs += @('-o', $zipPath, $url)

$ok = $false
$maxRounds = if ($proxy) { 4 } else { 2 }   # 有代理时给更多重试机会
$sw = [Diagnostics.Stopwatch]::StartNew()

for ($round = 1; $round -le $maxRounds; $round++) {
    L ("下载中（第 {0}/{1} 轮）…" -f $round, $maxRounds) -Level Info
    # --retry 只重试瞬时错误；连接重置属于需要重开一轮的情况
    & curl.exe @curlArgs --retry 2 --retry-delay 3
    $rc = $LASTEXITCODE
    $sw.Stop()

    if ($rc -eq 0 -and (Test-Path -LiteralPath $zipPath)) {
        $ok = $true
        break
    }
    L "第 $round 轮失败（curl 退出码 $rc）" -Level Warn
    if ($round -lt $maxRounds) {
        Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds (3 * $round)
        L "$($round * 3) 秒后重试…" -Level Info
    }
}

if (-not $ok) {
    L '下载失败' -Level Err
    if ($proxy) { L "提示：代理不稳定。可换 -Proxy 指定其他地址，或改用直连（移除代理设置后重试）" -Level Warn }
    L "日志：$logFile" -Level Err
    exit 1
}
$zipMB = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
L ("下载完成：{0} MB，用时 {1}s" -f $zipMB, [int]$sw.Elapsed.TotalSeconds) -Level Ok

# --- 4. 解压 ---
Step '解压并安装'
$stageDir = Join-Path $env:TEMP ("ffmpeg-stage-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
L "暂存目录：$stageDir" -Level Debug

try {
    L '解压中…' -Level Info
    Expand-Archive -LiteralPath $zipPath -DestinationPath $stageDir -Force

    # 官方包内层是 ffmpeg-<ver>-full_build
    $inner = Get-ChildItem -LiteralPath $stageDir -Directory |
        Where-Object { $_.Name -like 'ffmpeg-*' } |
        Select-Object -First 1

    if (-not $inner) { throw "解压后未找到 ffmpeg-* 目录，zip 结构异常" }
    L "包内根目录：$($inner.Name)" -Level Debug

    foreach ($exe in 'ffmpeg.exe', 'ffprobe.exe', 'ffplay.exe') {
        if (-not (Test-Path -LiteralPath (Join-Path $inner.FullName "bin\$exe"))) {
            throw "包内缺少 bin\$exe，下载可能不完整"
        }
    }

    # 先把旧目录改名让位，而不是直接删除 ——
    # 万一 Move-Item 失败，备份还在，可无损回滚。
    $backupDir = "$InstallDir.old_$(Get-Date -Format yyyyMMddHHmmss)"
    $hasOld = $false
    if (Test-Path -LiteralPath $InstallDir) {
        L "旧目录让位（改名备份）→ $backupDir" -Level Info
        Rename-Item -LiteralPath $InstallDir -NewName ([System.IO.Path]::GetFileName($backupDir)) -Force
        $hasOld = $true
    }

    try {
        L "移动到 $InstallDir" -Level Info
        Move-Item -LiteralPath $inner.FullName -Destination $InstallDir -Force
    } catch {
        if ($hasOld) {
            L "移动失败，回滚旧目录…" -Level Warn
            Rename-Item -LiteralPath $backupDir -NewName ([System.IO.Path]::GetFileName($InstallDir)) -Force -ErrorAction SilentlyContinue
        }
        throw
    }

    if ($hasOld) {
        L "删除备份：$backupDir" -Level Info
        Remove-Item -LiteralPath $backupDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
catch {
    L "解压/安装失败：$($_.Exception.Message)" -Level Err
    Remove-Item -LiteralPath $stageDir -Recurse -Force -ErrorAction SilentlyContinue
    exit 1
}
finally {
    Remove-Item -LiteralPath $stageDir -Recurse -Force -ErrorAction SilentlyContinue
}

# --- 5. PATH ---
Step '配置 PATH'
$binDir = Join-Path $InstallDir 'bin'
if ($NoPath) {
    L '-NoPath 指定，跳过 PATH 配置' -Level Info
} else {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $items = @()
    if ($userPath) { $items = @($userPath -split ';' | Where-Object { $_ -and $_.Trim() }) }
    $exists = $items | Where-Object { $_.Trim().TrimEnd('\').ToLowerInvariant() -eq $binDir.ToLowerInvariant() }
    if ($exists) {
        L "用户 PATH 已包含 $binDir，跳过" -Level Ok
    } else {
        [Environment]::SetEnvironmentVariable('Path', ((@($items) + @($binDir)) -join ';'), 'User')
        L "已加入用户级 PATH：$binDir" -Level Ok
    }
    L '系统级 PATH 未改动（用户级优先级更高，且无需管理员）' -Level Debug
}

# ----------------------------------------------------------------------------------
# 汇总
# ----------------------------------------------------------------------------------
Step '验证'
$fail = 0
$ver = Get-InstalledVersion
if ($ver) {
    L "  ffmpeg 版本：$ver" -Level Ok
} else {
    L '  ffmpeg 无法执行' -Level Err
    $fail++
}

$cfg = (& $ffmpegExe -hide_banner -buildconf 2>&1 | Out-String)
$totalMB = [math]::Round((Get-ChildItem -LiteralPath $InstallDir -Recurse -File -ErrorAction SilentlyContinue |
        Measure-Object Length -Sum).Sum / 1MB)
L ("  安装大小：{0} MB" -f $totalMB) -Level Ok

L '  编码器抽查：' -Level Info
$enc = (& $ffmpegExe -hide_banner -encoders 2>&1 | Out-String)
foreach ($e in 'libx264', 'libx265', 'libvpx-vp9', 'libmp3lame', 'aac') {
    if ($enc -match [regex]::Escape($e)) { L "    libx264 类  $e" -Level Ok }
    else { L "    $e  缺失" -Level Warn }
}
if ($cfg -match '--enable-gpl') { L '    GPL 授权（商用需注意 GPL/LGPL 差异）' -Level Ok }
foreach ($hw in 'nvenc', 'cuvid', 'vaapi') {
    if ($cfg -match "--enable-$hw") { L "    硬件加速 $hw" -Level Ok }
}

L ''
if ($fail -eq 0) {
    L '安装完成。新开一个终端后可直接使用 ffmpeg / ffprobe / ffplay。' -Level Ok
} else {
    L "安装完成，但有 $fail 项异常，请查看日志：$logFile" -Level Err
}

# 清理下载的 zip（约 245MB）
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
    L "已清理下载包：$zipPath" -Level Debug
}

L "日志：$logFile" -Level Info
exit $fail
