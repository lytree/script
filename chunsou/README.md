# chunsou（春蒐）· C# 移植版

> **代码来源：本项目移植自 [https://github.com/Funsiooo/chunsou](https://github.com/Funsiooo/chunsou)**
>
> 原项目作者：**Funsiooo** ｜ 原项目语言：Python 3 ｜ 原项目许可：GPL-3.0
> 原项目版权声明保留于 [LICENSE](./LICENSE)（GNU GPL v3）
>
> 本移植版基于原项目 commit `ab720c0f` 移植，遵循本仓库 `AGENTS.md` 规范改写为 .NET 10 File-Based App。
> 版权与许可归属原作者，本移植版同样以 GPL-3.0 发布。

多线程 Web 指纹识别工具，适用于安全测试人员前期的资产识别、风险收敛以及企业互联网资产摸查。

指纹规则约 **11129 条**（来自 EHole、dismap 等开源项目及原作者自收集），辅助能力包括子域名爆破、FOFA / Hunter 资产收集、AI 语义分析。

---

## 移植说明

功能与原项目对齐，但不是逐行翻译 —— 有三处实现被替换：

| 能力 | 原项目（Python） | 本移植版（C#） |
|---|---|---|
| 指纹匹配 | 逐条遍历 11129 条规则做 `substring` | **Aho-Corasick 自动机**，一次扫描匹配全部 13094 个关键词 |
| favicon 哈希 | `python-mmh3` | 手写 **MurmurHash3 x86_32**（`Modules/Murmur3.cs`，无 .NET 对应包） |
| 技术栈识别 | `python-Wappalyzer`（需下载数据集） | 内置轻量规则表（响应头 / Cookie / meta / 脚本引用） |
| 子域名爆破 | shell out 调用 OneForAll（需 Python 环境） | **原生 DNS 并发爆破**，零外部依赖 |
| HTML 解析 | BeautifulSoup | `HtmlAgilityPack` + 正则 |
| 命令行 | `argparse` | **`System.CommandLine` 2.0**（自动帮助、POSIX 组合、取值校验） |
| Excel 输出 | `openpyxl` | **`MiniExcel`**（流式写出，见下方实测） |
| 进度显示 | `tqdm` | `Spectre.Console` Progress |

**匹配语义与原项目逐条等价**，已用 70 组随机抽样用例交叉验证（body AND / title AND / header OR / icon_hash AND，均按原规则顺序取首个命中）。

性能：200KB 页面单页匹配约 **10ms**，规则库加载约 **300ms**。

### 为什么选 MiniExcel 而不是 ClosedXML

批量资产导出动辄几十万行，实测（Release，200000 行 × 5 列）：

| | 耗时 | 内存分配 | 文件大小 |
|---|---|---|---|
| **MiniExcel** | **1004 ms** | 710 MB | 7946 KB |
| ClosedXML | 4812 ms | 1152 MB | 4847 KB |

**快 4.8 倍，内存少 38%**。官方 benchmark 在千万行量级下差距更悬殊（15 MB vs 7141 MB 峰值内存）—— ClosedXML 全量加载 DOM，MiniExcel 走 ZipArchive + XmlReader 流式写出。指纹扫描本身已是多线程高占用，能少占一份内存是实打实的收益。

代价：MiniExcel 不支持单元格级样式（边框等），只支持表头级样式（底色、对齐）。对本工具够用 —— 输出是给人看的数据，不是给 Excel 宏二次加工的。

> 实现上用带 `[ExcelColumn(Name = "...")]` 的强类型行模型，而非 `object[]`。MiniExcel 会把 `object[]` 当成单个带索引器的对象，无法序列化。表头与列序由属性上的特性决定。

---

## 目录结构

```
chunsou/
├── chunsou.cs                  入口（顶层语句 + #include）
├── Modules/
│   ├── Ansi.cs                 终端配色
│   ├── CliOptions.cs           命令行选项定义（System.CommandLine）
│   ├── Config.cs               config.ini 读取 + 环境变量覆盖
│   ├── Murmur3.cs              MurmurHash3 x86_32
│   ├── AhoCorasick.cs          多模式串自动机
│   ├── FingerRules.cs          指纹规则库与四维匹配
│   ├── TechStack.cs            技术栈识别
│   ├── PageParser.cs           编码检测 / title / 正文 / favicon / DOM
│   ├── Scanner.cs              扫描核心
│   ├── AiAnalyzer.cs           AI 语义分析（GPT / DeepSeek）
│   ├── ResultWriter.cs         txt / xlsx 输出
│   ├── AssetApis.cs            FOFA / Hunter 资产收集
│   ├── SubdomainScanner.cs     子域名 DNS 爆破
│   └── SearchTips.cs           空间测绘语法速查表
├── finger.json                 指纹库（11129 条，移植自原项目）
├── wordlist.txt                子域名字典（移植自原项目 OneForAll）
├── config.example.ini          配置模板（复制为 config.ini 后填入凭据）
├── config.ini                  实际配置（自动生成，已被 .gitignore 忽略）
├── LICENSE                     GNU GPL v3（继承自原项目）
└── results/                    输出目录（运行时生成）
```

---

## 运行

```bash
dotnet run chunsou.cs -- --help
```

Linux / macOS 可直接执行（文件首行已是 shebang）：

```bash
chmod +x chunsou.cs
./chunsou.cs --help
```

> **资源定位**：`finger.json` / `config.ini` / `wordlist.txt` 按「当前工作目录 → 程序集目录 → 上级目录」顺序查找。
> 因此请在 `chunsou` 目录下运行，或把资源复制到可执行文件旁边。

---

## 命令行

由 [System.CommandLine](https://github.com/dotnet/command-line-api) 2.0 驱动，帮助信息自动生成：

```
用法:
  chunsou [options]

选项:
  -u, --url <url>               scan for a single url
  -f, --file <file>             specify a file for multi scanning
  -du, --domain <domain>        subdomain blasting of a single domain name
  -df, --domains <domains>      subburst the domain name in the specified file
  -fo, --fofa <fofa>            call the fofa api for asset collection
  -hu, --hunter <hunter>        call the hunter api for asset collection
  --ai <auto|force>             enable semantic analysis（裸用等同 auto）
  --ai-provider <gpt|deepseek>  specify ai provider
  --ai-model <ai-model>         specify ai model
  -p, --proxy <proxy>           proxy scan traffic: http / https / socks5
  -t, --threads <threads>       number of scanning threads [default: 50]
  -o, --output <output>         specified output file (.txt / .xlsx)
  -e, --error                   show the specific error cause of failed targets
  --verbose                     verbose logging
  --tip                         spatial mapping search syntax reference
  -?, -h, --help                Show help and usage information
  --version                     显示版本信息
```

除常规写法外，还支持：

- **POSIX 短选项组合** —— `-et10` 等价于 `-e -t 10`
- **`--opt=value` 与 `--opt value`** 两种赋值形式
- **取值校验** —— `--ai xxx`、`--ai-provider bad`、`-t 99999` 会被拦截并给出错误提示

```bash
# 下面几条等价
dotnet run chunsou.cs -- -e -t 100 -f urls.txt
dotnet run chunsou.cs -- -et100 -f urls.txt
dotnet run chunsou.cs -- --error --threads=100 --file urls.txt
```

> `--ai` 刻意不设默认值：设了默认值后裸用 `--ai` 会贪婪吞掉后一个选项（实测会把 `--ai-provider deepseek` 的值吃掉）。
> 只有 `Arity=ZeroOrOne` 且无默认值时，才能区分「未指定」与「显式 auto」。

---

## 用法

### 目标

```bash
# 单目标
dotnet run chunsou.cs -- -u 'http://example.com'

# 多目标（每行一个 URL；支持 CIDR 网段与 IP 范围）
dotnet run chunsou.cs -- -f urls.txt -t 100

# 目标文件支持以下写法
#   http://example.com        直接使用
#   example.com               自动补 http:// 与 https://
#   192.168.1.0/24            展开网段（上限 65536 个地址）
#   10.0.0.1-10.0.0.100       展开 IP 范围
#   # 注释                    跳过
```

### AI 语义分析

```bash
# auto：本地规则未命中时交给 AI 判断（默认）
dotnet run chunsou.cs -- -u http://example.com --ai

# force：跳过本地判断，强制让 AI 给出页面语义分析
dotnet run chunsou.cs -- -u http://example.com --ai force

# 指定厂商与模型
dotnet run chunsou.cs -- -f urls.txt --ai force --ai-provider deepseek
dotnet run chunsou.cs -- -u http://example.com --ai --ai-provider gpt --ai-model gpt-4o
```

> `--ai auto` 模式下若本地已识别出指纹，不会调用 AI，省 token。结果带 SHA256 缓存（`results/ai_cache.json`），相同页面只请求一次。

### 子域名爆破

```bash
dotnet run chunsou.cs -- -du example.com
dotnet run chunsou.cs -- -df domains.txt
```

自动检测 DNS 泛解析（若随机子域名能解析会给出告警）。结果同时输出 `http://` 与 `https://` 两种 URL，可直接拿去 `-f` 扫描。

### 资产收集

```bash
dotnet run chunsou.cs -- -fo 'domain="example.com"'
dotnet run chunsou.cs -- -hu 'domain="example.com"'
```

### 其他

```bash
# 语法速查表
dotnet run chunsou.cs -- -tip

# 代理（http / https / socks5）
dotnet run chunsou.cs -- -f urls.txt -p http://127.0.0.1:7890

# 输出格式（txt / xlsx）
dotnet run chunsou.cs -- -f urls.txt -o results.xlsx

# 显示失败原因
dotnet run chunsou.cs -- -f urls.txt -e

# 打印规则库统计
dotnet run chunsou.cs -- -u http://example.com --verbose
```

---

## 配置

首次运行会自动从 `config.example.ini` 复制出 `config.ini`，填入凭据即可。`config.ini` 已被 `.gitignore` 忽略，不会误提交密钥。

更推荐用**环境变量**，无需改文件：

| 环境变量 | 对应配置项 |
|---|---|
| `FOFA_EMAIL` | `[fofa_email] email` |
| `FOFA_KEY` | `[fofa_api_key] key` |
| `HUNTER_KEY` | `[hunter_api_key] key` |
| `GPT_API_KEY` / `OPENAI_API_KEY` | `[gpt_api] api_key` |
| `DEEPSEEK_API_KEY` | `[deepseek_api] api_key` |

---

## 输出格式

**txt**

```
[+] [200] http://example.com | 指纹 | 网站标题 | 技术栈
[-] [0] http://bad.example.com [连接超时]
```

**xlsx**：带表头样式与边框，首行冻结。AI force 模式下表头为「网页URL / AI分析结果」。

输出顺序固定为：`指纹 | 网站标题 | 技术栈`。

---

## 退出码

| 码 | 含义 |
|---|---|
| 0 | 成功 |
| 1 | 一般错误（网络异常、输出格式不支持等） |
| 2 | 参数错误（未知选项、缺值、非法线程数、无目标） |

---

## 指纹规则

`finger.json` 共 11129 条，移植自原项目 `modules/config/finger.json`。原作者标注的规则来源为 [Ehole](https://github.com/EdgeSecurityTeam/EHole)、[dismap](https://github.com/zhzyker/dismap) 及自收集。

```json
{
  "cms": "亿赛通电子文档安全管理系统",
  "method": "keyword",
  "location": "body",
  "keyword": ["电子文档安全管理系统", "CDGServer3"]
}
```

| method | location | 匹配语义 |
|---|---|---|
| `keyword` | `body` | 全部关键词出现在正文（AND） |
| `keyword` | `title` | 全部关键词出现在标题（AND） |
| `keyword` | `header` | 任一关键词出现在响应头（OR） |
| `icon_hash` | `body` | 全部关键词匹配 favicon 的 mmh3 哈希 |

更新规则库直接替换 `finger.json` 即可，无需改动代码。

---

## 交叉验证

指纹匹配语义与原项目等价，已验证：

```
PASS  body   => 29网课交单平台
PASS  title  => ClusterControl
PASS  header => Letta
PASS  icon   => 飞牛云 fnOS
PASS  none   => NULL
7/7 passed

70/70 passed   （从 finger.json 随机抽样的交叉比对）
```

MurmurHash3 与标准 mmh3 向量一致：

```
mmh3(b'')      = 0
mmh3(b'hello') = 613153351
mmh3(b'a')     = 1009084850
```

---

## 许可与致谢

- **原项目**：[Funsiooo/chunsou](https://github.com/Funsiooo/chunsou) —— GPL-3.0，作者 Funsiooo
- **本移植版**：沿用 GPL-3.0，完整许可证见 [LICENSE](./LICENSE)
- **指纹规则来源**：[EdgeSecurityTeam/EHole](https://github.com/EdgeSecurityTeam/EHole)、[zhzyker/dismap](https://github.com/zhzyker/dismap)
- **子域名字典来源**：原项目内置的 [OneForAll](https://github.com/OneForAll) `subnames.txt`
- **移植贡献**：lytree

本工具仅用于授权测试与资产梳理，请遵守当地法律法规。
