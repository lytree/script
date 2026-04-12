# AGENTS.md

## Repository Type
Standalone C# script collection - **no .csproj/.sln build files**. Scripts run directly via `dotnet script` or as single-file executables.

## Constraints
- **Never create .csproj or .sln files** - use file-based C# scripts only
- Run scripts via: `dotnet run Path/To/Script.cs`
- Or compile manually: `csc script.cs && ./script.exe` (Windows)

## Directory Structure
- `data/` - Default storage location for generated data
- `template/` - CLI templates (copy to create new CLI tools)
- `src/tdl/` - Telegram DL scripts (TdlDownload, TdlForward, etc.)
- `src/Downloader/` - M3u8 video downloader
- `src/Avalonia/` - GUI apps (ScottPlot, aval)
- `src/Helper/` - Utilities (Json, Images, Bytes, DateTime, Plot)
- `src/html/` - Playwright HTML utilities
- `src/Screen/` - Screen capture utilities
- `src/Http/` - HTTP API utilities
- `src/AI/` - AI utilities
- `src/` (root) - Miscellaneous scripts (clearn, rename, FixImages, etc.)

## Notes
- Directory.Build.props suppresses many nullable warnings (CS8600-8625, etc.)
- Some .gitignore entries reference project-specific paths that no longer exist