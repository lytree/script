# 安装步骤说明

本文档按实际执行顺序，逐步说明 `install-wsl-kali` 的完整安装过程、每一步会发生什么、需要在哪里交互，以及装完后的验证方法。

> 语法/参数总览见 [README.md](README.md)，本文只讲"怎么装、装的时候发生什么"。

## 第 0 步：安装前检查

| 检查项 | 方法 | 不满足时 |
|---|---|---|
| PowerShell 7+ | `pwsh -v` 输出 ≥ 7.0 | 从 https://aka.ms/pwsh 安装后重试 |
| Windows 版本 | Win10 21H2+ / Win11 | 升级系统 |
| 管理员权限 | — | 脚本自动弹 UAC，点"是"即可 |
| BIOS 虚拟化 | 任务管理器 → 性能 → CPU → "虚拟化: 已启用" | 进 BIOS 开启 Intel VT-x / AMD-V |
| 磁盘空间 | Kali 目标盘预留 ≥ 20 GB | 脚本会在路径选择时警告确认 |

保存好未提交的工作——**WSL 组件启用后可能需要重启电脑**。

## 第 1 步：启动脚本

```powershell
cd F:\Code\github\script
.\install-wsl-kali\install_wsl_kali.ps1
```

- 未提权运行 → 自动弹 UAC 提权重跑（参数原样转发）
- 出现主菜单，选择安装范围：

```
[1] 一键安装 WSL + Kali + .NET + uv/fnm + Windows 设置（推荐）
[2] 仅安装 WSL 平台
[3] 仅安装 Kali（要求 WSL 已就绪）
[4] 仅安装 .NET SDK
[5] 仅安装 Python uv + Node fnm
[6] 仅应用 Windows 开发者设置
[0] 退出
```

选 `[1]` 时实际执行顺序为：**WSL → .NET → uv/fnm → Windows 设置 → Kali**。

## 第 2 步：WSL 平台安装

1. 检测 WSL 组件状态（已就绪则整段跳过）
2. dism 启用 `VirtualMachinePlatform` / `Microsoft-Windows-Subsystem-Linux` 功能
   - 返回 3010 = 功能已启用但需重启生效
3. 安装新版 WSL 运行时（优先 winget configure 的 `wsl.winget`，失败回退 `wsl --install --no-distribution`，Store 下载失败再走 `--web-download`）

**需要重启时**：脚本注册登录恢复计划任务 `WslKaliInstallResume` → 自动重启（除非 `-NoReboot`）→ 重启登录后自动以非交互模式继续剩余步骤，装完任务自清理。**重启后无需手动做任何事。**

## 第 3 步：.NET SDK 10

1. `dotnet --list-sdks` 已有 10.x → 跳过
2. 依次尝试：`dotnet.winget`（configure）→ `winget install Microsoft.DotNet.SDK.10` → 官方 `dotnet-install.ps1`
3. 装完自动把安装目录加入系统 PATH，当前会话立即可用

## 第 4 步：Python uv + Node fnm

uv 和 fnm **各自独立安装**，一个失败不影响另一个：

- **uv**：已装检测 → `uv.winget`（configure）→ `winget install astral-sh.uv` → astral.sh 官方脚本（装到 `~\.local\bin`）
- **fnm**：已装检测 → `fnm.winget`（configure）→ `winget install Schniz.fnm` → GitHub Release 下载解压到 `%LOCALAPPDATA%\fnm`
- fnm 装完会询问是否把 `fnm env --use-on-cd` 初始化写入 PowerShell Profile（默认是；Profile 已含则跳过）

## 第 5 步：Windows 开发者设置

1. 选择设置范围：`[1] 默认（13 项）` / `[2] 全部（17 项）`
2. 优先 winget configure 应用对应的 `windows-default.winget` / `windows-full.winget`（DSC v3 原生 Registry 资源，需 winget 1.11+，首次运行会自动安装 dscv3 处理器）；configure 不可用/失败时自动回退逐项写注册表
3. 逐项幂等：已是目标值自动跳过；个别项失败只警告不中断
4. 默认档包含**启用远程桌面**（含防火墙规则）——不需要远程访问的机器请选"全部档"之外考虑，或事后手动关闭远程桌面
5. 结束后询问是否立即重启资源管理器（HKCU Explorer 项需重启 explorer 生效，屏幕会闪一下）

## 第 6 步：Kali Linux

1. **交互选择安装路径**：列出各盘可用空间、推荐非系统盘；输入预设编号或自定义路径
   - 校验：盘符存在、空间 ≥ 20 GB（不足警告确认）、目录非空需确认
   - 参数直跑时用 `-KaliPath` 免去此步
2. 安装：WSL ≥ 2.4.4 直接 `wsl --install -d kali-linux --location <路径>` 装到指定目录；旧版先普通安装再 `wsl --manage kali-linux --move` 迁移
3. Store 下载失败自动走 `--web-download`

## 第 7 步：安装后验证

```powershell
wsl -l -v                # 应列出 kali-linux（VERSION 2）
wsl -d kali-linux        # 进入 Kali，首次进入会要求创建 UNIX 用户
dotnet --list-sdks       # 应列出 10.x
uv --version             # uv 版本号
fnm --version            # fnm 版本号
fnm install --lts        # （可选）安装 Node LTS
uv python install 3.14   # （可选）安装 Python 解释器
```

## 非交互 / 无人值守安装

```powershell
.\install-wsl-kali\install_wsl_kali.ps1 -Install All -KaliPath D:\WSL\kali-linux -NonInteractive
```

- 全部交互点采用默认值（设置走默认档、fnm Profile 自动写入、不重启 explorer）
- `-NoReboot`：需重启时不自动重启，手动重启后恢复任务照常续装

## 中断与重试

- 脚本**幂等**：任何时候中断（Ctrl+C / 断电 / 重启），重新运行即可从断点继续，已完成步骤自动跳过
- 仅"WSL 需重启"场景会留下恢复任务；普通失败不留残留任务
- 个别 Windows 设置项失败 → 直接重跑脚本重试

## 卸载参考

```powershell
wsl --unregister kali-linux              # 删除 Kali 及其数据（不可恢复）
winget uninstall Microsoft.WSL           # 卸载 WSL 运行时
winget uninstall Microsoft.DotNet.SDK.10 # 卸载 .NET SDK
winget uninstall astral-sh.uv            # 卸载 uv
winget uninstall Schniz.fnm              # 卸载 fnm
Get-ScheduledTask WslKaliInstallResume -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false
```

Windows 设置项为注册表值，可在"设置"应用或注册表编辑器中手动还原。
