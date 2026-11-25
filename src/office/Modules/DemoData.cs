// ============================================================================
// Modules/DemoData.cs — src/office 下 word / excel / ppt 三个示例共用的数据源
//
// 由 word.cs / excel.cs / ppt.cs 通过 #:include 复用，勿单独运行。
// 数据模型：区域销售额（区域 / 营收 / 同比），内置一份示例，也可用 --data 指定 CSV。
// ============================================================================

using System.Globalization;

/// <summary>一行销售数据：区域名、营收（元）、同比增速（0.12 表示 +12%）。</summary>
public sealed record SalesRow(string Region, decimal Revenue, double Growth);

/// <summary>三个 Office 示例共用的数据加载器。</summary>
public static class DemoData
{
    /// <summary>内置示例数据，不传 --data 时使用。</summary>
    public static List<SalesRow> Sample() =>
    [
        new("华东", 182_400m, 0.12),
        new("华南", 141_900m, 0.08),
        new("华北", 128_600m, 0.05),
        new("西南", 97_300m, -0.03),
        new("东北", 76_800m, 0.21),
    ];

    /// <summary>
    /// 加载数据：csvPath 为 null 时返回内置示例，否则读 CSV。
    /// CSV 约定：首行表头 <c>Region,Revenue,Growth</c>（大小写不敏感），逗号分隔，允许带引号。
    /// </summary>
    public static List<SalesRow> Load(string? csvPath)
    {
        if (string.IsNullOrWhiteSpace(csvPath)) return Sample();
        if (!File.Exists(csvPath)) throw new FileNotFoundException($"数据文件不存在: {csvPath}", csvPath);

        var lines = File.ReadAllLines(csvPath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        if (lines.Count < 2) throw new InvalidDataException("CSV 至少需要表头 + 1 行数据");

        var header = SplitCsvLine(lines[0]);
        int iRegion = IndexOf(header, "Region"), iRevenue = IndexOf(header, "Revenue"), iGrowth = IndexOf(header, "Growth");
        if (iRegion < 0 || iRevenue < 0 || iGrowth < 0)
            throw new InvalidDataException("CSV 表头需包含 Region / Revenue / Growth 三列");

        var rows = new List<SalesRow>();
        for (int i = 1; i < lines.Count; i++)
        {
            var cells = SplitCsvLine(lines[i]);
            if (cells.Count <= Math.Max(iRegion, Math.Max(iRevenue, iGrowth))) continue;

            var region = cells[iRegion].Trim();
            if (region.Length == 0) continue;

            rows.Add(new SalesRow(
                region,
                ParseDecimal(cells[iRevenue]),
                ParsePercent(cells[iGrowth])));
        }

        if (rows.Count == 0) throw new InvalidDataException("CSV 中没有可用数据行");
        return rows;
    }

    static int IndexOf(List<string> header, string name) =>
        header.FindIndex(h => h.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));

    static decimal ParseDecimal(string raw)
    {
        var text = raw.Trim().Replace("¥", "").Replace("￥", "").Replace(",", "");
        return decimal.Parse(text, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.InvariantCulture);
    }

    static double ParsePercent(string raw)
    {
        var text = raw.Trim().Replace("%", "");
        double v = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        return raw.Contains('%') ? v / 100 : v;
    }

    /// <summary>极简 CSV 行拆分：支持双引号包裹与 "" 转义，逗号分隔。</summary>
    static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool quoted = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }

        cells.Add(sb.ToString());
        return cells;
    }
}
