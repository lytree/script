# Windows 开发环境脚本

本目录只保留 **3 个非安装类脚本**（文件关联 / 系统环境变量）。
**下载安装类任务已全部迁移到 [`../configurations/`](../configurations/) 的 `*.winget` 声明式配置**，
由 `winget configure` 统一安装（与 .NET / uv / fnm 同一套机制；**WSL 例外**，由脚本用 dism + `wsl.exe` 直接装）。

原 `.bat` / `.cmd` 入口与 `windows-dev-setup.ps1` / `install-ffmpeg.ps1` 已删除（git 历史可找回）。

---

## winget 配置（下载安装）

| 配置 | 内容 |
| --- | --- |
| [`../configurations/devtools.winget`](../configurations/devtools.winget) | PowerShell 7 / Git / Git LFS / gsudo |
| [`../configurations/toolchain.winget`](../configurations/toolchain.winget) | .NET SDK 10 / FFmpeg（Gyan full build）/ fnm / uv（默认安装参数） |
| [`../configurations/irfanview.winget`](../configurations/irfanview.winget) | IrfanView 64 位 + 插件包（RAW / HEIC / JXL 解码依赖） |

```powershell
# 安装开发基础工具链（pwsh7 + Git + LFS + gsudo）
winget configure -f .\install-wsl-kali\configurations\devtools.winget --accept-configuration-agreements --disable-interactivity

# 安装 .NET SDK 10 + FFmpeg + fnm + uv（一份合并配置，默认安装）
winget configure -f .\install-wsl-kali\configurations\toolchain.winget --accept-configuration-agreements --disable-interactivity

# 安装 IrfanView（本体 + 插件包）
winget configure -f .\install-wsl-kali\configurations\irfanview.winget --accept-configuration-agreements --disable-interactivity
```

声明式、幂等：已装且版本一致时自动跳过，重复运行无害。

### 安装位置：D 盘优先（`devtools.winget` 做不到，用配套脚本）

`Microsoft.WinGet.DSC/WinGetPackage` 资源只接受 `id / source / version / ensure / matchOption / useLatest / installMode`，
**没有安装路径参数**，所以 YAML 里写不了"装到 D 盘"。要控制落盘位置跑配套脚本：

```powershell
# D 盘存在 → D:\<App>（对齐本机既有布局 D:\Git、D:\Git LFS）；D 盘不可用 → C:\Program Files\<App>
gsudo pwsh -NoProfile -File .\install-wsl-kali\configurations\install-devtools.ps1

# 只预览，不安装
.\install-wsl-kali\configurations\install-devtools.ps1 -WhatIf

# 想保留 Program Files 结构 / 完全按默认位置装
.\install-devtools.ps1 -BaseDir 'D:\Program Files'
.\install-devtools.ps1 -SkipLocation
```

- `Git`（inno 安装器）认 `--location`，基本一定生效；`Git LFS` 跟随已装的 Git 目录，不单独指定路径。
- `PowerShell 7` / `gsudo` 是 msi/wix 包，能否吃下 `--location` 由安装包决定；带路径失败时脚本会去掉路径重试，并在结尾打印每个命令的真实位置。
- `PowerShell 7` 在 winget 源里默认给的是 msix（装进 WindowsApps，无法指定路径），脚本强制 `--installer-type msi`。
- 已安装的项直接跳过，不会把 D 盘上的安装迁移/重装到 C 盘。

> ⚠️ **FFmpeg 注意**：`Gyan.FFmpeg` 的安装包托管在 GitHub，本机实测 winget 走 GitHub
> 连接不稳（详见下方[winget 代理能力](#这台机器的-winget-代理能力是残缺的)）。
> configure 失败时用 `curl --proxy` 直下 full zip 手动解压兜底。

## 脚本清单（非安装类）

| 脚本 | 作用 | 管理员 |
| --- | --- | --- |
| [`set-system-env-pwsh7.ps1`](set-system-env-pwsh7.ps1) | 把 9 个环境变量写成**系统级**（Machine scope） | 需 |
| [`irfanview-assoc.ps1`](irfanview-assoc.ps1) | IrfanView 图片关联（45 种格式），可撤销（原 `.bat` 的 pwsh 7 移植版） | 否 |
| [`potplayer-assoc.ps1`](potplayer-assoc.ps1) | PotPlayer 视频关联（43 种格式），自动探测 exe，可撤销 | 否 |

约定：**必须 pwsh 7**（`#Requires -Version 7` 或运行时闸），`powershell.exe`（5.1）会失败。

---

## 快速开始

```powershell
# 1. 系统环境变量（管理员终端，或 gsudo 提权）
gsudo pwsh -NoProfile -ExecutionPolicy Bypass -File .\install-wsl-kali\windows\set-system-env-pwsh7.ps1

# 2. IrfanView 图片关联
.\install-wsl-kali\windows\irfanview-assoc.ps1

# 3. PotPlayer 视频关联
.\install-wsl-kali\windows\potplayer-assoc.ps1

# 4. 安装开发工具链 / FFmpeg / IrfanView → 走 winget configure（见上）
```

---

## 环境变量（系统级）

`set-system-env-pwsh7.ps1` 写入的 9 个变量，**当前已全部生效**：

| 变量 | 值 | 进 PATH |
| --- | --- | --- |
| `UV_PYTHON_INSTALL_DIR` | `E:\python\install` | — |
| `UV_CACHE_DIR` | `E:\python\cache` | — |
| `UV_TOOL_DIR` | `E:\python\tool` | — |
| `UV_TOOL_BIN_DIR` | `E:\python\tool\bin` | ✔ |
| `VCPKG_ROOT` | `E:\CPP\vcpkg` | ✔ |
| `MAVEN_HOME` | `E:\Java\apache-maven-3.6.3` | ✔ |
| `JAVA_HOME` | `E:\Java\jdk-25.0.3+9` | ✔ |
| `NODE_HOME` | `E:\nodejs` | ✔ |
| `NPM_CONFIG_PREFIX` | `E:\node\modules` | ✔ |

系统 PATH 额外包含：`E:\Java\apache-maven-3.6.3\bin`、`E:\Java\jdk-25.0.3+9\bin`、`E:\node\modules`、`E:\python\tool\bin`。

```powershell
# 已在管理员终端里
pwsh -NoProfile -ExecutionPolicy Bypass -File .\install-wsl-kali\windows\set-system-env-pwsh7.ps1

# 先看它要干什么，不实际写入
pwsh -NoProfile -ExecutionPolicy Bypass -File .\install-wsl-kali\windows\set-system-env-pwsh7.ps1 -DryRun
```

`#Requires -Version 7.0` —— **必须 pwsh 7**，不支持 5.1。
退出码：`0` 成功 / `1` 有异常项 / `2` 非管理员。

### 脚本做的四件事

1. 逐个写 **Machine 级**变量，每项**写完立即回读校验**
2. 系统 PATH **去重**（保留首次出现）后追加缺失项
3. **清除用户级同名变量** —— 否则用户级会**遮蔽**系统级，导致"明明写了却不生效"
4. 广播 `WM_SETTINGCHANGE`

> ⚠️ 只创建目录并写变量，**不负责往里装 JDK / Maven / Node 本体**。
> 路径写好之后，把工具装到这些目录才会被识别。

### Node.js 是「双目录」布局

Node 相关的东西分在两个目录，**两者都要在 PATH 里**才能用全：

| 目录 | 内容 | 在 PATH |
| --- | --- | --- |
| `E:\nodejs` | Node 本体：`node.exe`、`npm`、`npx`、`corepack` | ✔ |
| `E:\node\modules` | npm 全局包区：16 个包 + 各种 `.cmd` shim | ✔ |

`E:\node\modules` 里是 `eslint`、`hexo`、`tsx`、`asar`、`nrm`、`pnpm`、`concurrently`、`freebuff` 等全局命令。
**漏了这个目录，这些命令一个都调不出来** —— 而 `node`/`npm` 照常可用，很有迷惑性。

这个布局由 `C:\Users\hiyan\.npmrc` 决定，与系统环境变量是两套机制：

```
prefix=E:\node\modules      # 全局包装这里
cache=E:\node\cache
registry=https://registry.npmmirror.com
```

---

## IrfanView 图片关联

给 IrfanView（4.75+）关联 45 种图片格式，双击直接用 IrfanView 打开。
exe 自动探测：`-ExePath` 参数优先，否则按序尝试 winget 默认目录（`C:\Program Files\IrfanView`）、
32 位目录、`D:\IrfanView` 等候选路径。
IrfanView 本体推荐用 `irfanview.winget` 安装（见上）。

```powershell
irfanview-assoc.ps1                                          # 关联（exe 自动探测）
irfanview-assoc.ps1 -Remove                                  # 撤销
irfanview-assoc.ps1 -NoPause                                 # 无人值守（不暂停）
irfanview-assoc.ps1 -ExePath "D:\IrfanView\i_view64.exe"     # 手动指定 exe
```

**为什么写用户级（HKCU）而非系统级**：用户级优先级**高于**系统级，无需管理员，
且能覆盖系统默认关联（`.bmp` 本来指向 `Paint.Picture`，`.jpg` 指向 `jpegfile`）。

`-Remove` **只删** `IrfanView.Image` 和各扩展名下的 `OpenWithProgids` 条目，**保留扩展名键本身** ——
这样 Windows 会自动回落到系统默认关联，而不是变成"无程序关联"的异常状态。

覆盖格式：常规（jpg/png/bmp/gif/webp/tif/ico/svg）、相机 RAW（raw/cr2/nef/arw/dng/orf/raf/rw2/pef/srw）、
新格式（jp2/jxl/heic/avif）、设计格式（psd/tga/pcx/xcf/hdr/exr/dds）等共 45 种。

退出码：`0` 成功 / `1` 有失败项。

---

## PotPlayer 视频关联

`potplayer-assoc.ps1` 把 43 种视频格式关联到 PotPlayer（原 `.bat` 入口已删，直接跑 ps1）。

```powershell
potplayer-assoc.ps1                                       # 关联（自动探测 exe）
potplayer-assoc.ps1 -Remove                               # 撤销
potplayer-assoc.ps1 -ExePath "D:\PotPlayer\PotPlayerMini64.exe"
potplayer-assoc.ps1 -Extensions "mp4,mkv" -NoPause        # 只关联指定格式
```

**exe 自动探测**按序尝试：`App Paths` 注册表 → 已注册的 `PotPlayer.Media.64` ProgID 反查 →
13 个常见安装目录（C/D/E 盘的 `DAUM\PotPlayer` 等）→ `PATH`。都找不到则报错退出（码 `1`）并提示用 `-ExePath`。

**注册表布局**（`HKCU`，ProgID 名 `PotPlayer.Video`）：

| 键 | 值 |
| --- | --- |
| `...\PotPlayer.Video` | `PotPlayer 视频播放器` |
| `...\PotPlayer.Video\DefaultIcon` | `"<exe>",0` |
| `...\PotPlayer.Video\shell\open\command` | `"<exe>" "%1"` |
| `...\PotPlayer.Video\Capabilities` | `ApplicationName` / `Description` |
| `...\.mp4`（每个扩展名） | 默认值 = `PotPlayer.Video` |
| `...\.mp4\OpenWithProgids` | 值 `PotPlayer.Video` = 空串 |

`-Remove` **只删** `PotPlayer.Video` 和各扩展名下的 `OpenWithProgids\PotPlayer.Video`，
**保留扩展名键本身** —— 与 `irfanview-assoc.ps1` 同一策略，Windows 自动回落到系统默认关联。

退出码：`0` 成功 / `1` 有失败项或没找到 exe。

### 关联「看起来没生效」的两个原因

1. **UserChoice 压着**。Win10/11 的「打开方式 > 始终使用」会写
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.ext\UserChoice`，
   该键带签名校验，脚本不碰。删掉对应 `UserChoice` 键，或去
   「设置 > 应用 > 默认应用」重设一次。
2. **资源管理器没刷新**。换一个新开的 Explorer 窗口看，旧窗口的图标是缓存。

### 注意：本机装过 PotPlayer 但已卸载

`HKCU\Software\Classes\.mp4` / `.mkv` 下留有 `DaumLive64Backup.bak`（值指向 `WMP11.AssocFile.MP4`）——
DAUM 安装器的备份条目。**这是既有键，不要连带删除**，`-Remove` 也不会碰它。

---

## Kali on WSL2（本机专用 · 不入库）

这两个脚本**只存在于本地**，已被 `.gitignore` 排除（`install-wsl-kali/windows/*kali*.ps1`）——
镜像路径 `G:\Hyper\kali-linux\`、用户名等都是个人环境信息，没有对外分享价值。
下表仅为**本地索引**，仓库里下载不到，需要时直接跑本地文件。

| 脚本 | 作用 | 权限 |
| --- | --- | --- |
| `install-kali-wsl.ps1` | 把 `ext4.vhdx` 镜像原地导入 WSL2（`--import-in-place`，零拷贝） | 管理员 |
| `finish-kali-wsl.ps1` | 首次初始化：启动验证 / 密码 / 普通用户 / 免密 sudo | 普通 |

```powershell
# 1. 导入镜像（原地注册，不复制 25GB，管理员终端）
pwsh -NoProfile -ExecutionPolicy Bypass -File .\install-wsl-kali\windows\install-kali-wsl.ps1

# 2. 首次初始化（幂等，可重复跑）
pwsh -NoProfile -ExecutionPolicy Bypass -File .\install-wsl-kali\windows\finish-kali-wsl.ps1 -User kali
```

---

## 本机现状（2026-10）

**已就绪**

| 组件 | 版本 / 位置 |
| --- | --- |
| PowerShell 7 | 7.6.6 · `C:\Program Files\PowerShell\7\` |
| FFmpeg | 9.0.2 full build · `E:\ffmpeg\`（664MB） |
| uv | 0.12.22 · `~\AppData\Local\Microsoft\WinGet\Links` |
| Python | 3.12.14 / 3.13.15 / 3.14.8 · `E:\python\install\` |
| Node.js | v24.13.0 · `E:\nodejs` |
| npm | 11.20.0（全局包区 `E:\node\modules`） |
| IrfanView | 4.75 · 45 个图片格式已关联 |
| gsudo | 2.6.1 · `C:\Program Files\gsudo\Current\` |
| PotPlayer 关联脚本 | 已入库（`potplayer-assoc.ps1`）· PotPlayer 本体**未安装**，未执行 |
| 环境变量 | 9 个系统级变量全部生效 |

**未安装**：Git / Git LFS（`devtools.winget` 可装，尚未执行）

---

## 踩过的坑

> 前三条与已删除的 `.bat` 入口相关，保留作为历史记录。

- **`.ps1` 必须是 UTF-8 with BOM。** Windows PowerShell 5.1 读无 BOM 的 UTF-8 会按 GBK 解码，中文注释和字符串全变乱码，脚本当场语法错误（报 `意外的标记`）。改这些文件务必保留 BOM。
- **`#Requires` 对 `irm | iex` 无效。** 它只在引擎加载脚本**文件**时检查；管道送进来的是脚本块，`#Requires` 根本不被解析，5.1 会被静默放行。要拦版本就得自己写运行时检查。
- **`.bat` / `.cmd` 必须是纯 ASCII + CRLF。** cmd.exe 按 GBK 解码，中文符号（`√` `！`）会变乱码；LF 换行也可能解析异常。（`.bat` 入口已删，此坑成为历史）
- **注册表的「默认值」不能靠 `New-ItemProperty -Name '(default)'` 写。** 那只会建出一个**名字叫 `(default)` 的普通值**，真正的默认值仍是空。必须 `Set-Item -Path $k -Value ...`。
- **读注册表默认值只能用 `GetValue('')`。** `Get-Item` 在注册表 provider 下返回 `Microsoft.Win32.RegistryKey`，默认值在 `GetValue('')` 里；`Set-StrictMode -Version Latest` 下直接取 `.'(default)'` 属性会**抛异常**而不是返回 `$null`。
- **原生命令输出经 `2>&1` 是数组，不是字符串。** `ffmpeg -version` 把版本写到 stderr，`$out -match ...` 对数组只返回布尔、**不填 `$Matches`** —— 必须先 `[string]$line` 显式取单行。
- **`pause` 会卡住非交互调用。** 需要无人值守时提供 `-NoPause` 开关。
- **非管理员写 Machine 级变量会静默失败**，不报错。所以脚本开头显式校验并以退出码 `2` 退出 —— 别以为跑完了就成功了。
- **用户级变量会遮蔽系统级。** 提升到系统级后必须清掉用户级同名变量，否则 `echo %JAVA_HOME%` 读到的还是旧值。
- **`winget upgrade` 的"没得升"** 返回码是 `-1978335189`，不是 0。
- **`UV_PYTHON_INSTALL_DIR` 直接指向安装根**，不要再套一层。设成 `E:\python` 会让 Python 落到 `E:\python\cpython-*`，与 `E:\python\install\` 下的其他版本不一致。
- 环境变量改完**要新开终端**才生效（已开着的窗口读的还是旧值）。

### 这台机器的 winget 代理能力是残缺的

装 FFmpeg 时实测：

| 方式 | 结果 |
| --- | --- |
| `winget install Gyan.FFmpeg` 直连 | `0x80072efd` 连接失败 |
| `winget install ... --proxy` | 报错，需管理员启用 `winget settings --enable ProxyCommandLineOptions` |
| `winget` + `HTTP_PROXY` 环境变量 | **卡死 20 分钟**无进展，临时下载文件时间戳不动 |
| `curl --proxy` 直接下载 | **33 秒**完成 245MB |

**结论**：凡是从 GitHub 拉包的场景，用 curl 走代理 + 手动解压，比 winget 靠谱得多。
`toolchain.winget` 里的 `Gyan.FFmpeg` 在这台机器上有同样的风险，configure 失败时用 curl 直下兜底。
