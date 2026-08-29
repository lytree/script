#!/usr/bin/env pwsh
# ============================================================================
# search_videos_btsou.ps1
# 扫描指定目录下视频文件的"番号",逐个调用 btsou.cs 搜索对应磁力链接。
#
# 用法:
#   pwsh search_videos_btsou.ps1 [-Dir <目录>] [-OutDir <输出目录>] [-Pages N] [-Precise]
#
# 说明:
#   * 番号正则: (FC2-PPV-\d+|FC2-\d+|[A-Z]{2,5}-\d+)
#   * 无番号的文件(如播放器)自动跳过
#   * 每个番号 -> <OutDir>/<番号>.txt (纯磁链,stdout 直出)
#   * 汇总     -> <OutDir>/all_magnets.txt
#   * 统计     -> <OutDir>/summary.txt  (番号,条数)
#   * -Precise 仅保留标题含番号的结果(大幅降噪);默认不过滤以保召回
# ============================================================================

param(
    [string]$Dir      = "C:\Users\hiyan\Downloads\Video",
    [string]$OutDir   = "F:\Code\Github\script\btsou_results",
    [int]   $Pages    = 1,
    [switch]$Precise
)

$repo  = "F:\Code\Github\script"
$btsou = Join-Path $repo "src\btsou.cs"

Set-Location $repo

$pattern = '(FC2-PPV-\d+|FC2-\d+|[A-Z]{2,5}-\d+)'

# ---- 提取不重复番号 ----
$codes = @{}
foreach ($f in Get-ChildItem -LiteralPath $Dir -ErrorAction SilentlyContinue) {
    if ($f.Name -match $pattern) { $codes[$Matches[1]] = $true }
}
$codeList = $codes.Keys | Sort-Object

if ($codeList.Count -eq 0) {
    Write-Host "未在目录中找到任何番号: $Dir"
    exit 0
}
Write-Host "发现 $($codeList.Count) 个番号: $($codeList -join ', ')"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$allMag = Join-Path $OutDir 'all_magnets.txt'
"" | Set-Content -Encoding utf8 $allMag

$summaryLines = @()
foreach ($code in $codeList) {
    Write-Host "=== 搜索 $code ==="
    $txt = Join-Path $OutDir "$code.txt"
    # 用 splatting 拼参数,避免空字符串参数导致 dotnet run 解析失败
    $appArgs = @('search', $code, '--magnet', '--pages', $Pages)
    if ($Precise) { $appArgs += '--precise' }
    # 磁链走 stdout;进度/日志走 stderr(此处丢弃)
    dotnet run $btsou -- @appArgs > $txt 2>$null
    $mags = (Get-Content -Path $txt -Encoding utf8 | Where-Object { $_ -like 'magnet:*' })
    $n = $mags.Count
    Add-Content -Encoding utf8 $allMag "`n# $code  ($n 条)"
    Add-Content -Encoding utf8 $allMag $mags
    $summaryLines += "$code,$n"
    Write-Host "$code -> $n 条磁链"
}

$summaryLines | Set-Content -Encoding utf8 (Join-Path $OutDir 'summary.txt')
Write-Host "完成。结果目录: $OutDir"
