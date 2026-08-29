#!/usr/bin/env pwsh
# ============================================================================
# run.ps1 - 仓库统一脚本入口(支持 Tab 补全脚本名与脚本参数)
#
# 用法:
#   . .\run.ps1                # 加载 run 函数(首次),之后 run <脚本> <参数>
#   .\run.ps1 <脚本> [参数...]  # 直接调用,补全仍生效
#
# Tab 补全:
#   * 第 1 个位置参数            -> 仓库下所有 .cs / .ps1 脚本(模糊匹配)
#   * 已选好脚本后 + 空 token    -> 当前脚本支持的命令行选项
#   * 已选好脚本后 + --xxx       -> 按前缀过滤的选项
#   * 已选好脚本后 + basename    -> 自动展开为仓库内完整相对路径
#   * 其他                      -> 走 PowerShell 默认文件路径补全
#
# 示例:
#   .\run.ps1 btsou<Tab>               # -> src/search/btsou.cs
#   .\run.ps1 btsou.cs<Tab>            # -> src/search/btsou.cs
#   .\run.ps1 src/search/btsou.cs <Tab># -> 列出该脚本的全部 --xxx 选项
#   .\run.ps1 src/search/btsou.cs --p<Tab>
#                                        # -> --pages --precise --proxy
#
# 兼容: PowerShell 7+ (Windows / Linux / macOS)
# ============================================================================

[CmdletBinding()]
param()

# ---- 仓库根目录 ----
$script:RunRepoRoot = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }

# ---- 脚本发现(排除自身、bin / obj / .git / btsou_results) ----
function Initialize-RunScripts {
    Get-ChildItem -Path $script:RunRepoRoot -Recurse -Include *.cs, *.ps1 -File |
        Where-Object {
            $_.Name -ne 'run.ps1' -and
            $_.FullName -notmatch '[\\/](\.git|bin|obj|btsou_results)[\\/]'
        } |
        ForEach-Object {
            $rel = $_.FullName.Substring($script:RunRepoRoot.Length).TrimStart('\', '/')
            [pscustomobject]@{
                Path    = $rel
                Base    = [System.IO.Path]::GetFileName($rel)
                Ext     = $_.Extension.ToLowerInvariant()
            }
        }
}
$script:RunScripts = @(Initialize-RunScripts)

# 按 basename 索引,加速补全与选项解析
$script:RunScriptByBase = @{}
foreach ($s in $script:RunScripts) {
    if (-not $script:RunScriptByBase.ContainsKey($s.Base)) {
        $script:RunScriptByBase[$s.Base] = $s.Path
    }
}

# ---- 把用户输入的脚本名解析为仓库内实际路径 ----
function Resolve-RunScriptPath {
    param([string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return $null }
    # 1) 精确路径
    $abs = if ([System.IO.Path]::IsPathRooted($Name)) { $Name }
           else { Join-Path $script:RunRepoRoot $Name }
    if (Test-Path -LiteralPath $abs) { return $Name }
    # 2) basename 匹配
    $base = Split-Path $Name -Leaf
    if ($script:RunScriptByBase.ContainsKey($base)) {
        return $script:RunScriptByBase[$base]
    }
    return $null
}

# ---- 选项缓存(从 .cs 源码静态提取 --xxx / -x) ----
$script:RunOptionCache = @{}

function Get-RunScriptOptions {
    param([string]$ScriptRelPath)
    if ([string]::IsNullOrWhiteSpace($ScriptRelPath)) { return @() }
    $resolved = Resolve-RunScriptPath $ScriptRelPath
    if (-not $resolved) { return @() }
    if ($script:RunOptionCache.ContainsKey($resolved)) {
        return $script:RunOptionCache[$resolved]
    }
    $abs = Join-Path $script:RunRepoRoot $resolved
    if (-not (Test-Path -LiteralPath $abs) -or ($abs -notlike '*.cs')) {
        $script:RunOptionCache[$resolved] = @()
        return @()
    }
    $opts = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    try {
        $c = Get-Content -LiteralPath $abs -Raw -ErrorAction Stop
        foreach ($m in [regex]::Matches($c, '["'']--[a-zA-Z][a-zA-Z0-9-]+["'']')) {
            $v = $m.Value.Trim("'", '"')
            if ($v.Length -gt 2) { [void]$opts.Add($v) }
        }
        foreach ($m in [regex]::Matches($c, '["'']-[a-zA-Z][a-zA-Z0-9]*["'']')) {
            $v = $m.Value.Trim("'", '"')
            if ($v.Length -eq 2) { [void]$opts.Add($v) }
        }
    } catch {}
    $sorted = @($opts | Sort-Object)
    $script:RunOptionCache[$resolved] = $sorted
    return $sorted
}

# ---- 参数补全器 ----
$script:RunCompleter = {
    param($wordToComplete, $commandAst, $cursorPosition)

    # 已落定的位置参数(跳过正在输入的 token / -参数名 / -参数值)
    $elements = $commandAst.CommandElements
    $inProgressStart = if ([string]::IsNullOrEmpty($wordToComplete)) {
        $cursorPosition
    } else {
        $cursorPosition - $wordToComplete.Length
    }
    $completedPositional = @()
    $expectValue = $false
    for ($i = 1; $i -lt $elements.Count; $i++) {
        $el = $elements[$i]
        if ($el.Extent.EndOffset -gt $inProgressStart) { break }
        if ($expectValue) { $expectValue = $false; continue }
        if ($el -is [System.Management.Automation.language.CommandParameterAst]) {
            $text = $el.Extent.Text
            if ($text -notmatch '[:=]$') { $expectValue = $true }
            continue
        }
        $txt = $el.Extent.Text
        if ([string]::IsNullOrEmpty($txt)) { continue }
        $completedPositional += $txt.Trim("'", '"')
    }

    # 位置 0: 补全脚本路径
    if ($completedPositional.Count -eq 0) {
        $script:RunScripts |
            Where-Object { $_.Path -like "*$wordToComplete*" } |
            ForEach-Object {
                [System.Management.Automation.CompletionResult]::new(
                    $_.Path, $_.Path, 'ProviderItem', $_.Path)
            }
        return
    }

    $scriptName = $completedPositional[0]

    # 位置 ≥ 1,wordToComplete 为空(已敲完脚本名/上一个 token,准备开始下一个):
    #   主动列出该脚本的全部选项,方便用户挑选
    if ([string]::IsNullOrEmpty($wordToComplete)) {
        $resolved = Resolve-RunScriptPath $scriptName
        if ($resolved) {
            Get-RunScriptOptions $resolved | ForEach-Object {
                [System.Management.Automation.CompletionResult]::new(
                    $_, $_, 'ParameterName', $_)
            }
        }
        return
    }

    # 位置 ≥ 1,wordToComplete 是 basename:展开为仓库内完整相对路径
    if ($wordToComplete -notmatch '[\\/]' -and $wordToComplete -notlike '-*') {
        $resolved = Resolve-RunScriptPath $wordToComplete
        if ($resolved -and $resolved -ne $wordToComplete) {
            [System.Management.Automation.CompletionResult]::new(
                $resolved, $resolved, 'ProviderItem', $resolved)
            return
        }
    }

    # 位置 ≥ 1,wordToComplete 以 - 开头:按前缀补全该脚本的选项
    if ($wordToComplete -like '-*') {
        $resolved = Resolve-RunScriptPath $scriptName
        Get-RunScriptOptions $resolved |
            Where-Object { $_ -like "$wordToComplete*" } |
            ForEach-Object {
                [System.Management.Automation.CompletionResult]::new(
                    $_, $_, 'ParameterName', $_)
            }
        return
    }
}

foreach ($n in @('run', 'run.ps1')) {
    Register-ArgumentCompleter -CommandName $n -ScriptBlock $script:RunCompleter
}

# ---- 入口函数 ----
function global:run {
    [CmdletBinding()]
    param(
        [Parameter(Position = 0)]
        [string]$Script,

        [Parameter(ValueFromRemainingArguments)]
        [string[]]$Arguments
    )

    if ([string]::IsNullOrWhiteSpace($Script)) {
        Write-Host '用法: run <脚本名或路径> [参数...]' -ForegroundColor Yellow
        Write-Host ''
        Write-Host "可用脚本 (共 $($script:RunScripts.Count) 个):" -ForegroundColor Cyan
        $script:RunScripts | ForEach-Object { Write-Host "  $($_.Path)" }
        return
    }

    $resolved = Resolve-RunScriptPath $Script
    if (-not $resolved) {
        Write-Error "脚本不存在: $Script"
        exit 2
    }
    $abs = if ([System.IO.Path]::IsPathRooted($resolved)) { $resolved }
           else { Join-Path $script:RunRepoRoot $resolved }

    if (-not (Test-Path -LiteralPath $abs)) {
        Write-Error "脚本不存在: $Script"
        exit 2
    }

    Set-Location -LiteralPath $script:RunRepoRoot

    $ext = [System.IO.Path]::GetExtension($abs).ToLowerInvariant()
    switch ($ext) {
        '.cs' {
            & dotnet run $abs -- @Arguments
            exit $LASTEXITCODE
        }
        '.ps1' {
            & pwsh -File $abs @Arguments
            exit $LASTEXITCODE
        }
        default {
            Write-Error "不支持的脚本类型: $Script"
            exit 2
        }
    }
}

# ---- 直接执行本脚本时:转发到 run ----
# dot-source 时 InvocationName='.',直调时为 '.\run.ps1' / 'run.ps1'
if ($MyInvocation.InvocationName -and $MyInvocation.InvocationName -ne '.') {
    run @args
}