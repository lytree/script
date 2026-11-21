# third-party —— 上游脚本的本地只读镜像

[`scripts/README.md`](../README.md) 的「关联脚本（上游，非本仓库维护）」一节列出的第三方脚本，在这里各存一份**原样副本**。

存在的原因很简单：上游随时可能删仓库、改名、被墙，而 README 里那些说明（版本号、`@namespace`、`@updateURL` 指向哪里）需要有据可查。有了副本，这些判断可以随时本地复核，而不是靠记忆。

## 这不是安装源

- **不要**从本目录安装脚本，也不要把它当 `@updateURL` 用。安装地址见`scripts/README.md`，升级由上游推送。
- **不要**在这里改代码。镜像一旦本地修改，下次 `sync` 就会被覆盖，而且你会在"这脚本到底是谁的行为"上彻底失去判断。
- 本目录**不随本仓库的 MIT 许可证发布**，各文件仍归各自作者，许可证见下表与文件内 header。

## 清单

| 本地文件 | 脚本 | 上游仓库 | 版本 | 许可证 |
| --- | --- | --- | --- | --- |
| [`github-enhanced.user.js`](github-enhanced.user.js) | Github 增强 · 高速下载 | [XIU2/UserScript](https://github.com/XIU2/UserScript) | 2.6.41 | GPL-3.0 |
| [`zhihu-beautification.user.js`](zhihu-beautification.user.js) | 知乎美化 | [XIU2/UserScript](https://github.com/XIU2/UserScript) | 1.5.24 | GPL-3.0 |
| [`zhihu-enhanced.user.js`](zhihu-enhanced.user.js) | 知乎增强 | [XIU2/UserScript](https://github.com/XIU2/UserScript) | 2.3.32 | GPL-3.0 |
| [`ferrylink.user.js`](ferrylink.user.js) | 网盘直链下载助手（FerryLink） | [Wjavan/FerryLink](https://github.com/Wjavan/FerryLink) | 1.6.1 | GPL-3.0 |
| [`douyin-dl.user.js`](douyin-dl.user.js) | 抖音下载 | [zhzLuke96/douyin-dl-user-js](https://github.com/zhzLuke96/douyin-dl-user-js) | 1.5.15 | MIT |
| [`betterx.user.js`](betterx.user.js) | 更好的 X（BetterX） | [Iskongkongyo/BetterX](https://github.com/Iskongkongyo/BetterX) | 4.0.0 | MIT |

完整来源（原始文件名、下载地址、备用镜像、大小、sha256）在 [`manifest.json`](manifest.json)。本地文件名统一用 ASCII，是因为上游有的名字带中文（`网盘直链下载助手.user.js`）或带版本号（`BetterX-v4.0.0.js`），直接沿用会让每次升级都多一个文件。

## 同步

```bash
# 下载并更新镜像 + manifest（sha256 / 大小 / 日期会一并刷新）
node scripts/third-party/sync.mjs

# 只比对，不写盘；有差异退出码为 1，适合放进 CI 做版本巡检
node scripts/third-party/sync.mjs --check

# 只同步指定的几个
node scripts/third-party/sync.mjs --only=betterx --only=douyin-dl
```

纯 Node 18+，无第三方依赖。下载顺序是 `url` → `alternates`，网络错误会重试一次（HTTP 4xx 是确定性失败，不重试）：Release 资产的真实域名是 `objects.githubusercontent.com`，部分网络直连不通，所以清单里给了 `gh-proxy.com` 镜像；raw 偶尔抽风时退回 Bitbucket / jsDelivr。镜像可能比主干慢一拍，所以**判断版本一律以 sha256 与 `@version` 为准，不以镜像的 lag 为准**。

**信任模型**：首次同步时记下的 `sha256` 是后续比对的锚点。上游正常发版时哈希必然变化，`--check` 会如实报「有更新」；如果版本号没动而哈希变了，那值得停下来看一眼 —— 那意味着对方重写了同版本的文件。

## 来源记录

`@version` 只说明「第几版」，说明不了「对应上游哪一次改动」。所以每个条目除了版本，还记了 commit：

[`SOURCES.md`](SOURCES.md)（每次同步自动重写，别手工改）给出人能读的对照表：

```
node scripts/third-party/sync.mjs --report    # 同一张表打到 stdout
```

`manifest.json` 里每条记录的 `source` 字段是机器可读的那份：

| 字段 | 含义 |
| --- | --- |
| `refType` / `ref` | 取自分支（`master` / `main`，会移动）还是某个 Release tag（不会变） |
| `commit` | 该文件在上游当前对应的提交 sha —— 后期 diff、bisect、看「某行为哪来的」全靠它 |
| `committedAt` | 该提交的日期 |
| `resolvedUrl` | **实际**命中的下载地址（可能是备用镜像），出问题时先看这里 |
| `fetchedAt` | 该文件内容最后一次与上游对齐的时间 |

## 后期更新的标准动作

1. `node scripts/third-party/sync.mjs --check` —— 看谁动了。版本号变了会显示 `旧 → 新`；文件没变但上游有新提交会标「上游有新提交，本文件未变」，那可忽略。
2. `node scripts/third-party/sync.mjs` —— 真正拉取，`manifest.json` 与 `SOURCES.md` 一起刷新。
3. 回头校准`scripts/README.md`：版本号、以及那些「取决于实现」的判断（`@namespace` 是啥、`@updateURL` 指向哪、有没有外部依赖、权限面多大）。这些会随上游重写而失效，镜像就是用来复核它们的。

## 更新镜像的节奏

不追版本。只在两种情况跑一次：

1. `scripts/README.md` 的关联脚本表需要更新时（顺手把版本号和判断依据一起校准）；
2. 想复核某个脚本的实际行为时（比如确认它到底往哪儿发请求、`@updateURL` 指向哪）。
