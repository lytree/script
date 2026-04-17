
#:package Spectre.Console@*
#:package System.CommandLine@*
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

var m3u8Url =  "https://hls.isihiq.cn/videos5/4f6b836bfbfbb5e6779e14c79ea5d2d8/4f6b836bfbfbb5e6779e14c79ea5d2d8.m3u8?auth_key=1776252552-69df7688c246d-0-bfe1b425fdca82ef0ffbeafc3f17e087&v=3&time=0";
var output =  "output.mp4";
var concurrency = 8;
var headers = new Dictionary<string, string>
{

};
var ffmpegPath = "ffmpeg";
var quality = args.Length > 2 ? args[2] : "best";


var outputDir = Path.GetDirectoryName(Path.GetFullPath(output));
if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
{
    Directory.CreateDirectory(outputDir);
}

var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
var tempDir = Path.Combine(string.IsNullOrEmpty(outputDir) ? "." : outputDir, $".tmp_{timestamp}");

try
{
    Directory.CreateDirectory(tempDir);

    using var client = CreateHttpClient();

    AnsiConsole.Markup($"[yellow]Fetching master playlist...[/]");
    var masterContent = await FetchM3u8Async(client, m3u8Url);
    var streams = ParseMasterM3u8(masterContent, m3u8Url);

    string targetUrl;
    if (streams.Count > 0)
    {
        AnsiConsole.Markup($"[green]Found {streams.Count} quality options[/]");
        foreach (var s in streams)
        {
            AnsiConsole.Markup($"  - {s.Resolution} ({s.Bandwidth} bps)");
        }
        targetUrl = SelectStreamUrl(streams, quality, m3u8Url);
    }
    else
    {
        targetUrl = m3u8Url;
    }

    AnsiConsole.Markup($"[cyan]Using: {targetUrl}[/]\n");

    var m3u8Content = await FetchM3u8Async(client, targetUrl);
    var m3u8Info = ParseM3u8(m3u8Content, targetUrl);

    AnsiConsole.Markup($"[green]Found {m3u8Info.Segments.Count} segments[/]");

    if (m3u8Info.KeyInfo != null)
    {
        AnsiConsole.Markup($"[yellow]Encryption: {m3u8Info.KeyInfo.Method}[/]");
        if (m3u8Info.KeyInfo.Method == "AES-128")
        {
            var aesKey = await FetchAesKeyAsync(client, m3u8Info.KeyInfo);
            AnsiConsole.Markup("[green]AES-128 key fetched successfully[/]");
            await DownloadSegmentsAsync(client, m3u8Info, tempDir, aesKey);
        }
        else if (m3u8Info.KeyInfo.Method == "SAMPLE-AES")
        {
            throw new NotSupportedException("SAMPLE-AES (FairPlay) encryption requires license server. This is not supported in standalone mode.");
        }
        else
        {
            throw new NotSupportedException($"Encryption method '{m3u8Info.KeyInfo.Method}' is not supported.");
        }
    }
    else
    {
        await DownloadSegmentsAsync(client, m3u8Info, tempDir, null);
    }

    await WriteConcatListAsync(m3u8Info, tempDir);
    await MergeWithFFmpegAsync(tempDir, output);

    AnsiConsole.Markup($"[bold green]Done: {output}[/]");
}
finally
{
    if (Directory.Exists(tempDir))
    {
        Directory.Delete(tempDir, true);
    }
}



HttpClient CreateHttpClient()
{
    HttpClientHandler handler = new();
    var client = new HttpClient(handler);
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/78.0.3904.108 Safari/537.36");
    foreach (var kv in headers)
    {
        client.DefaultRequestHeaders.Add(kv.Key, kv.Value);
    }
    return client;
}

async Task<string> FetchM3u8Async(HttpClient client, string url)
{
    var response = await client.GetAsync(url);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStringAsync();
}

string ResolveUrl(string baseUrl, string relativeUrl)
{
    if (relativeUrl.StartsWith("http://") || relativeUrl.StartsWith("https://"))
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

    return streams.OrderByDescending(s => s.Bandwidth).ToList();
}

string SelectStreamUrl(List<StreamInfo> streams, string quality, string originalUrl)
{
    if (quality == "best")
    {
        return streams[0].Url;
    }
    else if (quality == "worst")
    {
        return streams[^1].Url;
    }
    else if (long.TryParse(quality, out var bandwidth))
    {
        var matched = streams.FirstOrDefault(s => s.Bandwidth <= bandwidth);
        return matched?.Url ?? streams[0].Url;
    }

    var exactMatch = streams.FirstOrDefault(s => s.Resolution == quality);
    return exactMatch?.Url ?? streams[0].Url;
}

M3u8Info ParseM3u8(string content, string m3u8Url)
{
    var segments = new List<TsSegment>();
    AesKeyInfo? keyInfo = null;
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
            keyInfo = new AesKeyInfo(method, keyUrl, iv);
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

    return new M3u8Info(segments, keyInfo);
}

async Task<byte[]> FetchAesKeyAsync(HttpClient client, AesKeyInfo keyInfo)
{
    var response = await client.GetAsync(keyInfo.KeyUrl);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsByteArrayAsync();
}

byte[] DecryptSegment(byte[] encryptedData, byte[] key, int segmentIndex, string? ivHex)
{
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

    using var aes = Aes.Create();
    aes.Mode = CipherMode.CBC;
    aes.Padding = PaddingMode.PKCS7;
    aes.Key = key;
    aes.IV = iv;

    using var decryptor = aes.CreateDecryptor();
    using var msInput = new MemoryStream(encryptedData);
    using var cs = new CryptoStream(msInput, decryptor, CryptoStreamMode.Read);
    using var msOutput = new MemoryStream();
    cs.CopyTo(msOutput);
    return msOutput.ToArray();
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

async Task DownloadSegmentsAsync(HttpClient client, M3u8Info m3u8Info, string tempDir, byte[]? aesKey)
{
    var semaphore = new SemaphoreSlim(concurrency);
    var failedSegments = new List<int>();
    var completed = 0;
    var totalBytes = 0L;
    var total = m3u8Info.Segments.Count;
    var lockObj = new object();
    var startTime = DateTime.Now;

    AnsiConsole.Progress()
        .Start(ctx =>
        {
            var task = ctx.AddTask("[cyan]Downloading segments...[/]", maxValue: total);

            var tasks = m3u8Info.Segments.Select(segment => Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var fileName = $"{segment.Index:D5}.ts";
                    var filePath = Path.Combine(tempDir, fileName);
                    var retries = 3;

                    for (int attempt = 0; attempt < retries; attempt++)
                    {
                        try
                        {
                            var data = await client.GetByteArrayAsync(segment.Url);
                            if (aesKey != null)
                            {
                                data = DecryptSegment(data, aesKey, segment.Index, m3u8Info.KeyInfo?.Iv);
                            }
                            await File.WriteAllBytesAsync(filePath, data);
                            lock (lockObj)
                            {
                                completed++;
                                totalBytes += data.Length;
                                var elapsed = (DateTime.Now - startTime).TotalSeconds;
                                var speed = elapsed > 0 ? totalBytes / elapsed : 0;
                                var speedStr = FormatSpeed(speed);
                                task.Description = $"[cyan]Downloading... {speedStr}[/]";
                                task.Increment(1);
                            }
                            return;
                        }
                        catch (Exception) when (attempt < retries - 1)
                        {
                            await Task.Delay(1000 * (1 << attempt));
                        }
                    }

                    lock (lockObj)
                    {
                        failedSegments.Add(segment.Index);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            })).ToArray();

            Task.WhenAll(tasks).Wait();
        });

    var elapsed = DateTime.Now - startTime;
    var speed = elapsed.TotalSeconds > 0 ? totalBytes / elapsed.TotalSeconds : 0;
    AnsiConsole.Markup($"\n[green]Downloaded {completed} segments ({FormatSize(totalBytes)}) in {elapsed.TotalSeconds:F1}s ({FormatSpeed(speed)})[/]");

    if (failedSegments.Count > 0)
    {
        throw new Exception($"Failed to download segments: {string.Join(", ", failedSegments)}");
    }
}

async Task WriteConcatListAsync(M3u8Info m3u8Info, string tempDir)
{
    var listPath = Path.Combine(tempDir, "filelist.txt");
    using var writer = new StreamWriter(listPath, false, new UTF8Encoding(false));
    foreach (var segment in m3u8Info.Segments)
    {
        var filePath = Path.Combine(tempDir, $"{segment.Index:D5}.ts");
        await writer.WriteLineAsync($"file '{filePath}'");
    }
}

async Task MergeWithFFmpegAsync(string tempDir, string outputPath)
{
    var listPath = Path.Combine(tempDir, "filelist.txt");
    var psi = new ProcessStartInfo
    {
        FileName = ffmpegPath,
        Arguments = $"-y -f \"concat\" -safe 0 -i \"{listPath}\" -c copy \"{outputPath}\"",
        UseShellExecute = false,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    AnsiConsole.Markup("[cyan]Merging with FFmpeg...[/]");

    using var process = new Process { StartInfo = psi };
    process.Start();

    var stderrTask = process.StandardError.ReadToEndAsync();
    var stderr = await stderrTask;

    await process.WaitForExitAsync();

    if (process.ExitCode != 0)
    {
        AnsiConsole.MarkupLine("[red]FFmpeg error:[/]");
        Console.WriteLine(stderr);
        throw new Exception($"FFmpeg exited with code {process.ExitCode}");
    }
}

record M3u8Info(List<TsSegment> Segments, AesKeyInfo? KeyInfo);
record TsSegment(int Index, string Url, double? Duration);
record AesKeyInfo(string Method, string KeyUrl, string? Iv);
record StreamInfo(long Bandwidth, string Resolution, string Codecs, string Url);