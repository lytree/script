# script

`lytree/script` —— 个人脚本仓库。主体是 **.NET 10 File-Based App** 工具集，另外归档了从 `lytree/GMScript` 迁移来的浏览器 / Windows / 第三方脚本。

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
| `src/` | 按功能分类的 C# File-Based App 脚本（tdl / search / data / files / video / crawler …） |
| `demo/`、`lib/`、`models/`、`data/`、`output/` | 示例、共享代码、模型、运行时数据与产物 |
| `run.ps1`、`env.cs` | 统一执行入口与公共路径扩展 |
| `scripts/` | 从 GMScript 迁移来的脚本归档，见 [`scripts/README.md`](scripts/README.md) |
| `scripts/browser/` | 2 个自维护油猴脚本 |
| `scripts/windows/` | 9 个 Windows 环境配置脚本（`.ps1` / `.bat` / `.cmd`） |
| `scripts/third-party/` | 6 个上游油猴脚本的只读镜像 + 同步工具 |

`data/`、`models/`、`output/`、`lib/` 是数据与产物目录，不是代码目录，新增脚本不要塞进去。

## 迁移脚本说明

`scripts/` 下的内容原属 `lytree/GMScript`，迁入时做了分类整理：

- **油猴脚本的发布源已迁到本仓库**：v1.2.1 起 `@updateURL` / `@downloadURL` 指向 `raw.githubusercontent.com/lytree/script/main/scripts/browser/...`，安装与自动更新都用 [`scripts/README.md`](scripts/README.md) 中的地址。老用户需重装一次才能接上新链路，旧 GMScript 仓库已冻结。
- 第三方镜像不是安装源，升级走各自上游；详见 [`scripts/third-party/README.md`](scripts/third-party/README.md)。
- Windows 脚本的可执行入口见 [`scripts/windows/README.md`](scripts/windows/README.md)。

## 开发约定

1. 新脚本**优先用 dotnet file-based app**（单文件 `.cs` + `#:` 指令，直接 `dotnet run`）。
2. 确实不适合 .NET 时再考虑其他方案；Python 脚本统一用 `uv` 管理执行与依赖。
3. 每个工具保持单文件；模块边界清晰且超过约 500 行时才拆目录。
4. CLI 支持 `--help`，成功返回 `0`、一般错误 `1`、参数错误 `2`；错误信息走 stderr。
5. 路径用 `Path.Combine`，不硬编码盘符。

## 许可

本仓库主体未单独声明整体许可证。迁移进来的自维护脚本沿用 [`scripts/LICENSE`](scripts/LICENSE)（MIT）；`scripts/third-party/` 下的镜像归各自上游作者所有，许可证见对应文件 header 与 [`scripts/third-party/README.md`](scripts/third-party/README.md)。
