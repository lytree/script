#!/usr/bin/env dotnet
#:package System.CommandLine@2.0.5
#:package Spectre.Console@*
#:package Spectre.Console.Ansi@*
#:package CliWrap@3.6.0

using System;
using System.IO;
using System.CommandLine;
using Spectre.Console;
using CliWrap;
using CliWrap.EventStream;

var pathOption = new Option<string>("--path")
{
    Description = "Path to .mov file or directory containing .mov files",
    Required = true
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
        var cmd = Cli.Wrap(ffmpeg)
            .WithArguments($"-i \"{movFile}\" -c copy \"{mp4File}\"")
            .WithValidation(CommandResultValidation.None);

        await foreach (var cmdEvent in cmd.ListenAsync())
        {
            switch (cmdEvent)
            {
                case StandardOutputCommandEvent stdOut:
                    AnsiConsole.WriteLine(stdOut.Text);
                    break;
                case StandardErrorCommandEvent stdErr:
                    AnsiConsole.WriteLine(stdErr.Text);
                    break;
            }
        }

        var result = await cmd.ExecuteAsync();

        if (result.ExitCode == 0)
        {
            AnsiConsole.Markup($"[bold green]Success: {mp4File}[/]");
        }
        else
        {
            AnsiConsole.Markup($"[bold red]Error: FFmpeg exited with code {result.ExitCode}[/]");
        }
    }
    catch (Exception ex)
    {
        AnsiConsole.Markup($"[bold red]Error: {ex.Message}[/]");
    }
}

string FindFfmpeg()
{
    string pathEnv = Environment.GetEnvironmentVariable("PATH");
    if (!string.IsNullOrEmpty(pathEnv))
    {
        string[] paths = pathEnv.Split(Path.PathSeparator);
        foreach (string p in paths)
        {
            string ffmpegPath = Path.Combine(p, "ffmpeg");
            if (File.Exists(ffmpegPath))
            {
                return ffmpegPath;
            }
        }
    }

    string currentDir = Directory.GetCurrentDirectory();
    string ffmpegCurrent = Path.Combine(currentDir, "ffmpeg");
    if (File.Exists(ffmpegCurrent))
    {
        return ffmpegCurrent;
    }

    string parentDir = Directory.GetParent(currentDir)?.FullName;
    if (!string.IsNullOrEmpty(parentDir))
    {
        string ffmpegParent = Path.Combine(parentDir, "ffmpeg");
        if (File.Exists(ffmpegParent))
        {
            return ffmpegParent;
        }
    }

    return null;
}