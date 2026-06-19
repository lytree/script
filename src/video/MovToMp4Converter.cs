#!/usr/bin/env dotnet
#:package System.CommandLine@2.*
#:package Spectre.Console@*
#:package Spectre.Console.Ansi@*

using System;
using System.Diagnostics;
using System.IO;
using System.CommandLine;
using Spectre.Console;

var pathOption = new Option<string>("--path")
{
    Description = "Path to .mov file or directory containing .mov files",
    DefaultValueFactory = (res) => @"C:\Users\hiyan\Downloads\Telegram Desktop"
};

var ffmpegPathOption = new Option<string>("--ffmpeg")
{
    Description = "Path to ffmpeg executable",
    DefaultValueFactory = (res) => "ffmpeg"
};

var rootCommand = new RootCommand("Convert MOV to MP4 using ffmpeg")
{
    pathOption,
    ffmpegPathOption
};

rootCommand.SetAction(async (ParseResult parseResult) =>
{
    var path = parseResult.GetValue(pathOption);
    var ffmpegPath = parseResult.GetValue(ffmpegPathOption);

    await ConvertMovToMp4(path, ffmpegPath);
});

await rootCommand.Parse(args).InvokeAsync();

async Task ConvertMovToMp4(string path, string ffmpegPath)
{
    string ffmpeg = ffmpegPath ?? FindFfmpeg();
    if (string.IsNullOrEmpty(ffmpeg))
    {
        AnsiConsole.Markup("[bold red]Error: ffmpeg not found, use --ffmpeg to specify path[/]");
        return;
    }

    AnsiConsole.Markup($"[green]Using ffmpeg: {ffmpeg}[/]");

    if (!Directory.Exists(path) && !File.Exists(path))
    {
        AnsiConsole.Markup($"[bold red]Error: Path not found: {path}[/]");
        return;
    }

    if (File.Exists(path))
    {
        if (Path.GetExtension(path).ToLower() == ".mov")
        {
            await ConvertSingleFile(ffmpeg, path);
        }
        else
        {
            AnsiConsole.Markup($"[bold red]Error: Only .mov files supported: {path}[/]");
        }
        return;
    }

    if (Directory.Exists(path))
    {
        var movFiles = Directory.GetFiles(path, "*.mov", SearchOption.AllDirectories);
        AnsiConsole.Markup($"[cyan]Found {movFiles.Length} .mov files[/]");

        foreach (var movFile in movFiles)
        {
            await ConvertSingleFile(ffmpeg, movFile);
        }
    }
}

async Task ConvertSingleFile(string ffmpeg, string movFile)
{
    string mp4File = Path.ChangeExtension(movFile, ".mp4");

    if (File.Exists(mp4File))
    {
        AnsiConsole.Markup($"[yellow]Skip: Output already exists: {mp4File}[/]");
        return;
    }

    AnsiConsole.Markup($"[cyan]Converting: {movFile} -> {mp4File}[/]");

    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-i \"{movFile}\" -c copy \"{mp4File}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                AnsiConsole.WriteLine(e.Data);
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                AnsiConsole.WriteLine(e.Data);
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        if (process.ExitCode == 0)
        {
            AnsiConsole.Markup($"[bold green]Success: {mp4File}[/]");
        }
        else
        {
            AnsiConsole.Markup($"[bold red]Error: FFmpeg exited with code {process.ExitCode}[/]");
        }
    }
    catch (Exception ex)
    {
        AnsiConsole.Markup($"[bold red]Error: {ex.Message}[/]");
    }
}

string FindFfmpeg()
{
    string exeName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    string pathEnv = Environment.GetEnvironmentVariable("PATH");
    if (!string.IsNullOrEmpty(pathEnv))
    {
        string[] paths = pathEnv.Split(Path.PathSeparator);
        foreach (string p in paths)
        {
            string ffmpegPath = Path.Combine(p, exeName);
            if (File.Exists(ffmpegPath))
            {
                return ffmpegPath;
            }
        }
    }

    string currentDir = Directory.GetCurrentDirectory();
    string ffmpegCurrent = Path.Combine(currentDir, exeName);
    if (File.Exists(ffmpegCurrent))
    {
        return ffmpegCurrent;
    }

    string parentDir = Directory.GetParent(currentDir)?.FullName;
    if (!string.IsNullOrEmpty(parentDir))
    {
        string ffmpegParent = Path.Combine(parentDir, exeName);
        if (File.Exists(ffmpegParent))
        {
            return ffmpegParent;
        }
    }

    return null;
}