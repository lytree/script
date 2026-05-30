#!/usr/bin/env dotnet

#:package Microsoft.Playwright@*
#:package Spectre.Console@*
#:package System.CommandLine@*
#:package CliWrap@*

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Spectre.Console;
using CliWrap;

var optionStartVid = new Option<int>("--start-vid")
{
    Description = "起始 vid",
    DefaultValueFactory = _ => 12200
};

var optionEndVid = new Option<int>("--end-vid")
{
    Description = "结束 vid",
    DefaultValueFactory = _ => 12300
};

var optionOutputDir = new Option<string>("--output-dir")
{
    Description = "下载输出目录",
    DefaultValueFactory = _ => "downloads"
};

var optionConcurrency = new Option<int>("--concurrency")
{
    Description = "M3U8 并发下载数",
    DefaultValueFactory = _ => 1
};

var optionQuality = new Option<string>("--quality")
{
    Description = "视频质量: best, worst, 或分辨率/带宽",
    DefaultValueFactory = _ => "best"
};

var optionSkipCrawl = new Option<bool>("--skip-crawl")
{
    Description = "跳过爬取，直接使用已有的 m3u8_urls.json 文件下载",
    DefaultValueFactory = _ => false
};

var optionOnlyCrawl = new Option<bool>("--only-crawl")
{
    Description = "仅爬取 m3u8 链接，不下载",
    DefaultValueFactory = _ => false
};

var optionFfmpegPath = new Option<string>("--ffmpeg-path")
{
    Description = "ffmpeg 可执行文件路径",
    DefaultValueFactory = _ => "ffmpeg"
};

var optionRetryCount = new Option<int>("--retry")
{
    Description = "下载失败分段的重试次数",
    DefaultValueFactory = _ => 3
};

var rootCommand = new RootCommand("魔典课堂视频爬取与下载工具")
{
    optionStartVid,
    optionEndVid,
    optionOutputDir,
    optionConcurrency,
    optionQuality,
    optionSkipCrawl,
    optionOnlyCrawl,
    optionFfmpegPath,
    optionRetryCount
};

rootCommand.SetAction((ParseResult parseResult) =>
{
    var startVid = parseResult.GetValue(optionStartVid);
    var endVid = parseResult.GetValue(optionEndVid);
    var outputDir = parseResult.GetValue(optionOutputDir)!;
    var concurrency = parseResult.GetValue(optionConcurrency);
    var quality = parseResult.GetValue(optionQuality)!;
    var skipCrawl = parseResult.GetValue(optionSkipCrawl);
    var onlyCrawl = parseResult.GetValue(optionOnlyCrawl);
    var ffmpegPath = parseResult.GetValue(optionFfmpegPath)!;
    var retryCount = parseResult.GetValue(optionRetryCount);

    if (!Directory.Exists(outputDir))
    {
        Directory.CreateDirectory(outputDir);
    }

    var urlMapFile = Path.Combine(outputDir, "m3u8_urls.json");

    try
    {
        if (skipCrawl)
        {
            DownloadVideos(urlMapFile, outputDir, concurrency, quality, ffmpegPath, retryCount).GetAwaiter().GetResult();
            return;
        }

        CrawlAndDownload(startVid, endVid, outputDir, urlMapFile, concurrency, quality, ffmpegPath, retryCount, onlyCrawl).GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[bold red]Error: {ex.Message}[/]");
        Environment.Exit(1);
    }
});

rootCommand.Parse(args).Invoke();

async Task CrawlAndDownload(int startVid, int endVid, string outputDir, string urlMapFile, int concurrency, string quality, string ffmpegPath, int retryCount, bool onlyCrawl)
{
    var userAgent = "Mozilla/5.0 (Linux; Android 13; 22127RK46C Build/TKQ1.220905.001; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/107.0.5304.141 Mobile Safari/537.36 XWEB/5127 MMWEBSDK/20230604 MMWEBID/7189 MicroMessenger/8.0.38.2400(0x28002639) WeChat/arm64 Weixin NetType/WIFI Language/zh_CN ABI/arm64 qcloudcdn-xinan Request-Source=4 Request-Channel";

    var existingResults = new List<VideoInfo>();
    if (File.Exists(urlMapFile))
    {
        var existingJson = await File.ReadAllTextAsync(urlMapFile);
        existingResults = System.Text.Json.JsonSerializer.Deserialize<List<VideoInfo>>(existingJson) ?? [];
        AnsiConsole.MarkupLine($"[cyan]已加载 {existingResults.Count} 条已有记录[/]");
    }

    var existingVids = existingResults.Select(v => v.Vid).ToHashSet();

    var alreadyDownloaded = new HashSet<string>();
    foreach (var file in Directory.GetFiles(outputDir, "*.mp4"))
    {
        alreadyDownloaded.Add(Path.GetFileNameWithoutExtension(file));
    }

    var manager = new PlaywrightManager(userAgent);

    try
    {
        await manager.Init();

        var vids = Enumerable.Range(startVid, endVid - startVid + 1)
            .Where(vid => !existingVids.Contains(vid))
            .ToList();

        if (vids.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]所有 vid 已爬取完毕[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[cyan]需要爬取 {vids.Count} 个 vid (跳过已爬取 {existingVids.Count} 个)[/]");

            foreach (var vid in vids)
            {
                try
                {
                    var info = await manager.GetM3u8Url(vid);
                    if (info != null)
                    {
                        existingResults.Add(info);
                        AnsiConsole.MarkupLine($"[green]vid={vid}: {info.Title} -> {info.M3u8Url}[/]");

                        if (!onlyCrawl && info.Success && !string.IsNullOrEmpty(info.M3u8Url))
                        {
                            var fileName = SanitizeFileName(info.Title);
                            if (!alreadyDownloaded.Contains(fileName))
                            {
                                await DownloadSingleVideo(info, outputDir, concurrency, quality, ffmpegPath, retryCount);
                                alreadyDownloaded.Add(fileName);
                            }
                            else
                            {
                                AnsiConsole.MarkupLine($"[yellow]跳过已下载: {info.Title}[/]");
                            }
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[yellow]vid={vid}: 未找到 m3u8 链接[/]");
                        existingResults.Add(new VideoInfo(vid, $"vid_{vid}", "", false));
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]vid={vid}: {ex.Message}[/]");
                    existingResults.Add(new VideoInfo(vid, $"vid_{vid}", "", false));
                }

                var json = System.Text.Json.JsonSerializer.Serialize(existingResults, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(urlMapFile, json);
            }

            AnsiConsole.MarkupLine($"[green]已保存 {existingResults.Count} 条记录到 {urlMapFile}[/]");
        }
    }
    finally
    {
        await manager.Dispose();
    }
}

async Task DownloadSingleVideo(VideoInfo video, string outputDir, int concurrency, string quality, string ffmpegPath, int retryCount)
{
    var fileName = SanitizeFileName(video.Title);
    var outputPath = Path.Combine(outputDir, $"{fileName}.mp4");

    AnsiConsole.MarkupLine($"[cyan]下载: {video.Title} (vid={video.Vid})[/]");

    try
    {
        await DownloadM3u8Video(video.M3u8Url, outputPath, concurrency, quality, new Dictionary<string, string> { ["Referer"] = "https://www.modianketang.com/" }, ffmpegPath, retryCount);
        AnsiConsole.MarkupLine($"[green]完成: {video.Title}[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]下载异常: {video.Title} - {ex.Message}[/]");
    }
}

async Task DownloadVideos(string urlMapFile, string outputDir, int concurrency, string quality, string ffmpegPath, int retryCount)
{
    if (!File.Exists(urlMapFile))
    {
        AnsiConsole.MarkupLine("[red]未找到 m3u8_urls.json 文件，请先爬取[/]");
        return;
    }

    var json = await File.ReadAllTextAsync(urlMapFile);
    var allVideos = System.Text.Json.JsonSerializer.Deserialize<List<VideoInfo>>(json) ?? [];

    var videos = allVideos.Where(v => v.Success && !string.IsNullOrEmpty(v.M3u8Url)).ToList();
    var failed = allVideos.Count(v => !v.Success);

    AnsiConsole.MarkupLine($"[cyan]共 {allVideos.Count} 条记录，{videos.Count} 个可下载，{failed} 个失败[/]");

    if (videos.Count == 0)
    {
        AnsiConsole.MarkupLine("[yellow]没有可下载的视频[/]");
        return;
    }

    var alreadyDownloaded = new HashSet<string>();
    foreach (var file in Directory.GetFiles(outputDir, "*.mp4"))
    {
        alreadyDownloaded.Add(Path.GetFileNameWithoutExtension(file));
    }

    var toDownload = videos.Where(v => !alreadyDownloaded.Contains(SanitizeFileName(v.Title))).ToList();

    if (toDownload.Count == 0)
    {
        AnsiConsole.MarkupLine("[green]所有视频已下载完毕[/]");
        return;
    }

    AnsiConsole.MarkupLine($"[cyan]需要下载 {toDownload.Count} 个视频 (跳过已下载 {videos.Count - toDownload.Count} 个)[/]");

    foreach (var video in toDownload)
    {
        await DownloadSingleVideo(video, outputDir, concurrency, quality, ffmpegPath, retryCount);
    }
}

async Task DownloadM3u8Video(string url, string outputPath, int concurrency, string quality, Dictionary<string, string> headers, string ffmpegPath, int retryCount)
{
    var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
    if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
    {
        Directory.CreateDirectory(outputDir);
    }

    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var tempDir = Path.Combine(string.IsNullOrEmpty(outputDir) ? "." : outputDir, $".tmp_{timestamp}");

    try
    {
        Directory.CreateDirectory(tempDir);

        using var client = CreateHttpClient(headers);

        AnsiConsole.MarkupLine("[yellow]Fetching m3u8...[/]");
        var masterContent = await FetchM3u8Async(client, url);
        var streams = ParseMasterM3u8(masterContent, url);

        string targetUrl;
        M3u8DownloadInfo? m3u8Info = null;

        if (streams.Count > 0)
        {
            var videoStreams = streams.Where(s => IsVideoStream(s.Codecs)).ToList();
            var displayStreams = videoStreams.Count > 0 ? videoStreams : streams;
            AnsiConsole.MarkupLine($"[green]Found {streams.Count} quality options, {displayStreams.Count} video streams[/]");
            foreach (var s in streams)
            {
                var isVideo = IsVideoStream(s.Codecs);
                var qualityLabel = GetQualityLabel(s.Resolution, s.Bandwidth);
                var videoTag = isVideo ? "" : " [dim](audio only)[/]";
                AnsiConsole.MarkupLine($"  - {qualityLabel} ({FormatBandwidth(s.Bandwidth)}){videoTag}");
            }
            targetUrl = SelectStreamUrl(streams, quality, url);
            AnsiConsole.MarkupLine($"[cyan]Using: {targetUrl}[/]");

            var m3u8Content = await FetchM3u8Async(client, targetUrl);
            m3u8Info = ParseM3u8(m3u8Content, targetUrl);
        }
        else
        {
            targetUrl = url;
            m3u8Info = ParseM3u8(masterContent, url);
        }

        AnsiConsole.MarkupLine($"[green]Found {m3u8Info.Segments.Count} segments[/]");

        if (m3u8Info.KeyInfo != null)
        {
            AnsiConsole.MarkupLine($"[yellow]Encryption: {m3u8Info.KeyInfo.Method}[/]");
            if (m3u8Info.KeyInfo.Method == "AES-128")
            {
                var aesKey = await FetchAesKeyAsync(client, m3u8Info.KeyInfo);
                AnsiConsole.MarkupLine("[green]AES-128 key fetched successfully[/]");
                await DownloadSegmentsAsync(client, m3u8Info, tempDir, aesKey, m3u8Info.KeyInfo.Method, retryCount, concurrency);
            }
            else if (m3u8Info.KeyInfo.Method == "AES-128-ECB")
            {
                var aesKey = await FetchAesKeyAsync(client, m3u8Info.KeyInfo);
                AnsiConsole.MarkupLine("[green]AES-128 ECB key fetched successfully[/]");
                await DownloadSegmentsAsync(client, m3u8Info, tempDir, aesKey, m3u8Info.KeyInfo.Method, retryCount, concurrency);
            }
            else if (m3u8Info.KeyInfo.Method == "SAMPLE-AES")
            {
                throw new NotSupportedException("SAMPLE-AES (FairPlay) encryption requires license server.");
            }
            else if (m3u8Info.KeyInfo.Method == "CHACHA20")
            {
                var key = await FetchAesKeyAsync(client, m3u8Info.KeyInfo);
                AnsiConsole.MarkupLine("[green]CHACHA20 key fetched successfully[/]");
                await DownloadSegmentsAsync(client, m3u8Info, tempDir, key, m3u8Info.KeyInfo.Method, retryCount, concurrency);
            }
            else
            {
                throw new NotSupportedException($"Encryption method '{m3u8Info.KeyInfo.Method}' is not supported.");
            }
        }
        else
        {
            await DownloadSegmentsAsync(client, m3u8Info, tempDir, null, null, retryCount, concurrency);
        }

        await WriteConcatListAsync(m3u8Info, tempDir);
        await MergeWithFFmpegAsync(tempDir, outputPath, ffmpegPath);

        AnsiConsole.MarkupLine($"[bold green]Done: {outputPath}[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[bold red]Error: {ex.Message}[/]");
        throw;
    }
    finally
    {
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, true);
        }
    }
}

HttpClient CreateHttpClient(Dictionary<string, string> headers)
{
    var handler = new HttpClientHandler();
    var client = new HttpClient(handler);
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    foreach (var kv in headers)
    {
        client.DefaultRequestHeaders.TryAddWithoutValidation(kv.Key, kv.Value);
    }
    return client;
}

async Task<string> FetchM3u8Async(HttpClient client, string url)
{
    var response = await client.GetAsync(url);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStringAsync();
}

async Task<byte[]> FetchAesKeyAsync(HttpClient client, EncryptInfo keyInfo)
{
    var response = await client.GetAsync(keyInfo.KeyUrl);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsByteArrayAsync();
}

string ResolveUrl(string baseUrl, string relativeUrl)
{
    if (relativeUrl.StartsWith("http://") || relativeUrl.StartsWith("https://") || relativeUrl.StartsWith("file://"))
    {
        return relativeUrl;
    }

    var baseUri = new Uri(baseUrl);
    if (relativeUrl.StartsWith("/"))
    {
        return $"{baseUri.Scheme}://{baseUri.Authority}{relativeUrl}";
    }

    var basePath = baseUri.AbsolutePath;
    var lastSlash = basePath.LastIndexOf('/');
    if (lastSlash >= 0)
    {
        basePath = basePath[..(lastSlash + 1)];
    }
    return new Uri(baseUri, basePath + relativeUrl).ToString();
}

List<StreamInfo> ParseMasterM3u8(string content, string m3u8Url)
{
    var streams = new List<StreamInfo>();
    var lines = content.Split('\n');

    for (int i = 0; i < lines.Length; i++)
    {
        var line = lines[i].Trim();

        if (line.StartsWith("#EXT-X-STREAM-INF:"))
        {
            var bandwidthMatch = Regex.Match(line, @"BANDWIDTH=(\d+)");
            var resolutionMatch = Regex.Match(line, @"RESOLUTION=(\d+x\d+)");
            var codecsMatch = Regex.Match(line, @"CODECS=""([^""]+)""");

            if (bandwidthMatch.Success && i + 1 < lines.Length)
            {
                var urlLine = lines[i + 1].Trim();
                if (!string.IsNullOrEmpty(urlLine) && !urlLine.StartsWith("#"))
                {
                    streams.Add(new StreamInfo(
                        Bandwidth: long.Parse(bandwidthMatch.Groups[1].Value),
                        Resolution: resolutionMatch.Success ? resolutionMatch.Groups[1].Value : "unknown",
                        Codecs: codecsMatch.Success ? codecsMatch.Groups[1].Value : "",
                        Url: ResolveUrl(m3u8Url, urlLine)
                    ));
                }
            }
        }
    }

    return [.. streams.OrderByDescending(s => s.Bandwidth)];
}

string SelectStreamUrl(List<StreamInfo> streams, string quality, string originalUrl)
{
    var videoStreams = streams.Where(s => IsVideoStream(s.Codecs)).ToList();
    var validStreams = videoStreams.Count > 0 ? videoStreams : streams;

    if (quality == "best")
    {
        return validStreams[0].Url;
    }
    else if (quality == "worst")
    {
        return validStreams[^1].Url;
    }
    else if (long.TryParse(quality, out var bandwidth))
    {
        var matched = validStreams.FirstOrDefault(s => s.Bandwidth <= bandwidth);
        return matched?.Url ?? validStreams[0].Url;
    }

    var exactMatch = validStreams.FirstOrDefault(s => s.Resolution == quality);
    return exactMatch?.Url ?? validStreams[0].Url;
}

bool IsVideoStream(string codecs)
{
    if (string.IsNullOrEmpty(codecs)) return true;
    return codecs.Contains("avc") || codecs.Contains("hvc") || codecs.Contains("hev") || codecs.Contains("vp0") || codecs.Contains("av1");
}

M3u8DownloadInfo ParseM3u8(string content, string m3u8Url)
{
    var segments = new List<TsSegment>();
    EncryptInfo? keyInfo = null;
    var lines = content.Split('\n');
    double? currentDuration = null;

    for (int i = 0; i < lines.Length; i++)
    {
        var line = lines[i].Trim();

        if (line.StartsWith("#EXT-X-KEY:"))
        {
            var methodMatch = Regex.Match(line, @"METHOD=([^,]+)");
            var uriMatch = Regex.Match(line, @"URI=""([^""]+)""");
            var ivMatch = Regex.Match(line, @"IV=0x([0-9a-fA-F]+)");

            var method = methodMatch.Success ? methodMatch.Groups[1].Value : "";
            var keyUrl = uriMatch.Success ? uriMatch.Groups[1].Value : "";
            var iv = ivMatch.Success ? ivMatch.Groups[1].Value : null;

            keyUrl = ResolveUrl(m3u8Url, keyUrl);
            keyInfo = new EncryptInfo(method, keyUrl, iv);
        }
        else if (line.StartsWith("#EXTINF:"))
        {
            var durationStr = line["#EXTINF:".Length..];
            var commaIdx = durationStr.IndexOf(',');
            if (commaIdx >= 0)
            {
                durationStr = durationStr[..commaIdx];
            }
            if (double.TryParse(durationStr, out var dur))
            {
                currentDuration = dur;
            }
        }
        else if (!string.IsNullOrEmpty(line) && !line.StartsWith("#"))
        {
            var resolvedUrl = ResolveUrl(m3u8Url, line);
            segments.Add(new TsSegment(segments.Count, resolvedUrl, currentDuration));
            currentDuration = null;
        }
    }

    return new M3u8DownloadInfo(segments, keyInfo);
}

string FormatBandwidth(long bandwidth)
{
    if (bandwidth >= 1_000_000)
        return $"{bandwidth / 1_000_000.0:F1} Mbps";
    if (bandwidth >= 1_000)
        return $"{bandwidth / 1_000.0:F0} Kbps";
    return $"{bandwidth} bps";
}

string GetQualityLabel(string resolution, long bandwidth)
{
    if (!string.IsNullOrEmpty(resolution) && resolution != "unknown")
    {
        var parts = resolution.Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out var height))
        {
            var label = height switch
            {
                >= 2160 => "4K",
                >= 1440 => "1440p",
                >= 1080 => "1080p",
                >= 720 => "720p",
                >= 480 => "480p",
                >= 360 => "360p",
                >= 240 => "240p",
                _ => $"{height}p"
            };
            return $"{label} ({resolution})";
        }
        return resolution;
    }
    return FormatBandwidth(bandwidth);
}

async Task DownloadSegmentsAsync(HttpClient client, M3u8DownloadInfo m3u8Info, string tempDir, byte[]? key, string? method, int retryCount, int concurrency)
{
    var failedSegments = new List<int>();
    var completed = 0;
    var totalBytes = 0L;
    var total = m3u8Info.Segments.Count;
    var startTime = DateTime.Now;
    var lockObj = new object();

    await AnsiConsole.Progress()
        .AutoClear(false)
        .Columns(
            new TaskDescriptionColumn(),
            new ProgressBarColumn(),
            new PercentageColumn(),
            new RemainingTimeColumn(),
            new SpinnerColumn())
        .StartAsync(async ctx =>
        {
            var progressTask = ctx.AddTask("[cyan]Downloading segments[/]", maxValue: total);

            using var semaphore = new SemaphoreSlim(concurrency);
            var tasks = new List<Task>();

            foreach (var segment in m3u8Info.Segments)
            {
                await semaphore.WaitAsync();

                var localSegment = segment;
                var t = Task.Run(async () =>
                {
                    try
                    {
                        var fileName = $"{localSegment.Index:D5}.ts";
                        var filePath = Path.Combine(tempDir, fileName);

                        for (int attempt = 0; attempt < retryCount; attempt++)
                        {
                            try
                            {
                                var data = await DownloadSegmentAsync(client, localSegment.Url);

                                if (key != null && method != null)
                                {
                                    data = DecryptSegment(data, key, localSegment.Index, m3u8Info.KeyInfo?.Iv, method);
                                }

                                await File.WriteAllBytesAsync(filePath, data);
                                Interlocked.Increment(ref completed);
                                Interlocked.Add(ref totalBytes, data.Length);
                                progressTask.Increment(1);
                                return;
                            }
                            catch
                            {
                                if (attempt < retryCount - 1)
                                    await Task.Delay(500 * (1 << attempt));
                            }
                        }
                        lock (lockObj)
                        {
                            failedSegments.Add(localSegment.Index);
                        }
                        progressTask.Increment(1);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });
                tasks.Add(t);
            }

            await Task.WhenAll(tasks);
        });

    var elapsed = DateTime.Now - startTime;
    var speed = elapsed.TotalSeconds > 0 ? totalBytes / elapsed.TotalSeconds : 0;
    AnsiConsole.MarkupLine($"[green]Downloaded {completed}/{total} segments ({FormatSize(totalBytes)}) in {elapsed.TotalSeconds:F1}s ({FormatSpeed(speed)})[/]");

    if (failedSegments.Count > 0)
    {
        throw new Exception($"Failed: {string.Join(", ", failedSegments)}");
    }
}

async Task<byte[]> DownloadSegmentAsync(HttpClient client, string url)
{
    var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

    if (((int)response.StatusCode).ToString().StartsWith("30"))
    {
        if (response.Headers.Location != null)
        {
            var redirectedUrl = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location.AbsoluteUri
                : new Uri(new Uri(url), response.Headers.Location).ToString();
            return await client.GetByteArrayAsync(redirectedUrl);
        }
    }

    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsByteArrayAsync();
}

byte[] DecryptSegment(byte[] encryptedData, byte[] key, int segmentIndex, string? ivHex, string method)
{
    if (method == "AES-128-ECB")
    {
        return AESDecrypt(encryptedData, key, null, CipherMode.ECB);
    }

    byte[] iv;
    if (ivHex != null)
    {
        iv = new byte[16];
        var hexBytes = Convert.FromHexString(ivHex);
        var offset = 16 - hexBytes.Length;
        Array.Copy(hexBytes, 0, iv, offset, hexBytes.Length);
    }
    else
    {
        iv = new byte[16];
        var indexBytes = BitConverter.GetBytes(segmentIndex);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(indexBytes);
        }
        Array.Copy(indexBytes, 0, iv, 12, 4);
    }

    if (method == "CHACHA20")
    {
        return ChaCha20Decrypt(encryptedData, key, iv);
    }

    return AESDecrypt(encryptedData, key, iv, CipherMode.CBC);
}

byte[] AESDecrypt(byte[] encryptedData, byte[] key, byte[]? iv, CipherMode mode)
{
    var aes = Aes.Create();
    aes.BlockSize = 128;
    aes.KeySize = 128;
    aes.Key = key;
    aes.IV = iv ?? new byte[16];
    aes.Mode = mode;
    aes.Padding = PaddingMode.PKCS7;

    using var decryptor = aes.CreateDecryptor();
    using var msInput = new MemoryStream(encryptedData);
    using var cs = new CryptoStream(msInput, decryptor, CryptoStreamMode.Read);
    using var msOutput = new MemoryStream();
    cs.CopyTo(msOutput);
    return msOutput.ToArray();
}

byte[] ChaCha20Decrypt(byte[] ciphertext, byte[] key, byte[] nonce)
{
    var decrypted = new byte[ciphertext.Length];

    var state = new uint[16];

    state[0] = 0x61707865;
    state[1] = 0x3320646e;
    state[2] = 0x79622d32;
    state[3] = 0x6b206574;

    for (int i = 0; i < 8; i++)
    {
        state[4 + i] = BitConverter.ToUInt32(key, i * 4);
    }

    for (int i = 0; i < 3; i++)
    {
        state[12 + i] = BitConverter.ToUInt32(nonce, i * 4);
    }

    state[15] = BitConverter.ToUInt32(nonce, 12);

    for (int i = 0; i < ciphertext.Length; i += 64)
    {
        var workingState = (uint[])state.Clone();

        for (int round = 0; round < 10; round += 2)
        {
            ChaCha20QuarterRound(workingState, 0, 4, 8, 12);
            ChaCha20QuarterRound(workingState, 1, 5, 9, 13);
            ChaCha20QuarterRound(workingState, 2, 6, 10, 14);
            ChaCha20QuarterRound(workingState, 3, 7, 11, 15);
            ChaCha20QuarterRound(workingState, 0, 5, 10, 15);
            ChaCha20QuarterRound(workingState, 1, 6, 11, 12);
            ChaCha20QuarterRound(workingState, 2, 7, 8, 13);
            ChaCha20QuarterRound(workingState, 3, 4, 9, 14);
        }

        for (int j = 0; j < 16; j++)
        {
            workingState[j] += state[j];
        }

        var block = new byte[64];
        for (int j = 0; j < 16; j++)
        {
            var bytes = BitConverter.GetBytes(workingState[j]);
            if (BitConverter.IsLittleEndian)
            {
                block[j * 4] = bytes[0];
                block[j * 4 + 1] = bytes[1];
                block[j * 4 + 2] = bytes[2];
                block[j * 4 + 3] = bytes[3];
            }
            else
            {
                block[j * 4 + 3] = bytes[0];
                block[j * 4 + 2] = bytes[1];
                block[j * 4 + 1] = bytes[2];
                block[j * 4] = bytes[3];
            }
        }

        var remaining = Math.Min(64, ciphertext.Length - i);
        for (int j = 0; j < remaining; j++)
        {
            decrypted[i + j] = (byte)(ciphertext[i + j] ^ block[j]);
        }

        state[12] = state[12] + 1;
        if (state[12] == 0) state[13] = state[13] + 1;
    }

    return decrypted;
}

void ChaCha20QuarterRound(uint[] state, int a, int b, int c, int d)
{
    state[a] += state[b];
    state[d] ^= state[a];
    state[d] = RotateLeft(state[d], 16);
    state[c] += state[d];
    state[b] ^= state[c];
    state[b] = RotateLeft(state[b], 12);
    state[a] += state[b];
    state[d] ^= state[a];
    state[d] = RotateLeft(state[d], 8);
    state[c] += state[d];
    state[b] ^= state[c];
    state[b] = RotateLeft(state[b], 7);
}

uint RotateLeft(uint value, int bits)
{
    return (value << bits) | (value >> (32 - bits));
}

string FormatSize(long bytes)
{
    if (bytes >= 1_000_000_000)
        return $"{bytes / 1_000_000_000.0:F2} GB";
    if (bytes >= 1_000_000)
        return $"{bytes / 1_000_000.0:F2} MB";
    if (bytes >= 1_000)
        return $"{bytes / 1_000.0:F2} KB";
    return $"{bytes} B";
}

string FormatSpeed(double bytesPerSecond)
{
    if (bytesPerSecond >= 1_000_000_000)
        return $"{bytesPerSecond / 1_000_000_000:F2} GB/s";
    if (bytesPerSecond >= 1_000_000)
        return $"{bytesPerSecond / 1_000_000:F2} MB/s";
    if (bytesPerSecond >= 1_000)
        return $"{bytesPerSecond / 1_000:F2} KB/s";
    return $"{bytesPerSecond:F0} B/s";
}

async Task WriteConcatListAsync(M3u8DownloadInfo m3u8Info, string tempDir)
{
    var listPath = Path.Combine(tempDir, "filelist.txt");
    using var writer = new StreamWriter(listPath, false, new UTF8Encoding(false));
    foreach (var segment in m3u8Info.Segments)
    {
        var filePath = Path.Combine(tempDir, $"{segment.Index:D5}.ts");
        await writer.WriteLineAsync($"file '{filePath}'");
    }
}

async Task MergeWithFFmpegAsync(string tempDir, string outputPath, string ffmpegPath)
{
    var listPath = Path.Combine(tempDir, "filelist.txt");

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync("[cyan]Merging with FFmpeg...[/]", async ctx =>
        {
            var result = await Cli.Wrap(ffmpegPath)
                .WithArguments($"-y -f concat -safe 0 -i \"{listPath}\" -c copy \"{outputPath}\"")
                .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
                {
                    if (!string.IsNullOrEmpty(line))
                    {
                        ctx.Status($"[cyan]Merging:[/] [dim]{Markup.Escape(line)}[/]");
                    }
                }))
                .ExecuteAsync();

            if (result.ExitCode != 0)
            {
                throw new Exception($"FFmpeg exited with code {result.ExitCode}");
            }
        });

    AnsiConsole.MarkupLine("[bold green]Merge completed![/]");
}

string SanitizeFileName(string name)
{
    var invalid = Path.GetInvalidFileNameChars();
    var result = new StringBuilder();
    foreach (var c in name)
    {
        if (invalid.Contains(c) || c == '：' || c == '？' || c == '＼' || c == '／' || c == '＊' || c == '＜' || c == '＞' || c == '｜' || c == '"')
        {
            result.Append('_');
        }
        else
        {
            result.Append(c);
        }
    }
    var sanitized = result.ToString().Trim();
    return string.IsNullOrEmpty(sanitized) ? "untitled" : sanitized;
}

record VideoInfo(int Vid, string Title, string M3u8Url, bool Success);
record M3u8DownloadInfo(List<TsSegment> Segments, EncryptInfo? KeyInfo);
record TsSegment(int Index, string Url, double? Duration);
record EncryptInfo(string Method, string KeyUrl, string? Iv);
record StreamInfo(long Bandwidth, string Resolution, string Codecs, string Url);

class PlaywrightManager
{
    private readonly string _userAgent;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;

    public PlaywrightManager(string userAgent)
    {
        _userAgent = userAgent;
    }

    public async Task Init()
    {
        AnsiConsole.MarkupLine("[cyan]启动 Playwright...[/]");

        _playwright = await Microsoft.Playwright.Playwright.CreateAsync();

        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = _userAgent,
            ViewportSize = new ViewportSize { Width = 375, Height = 812 },
            IsMobile = true,
            HasTouch = true,
        });

        AnsiConsole.MarkupLine("[green]Playwright 启动成功[/]");
    }

    public async Task<VideoInfo?> GetM3u8Url(int vid)
    {
        if (_context == null) throw new InvalidOperationException("Playwright 未初始化");

        var url = $"https://www.modianketang.com/bookqr.html#/about?vid={vid}";
        var m3u8Url = "";
        var pageTitle = $"vid_{vid}";
        var apiTitle = "";
        var page = await _context.NewPageAsync();

        try
        {
            var responseTcs = new TaskCompletionSource<string>();

            page.Response += async (_, response) =>
            {
                try
                {
                    var extracted = ExtractM3u8Url(response.Url);
                    if (!string.IsNullOrEmpty(extracted))
                    {
                        responseTcs.TrySetResult(extracted);
                    }

                    if (response.Url.Contains("/api/") && response.Url.Contains("vid"))
                    {
                        try
                        {
                            var text = await response.TextAsync();
                            var titleMatch = System.Text.RegularExpressions.Regex.Match(text, @"""title""\s*:\s*""([^""]+)""");
                            if (titleMatch.Success)
                            {
                                apiTitle = titleMatch.Groups[1].Value;
                            }
                            var nameMatch = System.Text.RegularExpressions.Regex.Match(text, @"""name""\s*:\s*""([^""]+)""");
                            if (nameMatch.Success && string.IsNullOrEmpty(apiTitle))
                            {
                                apiTitle = nameMatch.Groups[1].Value;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            };

            page.Request += (_, request) =>
            {
                try
                {
                    var extracted = ExtractM3u8Url(request.Url);
                    if (!string.IsNullOrEmpty(extracted))
                    {
                        responseTcs.TrySetResult(extracted);
                    }
                }
                catch { }
            };

            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 30000
            });

            try
            {
                await page.WaitForTimeoutAsync(3000);

                pageTitle = await page.EvaluateAsync<string>(@"() => {
                    const titleEl = document.querySelector('#title');
                    if (titleEl && titleEl.textContent.trim()) return titleEl.textContent.trim();

                    const selectors = [
                        '.video-info .name', '.video-name', '.course-name',
                        '.detail-title', '.video-title', '.course-title',
                        '.video-detail .title', '.detail-info .name',
                        '.player-title', '.vod-name', '.video-name-text',
                        'h1', 'h2', 'h3'
                    ];
                    for (const sel of selectors) {
                        const el = document.querySelector(sel);
                        if (el && el.textContent.trim() && el.textContent.trim() !== '预览') {
                            return el.textContent.trim();
                        }
                    }

                    try {
                        const app = document.querySelector('#app') || document.querySelector('[data-v-app]');
                        if (app && app.__vue__) {
                            const vm = app.__vue__;
                            const data = vm.$data || vm._data || {};
                            if (data.title) return data.title;
                            if (data.videoInfo && data.videoInfo.title) return data.videoInfo.title;
                            if (data.detail && data.detail.title) return data.detail.title;
                            if (data.info && data.info.title) return data.info.title;
                            if (data.name) return data.name;
                        }
                    } catch(e) {}

                    if (document.title && document.title !== 'about:blank' && document.title !== '预览') {
                        return document.title;
                    }
                    return '';
                }");
                if (string.IsNullOrEmpty(pageTitle) || pageTitle == "预览")
                {
                    pageTitle = !string.IsNullOrEmpty(apiTitle) ? apiTitle : $"vid_{vid}";
                }

                var videoElement = await page.QuerySelectorAsync("video");
                if (videoElement != null)
                {
                    var src = await videoElement.GetAttributeAsync("src");
                    if (!string.IsNullOrEmpty(src) && IsM3u8Path(src))
                    {
                        responseTcs.TrySetResult(src);
                    }
                }
            }
            catch { }

            try
            {
                await page.ClickAsync("video", new PageClickOptions { Timeout = 5000 });
                await page.WaitForTimeoutAsync(2000);
            }
            catch { }

            if (responseTcs.Task.IsCompleted)
            {
                m3u8Url = await responseTcs.Task;
            }
            else
            {
                var completed = responseTcs.Task.Wait(8000);
                if (completed)
                {
                    m3u8Url = await responseTcs.Task;
                }
            }

            if (string.IsNullOrEmpty(m3u8Url))
            {
                m3u8Url = await page.EvaluateAsync<string>(@"() => {
                    const videos = document.querySelectorAll('video');
                    for (const v of videos) {
                        if (v.src && v.src.includes('.m3u8')) return v.src;
                        const sources = v.querySelectorAll('source');
                        for (const s of sources) {
                            if (s.src && s.src.includes('.m3u8')) return s.src;
                        }
                    }
                    return '';
                }");
            }

            if (string.IsNullOrEmpty(m3u8Url))
            {
                m3u8Url = await page.EvaluateAsync<string>(@"() => {
                    if (window.performance && window.performance.getEntriesByType) {
                        const resources = window.performance.getEntriesByType('resource');
                        const m3u8Resources = resources.filter(r => {
                            try {
                                const u = new URL(r.name);
                                return u.pathname.endsWith('.m3u8');
                            } catch(e) { return false; }
                        });
                        if (m3u8Resources.length > 0) {
                            return m3u8Resources[0].name;
                        }
                    }
                    return '';
                }");
            }
        }
        finally
        {
            await page.CloseAsync();
        }

        if (string.IsNullOrEmpty(m3u8Url))
        {
            return null;
        }

        return new VideoInfo(vid, pageTitle, m3u8Url, true);
    }

    static string ExtractM3u8Url(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";

        if (url.Contains(".m3u8"))
        {
            try
            {
                var uri = new Uri(url);
                if (uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    return url;
                }
            }
            catch { }
        }

        return "";
    }

    static bool IsM3u8Path(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var uri = new Uri(path);
            return uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
        }
    }

    public async Task Dispose()
    {
        if (_browser != null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}
