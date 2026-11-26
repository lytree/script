# install_wsl_kali.ps1 - 主入口：菜单 + 调度
# 一键安装 WSL + Kali Linux + .NET SDK + Python uv + Node fnm + Windows 开发者设置
# （实现思路参考 microsoft/WindowsDeveloperConfig）
# 用法:
#   .\install-wsl-kali\install_wsl_kali.ps1                          # 交互式：菜单选择 + 交互选择 Kali 安装路径
#   .\install-wsl-kali\install_wsl_kali.ps1 -Install WslOnly         # 仅装 WSL，不询问
#   .\install-wsl-kali\install_wsl_kali.ps1 -Install DotNetOnly      # 仅装 .NET SDK
#   .\install-wsl-kali\install_wsl_kali.ps1 -Install ToolsOnly       # 仅装 Python uv + Node fnm
#   .\install-wsl-kali\install_wsl_kali.ps1 -Install SettingsOnly    # 仅应用 Windows 开发者设置
#   .\install-wsl-kali\install_wsl_kali.ps1 -Install All -KaliPath D:\WSL\kali-linux -NonInteractive
#   .\install-wsl-kali\install_wsl_kali.ps1 -Install All -WslPath D:\WSL -NonInteractive   # 自定义 WSL 磁盘根目录
#   .\install-wsl-kali\install_wsl_kali.ps1 -Install All -SkipWingetConfig   # 跳过 winget configure 路径
# 模块结构:
#   _common.ps1           公共辅助（输出/交互/提权/进程/winget/winget configure/PATH/重启恢复）
#   _wsl.ps1              WSL 平台安装（dism 启用功能 + wsl --install/--update，纯 PowerShell）
#   _kali.ps1             WSL 磁盘存储位置（自定义/迁移）+ Kali 安装
#   _dotnet.ps1           .NET SDK 安装
#   _tools.ps1            Python uv + Node fnm 安装
#   _windows-settings.ps1 Windows 开发者设置
#   configurations/*.winget  winget configure 声明式配置
#                            （toolchain 等用 schema 0.2；windows-default/windows-full 用 DSC v3 原生 Registry 资源）
# 说明:
#   - 脚本幂等，可重复运行，已完成的步骤会自动跳过
#   - WSL 不走 winget configure：脚本直接用 dism + wsl.exe 处理，失败再回退 winget install Microsoft.WSL
#   - 其余组件优先走 winget configure（.winget 配置，声明式/幂等），失败自动回退 winget install / 官方脚本
#   - WSL 磁盘位置可自定义（-WslPath 根目录 / -KaliPath 完整路径 / 交互选择），已安装发行版可迁移
#   - WSL 组件安装后如需重启，会注册一个登录恢复计划任务，重启后自动继续安装
#   - 需要管理员权限运行（未提权时会自动请求 UAC）
# 注意: 本脚本仅支持 PowerShell 7+（pwsh），不支持 Windows PowerShell 5.1

#requires -Version 7

[CmdletBinding()]
param(
    [ValidateSet('All', 'WslOnly', 'KaliOnly', 'DotNetOnly', 'ToolsOnly', 'SettingsOnly')]
    [string]$Install,
    [string]$KaliPath = '',      # Kali 虚拟磁盘完整目录（如 D:\WSL\kali-linux）
    [string]$WslPath  = '',      # WSL 磁盘根目录（如 D:\WSL → 发行版落在 D:\WSL\<发行版名>）
    [switch]$NonInteractive,
    [switch]$NoReboot,
    [switch]$SkipWingetConfig   # 跳过 winget configure（configurations/*.winget），直接用安装命令
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# 加载模块（dot-source 到本脚本作用域）
foreach ($module in @('_common.ps1', '_wsl.ps1', '_kali.ps1', '_dotnet.ps1', '_tools.ps1', '_windows-settings.ps1')) {
    $modulePath = Join-Path $PSScriptRoot $module
    if (-not (Test-Path -LiteralPath $modulePath)) {
        Write-Err2 "缺少模块文件: $modulePath"
        exit 1
    }
    . $modulePath
}

# winget configure 开关（configurations/*.winget 声明式安装路径）
$Script:UseWingetConfig = -not $SkipWingetConfig

function Show-MainMenu {
    Write-Host ''
    Write-Host '========= WSL + Kali + 开发环境 一键安装 ==========' -ForegroundColor Cyan
    Write-Host '  [1] 一键安装 WSL + Kali + .NET + uv/fnm + Windows 设置（推荐）'
    Write-Host '  [2] 仅安装 WSL 平台'
    Write-Host '  [3] 仅安装 Kali（要求 WSL 已就绪）'
    Write-Host '  [4] 仅安装 .NET SDK'
    Write-Host '  [5] 仅安装 Python uv + Node fnm'
    Write-Host '  [6] 仅应用 Windows 开发者设置'
    Write-Host '  [0] 退出'
    Write-Host '===================================================' -ForegroundColor Cyan
    $answer = Read-InputOrDefault '请选择' '1'
    switch ($answer) {
        '1' { return 'All' }
        '2' { return 'WslOnly' }
        '3' { return 'KaliOnly' }
        '4' { return 'DotNetOnly' }
        '5' { return 'ToolsOnly' }
        '6' { return 'SettingsOnly' }
        default { exit 0 }
    }
}

Assert-Admin
Remove-ResumeTask   # 本次运行若由恢复任务唤起，先清理旧任务

try {
    $action = $Install
    if (-not $action) { $action = Show-MainMenu }
    $Script:InstallAction = $action
    Write-Info "安装计划: $action"

    # 提前确定 WSL 磁盘位置：重启恢复任务会沿用同一路径，避免重启后落到默认目录
    if ($action -in @('All', 'KaliOnly')) {
        $Script:DistroTargetPath = Resolve-WslDistroPath
    }

    if ($action -in @('All', 'WslOnly')) {
        Install-WslPlatform   # 若需要重启，函数内会安排恢复任务并抛出
    }

    if ($action -in @('All', 'DotNetOnly')) {
        Install-DotNetSdk
    }

    if ($action -in @('All', 'ToolsOnly')) {
        Install-PythonNodeToolchain
    }

    if ($action -in @('All', 'SettingsOnly')) {
        Install-WindowsSettings
    }

    if ($action -in @('All', 'KaliOnly')) {
        Install-Kali -TargetPath $Script:DistroTargetPath
        Show-WslStorageInfo
    }

    Write-Info ''
    Write-Info '全部完成 ✔   验证: wsl -l -v / dotnet --list-sdks / uv --version / fnm --version'
}
catch {
    Write-Err2 "安装未完成: $($_.Exception.Message)"
    # 已安排重启（RestartSignalled）时保留恢复任务，重启后自动继续；其他失败直接清理
    if (-not $Script:RestartSignalled) { Remove-ResumeTask }
    exit 1
}
