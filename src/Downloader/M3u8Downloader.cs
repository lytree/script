#!/usr/bin/env dotnet
#:package Spectre.Console@*
#:package System.CommandLine@*
#:package CliWrap@*

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;
using CliWrap;

var optionUrl = new Option<string>("--url")
{
    Description = "The m3u8 URL to download (required)",
    DefaultValueFactory = (res) => "https://devstreaming-cdn.apple.com/videos/streaming/examples/bipbop_4x3/bipbop_4x3_variant.m3u8"
};

var optionOutput = new Option<string>("--output")
{
    Description = "Output file path (default: output.mp4)"
};

var optionConcurrency = new Option<int>("--concurrency")
{
    Description = "Number of concurrent downloads (default: 8)"
};

var optionQuality = new Option<string>("--quality")
{
    Description = "Video quality: best, worst, or resolution/bandwidth (default: best)"
};

var optionHeaders = new Option<string[]>("--header")
{
    Description = "HTTP headers in format key=value (can be specified multiple times)"
};

var optionFfmpegPath = new Option<string>("--ffmpeg-path")
{
    Description = "Path to ffmpeg executable (default: ffmpeg)"
};

var optionRetryCount = new Option<int>("--retry")
{
    Description = "Number of retry attempts for failed segments (default: 3)"
};

var optionSpeedLimit = new Option<long>("--speed-limit")
{
    Description = "Download speed limit in bytes per second, 0 = unlimited (default: 0)"
};

var rootCommand = new RootCommand("M3u8 Downloader - Download m3u8 videos")
{
    optionUrl,
    optionOutput,
    optionConcurrency,
    optionQuality,
    optionHeaders,
    optionFfmpegPath,
    optionRetryCount,
    optionSpeedLimit
};

var tempDir = "";
var speedContainer = new SpeedContainer();

rootCommand.SetAction((ParseResult parseResult) =>
{
    var url = parseResult.GetValue(optionUrl);
    var output = parseResult.GetValue(optionOutput) ?? "output.mp4";
    var concurrency = parseResult.GetValue(optionConcurrency);
    var quality = parseResult.GetValue(optionQuality) ?? "best";
    var headerPairs = parseResult.GetValue(optionHeaders);
    var ffmpegPath = parseResult.GetValue(optionFfmpegPath) ?? "ffmpeg";
    var retryCount = parseResult.GetValue(optionRetryCount);
    var speedLimit = parseResult.GetValue(optionSpeedLimit);

    if (string.IsNullOrEmpty(url))
    {
        AnsiConsole.Markup("[bold red]Error: --url is required[/]");
        return;
    }

    speedContainer.SpeedLimit = speedLimit;

    var headers = new Dictionary<string, string>();
    if (headerPairs != null)
    {
        foreach (var pair in headerPairs)
        {
            var idx = pair.IndexOf('=');
            if (idx > 0)
            {
                var key = pair[..idx];
                var value = pair[(idx + 1)..];
                headers[key] = value;
            }
        }
    }

    var outputDir = Path.GetDirectoryName(Path.GetFullPath(output));
    if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
    {
        Directory.CreateDirectory(outputDir);
    }

    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    tempDir = Path.Combine(string.IsNullOrEmpty(outputDir) ? "." : outputDir, $".tmp_{timestamp}");

    try
    {
        Directory.CreateDirectory(tempDir);

        using var client = CreateHttpClient(headers);

        AnsiConsole.Markup($"[yellow]Fetching master playlist...[/]");
        var masterContent = FetchM3u8Async(client, url).GetAwaiter().GetResult();
        var streams = ParseMasterM3u8(masterContent, url);

        string targetUrl;
        if (streams.Count > 0)
        {
            var videoStreams = streams.Where(s => IsVideoStream(s.Codecs)).ToList();
            var displayStreams = videoStreams.Count > 0 ? videoStreams : streams;
            AnsiConsole.Markup($"[green]Found {streams.Count} quality options, {displayStreams.Count} video streams[/]");
            foreach (var s in streams)
            {
                var isVideo = IsVideoStream(s.Codecs);
                var qualityLabel = GetQualityLabel(s.Resolution, s.Bandwidth);
                var videoTag = isVideo ? "" : " [dim](audio only)[/]";
                AnsiConsole.Markup($"  - {qualityLabel} ({FormatBandwidth(s.Bandwidth)}){videoTag}");
            }
            targetUrl = SelectStreamUrl(streams, quality, url);
        }
        else
        {
            targetUrl = url;
        }

        AnsiConsole.Markup($"[cyan]Using: {targetUrl}[/]\n");

        var m3u8Content = FetchM3u8Async(client, targetUrl).GetAwaiter().GetResult();
        var m3u8Info = ParseM3u8(m3u8Content, targetUrl);

        AnsiConsole.Markup($"[green]Found {m3u8Info.Segments.Count} segments[/]");

        if (m3u8Info.KeyInfo != null)
        {
            AnsiConsole.Markup($"[yellow]Encryption: {m3u8Info.KeyInfo.Method}[/]");
            if (m3u8Info.KeyInfo.Method == "AES-128")
            {
                var aesKey = FetchAesKeyAsync(client, m3u8Info.KeyInfo).GetAwaiter().GetResult();
                AnsiConsole.Markup("[green]AES-128 key fetched successfully[/]");
                DownloadSegmentsAsync(client, m3u8Info, tempDir, aesKey, m3u8Info.KeyInfo.Method, retryCount, concurrency).GetAwaiter().GetResult();
            }
            else if (m3u8Info.KeyInfo.Method == "AES-128-ECB")
            {
                var aesKey = FetchAesKeyAsync(client, m3u8Info.KeyInfo).GetAwaiter().GetResult();
                AnsiConsole.Markup("[green]AES-128 ECB key fetched successfully[/]");
                DownloadSegmentsAsync(client, m3u8Info, tempDir, aesKey, m3u8Info.KeyInfo.Method, retryCount, concurrency).GetAwaiter().GetResult();
            }
            else if (m3u8Info.KeyInfo.Method == "SAMPLE-AES")
            {
                throw new NotSupportedException("SAMPLE-AES (FairPlay) encryption requires license server.");
            }
            else if (m3u8Info.KeyInfo.Method == "CHACHA20")
            {
                var key = FetchAesKeyAsync(client, m3u8Info.KeyInfo).GetAwaiter().GetResult();
                AnsiConsole.Markup("[green]CHACHA20 key fetched successfully[/]");
                DownloadSegmentsAsync(client, m3u8Info, tempDir, key, m3u8Info.KeyInfo.Method, retryCount, concurrency).GetAwaiter().GetResult();
            }
            else
            {
                throw new NotSupportedException($"Encryption method '{m3u8Info.KeyInfo.Method}' is not supported.");
            }
        }
        else
        {
            DownloadSegmentsAsync(client, m3u8Info, tempDir, null, null, retryCount, concurrency).GetAwaiter().GetResult();
        }

        WriteConcatListAsync(m3u8Info, tempDir).GetAwaiter().GetResult();
        MergeWithFFmpegAsync(tempDir, output, ffmpegPath).GetAwaiter().GetResult();

        AnsiConsole.Markup($"[bold green]Done: {output}[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.Markup($"[bold red]Error: {ex.Message}[/]");
        throw;
    }
    finally
    {
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, true);
        }
    }
});

rootCommand.Parse(args).Invoke();



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

    return streams.OrderByDescending(s => s.Bandwidth).ToList();
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

M3u8Info ParseM3u8(string content, string m3u8Url)
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

    return new M3u8Info(segments, keyInfo);
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

async Task DownloadSegmentsAsync(HttpClient client, M3u8Info m3u8Info, string tempDir, byte[]? key, string? method, int retryCount, int concurrency)
{
    var failedSegments = new List<int>();
    var completed = 0;
    var totalBytes = 0L;
    var total = m3u8Info.Segments.Count;
    var startTime = DateTime.Now;

    AnsiConsole.Markup($"[cyan]Downloading {total} segments with {concurrency} threads...[/]\n");

    using var semaphore = new SemaphoreSlim(concurrency);
    var tasks = new List<Task>();

    foreach (var segment in m3u8Info.Segments)
    {
        await semaphore.WaitAsync();

        var task = Task.Run(async () =>
        {
            try
            {
                var fileName = $"{segment.Index:D5}.ts";
                var filePath = Path.Combine(tempDir, fileName);

                for (int attempt = 0; attempt < retryCount; attempt++)
                {
                    try
                    {
                        var data = await DownloadSegmentAsync(client, segment.Url);

                        if (key != null && method != null)
                        {
                            data = DecryptSegment(data, key, segment.Index, m3u8Info.KeyInfo?.Iv, method);
                        }

                        await File.WriteAllBytesAsync(filePath, data);
                        Interlocked.Increment(ref completed);
                        Interlocked.Add(ref totalBytes, data.Length);

                        var elapsed = (DateTime.Now - startTime).TotalSeconds;
                        var speed = elapsed > 0 ? totalBytes / elapsed : 0;
                        if (completed % 10 == 0 || completed == total)
                        {
                            AnsiConsole.Markup($"[cyan]Progress: {completed}/{total} ({FormatSpeed(speed)})[/]\r");
                        }
                        return;
                    }
                    catch
                    {
                        if (attempt < retryCount - 1)
                            await Task.Delay(500 * (1 << attempt));
                    }
                }
                failedSegments.Add(segment.Index);
            }
            finally
            {
                semaphore.Release();
            }
        });
        tasks.Add(task);
    }

    await Task.WhenAll(tasks);

    var elapsed = DateTime.Now - startTime;
    var speed = elapsed.TotalSeconds > 0 ? totalBytes / elapsed.TotalSeconds : 0;
    AnsiConsole.Markup($"\n[green]Downloaded {completed}/{total} segments ({FormatSize(totalBytes)}) in {elapsed.TotalSeconds:F1}s ({FormatSpeed(speed)})[/]");

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

async Task MergeWithFFmpegAsync(string tempDir, string outputPath, string ffmpegPath)
{
    var listPath = Path.Combine(tempDir, "filelist.txt");

    AnsiConsole.Markup("[cyan]Merging with FFmpeg...[/]");

    var result = await Cli.Wrap(ffmpegPath)
        .WithArguments($"-y -f concat -safe 0 -i \"{listPath}\" -c copy \"{outputPath}\"")
        .WithStandardErrorPipe(PipeTarget.ToDelegate(line => AnsiConsole.Markup($"[grey]{line}[/]")))
        .ExecuteAsync();

    if (result.ExitCode != 0)
    {
        throw new Exception($"FFmpeg exited with code {result.ExitCode}");
    }
}

record M3u8Info(List<TsSegment> Segments, EncryptInfo? KeyInfo);
record TsSegment(int Index, string Url, double? Duration);
record EncryptInfo(string Method, string KeyUrl, string? Iv);
record StreamInfo(long Bandwidth, string Resolution, string Codecs, string Url);

class SpeedContainer
{
    public long SpeedLimit { get; set; }
    public long ResponseLength { get; set; }
    public bool SingleSegment { get; set; }
    public bool ShouldStop { get; set; }
    public int LowSpeedCount { get; set; }

    public void ResetLowSpeedCount()
    {
        LowSpeedCount = 0;
    }
}
