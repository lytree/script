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
var rootCommand = new RootCommand("使用 ffmpeg 无损转换 mov 到 mp4");

// 添加路径参数
var pathOption = new Option<string>("--path") { Required = true };
var ffmpegPathOption = new Option<string>("--ffmpeg") { DefaultValueFactory = (res) => "ffmpeg" };
rootCommand.Options.Add(pathOption);
rootCommand.Options.Add(ffmpegPathOption);


// 解析命令行参数
var root = rootCommand.Parse(args);
await ConvertMovToMp4(root.GetValue(pathOption), root.GetValue(ffmpegPathOption));
// 转换 mov 到 mp4
async Task ConvertMovToMp4(string path, string ffmpegPath = null)
{
    // 检查 ffmpeg 路径
    string ffmpeg = ffmpegPath ?? FindFfmpeg();
    if (string.IsNullOrEmpty(ffmpeg))
    {
        AnsiConsole.WriteLine("错误: 找不到 ffmpeg 可执行文件，请使用 --ffmpeg 参数指定路径");
        return;
    }

    AnsiConsole.WriteLine($"使用 ffmpeg: {ffmpeg}");

    // 检查路径是否存在
    if (!Directory.Exists(path) && !File.Exists(path))
    {
        AnsiConsole.WriteLine($"错误: 路径不存在: {path}");
        return;
    }

    // 如果是文件，直接转换
    if (File.Exists(path))
    {
        if (Path.GetExtension(path).ToLower() == ".mov")
        {
            await ConvertSingleFile(ffmpeg, path);
        }
        else
        {
            AnsiConsole.WriteLine($"错误: 只支持 .mov 文件: {path}");
        }
        return;
    }

    // 如果是目录，遍历所有 .mov 文件（包括所有子目录）
    if (Directory.Exists(path))
    {
        var movFiles = Directory.GetFiles(path, "*.mov", SearchOption.AllDirectories);
        AnsiConsole.WriteLine($"找到 {movFiles.Length} 个 .mov 文件");

        foreach (var movFile in movFiles)
        {
            await ConvertSingleFile(ffmpeg, movFile);
        }
    }
}

// 转换单个文件
async Task ConvertSingleFile(string ffmpeg, string movFile)
{
    string mp4File = Path.ChangeExtension(movFile, ".mp4");

    // 检查输出文件是否已存在
    if (File.Exists(mp4File))
    {
        AnsiConsole.WriteLine($"跳过: 输出文件已存在: {mp4File}");
        return;
    }

    AnsiConsole.WriteLine($"转换: {movFile} -> {mp4File}");
    AnsiConsole.WriteLine("开始转换...");

    try
    {
        // 使用 CliWrap 执行 ffmpeg 命令
        var cmd = Cli.Wrap(ffmpeg)
            .WithArguments($"-i \"{movFile}\" -c copy \"{mp4File}\"")
            .WithValidation(CommandResultValidation.None);

        // 实时读取输出
        await foreach (var cmdEvent in cmd.ListenAsync())
        {
            switch (cmdEvent)
            {
                case StandardOutputCommandEvent stdOut:
                    AnsiConsole.WriteLine($"ffmpeg: {stdOut.Text}");
                    break;
                case StandardErrorCommandEvent stdErr:
                    AnsiConsole.WriteLine($"ffmpeg: {stdErr.Text}");
                    break;
            }
        }

        // 等待命令完成并获取结果
        var result = await cmd.ExecuteAsync();

        if (result.ExitCode == 0)
        {
            AnsiConsole.WriteLine($"成功: 转换完成: {mp4File}");
        }
        else
        {
            AnsiConsole.WriteLine($"错误: 转换失败，退出代码: {result.ExitCode}");
        }
    }
    catch (Exception ex)
    {
        AnsiConsole.WriteLine($"错误: {ex.Message}");
    }
}

// 查找 ffmpeg 可执行文件
string FindFfmpeg()
{
    // 检查系统环境变量 PATH
    string pathEnv = Environment.GetEnvironmentVariable("PATH");
    if (!string.IsNullOrEmpty(pathEnv))
    {
        string[] paths = pathEnv.Split(Path.PathSeparator);
        foreach (string path in paths)
        {
            string ffmpegPath = Path.Combine(path, "ffmpeg.exe");
            if (File.Exists(ffmpegPath))
            {
                return ffmpegPath;
            }
        }
    }

    // 检查当前目录
    string currentDir = Directory.GetCurrentDirectory();
    string ffmpegCurrent = Path.Combine(currentDir, "ffmpeg.exe");
    if (File.Exists(ffmpegCurrent))
    {
        return ffmpegCurrent;
    }

    // 检查上级目录
    string parentDir = Directory.GetParent(currentDir)?.FullName;
    if (!string.IsNullOrEmpty(parentDir))
    {
        string ffmpegParent = Path.Combine(parentDir, "ffmpeg.exe");
        if (File.Exists(ffmpegParent))
        {
            return ffmpegParent;
        }
    }

    return null;
}