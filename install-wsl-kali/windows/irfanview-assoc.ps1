#requires -Version 7
<#
.SYNOPSIS
  IrfanView 图片文件关联 - 安装 / 撤销（原 irfanview-assoc.bat 的 PowerShell 7 移植版）
.DESCRIPTION
  写 HKCU（用户级）而非系统级：
    - 不需要管理员权限
    - 用户级优先级高于系统默认（如 .bmp 默认指向 Paint.Picture）
  /remove 只删 ProgID 与 OpenWithProgids 条目，保留扩展名键本身 —— Windows 自动回落系统默认关联。
.EXAMPLE
  .\irfanview-assoc.ps1                          # 关联（exe 自动探测，winget 安装目录优先）
  .\irfanview-assoc.ps1 -Remove                  # 撤销
  .\irfanview-assoc.ps1 -NoPause                 # 无人值守（不暂停）
  .\irfanview-assoc.ps1 -ExePath "D:\IrfanView\i_view64.exe"   # 手动指定 exe
#>
[CmdletBinding()]
param(
    [switch]$Remove,
    [switch]$NoPause,
    [string]$ExePath = ''   # 手动指定 i_view64.exe 路径，跳过自动探测
)

$ProgId = 'IrfanView.Image'
# 单行列表，避免续行问题
$Exts   = @('jpg','jpeg','jpe','png','bmp','dib','gif','webp','tif','tiff','ico','cur','svg','svgz',
            'psd','raw','cr2','nef','arw','dng','orf','raf','rw2','pef','srw',
            'jp2','j2k','jpf','jpx','jxl','heic','heif','avif','tga','targa','pcx','xcf','wbmp',
            'pbm','pgm','ppm','pnm','hdr','exr','dds')

# exe 定位：-ExePath 参数优先，否则按序探测（winget 默认安装目录 + 常见自定义目录）
$IvCandidates = @(
    'C:\Program Files\IrfanView\i_view64.exe',      # winget（IrfanView.IrfanView）默认目录
    'C:\Program Files (x86)\IrfanView\i_view64.exe',# winget 32 位默认目录
    'D:\IrfanView\i_view64.exe'                     # 本机历史自定义目录
)
$IvExe = if ($ExePath) {
    $ExePath
}
else {
    ($IvCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
}
if (-not $IvExe) {
    Write-Host '[X] IrfanView not found.' -ForegroundColor Red
    Write-Host '    已尝试: ' ($IvCandidates -join ' / ') -ForegroundColor Red
    Write-Host '    先安装（推荐 winget）:' -ForegroundColor Red
    Write-Host '      winget configure -f configurations\irfanview.winget --accept-configuration-agreements' -ForegroundColor Red
    Write-Host '    或手动指定路径:' -ForegroundColor Red
    Write-Host '      irfanview-assoc.ps1 -ExePath "D:\IrfanView\i_view64.exe"' -ForegroundColor Red
    exit 1
}

$classes = 'HKCU:\Software\Classes'
$progKey = "$classes\$ProgId"

function Invoke-Install {
    Write-Host '  Registering program...'
    # 注册 ProgID（注意: 默认值必须用 Set-Item 写，New-ItemProperty -Name '(default)' 只会建出名为 (default) 的普通值）
    New-Item -Path "$progKey\shell\open\command" -Force | Out-Null
    Set-Item -Path $progKey -Value 'IrfanView Image Viewer'
    Set-Item -Path "$progKey\shell\open\command" -Value "`"$IvExe`" `"%1`""
    New-Item -Path "$progKey\Capabilities" -Force | Out-Null
    New-ItemProperty -Path "$progKey\Capabilities" -Name 'ApplicationName' -Value 'IrfanView Image Viewer' -PropertyType String -Force | Out-Null
    New-Item -Path "$progKey\SupportedTypes" -Force | Out-Null

    foreach ($ext in $Exts) {
        New-ItemProperty -Path "$progKey\SupportedTypes" -Name ".$ext" -Value '' -PropertyType String -Force | Out-Null
        New-Item -Path "$classes\.$ext\OpenWithProgids" -Force | Out-Null
        Set-Item -Path "$classes\.$ext" -Value $ProgId
        New-ItemProperty -Path "$classes\.$ext\OpenWithProgids" -Name $ProgId -Value '' -PropertyType String -Force | Out-Null
    }
    Write-Host "  Associated $($Exts.Count) extensions."
    Write-Host ''
    Write-Host '  Verifying...'

    $bad = 0
    foreach ($ext in 'jpg','png','bmp','gif','webp','svg','ico','psd','raw') {
        $actual = (Get-Item -Path "$classes\.$ext" -ErrorAction SilentlyContinue).GetValue('')
        if ($actual -eq $ProgId) { Write-Host "    [OK ] .$ext" }
        else { Write-Host "    [BAD] .$ext"; $bad++ }
    }
    $cmd = (Get-Item -Path "$progKey\shell\open\command" -ErrorAction SilentlyContinue).GetValue('')
    if ($cmd -like "*$IvExe*") { Write-Host '    [OK ] open command' }
    else { Write-Host '    [BAD] open command'; $bad++ }

    Write-Host ''
    if ($bad -eq 0) {
        Write-Host '[OK] Done. Open a NEW Explorer window to see the changes.'
    }
    else {
        Write-Warning "$bad item(s) failed to verify."
    }
    return $bad
}

function Invoke-Remove {
    Write-Host '  Removing association...'
    Remove-Item -Path $progKey -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($ext in $Exts) {
        # 只删 OpenWithProgids 下的条目，保留扩展名键本身 → 回落系统默认关联
        Remove-ItemProperty -Path "$classes\.$ext\OpenWithProgids" -Name $ProgId -ErrorAction SilentlyContinue
    }
    Write-Host '[OK] Association removed.'
    Write-Host '  Note: extension keys themselves were left in place, so Windows'
    Write-Host '        falls back to the machine-level default for each format.'
    return 0
}

Write-Host ''
Write-Host "  IrfanView : $IvExe"
Write-Host "  ProgID    : $ProgId"
Write-Host "  Mode      : $(if ($Remove) { 'remove' } else { 'install' })"
Write-Host ''

$code = 1
if (-not (Test-Path -LiteralPath $IvExe)) {
    Write-Host "[X] IrfanView not found: $IvExe" -ForegroundColor Red
}
elseif ($Remove) { $code = Invoke-Remove }
else             { $code = Invoke-Install }

if (-not $NoPause) {
    Write-Host ''
    Read-Host 'Press Enter to exit' | Out-Null
}
exit $code
