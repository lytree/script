#!/usr/bin/env dotnet run

#:package System.CommandLine@*

// ============================================================================
// TelegramChannelExport.cs — 导出 Telegram 频道网页预览(t.me/s/)消息为 JSON
//
// 与 src/tdl/TdlMessageExporter.cs 的区别: 走 t.me/s/ 公开预览页, 无需登录,
// 可直接拿到图片/视频等媒体资源的 CDN 直链和 #标签。
//
// 用法:
//   dotnet run src/crawler/TelegramChannelExport.cs -- --link <起始消息链接> [选项]
//
// 选项:
//   --link         起始消息链接, 从该消息一直导出到最新 (默认 https://t.me/sourpuss1988/8885)
//   --output       输出 JSON 路径 (默认 output/telegram/<频道>_<起始id>_latest.json)
//   --proxy        HTTP 代理 host:port (默认读取环境变量 tdl_proxy, 缺省 127.0.0.1:7897; 传 none 关闭)
//   --delay        翻页间隔毫秒 (默认 800)
//   --from-json    跳过抓取, 直接读取已导出的 JSON
//   --split-by-tag 按 #标签 拆分, 每个标签一个 JSON 文件 (多标签消息会出现在多个文件)
//
// 输出 JSON 头部记录 LatestMessageUrl (本次导出的最新消息链接), 便于下次从该链接增量导出。
//
// 示例:
//   dotnet run src/crawler/TelegramChannelExport.cs -- --link https://t.me/sourpuss1988/8885
//   dotnet run src/crawler/TelegramChannelExport.cs -- --from-json output/telegram/sourpuss1988_8885_latest.json --split-by-tag
// ============================================================================

using System.CommandLine;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

var optLink = new Option<string>("--link")
{
    Description = "起始消息链接 (https://t.me/<频道>/<消息id>)",
    DefaultValueFactory = _ => "https://t.me/sourpuss1988/8885"
};
var optOutput = new Option<string?>("--output") { Description = "输出 JSON 路径" };
var optProxy = new Option<string?>("--proxy") { Description = "HTTP 代理 host:port, none 表示不使用" };
var optDelay = new Option<int>("--delay") { Description = "翻页间隔(毫秒)", DefaultValueFactory = _ => 800 };
var optFromJson = new Option<string?>("--from-json") { Description = "跳过抓取, 读取已导出的 JSON" };
var optSplitByTag = new Option<bool>("--split-by-tag") { Description = "按 #标签 拆分, 每个标签一个 JSON 文件" };
var optSplitByAuthor = new Option<bool>("--split-by-author") { Description = "按作者拆分 (取第一个非通用标签作为作者)" };
var optExcludeTags = new Option<string?>("--exclude-tags") { Description = "作者拆分时排除的通用标签, 逗号分隔" };

const string DefaultExcludeTags = "cosplay,合集,推特,抖音,b站,快手,直播,微博,小红书,黑丝,白丝,丝袜,足,腿,P盘,原图,二次元";

var root = new RootCommand("导出 Telegram 频道网页预览消息为 JSON (含正文/媒体直链/标签)");
root.Options.Add(optLink);
root.Options.Add(optOutput);
root.Options.Add(optProxy);
root.Options.Add(optDelay);
root.Options.Add(optFromJson);
root.Options.Add(optSplitByTag);
root.Options.Add(optSplitByAuthor);
root.Options.Add(optExcludeTags);

root.SetAction(async (parseResult, ct) =>
{
    string link = parseResult.GetValue(optLink)!;
    string? output = parseResult.GetValue(optOutput);
    int delay = parseResult.GetValue(optDelay);
    string? fromJson = parseResult.GetValue(optFromJson);
    bool splitByTag = parseResult.GetValue(optSplitByTag);
    bool splitByAuthor = parseResult.GetValue(optSplitByAuthor);
    string excludeTags = parseResult.GetValue(optExcludeTags) ?? DefaultExcludeTags;

    var m = Regex.Match(link, @"t\.me/(?:s/)?([A-Za-z0-9_]+)/(\d+)");
    if (!m.Success)
    {
        Console.Error.WriteLine($"无法解析消息链接: {link} (期望 https://t.me/<频道>/<消息id>)");
        return 2;
    }

    string channel = m.Groups[1].Value;
    long startId = long.Parse(m.Groups[2].Value);

    output ??= Path.Combine("output", "telegram", $"{channel}_{startId}_latest.json");

    var jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    TgExport export;

    if (!string.IsNullOrWhiteSpace(fromJson))
    {
        export = JsonSerializer.Deserialize<TgExport>(await File.ReadAllTextAsync(fromJson), jsonOptions)
            ?? throw new InvalidOperationException($"无法解析 JSON: {fromJson}");
        output = fromJson;
        if (export.StartMessageId == 0 && export.Messages.Count > 0)
        {
            export.StartMessageId = export.Messages.Min(x => x.Id);
            export.EndMessageId = export.Messages.Max(x => x.Id);
            export.Count = export.Messages.Count;
        }
        export.LatestMessageUrl = export.Messages.Count > 0
            ? $"https://t.me/{export.Channel}/{export.EndMessageId}"
            : null;
        Console.WriteLine($"读取已有导出: {fromJson} ({export.Count} 条, {export.StartMessageId} → {export.EndMessageId})");
    }
    else
    {
        string? proxy = parseResult.GetValue(optProxy)
            ?? Environment.GetEnvironmentVariable("tdl_proxy");

        using var http = CreateHttpClient(proxy);

        var messages = new SortedDictionary<long, TgMessage>();

        // 从起始消息往后翻页, 直到没有新消息
        long cursor = startId - 1;
        for (int page = 1; page <= 500; page++)
        {
            string url = $"https://t.me/s/{channel}?after={cursor}";
            string? html = await FetchAsync(http, url);
            if (html == null)
            {
                Console.Error.WriteLine($"拉取失败, 停止于 after={cursor}");
                break;
            }

            var pageMessages = ParsePage(html, channel)
                .Where(x => x.Id >= startId)
                .ToList();

            int added = 0;
            foreach (var msg in pageMessages)
            {
                if (messages.TryAdd(msg.Id, msg)) added++;
            }

            Console.WriteLine($"第 {page} 页: 解析 {pageMessages.Count} 条, 新增 {added} 条 (累计 {messages.Count})");

            if (added == 0) break;

            long maxId = pageMessages.Max(x => x.Id);
            if (maxId <= cursor) break;
            cursor = maxId;

            await Task.Delay(delay, ct);
        }

        // 兜底: 确保起始消息本身在结果中
        if (!messages.ContainsKey(startId))
        {
            string? html = await FetchAsync(http, $"https://t.me/s/{channel}?before={startId + 1}");
            if (html != null)
            {
                var first = ParsePage(html, channel).FirstOrDefault(x => x.Id == startId);
                if (first != null) messages[startId] = first;
            }
        }

        if (messages.Count == 0)
        {
            Console.Error.WriteLine("未解析到任何消息, 请检查频道是否存在或代理是否可用");
            return 1;
        }

        export = new TgExport
        {
            Channel = channel,
            StartMessageId = startId,
            EndMessageId = messages.Keys.Max(),
            LatestMessageUrl = $"https://t.me/{channel}/{messages.Keys.Max()}",
            ExportedAt = DateTimeOffset.Now,
            Count = messages.Count,
            Messages = messages.Values.ToList()
        };

        string? dir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(export, jsonOptions), Encoding.UTF8);

        Console.WriteLine($"导出完成: {messages.Count} 条消息 ({startId} → {export.EndMessageId})");
        Console.WriteLine($"最新消息: {export.LatestMessageUrl}");
        Console.WriteLine($"文件已保存: {Path.GetFullPath(output)}");
    }

    if (splitByTag)
    {
        await SplitByTagAsync(export, output, jsonOptions);
    }

    if (splitByAuthor)
    {
        await SplitByAuthorAsync(export.Messages, output, excludeTags, jsonOptions);
    }

    return 0;
});

return await root.Parse(args).InvokeAsync();

// ============================================================================

static async Task SplitByTagAsync(TgExport export, string sourceJson, JsonSerializerOptions jsonOptions)
{
    string sourceDir = Path.GetDirectoryName(sourceJson) ?? "";
    string outDir = Path.Combine(sourceDir, Path.GetFileNameWithoutExtension(sourceJson) + "_by_tag");
    Directory.CreateDirectory(outDir);

    // 标签大小写不敏感合并, 保留首次出现的写法
    var groups = new Dictionary<string, List<TgMessage>>(StringComparer.OrdinalIgnoreCase);
    foreach (var msg in export.Messages)
    {
        foreach (var tag in msg.Tags ?? [])
        {
            if (!groups.TryGetValue(tag, out var list))
            {
                list = [];
                groups[tag] = list;
            }
            list.Add(msg);
        }
    }

    var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var index = new List<TgTagIndex>();

    foreach (var (tag, messages) in groups.OrderByDescending(x => x.Value.Count).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
    {
        string baseName = SafeFileName(tag.TrimStart('#'));
        if (baseName.Length == 0) baseName = "tag";

        string fileName = baseName + ".json";
        for (int i = 2; !usedNames.Add(fileName); i++)
        {
            fileName = $"{baseName}_{i}.json";
        }

        var tagExport = new TgTagExport
        {
            Tag = tag,
            Count = messages.Count,
            Messages = messages.OrderBy(x => x.Id).ToList()
        };
        await File.WriteAllTextAsync(Path.Combine(outDir, fileName), JsonSerializer.Serialize(tagExport, jsonOptions), Encoding.UTF8);
        index.Add(new TgTagIndex { Tag = tag, Count = messages.Count, File = fileName });
    }

    // 无标签消息单独一个文件
    var untagged = export.Messages.Where(x => x.Tags == null || x.Tags.Count == 0).ToList();
    if (untagged.Count > 0)
    {
        var tagExport = new TgTagExport { Tag = "(无标签)", Count = untagged.Count, Messages = untagged };
        await File.WriteAllTextAsync(Path.Combine(outDir, "_untagged.json"), JsonSerializer.Serialize(tagExport, jsonOptions), Encoding.UTF8);
        index.Add(new TgTagIndex { Tag = "(无标签)", Count = untagged.Count, File = "_untagged.json" });
    }

    await File.WriteAllTextAsync(Path.Combine(outDir, "index.json"), JsonSerializer.Serialize(index, jsonOptions), Encoding.UTF8);

    Console.WriteLine($"按标签拆分完成: {index.Count} 个文件 (含 _untagged 与 index)");
    Console.WriteLine($"输出目录: {Path.GetFullPath(outDir)}");
}

static async Task SplitByAuthorAsync(List<TgMessage> messages, string sourceJson, string excludeTags, JsonSerializerOptions jsonOptions)
{
    string sourceDir = Path.GetDirectoryName(sourceJson) ?? "";
    string outDir = Path.Combine(sourceDir, Path.GetFileNameWithoutExtension(sourceJson) + "_by_author");
    Directory.CreateDirectory(outDir);

    var exclude = excludeTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => "#" + x.TrimStart('#'))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var groups = new Dictionary<string, List<TgMessage>>(StringComparer.OrdinalIgnoreCase);
    var unknown = new List<TgMessage>();

    foreach (var msg in messages)
    {
        // 作者 = 标签里第一个非通用标签 (如 #五更百鬼 / #Bambi)
        string? author = msg.Tags?.FirstOrDefault(t => !exclude.Contains(t));
        if (author == null)
        {
            unknown.Add(msg);
            continue;
        }

        if (!groups.TryGetValue(author, out var list))
        {
            list = [];
            groups[author] = list;
        }
        list.Add(msg);
    }

    var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var index = new List<TgTagIndex>();

    foreach (var (author, list) in groups.OrderByDescending(x => x.Value.Count).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
    {
        string baseName = SafeFileName(author.TrimStart('#'));
        if (baseName.Length == 0) baseName = "author";

        string fileName = baseName + ".json";
        for (int i = 2; !usedNames.Add(fileName); i++)
        {
            fileName = $"{baseName}_{i}.json";
        }

        var authorExport = new TgTagExport
        {
            Tag = author,
            Count = list.Count,
            Messages = list.OrderBy(x => x.Id).ToList()
        };
        await File.WriteAllTextAsync(Path.Combine(outDir, fileName), JsonSerializer.Serialize(authorExport, jsonOptions), Encoding.UTF8);
        index.Add(new TgTagIndex { Tag = author, Count = list.Count, File = fileName });
    }

    if (unknown.Count > 0)
    {
        var unknownExport = new TgTagExport { Tag = "(未知作者)", Count = unknown.Count, Messages = unknown };
        await File.WriteAllTextAsync(Path.Combine(outDir, "_unknown.json"), JsonSerializer.Serialize(unknownExport, jsonOptions), Encoding.UTF8);
        index.Add(new TgTagIndex { Tag = "(未知作者)", Count = unknown.Count, File = "_unknown.json" });
    }

    await File.WriteAllTextAsync(Path.Combine(outDir, "index.json"), JsonSerializer.Serialize(index, jsonOptions), Encoding.UTF8);

    Console.WriteLine($"按作者拆分完成: {index.Count} 个文件");
    Console.WriteLine($"输出目录: {Path.GetFullPath(outDir)}");
}

static string SafeFileName(string name)
{
    string invalid = new string(Path.GetInvalidFileNameChars());
    string safe = Regex.Replace(name, $"[{Regex.Escape(invalid)}]", "_").Trim().TrimEnd('.');
    return safe.Length > 100 ? safe[..100] : safe;
}

static HttpClient CreateHttpClient(string? proxy)
{
    var handler = new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    };

    if (!string.Equals(proxy, "none", StringComparison.OrdinalIgnoreCase))
    {
        string raw = string.IsNullOrWhiteSpace(proxy) ? "127.0.0.1:7897" : proxy.Trim();
        handler.Proxy = new WebProxy($"http://{raw}");
        handler.UseProxy = true;
        Console.WriteLine($"使用代理: {raw}");
    }

    var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
    return client;
}

static async Task<string?> FetchAsync(HttpClient http, string url)
{
    for (int attempt = 1; attempt <= 5; attempt++)
    {
        try
        {
            using var resp = await http.GetAsync(url);
            if (resp.IsSuccessStatusCode) return await resp.Content.ReadAsStringAsync();
            Console.Error.WriteLine($"HTTP {(int)resp.StatusCode} ({attempt}/5): {url}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"请求异常 ({attempt}/5): {ex.Message}");
        }

        await Task.Delay(1500 * attempt);
    }

    return null;
}

static List<TgMessage> ParsePage(string html, string channel)
{
    var result = new List<TgMessage>();

    // 每个消息块以 tgme_widget_message_wrap 开头
    var starts = Regex.Matches(html, "<div class=\"tgme_widget_message_wrap[^\"]*\">")
        .Select(x => x.Index)
        .ToList();

    for (int i = 0; i < starts.Count; i++)
    {
        int start = starts[i];
        int end = i + 1 < starts.Count ? starts[i + 1] : html.Length;
        string block = html[start..end];

        var idMatch = Regex.Match(block, "data-post=\"[^\"/]+/(\\d+)\"");
        if (!idMatch.Success) continue;

        long id = long.Parse(idMatch.Groups[1].Value);
        var message = new TgMessage
        {
            Id = id,
            Url = $"https://t.me/{channel}/{id}",
            Date = MatchText(block, "<time datetime=\"([^\"]+)\""),
            Views = MatchText(block, "class=\"tgme_widget_message_views\">([^<]+)<"),
            Author = MatchText(block, "class=\"tgme_widget_message_owner_name\"[^>]*>([^<]+)<"),
            ForwardedFrom = MatchText(block, "class=\"tgme_widget_message_forwarded_from_name\"[^>]*>([^<]+)<")
        };

        string? textHtml = MatchText(block, "class=\"tgme_widget_message_text[^\"]*\"[^>]*>([\\s\\S]*?)</div>");
        if (textHtml != null)
        {
            message.TextHtml = textHtml.Trim();
            message.Text = HtmlToPlainText(textHtml);
            message.Tags = ExtractTags(textHtml, message.Text);
        }

        message.Media = ExtractMedia(block);

        result.Add(message);
    }

    return result;
}

static string? MatchText(string input, string pattern)
{
    var m = Regex.Match(input, pattern);
    return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;
}

static string HtmlToPlainText(string html)
{
    string s = Regex.Replace(html, "<br\\s*/?>", "\n");
    s = Regex.Replace(s, "<[^>]+>", "");
    return WebUtility.HtmlDecode(s).Trim();
}

static List<string> ExtractTags(string textHtml, string? plainText)
{
    var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // 网页里标签渲染为 ?q=%23tag 的链接
    foreach (Match m in Regex.Matches(textHtml, "\\?q=%23([^\"&]+)"))
    {
        tags.Add("#" + Uri.UnescapeDataString(m.Groups[1].Value));
    }

    // 正文里的 #hashtag
    if (!string.IsNullOrEmpty(plainText))
    {
        foreach (Match m in Regex.Matches(plainText, "#([\\p{L}\\p{N}_]{1,64})"))
        {
            tags.Add("#" + m.Groups[1].Value);
        }
    }

    return tags.ToList();
}

static List<TgMedia> ExtractMedia(string block)
{
    var media = new List<TgMedia>();

    foreach (Match m in Regex.Matches(block,
                 "<a[^>]+class=\"tgme_widget_message_photo_wrap[^\"]*\"[^>]+style=\"[^\"]*?url\\('([^']+)'\\)"))
    {
        media.Add(new TgMedia { Type = "photo", Url = WebUtility.HtmlDecode(m.Groups[1].Value) });
    }

    string? videoThumb = MatchText(block, "class=\"tgme_widget_message_video_thumb[^\"]*\"[^>]*?url\\('([^']+)'\\)");
    foreach (Match m in Regex.Matches(block, "<video[^>]*\\bsrc=\"([^\"]+)\""))
    {
        media.Add(new TgMedia
        {
            Type = "video",
            Url = WebUtility.HtmlDecode(m.Groups[1].Value),
            Thumbnail = videoThumb
        });
    }

    foreach (Match m in Regex.Matches(block, "<audio[^>]*\\bsrc=\"([^\"]+)\""))
    {
        media.Add(new TgMedia { Type = "audio", Url = WebUtility.HtmlDecode(m.Groups[1].Value) });
    }

    foreach (Match m in Regex.Matches(block,
                 "<a[^>]+class=\"tgme_widget_message_document[^\"]*\"[^>]+href=\"([^\"]+)\""))
    {
        media.Add(new TgMedia { Type = "document", Url = WebUtility.HtmlDecode(m.Groups[1].Value) });
    }

    var linkPreview = Regex.Match(block, "<a[^>]+class=\"tgme_widget_message_link_preview[^\"]*\"[^>]+href=\"([^\"]+)\"");
    if (linkPreview.Success)
    {
        media.Add(new TgMedia
        {
            Type = "link",
            Url = WebUtility.HtmlDecode(linkPreview.Groups[1].Value),
            Thumbnail = MatchText(block, "class=\"link_preview_image\"[^>]*?url\\('([^']+)'\\)")
        });
    }

    return media;
}

public class TgExport
{
    public string Channel { get; set; } = "";
    public long StartMessageId { get; set; }
    public long EndMessageId { get; set; }
    /// <summary>本次导出的最新一条消息链接, 下次可从这里继续增量导出。</summary>
    public string? LatestMessageUrl { get; set; }
    public DateTimeOffset ExportedAt { get; set; }
    public int Count { get; set; }
    public List<TgMessage> Messages { get; set; } = [];
}

public class TgMessage
{
    public long Id { get; set; }
    public string Url { get; set; } = "";
    public string? Date { get; set; }
    public string? Views { get; set; }
    public string? Author { get; set; }
    public string? ForwardedFrom { get; set; }
    public string? Text { get; set; }
    public string? TextHtml { get; set; }
    public List<string>? Tags { get; set; }
    public List<TgMedia>? Media { get; set; }
}

public class TgMedia
{
    public string Type { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Thumbnail { get; set; }
}

public class TgTagExport
{
    public string Tag { get; set; } = "";
    public int Count { get; set; }
    public List<TgMessage> Messages { get; set; } = [];
}

public class TgTagIndex
{
    public string Tag { get; set; } = "";
    public int Count { get; set; }
    public string File { get; set; } = "";
}
