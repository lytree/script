#!/usr/bin/env dotnet run

#:package EPPlus@*
#:package Microsoft.Data.SqlClient@*

using OfficeOpenXml;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Text;
using System.Text.RegularExpressions;

// ============================================================
// 将 Excel 文件导入 (localdb)\MSSQLLocalDB
// 用法:
//   dotnet run ImportExcelToLocalDb.cs -- --preview          # 预览结构(不导入)
//   dotnet run ImportExcelToLocalDb.cs --                     # 导入全部 sheet
//   dotnet run ImportExcelToLocalDb.cs -- --sheet 记录        # 只导入指定 sheet
//   dotnet run ImportExcelToLocalDb.cs -- --drop              # 重建已存在的表
//   dotnet run ImportExcelToLocalDb.cs -- --db RecordDB       # 指定数据库名
// ============================================================

const string DefaultFile = @"C:\Users\hiyan\OneDrive\文档\记录.xlsx";
const string DefaultDb = "RecordDB";
const string ConnPrefix = "Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;";

var filePath = DefaultFile;
var dbName = DefaultDb;
var previewOnly = false;
var dropTables = false;
string? sheetFilter = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--file" when i + 1 < args.Length: filePath = args[++i]; break;
        case "--db" when i + 1 < args.Length: dbName = args[++i]; break;
        case "--sheet" when i + 1 < args.Length: sheetFilter = args[++i]; break;
        case "--preview": previewOnly = true; break;
        case "--drop": dropTables = true; break;
        case "--help" or "-h":
            Console.WriteLine("""
                导入 Excel 到 (localdb)\MSSQLLocalDB
                  --file <path>   Excel 文件路径 (默认: 记录.xlsx)
                  --db <name>     目标数据库名 (默认: RecordDB)
                  --sheet <name>  只处理指定 sheet
                  --preview       仅预览结构, 不导入
                  --drop          重建已存在的表
                """);
            return 0;
        default:
            Console.Error.WriteLine($"未知参数: {args[i]}");
            return 2;
    }
}

try
{
    ExcelPackage.License.SetNonCommercialPersonal("Personal Use");

    using var package = new ExcelPackage(new FileInfo(filePath));
    var sheets = package.Workbook.Worksheets
        .Where(ws => ws.Dimension is not null)
        .Where(ws => sheetFilter is null || string.Equals(ws.Name, sheetFilter, StringComparison.OrdinalIgnoreCase))
        .ToList();

    if (sheets.Count == 0)
    {
        Console.Error.WriteLine($"未找到工作表{(sheetFilter is null ? "" : $": {sheetFilter}")}");
        return 2;
    }

    if (previewOnly)
    {
        Console.WriteLine($"文件: {filePath}");
        Console.WriteLine($"数据库: {dbName} (预览模式, 不写入)");
        Console.WriteLine(new string('-', 70));
    }

    foreach (var ws in sheets)
    {
        var tableName = Sanitize(ws.Name);
        var headers = ReadHeaders(ws);
        var rows = ReadRows(ws, headers.Count);
        var columns = InferColumns(headers, rows);

        Console.WriteLine($"[{(previewOnly ? "预览" : "导入")}] Sheet: {ws.Name} -> 表: {tableName} | 行数: {rows.Count} | 列数: {columns.Count}");
        if (previewOnly)
        {
            foreach (var c in columns)
                Console.WriteLine($"    - {c.Name}  {c.SqlType}  (示例: {c.Sample})");
            continue;
        }

        EnsureDatabase(dbName);
        CreateTable(dbName, tableName, columns, dropTables);
        BulkInsert(dbName, tableName, columns, rows);
        Console.WriteLine($"    已写入 {rows.Count} 行 -> [{dbName}].[dbo].[{tableName}]");
    }

    Console.WriteLine(new string('-', 70));
    Console.WriteLine($"完成。{(previewOnly ? "预览结束, 未做任何写入。" : $"数据已存入 (localdb)\\MSSQLLocalDB 的 {dbName} 库。")}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

// ---------------- Excel 读取 ----------------

List<string> ReadHeaders(ExcelWorksheet ws)
{
    var end = ws.Dimension.End;
    var headers = new List<string>(end.Column);
    for (var col = 1; col <= end.Column; col++)
    {
        var v = ws.Cells[1, col].Value;
        var name = v?.ToString()?.Trim() ?? $"列{col}";
        headers.Add(name);
    }
    return headers;
}

List<List<object?>> ReadRows(ExcelWorksheet ws, int colCount)
{
    var end = ws.Dimension.End;
    var rows = new List<List<object?>>();
    for (var r = 2; r <= end.Row; r++)
    {
        var allEmpty = true;
        var row = new List<object?>(colCount);
        for (var c = 1; c <= colCount; c++)
        {
            var v = ws.Cells[r, c].Value;
            if (v is not null)
                allEmpty = false;
            row.Add(v);
        }
        if (!allEmpty)
            rows.Add(row);
    }
    return rows;
}

// ---------------- 类型推断 ----------------

// 列信息: (列名, .NET类型, SQL类型, 示例值)
List<(string Name, Type DataType, string SqlType, string Sample)> InferColumns(List<string> headers, List<List<object?>> rows)
{
    var result = new List<(string, Type, string, string)>();
    var used = new HashSet<string>();

    for (var c = 0; c < headers.Count; c++)
    {
        var baseName = Sanitize(headers[c]);
        var name = baseName;
        var n = 2;
        while (!used.Add(name))
            name = $"{baseName}_{n++}";

        var dataType = InferType(rows, c);
        var sqlType = SqlTypeFor(dataType);
        var sample = rows.FirstOrDefault(r => r[c] is not null)?[c]?.ToString() ?? "(全部为空)";
        if (sample.Length > 40) sample = sample[..40] + "...";
        result.Add((name, dataType, sqlType, sample));
    }
    return result;
}

Type InferType(List<List<object?>> rows, int col)
{
    Type? kind = null;
    foreach (var row in rows)
    {
        var v = row[col];
        if (v is null) continue;

        if (kind is null)
            kind = Classify(v);
        else if (kind != typeof(string))
        {
            var c = Classify(v);
            if (c != kind)
                kind = typeof(string); // 类型冲突 -> 整列降级为字符串
        }
    }
    return kind ?? typeof(string);
}

Type Classify(object v)
{
    if (v is bool) return typeof(bool);
    if (v is DateTime) return typeof(DateTime);
    if (v is byte or short or int) return typeof(int);
    if (v is long) return typeof(long);
    if (v is float f2) return f2 % 1 == 0 ? typeof(long) : typeof(double);
    if (v is double dd) return dd % 1 == 0 ? typeof(long) : typeof(double);
    if (v is decimal dm) return dm % 1 == 0 ? typeof(long) : typeof(double);

    var s = v.ToString();
    if (DateTime.TryParse(s, out _)) return typeof(DateTime);
    if (long.TryParse(s, out _)) return typeof(long);
    if (double.TryParse(s, out _)) return typeof(double);
    return typeof(string);
}

string SqlTypeFor(Type t)
{
    if (t == typeof(int)) return "INT";
    if (t == typeof(long)) return "BIGINT";
    if (t == typeof(double)) return "FLOAT";
    if (t == typeof(DateTime)) return "DATETIME2";
    if (t == typeof(bool)) return "BIT";
    return "NVARCHAR(MAX)";
}

// ---------------- 名称清洗 ----------------

string Sanitize(string raw)
{
    var s = Regex.Replace(raw ?? "", "[^0-9a-zA-Z_\\u4e00-\\u9fff]", "_");
    if (s.Length == 0) return "Col";
    if (char.IsDigit(s[0])) s = "C_" + s;
    if (s.Length > 100) s = s[..100];
    return s;
}

// ---------------- SQL Server ----------------

string Conn(string db) => ConnPrefix + $"Initial Catalog={db};";

void EnsureDatabase(string db)
{
    using var conn = new SqlConnection(Conn("master"));
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = $"IF DB_ID(N'{db}') IS NULL CREATE DATABASE [{db}]";
    cmd.ExecuteNonQuery();
}

void CreateTable(string db, string table, List<(string Name, Type DataType, string SqlType, string Sample)> columns, bool drop)
{
    using var conn = new SqlConnection(Conn(db));
    conn.Open();
    if (drop)
    {
        using var dropCmd = conn.CreateCommand();
        dropCmd.CommandText = $"IF OBJECT_ID(N'[{table}]', N'U') IS NOT NULL DROP TABLE [{table}]";
        dropCmd.ExecuteNonQuery();
    }

    var sb = new StringBuilder();
    sb.AppendLine($"IF OBJECT_ID(N'[{table}]', N'U') IS NULL");
    sb.AppendLine("BEGIN");
    sb.AppendLine($"    CREATE TABLE [{table}] (");
    sb.AppendLine("        [Id] INT IDENTITY(1,1) PRIMARY KEY,");
    foreach (var c in columns)
        sb.AppendLine($"        [{c.Name}] {c.SqlType} NULL,");
    sb.Length -= 2; // 去掉末尾逗号换行
    sb.AppendLine();
    sb.AppendLine("    );");
    sb.AppendLine("END");

    using var cmd = conn.CreateCommand();
    cmd.CommandText = sb.ToString();
    cmd.ExecuteNonQuery();
}

void BulkInsert(string db, string table, List<(string Name, Type DataType, string SqlType, string Sample)> columns, List<List<object?>> rows)
{
    using var dt = new DataTable();
    foreach (var c in columns)
        dt.Columns.Add(c.Name, c.DataType);

    foreach (var row in rows)
    {
        var dr = dt.NewRow();
        for (var i = 0; i < columns.Count; i++)
        {
            var v = ConvertValue(columns[i].DataType, row[i]);
            dr[columns[i].Name] = v ?? DBNull.Value;
        }
        dt.Rows.Add(dr);
    }

    using var conn = new SqlConnection(Conn(db));
    conn.Open();
    using var bulk = new SqlBulkCopy(conn)
    {
        DestinationTableName = $"[dbo].[{table}]",
        BatchSize = 1000,
        BulkCopyTimeout = 120,
    };
    foreach (var c in columns)
        bulk.ColumnMappings.Add(c.Name, c.Name);
    bulk.WriteToServer(dt);
}

object? ConvertValue(Type dataType, object? v)
{
    if (v is null) return null;
    try
    {
        if (dataType == typeof(DateTime)) return v is DateTime dt ? dt : Convert.ToDateTime(v);
        if (dataType == typeof(bool)) return Convert.ToBoolean(v);
        if (dataType == typeof(int)) return Convert.ToInt32(v);
        if (dataType == typeof(long)) return Convert.ToInt64(v);
        if (dataType == typeof(double)) return Convert.ToDouble(v);
        return v.ToString();
    }
    catch
    {
        return v.ToString();
    }
}
