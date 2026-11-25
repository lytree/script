# script

`lytree/script` —— 个人脚本仓库。主体是 **.NET 10 File-Based App** 工具集，另外归档了从 `lytree/GMScript` 迁移来的浏览器 / Windows / 第三方脚本，以及它们的安装与维护说明。

## 快速开始

统一入口是 `run.ps1`（PowerShell 7+，支持 Tab 补全）：

```powershell
. .\run.ps1
run btsou list              # basename 模糊匹配到 src/search/btsou.cs
run src/search/btsou.cs --help
```

直接执行也可以：

```bash
dotnet run src/search/btsou.cs -- list
```

更完整的仓库操作说明见 [`AGENT.md`](AGENT.md)。

## 目录结构

| 路径 | 内容 |
| --- | --- |
| `src/` | 按功能分类的 C# File-Based App 脚本（tdl / search / data / files / video / crawler / office …） |
| `demo/`、`lib/`、`models/`、`data/`、`output/` | 示例、共享代码、模型、运行时数据与产物 |
| `run.ps1`、`env.cs` | 统一执行入口与公共路径扩展 |
| [`browser/`](browser/) | 2 个自维护油猴脚本（**发布源**，见[油猴脚本](#油猴脚本browser)） |
| [`install-wsl-kali/windows/`](install-wsl-kali/windows/) | 3 个 Windows 环境配置脚本 + 2 份 winget 安装配置 |
| [`third-party/`](third-party/) | 6 个上游油猴脚本的只读镜像 + 同步工具 |
| `scripts/` | 迁移后仅余 `LICENSE`（自维护脚本的 MIT 许可） |

> **发布源已迁到本仓库根目录**：v1.2.1 起两个油猴脚本改由本仓库发版；v1.2.2 起脚本目录由 `scripts/browser/`
> 移到仓库根目录 `browser/`，`@updateURL` / `@downloadURL` 相应改为
> `raw.githubusercontent.com/lytree/script/main/browser/...`，安装与自动更新都走下方地址。
> 原 `lytree/GMScript` 仓库的 `userscripts/` 已冻结、不再更新 ——
> **老用户需按下方地址重装一次**才能接上新的更新链路（见[变更记录](#变更记录)）。

`data/`、`models/`、`output/`、`lib/` 是数据与产物目录，不是代码目录，新增脚本不要塞进去。

## 油猴脚本（browser/）

个人维护的油猴脚本（Tampermonkey / Violentmonkey）合集，存放在 [`browser/`](browser/)。通过本仓库 `main` 分支的 raw 地址发布，**装一次即可自动更新**。脚本源自 `lytree/GMScript`。

### 一键安装

> 前置条件：浏览器先装好 [Tampermonkey](https://www.tampermonkey.net/) 或 Violentmonkey。
> 打开下面任一地址会弹出脚本管理器的安装页；若页面直接显示源码，说明没装脚本管理器。
>
> **同一功能的脚本只装一个。** Telegram 系只有一个脚本（#2 合并版）；旧版 `telegram-only` 已从仓库移除，若你此前装过，请先停用/删除旧版再装 #2，否则媒体查看器里会出现重复按钮。

#### 1. Get Microsoft Rewards (修复/增强版) · v1.2.2

```
https://raw.githubusercontent.com/lytree/script/main/browser/get-microsoft-rewards.user.js
```

👉 [点击安装](https://raw.githubusercontent.com/lytree/script/main/browser/get-microsoft-rewards.user.js)

<sub>微软 Rewards 自动助手：本地自动搜索（多热搜源轮换）、每日活动、自动签到、阅读任务，支持 PC / 移动端接口双通道。**业务日期按太平洋时间（PT）计算**——Rewards 的日界是 PT 午夜（北京 15:00 / 16:00），dashboard 接口的 `dailySetPromotions` key 又可能按 UTC 出，用浏览器本地日期去比会差 8 小时、拿不到当天任务；v1.2.0 起统一以 PT 业务日为准，接口 key 兼容 UTC 口径。**浮动图标可拖拽**——位置只在当前页面内有效，不写存储、不跨刷新保留，每次打开都回到右下角（想立刻归位就双击标题栏）；靠近屏幕边缘时展开会自动往内收，展开后标题栏同样可拖，内容超长时面板内部滚动。</sub>

#### 2. Telegram Media Downloader (Merged) · v1.2.2 · 推荐

```
https://raw.githubusercontent.com/lytree/script/main/browser/telegram-media-downloader-merged.user.js
```

👉 [点击安装](https://raw.githubusercontent.com/lytree/script/main/browser/telegram-media-downloader-merged.user.js)

<sub>由三份上游实现（Nestor Qin / yoshiko.wisoky / copyMister）与旧 `telegram-only` 变体合并而成的单一实现：受限频道下载、WebA(webz) + WebK(webk) 双端挂载、原生 `downloadToDisc` 通道优先、任务面板（标题栏可拖动、位置记忆、双击复位、取消 / 重试 / 续传 / 4 MB 分片）、右键菜单与选择栏批量下载、整组下载（相册 + 评论区）、受保护消息 `.no-forwards` 文字复制解锁、五语 i18n；不含任何推广 / 邀请 / 统计外链。**注意：这是新的脚本身份**，旧版 `telegram-only`（v1.0.7）已下线，请先停用/卸载旧版再装本脚本，否则媒体查看器会出现重复按钮。</sub>

<sub>以上地址同时是 `@downloadURL` 与 `@updateURL`。脚本管理器会按此地址定期检查版本，**升级无需重新安装**。</sub>

### 脚本列表

| 文件名 | 版本 | 说明 |
| --- | --- | --- |
| [`get-microsoft-rewards.user.js`](browser/get-microsoft-rewards.user.js) | 1.2.2 | 微软 Rewards 自动助手：本地自动搜索、每日活动、自动签到、阅读任务。多区域兼容与卡片过滤逻辑优化；**业务日期按 PT 计算**（修正与接口 UTC 日期差 8 小时、取不到当天任务的问题），每日任务批次按业务日挑选、缺失时回退到最近一批且不并入历史、不取未来批次；**浮动图标可拖拽（不记忆位置，刷新回右下角）、边缘自动避让、双击标题栏复位**，展开后标题栏也可拖，内容超长时面板内部滚动。<br>显示名：`Get Microsoft Rewards (修复/增强版)` |
| [`telegram-media-downloader-merged.user.js`](browser/telegram-media-downloader-merged.user.js) | 1.2.2 | 从限制下载的 Telegram 频道获取图片 / 视频 / 语音 / 文件。**旧 `telegram-only` 与 `Telegram-Media-Downloader-Pro` 的合并版**：WebA(webz) + WebK(webk) 双端挂载、原生 `downloadToDisc` 通道优先；**默认不弹目录选择框，文件直接落系统下载目录**（可选指定归档目录）、任务面板（标题栏可拖动、位置记忆、双击复位、取消 / 重试 / 续传 / 4 MB 分片）、右键菜单下载、选择栏批量下载（面板显示 `n/N` 条目进度与当前文件名、可取消）、整组下载（相册 + 评论区）、受保护消息 `.no-forwards` 文字复制解锁、五语 i18n。<br>仓库侧加固：`@match` 严格限定 + 运行时域名守卫，无任何推广 / 邀请 / 统计外链。Telegram 下载脚本**只装这一个**。<br>显示名：`Telegram Media Downloader (Merged)` |

### 常见问题

**地址打不开 / 返回 404？**
确认文件名与大小写完全一致 —— raw 地址是**区分大小写**的。直接复制上面的地址，不要手打。

**装完不自动更新？**
在脚本管理器里打开该脚本 → 设置 → 检查更新。若仍无反应，说明当前安装版本的 `@updateURL` 指向失效地址（比如仍指向旧的 `scripts/browser/`），重新访问上方安装地址覆盖安装一次即可恢复正常。

**媒体查看器里出现两个下载按钮 / 面板重复？**
说明同时启用了多个 Telegram 下载脚本（仓库内的 `merged`、从别处装的 `Pro`，或你此前装的旧版 `telegram-only`）。在脚本管理器里只保留 `merged`，其余停用或删除并刷新页面。旧版 `telegram-only` 已从仓库移除，其更新地址会返回 404，建议直接卸载。

**我改了 `@name`，为什么老用户变成两个脚本？**
`@name` + `@namespace` 共同构成 Tampermonkey 的脚本身份。发布后改动这两项会被视为新脚本，老用户既收不到更新、又会重复安装。

**升级后遇到 404、或冒出重复按钮 / 面板？**
先看下一节的**变更记录** —— 历史上几次改动都要求老用户手动重装或卸载一次。

### 变更记录

只收录**会影响已安装用户**的变更：下线、改名、目录迁移、脚本身份变化。
普通 `@version` 递增不在其中，版本演进看各脚本 header 或提交历史。

| 日期 | 变更 | 对已装用户的影响 |
| --- | --- | --- |
| 2026-11 | **脚本目录由 `scripts/browser/` 移到仓库根目录 `browser/`**，`@updateURL` / `@downloadURL` 改为 `raw.githubusercontent.com/lytree/script/main/browser/...`（v1.2.2） | 旧地址 `main/scripts/browser/...` 现已 404，**自动更新会静默断掉** → 用上方地址重装一次即可恢复 |
| 2026-11 | **发布源从 `lytree/GMScript` 迁到本仓库**：脚本身份（`@name` / `@namespace`）未动，但 `@updateURL` / `@downloadURL` 改指本仓库 raw 地址（v1.2.1） | 老用户本机副本仍指向 GMScript → **用上方地址重装一次**才会接上新的更新链路；GMScript 侧 `userscripts/` 已冻结，不再推送新版 |
| 2026-11 | 下线 `wps-auto-signin.user.js`（WPS Auto Sign-in 自动签到 v1.0.0），连同 `userscripts/tests/` 下两份测试 | raw 地址现已 404，**不再收到自动更新**；需在脚本管理器里手动停用 / 卸载。仓库不再维护该功能 |
| 2026-10 | 脚本从仓库根目录移入 `userscripts/`，raw 地址随之改变（脚本内 `@updateURL` / `@downloadURL` 已同步） | 本机已装副本仍指向旧地址 → **重新安装一次**即可恢复自动更新；根目录旧地址已失效 |
| 2026-10 | 删除 `telegram-media-downloader.telegram-only.user.js`（v1.0.7），功能并入 `telegram-media-downloader-merged.user.js`（v1.2.0） | 两者 `@name` / `@namespace` 不同，管理器视为两个脚本 → **必须先卸载旧版**再装合并版，否则媒体查看器出现两个下载按钮；旧地址已 404，不会自动更新 |

### 命名与更新约定

1. **文件名全用 ASCII 英文，kebab-case** —— 不含中文、空格、括号。这样 URL 无需百分号编码，地址短且不会因编码写错而 404。
2. **文件名不带版本号**，版本号只由 header 里的 `@version` 承载 —— 这样 raw 地址恒定，用户无需重装。
3. **`@downloadURL` 与 `@updateURL` 指向同一个 `.user.js`**（不使用独立的 `.meta.js`），维护成本最低。
4. **`@name` + `@namespace` 一旦发布不再改动**，理由见上方常见问题。
5. **脚本显示名可以保留中文**（如 `@name:zh-CN`），它与文件名各自独立，互不影响。
6. **移动 / 重命名文件必须同步改 `@updateURL` 与 `@downloadURL`**，并预先告知用户重新安装 —— 否则自动更新链路会静默断掉。这类变更同时登记到上方[「变更记录」](#变更记录)。

### 发布流程

1. 修改 `browser/*.user.js`，递增 `@version`。
2. `git add -A && git commit -m "..." && git push`（或直接在 GitHub 网页上传覆盖同名文件）。
3. 若本次改动会**影响已安装用户**（下线、改名、移动文件、改 `@namespace`），在「变更记录」补一行 —— 写清用户**要做什么**，而不只是改了哪些代码。
4. **推送后校验 raw 地址返回 200**，确认更新链路通顺：

   ```bash
   curl -sI https://raw.githubusercontent.com/lytree/script/main/browser/get-microsoft-rewards.user.js | head -1
   ```

> 注意：重命名或移动文件时，GitHub 上必须同步操作，且脚本内的 `@updateURL` / `@downloadURL` 要一并更新，
> 否则 raw 地址会 404、自动更新失效。

### 本地测试

当前仓库内的脚本均不附带测试；若后续为某个脚本补上测试，统一放在 `browser/tests/`，用纯 Node 运行，
被测脚本按 `import.meta.url` 相对定位，clone 到任意目录都能跑。

## 关联脚本（上游，非本仓库维护）

以下脚本来自第三方仓库，**不在本仓库内、也不由本仓库发版**。这里只做索引，方便装机时一次装齐；
版本、更新与问题反馈均以上游为准，请遵守各自许可证。

| 脚本 | 来源 / 许可 | 当前版本 | 功能 | 安装 | 备用直链 |
| --- | --- | --- | --- | --- | --- |
| Github 增强 · 高速下载 | [XIU2/UserScript](https://github.com/XIU2/UserScript)<br>GPL-3.0 | 2.6.41 | 高速下载 Git Clone/SSH、Release、Raw、Code(ZIP)（公益加速源），项目列表单文件快捷下载 | [GreasyFork 412245](https://greasyfork.org/zh-CN/scripts/412245) | [`GithubEnhanced-High-Speed-Download.user.js`](https://raw.githubusercontent.com/XIU2/UserScript/master/GithubEnhanced-High-Speed-Download.user.js) |
| 知乎美化 | [XIU2/UserScript](https://github.com/XIU2/UserScript)<br>GPL-3.0 | 1.5.24 | 宽屏显示、暗黑模式（4 种，可跟随浏览器）、屏蔽首页活动广告、隐藏文章开头大图、调整图片最大高度、下翻自动隐藏顶栏 | [GreasyFork 412212](https://greasyfork.org/zh-CN/scripts/412212) | [`Zhihu-Beautification.user.js`](https://raw.githubusercontent.com/XIU2/UserScript/master/Zhihu-Beautification.user.js) |
| 知乎增强 | [XIU2/UserScript](https://github.com/XIU2/UserScript)<br>GPL-3.0 | 2.3.32 | 移除登录弹窗、屏蔽指定类别 / 低赞低评 / 用户 / 关键词、默认收起回答、快捷收起与回到顶部、默认高清原图、站外直链等 | [GreasyFork 419081](https://greasyfork.org/zh-CN/scripts/419081) | [`Zhihu-Enhanced.user.js`](https://raw.githubusercontent.com/XIU2/UserScript/master/Zhihu-Enhanced.user.js) |
| 抖音下载 | [zhzLuke96/douyin-dl-user-js](https://github.com/zhzLuke96/douyin-dl-user-js)<br>MIT | 1.5.15 | 无水印视频 / 图集 / 评论区图片 / 表情包 / 封面下载，视频帧截图，弹幕下载（ASS），下载历史与自定义文件名，作者主页批量下载（断点续传、并发、筛选、后台继续），可接 abdm / aria2 / IDM / BitComet 外部下载器 | [Release 直链](https://github.com/zhzLuke96/douyin-dl-user-js/releases/latest/download/dy-dl.user.js) | [GreasyFork 522326](https://greasyfork.org/zh-CN/scripts/522326) |
| 更好的 X（BetterX） | [Iskongkongyo/BetterX](https://github.com/Iskongkongyo/BetterX)<br>MIT | 4.0.0<br><sub>源码已到 4.1.0，未发版</sub> | X（Twitter）增强：时间线帖子本地记录、全文搜索、收藏 / 置顶 / 备注、闪现帖标记；黄推与广告净化、媒体批量下载（ZIP、GIF）；通知订阅管理、宽屏与主页增强、四语界面 | [GreasyFork 588748](https://greasyfork.org/zh-CN/scripts/588748) | [Release v4.0.0](https://github.com/Iskongkongyo/BetterX/releases/download/v4.0.0/BetterX-v4.0.0.js) |
| 网盘直链下载助手（FerryLink） | [Wjavan/FerryLink](https://github.com/Wjavan/FerryLink)<br>GPL-3.0 | 1.6.5 | 百度 / 阿里 / 天翼 / 迅雷 / 夸克 / 移动 / 城通网盘直链解析与批量取链（含百度分享页），推送到 IDM / Aria2 / Motrix / BitComet / XDown，或复制 cURL、aria2c 命令；多链并发、主题与深色模式 | [GreasyFork 595544](https://greasyfork.org/zh-CN/scripts/595544) | [`网盘直链下载助手.user.js`](https://raw.githubusercontent.com/Wjavan/FerryLink/main/%E7%BD%91%E7%9B%98%E7%9B%B4%E9%93%BE%E4%B8%8B%E8%BD%BD%E5%8A%A9%E6%89%8B.user.js) |

<sub>版本号核对于 2026-11-14。XIU2 三份 GitHub raw 直链实测返回 200；抖音下载取最新 Release tag `v1.5.15`（2026-09-09 发布，仓库此后仍有提交但未发新版）；BetterX 取最新 Release `v4.0.0`（2026-10-24），但仓库 README 徽章与构建产物名已标到 4.1.0，尚无对应 tag；FerryLink 版本取自脚本 header（与最新 Release tag `v1.6.5` 一致，2026-11-13 发布），raw 直链实测 200 且与仓库内容一致。</sub>

<sub>**本地镜像**：以上 6 个脚本各存了一份原样副本在 [`third-party/`](third-party/)，每个副本都记了来源仓库、ref、**对应的上游 commit**、sha256 与同步时间（见 [`third-party/SOURCES.md`](third-party/SOURCES.md)），后期核对「镜像落后多少」有据可查。同步工具：`node third-party/sync.mjs`。镜像只用于阅读与复核 —— **不要从镜像安装，也不要把它当 `@updateURL`**，安装用上表地址。</sub>

**通用须知**

- **更新走上游**：升级由 GreasyFork / GitHub Release 推送。**不要把它们的 `@updateURL` / `@downloadURL` 改成本仓库地址** —— 本仓库不跟上游同步，改了等于静默断更。
- **与本仓库脚本无冲突**：各自有独立 `@namespace` 与域名，可与上方 #1、#2 共存，安装顺序无所谓。
- **同一脚本只装一份**：GreasyFork 版与 GitHub 版并存会出现重复按钮 / 重复面板。

**XIU2 系（Github 增强 / 知乎美化 / 知乎增强）**

- **知乎两个脚本是分层的**：美化只管样式（宽屏 / 暗黑 / 图片高度），增强只管功能（弹窗 / 屏蔽 / 快捷操作），**建议同装**，功能不重叠。
- **备用源**：GitHub raw 被墙时可用 Bitbucket 镜像 `https://bitbucket.org/xiu2/userscript/raw/master/<文件名>`，两份内容一致（已验证字节数相同）。
- **一个坑**：知乎增强 header 里的 `@namespace` 是 `https://greasyfork.org/scripts/4122051`（多一位，上游笔误）。它只作为脚本身份标识，不影响功能；但**不要**去「修正」它 —— 改了等于换身份，老用户的配置和更新链路都会断。

**抖音下载（zhzLuke96）**

- **装 Release 那份**：仓库根目录没有可直接安装的 `.user.js`，源码是 TypeScript 多模块 + esbuild 构建，安装产物 `dy-dl.user.js`（约 270 KB）只随 Release 发布，`@updateURL` 指向 `releases/latest/download/`，发新 tag 即自动升级。`@namespace` 是 `https://github.com/zhzLuke96/douyin-dl-user-js`（镜像内核对）。
- **不要和 GreasyFork 版同装**：`dy-dl.user.js` 是同一个脚本的两份分发，且 Release 版随 tag 走、GreasyFork 版由作者单独同步，版本可能不一致。同装时按 `@namespace` 判定 —— 相同则互相覆盖（旧版配置被顶掉），不同则是两个脚本同时生效（悬浮按钮重叠）。**只留一份，优先 Release。**
- **网络**：Release 资源走 `objects.githubusercontent.com`，部分网络环境下直连超时（本机实测 000）；打不开就退回 GreasyFork 522326 或走代理。
- **能力边界**：批量下载不支持打包 zip，图集会逐项下载；图片转码 / 压缩仅在浏览器下载时生效，走外部下载器时不处理。

**更好的 X（BetterX）**

- **没有稳定直链**：Release 资源名带版本号（`BetterX-v4.0.0.js`），不存在 `releases/latest/download/<固定名>` 这种地址，所以表里给的是 v4.0.0 的**固定**资产地址。日常安装走 GreasyFork / OpenUserJS。产物头部的 `@downloadURL` 也指向 `update.greasyfork.org/scripts/588748/…`（镜像内核对），所以即便装了 Release 资产，后续升级同样由 GreasyFork 发版。
- **源码不是单文件**：`src/*.part.js` 分片由 `scripts/build.mjs` 拼装，产物名形如 `更好的X（BetterX）v4.1.0.js`，仓库根目录没有可直接安装的文件。改代码需 `npm run check`（构建 + 约 30 个 node 测试），这点和本仓库「单文件 + 可选测试」的约定不同。
- **上游 Release 命名不规范**：`v3.8.0` 里同时挂了 `BetterX-v3.7.0.js` 和 `BetterX-v3.8.0.js`，`v3.6.0` 挂的是 `.rar` 压缩包。手动下载时**认准资产名里的版本号**，别把旧版当新版装。
- **运行时域名**：`x.com` / `m.x.com` / `twitter.com`，与本仓库现有脚本无交集；记录只存浏览器本地，不上传。
- **管理器选择**：安卓 Firefox + Violentmonkey 取视频真实地址可能受登录态限制，抓视频建议换 Tampermonkey。

**网盘直链下载助手（FerryLink）**

- **文件名是中文，正好是本仓库约定的反面教材**：仓库根目录唯一脚本叫 `网盘直链下载助手.user.js`，raw 地址必须百分号编码才能访问（直接用中文字符的 URL 在本机实测返回 000）。这也解释了本仓库为什么强制 ASCII kebab-case。
- **更新走 GreasyFork**：header 里 `@updateURL` / `@downloadURL` 都指向 `update.greasyfork.org/scripts/595544/...`，即使从仓库 raw 装的，升级也由 GreasyFork 分发。**Release 里没有 js 资产**（v1.6.1 – v1.6.5 每版都只挂 changelog），别去 Release 找文件。
- **1.6.2 → 1.6.5 的变化面**：新增 `https://pan.baidu.com/s/*` 匹配（百度**分享页**也能解析直链）、夸克直链补齐文件名、深色模式弹窗补底、切页后「下载助手」按钮不再消失；RPC 推送不再下发非法保存路径，文件名按 Windows 保留字符与 ext4/APFS 255 字节上限清洗（路径过长 / 含 `:*?"<>|` 而推送失败的问题由此解决）。`@require` 仍是那三个依赖，`@connect *` 也仍在，权限面没变。
- **三个 `@require` 外部依赖**：jQuery 3.7.0 / sweetalert2 10.16.6 / js-md5 0.7.3，全部来自 `registry.npmmirror.com`。该源不通或被拦时脚本直接起不来（页面无反应，控制台会有 require 失败）。
- **权限面偏大**：为适配任意下载器与直链域名，声明了 `@connect *`、`GM_cookie`、`GM_xmlhttpRequest`。功能上说得通，但意味着脚本能向任意域发请求、能读 Cookie，装前自己掂量。
- **是二次开发，不是原创**：header 描述写明「基于油小猴(youxiaohou.com)的网盘直链下载助手修改」，GPL-3.0。
- **能力边界**：只解析直链，**不突破限速**，上游自己建议配超级会员；百度超过 50 MB 的文件用浏览器直下会被 CDN 拒绝，得走下载器。
- **备用分发**：ScriptCat 脚本页 8398（本机实测 200）；GreasyFork 页在本机网络下超时，不代表站点不可用。

## Windows 环境脚本（install-wsl-kali/windows/）

Windows 环境配置脚本 + winget 安装配置，**全部为命令行工具，不影响上面的油猴脚本**。

```powershell
# 一键配置全部系统级环境变量（自动提权）
.\install-wsl-kali\windows\set-system-env-pwsh7.ps1

# 给图片关联 IrfanView
.\install-wsl-kali\windows\irfanview-assoc.ps1

# 给视频关联 PotPlayer
.\install-wsl-kali\windows\potplayer-assoc.ps1

# 安装 pwsh7 / Git / LFS / gsudo、FFmpeg → 走 winget configure（声明式、幂等）
winget configure -f .\install-wsl-kali\configurations\devtools.winget --accept-configuration-agreements --disable-interactivity
winget configure -f .\install-wsl-kali\configurations\ffmpeg.winget  --accept-configuration-agreements --disable-interactivity
```

涵盖：系统级环境变量（uv / Python / Java / Maven / Node / vcpkg）、IrfanView 图片关联、PotPlayer 视频关联；
下载安装类（PowerShell 7 / Git / Git LFS / gsudo / FFmpeg）已迁移到 [`install-wsl-kali/configurations/`](install-wsl-kali/configurations/) 的 `*.winget` 声明式配置。

**约定**：脚本必须 pwsh 7（5.1 会失败）；下载安装统一走 `winget configure`，不再提供 `.bat` / `.cmd` 入口。

📖 详见 **[`install-wsl-kali/windows/README.md`](install-wsl-kali/windows/README.md)** —— 含完整脚本清单、参数说明、本机现状与踩坑记录。

## Office 文档脚本（src/office/）

用 [OfficeIMO](https://github.com/EvotecIT/OfficeIMO) 在命令行里生成 / 读取 Word、Excel、PowerPoint，**不需要装 Office、不走 COM**。

```powershell
. .\run.ps1
run word.cs  --pdf                                   # 生成 Word 报告（标题/列表/表格/图表/页眉页脚）
run excel.cs --pdf                                   # 生成 Excel 报表（多表/公式/图表/冻结窗格）
run ppt.cs   --pdf                                   # 生成 4 页 PPT（封面/要点/表格/图表/备注）
run read.cs  output/officeimo/word.docx --mode markdown # 读文档 → 文本 / Markdown / 结构信息
```

| 脚本 | 说明 |
| --- | --- |
| [`src/office/word.cs`](src/office/word.cs) | Word 报告，含内嵌图表、页眉页脚与页码、文档属性、可选 PDF |
| [`src/office/excel.cs`](src/office/excel.cs) | Excel 报表，含样式、数字格式、跨表公式、自动筛选、柱状图、打印设置 |
| [`src/office/ppt.cs`](src/office/ppt.cs) | PowerPoint 汇报，含项目符号、表格、原生图表、演讲者备注 |
| [`src/office/read.cs`](src/office/read.cs) | 读 `.docx/.xlsx/.pptx`，输出 text / markdown / info，可转 PDF |

三个生成脚本默认用内置示例数据，也可 `--data sales.csv` 换自己的数据（表头 `Region,Revenue,Growth`）。

📖 详见 **[`src/office/README.md`](src/office/README.md)** —— 选项、CSV 约定、依赖版本与踩坑记录。

## 第三方镜像（third-party/）

[`third-party/`](third-party/) 存的是「关联脚本」表里 6 个上游脚本的**原样副本**，只用于阅读与复核，不是安装源；升级走各自上游，同步与校准流程见 [`third-party/README.md`](third-party/README.md)。

## 开发约定

1. 新脚本**优先用 dotnet file-based app**（单文件 `.cs` + `#:` 指令，直接 `dotnet run`）。
2. 确实不适合 .NET 时再考虑其他方案；Python 脚本统一用 `uv` 管理执行与依赖。
3. 每个工具保持单文件；模块边界清晰且超过约 500 行时才拆目录。
4. CLI 支持 `--help`，成功返回 `0`、一般错误 `1`、参数错误 `2`；错误信息走 stderr。
5. 路径用 `Path.Combine`，不硬编码盘符。

## 许可

本仓库主体未单独声明整体许可证。`browser/`、`install-wsl-kali/windows/` 下的自维护脚本沿用 [`scripts/LICENSE`](scripts/LICENSE)（MIT）；`third-party/` 下的镜像归各自上游作者所有，许可证见对应文件 header 与 [`third-party/README.md`](third-party/README.md)。各脚本另有自身许可证声明（见脚本 header 的 `@license`）。
