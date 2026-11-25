# _kali.ps1 - WSL 发行版检测 + Kali 安装（含交互式安装路径选择）
# 依赖: _common.ps1

# ---------------- 发行版检测 ----------------
function Get-WslDistroNames {
    $names = @()
    if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) { return $names }
    $env:WSL_UTF8 = '1'   # 让 wsl 输出 UTF-8 而非 UTF-16
    $stdoutFile = [System.IO.Path]::GetTempFileName()
    $stderrFile = [System.IO.Path]::GetTempFileName()
    $stdinFile  = [System.IO.Path]::GetTempFileName()
    try {
        $proc = Start-Process -FilePath 'wsl.exe' -ArgumentList '--list --quiet' -NoNewWindow -PassThru `
            -RedirectStandardOutput $stdoutFile -RedirectStandardError $stderrFile -RedirectStandardInput $stdinFile
        if (-not $proc.WaitForExit(120000)) { try { $proc.Kill() } catch {}; return $names }
        if ($proc.ExitCode -ne 0) { return $names }
        $names = @(Get-Content -LiteralPath $stdoutFile -Encoding UTF8 |
            ForEach-Object { ($_ -replace "`0", '').Trim() } | Where-Object { $_ })
    }
    catch { Write-Dim "无法列出 WSL 发行版: $($_.Exception.Message)" }
    finally { Remove-Item -LiteralPath $stdoutFile, $stderrFile, $stdinFile -Force -ErrorAction SilentlyContinue }
    return $names
}

function Test-DistroInstalled {
    param([string]$DistroName)
    return (@(Get-WslDistroNames | Where-Object { $_ -ieq $DistroName }).Count -gt 0)
}

function Wait-DistroVisible {
    param([string]$DistroName)
    for ($i = 1; $i -le 10; $i++) {
        if (Test-DistroInstalled -DistroName $DistroName) { return $true }
        if ($i -eq 1) { Write-Dim '等待 WSL 注册新发行版...' }
        Start-Sleep -Seconds 3
    }
    return $false
}

# ---------------- Kali 安装路径交互 ----------------
function Get-FreeSpaceGB {
    param([string]$Path)
    $drive = Get-PSDrive -Name $Path.Substring(0, 1).ToUpper() -ErrorAction SilentlyContinue
    if (-not $drive) { return $null }
    return [math]::Round($drive.Free / 1GB, 1)
}

function Confirm-KaliPath {
    # 校验并准备安装目录，返回规范化的完整路径
    param([string]$Chosen)
    try { $Chosen = [System.IO.Path]::GetFullPath($Chosen) } catch { throw "安装路径无效: $Chosen" }
    if ($Chosen -notmatch '^[A-Za-z]:\\') { throw "安装路径必须是本地盘符的绝对路径: $Chosen" }

    $freeGB = Get-FreeSpaceGB -Path $Chosen
    if ($null -eq $freeGB) { throw "盘符 $($Chosen.Substring(0, 2)) 不存在: $Chosen" }
    if ($freeGB -lt $Script:MinFreeGB) {
        Write-Warn2 "警告: 该盘可用空间仅 ${freeGB} GB，Kali 建议至少 ${Script:MinFreeGB} GB。"
        if (-not (Ask-Choice '仍要继续吗？' $false)) { throw '用户取消安装。' }
    }

    if (Test-Path -LiteralPath $Chosen) {
        $children = @(Get-ChildItem -LiteralPath $Chosen -Force -ErrorAction SilentlyContinue)
        if ($children.Count -gt 0) {
            Write-Warn2 "目录非空: $Chosen （建议使用空目录存放 Kali 虚拟磁盘。）"
            if (-not (Ask-Choice '仍要使用该目录吗？' $false)) { throw '用户取消安装。' }
        }
    }
    else {
        New-Item -ItemType Directory -Path $Chosen -Force | Out-Null
        Write-Dim "已创建目录: $Chosen"
    }
    return $Chosen
}

function Resolve-KaliInstallPath {
    # 交互式选择安装路径；非交互时用 -KaliPath 或默认值
    $candidates = @()
    # 优先推荐非系统盘
    $dataDrives = @(Get-PSDrive -PSProvider FileSystem |
        Where-Object { $_.Name -notin @('C', 'Temp') -and $_.Free -gt ($Script:MinFreeGB * 1GB) } |
        Sort-Object Free -Descending)
    if ($dataDrives.Count -gt 0) {
        $candidates += (Join-Path "$($dataDrives[0].Name):\" 'WSL\kali-linux')
    }
    $localDefault = Join-Path $env:LOCALAPPDATA 'WSL\kali-linux'
    $candidates += $localDefault

    if ($KaliPath) { return (Confirm-KaliPath -Chosen $KaliPath) }
    if ($NonInteractive) { return (Confirm-KaliPath -Chosen $candidates[0]) }

    Write-Info ''
    Write-Info '===== 选择 Kali 安装位置（存放 Kali 虚拟磁盘的目录）====='
    for ($i = 0; $i -lt $candidates.Count; $i++) {
        $free = Get-FreeSpaceGB -Path $candidates[$i]
        $freeText = $null -ne $free ? "可用空间 ${free} GB" : '盘符不存在'
        Write-Host ("  [{0}] {1}   ({2})" -f ($i + 1), $candidates[$i], $freeText)
    }
    Write-Host '  [0] 自定义路径'

    $chosen = $null
    while ($null -eq $chosen) {
        $answer = Read-InputOrDefault "请选择（0-$($candidates.Count)）" '1'
        if ($answer -eq '0') {
            $chosen = Read-InputOrDefault '请输入完整路径（如 D:\WSL\kali-linux）' $localDefault
        }
        elseif ($answer -match '^[A-Za-z]:') {
            $chosen = $answer   # 直接输入了路径
        }
        else {
            $idx = 0
            if ([int]::TryParse($answer, [ref]$idx) -and $idx -ge 1 -and $idx -le $candidates.Count) {
                $chosen = $candidates[$idx - 1]
            }
            else {
                Write-Dim '输入无效，请重试。'
            }
        }
    }
    return (Confirm-KaliPath -Chosen $chosen)
}

# ---------------- Kali 安装 ----------------
function Install-Kali {
    param([string]$TargetPath)

    if (-not (Test-WslComponentsReady)) {
        throw "WSL 平台未就绪，无法安装 Kali。请先安装 WSL（install_wsl_kali.ps1 -Install WslOnly），重启后再运行一次。"
    }

    if (Test-DistroInstalled -DistroName $Script:KaliDistroName) {
        Write-Info "Kali ($Script:KaliDistroName) 已安装，跳过。"
        if ($TargetPath) {
            Write-Dim "如需迁移到 $TargetPath，可执行: wsl --manage $Script:KaliDistroName --move `"$TargetPath`""
        }
        return
    }

    if (-not $TargetPath) { $TargetPath = Resolve-KaliInstallPath }
    Write-Info "Kali 安装位置: $TargetPath"
    Write-Dim '（安装窗口可能弹出，属正常现象；下载约需几分钟。）'

    # 路径 1: 新版 WSL (>= 2.4.4) 支持 --location 直接装到指定目录
    $code = Invoke-WslDirect -Arguments @('--install', '-d', $Script:KaliDistroName, '--no-launch', '--location', $TargetPath)
    if ($code -eq 0 -and (Wait-DistroVisible -DistroName $Script:KaliDistroName)) {
        Write-Info 'Kali 安装完成。'
        return
    }
    Write-Dim "--location 安装路径返回 $code，尝试回退方案..."

    # 路径 2: 普通安装 + wsl --manage --move 迁移
    $code = Invoke-WslDirect -Arguments @('--install', '-d', $Script:KaliDistroName, '--no-launch')
    if ($code -ne 0) {
        # 路径 3: Store 不可用时改走网络下载
        Write-Warn2 'Store 安装失败，改用 --web-download 从网络下载...'
        $code = Invoke-WslDirect -Arguments @('--install', '-d', $Script:KaliDistroName, '--no-launch', '--web-download')
    }
    if ($code -ne 0) {
        throw "wsl --install -d $Script:KaliDistroName 失败（退出码 $code）。请检查网络/虚拟化后重新运行本脚本。"
    }
    if (-not (Wait-DistroVisible -DistroName $Script:KaliDistroName)) {
        throw "$Script:KaliDistroName 已执行安装但未出现在 wsl --list 中，请重新运行本脚本验证。"
    }

    Write-Info "迁移 Kali 到指定路径: $TargetPath ..."
    $moveCode = Invoke-WslDirect -Arguments @('--manage', $Script:KaliDistroName, '--move', $TargetPath)
    if ($moveCode -ne 0) {
        Write-Warn2 "迁移失败（退出码 $moveCode）。Kali 暂留在默认位置，稍后可手动执行:"
        Write-Warn2 "  wsl --manage $Script:KaliDistroName --move `"$TargetPath`""
    }
    else {
        Write-Info 'Kali 已迁移到指定路径。'
    }
    Write-Info 'Kali 安装完成。首次进入: wsl -d kali-linux'
}
