# _tools.ps1 - Python uv / Node fnm 工具链安装（winget 优先，各自带回退）
# 依赖: _common.ps1

function Install-Uv {
    if (Test-ToolOnPath 'uv.exe') {
        Write-Info "uv 已安装（$(& uv.exe --version 2>$null)），跳过。"
        return
    }

    # 路径 0: winget configure（configurations/uv.winget）
    if ((Invoke-WingetConfiguration 'uv') -eq 0 -and (Test-ToolOnPath 'uv.exe')) {
        Write-Info 'uv 安装完成（winget configure）。'
        Write-Dim '提示: 可用 uv python install <版本> 管理 Python 解释器，例如 uv python install 3.14'
        return
    }

    # 路径 1: winget install
    $code = Install-WingetPackage -Id $Script:UvPkgId
    if ($code -eq 0 -and (Test-ToolOnPath 'uv.exe')) {
        Write-Info 'uv 安装完成。'
        return
    }
    if ($code -gt 0) { Write-Dim "winget 返回 $code，尝试回退方案..." }

    # 路径 2: astral.sh 官方安装脚本（装到 %USERPROFILE%\.local\bin，脚本自带 PATH 持久化）
    Write-Info '通过 astral.sh 官方脚本安装 uv...'
    $tmpScript = [System.IO.Path]::GetTempFileName() + '.ps1'
    try {
        Invoke-RestMethod -Uri 'https://astral.sh/uv/install.ps1' -OutFile $tmpScript
        & $tmpScript
        if ($LASTEXITCODE -ne 0) { throw "uv 官方安装脚本失败（退出码 $LASTEXITCODE）。" }
        Add-PathDirectory -Dir (Join-Path $env:USERPROFILE '.local\bin')
        if (-not (Test-ToolOnPath 'uv.exe')) { throw 'uv 安装后未在 PATH 中找到，请重开终端后再运行本脚本验证。' }
        Write-Info 'uv 安装完成。'
        Write-Dim '提示: 可用 uv python install <版本> 管理 Python 解释器，例如 uv python install 3.14'
    }
    finally {
        Remove-Item -LiteralPath $tmpScript -Force -ErrorAction SilentlyContinue
    }
}

function Install-Fnm {
    if (Test-ToolOnPath 'fnm.exe') {
        Write-Info "fnm 已安装（$(& fnm.exe --version 2>$null)），跳过。"
        return
    }

    # 路径 0: winget configure（configurations/fnm.winget）
    if ((Invoke-WingetConfiguration 'fnm') -eq 0 -and (Test-ToolOnPath 'fnm.exe')) {
        Write-Info 'fnm 安装完成（winget configure）。'
        Complete-FnmSetup
        return
    }

    # 路径 1: winget install
    $code = Install-WingetPackage -Id $Script:FnmPkgId
    if ($code -eq 0 -and (Test-ToolOnPath 'fnm.exe')) {
        Complete-FnmSetup
        return
    }
    if ($code -gt 0) { Write-Dim "winget 返回 $code，尝试回退方案..." }

    # 路径 2: GitHub Release 下载（fnm-windows.zip，解压到 %LOCALAPPDATA%\fnm）
    Write-Info '从 GitHub Release 下载 fnm...'
    $fnmDir = Join-Path $env:LOCALAPPDATA 'fnm'
    $zipFile = [System.IO.Path]::GetTempFileName() + '.zip'
    try {
        Invoke-RestMethod -Uri 'https://github.com/Schniz/fnm/releases/latest/download/fnm-windows.zip' -OutFile $zipFile
        New-Item -ItemType Directory -Path $fnmDir -Force | Out-Null
        Expand-Archive -LiteralPath $zipFile -DestinationPath $fnmDir -Force
        Add-PathDirectory -Dir $fnmDir
        if (-not (Test-ToolOnPath 'fnm.exe')) { throw 'fnm 解压后未在 PATH 中找到，请重开终端后再运行本脚本验证。' }
        Write-Info 'fnm 安装完成。'
        Complete-FnmSetup
    }
    finally {
        Remove-Item -LiteralPath $zipFile -Force -ErrorAction SilentlyContinue
    }
}

function Complete-FnmSetup {
    Write-Dim '提示: 可用 fnm install --lts 安装 Node LTS 版本。'
    # fnm 需要 shell 挂钩才能自动切换 Node 版本，可选写入 PowerShell Profile
    $profilePath = $PROFILE.CurrentUserCurrentHost
    $hookLine    = 'fnm env --use-on-cd --shell power-shell | Out-String | Invoke-Expression'
    $profileDir  = Split-Path -Parent $profilePath
    if ((Test-Path -LiteralPath $profilePath) -and ((Get-Content -LiteralPath $profilePath -Raw) -match 'fnm env')) {
        Write-Dim 'PowerShell Profile 已包含 fnm 初始化，跳过。'
        return
    }
    if (Ask-Choice "是否将 fnm 初始化写入 Profile（$profilePath）？" $true) {
        if (-not (Test-Path -LiteralPath $profileDir)) { New-Item -ItemType Directory -Path $profileDir -Force | Out-Null }
        Add-Content -LiteralPath $profilePath -Value "`n# fnm (Node 版本管理) - 由 install_wsl_kali.ps1 添加`n$hookLine" -Encoding UTF8
        Write-Info '已写入 Profile，新开终端后 fnm 自动生效。'
    }
}

function Install-PythonNodeToolchain {
    # uv / fnm 各自独立安装：configure（uv.winget / fnm.winget）→ winget install → 官方脚本回退
    # 某一个失败不影响另一个继续安装
    Install-Uv
    Install-Fnm
}
