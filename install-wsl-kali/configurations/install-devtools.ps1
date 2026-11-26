#Requires -Version 5.1
<#
.SYNOPSIS
    安装开发基础工具链（PowerShell 7 / Git / Git LFS / gsudo），默认落盘到 D 盘，D 盘不可用时回落 C 盘。

.DESCRIPTION
    为什么不能只靠 devtools.winget：
      winget configure 使用的 Microsoft.WinGet.DSC/WinGetPackage 资源只暴露
      Id / Source / Version / Ensure / MatchOption / UseLatest / InstallMode 这几个参数
      （见模块 Microsoft.WinGet.DSC.psm1 的 class WinGetPackage），没有安装路径项，
      声明式配置表达不了"装到 D 盘"。所以路径决策和安装都放在本脚本，改用
      winget install --location；devtools.winget 保留为幂等兜底（已装的项全部跳过，
      不会把 D 盘上的东西重装回 C 盘）。

    落盘位置（按顺序决策）：
      1) D:\<App>                  —— D: 存在且可访问时优先（对齐本机既有习惯：D:\Git、D:\Git LFS）
      2) C:\Program Files\<App>    —— D: 不存在 / 空光驱 / 未挂载盘符时回落
      3) -BaseDir <根目录>         —— 强制根目录，例如 -BaseDir 'D:\Program Files'
      4) -SkipLocation             —— 不传 --location，完全按安装器默认位置

    已知限制（务实说明，别指望 100%）：
      - Git（inno 安装器）认 /DIR=，--location 基本一定生效。
      - Git LFS 不单独指定路径：它的安装器跟随已装的 Git 目录，Git 在 D 盘则 LFS 也在 D 盘。
      - PowerShell 7 / gsudo 是 msi/wix 安装包，能否吃下 --location 由安装包自身决定；
        脚本兜底逻辑是"带路径失败就去掉路径重试"，最终落到哪个盘以结尾打印的真实路径为准。
      - PowerShell 7 在 winget 源里默认给的是 msix（装进 WindowsApps，无法指定路径），
        所以脚本强制 --installer-type msi。
      - 装完需新开终端，PATH 才会包含新目录。

.EXAMPLE
    gsudo pwsh -NoProfile -File .\install-wsl-kali\configurations\install-devtools.ps1

.EXAMPLE
    # 先看会执行什么，不实际安装
    .\install-devtools.ps1 -WhatIf

.EXAMPLE
    # 想沿用 Program Files 结构
    .\install-devtools.ps1 -BaseDir 'D:\Program Files'
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$BaseDir,
    [switch]$SkipLocation,
    [int]$MinFreeGB = 2
)

$ErrorActionPreference = 'Stop'

function Write-Step { param([string]$Msg) Write-Host "==> $Msg" -ForegroundColor Cyan }
function Write-Note { param([string]$Msg) Write-Host "    $Msg" -ForegroundColor DarkGray }
function Write-WarnMsg { param([string]$Msg) Write-Host $Msg -ForegroundColor Yellow }

# ---------------- 提权 ----------------
function Assert-Elevated {
    # 机器级安装需要管理员；非管理员时原样转发参数重新拉起自己
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { return }

    Write-WarnMsg '安装到 Program Files / 系统盘根目录需要管理员权限，正在请求 UAC 提权...'
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($BaseDir)      { $argList += @('-BaseDir', "`"$BaseDir`"") }
    if ($SkipLocation) { $argList += '-SkipLocation' }
    $argList += @('-MinFreeGB', $MinFreeGB)

    $exe  = (Get-Process -Id $PID).Path
    $proc = Start-Process -FilePath $exe -ArgumentList ($argList -join ' ') -Verb RunAs -PassThru
    exit $proc.ExitCode
}

# ---------------- 安装根目录决策 ----------------
function Resolve-InstallBase {
    param([string]$Explicit)

    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit)) { throw "指定的 -BaseDir 不存在：$Explicit" }
        $resolved = $Explicit.TrimEnd('\')
        Write-Note "使用显式指定的根目录（-BaseDir）：$resolved"
        return $resolved
    }

    $drive  = Get-PSDrive -Name D -PSProvider FileSystem -ErrorAction SilentlyContinue
    $usable = $false
    if ($drive) {
        try { $usable = Test-Path -LiteralPath 'D:\' } catch { $usable = $false }
    }
    if (-not $usable) {
        Write-WarnMsg 'D 盘不存在或不可访问（空光驱 / 未挂载盘符都算），回落到 C:\Program Files。'
        return 'C:\Program Files'
    }

    $freeGB = 0
    if ($drive.Free) { $freeGB = [math]::Round($drive.Free / 1GB, 1) }
    if ($freeGB -lt $MinFreeGB) {
        Write-WarnMsg ("D 盘可用空间仅 {0} GB（阈值 {1} GB），仍按规则装 D 盘；空间不足请改用 -BaseDir。" -f $freeGB, $MinFreeGB)
    }
    else {
        Write-Note ("D 盘可用 {0} GB，使用 D 盘。" -f $freeGB)
    }
    return 'D:\'
}

# ---------------- winget 封装 ----------------
function Test-PackageInstalled {
    param([Parameter(Mandatory)][string]$Id)
    $out = & winget.exe list --id $Id --exact --accept-source-agreements 2>$null
    return (($LASTEXITCODE -eq 0) -and (($out | Out-String) -match [regex]::Escape($Id)))
}

function Invoke-WingetInstall {
    param(
        [Parameter(Mandatory)][string]$Id,
        [string]$Location,
        [string[]]$Extra
    )
    $wgArgs = @(
        'install', '--id', $Id, '--exact', '--scope', 'machine', '--silent',
        '--accept-source-agreements', '--accept-package-agreements'
    )
    if ($Extra)    { $wgArgs += $Extra }
    if ($Location) { $wgArgs += @('--location', $Location) }

    Write-Note ('winget ' + ($wgArgs -join ' '))
    & winget.exe @wgArgs
    return $LASTEXITCODE
}

# ---------------- 包清单 ----------------
# Sub = 子目录名；$null 表示不给该包传 --location
# Extra = 额外的 winget 参数
$packages = @(
    [pscustomobject]@{
        Label = 'PowerShell 7'
        Id    = 'Microsoft.PowerShell'
        Sub   = 'PowerShell'
        Extra = @('--installer-type', 'msi')   # 源里默认 msix，无法指定路径
    }
    [pscustomobject]@{
        Label = 'Git'
        Id    = 'Git.Git'
        Sub   = 'Git'
        Extra = @()
    }
    [pscustomobject]@{
        Label = 'Git LFS'
        Id    = 'GitHub.GitLFS'
        Sub   = $null                            # 跟随已装的 Git 目录
        Extra = @()
    }
    [pscustomobject]@{
        Label = 'gsudo'
        Id    = 'gerardog.gsudo'
        Sub   = 'gsudo'
        Extra = @()
    }
)

# ---------------- 主流程 ----------------
if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) {
    throw '未找到 winget（App Installer）。请先在 Microsoft Store 安装“应用安装程序”，或改用别的安装方式。'
}
if (-not $WhatIfPreference) { Assert-Elevated }

$base = Resolve-InstallBase -Explicit $BaseDir
Write-Step "安装根目录：$base（每个包落在 $base<App>）"

$failed = New-Object System.Collections.Generic.List[string]
foreach ($pkg in $packages) {
    Write-Step "$($pkg.Label)（$($pkg.Id)）"

    if (Test-PackageInstalled -Id $pkg.Id) {
        Write-Note '已安装，跳过；如需换盘请先卸载再重跑（本脚本不做迁移）。'
        continue
    }

    $target = $null
    if ($pkg.Sub -and -not $SkipLocation) { $target = Join-Path $base $pkg.Sub }
    $targetLabel = if ($target) { $target } else { '安装器默认位置' }

    if (-not $PSCmdlet.ShouldProcess("$($pkg.Label) → $targetLabel", 'winget install')) { continue }

    $code = Invoke-WingetInstall -Id $pkg.Id -Location $target -Extra $pkg.Extra
    if ($code -ne 0 -and $target) {
        Write-WarnMsg ("带 --location 安装失败（退出码 {0}），去掉路径参数重试一次..." -f $code)
        $code = Invoke-WingetInstall -Id $pkg.Id -Location $null -Extra $pkg.Extra
        if ($code -eq 0) { Write-WarnMsg "$($pkg.Label) 已装到安装器默认位置（大概率 C 盘）。" }
    }
    if ($code -ne 0) {
        Write-WarnMsg "$($pkg.Label) 安装失败（退出码 $code）。"
        $failed.Add($pkg.Label)
    }
}

# 刷新当前会话 PATH（machine + user），否则刚装完的命令找不到
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
            [Environment]::GetEnvironmentVariable('Path', 'User')

Write-Step '落盘结果（真实位置以此为准）'
$wantDrive = [System.IO.Path]::GetPathRoot($base).TrimEnd('\', ':')
$onTarget  = 0
$checked   = 0
foreach ($cmd in 'pwsh', 'git', 'git-lfs', 'gsudo') {
    $c = Get-Command $cmd -ErrorAction SilentlyContinue
    if (-not $c -or -not $c.Source) {
        Write-Note ("{0,-8} 当前会话未找到（新开终端再看）" -f $cmd)
        continue
    }
    $checked++
    $drive = [System.IO.Path]::GetPathRoot($c.Source).TrimEnd('\', ':')
    if ($drive -eq $wantDrive) { $onTarget++ }
    Write-Note ("{0,-8} {1}" -f $cmd, $c.Source)
}
Write-Note ("其中 {0}/{1} 个已找到的命令在 {2} 盘；不在的说明该安装器忽略了 --location（msi/wix 常见），或本来就是旧安装。" -f $onTarget, $checked, $wantDrive)
Write-Note '提示: 新开终端后 pwsh / git / git-lfs / gsudo 才稳定可用。'

if ($failed.Count -gt 0) {
    Write-WarnMsg ('失败项: ' + ($failed -join ', ') + '。可改用 devtools.winget 兜底: winget configure -f configurations\devtools.winget --accept-configuration-agreements --disable-interactivity')
    exit 1
}
Write-Note '全部完成。'
