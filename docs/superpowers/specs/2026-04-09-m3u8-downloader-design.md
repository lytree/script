# M3u8 Downloader Design

## Overview

C# file-based-apps script that downloads video from an m3u8 URL with concurrent .ts segment downloading, AES-128 decryption support, and FFmpeg-based MP4 merging.

## Architecture

Single file: `src/M3u8Downloader.cs`, using `#:package` directives. Zero NuGet dependencies.

### Flow

```
m3u8 URL → download m3u8 text → parse EXT-X-KEY + .ts list
  → concurrent download .ts segments (with AES-128 decryption)
  → generate ffmpeg concat file list
  → call ffmpeg -f concat to merge into MP4
  → cleanup temp .ts files
```

## Data Models

```csharp
record M3u8Info(
    List<TsSegment> Segments,
    AesKeyInfo? KeyInfo
);

record TsSegment(
    int Index,
    string Url,
    double? Duration
);

record AesKeyInfo(
    string Method,       // "AES-128"
    string KeyUrl,       // EXT-X-KEY URI
    string? Iv           // IV hex value, or null (use segment index as IV)
);
```

## Components

1. **FetchM3u8** — download m3u8 text using HttpClient with custom headers
2. **ParseM3u8** — line-by-line parsing to extract `#EXT-X-KEY` and `#EXTINF` + URL. Relative URLs resolved against m3u8 base URL
3. **FetchAesKey** — download 16-byte AES key binary if encrypted
4. **DownloadSegments** — `SemaphoreSlim` controlled concurrency (default 8). Each segment: download → AES-128-CBC decrypt if encrypted (IV = segment index big-endian 16 bytes, or specified IV) → save as `00001.ts`, `00002.ts`...
5. **WriteConcatList** — generate ffmpeg concat format text file `filelist.txt`
6. **MergeWithFFmpeg** — `ffmpeg -f concat -safe 0 -i filelist.txt -c copy output.mp4`
7. **Cleanup** — delete `.tmp_<timestamp>/` directory

## Configuration (top of script)

```csharp
var m3u8Url = "https://example.com/video/index.m3u8";
var output = "output.mp4";
var concurrency = 8;
var headers = new Dictionary<string, string> { ... };
var ffmpegPath = "ffmpeg";  // or full path
```

## Error Handling

- .ts download failure: retry 3 times with exponential backoff (1s, 2s, 4s). If still fails, abort and report failed segment index
- AES key download failure: abort immediately
- FFmpeg merge failure: preserve `.tmp` directory for manual inspection, output ffmpeg stderr
- m3u8 download/parse failure: abort with specific error message

## Progress Display

- Each segment download complete: `[12/86] Downloaded 00012.ts`
- Decryption: no extra display (near-instant)
- FFmpeg merge: forward ffmpeg stdout/stderr to console

## Temp File Strategy

Create `.tmp_<timestamp>/` subdirectory under output directory for .ts segments and concat list. Delete after successful merge.

## AES-128 Decryption Details

- Cipher: AES-128-CBC with PKCS7 padding
- Key: 16-byte binary downloaded from EXT-X-KEY URI
- IV: if EXT-X-KEY specifies `IV=0x...`, parse hex bytes; otherwise use segment sequence number (0-based) as big-endian 16-byte array
- Key and headers from m3u8 are inherited for key URL requests too
