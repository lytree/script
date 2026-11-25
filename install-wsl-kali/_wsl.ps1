# _wsl.ps1 - WSL 平台检测与安装（参考 microsoft/WindowsDeveloperConfig steps/wsl.ps1）
# 依赖: _common.ps1

function Test-WslRuntimeCurrent {
    # 新版 WSL（Store 包）支持 --version；旧版内置 WSL 返回非零
    return ((Get-WslExitCode -Arguments @('--version') -TimeoutSeconds 60 -CloseStdin) -eq 0)
}

function Test-WslComponentsReady {
    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='vmcompute'" -ErrorAction SilentlyContinue
    return ($null -ne $svc) -and (Test-WslRuntimeCurrent)
}

function Enable-WslFeatures {
    # dism 提供稳定的退出码；3010 = 已启用但需重启
    foreach ($feature in @('VirtualMachinePlatform', 'Microsoft-Windows-Subsystem-Linux')) {
        Write-Info "启用 Windows 功能: $feature ..."
        $proc = Start-Process -FilePath 'dism.exe' `
            -ArgumentList '/online', '/enable-feature', "/featurename:$feature", '/all', '/norestart', '/quiet' `
            -NoNewWindow -Wait -PassThru
        if ($proc.ExitCode -eq 3010) { $Script:RestartSignalled = $true }
        elseif ($proc.ExitCode -ne 0) { throw "启用 $feature 失败（dism 退出码 $($proc.ExitCode)）。" }
    }
}

function Update-WslRuntime {
    Write-Info '当前是 Windows 内置的旧版 WSL，正在升级到新版（wsl --update）...'
    foreach ($updateArgs in @(@('--update'), @('--update', '--web-download'))) {
        $code = Invoke-WslDirect -Arguments $updateArgs
        if ($code -eq 0 -and (Test-WslRuntimeCurrent)) { return $true }
        Write-Dim "wsl $($updateArgs -join ' ') 返回 $code，尝试下一条路径..."
    }
    Write-Warn2 'WSL 升级失败。请检查网络或手动执行 wsl --update 后重新运行本脚本。'
    return $false
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

    # 路径 0: winget configure（声明式启用虚拟机平台 + 安装 WSL 包）
    if ((Invoke-WingetConfiguration 'wsl') -eq 0) {
        if (-not (Test-WslRuntimeCurrent)) { [void](Update-WslRuntime) }
        # 复用既有判定：功能已启用但需重启激活 → Wait-ForReboot
        if (-not $Script:RestartSignalled -and -not (Test-ServicingRebootPending) -and (Test-WslComponentsReady)) {
            Write-Info 'WSL 平台安装完成（winget configure）。'
            return
        }
        Write-Dim 'winget configure 已执行，组件需要重启激活。'
        Wait-ForReboot
        if (-not $NoReboot) {
            throw '已安排重启。重启并登录后恢复任务会自动继续；也可手动重新运行本脚本（幂等）。'
        }
        return
    }

    Test-VirtualizationHint
    $Script:RestartSignalled = $false

    try {
        Write-Info '安装 WSL 平台组件（wsl --install --no-distribution）...'
        Write-Dim '（期间可能弹出 WSL 安装窗口，属正常现象，可能需要几分钟。）'
        # 参考 WindowsDeveloperConfig: wsl 的安装引导需要真实控制台，不重定向
        $code = Invoke-WslDirect -Arguments @('--install', '--no-distribution')
        if ($code -in @(3010, 1641)) { $Script:RestartSignalled = $true }
        elseif ($code -ne 0) { throw "wsl --install --no-distribution 失败（退出码 $code）。" }
    }
    catch {
        Write-Warn2 "wsl 自身安装器无法运行（$($_.Exception.Message)）。"
        Write-Warn2 '改用 dism 直接启用 Windows 功能。'
        Enable-WslFeatures
    }

    if (-not (Test-WslRuntimeCurrent)) { [void](Update-WslRuntime) }

    # 无需重启且平台已激活 → 完成
    if (-not $Script:RestartSignalled -and -not (Test-ServicingRebootPending) -and (Test-WslComponentsReady)) {
        Write-Info 'WSL 平台安装完成。'
        return
    }
    Wait-ForReboot
    if (-not $NoReboot) {
        throw '已安排重启。重启并登录后恢复任务会自动继续；也可手动重新运行本脚本（幂等）。'
    }
}
