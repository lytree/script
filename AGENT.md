# AGENT.md

> 面向 AI Agent 的仓库操作指南。通用规范见同目录 `AGENTS.md`，本文只讲**这个仓库具体是什么样、怎么跑、怎么改**。
> 两份文件冲突时，以本文的仓库事实为准；风格/编码规范仍以 `AGENTS.md` 为准。

---

## 1. 这是什么

`lytree/script` —— 个人自用的 **.NET 10 File-Based App 脚本仓库**。没有 `.sln`，没有传统 `.csproj`（仅 `Directory.Build.props` 提供公共属性），每个工具是**一个独立的 `.cs` 文件**，靠 `#:` 指令声明依赖后直接 `dotnet run` 执行。

技术底座：

| 项 | 值 |
|---|---|
| SDK | .NET 10（本机实测 `10.0.401`） |
| 包管理 | NuGet，源见 `NuGet.config` |
| CLI 框架 | `System.CommandLine` 2.0 / `Spectre.Console` |
| 日志 | `YLFramework.ZLogging`（`AddZLoggerSpectreConsoleAndFile`） |
| 入口脚本 | `run.ps1`（PowerShell 7+，带 Tab 补全） |

`NuGet.config` 里有一个**本机私有源** `F:\Code\Github\framework\nugets`（配合 `<clear />` 使用）。换机器时这个路径不存在会还原失败——改仓库前先确认，或临时注释掉该行。

---

## 2. 目录地图

```
script/
├── AGENTS.md              # 通用规范（风格/CLI/日志/跨平台）
├── AGENT.md               # 本文件：仓库事实 + 操作指引
├── run.ps1                # 统一入口：run <脚本名> [参数...]，支持 Tab 补全
├── search_videos_btsou.ps1
├── env.cs                 # 路径扩展：Path.EntryPointDataPath() 等（tdl 系列广泛复用）
├── Directory.Build.props  # 开启 Include/Transitive 指令；屏蔽一批 nullable 警告
├── NuGet.config
├── .agents/skills/dotnet-file-based-apps/   # 本地 skill，写 file-based app 前先读
│
├── chunsou/               # 独立子项目：Web 指纹识别（GPL-3.0，移植自 Funsiooo/chunsou）
│   ├── chunsou.cs         #   唯一入口，顶部 #:include 14 个 Modules/*.cs
│   ├── Modules/           #   Scanner / AhoCorasick / Murmur3 / FingerRules / ...
│   ├── config.example.ini #   配置模板（真实配置不入库）
│   ├── finger.json        #   指纹规则库（2.4 MB）
│   ├── wordlist.txt       #   子域名爆破字典
│   └── README.md          #   详细的移植说明与性能数据，改动前先读
│
├── src/                   # 按功能分目录的散装脚本
│   ├── tdl/               #   TDLib Telegram 工具（20+ 文件，#:include 复用 TdlEnv/TdlUpdateHandler）
│   ├── search/btsou.cs    #   BTSOU_Plus 磁链搜索
│   ├── data/              #   Excel 导入、去重、innodb 分页
│   ├── files/             #   批量重命名 / 移动 / 图片修复
│   ├── video/             #   MOV→MP4 转码、视频字符检测
│   ├── crawler/           #   Playwright / 慕课网爬虫
│   ├── pdf/ReadPdf.cs
│   ├── html/Playwright.cs
│   ├── Http/              #   api.cs / Json.cs
│   ├── Screen/screen.cs
│   ├── AI/1.cs
│   ├── Avalonia/          #   GUI 实验（含 .fsx）
│   └── Helper/            #   Bytes / DateTime / Images / Json / Plot，被大量 #:include
│
├── data/                  # 运行时数据（1220 jpg / 361 mp4），不是代码
├── models/                # onnx / mnn / pth 模型文件
├── lib/                   # 共享绘图代码 + 字体
└── output/                # 日志与导出产物（csv / txt / log）
```

**注意**：`data/`、`models/`、`output/`、`lib/` 是数据与产物目录，**改动代码时不要动它们，也不要把新的大文件塞进去**。

---

## 3. 怎么跑

统一走 `run.ps1`，它会自动切到仓库根目录再执行：

```powershell
# 交互式加载（推荐，Tab 补全生效）
. .\run.ps1
run btsou list              # basename 模糊匹配 → src/search/btsou.cs
run src/search/btsou.cs --help

# 直接调用
.\run.ps1 btsou --pages 3
```

Tab 补全行为：第 1 个位置参数补全仓库内所有 `.cs`/`.ps1`；选定脚本后按空格补全会列出该脚本源码里出现的所有 `--xxx` 选项；`--p<Tab>` 按前缀过滤。

不用 `run.ps1` 时：

```bash
dotnet run src/search/btsou.cs -- list      # 必须带 -- 分隔符
dotnet chunsou/chunsou.cs -- --help
```

跨平台直接执行（文件首行 shebang 必需）：

```bash
chmod +x src/search/btsou.cs
./src/search/btsou.cs -- list
```

---

## 4. 新增一个工具

放对目录 → 写文件头 → 跑起来。最小模板：

```csharp
#!/usr/bin/env dotnet run
#:package System.CommandLine@*
#:package Spectre.Console@*

// ============================================================================
// <名称>.cs — 一句话说明
//
// 用法:
//   dotnet run src/<目录>/<名称>.cs -- <子命令> [选项]
//
// 示例:
//   dotnet run src/<目录>/<名称>.cs -- list
// ============================================================================

using System.CommandLine;
using Spectre.Console;

var root = new RootCommand("一句话说明");
root.Parse(args).Invoke();
```

**硬性要求**：

1. `#:` 指令必须在文件顶部、所有 C# 代码之前。
2. 首行 shebang `#!/usr/bin/env dotnet run`。
3. 文件头注释块包含：功能、用法、示例。
4. CLI 必须支持 `--help`；失败用非零退出码（`0` 成功 / `1` 一般错误 / `2` 参数错误）。
5. 错误信息走 `Console.Error`，正常输出走 stdout。
6. 跨平台：路径一律 `Path.Combine`，禁止硬编码盘符、禁止 `cmd.exe` 专属逻辑。

---

## 5. 复用已有代码（别重复造）

仓库里已有大量可复用资产，加新功能前先查：

| 需求 | 已有实现 |
|---|---|
| 路径解析 | `env.cs` —— `Path.EntryPointDataPath()` / `EntryPointOutputPath()` / `EntryPointModelsPath()` … |
| 字节 / 时间 / 图片 / JSON / 绘图 | `src/Helper/` —— 直接 `#:include ../Helper/*.cs` |
| 路径补齐类扩展 | `src/data/` —— `#:include ../Data/*.cs`（注意大小写，Linux 区分） |
| TDLib 会话、NDJSON 接收、下载进度 | `src/tdl/TdlEnv.cs`、`TdlUpdateHandler.cs`、`TdlDownloadTracker.cs` |
| 命令行参数定义 | 参考 `chunsou/Modules/CliOptions.cs`（System.CommandLine 集中声明模式） |
| 终端表格 / 进度条 | `Spectre.Console`（`Table`、`Progress`、`FigletText`） |

`#:include` 的路径是**相对于当前文件**的，写的时候按引用方位置算。同一个文件被 include 多次会报重复定义，共享代码要保证「可重复 include」。

---

## 6. 提交与目录卫生

历史提交遵循 Conventional Commits：

```
feat(chunsou): 移植 chunsou 指纹识别工具为 C# File-Based App
refactor(tdlib): 适配新版 TDLib API 变更
feat(env): add multiple standard directory path methods
```

Agent 提交时照此格式写。`.gitignore` 已覆盖 `output/`、`data/` 产物与 `bin`/`obj`，**不要手动 `git add` 大文件或运行产物**。

---

## 7. Agent 禁区

- ❌ 不生成 `.sln`、不新建传统 `.csproj`（除非用户明确要求）
- ❌ 不引入 ASP.NET Host、重 DI 框架、企业分层架构
- ❌ 不把一个几十行的脚本拆成多个文件（超过 ~500 行、模块边界清晰时才拆，如 `chunsou/Modules/`）
- ❌ 不修改 `data/`、`models/`、`lib/` 下的资源文件
- ❌ 不提交 `config.ini` 等含真实凭据的文件（只维护 `*.example.ini`）
- ❌ 不擅自 push 或改远端；提交前先跑一次 `--help` 确认能启动
- ❌ 移植/引用第三方代码时保留原 LICENSE 与版权声明（参考 `chunsou/` 的做法）