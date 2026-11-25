# install-wsl-kali — Windows 开发环境一键安装

一键安装 **WSL + Kali Linux + .NET SDK + Python uv + Node fnm**，并应用一组 **Windows 开发者设置**。

实现思路参考 [microsoft/WindowsDeveloperConfig](https://github.com/microsoft/WindowsDeveloperConfig)（dism 启用功能 → `wsl --install --no-distribution` → 重启恢复），并在其基础上扩展了工具链安装与系统设置。

## 环境要求

- **PowerShell 7+（pwsh）**，不支持 Windows PowerShell 5.1
- Windows 10 21H2+ / Windows 11
- 需要管理员权限（未提权时自动弹 UAC）
- BIOS 已开启虚拟化（未开启时脚本会提示）

> 逐步安装过程、每步交互点与装后验证见 [INSTALL.md](INSTALL.md)。

## 快速开始

```powershell
# 交互式：菜单选择 + 交互选择 Kali 安装路径
.\install-wsl-kali\install_wsl_kali.ps1

# 一键全装（WSL → .NET → uv/fnm → Windows 设置 → Kali）
.\install-wsl-kali\install_wsl_kali.ps1 -Install All

# 仅装某一项
.\install-wsl-kali\install_wsl_kali.ps1 -Install WslOnly       # 仅 WSL 平台
.\install-wsl-kali\install_wsl_kali.ps1 -Install KaliOnly      # 仅 Kali（要求 WSL 已就绪）
.\install-wsl-kali\install_wsl_kali.ps1 -Install DotNetOnly    # 仅 .NET SDK 10
.\install-wsl-kali\install_wsl_kali.ps1 -Install ToolsOnly     # 仅 Python uv + Node fnm
.\install-wsl-kali\install_wsl_kali.ps1 -Install SettingsOnly  # 仅 Windows 开发者设置

# 非交互 + 指定 Kali 安装路径
.\install-wsl-kali\install_wsl_kali.ps1 -Install All -KaliPath D:\WSL\kali-linux -NonInteractive
```

交互模式菜单：

```
[1] 一键安装 WSL + Kali + .NET + uv/fnm + Windows 设置（推荐）
[2] 仅安装 WSL 平台
[3] 仅安装 Kali（要求 WSL 已就绪）
[4] 仅安装 .NET SDK
[5] 仅安装 Python uv + Node fnm
[6] 仅应用 Windows 开发者设置
[0] 退出
```

### 参数

| 参数 | 说明 |
|---|---|
| `-Install <All\|WslOnly\|KaliOnly\|DotNetOnly\|ToolsOnly\|SettingsOnly>` | 跳过菜单直接执行 |
| `-KaliPath <路径>` | 指定 Kali 安装目录（免交互路径选择） |
| `-NonInteractive` | 非交互模式，全部采用默认值（恢复任务自动使用此模式） |
| `-NoReboot` | 需要重启时不自动重启，仅提示 |
| `-SkipWingetConfig` | 跳过 winget configure 路径，直接用 winget install / 官方脚本 |

## 模块结构

```
install-wsl-kali/
├── install_wsl_kali.ps1    # 主入口：参数、菜单、调度（All 顺序: WSL → .NET → uv/fnm → 设置 → Kali）
├── _common.ps1             # 公共辅助：输出/交互、UAC 提权、wsl 进程封装、winget 封装、
│                           #   winget configure 封装、PATH 管理、重启恢复计划任务
├── _wsl.ps1                # WSL 平台安装（dism 启用功能 + wsl --install）
├── _kali.ps1               # Kali 安装（交互式选择安装路径，推荐非系统盘）
├── _dotnet.ps1             # .NET SDK 10 安装
├── _tools.ps1              # Python uv + Node fnm（各自独立安装，互不影响）
├── _windows-settings.ps1   # Windows 开发者设置（注册表）
├── configurations/         # winget configure 声明式配置
    ├── wsl.winget          #   启用 VirtualMachinePlatform + 安装 Microsoft.WSL（schema 0.2）
    ├── dotnet.winget       #   安装 Microsoft.DotNet.SDK.10（schema 0.2）
    ├── uv.winget           #   安装 astral-sh.uv（schema 0.2）
    ├── fnm.winget          #   安装 Schniz.fnm（schema 0.2）
    ├── devtools.winget     #   安装 PowerShell 7 / Git / Git LFS / gsudo（schema 0.2）
    ├── ffmpeg.winget       #   安装 FFmpeg（Gyan.FFmpeg，schema 0.2）
    ├── irfanview.winget    #   安装 IrfanView 64 位 + 插件包（schema 0.2）
    ├── windows-default.winget  #   Windows 设置默认档 13 项（DSC v3 原生 Registry 资源）
    └── windows-full.winget     #   Windows 设置全部档 17 项（DSC v3 原生 Registry 资源）
└── windows/                # Windows 环境脚本（IrfanView / PotPlayer 文件关联、系统环境变量；
                            #   下载安装类已全部迁到 configurations/*.winget），详见 windows/README.md
```

模块通过 dot-source 加载进主入口作用域，新增功能只需添加 `_xxx.ps1` 并注册到主入口的模块列表。

## 安装策略（三级回退）

每个组件按以下顺序尝试，任何一级成功即停止；脚本**幂等**，已安装/已设置的项自动跳过，可重复运行：

1. **winget configure**（`configurations/*.winget`，声明式、幂等；`-SkipWingetConfig` 可跳过。Windows 设置用 DSC v3 原生 `Microsoft.Windows/Registry` 资源，需 winget 1.11+，首次运行自动安装 dscv3 处理器）
2. **winget install**（`.NET` → `Microsoft.DotNet.SDK.10`，uv → `astral-sh.uv`，fnm → `Schniz.fnm`）
3. **官方脚本/回退**（.NET → `dotnet-install.ps1`；uv → astral.sh 安装脚本；fnm → GitHub Release 下载）

例外：

- **WSL**：configure 成功后仍复用重启判定（功能启用但需激活 → 注册恢复任务 → 重启续装）；旧版运行时走 `wsl --update --web-download` 回退
- **Kali**：无 winget 包，直接 `wsl --install -d kali-linux`；WSL ≥ 2.4.4 支持 `--location` 装到指定目录，否则普通安装后用 `wsl --manage kali-linux --move` 迁移

## Kali 安装路径选择

交互模式下会列出各盘可用空间并推荐非系统盘（预留 ≥ 20 GB），支持预设项和自定义路径；校验盘符存在、空间不足会警告确认、目录非空需确认。

## Windows 开发者设置

注册表设置分两档：**默认档（13 项）** / **全部档（17 项）**，交互模式可选，非交互模式自动应用默认档。

**默认档**：Win32 长路径、开发者模式、Sudo（inline）、显示文件扩展名、显示隐藏文件、任务栏“结束任务”、关闭小组件、关闭资源管理器同步提供程序提示、关闭开始/搜索联网搜索建议、关闭搜索亮点、关闭开始菜单推荐内容、关闭开始菜单账户通知、**启用远程桌面**（含防火墙规则）。

**全部档额外 4 项**：资源管理器默认打开“此电脑”、快速访问不显示常用文件夹/最近文件、勿扰模式。

实现细节：优先通过 winget configure 应用 `windows-default.winget` / `windows-full.winget`（DSC v3 原生 Registry 资源，逐项幂等），configure 不可用时回退逐项写注册表；远程桌面防火墙规则由脚本单独启用；HKCU Explorer 项可选立即重启资源管理器生效。

## 重启恢复

WSL 组件启用后如需重启，脚本会注册一个**登录触发的计划任务**（`WslKaliInstallResume`），重启登录后自动以 `-NonInteractive` 继续剩余安装，完成后任务自清理。手动中断的失败不会留下恢复任务。

## 常见问题

| 现象 | 处理 |
|---|---|
| `winget configure` 报内部错误 `-2146233079` | 微软已知问题，通常是缺 VC++ 运行库；或加 `-SkipWingetConfig` 走安装命令路径 |
| 提示未检测到 Hypervisor | 进 BIOS 开启虚拟化（Intel VT-x / AMD-V） |
| 刚装完的命令当前终端找不到 | 脚本已刷新会话 PATH；仍不行则重开终端 |
| 个别设置项失败 | 重新运行脚本即可重试（幂等） |
| Store 版 WSL 下载失败 | 脚本自动走 `--web-download` 回退 |
