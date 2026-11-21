#Requires -Version 7.0
<#
.SYNOPSIS
    用 PowerShell 7 写入【系统级】环境变量（Machine scope）。

.DESCRIPTION
    把 uv / Python 相关配置与 Java / Maven / Node / vcpkg 路径统一提升到系统级（Machine），
    并去重追加进系统 PATH，最后广播 WM_SETTINGCHANGE。

    必须以管理员身份运行 —— 非管理员写 Machine 级变量会静默失败。

.PARAMETER DryRun
    只打印将要执行的动作，不实际写入。
#>
[CmdletBinding()]
param(
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ----------------------------------------------------------------------------------
# 日志
# ----------------------------------------------------------------------------------
$logFile = Join-Path $env:TEMP 'set-system-env.log'
function L {
    param([string] $Message, [ValidateSet('Info', 'Ok', 'Warn', 'Err', 'Cmd')][string] $Level = 'Info')
    $color = switch ($Level) {
        'Ok' { 'Green' }; 'Warn' { 'Yellow' }; 'Err' { 'Red' }; 'Cmd' { 'DarkGray' }; default { 'Gray' }
    }
    $line = '[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message
    Write-Host $line -ForegroundColor $color
    try { Add-Content -LiteralPath $logFile -Value $line -Encoding utf8 -ErrorAction Stop } catch { }
}

# ----------------------------------------------------------------------------------
# 0. 管理员校验
# ----------------------------------------------------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

L "PowerShell $($PSVersionTable.PSVersion)  ($([Environment]::OSVersion.VersionString))"
L "管理员权限：$isAdmin"

if (-not $isAdmin) {
    L '当前不是管理员，无法写系统级环境变量。请右键以管理员身份重新运行。' -Level Err
    exit 2
}

# ----------------------------------------------------------------------------------
# 1. 目标环境变量（全部 Machine 级）
# ----------------------------------------------------------------------------------
$envSpecs = @(
    # --- uv / Python ---
    [pscustomobject]@{ Name = 'UV_PYTHON_INSTALL_DIR'; Value = 'E:\python\install'; OnPath = $false; Group = 'uv' }
    [pscustomobject]@{ Name = 'UV_CACHE_DIR'; Value = 'E:\python\cache'; OnPath = $false; Group = 'uv' }
    [pscustomobject]@{ Name = 'UV_TOOL_DIR'; Value = 'E:\python\tool'; OnPath = $false; Group = 'uv' }
    [pscustomobject]@{ Name = 'UV_TOOL_BIN_DIR'; Value = 'E:\python\tool\bin'; OnPath = $true; Group = 'uv' }

    # --- 开发工具链 ---
    [pscustomobject]@{ Name = 'VCPKG_ROOT'; Value = 'E:\CPP\vcpkg'; OnPath = $true; Group = 'toolchain' }
    [pscustomobject]@{ Name = 'MAVEN_HOME'; Value = 'E:\Java\apache-maven-3.6.3'; OnPath = $true; Group = 'toolchain' }
    [pscustomobject]@{ Name = 'JAVA_HOME'; Value = 'E:\Java\jdk-25.0.3+9'; OnPath = $true; Group = 'toolchain' }
    [pscustomobject]@{ Name = 'NODE_HOME'; Value = 'E:\nodejs'; OnPath = $true; Group = 'toolchain' }
    [pscustomobject]@{ Name = 'NPM_CONFIG_PREFIX'; Value = 'E:\node\modules'; OnPath = $true; Group = 'node' }
)

# 需要额外放进 PATH 的目录（bin 之外）
$extraPathEntries = @(
    'E:\CPP\vcpkg'
    'E:\Java\apache-maven-3.6.3\bin'
    'E:\Java\jdk-25.0.3+9\bin'
    'E:\nodejs'
    # npm 全局包区：eslint / hexo / tsx / asar / nrm / pnpm 等全局命令的 shim 都在这里，
    # 不加进 PATH 的话这些命令全部调不出来（Node 本体目录里只有 node/npm/npx/corepack）
    'E:\node\modules'
    'E:\python\tool\bin'
)

# 用户级同名变量需要清理的清单（提升到系统级后，用户级会遮蔽系统级）
$shadowedNames = $envSpecs.Name

# ----------------------------------------------------------------------------------
# 2. 写入系统级环境变量
# ----------------------------------------------------------------------------------
L '开始写入系统级（Machine）环境变量…' -Level Info
foreach ($s in $envSpecs) {
    try {
        $old = [Environment]::GetEnvironmentVariable($s.Name, 'Machine')
        if ($old -eq $s.Value) {
            L ("{0,-22} 已是 {1}，跳过" -f $s.Name, $s.Value) -Level Ok
            continue
        }
        if ($DryRun) {
            L ("[DryRun] {0,-22} {1} -> {2}" -f $s.Name, $(if ($old) { $old } else { '<未设置>' }), $s.Value) -Level Cmd
            continue
        }
        [Environment]::SetEnvironmentVariable($s.Name, $s.Value, 'Machine')
        $now = [Environment]::GetEnvironmentVariable($s.Name, 'Machine')
        if ($now -eq $s.Value) {
            L ("{0,-22} {1} -> {2}" -f $s.Name, $(if ($old) { $old } else { '<未设置>' }), $s.Value) -Level Ok
        } else {
            L ("{0,-22} 写入校验失败（当前 {1}）" -f $s.Name, $now) -Level Err
        }
    } catch {
        L ("{0,-22} 失败：{1}" -f $s.Name, $_.Exception.Message) -Level Err
    }
}

# ----------------------------------------------------------------------------------
# 3. 系统 PATH 去重追加
# ----------------------------------------------------------------------------------
L '开始处理系统 PATH…' -Level Info
try {
    $cur = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $items = [System.Collections.Generic.List[string]]::new()
    if ($cur) {
        foreach ($p in ($cur -split ';')) {
            $t = $p.Trim()
            if ($t) { $items.Add($t) }
        }
    }
    $before = $items.Count

    # 先剔除重复项（保留首次出现）
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $deduped = [System.Collections.Generic.List[string]]::new()
    foreach ($p in $items) {
        $norm = $p.TrimEnd('\')
        if ($seen.Add($norm)) { $deduped.Add($p) } else { L "PATH 去重（重复项）：$p" -Level Warn }
    }
    $items = $deduped

    $targets = [System.Collections.Generic.List[string]]::new()
    foreach ($s in $envSpecs) { if ($s.OnPath) { $targets.Add($s.Value) } }
    foreach ($e in $extraPathEntries) { $targets.Add($e) }

    foreach ($t in $targets) {
        $norm = $t.Trim().TrimEnd('\')
        if ($items | Where-Object { $_.TrimEnd('\').ToLowerInvariant() -eq $norm.ToLowerInvariant() }) {
            continue
        }
        if ($DryRun) {
            L "[DryRun] PATH 追加：$norm" -Level Cmd
        } else {
            $items.Add($norm)
            L "PATH 追加：$norm" -Level Ok
        }
    }

    if (-not $DryRun) {
        $newPath = $items -join ';'
        [Environment]::SetEnvironmentVariable('Path', $newPath, 'Machine')
        L ("系统 PATH：{0} -> {1} 项" -f $before, $items.Count) -Level Ok
    }
} catch {
    L ("PATH 处理失败：{0}" -f $_.Exception.Message) -Level Err
}

# ----------------------------------------------------------------------------------
# 4. 清理用户级同名变量（否则会遮蔽系统级）
# ----------------------------------------------------------------------------------
L '检查用户级同名变量（会遮蔽系统级）…' -Level Info
foreach ($n in $shadowedNames) {
    try {
        $u = [Environment]::GetEnvironmentVariable($n, 'User')
        if ($null -ne $u -and $u -ne '') {
            if ($DryRun) {
                L "[DryRun] 将清除用户级 $n = $u" -Level Cmd
            } else {
                [Environment]::SetEnvironmentVariable($n, $null, 'User')
                L "已清除用户级 $n（原 $u），避免遮蔽系统级" -Level Ok
            }
        }
    } catch {
        L ("清理用户级 {0} 失败：{1}" -f $n, $_.Exception.Message) -Level Warn
    }
}

# ----------------------------------------------------------------------------------
# 5. 广播环境变量变更
# ----------------------------------------------------------------------------------
if (-not $DryRun) {
    try {
        if (-not ('Win32EnvNotify' -as [type])) {
            Add-Type -Namespace 'Win32' -Name 'EnvNotify' -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
public static extern System.IntPtr SendMessageTimeout(System.IntPtr hWnd, uint Msg, System.UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out System.UIntPtr lResult);
'@
        }
        $res = [System.UIntPtr]::Zero
        [void] [Win32EnvNotify]::SendMessageTimeout([System.IntPtr] 0xffff, 0x1A, [System.UIntPtr]::Zero, 'Environment', 0x2, 5000, [ref] $res)
        L '已广播 WM_SETTINGCHANGE，新开终端即生效。' -Level Ok
    } catch {
        L ("广播失败（不影响写入，需重开终端）：{0}" -f $_.Exception.Message) -Level Warn
    }
}

# ----------------------------------------------------------------------------------
# 6. 回读校验
# ----------------------------------------------------------------------------------
L '回读校验…' -Level Info
$bad = 0
foreach ($s in $envSpecs) {
    $m = [Environment]::GetEnvironmentVariable($s.Name, 'Machine')
    $u = [Environment]::GetEnvironmentVariable($s.Name, 'User')
    if ($m -eq $s.Value) {
        L ("  OK   {0,-22} = {1}{2}" -f $s.Name, $m, $(if ($u) { "   [用户级仍存在: $u]" } else { '' })) -Level Ok
    } else {
        L ("  BAD  {0,-22} = {1}（预期 {2}）" -f $s.Name, $(if ($m) { $m } else { '<未设置>' }), $s.Value) -Level Err
        $bad++
    }
}

$mp = [Environment]::GetEnvironmentVariable('Path', 'Machine')
L ("  系统 PATH 共 {0} 项，{1} 字符" -f (($mp -split ';' | Where-Object { $_ }).Count), $mp.Length) -Level Info

# PATH 关键项抽查
L 'PATH 关键项抽查…' -Level Info
$mustHave = @('E:\nodejs', 'E:\node\modules', 'E:\python\tool\bin')
foreach ($m in $mustHave) {
    $inPath = @(($mp -split ';') | Where-Object { $_.Trim().TrimEnd('\').ToLowerInvariant() -eq $m.TrimEnd('\').ToLowerInvariant() }).Count -gt 0
    $dirOk = Test-Path -LiteralPath $m
    $mark = if ($inPath -and $dirOk) { 'OK  ' } elseif (-not $inPath) { 'BAD ' } else { 'WARN' }
    $note = if (-not $inPath) { '不在 PATH' } elseif (-not $dirOk) { '目录不存在' } else { '' }
    L ("  {0} {1,-22} {2}" -f $mark, $m, $note) -Level $(if ($mark -eq 'OK  ') { 'Ok' } elseif ($mark -eq 'WARN') { 'Warn' } else { 'Err' })
    if ($mark -eq 'BAD ') { $bad++ }
}

# 清理用户级同名变量后，验证是否还有遮蔽
L '用户级遮蔽检查…' -Level Info
foreach ($n in $shadowedNames) {
    $u = [Environment]::GetEnvironmentVariable($n, 'User')
    if ($u) {
        L ("  {0,-22} 用户级仍存在 = {1}（会遮蔽系统级！）" -f $n, $u) -Level Warn
    }
}

L ("完成。异常项：{0}。日志：{1}" -f $bad, $logFile) -Level $(if ($bad -eq 0) { 'Ok' } else { 'Err' })
exit ([int]($bad -ne 0))
