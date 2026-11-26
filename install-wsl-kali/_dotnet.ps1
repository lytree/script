# _dotnet.ps1 - .NET SDK 安装（winget 优先，回退官方 dotnet-install.ps1）
# 依赖: _common.ps1

function Get-DotNetSdkVersions {
    # 返回已安装 SDK 的版本号列表；dotnet 不存在时返回空
    # dotnet --list-sdks 输出格式: 10.0.100 [C:\Program Files\dotnet\sdk\10.0.100]
    if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) { return @() }
    try {
        $out = & dotnet.exe --list-sdks 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $out) { return @() }
        return @($out | ForEach-Object { ($_ -split '\s+')[0].Trim() } |
            Where-Object { $_ -match '^\d+\.\d+' })
    }
    catch { return @() }
}

function Test-DotNetSdkInstalled {
    # 仓库底座是 .NET 10，只需检测到 10.x SDK 即视为已装
    return (@(Get-DotNetSdkVersions | Where-Object { $_ -match "^$($Script:DotNetChannel.Split('.')[0])\." }).Count -gt 0)
}

function Install-DotNetSdk {
    if (Test-DotNetSdkInstalled) {
        $v = (Get-DotNetSdkVersions | Where-Object { $_ -match '^\d+\.' } | Select-Object -First 1)
        Write-Info ".NET SDK 已安装（$v），跳过。"
        return
    }

    # 路径 0: winget configure（configurations/toolchain.winget，已合并 dotnet/ffmpeg/fnm/uv）
    if ((Invoke-WingetConfiguration 'toolchain') -eq 0) {
        Add-PathDirectory -Dir (Join-Path $env:ProgramFiles 'dotnet')
        if (Test-DotNetSdkInstalled) {
            Write-Info '.NET SDK 安装完成（winget configure）。'
            return
        }
        Write-Dim 'configure 执行成功但未检测到 10.x SDK，尝试其他方案...'
    }

    # 路径 1: winget（参考 WindowsDeveloperConfig 的包管理方式）
    $code = Install-WingetPackage -Id $Script:DotNetSdkId
    if ($code -eq 0) {
        Add-PathDirectory -Dir (Join-Path $env:ProgramFiles 'dotnet')
        if (Test-DotNetSdkInstalled) {
            Write-Info '.NET SDK 安装完成。'
            return
        }
        Write-Dim 'winget 执行成功但未检测到 10.x SDK，尝试回退方案...'
    }
    elseif ($code -gt 0) {
        Write-Dim "winget 返回 $code，尝试回退方案..."
    }
    else {
        Write-Warn2 '未找到 winget，改用官方 dotnet-install 脚本安装。'
    }

    # 路径 2: 官方 dotnet-install.ps1（不依赖 Store/winget）
    Write-Info "通过 dotnet-install.ps1 安装 .NET SDK（Channel: $($Script:DotNetChannel)）..."
    $installDir = Join-Path $env:ProgramFiles 'dotnet'
    $tmpScript  = [System.IO.Path]::GetTempFileName() + '.ps1'
    try {
        Invoke-RestMethod -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $tmpScript
        & $tmpScript -Channel $Script:DotNetChannel -InstallDir $installDir
        if ($LASTEXITCODE -ne 0) { throw "dotnet-install.ps1 失败（退出码 $LASTEXITCODE）。" }
        Add-PathDirectory -Dir $installDir
        if (Test-DotNetSdkInstalled) {
            Write-Info '.NET SDK 安装完成。'
        }
        else {
            throw '.NET SDK 安装后未检测到，请检查输出或重新运行本脚本。'
        }
    }
    finally {
        Remove-Item -LiteralPath $tmpScript -Force -ErrorAction SilentlyContinue
    }
}
