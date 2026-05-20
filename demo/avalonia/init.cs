#!/usr/bin/dotnet run

#:sdk Microsoft.NET.Sdk.Web

#:package CommunityToolkit.Mvvm@8.*
#:package Avalonia.Desktop@12.*
#:package Avalonia.Fonts.Inter@12.*
#:package Avalonia.Themes.Fluent@12.*
#:package Avalonia.Markup.Declarative@12.*
#:package SkiaSharp@3.*
#:property PublishAot=false
#:property OutputType=WinExe

#if OS_LINUX
#:package SkiaSharp.NativeAssets.Linux.NoDependencies@3.*
#endif



using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Declarative;
using Avalonia.Themes.Fluent;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lemon.Hosting.AvaloniauiDesktop;
using SkiaSharp;
using Avalonia.Controls.ApplicationLifetimes;

[assembly: SupportedOSPlatform("windows")]
[assembly: SupportedOSPlatform("linux")]
[assembly: SupportedOSPlatform("macos")]
#region Avalonia 加载字体

static string GetSafeFont()
{
    var installed = SKFontManager.Default.GetFontFamilies();

    // 优先搜索 Linux 常用开源字体
    string[] linuxFonts = ["DejaVu Sans", "Liberation Sans", "Noto Sans", "FreeSans"];

    foreach (var font in linuxFonts)
    {
        if (installed.Contains(font)) return font;
    }

    // 如果都没有，返回第一个可用的字体
    return installed.Length > 0 ? installed[0] : "sans-serif";
}
#endregion



#region 初始化 Avalonia 环境

var lifetime = new ClassicDesktopStyleApplicationLifetime { Args = args, ShutdownMode = ShutdownMode.OnLastWindowClose };

AppBuilder.Configure<Application>()
    .UsePlatformDetect()
    .AfterSetup(b => b.Instance?.Styles.Add(new FluentTheme()))
    .SetupWithLifetime(lifetime);


#endregion