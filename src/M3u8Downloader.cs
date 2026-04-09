using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

var m3u8Url = "https://example.com/video/index.m3u8";
var output = "output.mp4";
var concurrency = 8;
var headers = new Dictionary<string, string>
{
    { "Referer", "https://example.com" },
};
var ffmpegPath = "ffmpeg";

record M3u8Info(List<TsSegment> Segments, AesKeyInfo? KeyInfo);
record TsSegment(int Index, string Url, double? Duration);
record AesKeyInfo(string Method, string KeyUrl, string? Iv);

HttpClient CreateHttpClient()
{
    var handler = new HttpClientHandler();
    var client = new HttpClient(handler);
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
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
        basePath = basePath.Substring(0, lastSlash + 1);
    }
    return new Uri(baseUri, basePath + relativeUrl).ToString();
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
            var durationStr = line.Substring("#EXTINF:".Length);
            var commaIdx = durationStr.IndexOf(',');
            if (commaIdx >= 0)
            {
                durationStr = durationStr.Substring(0, commaIdx);
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

async Task DownloadSegmentsAsync(HttpClient client, M3u8Info m3u8Info, string tempDir, byte[]? aesKey)
{
    var semaphore = new SemaphoreSlim(concurrency);
    var failedSegments = new List<int>();
    var completed = 0;
    var total = m3u8Info.Segments.Count;
    var lockObj = new object();

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
                        Console.WriteLine($"[{completed}/{total}] Downloaded {fileName}");
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

    await Task.WhenAll(tasks);

    if (failedSegments.Count > 0)
    {
        throw new Exception($"Failed to download segments: {string.Join(", ", failedSegments)}");
    }
}

async Task WriteConcatListAsync(M3u8Info m3u8Info, string tempDir)
{
    var listPath = Path.Combine(tempDir, "filelist.txt");
    using var writer = new StreamWriter(listPath, false, Encoding.UTF8);
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
        Arguments = $"-y -f concat -safe 0 -i \"{listPath}\" -c copy \"{outputPath}\"",
        UseShellExecute = false,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    using var process = new Process { StartInfo = psi };
    process.Start();

    var stderrTask = process.StandardError.ReadToEndAsync();
    var stderr = await stderrTask;

    await process.WaitForExitAsync();

    Console.Write(stderr);

    if (process.ExitCode != 0)
    {
        throw new Exception($"FFmpeg exited with code {process.ExitCode}");
    }
}

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
    var m3u8Content = await FetchM3u8Async(client, m3u8Url);
    var m3u8Info = ParseM3u8(m3u8Content, m3u8Url);

    Console.WriteLine($"Found {m3u8Info.Segments.Count} segments");

    byte[]? aesKey = null;
    if (m3u8Info.KeyInfo != null && m3u8Info.KeyInfo.Method == "AES-128")
    {
        aesKey = await FetchAesKeyAsync(client, m3u8Info.KeyInfo);
        Console.WriteLine("AES-128 encryption detected, key fetched");
    }

    await DownloadSegmentsAsync(client, m3u8Info, tempDir, aesKey);
    await WriteConcatListAsync(m3u8Info, tempDir);
    await MergeWithFFmpegAsync(tempDir, output);

    Console.WriteLine($"Done: {output}");
}
finally
{
    if (Directory.Exists(tempDir))
    {
        Directory.Delete(tempDir, true);
    }
}
