# _common.ps1 - 公共辅助：常量 / 输出交互 / 提权 / 进程 / winget / PATH / 重启恢复
# 被 install_wsl_kali.ps1 主入口 dot-source，不要单独运行

# ---------------- 常量 ----------------
$Script:KaliDistroName   = 'kali-linux'
$Script:ResumeTaskName   = 'WslKaliInstallResume'
$Script:RestartSignalled = $false
$Script:MinFreeGB        = 20   # Kali 建议预留的磁盘空间
$Script:DotNetSdkId      = 'Microsoft.DotNet.SDK.10'   # 仓库底座是 .NET 10
$Script:DotNetChannel    = '10.0'
$Script:UvPkgId          = 'astral-sh.uv'
$Script:FnmPkgId         = 'Schniz.fnm'
$Script:UseWingetConfig  = $true   # 优先用 winget configure + configurations/*.winget（-SkipWingetConfig 关闭）

# ---------------- 输出与交互 ----------------
function Write-Info  { param([string]$Msg) Write-Host $Msg -ForegroundColor Cyan }
function Write-Dim   { param([string]$Msg) Write-Host $Msg -ForegroundColor DarkGray }
function Write-Warn2 { param([string]$Msg) Write-Host $Msg -ForegroundColor Yellow }
function Write-Err2  { param([string]$Msg) Write-Host $Msg -ForegroundColor Red }

function Ask-Choice {
    # 返回 $true 表示用户确认；非交互模式直接采用默认值
    param([string]$Prompt, [bool]$DefaultYes)
    if ($NonInteractive) { return $DefaultYes }
    $suffix = $DefaultYes ? '[Y/n]' : '[y/N]'
    while ($true) {
        $answer = Read-Host "$Prompt $suffix"
        if ([string]::IsNullOrWhiteSpace($answer)) { return $DefaultYes }
        switch ($answer.Trim().ToUpper()) {
            'Y' { return $true }
            'N' { return $false }
        }
        Write-Dim '请输入 Y 或 N。'
    }
}

function Read-InputOrDefault {
    param([string]$Prompt, [string]$Default)
    if ($NonInteractive) { return $Default }
    $answer = Read-Host "$Prompt [$Default]"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
    return $answer.Trim()
}

# ---------------- 管理员提权 ----------------
function Assert-Admin {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { return }

    Write-Warn2 '此脚本需要管理员权限，正在请求 UAC 提权...'
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Install)          { $argList += @('-Install', $Install) }
    if ($KaliPath)         { $argList += @('-KaliPath', "`"$KaliPath`"") }
    if ($NonInteractive)   { $argList += '-NonInteractive' }
    if ($NoReboot)         { $argList += '-NoReboot' }
    if ($SkipWingetConfig) { $argList += '-SkipWingetConfig' }
    $pwsh = (Get-Process -Id $PID).Path
    $proc = Start-Process -FilePath $pwsh -ArgumentList ($argList -join ' ') -Verb RunAs -PassThru
    exit $proc.ExitCode
}

# ---------------- 进程辅助 ----------------
# wsl.exe 的消息文本本地化且编码特殊，一律用退出码判断结果
function Get-WslExitCode {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [int]$TimeoutSeconds = 300,
        [switch]$CloseStdin
    )
    if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) { return -1 }

    $stdoutFile = [System.IO.Path]::GetTempFileName()
    $stderrFile = [System.IO.Path]::GetTempFileName()
    $stdinFile  = $null
    try {
        if ($CloseStdin) {
            # 关闭 stdin，避免缺失运行时导致 wsl 卡在安装提示
            $stdinFile = [System.IO.Path]::GetTempFileName()
        }
        $argLine = $Arguments -join ' '
        $procArgs = @{
            FilePath               = 'wsl.exe'
            ArgumentList           = $argLine
            NoNewWindow            = $true
            PassThru               = $true
            RedirectStandardOutput = $stdoutFile
            RedirectStandardError  = $stderrFile
        }
        if ($stdinFile) { $procArgs.RedirectStandardInput = $stdinFile }
        $proc = Start-Process @procArgs
        if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
            try { $proc.Kill() } catch {}
            Write-Dim "wsl $($Arguments -join ' ') 超时（${TimeoutSeconds}s）。"
            return -1
        }
        return $proc.ExitCode
    }
    catch {
        Write-Dim "wsl $($Arguments -join ' ') 无法运行: $($_.Exception.Message)"
        return -1
    }
    finally {
        Remove-Item -LiteralPath $stdoutFile, $stderrFile -Force -ErrorAction SilentlyContinue
        if ($stdinFile) { Remove-Item -LiteralPath $stdinFile -Force -ErrorAction SilentlyContinue }
    }
}

# 直接调用 wsl.exe（不重定向）：安装引导需要真实控制台，输出直接打印到当前窗口
function Invoke-WslDirect {
    param([Parameter(Mandatory)][string[]]$Arguments)
    Write-Dim "> wsl $($Arguments -join ' ')"
    & wsl.exe @Arguments
    return $LASTEXITCODE
}

# ---------------- winget / PATH ----------------
function Install-WingetPackage {
    # winget 安装通用封装；winget 不存在时返回 -1
    param([Parameter(Mandatory)][string]$Id)
    if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) { return -1 }
    Write-Info "通过 winget 安装 $Id ..."
    Write-Dim '（下载安装可能需要几分钟。）'
    $proc = Start-Process -FilePath 'winget.exe' `
        -ArgumentList 'install', '--id', $Id, '--exact', '--silent', `
                      '--accept-source-agreements', '--accept-package-agreements' `
        -NoNewWindow -Wait -PassThru
    # 刷新会话 PATH（machine + user），否则刚装完的命令在当前会话检测不到
    if ($proc.ExitCode -eq 0) {
        $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
                    [Environment]::GetEnvironmentVariable('Path', 'User')
    }
    return $proc.ExitCode
}

function Invoke-WingetConfiguration {
    # 应用 configurations/<Name>.winget 配置文件（winget configure / DSC）
    # 返回退出码：0 = 已达成期望状态（幂等，重复运行只改缺失项）；-1 = 不可用
    param([Parameter(Mandatory)][string]$Name)
    if (-not $Script:UseWingetConfig) { return -1 }
    if (-not (Test-ToolOnPath 'winget.exe')) {
        Write-Dim '未找到 winget，跳过 winget configure 路径。'
        return -1
    }
    $configFile = Join-Path $PSScriptRoot "configurations\$Name.winget"
    if (-not (Test-Path -LiteralPath $configFile)) {
        Write-Dim "找不到配置文件 $configFile，跳过 winget configure 路径。"
        return -1
    }
    Write-Info "通过 winget configure 应用 $Name.winget ..."
    Write-Dim '（首次运行会下载 DSC 资源模块，可能需要几分钟。）'
    $proc = Start-Process -FilePath 'winget.exe' `
        -ArgumentList 'configure', '-f', "`"$configFile`"", '--accept-configuration-agreements', '--disable-interactivity' `
        -NoNewWindow -Wait -PassThru
    if ($proc.ExitCode -eq 0) {
        # 刷新会话 PATH，保证新装命令在当前会话可检测
        $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
                    [Environment]::GetEnvironmentVariable('Path', 'User')
    }
    else {
        Write-Dim "winget configure 返回 $($proc.ExitCode)，将尝试回退方案。"
        Write-Dim '提示: 若报"内部错误 -2146233079"，通常是缺少 VC++ 运行库，先执行: winget install Microsoft.VCRedist.2015+.x64'
    }
    return $proc.ExitCode
}

function Add-PathDirectory {
    param([string]$Dir)
    if (-not (Test-Path -LiteralPath $Dir)) { return }
    # 当前会话立即生效
    if (-not (($env:Path -split ';') -contains $Dir)) {
        $env:Path = "$Dir;$env:Path"
    }
    # 机器级 PATH 持久化（重复运行不重复追加）
    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    if (($machinePath -split ';') -notcontains $Dir) {
        [Environment]::SetEnvironmentVariable('Path', "$machinePath;$Dir", 'Machine')
        Write-Dim "已将 $Dir 加入系统 PATH（已打开的终端需重开生效）。"
    }
}

function Test-ToolOnPath {
    param([Parameter(Mandatory)][string]$Command)
    return [bool](Get-Command $Command -ErrorAction SilentlyContinue)
}

# ---------------- 重启恢复 ----------------
function Test-ServicingRebootPending {
    # CBS 服务组件待重启（dism 启用功能后的典型状态）
    return (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending')
}

function Remove-ResumeTask {
    if (Get-ScheduledTask -TaskName $Script:ResumeTaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $Script:ResumeTaskName -Confirm:$false -ErrorAction SilentlyContinue
    }
}

function Register-ResumeTask {
    Remove-ResumeTask
    $action   = New-ScheduledTaskAction -Execute (Get-Process -Id $PID).Path `
                  -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Install $Install -NonInteractive" + `
                  ($(if ($SkipWingetConfig) { ' -SkipWingetConfig' } else { '' })) `
                  -WorkingDirectory (Split-Path -Parent $PSCommandPath)
    $trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopIfGoingOnBatteries -AllowStartIfOnBatteries
    Register-ScheduledTask -TaskName $Script:ResumeTaskName -Action $action -Trigger $trigger `
        -Settings $settings -Description 'WSL+Kali 安装重启后自动继续' -Force | Out-Null
    Write-Dim "已注册恢复任务 $Script:ResumeTaskName（下次登录自动继续安装）。"
}

function Wait-ForReboot {
    $Script:RestartSignalled = $true   # 标记已安排重启，异常处理时保留恢复任务

    if ($NoReboot) {
        Write-Warn2 'Windows 组件已启用，需要重启才能生效。请手动重启后重新运行本脚本继续安装。'
        return
    }
    Write-Info ''
    Write-Info 'WSL 平台组件已安装，需要重启计算机激活。'
    Register-ResumeTask
    if (-not (Ask-Choice '是否立即重启？（请先保存所有工作）' $false)) {
        Write-Warn2 '请手动重启；登录后恢复任务会自动继续安装。'
        return
    }
    Restart-Computer -Force
}
