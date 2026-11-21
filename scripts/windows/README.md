# Windows 开发环境脚本

一组 Windows 环境搭建 / 配置脚本。**`.ps1` 承载全部逻辑，`.bat` / `.cmd` 只做调用入口**（定位 pwsh → 转发参数 → 透传退出码）。

需要管理员权限的用 `.cmd` 入口（会自动弹 UAC），不需要的用 `.bat`。

---

## 脚本清单

| 脚本 | 作用 | 管理员 |
| --- | --- | --- |
| [`run-as-admin.cmd`](run-as-admin.cmd) | **一键提权入口**，双击即用 | 自动 |
| [`set-system-env-pwsh7.ps1`](set-system-env-pwsh7.ps1) | 把 9 个环境变量写成**系统级**（Machine scope） | 需 |
| [`windows-dev-setup.ps1`](windows-dev-setup.ps1) | 装 PowerShell 7 / Git / Git LFS / gsudo（支持代理，**pwsh 7 only**） | 自动 |
| [`install-ffmpeg.ps1`](install-ffmpeg.ps1) + [`install-ffmpeg.bat`](install-ffmpeg.bat) | 装 FFmpeg 到 `E:\ffmpeg` | 否 |
| [`irfanview-assoc.bat`](irfanview-assoc.bat) | IrfanView 图片关联（45 种格式），可撤销 | 否 |
| [`potplayer-assoc.bat`](potplayer-assoc.bat) + [`potplayer-assoc.ps1`](potplayer-assoc.ps1) | PotPlayer 视频关联（43 种格式），自动探测 exe，可撤销 | 否 |
| [`ffmpeg-install.bat`](ffmpeg-install.bat) | FFmpeg 的早期纯 bat 版本，保留备用 | 否 |

此外还有 2 个**本机专用、不入库**的脚本（见下方[Kali on WSL2](#kali-on-wsl2本机专用--不入库)）：
`install-kali-wsl.ps1`、`finish-kali-wsl.ps1`。

---

## 快速开始

```powershell
# 1. 一键提权 + 配置全部系统环境变量（最常用）
.\scripts\windows\run-as-admin.cmd

# 2. 装 FFmpeg（自动探测本机代理）
.\scripts\windows\install-ffmpeg.bat

# 3. 给图片关联 IrfanView
.\scripts\windows\irfanview-assoc.bat

# 4. 给视频关联 PotPlayer
.\scripts\windows\potplayer-assoc.bat
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
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\windows\set-system-env-pwsh7.ps1

# 先看它要干什么，不实际写入
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\windows\set-system-env-pwsh7.ps1 -DryRun
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

## 装工具

### PowerShell 7 / Git / Git LFS / gsudo

```powershell
# 基础用法（无代理）
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\windows\windows-dev-setup.ps1

# 带代理（优先 D 盘安装）
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\windows\windows-dev-setup.ps1 -Proxy http://127.0.0.1:7890

# 只打印命令，不实际执行
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\windows\windows-dev-setup.ps1 -DryRun

# 跳过某一步（可组合）
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\windows\windows-dev-setup.ps1 -SkipGsudo
```

`#Requires -Version 7.0` —— **必须 pwsh 7**，不支持 5.1；会自动弹 UAC。
退出码：`0` 成功 / `1` 有失败项 / `3` 版本不足（5.1）。

gsudo 走 winget（`gerardog.gsudo`）装到默认目录 `C:\Program Files\gsudo`（MSI 不支持自定义路径），
装完即可用 `gsudo <命令>` 提权执行单条命令。也可用 `-SkipGsudo` 跳过。

### FFmpeg

```bat
install-ffmpeg.bat                                  :: 装 / 升级（自动探测代理）
install-ffmpeg.bat -Force                           :: 强制重装
install-ffmpeg.bat -Proxy http://127.0.0.1:7897    :: 指定代理
install-ffmpeg.bat -InstallDir E:\tools\ffmpeg -Version 9.0.2
install-ffmpeg.bat /nopause                         :: 无人值守
```

装到 `E:\ffmpeg`（664MB），`bin` 自动进用户级 PATH。
**幂等**：已装且版本一致时 2 秒返回，不重复下载。

---

## 托管后当「一行流」用

风格对齐 `irm https://astral.sh/uv/install.ps1 | iex`：

```powershell
# 管道执行时无法传参，用环境变量传
$env:DEV_SETUP_PROXY = 'http://127.0.0.1:7890'
pwsh -NoProfile -c "irm https://raw.githubusercontent.com/lytree/xxx/main/install.ps1 | iex"
```

**必须用 `pwsh`，不能写 `powershell`。** 仓库内所有 `.ps1` 都已收敛到 PowerShell 7，
`powershell.exe`（5.1）跑任何一个都会失败。

若脚本托管在别处，额外设 `DEV_SETUP_URL` 指向它自己的 raw 地址 —— 管道执行时 `$PSCommandPath` 为空，
脚本需要靠这个地址把自己落盘再提权重启。

### 为什么文档命令和运行时闸要写两遍

`#Requires -Version 7.0` **只在加载脚本文件时生效**。`irm | iex` 送进来的是一段脚本块，
引擎不解析 `#Requires` —— 5.1 会被静默放行，然后在后面某个莫名其妙的地方炸掉。

所以脚本里另有一道运行时检查（`$PSVersionTable.PSVersion.Major -lt 7` → 打印安装指引 → `exit 3`），
让 `-File` 和 `irm | iex` 两条入口的版本行为完全一致。

---

## IrfanView 图片关联

给 `D:\IrfanView\i_view64.exe`（4.75）关联 45 种图片格式，双击直接用 IrfanView 打开。

```bat
irfanview-assoc.bat                    :: 关联
irfanview-assoc.bat /remove            :: 撤销
irfanview-assoc.bat /nopause          :: 无人值守
```

**为什么写用户级（HKCU）而非系统级**：用户级优先级**高于**系统级，无需管理员，
且能覆盖系统默认关联（`.bmp` 本来指向 `Paint.Picture`，`.jpg` 指向 `jpegfile`）。

`/remove` **只删** `IrfanView.Image` 和各扩展名下的 `OpenWithProgids` 条目，**保留扩展名键本身** ——
这样 Windows 会自动回落到系统默认关联，而不是变成"无程序关联"的异常状态。

覆盖格式：常规（jpg/png/bmp/gif/webp/tif/ico/svg）、相机 RAW（raw/cr2/nef/arw/dng/orf/raf/rw2/pef/srw）、
新格式（jp2/jxl/heic/avif）、设计格式（psd/tga/pcx/xcf/hdr/exr/dds）等共 45 种。

改路径：编辑脚本顶部的 `IV_EXE`。

---

## PotPlayer 视频关联

`potplayer-assoc.ps1`（全部逻辑）+ `potplayer-assoc.bat`（入口），把 43 种视频格式关联到 PotPlayer。

```bat
potplayer-assoc.bat                                       :: 关联（自动探测 exe）
potplayer-assoc.bat /remove                               :: 撤销
potplayer-assoc.bat -ExePath "D:\PotPlayer\PotPlayerMini64.exe"
potplayer-assoc.bat -Extensions mp4,mkv /nopause          :: 只关联指定格式
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

`/remove` **只删** `PotPlayer.Video` 和各扩展名下的 `OpenWithProgids\PotPlayer.Video`，
**保留扩展名键本身** —— 与 `irfanview-assoc.bat` 同一策略，Windows 自动回落到系统默认关联。

退出码：`0` 成功 / `1` 有失败项或没找到 exe / `3` PowerShell 5.1。

### 关联「看起来没生效」的两个原因

1. **UserChoice 压着**。Win10/11 的「打开方式 > 始终使用」会写
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.ext\UserChoice`，
   该键带签名校验，脚本不碰。删掉对应 `UserChoice` 键，或去
   「设置 > 应用 > 默认应用」重设一次。
2. **资源管理器没刷新**。换一个新开的 Explorer 窗口看，旧窗口的图标是缓存。

### 注意：本机装过 PotPlayer 但已卸载

`HKCU\Software\Classes\.mp4` / `.mkv` 下留有 `DaumLive64Backup.bak`（值指向 `WMP11.AssocFile.MP4`）——
DAUM 安装器的备份条目。**这是既有键，不要连带删除**，本脚本的 `/remove` 也不会碰它。

---

## Kali on WSL2（本机专用 · 不入库）

这两个脚本**只存在于本地**，已被 `.gitignore` 排除（`scripts/windows/*kali*.ps1`）——
镜像路径 `G:\Hyper\kali-linux\`、用户名等都是个人环境信息，没有对外分享价值。
下表仅为**本地索引**，仓库里下载不到，需要时直接跑本地文件。

| 脚本 | 作用 | 权限 |
| --- | --- | --- |
| `install-kali-wsl.ps1` | 把 `ext4.vhdx` 镜像原地导入 WSL2（`--import-in-place`，零拷贝） | 管理员 |
| `finish-kali-wsl.ps1` | 首次初始化：启动验证 / 密码 / 普通用户 / 免密 sudo | 普通 |

```powershell
# 1. 导入镜像（原地注册，不复制 25GB）
.\scripts\windows\run-as-admin.cmd
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\windows\install-kali-wsl.ps1

# 2. 首次初始化（幂等，可重复跑）
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\windows\finish-kali-wsl.ps1 -User kali
```

细节（`--import-in-place` 的回退策略、`-Force` 的删数据风险、导入前后的校验项等）
见两个脚本自身的注释头。通用踩坑（`wsl -e` 不经 shell 解析、`passwd` 无输入会卡死等）
见下方[踩过的坑](#踩过的坑)。

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

**未安装**：Git / Git LFS（`windows-dev-setup.ps1` 可装，尚未执行）

---

## 踩过的坑

- **`.ps1` 必须是 UTF-8 with BOM。** Windows PowerShell 5.1 读无 BOM 的 UTF-8 会按 GBK 解码，中文注释和字符串全变乱码，脚本当场语法错误（报 `意外的标记`）。改这些文件务必保留 BOM。
- **`#Requires` 对 `irm | iex` 无效。** 它只在引擎加载脚本**文件**时检查；管道送进来的是脚本块，`#Requires` 根本不被解析，5.1 会被静默放行。要拦版本就得自己写运行时检查。
- **`irm | iex` 的落盘重启用 `utf8BOM` 而非 `UTF8`。** pwsh 7 里 `-Encoding UTF8` 等于 `utf8NoBOM`，落盘的文件再被别的进程读时没有保险。
- **`(Get-Process -Id $PID).Path` 不能用来找宿主 shell。** 在 ISE / VS Code 集成终端里它可能返回 `powershell.exe` 或干脆为空；提权重启应显式解析 `pwsh`。
- **`wsl -e` 不经过 shell 解析。** 必须显式 `-e /bin/bash -lc`，否则 `;` `|` `>` 会被当作可执行文件名的一部分，`execvpe` 直接报 `No such file or directory`。
- **函数返回退出码会混进输出。** `Invoke-Wsl` 里 `& $Wsl ...` 的 stdout 也进输出流，`return $LASTEXITCODE` 再追加一个 —— `$code` 变成数组，而 `数组 -ne 0` 返回的是「非零元素」永远为真。**退出码必须走脚本级变量。**
- **`passwd` 在无输入时无限等待。** 脚本里包 `passwd` 必须做成显式开关（如 `-SetPassword`），否则非交互调用会卡死。
- **WSL 输出后光标常停在行尾**（不补末尾换行），紧随其后的 `Write-Host` 会接在同一行。用「刚跑过 WSL」标志位决定是否补换行，比到处手写 `Write-Host ''` 可靠。
- **`.bat` / `.cmd` 必须是纯 ASCII + CRLF。** cmd.exe 按 GBK 解析，中文符号（`√` `！`）会变乱码；LF 换行也可能解析异常。所以这些文件里的提示文字都是英文，且已转成 CRLF。
- **`enabledelayedexpansion` 下 `echo [!]` 会打成 `[]`。** `!]` 被当成变量引用吃掉了。写提示文字别用 `!`，改用 `[WARN]`。
- **cmd.exe 把 `,` 当参数分隔符，`-File` 收不了数组。** `-Extensions mp4,mkv` 经 `.bat` 的 shift 循环会变成两个参数，pwsh 报「找不到接受自变量 'mkv' 的位置参数」。所以：`.bat` 里**原样转发 `%*`**（不用 shift 循环），`.ps1` 的对应参数声明成 `[string]` 再 `-split '[,;\s]+'`。
- **别把参数丢给 `findstr` 判断。** 载荷里含逗号时 findstr 会把 `/a` 当开关（"忽略 /a" + "错误的命令行"）。用 `call set` 纯字符串替换来剥 `/nopause`。
- **注册表的「默认值」不能靠 `New-ItemProperty -Name '(default)'` 写。** 那只会建出一个**名字叫 `(default)` 的普通值**，真正的默认值仍是空 —— 写完 `reg query /ve` 一看是空的。必须 `Set-Item -Path $k -Value ...`（且 `-Value $null` 会报 `Value cannot be null`，删空值得走 `[Microsoft.Win32.Registry]….DeleteValue('')`）。
- **读注册表默认值只能用 `GetValue('')`。** `Get-Item` 在注册表 provider 下返回 `Microsoft.Win32.RegistryKey`，默认值在 `GetValue('')` 里；`PSObject` 里那个属性的**名字是空字符串**（控制台显示成 `(default)`），按 `'(default)'` 去索引会拿到 `$null`。而且 `Set-StrictMode -Version Latest` 下直接取 `.'(default)'` 属性会**抛异常**而不是返回 `$null`。
- **原生命令输出经 `2>&1` 是数组，不是字符串。** `ffmpeg -version` 把版本写到 stderr，`$out -match ...` 对数组只返回布尔、**不填 `$Matches`** —— 必须先 `[string]$line` 显式取单行，否则版本检测永远失败。
- **bat 里用 `!VAR!` 就要开 `setlocal enabledelayedexpansion`**，否则 `!` 不展开。
- **`pause` 会卡住非交互调用。** 需要无人值守时提供 `/nopause` 开关。
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
`install-ffmpeg.ps1` 就是这么做的，并内置了 10 个常见代理端口的自动探测：

```
7897 → 7890 → 10809 → 10808 → 1080 → 7891 → 1081 → 2080 → 8888 → 8080
```

探测到且验证能访问 GitHub 才采用，没探测到就直连。下载有**多轮重试**（有代理时 4 轮），
因为代理链路偶发 `curl (35) Connection was reset`。

### 安装安全性（FFmpeg）

- 校验包内三个 exe（`ffmpeg` / `ffprobe` / `ffplay`）齐全才继续，zip 不完整会报错退出
- **旧目录改名让位而非直接删除**，移动失败自动回滚，成功后才删备份
- 装完自动清理 245MB 的下载 zip
