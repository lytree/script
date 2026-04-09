# M3u8 Downloader Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a C# file-based-apps script that downloads video from m3u8 URLs with concurrent .ts segment downloading, AES-128 decryption, and FFmpeg MP4 merging.

**Architecture:** Single file `src/M3u8Downloader.cs` using `#:package` directives. HttpClient with custom headers fetches m3u8, parses segments and encryption keys, downloads .ts segments concurrently with SemaphoreSlim, decrypts AES-128-CBC if needed, then merges via FFmpeg concat demuxer into MP4.

**Tech Stack:** C# script (file-based-apps), System.Net.Http, System.Security.Cryptography, System.Diagnostics.Process (FFmpeg)

---

### Task 1: Create script file with data models and configuration

**Files:**
- Create: `src/M3u8Downloader.cs`

- [ ] **Step 1: Write the script skeleton with records and config variables**

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

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
```

- [ ] **Step 2: Verify file exists and compiles conceptually**

Run: `type src\M3u8Downloader.cs`
Expected: file content shown

---

### Task 2: Implement M3u8 fetching with custom headers

**Files:**
- Modify: `src/M3u8Downloader.cs`

- [ ] **Step 1: Add HttpClient factory and FetchM3u8 method after the records**

```csharp
HttpClient CreateHttpClient()
{
    var handler = new HttpClientHandler();
    var client = new HttpClient(handler);
    foreach (var (key, value) in headers)
    {
        client.DefaultRequestHeaders.Add(key, value);
    }
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    return client;
}

async Task<string> FetchM3u8Async(HttpClient client, string url)
{
    var response = await client.GetAsync(url);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStringAsync();
}
```

---

### Task 3: Implement M3u8 parser

**Files:**
- Modify: `src/M3u8Downloader.cs`

- [ ] **Step 1: Add ParseM3u8 method and URL resolver**

```csharp
string ResolveUrl(string baseUrl, string relativeUrl)
{
    if (relativeUrl.StartsWith("http://") || relativeUrl.StartsWith("https://"))
        return relativeUrl;

    var baseUri = new Uri(baseUrl);
    if (relativeUrl.StartsWith("/"))
        return $"{baseUri.Scheme}://{baseUri.Host}{relativeUrl}";

    var parentPath = baseUri.AbsolutePath.Substring(0, baseUri.AbsolutePath.LastIndexOf('/') + 1);
    return new Uri(baseUri, parentPath + relativeUrl).ToString();
}

M3u8Info ParseM3u8(string content, string m3u8Url)
{
    var segments = new List<TsSegment>();
    AesKeyInfo? keyInfo = null;
    double? currentDuration = null;
    var keyMethod = "";
    var keyUrl = "";
    string? keyIv = null;

    foreach (var rawLine in content.Split('\n'))
    {
        var line = rawLine.Trim();

        if (line.StartsWith("#EXT-X-KEY:"))
        {
            var methodMatch = Regex.Match(line, @"METHOD=([^,]+)");
            var uriMatch = Regex.Match(line, @"URI=""([^""]+)""");
            var ivMatch = Regex.Match(line, @"IV=0x([0-9a-fA-F]+)");

            keyMethod = methodMatch.Success ? methodMatch.Groups[1].Value : "";
            keyUrl = uriMatch.Success ? ResolveUrl(m3u8Url, uriMatch.Groups[1].Value) : "";
            keyIv = ivMatch.Success ? ivMatch.Groups[1].Value : null;

            if (keyMethod == "AES-128" && !string.IsNullOrEmpty(keyUrl))
            {
                keyInfo = new AesKeyInfo(keyMethod, keyUrl, keyIv);
            }
        }
        else if (line.StartsWith("#EXTINF:"))
        {
            var durationStr = line.Substring(8).TrimEnd(',');
            if (double.TryParse(durationStr, out var dur))
                currentDuration = dur;
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
```

---

### Task 4: Implement AES-128 decryption

**Files:**
- Modify: `src/M3u8Downloader.cs`

- [ ] **Step 1: Add FetchAesKeyAsync and DecryptSegment methods**

```csharp
async Task<byte[]> FetchAesKeyAsync(HttpClient client, AesKeyInfo keyInfo)
{
    var response = await client.GetAsync(keyInfo.KeyUrl);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsByteArrayAsync();
}

byte[] DecryptSegment(byte[] encryptedData, byte[] key, int segmentIndex, string? ivHex)
{
    byte[] iv;
    if (!string.IsNullOrEmpty(ivHex))
    {
        iv = new byte[16];
        for (var i = 0; i < 16; i++)
            iv[i] = Convert.ToByte(ivHex.Substring(i * 2, 2), 16);
    }
    else
    {
        iv = new byte[16];
        var indexBytes = BitConverter.GetBytes(segmentIndex);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(indexBytes);
        Array.Copy(indexBytes, 0, iv, 12, 4);
    }

    using var aes = Aes.Create();
    aes.Key = key;
    aes.IV = iv;
    aes.Mode = CipherMode.CBC;
    aes.Padding = PaddingMode.PKCS7;

    using var decryptor = aes.CreateDecryptor();
    return decryptor.TransformFinalBlock(encryptedData, 0, encryptedData.Length);
}
```

---

### Task 5: Implement concurrent segment downloading

**Files:**
- Modify: `src/M3u8Downloader.cs`

- [ ] **Step 1: Add DownloadSegmentsAsync method with retry logic**

```csharp
async Task DownloadSegmentsAsync(HttpClient client, M3u8Info m3u8Info, string tempDir, byte[]? aesKey)
{
    var semaphore = new SemaphoreSlim(concurrency);
    var total = m3u8Info.Segments.Count;
    var completed = 0;
    var failedSegments = new List<int>();

    var tasks = m3u8Info.Segments.Select(async segment =>
    {
        await semaphore.WaitAsync();
        try
        {
            var success = false;
            for (var retry = 0; retry < 3; retry++)
            {
                try
                {
                    var data = await client.GetByteArrayAsync(segment.Url);
                    if (aesKey != null && m3u8Info.KeyInfo != null)
                    {
                        data = DecryptSegment(data, aesKey, segment.Index, m3u8Info.KeyInfo.Iv);
                    }
                    var filePath = Path.Combine(tempDir, $"{segment.Index:D5}.ts");
                    await File.WriteAllBytesAsync(filePath, data);
                    Interlocked.Increment(ref completed);
                    Console.WriteLine($"[{completed}/{total}] Downloaded {segment.Index:D5}.ts");
                    success = true;
                    break;
                }
                catch (Exception ex) when (retry < 2)
                {
                    var delay = (int)Math.Pow(2, retry) * 1000;
                    Console.WriteLine($"Retry {retry + 1}/3 for segment {segment.Index}: {ex.Message}. Waiting {delay}ms...");
                    await Task.Delay(delay);
                }
            }

            if (!success)
            {
                failedSegments.Add(segment.Index);
                Console.WriteLine($"FAILED: segment {segment.Index} after 3 retries");
            }
        }
        finally
        {
            semaphore.Release();
        }
    });

    await Task.WhenAll(tasks);

    if (failedSegments.Count > 0)
    {
        throw new Exception($"Failed to download segments: {string.Join(", ", failedSegments)}");
    }
}
```

---

### Task 6: Implement concat list and FFmpeg merge

**Files:**
- Modify: `src/M3u8Downloader.cs`

- [ ] **Step 1: Add WriteConcatList and MergeWithFFmpegAsync methods**

```csharp
async Task WriteConcatListAsync(M3u8Info m3u8Info, string tempDir)
{
    var listPath = Path.Combine(tempDir, "filelist.txt");
    var sb = new StringBuilder();
    foreach (var segment in m3u8Info.Segments)
    {
        var filePath = Path.Combine(tempDir, $"{segment.Index:D5}.ts");
        sb.AppendLine($"file '{filePath.Replace("'", "'\\''")}'");
    }
    await File.WriteAllTextAsync(listPath, sb.ToString());
}

async Task MergeWithFFmpegAsync(string tempDir, string outputPath)
{
    var listPath = Path.Combine(tempDir, "filelist.txt");
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = ffmpegPath,
        Arguments = $"-y -f concat -safe 0 -i \"{listPath}\" -c copy \"{outputPath}\"",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    using var process = System.Diagnostics.Process.Start(psi)!;

    var stderrTask = process.StandardError.ReadToEndAsync();
    var stdoutTask = process.StandardOutput.ReadToEndAsync();

    await process.WaitForExitAsync();

    var stderr = await stderrTask;

    if (process.ExitCode != 0)
    {
        Console.WriteLine($"FFmpeg error:\n{stderr}");
        throw new Exception($"FFmpeg exited with code {process.ExitCode}. Temp files preserved at: {tempDir}");
    }

    Console.WriteLine($"Merged successfully: {outputPath}");
}
```

---

### Task 7: Implement main orchestration and cleanup

**Files:**
- Modify: `src/M3u8Downloader.cs`

- [ ] **Step 1: Add the main execution flow after all method definitions**

```csharp
var tempDir = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".",
    $".tmp_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"
);
Directory.CreateDirectory(tempDir);

try
{
    using var client = CreateHttpClient();

    Console.WriteLine($"Fetching m3u8: {m3u8Url}");
    var m3u8Content = await FetchM3u8Async(client, m3u8Url);

    var m3u8Info = ParseM3u8(m3u8Content, m3u8Url);
    Console.WriteLine($"Found {m3u8Info.Segments.Count} segments, encrypted: {m3u8Info.KeyInfo != null}");

    byte[]? aesKey = null;
    if (m3u8Info.KeyInfo != null)
    {
        Console.WriteLine($"Fetching AES key from: {m3u8Info.KeyInfo.KeyUrl}");
        aesKey = await FetchAesKeyAsync(client, m3u8Info.KeyInfo);
    }

    Console.WriteLine($"Downloading {m3u8Info.Segments.Count} segments (concurrency: {concurrency})...");
    await DownloadSegmentsAsync(client, m3u8Info, tempDir, aesKey);

    Console.WriteLine("Writing concat list...");
    await WriteConcatListAsync(m3u8Info, tempDir);

    Console.WriteLine("Merging with FFmpeg...");
    await MergeWithFFmpegAsync(tempDir, output);

    Console.WriteLine("Done!");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine($"Temp files at: {tempDir}");
    return;
}

try
{
    Directory.Delete(tempDir, true);
    Console.WriteLine("Temp files cleaned up.");
}
catch
{
    Console.WriteLine($"Warning: could not delete temp dir: {tempDir}");
}
```

---

### Task 8: Verify the complete script

- [ ] **Step 1: Read the final file to verify completeness**

Run: `type src\M3u8Downloader.cs`
Expected: complete script with all methods and main flow

- [ ] **Step 2: Commit**

```bash
git add src/M3u8Downloader.cs
git commit -m "feat: add m3u8 video downloader with AES-128 and FFmpeg merge"
```
