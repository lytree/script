#!/usr/bin/env dotnet

using System.Text.Json;
using System.Globalization;

using var doc = JsonDocument.Parse(File.ReadAllText(@"F:\Code\Github\script\data\tdl\media-index\8682502640.json"));
var root = doc.RootElement;
var msgs = root.GetProperty("MediaMessages").EnumerateArray().ToList();

var albums = msgs.GroupBy(m => m.TryGetProperty("MediaAlbumId", out var a) ? a.GetInt64() : 0L).ToList();
var nonZero = albums.Where(g => g.Key != 0).ToList();
var multi = nonZero.Where(g => g.Count() > 1).ToList();
var noAlbum = albums.FirstOrDefault(g => g.Key == 0)?.Count() ?? 0;

Console.WriteLine($"聊天: {root.GetProperty("ChatTitle").GetString()} ({root.GetProperty("ChatId").GetInt64()})");
Console.WriteLine($"媒体总数: {msgs.Count}");
Console.WriteLine($"  Telegram Album 总数: {nonZero.Count}");
Console.WriteLine($"    - 多元素 album: {multi.Count} (共 {multi.Sum(g => g.Count())} 条)");
Console.WriteLine($"    - 单元素 album: {nonZero.Count(g => g.Count() == 1)}");
Console.WriteLine($"  无 album_id: {noAlbum}");

var times = msgs.Select(m => DateTime.Parse(m.GetProperty("Date").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)).OrderBy(t => t).ToList();
Console.WriteLine($"时间范围: {times[0]:yyyy-MM-dd HH:mm} ~ {times[^1]:yyyy-MM-dd HH:mm}");
var deltas = new List<double>();
for (int i = 1; i < times.Count; i++) deltas.Add((times[i] - times[i-1]).TotalMinutes);
deltas.Sort();
Console.WriteLine($"中位间隔: {deltas[deltas.Count/2]:F1} 分钟");
foreach (var t in new[] {1.0, 5.0, 10.0, 30.0, 60.0, 180.0, 1440.0})
    Console.WriteLine($"  间隔 <= {t,5:F0} 分钟: {deltas.Count(d => d <= t),4} / {deltas.Count}");

// 按类型分组统计
var byType = msgs.GroupBy(m => m.GetProperty("Type").GetString()).ToDictionary(g => g.Key, g => g.Count());
foreach (var kv in byType.OrderByDescending(kv => kv.Value))
    Console.WriteLine($"  {kv.Key}: {kv.Value}");

// 离群点（最大间隔前 10）
var deltasIdx = Enumerable.Range(1, deltas.Count).OrderByDescending(i => deltas[i-1]).Take(10).ToList();
Console.WriteLine("\n最大 10 个间隔:");
foreach (var i in deltasIdx)
    Console.WriteLine($"  {times[i-1]:yyyy-MM-dd HH:mm} -> {times[i]:yyyy-MM-dd HH:mm} = {deltas[i-1]/60.0:F1} h");
