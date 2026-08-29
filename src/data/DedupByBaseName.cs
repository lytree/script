#!/usr/bin/env dotnet run

// ============================================================
// 读取 (localdb)\MSSQLLocalDB, 按唯一键去重
// 默认 dry-run: 只统计/预览, 不修改数据。
// 必须加 --delete 才会真正删除重复行。
//
// 用法:
//   dotnet run DedupByBaseName.cs -- --dry-run            # 预览(默认)
//   dotnet run DedupByBaseName.cs --                       # 同上, 仅预览
//   dotnet run DedupByBaseName.cs -- --delete             # 真正删除重复行
//
//   dotnet run DedupByBaseName.cs -- --db RecordDB --table AV --key BaseName
//   dotnet run DedupByBaseName.cs -- --keep latest        # EnrichedAt 最新 (默认)
//   dotnet run DedupByBaseName.cs -- --keep first         # Id 最小(最早导入)
//   dotnet run DedupByBaseName.cs -- --keep last          # Id 最大(最晚导入)
//   dotnet run DedupByBaseName.cs -- --top 20             # 预览重复组时最多列出条数
// ============================================================

#:package Microsoft.Data.SqlClient@*

using Microsoft.Data.SqlClient;
using System.Data;

const string ConnPrefix = "Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;";

string db = "RecordDB";
string table = "AV";
string key = "BaseName";
string keep = "latest";          // latest | first | last
bool dryRun = true;
int top = 20;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--db" when i + 1 < args.Length: db = args[++i]; break;
        case "--table" when i + 1 < args.Length: table = args[++i]; break;
        case "--key" when i + 1 < args.Length: key = args[++i]; break;
        case "--keep" when i + 1 < args.Length: keep = args[++i].ToLowerInvariant(); break;
        case "--top" when i + 1 < args.Length: int.TryParse(args[++i], out top); break;
        case "--delete": dryRun = false; break;
        case "--dry-run": dryRun = true; break;
        case "--help" or "-h":
            Console.WriteLine("""
                读取 (localdb)\MSSQLLocalDB, 按唯一键去重
                  --db <name>     数据库名     (默认 RecordDB)
                  --table <name>  表名         (默认 AV)
                  --key <col>     唯一键列名   (默认 BaseName)
                  --keep <mode>   保留规则: latest(EnrichedAt最新,默认) | first(Id最小) | last(Id最大)
                  --delete        真正删除重复行 (默认仅预览)
                  --top <n>       预览重复组最多列出条数 (默认 20)
                """);
            return 0;
        default:
            Console.Error.WriteLine($"未知参数: {args[i]}");
            return 2;
    }
}

if (keep is not ("latest" or "first" or "last"))
{
    Console.Error.WriteLine($"--keep 仅支持 latest|first|last, 收到: {keep}");
    return 2;
}

// 唯一键排序表达式 (rn=1 为保留行)
string OrderByExpr = keep switch
{
    "first" => $"ORDER BY [{key}] ASC, [Id] ASC",
    "last"  => $"ORDER BY [{key}] ASC, [Id] DESC",
    _       => $"ORDER BY [{key}] ASC, " +
               $"CASE WHEN [EnrichedAt] IS NULL THEN 0 ELSE 1 END DESC, " +
               $"[EnrichedAt] DESC, [Id] DESC"
};

try
{
    var connStr = ConnPrefix + $"Initial Catalog={db};";
    EnsureColumn(connStr, table, key);
    if (keep == "latest") EnsureColumn(connStr, table, "EnrichedAt");

    // 统计
    int total, distinctKey, nullRows, dupGroups, removable;
    List<(string Key, int Cnt)> groups;
    using (var conn = new SqlConnection(connStr))
    {
        conn.Open();
        total = ScalarInt(conn, $"SELECT COUNT(*) FROM [{table}]");
        distinctKey = ScalarInt(conn, $"SELECT COUNT(DISTINCT [{key}]) FROM [{table}] WHERE [{key}] IS NOT NULL AND [{key}] <> ''");
        nullRows = ScalarInt(conn, $"SELECT COUNT(*) FROM [{table}] WHERE [{key}] IS NULL OR [{key}] = ''");
        groups = QueryGroups(conn, table, key, top, out dupGroups, out removable);
    }

    Console.WriteLine($"数据库: {db}  表: [{table}]  唯一键: [{key}]  保留规则: {keep}");
    Console.WriteLine($"总行数            : {total}");
    Console.WriteLine($"NULL/空 {key}     : {nullRows}  (不参与去重)");
    Console.WriteLine($"不同 {key} 数      : {distinctKey}");
    Console.WriteLine($"重复组数          : {dupGroups}");
    Console.WriteLine($"可删除的重复行    : {removable}");
    if (groups.Count > 0)
    {
        Console.WriteLine($"重复示例 (前 {Math.Min(top, groups.Count)}):");
        foreach (var g in groups)
            Console.WriteLine($"    {g.Key}  x{g.Cnt}");
    }

    if (dryRun)
    {
        Console.WriteLine("\n[预览模式] 未做任何修改。加 --delete 才会真正删除。");
        return 0;
    }

    // 真正删除
    Console.WriteLine("\n⚠️ 即将删除重复行 (每个 BaseName 仅保留 1 条)...");
    int deleted;
    using (var conn = new SqlConnection(connStr))
    {
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH cte AS (
                SELECT *,
                    ROW_NUMBER() OVER (
                        PARTITION BY [{key}]
                        {OrderByExpr}
                    ) AS rn
                FROM [{table}]
                WHERE [{key}] IS NOT NULL AND [{key}] <> ''
            )
            DELETE FROM cte WHERE rn > 1;
            """;
        deleted = cmd.ExecuteNonQuery();
    }
    Console.WriteLine($"已删除重复行: {deleted}");

    // 删除后复核
    using (var conn = new SqlConnection(connStr))
    {
        conn.Open();
        var after = ScalarInt(conn, $"SELECT COUNT(*) FROM [{table}]");
        var afterDistinct = ScalarInt(conn, $"SELECT COUNT(DISTINCT [{key}]) FROM [{table}] WHERE [{key}] IS NOT NULL AND [{key}] <> ''");
        Console.WriteLine($"删除后总行数: {after}  | 不同 {key} 数: {afterDistinct}");
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

// ---------------- helpers ----------------

void EnsureColumn(string connStr, string table, string col)
{
    using var conn = new SqlConnection(connStr);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[{table}]') AND name = N'{col}'";
    if (cmd.ExecuteScalar() is null)
        throw new InvalidOperationException($"表 [{table}] 中不存在列 [{col}]");
}

int ScalarInt(SqlConnection conn, string sql)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return (int)cmd.ExecuteScalar()!;
}

List<(string Key, int Cnt)> QueryGroups(SqlConnection conn, string table, string key, int topN, out int dupGroups, out int removable)
{
    var list = new List<(string, int)>();
    dupGroups = 0; removable = 0;
    using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
        SELECT [{key}], COUNT(*) AS Cnt
        FROM [{table}]
        WHERE [{key}] IS NOT NULL AND [{key}] <> ''
        GROUP BY [{key}]
        HAVING COUNT(*) > 1
        ORDER BY Cnt DESC, [{key}]
        OFFSET 0 ROWS FETCH NEXT {topN} ROWS ONLY;
        """;
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        var k = r.GetString(0);
        var c = r.GetInt32(1);
        list.Add((k, c));
    }
    r.Close(); // 关闭 reader, 避免与下面的统计查询冲突
    // 全量重复统计
    using (var cmd2 = conn.CreateCommand())
    {
        cmd2.CommandText = $"""
            SELECT COUNT(*) AS G, SUM(Cnt-1) AS Rm FROM (
                SELECT [{key}], COUNT(*) AS Cnt
                FROM [{table}]
                WHERE [{key}] IS NOT NULL AND [{key}] <> ''
                GROUP BY [{key}]
                HAVING COUNT(*) > 1
            ) t;
            """;
        using var r2 = cmd2.ExecuteReader();
        if (r2.Read())
        {
            dupGroups = r2.IsDBNull(0) ? 0 : r2.GetInt32(0);
            removable = r2.IsDBNull(1) ? 0 : r2.GetInt32(1);
        }
    }
    return list;
}
