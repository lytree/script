# AGENTS.md

## Repository Type
Standalone C# script collection - **no .csproj/.sln build files**. Scripts run directly via dotnet-script.

## Constraints
- **Never create .csproj or .sln files** - use file-based C# scripts only
- Run scripts via: `dotnet run Path/To/Script.cs` (not `dotnet script`)
- Each script declares its own dependencies via `#:package PackageName@*` directive

## Directory Structure
- `src/` (root) - Miscellaneous scripts (clearn, rename, FixImages, etc.)
- `src/tdl/` - Telegram DL scripts (TdlDownload, TdlForward, etc.)
- `src/Downloader/` - M3u8 video downloader
- `src/Avalonia/` - GUI apps (ScottPlot, aval)
- `src/Helper/` - Utilities (Json, Images, Bytes, DateTime, Plot)
- `src/html/` - Playwright HTML utilities
- `src/Screen/` - Screen capture utilities
- `src/Http/` - HTTP API utilities
- `src/AI/` - AI utilities
- `data/` - Default storage for generated data
- `template/cli.cs` - CLI template (copy to create new tools)

## Notes
- Dependencies in scripts use dotnet-script directives at the top: `#:package`, `#:property`
- Directory.Build.props suppresses nullable warnings (CS8600-8625, etc.)
- No test framework or CI - run scripts directly to verify