# _kali.ps1 - WSL 磁盘存储位置（自定义）+ Kali 安装
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

# ---------------- WSL 磁盘存储位置 ----------------
function Get-WslDistroBasePath {
    # 已注册发行版的实际磁盘目录（HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Lxss\<guid>\BasePath）
    param([Parameter(Mandatory)][string]$DistroName)
    $root = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Lxss'
    if (-not (Test-Path -LiteralPath $root)) { return $null }
    foreach ($key in @(Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {
        $props = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
        if ($null -eq $props) { continue }
        if (-not $props.PSObject.Properties['DistributionName']) { continue }
        if ($props.DistributionName -ieq $DistroName) { return [string]$props.BasePath }
    }
    return $null
}

function Test-SamePath {
    param([string]$Left, [string]$Right)
    try {
        $l = [System.IO.Path]::GetFullPath($Left).TrimEnd('\')
        $r = [System.IO.Path]::GetFullPath($Right).TrimEnd('\')
    }
    catch { return $false }
    return [string]::Equals($l, $r, [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-FreeSpaceGB {
    param([string]$Path)
    $drive = Get-PSDrive -Name $Path.Substring(0, 1).ToUpper() -ErrorAction SilentlyContinue
    if (-not $drive) { return $null }
    return [math]::Round($drive.Free / 1GB, 1)
}

function Confirm-WslDistroPath {
    # 校验并准备磁盘目录，返回规范化的完整路径（发行版虚拟磁盘就落在这里）
    param([string]$Chosen, [string]$DistroName = $Script:KaliDistroName)
    try { $Chosen = [System.IO.Path]::GetFullPath($Chosen) } catch { throw "磁盘位置无效: $Chosen" }
    if ($Chosen -notmatch '^[A-Za-z]:\\') { throw "磁盘位置必须是本地盘符的绝对路径: $Chosen" }

    $freeGB = Get-FreeSpaceGB -Path $Chosen
    if ($null -eq $freeGB) { throw "盘符 $($Chosen.Substring(0, 2)) 不存在: $Chosen" }
    if ($freeGB -lt $Script:MinFreeGB) {
        Write-Warn2 "警告: 该盘可用空间仅 ${freeGB} GB，Kali 建议至少 ${Script:MinFreeGB} GB。"
        if (-not (Ask-Choice '仍要继续吗？' $false)) { throw '用户取消安装。' }
    }

    if (Test-Path -LiteralPath $Chosen) {
        $children = @(Get-ChildItem -LiteralPath $Chosen -Force -ErrorAction SilentlyContinue)
        if ($children.Count -gt 0) {
            Write-Warn2 "目录非空: $Chosen （建议使用空目录存放虚拟磁盘。）"
            if (-not (Ask-Choice '仍要使用该目录吗？' $false)) { throw '用户取消安装。' }
        }
    }
    else {
        # 只创建父目录：发行版目录交给 WSL 自己创建（--location / --move 都要求目标目录不存在或为空）
        $parent = Split-Path -Parent $Chosen
        if ($parent -and -not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
            Write-Dim "已创建目录: $parent"
        }
    }
    return $Chosen
}

function Resolve-WslDistroPath {
    # 确定 WSL 发行版磁盘位置：
    #   -KaliPath <完整路径>  直接指定（优先）
    #   -WslPath <根目录>     发行版落在 <根目录>\<发行版名>
    #   非交互                取推荐候选（非系统盘优先）
    #   交互                  列出各盘可用空间，可选预设项或自定义路径
    param([string]$DistroName = $Script:KaliDistroName)

    if ($KaliPath) { return (Confirm-WslDistroPath -Chosen $KaliPath -DistroName $DistroName) }
    if ($WslPath)  { return (Confirm-WslDistroPath -Chosen (Join-Path $WslPath $DistroName) -DistroName $DistroName) }

    $candidates = @()
    $dataDrives = @(Get-PSDrive -PSProvider FileSystem |
        Where-Object { $_.Name -notin @('C', 'Temp') -and $_.Free -gt ($Script:MinFreeGB * 1GB) } |
        Sort-Object Free -Descending)
    if ($dataDrives.Count -gt 0) {
        $candidates += (Join-Path "$($dataDrives[0].Name):\" "WSL\$DistroName")
    }
    $candidates += (Join-Path $env:LOCALAPPDATA "WSL\$DistroName")

    if ($NonInteractive) { return (Confirm-WslDistroPath -Chosen $candidates[0] -DistroName $DistroName) }

    Write-Info ''
    Write-Info '===== 选择 WSL 磁盘存储位置（发行版虚拟磁盘目录）====='
    for ($i = 0; $i -lt $candidates.Count; $i++) {
        $free = Get-FreeSpaceGB -Path $candidates[$i]
        $freeText = $null -ne $free ? "可用空间 ${free} GB" : '盘符不存在'
        Write-Host ("  [{0}] {1}   ({2})" -f ($i + 1), $candidates[$i], $freeText)
    }
    Write-Host '  [0] 自定义路径'
    Write-Dim '  提示: 也可用 -WslPath D:\WSL（根目录）或 -KaliPath D:\WSL\kali-linux（完整路径）跳过此步。'

    $chosen = $null
    while ($null -eq $chosen) {
        $answer = Read-InputOrDefault "请选择（0-$($candidates.Count)）" '1'
        if ($answer -eq '0') {
            $chosen = Read-InputOrDefault '请输入完整路径（如 D:\WSL\kali-linux）' $candidates[0]
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
    return (Confirm-WslDistroPath -Chosen $chosen -DistroName $DistroName)
}

function Move-WslDistro {
    # 把已安装发行版的虚拟磁盘迁移到指定目录（wsl --manage <distro> --move <path>）
    param([Parameter(Mandatory)][string]$DistroName, [Parameter(Mandatory)][string]$TargetPath)
    Write-Info "迁移 $DistroName 的磁盘到 $TargetPath ..."
    Write-Dim '（迁移期间该发行版会被停止，耗时取决于磁盘占用大小。）'
    $code = Invoke-WslDirect -Arguments @('--manage', $DistroName, '--move', $TargetPath)
    if ($code -ne 0) {
        Write-Warn2 "迁移失败（退出码 $code）。可手动执行:"
        Write-Warn2 "  wsl --manage $DistroName --move `"$TargetPath`""
        return $false
    }
    Write-Info '磁盘已迁移到指定位置。'
    return $true
}

function Show-WslStorageInfo {
    # 汇总各发行版当前磁盘位置，便于确认自定义位置是否生效
    $names = @(Get-WslDistroNames)
    if ($names.Count -eq 0) { return }
    Write-Info 'WSL 发行版磁盘位置:'
    foreach ($name in $names) {
        $basePath = Get-WslDistroBasePath -DistroName $name
        $text = $basePath ? $basePath : '(注册表中未找到)'
        Write-Host ("  {0,-16} {1}" -f $name, $text)
    }
}

# ---------------- Kali 安装 ----------------
function Install-Kali {
    param([string]$TargetPath)

    if (-not (Test-WslComponentsReady)) {
        throw "WSL 平台未就绪，无法安装 Kali。请先安装 WSL（install_wsl_kali.ps1 -Install WslOnly），重启后再运行一次。"
    }

    if (Test-DistroInstalled -DistroName $Script:KaliDistroName) {
        Write-Info "Kali ($Script:KaliDistroName) 已安装，跳过。"
        $currentPath = Get-WslDistroBasePath -DistroName $Script:KaliDistroName
        if ($currentPath) { Write-Dim "当前磁盘位置: $currentPath" }

        # 已安装但不在自定义位置 → 询问是否迁移
        if ($TargetPath -and $currentPath -and -not (Test-SamePath $currentPath $TargetPath)) {
            Write-Dim "目标位置: $TargetPath"
            if (Ask-Choice "是否把 Kali 磁盘迁移到 $TargetPath ？" $false) {
                [void](Move-WslDistro -DistroName $Script:KaliDistroName -TargetPath $TargetPath)
            }
            else {
                Write-Dim "稍后可手动迁移: wsl --manage $Script:KaliDistroName --move `"$TargetPath`""
            }
        }
        return
    }

    if (-not $TargetPath) { $TargetPath = Resolve-WslDistroPath }
    Write-Info "Kali 磁盘位置: $TargetPath"
    Write-Dim '（安装窗口可能弹出，属正常现象；下载约需几分钟。）'

    # 路径 1: 新版 WSL (>= 2.4.4) 支持 --location 直接装到指定目录
    $code = Invoke-WslDirect -Arguments @('--install', '-d', $Script:KaliDistroName, '--no-launch', '--location', $TargetPath)
    if ($code -eq 0 -and (Wait-DistroVisible -DistroName $Script:KaliDistroName)) {
        Write-Info 'Kali 安装完成。'
        return
    }
    Write-Dim "--location 安装路径返回 $code，尝试回退方案..."

    # 路径 2: 普通安装 + wsl --manage --move 迁移到自定义位置
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

    [void](Move-WslDistro -DistroName $Script:KaliDistroName -TargetPath $TargetPath)
    Write-Info 'Kali 安装完成。首次进入: wsl -d kali-linux'
}
