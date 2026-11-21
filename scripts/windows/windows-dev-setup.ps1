#Requires -Version 7.0
<#
.SYNOPSIS
    Windows 开发环境一键安装 / 配置脚本（需要 PowerShell 7 / pwsh）。

.DESCRIPTION
    通过 winget 安装最新版的 PowerShell 7(pwsh)、Git、Git LFS（默认优先装到 D 盘）、
    gsudo（装到默认目录），并以「系统级」写入 VCPKG_ROOT / MAVEN_HOME / JAVA_HOME /
    NODE_HOME 四个环境变量，同时把这些目录追加进系统 PATH（去重、不重复写）。

    脚本会自动提权（需要管理员，因为要写系统环境变量）；
    从网络管道执行（irm | iex）时会先把自身落盘为临时文件再提权重启。

    ⚠️ 只支持 PowerShell 7（pwsh），不支持 Windows PowerShell 5.1 ——
    `#Requires -Version 7.0` 在 `-File` 与 `irm | iex` 两种入口下都会拦截 5.1。
    仓库内其余脚本（set-system-env / install-ffmpeg）与 .bat 包装器同样统一到 pwsh 7。

.PARAMETER Proxy
    代理地址，例如 http://127.0.0.1:7890 。
    会同时作用于：winget 子进程环境变量（HTTP_PROXY/HTTPS_PROXY/ALL_PROXY）、
    winget 自身 --proxy 参数（若当前 winget 版本支持）。
    也可用环境变量 DEV_SETUP_PROXY 传入，便于 irm | iex 方式使用。

.PARAMETER InstallRoot
    安装根目录。默认：存在 D 盘则用 D:\Program Files，否则 C:\Program Files。

.PARAMETER GitRoot / PwshRoot
    指定 Git / PowerShell 的安装目录（默认在 InstallRoot 下）。

.EXAMPLE
    pwsh -NoProfile -ExecutionPolicy Bypass -File .\windows-dev-setup.ps1

.EXAMPLE
    pwsh -NoProfile -ExecutionPolicy Bypass -File .\windows-dev-setup.ps1 -Proxy http://127.0.0.1:7890

.EXAMPLE
    # 托管到 raw 地址后，用 uv 那种一行流执行（必须用 pwsh，powershell.exe 会被 #Requires 拦下）
    $env:DEV_SETUP_PROXY = 'http://127.0.0.1:7890'
    pwsh -NoProfile -c "irm https://raw.githubusercontent.com/<you>/<repo>/main/install.ps1 | iex"
#>
[CmdletBinding()]
param(
    # 代理地址；留空则不启用。可用环境变量 DEV_SETUP_PROXY 兜底。
    [string] $Proxy = $env:DEV_SETUP_PROXY,

    # 安装根目录（Git / PowerShell）。留空自动选择 D 盘优先。
    [string] $InstallRoot = '',

    # 单独指定安装目录
    [string] $GitRoot = '',
    [string] $PwshRoot = '',

    # PowerShell 通道：stable = 正式版（默认），preview = 预览版
    [ValidateSet('stable', 'preview')]
    [string] $PwshChannel = 'stable',

    # 跳过某些步骤
    [switch] $SkipPwsh,
    [switch] $SkipGit,
    [switch] $SkipGitLfs,
    [switch] $SkipGsudo,
    [switch] $SkipEnvVars,

    # 不自动提权（默认会自动提权；不提权时写系统环境变量会失败）
    [switch] $NoElevate,

    # 只打印将要执行的命令，不实际执行
    [switch] $DryRun,

    # 结束后等待一个回车（交互式双击运行时用）
    [switch] $Pause
)

# 变量合并阶段的 try/catch 自行处理错误；此处不设全局 Stop，避免原生命令 stderr 直接终止脚本
$ErrorActionPreference = 'Continue'

# --------------------------------------------------------------------------------------
# 0.5 版本闸
# --------------------------------------------------------------------------------------
# 顶部的 `#Requires -Version 7.0` 只在「加载脚本文件」时生效；
# 而 `irm | iex` 送进来的是一段脚本块，引擎不解析 #Requires —— 5.1 会被静默放行。
# 所以这里显式补一道运行时检查，让两条入口的版本行为一致。
if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Host ''
    Write-Host '  [X] 需要 PowerShell 7（pwsh），当前是 ' $PSVersionTable.PSVersion -ForegroundColor Red
    Write-Host ''
    Write-Host '  请改用 pwsh 重新执行：' -ForegroundColor Yellow
    Write-Host '    pwsh -NoProfile -ExecutionPolicy Bypass -File <脚本路径>' -ForegroundColor Gray
    Write-Host '  若本机还没装 pwsh：' -ForegroundColor Yellow
    Write-Host '    winget install --id Microsoft.PowerShell -e' -ForegroundColor Gray
    Write-Host ''
    exit 3
}

# --------------------------------------------------------------------------------------
# 0. 全局配置
# --------------------------------------------------------------------------------------
$Script:StartTime = Get-Date
$Script:LogFile = Join-Path $env:TEMP ('windows-dev-setup-{0}.log' -f $Script:StartTime.ToString('yyyyMMdd-HHmmss'))
$Script:StepNo = 0
$Script:StepTotal = 6
$Script:Failed = New-Object System.Collections.ArrayList
$Script:Skipped = New-Object System.Collections.ArrayList

# 四个系统环境变量定义：名称 / 值 / 是否同时加入系统 PATH
$EnvVarSpecs = @(
    [pscustomobject]@{ Name = 'VCPKG_ROOT'; Value = 'E:\CPP\vcpkg'; OnPath = $true; Desc = 'vcpkg(C++ 包管理器)' }
    [pscustomobject]@{ Name = 'MAVEN_HOME'; Value = 'E:\Java\apache-maven-3.6.3'; OnPath = $true; Desc = 'Apache Maven' }
    [pscustomobject]@{ Name = 'JAVA_HOME'; Value = 'E:\Java\jdk-25.0.3+9'; OnPath = $true; Desc = 'JDK 25' }
    [pscustomobject]@{ Name = 'NODE_HOME'; Value = 'E:\nodejs'; OnPath = $true; Desc = 'Node.js' }
)

# --------------------------------------------------------------------------------------
# 1. 日志
# --------------------------------------------------------------------------------------
function Write-Log {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $Message,
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

    $ts = (Get-Date).ToString('HH:mm:ss')
    $line = switch ($Level) {
        'Step' { "[$ts] === $Message ===" }
        'Warn' { "[$ts] !  $Message" }
        'Err'  { "[$ts] x  $Message" }
        'Ok'   { "[$ts] √  $Message" }
        'Cmd'  { "[$ts] >  $Message" }
        default { "[$ts]    $Message" }
    }

    if ($Level -eq 'Debug') {
        Write-Verbose $line
    } else {
        Write-Host $line -ForegroundColor $color
    }

    try {
        Add-Content -LiteralPath $Script:LogFile -Value $line -Encoding UTF8 -ErrorAction Stop
    } catch { }
}

function Write-StepHeader {
    param([Parameter(Mandatory = $true)][string] $Title)
    $Script:StepNo++
    Write-Log ("步骤 {0}/{1} · {2}" -f $Script:StepNo, $Script:StepTotal, $Title) -Level Step
}

function Add-Skipped { param([string] $Text) [void]$Script:Skipped.Add($Text) }
function Add-Failed { param([string] $Text) [void]$Script:Failed.Add($Text) }

# --------------------------------------------------------------------------------------
# 2. 基础工具
# --------------------------------------------------------------------------------------
function Test-Admin {
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        return (New-Object Security.Principal.WindowsPrincipal $id).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { return $false }
}

function Test-Drive {
    param([string] $DriveLetter)
    if ([string]::IsNullOrWhiteSpace($DriveLetter)) { return $false }
    return (Test-Path -LiteralPath ($DriveLetter.TrimEnd(':') + ':\'))
}

function Resolve-InstallRoot {
    if (-not [string]::IsNullOrWhiteSpace($InstallRoot)) { return $InstallRoot.TrimEnd('\') }
    if (Test-Drive 'D') { return 'D:\Program Files' }
    return 'C:\Program Files'
}

function ConvertTo-Array { param($InputObject) @($InputObject) }

# 解析当前脚本自身路径（管道 iex 执行时 $PSCommandPath 为空，需要回退）
function Resolve-SelfPath {
    if ($PSCommandPath -and (Test-Path -LiteralPath $PSCommandPath)) { return $PSCommandPath }

    $url = $env:DEV_SETUP_URL
    if ($url) {
        $tmp = Join-Path $env:TEMP 'windows-dev-setup.ps1'
        Write-Log "从 $url 拉取脚本到 $tmp 以便提权重执行" -Level Debug
        Invoke-WebRequest -Uri $url -OutFile $tmp
        return $tmp
    }

    $body = $MyInvocation.MyCommand.Definition
    if ($body) {
        $tmp = Join-Path $env:TEMP 'windows-dev-setup.ps1'
        # utf8BOM：保持仓库约定，5.1 万一被误调用也不会因 GBK 解码而语法错误
        Set-Content -LiteralPath $tmp -Value $body -Encoding utf8BOM
        return $tmp
    }
    return $null
}

# 解析 pwsh 可执行文件路径（提权重启用）。
# 脚本已锁定 PS 7，所以这里只认 pwsh —— 不再用 (Get-Process -Id $PID).Path 猜宿主，
# 避免在 ISE / VS Code 集成终端等宿主里拿到 powershell.exe 又降级回去。
function Resolve-PwshPath {
    $cmd = Get-Command 'pwsh' -CommandType Application -ErrorAction SilentlyContinue |
           Select-Object -First 1
    if ($cmd -and $cmd.Source) { return $cmd.Source }

    foreach ($p in @(
            "$env:ProgramFiles\PowerShell\7\pwsh.exe",
            "${env:ProgramFiles(x86)}\PowerShell\7\pwsh.exe")) {
        if ($p -and (Test-Path -LiteralPath $p)) { return $p }
    }
    return $null
}

function Invoke-SelfElevated {
    $self = Resolve-SelfPath
    if (-not $self) {
        Write-Log '无法定位脚本自身路径，请右键以管理员身份在 PowerShell 中重新执行。' -Level Err
        return $false
    }

    $psExe = Resolve-PwshPath
    if (-not $psExe) {
        Write-Log '找不到 pwsh.exe，无法自动提权。请先安装 PowerShell 7 后重试。' -Level Err
        return $false
    }

    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $self))
    if ($Proxy)                    { $argList += @('-Proxy', ('"{0}"' -f $Proxy)) }
    if ($InstallRoot)              { $argList += @('-InstallRoot', ('"{0}"' -f $InstallRoot)) }
    if ($GitRoot)                  { $argList += @('-GitRoot', ('"{0}"' -f $GitRoot)) }
    if ($PwshRoot)                 { $argList += @('-PwshRoot', ('"{0}"' -f $PwshRoot)) }
    if ($PwshChannel -eq 'preview') { $argList += '-PwshChannel'; $argList += 'preview' }
    if ($SkipPwsh)                 { $argList += '-SkipPwsh' }
    if ($SkipGit)                  { $argList += '-SkipGit' }
    if ($SkipGitLfs)               { $argList += '-SkipGitLfs' }
    if ($SkipGsudo)                { $argList += '-SkipGsudo' }
    if ($SkipEnvVars)              { $argList += '-SkipEnvVars' }
    if ($DryRun)                   { $argList += '-DryRun' }
    if ($Pause)                    { $argList += '-Pause' }

    Write-Log '需要管理员权限，正在弹出 UAC 提权窗口…' -Level Info
    try {
        Start-Process -FilePath $psExe -ArgumentList $argList -Verb RunAs | Out-Null
        return $true
    } catch {
        Write-Log ("提权失败：{0}" -f $_.Exception.Message) -Level Err
        return $false
    }
}

# --------------------------------------------------------------------------------------
# 3. 代理
# --------------------------------------------------------------------------------------
$script:UseProxy = -not [string]::IsNullOrWhiteSpace($Proxy)

function Initialize-Proxy {
    if (-not $script:UseProxy) {
        Write-Log '未设置代理，直连。' -Level Debug
        return
    }

    $Proxy = $Proxy.Trim().TrimEnd('/')
    Write-Log "已启用代理：$Proxy" -Level Info

    # 供原生命令（curl/git/gh 等）继承
    foreach ($name in @('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY')) {
        Set-Item -Path ("Env:{0}" -f $name) -Value $Proxy
    }
    # 同时设置小写形式，兼容部分工具
    Set-Item -Path 'Env:http_proxy' -Value $Proxy
    Set-Item -Path 'Env:https_proxy' -Value $Proxy
}

# winget 从 1.10 起才支持 --proxy，需探测当前版本是否支持
function Get-WingetProxyArgs {
    if (-not $script:UseProxy) { return @() }
    try {
        $help = (& winget install --help 2>&1 | Out-String)
        if ($help -match '--proxy') {
            return @('--proxy', $Proxy)
        }
    } catch { }
    return @()
}

# --------------------------------------------------------------------------------------
# 4. winget 封装
# --------------------------------------------------------------------------------------
function Initialize-Winget {
    Write-Log '检查 winget…' -Level Info
    $cmd = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $cmd) {
        throw '未找到 winget。请先安装「应用安装程序」(App Installer) 或从 Microsoft Store 更新到最新 Windows 版本。'
    }

    $ver = (& winget --version 2>$null | Out-String).Trim()
    Write-Log "winget 版本：$ver" -Level Ok

    if ($DryRun) { return }

    Write-Log '刷新 winget 软件源…' -Level Info
    & winget source update --disable-interactivity 2>&1 | ForEach-Object { Write-Log $_ -Level Debug }
}

function Get-WingetInstalledVersion {
    param([Parameter(Mandatory = $true)][string] $Id)

    $out = (& winget list --id $Id -e --accept-source-agreements --disable-interactivity 2>$null | Out-String)
    if (-not $out) { return $null }
    if ($out -match 'No installed package found') { return $null }

    $m = [regex]::Match($out, '(?m)^.*?' + [regex]::Escape($Id) + '\s+([^\s]+)')
    if ($m.Success) { return $m.Groups[1].Value.Trim() }
    return $null
}

# 升级已装包 / 未装则安装，达成「最新版」目标
function Sync-WingetPackage {
    param(
        [Parameter(Mandatory = $true)][string] $Id,
        [string] $DisplayName = '',
        [string] $Location = '',
        [string[]] $ExtraArgs = @()
    )

    if (-not $DisplayName) { $DisplayName = $Id }

    $installed = $null
    if (-not $DryRun) { $installed = Get-WingetInstalledVersion -Id $Id }
    if ($DryRun) { $installed = '<dry-run>' }

    $proxyArgs = Get-WingetProxyArgs

    if ($installed) {
        Write-Log ("{0} 已安装（{1}），检查是否有更新…" -f $DisplayName, $installed) -Level Info
        $args = @('upgrade', '--id', $Id, '-e', '--source', 'winget', '--silent',
            '--accept-source-agreements', '--accept-package-agreements', '--disable-interactivity') +
            $ExtraArgs + $proxyArgs

        if ($DryRun) {
            Write-Log ("[DryRun] winget {0}" -f ($args -join ' ')) -Level Cmd
            return $true
        }

        & winget @args 2>&1 | ForEach-Object { Write-Log $_ -Level Debug }
        $code = $LASTEXITCODE
        if ($code -eq 0) {
            Write-Log "$DisplayName 已更新到最新版" -Level Ok
        } elseif ("$code" -eq '-1978335189') {
            Write-Log "$DisplayName 已是最新版本（$installed）" -Level Ok
        } else {
            Write-Log ("{0} 升级返回码 {1}，已保留现有安装。" -f $DisplayName, $code) -Level Warn
        }
        return $true
    }

    Write-Log "安装 $DisplayName（$Id）…" -Level Info
    $args = @('install', '--id', $Id, '-e', '--source', 'winget', '--silent',
        '--accept-source-agreements', '--accept-package-agreements', '--disable-interactivity') +
        $ExtraArgs
    if ($Location) { $args += @('--location', $Location) }
    $args += $proxyArgs

    if ($DryRun) {
        Write-Log ("[DryRun] winget {0}" -f ($args -join ' ')) -Level Cmd
        return $true
    }

    & winget @args 2>&1 | ForEach-Object { Write-Log $_ -Level Debug }
    $code = $LASTEXITCODE
    if ($code -eq 0) { return $true }

    # 指定目录失败时，退回默认目录再试一次（部分安装包不支持自定义路径）
    if ($Location) {
        Write-Log "指定目录 $Location 安装失败（返回码 $code），改用默认目录重试…" -Level Warn
        $retry = @('install', '--id', $Id, '-e', '--source', 'winget', '--silent',
            '--accept-source-agreements', '--accept-package-agreements', '--disable-interactivity') +
            $ExtraArgs + $proxyArgs
        & winget @retry 2>&1 | ForEach-Object { Write-Log $_ -Level Debug }
        return ($LASTEXITCODE -eq 0)
    }
    return $false
}

# --------------------------------------------------------------------------------------
# 5. 环境变量 / PATH
# --------------------------------------------------------------------------------------
function Get-MachinePath {
    return [Environment]::GetEnvironmentVariable('Path', 'Machine')
}

function Set-EnvVarMachine {
    param([string] $Name, [string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "环境变量 $Name 的值为空"
    }

    $old = [Environment]::GetEnvironmentVariable($Name, 'Machine')
    if ($old -eq $Value) {
        Write-Log ("系统变量 {0} 已是 {1}，跳过" -f $Name, $Value) -Level Ok
        return $false
    }

    if ($DryRun) {
        Write-Log ("[DryRun] 设置系统变量 {0} = {1}（原：{2}）" -f $Name, $Value, $old) -Level Cmd
        return $false
    }

    [Environment]::SetEnvironmentVariable($Name, $Value, 'Machine')
    Write-Log ("系统变量 {0}: {1} -> {2}" -f $Name, $(if ($old) { $old } else { '<空>' }), $Value) -Level Ok
    return $true
}

function Add-MachinePathEntry {
    param([string] $Entry)

    if ([string]::IsNullOrWhiteSpace($Entry)) { return $false }
    $normalized = $Entry.Trim().TrimEnd('\')
    $current = Get-MachinePath
    $items = @()

    if ($current) {
        $items = $current -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    }

    $exists = $false
    foreach ($i in $items) {
        if ($i.Trim().TrimEnd('\').ToLowerInvariant() -eq $normalized.ToLowerInvariant()) { $exists = $true; break }
    }
    if ($exists) {
        Write-Log "系统 PATH 已包含 $normalized，跳过" -Level Ok
        return $false
    }

    if ($DryRun) {
        Write-Log "[DryRun] 追加系统 PATH：$normalized" -Level Cmd
        return $false
    }

    $newPath = (@($items) + @($normalized)) -join ';'
    [Environment]::SetEnvironmentVariable('Path', $newPath, 'Machine')
    Write-Log "已追加到系统 PATH：$normalized" -Level Ok
    return $true
}

function Send-EnvChangeBroadcast {
    if ($DryRun) { return }
    try {
        if (-not ('Win32EnvBroadcast' -as [type])) {
            Add-Type -Namespace 'Win32' -Name 'EnvBroadcast' -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
public static extern System.IntPtr SendMessageTimeout(System.IntPtr hWnd, uint Msg, System.UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out System.UIntPtr lResult);
'@
        }
        $result = [System.UIntPtr]::Zero
        $HWND_BROADCAST = [System.IntPtr] 0xffff
        $WM_SETTINGCHANGE = 0x1A
        $SMTO_ABORTIFHUNG = 0x2
        [void] [Win32EnvBroadcast]::SendMessageTimeout($HWND_BROADCAST, $WM_SETTINGCHANGE, [System.UIntPtr]::Zero, 'Environment', $SMTO_ABORTIFHUNG, 5000, [ref] $result)
        Write-Log '已广播环境变量变更（WM_SETTINGCHANGE），新开的终端立即生效。' -Level Debug
    } catch {
        Write-Log ('广播环境变量变更失败：{0}（不影响本次设置，只是需重开终端）' -f $_.Exception.Message) -Level Debug
    }
}

# --------------------------------------------------------------------------------------
# 6. 版本校验
# --------------------------------------------------------------------------------------
function Test-CommandVersion {
    param(
        [Parameter(Mandatory = $true)][string] $Command,
        [string[]] $Arguments = @('--version')
    )

    if ($DryRun) { return $null }
    $cmd = Get-Command $Command -ErrorAction SilentlyContinue
    if (-not $cmd) { return $null }

    try {
        $out = (& $Command @Arguments 2>&1 | Select-Object -First 1 | Out-String).Trim()
        if ($out) { return $out }
    } catch { }
    return $null
}

# --------------------------------------------------------------------------------------
# 7. 主流程
# --------------------------------------------------------------------------------------
function Invoke-Main {
    Write-Log 'Windows 开发环境一键配置 · PowerShell 7 / Git / Git LFS / gsudo / 环境变量' -Level Info
    Write-Log ("完整日志：{0}" -f $Script:LogFile) -Level Info

    # --- 提权 ---
    if (-not (Test-Admin)) {
        if ($NoElevate) {
            Write-Log '当前不是管理员且指定了 -NoElevate，环境变量步骤可能失败。' -Level Warn
        } else {
            if (Invoke-SelfElevated) {
                Write-Log '已在新窗口中以管理员身份继续执行，本窗口即将退出。' -Level Ok
                return
            }
            Add-Failed '提权'
        }
    }

    # --- 0. 代理 + winget ---
    Initialize-Proxy

    Write-StepHeader '检查 winget'
    try {
        Initialize-Winget
    } catch {
        Write-Log $_.Exception.Message -Level Err
        Add-Failed 'winget 检查'
    }

    $root = Resolve-InstallRoot
    $gitDir = if ($GitRoot) { $GitRoot.TrimEnd('\') } else { "$root\Git" }
    $pwshDir = if ($PwshRoot) { $PwshRoot.TrimEnd('\') } else { "$root\PowerShell\7" }
    Write-Log "安装根目录：$root" -Level Info
    if ($script:UseProxy) { Write-Log "代理：$Proxy" -Level Info }

    # --- 1. PowerShell 7 ---
    Write-StepHeader "安装 PowerShell 7（$PwshChannel）"
    if ($SkipPwsh) {
        Add-Skipped 'PowerShell 7'
    } else {
        $pwshId = if ($PwshChannel -eq 'preview') { 'Microsoft.PowerShell.Preview' } else { 'Microsoft.PowerShell' }
        try {
            $ok = Sync-WingetPackage -Id $pwshId -DisplayName "PowerShell 7 ($PwshChannel)" -Location $pwshDir
            if ($ok) {
                $v = Test-CommandVersion -Command 'pwsh' -Arguments @('-NoProfile', '-v')
                if ($v) { Write-Log "验证：pwsh $v" -Level Ok } else { Write-Log '当前会话尚找不到 pwsh，新开终端后可用。' -Level Info }
            } else {
                Add-Failed "PowerShell 7 ($pwshId)"
            }
        } catch {
            Write-Log $_.Exception.Message -Level Err
            Add-Failed "PowerShell 7 ($pwshId)"
        }
    }

    # --- 2. Git ---
    Write-StepHeader '安装 Git（最新版，优先 D 盘）'
    if ($SkipGit) {
        Add-Skipped 'Git'
    } else {
        try {
            $ok = Sync-WingetPackage -Id 'Git.Git' -DisplayName 'Git' -Location $gitDir
            if ($ok) {
                # 安装器在自定义目录安装时不一定写 PATH，这里兜底补一条
                Add-MachinePathEntry -Entry (Join-Path $gitDir 'cmd') | Out-Null
                $v = Test-CommandVersion -Command 'git' -Arguments @('--version')
                if ($v) { Write-Log "验证：$v" -Level Ok } else { Write-Log '当前会话尚找不到 git，新开终端后可用。' -Level Info }
            } else {
                Add-Failed 'Git (Git.Git)'
            }
        } catch {
            Write-Log $_.Exception.Message -Level Err
            Add-Failed 'Git (Git.Git)'
        }
    }

    # --- 3. Git LFS ---
    Write-StepHeader '安装 Git LFS（最新版）'
    if ($SkipGitLfs) {
        Add-Skipped 'Git LFS'
    } else {
        try {
            $lfsDir = Join-Path $gitDir 'lfs'
            $ok = Sync-WingetPackage -Id 'Git.LFS' -DisplayName 'Git LFS' -Location $lfsDir
            if ($ok) {
                $v = Test-CommandVersion -Command 'git' -Arguments @('lfs', 'version')
                if ($v) { Write-Log "验证：$v" -Level Ok } else { Write-Log '当前会话尚找不到 git-lfs，新开终端后可用。' -Level Info }
            } else {
                Add-Failed 'Git LFS (Git.LFS)'
            }
        } catch {
            Write-Log $_.Exception.Message -Level Err
            Add-Failed 'Git LFS (Git.LFS)'
        }
    }

    # --- 4. gsudo ---
    Write-StepHeader '安装 gsudo（sudo for Windows）'
    if ($SkipGsudo) {
        Add-Skipped 'gsudo'
    } else {
        try {
            # gsudo 是 MSI 安装包，不支持自定义目录；装到默认的 C:\Program Files\gsudo
            $ok = Sync-WingetPackage -Id 'gerardog.gsudo' -DisplayName 'gsudo'
            if ($ok) {
                $v = Test-CommandVersion -Command 'gsudo' -Arguments @('--version')
                if ($v) { Write-Log "验证：$v" -Level Ok } else { Write-Log '当前会话尚找不到 gsudo，新开终端后可用。' -Level Info }
            } else {
                Add-Failed 'gsudo (gerardog.gsudo)'
            }
        } catch {
            Write-Log $_.Exception.Message -Level Err
            Add-Failed 'gsudo (gerardog.gsudo)'
        }
    }

    # --- 5. 环境变量 ---
    Write-StepHeader '写入系统环境变量'
    if ($SkipEnvVars) {
        Add-Skipped '系统环境变量'
    } elseif (-not (Test-Admin) -and -not $DryRun) {
        Write-Log '非管理员身份无法写系统环境变量，已跳过。' -Level Warn
        Add-Failed '系统环境变量（需要管理员）'
    } else {
        try {
            foreach ($spec in $EnvVarSpecs) {
                try {
                    if (-not (Test-Path -LiteralPath $spec.Value) -and -not $DryRun) {
                        New-Item -ItemType Directory -Path $spec.Value -Force | Out-Null
                        Write-Log ("已创建目录 {0}" -f $spec.Value) -Level Info
                    }
                    Set-EnvVarMachine -Name $spec.Name -Value $spec.Value | Out-Null
                    if ($spec.OnPath) { Add-MachinePathEntry -Entry $spec.Value | Out-Null }
                } catch {
                    Write-Log ("{0} 配置失败：{1}" -f $spec.Name, $_.Exception.Message) -Level Err
                    Add-Failed $spec.Name
                }
            }
            Send-EnvChangeBroadcast
        } catch {
            Write-Log $_.Exception.Message -Level Err
            Add-Failed '系统环境变量'
        }
    }

    # ----------------------------------------------------------------------------------
    # 8. 汇总
    # ----------------------------------------------------------------------------------
    $elapsed = (Get-Date) - $Script:StartTime
    Write-Host ''
    Write-Host '  结果汇总' -ForegroundColor Cyan
    Write-Host '  ------------------------------------------------------------' -ForegroundColor DarkGray

    Write-Host '  工具版本：' -ForegroundColor Gray
    $checks = @(
        @{ Label = 'pwsh      '; Cmd = 'pwsh'; Args = @('-NoProfile', '-v') },
        @{ Label = 'git       '; Cmd = 'git'; Args = @('--version') },
        @{ Label = 'git-lfs   '; Cmd = 'git'; Args = @('lfs', 'version') },
        @{ Label = 'gsudo     '; Cmd = 'gsudo'; Args = @('--version') },
        @{ Label = 'java      '; Cmd = 'java'; Args = @('-version') },
        @{ Label = 'maven     '; Cmd = 'mvn'; Args = @('-v') },
        @{ Label = 'node      '; Cmd = 'node'; Args = @('-v') }
    )
    foreach ($c in $checks) {
        $v = Test-CommandVersion -Command $c.Cmd -Arguments $c.Args
        if ($v) {
            Write-Host ("    {0}{1}" -f $c.Label, $v) -ForegroundColor Gray
        } else {
            Write-Host ("    {0}未安装或不在当前 PATH" -f $c.Label) -ForegroundColor DarkGray
        }
    }

    Write-Host '  环境变量（系统级）：' -ForegroundColor Gray
    foreach ($spec in $EnvVarSpecs) {
        $cur = [Environment]::GetEnvironmentVariable($spec.Name, 'Machine')
        if ($cur -eq $spec.Value) {
            Write-Host ("    {0,-12} = {1}" -f $spec.Name, $cur) -ForegroundColor Gray
        } elseif ($DryRun) {
            Write-Host ("    {0,-12} 将被设置为 {1}（当前 {2}）" -f $spec.Name, $spec.Value, $(if ($cur) { $cur } else { '<未设置>' })) -ForegroundColor DarkGray
        } else {
            Write-Host ("    {0,-12} = {1}  ← 与预期不符（预期 {2}）" -f $spec.Name, $(if ($cur) { $cur } else { '<未设置>' }), $spec.Value) -ForegroundColor Yellow
            Add-Failed ($spec.Name + ' 值不一致')
        }
    }

    if ($DryRun) {
        Write-Host '  DryRun 模式：以上命令未实际执行，无失败项属正常。' -ForegroundColor Cyan
    }

    if ($Script:Skipped.Count -gt 0) {
        Write-Host ("  跳过：{0}" -f ($Script:Skipped -join ', ')) -ForegroundColor DarkGray
    }
    if ($Script:Failed.Count -gt 0) {
        Write-Host ("  失败/需人工确认：{0}" -f ($Script:Failed -join ', ')) -ForegroundColor Yellow
    } elseif (-not $DryRun) {
        Write-Host '  全部步骤成功。' -ForegroundColor Green
    }

    Write-Host ("  耗时 {0:N1}s" -f $elapsed.TotalSeconds) -ForegroundColor DarkGray
    Write-Host ("  日志：{0}" -f $Script:LogFile) -ForegroundColor DarkGray
    Write-Host '  ------------------------------------------------------------' -ForegroundColor DarkGray
    Write-Host '  提示：新开一个 PowerShell / CMD 窗口，环境变量才生效。' -ForegroundColor DarkGray
    Write-Host ''

    if ($Pause) {
        try { [void] (Read-Host '按 Enter 键退出') } catch { }
    }

    if ($Script:Failed.Count -gt 0) { exit 1 } else { exit 0 }
}

Invoke-Main
