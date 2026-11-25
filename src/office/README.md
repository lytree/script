# Office 文档脚本（src/office/）

用 [OfficeIMO](https://github.com/EvotecIT/OfficeIMO) 在命令行里生成 / 读取 Word、Excel、PowerPoint 的四个 File-Based App 示例。**不需要安装 Office，也不走 COM**，Windows / macOS / Linux 行为一致。

统一走仓库入口：

```powershell
. .\run.ps1
run word.cs  --pdf       # Tab 补全可用；run 只按完整文件名匹配，别漏掉 .cs
run excel.cs --pdf
run ppt.cs   --pdf
run read.cs  output/officeimo/word.docx --mode markdown
```

等价写法：`dotnet run src/office/word.cs -- --pdf`。

## 四个脚本

| 脚本 | 干什么 | 覆盖的 OfficeIMO 操作 |
| --- | --- | --- |
| [`word.cs`](word.cs) | 生成区域销售报告 `.docx` | `WordDocument.Create` / `AddHeading` / `AddParagraph` / `AddList` / `AddTable` / `AddChart` / `AddHorizontalLine` / 页眉页脚 + `AddPageNumber` / 文档属性 / `SaveAsPdf` |
| [`excel.cs`](excel.cs) | 生成两表报表 `.xlsx` | `ExcelDocument.Create` / `AddSheet` / `Cell().SetValue().SetBold().SetFillColor()` / `SetNumberFormat` / `SetFormula` / `Range().SetBorder().AutoFilter()` / `View.Freeze` / `AddChart` / `Merge` / `Calculate` / `AutoFitColumns` / 打印页眉 / `SaveAsPdf` |
| [`ppt.cs`](ppt.cs) | 生成 4 页汇报 `.pptx` | `PowerPointDocument.Create` / `AddSlide` / `AddTitle` / `AddTextBox().AddBullets()` / `AddTable` + 单元格着色 / `AddChart` / `Notes` / `SlideSize.GetContentBox` / `SaveAsPdf` |
| [`read.cs`](read.cs) | 读 `.docx/.xlsx/.pptx` → 文本 / Markdown / 结构信息，可转 PDF | `WordDocument.Load` / `ExcelDocument.Load` / `PowerPointDocument.Load` / `doc.Elements` 顺序遍历 / `GetFormattedText` / `SaveAsPdf` |

`Modules/DemoData.cs` 是前三者共用的数据源（`#:include` 引入），不要单独运行。

## 数据来源

默认用内置的 5 行示例；也可以喂自己的 CSV（`--data`）：

```csv
Region,Revenue,Growth
华东,182400,0.12
华南,141900,8%
"西北区",320000,-0.12
```

- 表头必须是 `Region,Revenue,Growth`（大小写不敏感）
- `Revenue` 允许千分位、`¥` / `￥`
- `Growth` 支持 `0.12` 或 `12%` 两种写法
- 支持双引号包裹与 `""` 转义

## 通用选项

| 选项 | 说明 |
| --- | --- |
| `--out <file>` | 输出路径，默认当前目录 `officeimo-word.docx` / `-excel.xlsx` / `-ppt.pptx` |
| `--data <file>` | CSV 数据源，缺省用内置示例 |
| `--title <text>` | 报告 / 封面标题 |
| `--theme <name>` | `modern`（默认）/ `classic` / `minimal` / `boardroom` / `dark`，见 `OfficeTheme` |
| `--pdf` | 额外导出同名 PDF |
| `--help` / `-h` | 帮助 |
| `--version` | 版本号 |

`read.cs` 另有 `--mode text|markdown|info`、`--sheet <名称>`（可重复）、`--out`。

退出码统一：`0` 成功 / `1` 运行错误 / `2` 参数错误（错误信息走 stderr）。

## 依赖

四个脚本的 `#:` 指令都锁在 **3.6.2**。OfficeIMO 的设计要求同一应用内所有包版本一致，升级时四个文件一起改：

```
#:package OfficeIMO.Word@3.6.2          # Word 引擎
#:package OfficeIMO.Word.Pdf@3.6.2      # 转 PDF 的适配包，只有用到 --pdf 才需要
```

首次运行会从 NuGet 还原（几十 MB），之后走缓存。

## 生成结果

`output/officeimo/` 下是本机实测产物（已被 `.gitignore` 忽略）：

| 文件 | 实测 |
| --- | --- |
| `word.docx` / `word.pdf` | 17 段落 / 1 表格 / 1 图表，PDF 2 页，页眉 + 页脚页码正常 |
| `excel.xlsx` / `excel.pdf` | 2 工作表，6 个公式预计算，PDF 2 页，图表与汇总都在 |
| `ppt.pptx` / `ppt.pdf` | 4 页 960×540，表格 5 行 4 列、图表、备注齐全，PDF 4 页 |
| `read.cs` | 三个文件都能正确读回（中文、表格、备注无乱码） |

## 踩过的坑

- **公式单元格先 `Calculate()` 再 `AutoFitColumns()`**。否则公式还没有缓存值，列宽按空内容算，导出的 PDF 里会显示成 `##`。
- **`AddPageNumber` 会新起段落**。页脚文字和页码要对齐到不同侧，就分别设置，别指望同一个 `WordParagraph` 里两段文字各管一边。
- **取单元格文本用 `GetFormattedText(provider)`**，直接取 `Value` 拿到的是底层对象，日期和百分比不会按格式渲染。
- **`doc.Elements` 才是文档顺序**。分别遍历 `Paragraphs` 和 `Tables` 会让所有表格堆到文末，Markdown 结构就错了。
- **`OfficeTheme` 只在新建时生效**，加载已有文档不会重排样式；`dark` 主题导出 PDF 时注意正文对比度。
- **别把 `--out` 指向源文件**：`read.cs --pdf` 会把 PDF 写到源文件同目录的同名 `.pdf`，同名时直接覆盖。
