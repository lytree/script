# _windows-settings.ps1 - Windows 开发者设置（参考 microsoft/WindowsDeveloperConfig registry-*.ps1）
# 依赖: _common.ps1

function Convert-RegPath {
    # 'HKLM\SYSTEM\...' / 'HKCU\...' → PowerShell 注册表路径
    param([Parameter(Mandatory)][string]$KeyPath)
    if ($KeyPath -match '^(HKLM|HKCU)\\(.+)$') { return "$($Matches[1]):\$($Matches[2])" }
    return $KeyPath
}

function Test-RegistryTweak {
    param([Parameter(Mandatory)][hashtable]$Tweak)
    $psPath = Convert-RegPath $Tweak.KeyPath
    if (-not (Test-Path -LiteralPath $psPath)) { return $false }
    try { return ((Get-ItemPropertyValue -LiteralPath $psPath -Name $Tweak.ValueName) -eq $Tweak.Value) }
    catch { return $false }
}

function Invoke-RegistryTweak {
    param([Parameter(Mandatory)][hashtable]$Tweak)
    $psPath = Convert-RegPath $Tweak.KeyPath
    if (-not (Test-Path -LiteralPath $psPath)) { New-Item -Path $psPath -Force | Out-Null }
    New-ItemProperty -LiteralPath $psPath -Name $Tweak.ValueName -Value $Tweak.Value -PropertyType DWord -Force | Out-Null
}

# Core = 默认档（用户指定的默认开启项）；其余为 Full 档（全部）
$Script:WinSettingsTweaks = @(
    @{ Name = 'LongPaths'; Core = $true
       KeyPath = 'HKLM\SYSTEM\CurrentControlSet\Control\FileSystem'; ValueName = 'LongPathsEnabled'; Value = 1
       Description = '启用 Win32 长路径支持' }
    @{ Name = 'DeveloperMode'; Core = $true
       KeyPath = 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'; ValueName = 'AllowDevelopmentWithoutDevLicense'; Value = 1
       Description = '启用开发者模式（侧载 + 开发功能）' }
    @{ Name = 'Sudo'; Core = $true; BestEffort = $true   # Win10 无 Sudo，写入无害
       KeyPath = 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Sudo'; ValueName = 'Enabled'; Value = 3
       Description = '启用 Sudo（inline 模式）' }
    @{ Name = 'ShowFileExtensions'; Core = $true
       KeyPath = 'HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced'; ValueName = 'HideFileExt'; Value = 0
       Description = '显示文件扩展名' }
    @{ Name = 'ShowHiddenFiles'; Core = $true
       KeyPath = 'HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced'; ValueName = 'Hidden'; Value = 1
       Description = '显示隐藏文件' }
    @{ Name = 'TaskbarEndTask'; Core = $true
       KeyPath = 'HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings'; ValueName = 'TaskbarEndTask'; Value = 1
       Description = '任务栏图标右键显示"结束任务"' }
    @{ Name = 'TipsOff'; Core = $true
       KeyPath = 'HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced'; ValueName = 'ShowSyncProviderNotifications'; Value = 0
       Description = '关闭资源管理器同步提供程序提示' }
    @{ Name = 'WebSearchOff'; Core = $true
       KeyPath = 'HKCU\SOFTWARE\Policies\Microsoft\Windows\Explorer'; ValueName = 'DisableSearchBoxSuggestions'; Value = 1
       Description = '关闭开始/搜索的联网搜索建议' }
    @{ Name = 'SearchHighlightOff'; Core = $true; BestEffort = $true
       KeyPath = 'HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\SearchSettings'; ValueName = 'IsDynamicSearchBoxEnabled'; Value = 0
       Description = '关闭"显示搜索亮点"' }
    @{ Name = 'StartRecommendations'; Core = $true
       KeyPath = 'HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced'; ValueName = 'Start_IrisRecommendations'; Value = 0
       Description = '关闭开始菜单推荐内容' }
    @{ Name = 'StartAccountNotifications'; Core = $true
       KeyPath = 'HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced'; ValueName = 'Start_AccountNotifications'; Value = 0
       Description = '关闭开始菜单账户通知' }
    @{ Name = 'WidgetServiceOff'; Core = $true; BestEffort = $true   # Windows 可能对 Widgets 策略键加保护
       KeyPath = 'HKLM\SOFTWARE\Policies\Microsoft\Dsh'; ValueName = 'AllowNewsAndInterests'; Value = 0
       Description = '关闭小组件（Widgets）' }
    @{ Name = 'RemoteDesktop'; Core = $true
       KeyPath = 'HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server'; ValueName = 'fDenyTSConnections'; Value = 0
       Description = '启用远程桌面' }
    @{ Name = 'OpenThisPC'
       KeyPath = 'HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced'; ValueName = 'LaunchTo'; Value = 1
       Description = '资源管理器默认打开"此电脑"' }
    @{ Name = 'FrequentFolders'
       KeyPath = 'HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer'; ValueName = 'ShowFrequent'; Value = 0
       Description = '快速访问不显示常用文件夹' }
    @{ Name = 'FrequentFiles'
       KeyPath = 'HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer'; ValueName = 'ShowRecent'; Value = 0
       Description = '快速访问不显示最近使用的文件' }
    @{ Name = 'DoNotDisturb'
       KeyPath = 'HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings'; ValueName = 'NOC_GLOBAL_SETTING_TOASTS_ENABLED'; Value = 0
       Description = '开启勿扰（禁用所有通知横幅）' }
)

function Install-WindowsSettings {
    Write-Info '===== Windows 开发者设置 ====='
    $applyFull = $NonInteractive ? $false : $true
    if (-not $NonInteractive) {
        Write-Host '  [1] 默认：长路径 / 开发者模式 / Sudo / 扩展名 / 隐藏文件 / 结束任务 / 远程桌面'
        Write-Host '        / 关小组件 / 关同步提示 / 关联网搜索建议与亮点 / 关开始菜单推荐与账户通知'
        Write-Host '  [2] 全部：以上 + 资源管理器打开"此电脑" / 快速访问精简 / 勿扰'
        $applyFull = (Read-InputOrDefault '请选择设置范围' '1') -eq '2'
    }

    # 注意: 用索引器 $_['Core'] 而非 $_.Core —— 部分项没有 Core 键，StrictMode 下属性访问会报错
    $tweaks = $applyFull ? $Script:WinSettingsTweaks : @($Script:WinSettingsTweaks | Where-Object { $_['Core'] })

    $failed = 0
    foreach ($tweak in $tweaks) {
        if (Test-RegistryTweak -Tweak $tweak) {
            Write-Dim "已是目标值，跳过: $($tweak.Description)"
            continue
        }
        try {
            Invoke-RegistryTweak -Tweak $tweak
            Write-Info "已设置: $($tweak.Description)"
        }
        catch {
            $failed++
            if ($tweak['BestEffort']) {
                Write-Warn2 "跳过（非关键项）: $($tweak.Description) — $($_.Exception.Message)"
            }
            else {
                Write-Warn2 "设置失败: $($tweak.Description) — $($_.Exception.Message)"
            }
        }
    }

    # 远程桌面防火墙规则（按规则名匹配，避免本地化组名问题；规则已启用时重复启用无害）
    try {
        Get-NetFirewallRule -Name 'RemoteDesktop-*' -ErrorAction Stop | Enable-NetFirewallRule
        Write-Info '远程桌面防火墙规则已启用。'
    }
    catch { Write-Warn2 "远程桌面防火墙规则启用失败: $($_.Exception.Message)" }

    # HKCU Explorer 相关项需重启资源管理器或注销生效
    if (-not $NonInteractive -and (Ask-Choice '是否立即重启资源管理器使设置生效？（屏幕会闪烁一下）' $false)) {
        Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
        Write-Dim '资源管理器已重启。'
    }
    if ($failed -gt 0) { Write-Warn2 "共 $failed 项设置失败，可稍后重新运行本脚本重试。" }
}
