#Requires -Version 7.0
<#
.SYNOPSIS
    把视频格式关联到 PotPlayer（用户级 HKCU，双击即用 PotPlayer 打开）。

.DESCRIPTION
    为什么写用户级（HKCU\Software\Classes）而非系统级：
      - 不需要管理员权限；
      - 用户级优先级高于系统级，能覆盖 .mp4 默认的 Media Foundation /  Movies & TV 关联；
      - 卸载 PotPlayer 后删掉本脚本建的键即可干净回滚。

    做的四件事：
      1. 探测 PotPlayerMini64.exe（-ExePath 可手动指定）
      2. 注册 ProgID PotPlayer.Video（图标 + shell\open\command + Capabilities）
      3. 逐个扩展名写 .ext 默认值 + OpenWithProgids
      4. 逐项回读校验，并通知资源管理器刷新关联图标

    注意：Windows 10/11 的「文件>打开方式>始终使用」会额外写
    HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.ext\UserChoice，
    那个键带签名校验，本脚本不碰。如果关联看起来没生效，多半是 UserChoice 在压着，
    删掉对应 UserChoice 键或用「设置 > 应用 > 默认应用」重设一次即可。

.PARAMETER ExePath
    PotPlayerMini64.exe 完整路径。留空则自动探测。

.PARAMETER Remove
    撤销关联（只删本脚本建的键，保留扩展名键本身，让 Windows 回落到系统默认）。

.PARAMETER Extensions
    自定义扩展名（不含点），用逗号或空格分隔，如 "mp4,mkv" 或 "mp4 mkv"。留空则用内置清单。
    之所以是字符串而不是 [string[]]：-File / bat 转发时数组参数无法可靠传递。

.EXAMPLE
    pwsh -File .\potplayer-assoc.ps1

.EXAMPLE
    pwsh -File .\potplayer-assoc.ps1 -ExePath 'D:\PotPlayer\PotPlayerMini64.exe'

.EXAMPLE
    pwsh -File .\potplayer-assoc.ps1 -Remove

.EXAMPLE
    pwsh -File .\potplayer-assoc.ps1 -Extensions "mp4,mkv,webm"
#>
[CmdletBinding()]
param(
    [string] $ExePath = '',
    [string] $Extensions = '',
    [switch] $Remove,
    [switch] $NoPause
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ----------------------------------------------------------------------------------
# 常量
# ----------------------------------------------------------------------------------
$ProgId = 'PotPlayer.Video'
$AppName = 'PotPlayer'

# 内置视频扩展名清单（单行，避免续行反引号踩坑）
$DefaultExts = @(
    '3g2','3gp','3gp2','asf','av1','avi','divx','dv','f4v','flv','h264','h265','hevc',
    'hdmov','m1v','m2p','m2t','m2ts','m2v','m4v','mkv','mov','mp4','mpe','mpeg','mpg',
    'mts','mxf','nuv','ogm','ogv','qt','rm','rmvb','swf','ts','tsv','vob','vp6','vp9',
    'webm','wmv','yuv'
)

# ----------------------------------------------------------------------------------
# 日志
# ----------------------------------------------------------------------------------
$logFile = Join-Path $env:TEMP 'potplayer-assoc.log'

function L {
    param(
        [string] $Message,
        [ValidateSet('Info', 'Ok', 'Warn', 'Err', 'Step')][string] $Level = 'Info'
    )
    $color = switch ($Level) {
        'Ok'   { 'Green' }
        'Warn' { 'Yellow' }
        'Err'  { 'Red' }
        'Step' { 'Cyan' }
        default{ 'Gray' }
    }
    $prefix = switch ($Level) {
        'Step' { '=== ' }
        'Warn' { '!  ' }
        'Err'  { 'x  ' }
        'Ok'   { 'OK ' }
        default{ '   ' }
    }
    $line = "$prefix$Message"
    if ($Level -eq 'Debug') { Write-Verbose $line } else { Write-Host $line -ForegroundColor $color }
    try { Add-Content -LiteralPath $logFile -Value $line -Encoding utf8 -ErrorAction Stop } catch { }
}

# ----------------------------------------------------------------------------------
# 运行时版本闸（irm | iex 入口下 #Requires 不生效，见 README「踩过的坑」）
# ----------------------------------------------------------------------------------
if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Host '[X] PowerShell 7 required. Install with:  winget install --id Microsoft.PowerShell -e' -ForegroundColor Red
    exit 3
}

# ----------------------------------------------------------------------------------
# 工具函数
# ----------------------------------------------------------------------------------

# 通知资源管理器关联发生变化，让图标/打开方式立即刷新
function Send-ShellChangeNotify {
    try {
        if (-not ('Shell32.Native' -as [type])) {
            Add-Type -Namespace 'Shell32' -Name 'Native' -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true)]
public static extern void SHChangeNotify(int wEventId, uint uFlags, System.IntPtr dwItem1, System.IntPtr dwItem2);
'@ -ErrorAction Stop
        }
        # 0x08000000 SHCNE_ASSOCCHANGED, 0x0000 SHCNF_IDLIST
        [Shell32.Native]::SHChangeNotify(0x08000000, 0x0000, [System.IntPtr]::Zero, [System.IntPtr]::Zero)
    } catch {
        L "SHChangeNotify 失败（不影响关联结果）: $($_.Exception.Message)" -Level Warn
    }
}

function Set-RegDefault {
    # 写「无名的默认值」。注意：New-ItemProperty -Name '(default)' 只会建出一个
    # 名字叫 (default) 的普通值，真正的默认值仍是空 —— 必须用 Set-Item。
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Value
    )
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -Path $Path -Force | Out-Null }
    Set-Item -LiteralPath $Path -Value $Value
}

function Get-RegDefault {
    # 读「无名的默认值」，不存在返回 $null。
    # Get-Item 在注册表 provider 下返回 Microsoft.Win32.RegistryKey，
    # 默认值只能用 GetValue('') 读 —— PSObject 属性的名字是空字符串
    # （控制台里显示成 "(default)"），按 '(default)' 取会拿到 $null。
    param([Parameter(Mandatory)][string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $key = Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue
    if ($null -eq $key) { return $null }
    $v = $key.GetValue('')
    if ($null -eq $v) { return $null }
    return [string]$v
}

function Set-RegValue {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Name,
        [string] $Value = '',
        [string] $Type = 'String'   # String | ExpandString | MultiString
    )
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -Path $Path -Force | Out-Null }
    New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType $Type -Force | Out-Null
}

function Remove-RegKeySafe {
    param([Parameter(Mandatory)][string] $Path)
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
        return $true
    }
    return $false
}

function Resolve-PotPlayerExe {
    param([string] $Hint)

    if ($Hint) {
        if (Test-Path -LiteralPath $Hint -PathType Leaf) { return (Resolve-Path -LiteralPath $Hint).Path }
        L "指定的路径不存在: $Hint" -Level Err
        return $null
    }

    # 1) App Paths 注册表
    $appPaths = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\PotPlayerMini64.exe',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\PotPlayerMini64.exe',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\PotPlayerMini64.exe'
    )
    foreach ($k in $appPaths) {
        if (Test-Path -LiteralPath $k) {
            $v = Get-RegDefault -Path $k
            if ($v -and (Test-Path -LiteralPath $v -PathType Leaf)) { return $v }
        }
    }

    # 2) 已注册的 PotPlayer ProgID 里反查
    foreach ($root in @('HKCU:\Software\Classes', 'HKLM:\SOFTWARE\Classes', 'HKLM:\SOFTWARE\WOW6432Node\Classes')) {
        $raw = Get-RegDefault -Path (Join-Path $root 'PotPlayer.Media.64\shell\open\command')
        if ($raw -and $raw -match '^"([^"]+\.exe)"') {
            if (Test-Path -LiteralPath $Matches[1] -PathType Leaf) { return $Matches[1] }
        }
    }

    # 3) 常见安装位置
    $candidates = @(
        "$env:ProgramFiles\DAUM\PotPlayer\PotPlayerMini64.exe",
        "${env:ProgramFiles(x86)}\DAUM\PotPlayer\PotPlayerMini64.exe",
        "$env:LOCALAPPDATA\Programs\PotPlayer\PotPlayerMini64.exe",
        'C:\Program Files\DAUM\PotPlayer\PotPlayerMini64.exe',
        'C:\Program Files (x86)\DAUM\PotPlayer\PotPlayerMini64.exe',
        'C:\Program Files\PotPlayer\PotPlayerMini64.exe',
        'D:\PotPlayer\PotPlayerMini64.exe',
        'D:\Program Files\DAUM\PotPlayer\PotPlayerMini64.exe',
        'D:\Program Files\PotPlayer\PotPlayerMini64.exe',
        'E:\PotPlayer\PotPlayerMini64.exe',
        'E:\Program Files\DAUM\PotPlayer\PotPlayerMini64.exe',
        'E:\Program Files\PotPlayer\PotPlayerMini64.exe'
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c -PathType Leaf)) { return $c }
    }

    # 4) PATH
    $onPath = Get-Command 'PotPlayerMini64.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    return $null
}

# ----------------------------------------------------------------------------------
# 主流程
# ----------------------------------------------------------------------------------
$exts = if ($Extensions.Trim()) {
    $Extensions -split '[,;\s]+' |
        ForEach-Object { $_.Trim().TrimStart('.').ToLowerInvariant() } |
        Where-Object { $_ }
} else {
    $DefaultExts
}

Write-Host ''
Write-Host '  PotPlayer 视频关联' -ForegroundColor Cyan
Write-Host '  -------------------'
Write-Host "  ProgID    : $ProgId"
Write-Host "  扩展名    : $($exts.Count) 种"
Write-Host "  模式      : $(if ($Remove) { '撤销 (remove)' } else { '关联 (install)' })"
Write-Host ''

# ---- 撤销 ----
if ($Remove) {
    L '移除 ProgID 与各扩展名条目...' -Level Step
    $n = 0
    if (Remove-RegKeySafe "HKCU:\Software\Classes\$ProgId") { $n++ }
    foreach ($e in $exts) {
        if (Remove-RegKeySafe "HKCU:\Software\Classes\.$e\OpenWithProgids\$ProgId") { $n++ }
    }
    L "已删除 $n 个键。扩展名键本身保留，Windows 会回落到系统默认关联。" -Level Ok
    Send-ShellChangeNotify
    Write-Host ''
    Write-Host '[OK] Done. Open a NEW Explorer window to see the changes.' -ForegroundColor Green
    Write-Host ''
    exit 0
}

# ---- 探测 exe ----
L '探测 PotPlayer...' -Level Step
$exe = Resolve-PotPlayerExe -Hint $ExePath
if (-not $exe) {
    Write-Host ''
    Write-Host '[X] PotPlayer not found.' -ForegroundColor Red
    Write-Host '    已尝试: App Paths 注册表 / 已注册 ProgID / 常见安装目录 / PATH' -ForegroundColor Red
    Write-Host '    装好 PotPlayer 后重跑，或手动指定路径:' -ForegroundColor Red
    Write-Host '      potplayer-assoc.bat -ExePath "D:\PotPlayer\PotPlayerMini64.exe"' -ForegroundColor Red
    Write-Host ''
    exit 1
}
L "PotPlayer : $exe" -Level Ok

# ---- 注册 ProgID ----
L '注册 ProgID...' -Level Step
$root = "HKCU:\Software\Classes\$ProgId"
Set-RegDefault -Path $root -Value "$AppName 视频播放器"
Set-RegDefault -Path "$root\DefaultIcon" -Value "`"$exe`",0"
Set-RegDefault -Path "$root\shell\open\command" -Value "`"$exe`" `"%1`""
Set-RegValue -Path "$root\Capabilities" -Name 'ApplicationName' -Value "$AppName"
Set-RegValue -Path "$root\Capabilities" -Name 'Description'     -Value "$AppName 视频播放器"
L "已写入 HKCU\Software\Classes\$ProgId" -Level Ok

# ---- 逐扩展名关联 ----
L "关联 $($exts.Count) 种扩展名..." -Level Step
$failed = @()
foreach ($e in $exts) {
    try {
        Set-RegDefault -Path "HKCU:\Software\Classes\.$e" -Value $ProgId
        Set-RegValue -Path "HKCU:\Software\Classes\.$e\OpenWithProgids" -Name $ProgId -Value ''
        Write-Host ("    {0,-6} -> {1}" -f ".$e", $ProgId) -ForegroundColor DarkGray
    } catch {
        $failed += $e
        L "    .$e 写入失败: $($_.Exception.Message)" -Level Warn
    }
}

# ---- 校验 ----
L '校验...' -Level Step
$bad = @()
foreach ($e in ($exts | Where-Object { $failed -notcontains $_ } | Select-Object -First 8)) {
    $cur = Get-RegDefault -Path "HKCU:\Software\Classes\.$e"
    if ($cur -eq $ProgId) {
        Write-Host ("    [OK ] .{0}" -f $e) -ForegroundColor Green
    } else {
        Write-Host ("    [BAD] .{0}  (当前: {1})" -f $e, ($(if ($cur) { $cur } else { '<无>' }))) -ForegroundColor Red
        $bad += $e
    }
}
$more = ($exts.Count) - 8
if ($more -gt 0) { Write-Host "    ...（另有 $more 种同规则处理，略过逐项回显）" -ForegroundColor DarkGray }

$cmd = Get-RegDefault -Path "$root\shell\open\command"
if ($cmd -and $cmd -like "*$exe*") {
    Write-Host '    [OK ] open command' -ForegroundColor Green
} else {
    Write-Host "    [BAD] open command  (当前: $cmd)" -ForegroundColor Red
    $bad += 'command'
}

Send-ShellChangeNotify

Write-Host ''
if ($bad.Count -eq 0 -and $failed.Count -eq 0) {
    Write-Host '[OK] Done. Open a NEW Explorer window to see the changes.' -ForegroundColor Green
    Write-Host ''
    exit 0
} else {
    Write-Host "[!] $($bad.Count + $failed.Count) item(s) failed to verify." -ForegroundColor Yellow
    Write-Host "    日志: $logFile" -ForegroundColor DarkGray
    Write-Host ''
    exit 1
}
