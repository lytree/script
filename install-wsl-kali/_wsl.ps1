# _wsl.ps1 - WSL 平台检测与安装（纯 PowerShell：dism 启用功能 + wsl.exe 安装/升级，不使用 winget configure）
# 依赖: _common.ps1

function Test-WslRuntimeCurrent {
    # 新版 WSL（Store 包）支持 --version；旧版内置 WSL 返回非零
    return ((Get-WslExitCode -Arguments @('--version') -TimeoutSeconds 60 -CloseStdin) -eq 0)
}

function Test-WslComponentsReady {
    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='vmcompute'" -ErrorAction SilentlyContinue
    return ($null -ne $svc) -and (Test-WslRuntimeCurrent)
}

function Get-WslFeatureState {
    # 返回 Windows 可选功能状态：Enabled / EnablePending / Disabled / Unknown
    param([Parameter(Mandatory)][string]$FeatureName)
    try {
        $feature = Get-WindowsOptionalFeature -Online -FeatureName $FeatureName -ErrorAction Stop
        return [string]$feature.State
    }
    catch {
        Write-Dim "查询功能状态失败（$FeatureName）: $($_.Exception.Message)"
        return 'Unknown'
    }
}

function Enable-WslFeatures {
    # dism 提供稳定的退出码；3010 = 已启用但需重启
    foreach ($feature in @('VirtualMachinePlatform', 'Microsoft-Windows-Subsystem-Linux')) {
        $state = Get-WslFeatureState -FeatureName $feature
        if ($state -eq 'Enabled') {
            Write-Dim "Windows 功能已启用: $feature"
            continue
        }
        if ($state -eq 'EnablePending') {
            Write-Dim "Windows 功能待重启生效: $feature"
            $Script:RestartSignalled = $true
            continue
        }
        Write-Info "启用 Windows 功能: $feature ..."
        $proc = Start-Process -FilePath 'dism.exe' `
            -ArgumentList '/online', '/enable-feature', "/featurename:$feature", '/all', '/norestart', '/quiet' `
            -NoNewWindow -Wait -PassThru
        if ($proc.ExitCode -eq 3010) { $Script:RestartSignalled = $true }
        elseif ($proc.ExitCode -ne 0) { throw "启用 $feature 失败（dism 退出码 $($proc.ExitCode)）。" }
        else { Write-Dim "已启用: $feature" }
    }
}

function Update-WslRuntime {
    # 已有旧版运行时 → 升级到 Store 版（wsl --update 优先，网络受限再走 --web-download）
    Write-Info '升级 WSL 运行时（wsl --update）...'
    foreach ($updateArgs in @(@('--update'), @('--update', '--web-download'))) {
        $code = Invoke-WslDirect -Arguments $updateArgs
        if ($code -in @(3010, 1641)) { $Script:RestartSignalled = $true }
        if ($code -eq 0 -and (Test-WslRuntimeCurrent)) { return $true }
        Write-Dim "wsl $($updateArgs -join ' ') 返回 $code，尝试下一条路径..."
    }
    # 最后回退：winget 直接装 Store 版包（命令行调用，不依赖 .winget 配置文件）
    if (Test-ToolOnPath 'winget.exe') {
        Write-Dim '改用 winget 安装 Microsoft.WSL 作为最后回退...'
        if ((Install-WingetPackage -Id 'Microsoft.WSL') -eq 0 -and (Test-WslRuntimeCurrent)) { return $true }
    }
    return $false
}

function Install-WslRuntime {
    # 安装 WSL 运行时：wsl --install --no-distribution（不装发行版，发行版由 _kali.ps1 单独装到自定义磁盘位置）
    Write-Info '安装 WSL 运行时（wsl --install --no-distribution）...'
    Write-Dim '（期间可能弹出 WSL 安装窗口，属正常现象，可能需要几分钟。）'
    $code = Invoke-WslDirect -Arguments @('--install', '--no-distribution')
    if ($code -in @(3010, 1641)) { $Script:RestartSignalled = $true }
    elseif ($code -ne 0) { Write-Dim "wsl --install --no-distribution 返回 $code，改为升级运行时..." }
    if (Test-WslRuntimeCurrent) { return $true }
    return (Update-WslRuntime)
}

function Set-WslDefaultVersion2 {
    # 默认发行版版本设为 WSL2（幂等；没有发行版时也可执行）
    $code = Get-WslExitCode -Arguments @('--set-default-version', '2') -TimeoutSeconds 120 -CloseStdin
    if ($code -eq 0) { Write-Dim 'WSL 默认版本已设为 2。' }
    else { Write-Dim "设置 WSL 默认版本返回 $code（可忽略）。" }
}

function Test-VirtualizationHint {
    $cs = Get-CimInstance -ClassName Win32_ComputerSystem
    if (-not $cs.HypervisorPresent) {
        Write-Warn2 '提示: 未检测到 Hypervisor。若安装后 WSL 仍不可用，请到 BIOS/UEFI 打开硬件虚拟化（VT-x / AMD-V）；虚拟机内请为该 VM 开启嵌套虚拟化。'
    }
}

function Install-WslPlatform {
    if (Test-WslComponentsReady) {
        Write-Info 'WSL 平台已就绪，跳过。'
        return
    }

    Test-VirtualizationHint
    $Script:RestartSignalled = $false

    # 1) dism 启用 Windows 功能（脚本直接处理，不再经过 winget configure / wsl.winget）
    Enable-WslFeatures

    # 2) 安装/升级 WSL 运行时；功能刚启用时先重启，重启后由恢复任务继续
    if ($Script:RestartSignalled) {
        Write-Dim 'Windows 功能刚启用，运行时安装将在重启后继续。'
    }
    elseif (Install-WslRuntime) {
        Set-WslDefaultVersion2
    }
    else {
        # 运行时没装上：需要重启激活组件 → 交给恢复任务续装；否则按失败处理，不留下重启循环
        if ($Script:RestartSignalled -or (Test-ServicingRebootPending)) {
            Write-Dim '组件需要重启激活，重启后由恢复任务继续安装运行时。'
            $Script:RestartSignalled = $true
        }
        else {
            throw 'WSL 运行时安装失败。请检查网络 / 代理后重新运行本脚本，或手动执行 wsl --update。'
        }
    }

    # 3) 无需重启且平台已激活 → 完成
    if (-not $Script:RestartSignalled -and -not (Test-ServicingRebootPending) -and (Test-WslComponentsReady)) {
        Write-Info 'WSL 平台安装完成。'
        return
    }
    Wait-ForReboot
    if (-not $NoReboot) {
        throw '已安排重启。重启并登录后恢复任务会自动继续；也可手动重新运行本脚本（幂等）。'
    }
}
