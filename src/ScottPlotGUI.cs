#:package Avalonia@11.3.10
#:package Avalonia.Desktop@11.3.10
#:package Avalonia.Fonts.Inter@11.3.10
#:package Avalonia.Diagnostics@11.3.10
#:package Avalonia.Themes.Fluent@11.3.10
#:package Avalonia.Markup.Declarative@11.3.7-beta05
#:package ScottPlot@5.1.57
#:package ScottPlot.Avalonia@5.1.57

#if OS_LINUX
#:package SkiaSharp@3.119.1
#:package SkiaSharp.NativeAssets.Linux@3.119.1
#endif



using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Markup.Declarative;
using ScottPlot;
using ScottPlot.Avalonia;
using Avalonia.Media;


var lifetime = new ClassicDesktopStyleApplicationLifetime { Args = args, ShutdownMode = ShutdownMode.OnLastWindowClose };

AppBuilder.Configure<Application>()
    .UsePlatformDetect()
    .AfterSetup(b => b.Instance?.Styles.Add(new FluentTheme()))
    .SetupWithLifetime(lifetime);





decimal? counter = 0;

lifetime.MainWindow = new Window().Height(1024).Width(800).Title("Avalonia MVU Template")
    .Content(new FuncComponent<decimal?>(counter,
        _ => BuildAnalysisDashboard(out AvaPlot plot1, out AvaPlot plot2)));










lifetime.Start(args);



/// <summary>
/// 构建分析仪表盘主界面
/// </summary>
Grid BuildAnalysisDashboard(out AvaPlot wavePlot, out AvaPlot specPlot)
{


    // --- 调用你之前的单元封装方法生成两个区域 ---
    // 假设 CreateChartUnit 已经定义在类中
    var waveSection = CreateChartUnit(out wavePlot, "时域波形", ScottPlot.Colors.DodgerBlue);
    var specSection = CreateChartUnit(out specPlot, "频域频谱", ScottPlot.Colors.Crimson);
    var rightTool = CreateChartTools(wavePlot, specPlot);
    // --- 绑定跨图表联动逻辑 (ECharts 风格联动) ---
    // BindSynchronization(wavePlot, specPlot);

    // --- 将它们放入主布局 ---
    var mainLayout = new Grid()
        .Rows("*, *")
        .Children(
            waveSection.Row(0).Margin(5),
            specSection.Row(1).Margin(5)
        );
    return new Grid().Cols("*,Auto").Children(
        mainLayout.Col(0),
        rightTool.Col(1).Width(50)
    );

}

Border CreateChartTools(AvaPlot wavePlot, AvaPlot specPlot)
{
    var Border = new Border();
    return Border;
}

/// <summary>
/// 创建一个带有独立工具栏的图表单元
/// </summary>
/// <param name="plot">输出图表引用，用于后续数据操作</param>
/// <param name="title">工具栏显示的标题</param>
/// <param name="themeColor">工具栏的主题颜色</param>
/// <param name="customButtons">可选：针对该图表特有的按钮</param>
/// <returns>封装好的 Grid 容器</returns>
Grid CreateChartUnit(out AvaPlot plot, string title, ScottPlot.Color themeColor, List<Button>? customButtons = default)
{
    var newPlot = new AvaPlot();
    plot = newPlot;

    // 1. 配置基础样式 (针对 Linux 字体加固)
    newPlot.Plot.Axes.Color(themeColor);
    string safeFont = "DejaVu Sans";
    newPlot.Plot.Axes.Bottom.TickLabelStyle.FontName = safeFont;
    newPlot.Plot.Axes.Left.TickLabelStyle.FontName = safeFont;

    // --- 2. 创建第一行的拆分区域 (1行 2列) ---
    var topSplitGrid = new Grid()
    {
        ColumnDefinitions = new ColumnDefinitions("*, *"), // 左右平分
        Height = 50 // 可以设定一个固定高度
    };


    // 左上角：例如放置设备信息
    var topLeftInfo = new Border()
    {
        Background = Brushes.LightGray,
        CornerRadius = new CornerRadius(5),
        Margin = new Thickness(5),
        Child = new TextBlock { Text = "设备状态: 运行中", Foreground = Brushes.Black, Margin = new Thickness(10) }
    };

    // 右上角：例如放置实时数值
    var topRightInfo = new Border()
    {
        Background = Brushes.LightSlateGray,
        CornerRadius = new CornerRadius(5),
        Margin = new Thickness(5),
        Child = new TextBlock { Text = "当前转速: 1500 RPM", Foreground = Brushes.Black, Margin = new Thickness(10) }
    };

    topSplitGrid.Children.Add(topLeftInfo.Col(0));
    topSplitGrid.Children.Add(topRightInfo.Col(1));
    // 3. 组合布局
    var unitGrid = new Grid().Rows("Auto,*").Cols("*");
    unitGrid.Children.Add(topSplitGrid.Row(0));
    unitGrid.Children.Add(newPlot.Row(1));

    return unitGrid;
}


// 封装一个创建图标按钮的通用方法
Button CreateIconButton(string svg, string toolTip)
{
    var btn = new Button
    {
        // Background = Brushes.Transparent, // 透明背景
        BorderBrush = Brushes.Transparent,
        Padding = new Thickness(5),
        Height = 32,
        Width = 32,
        Content = new PathIcon
        {
            // 核心：解析 SVG 路径字符串
            Data = StreamGeometry.Parse(svg),
        }

    };
    // 设置悬浮提示
    ToolTip.SetTip(btn, toolTip);

    // 增加一个简单的悬浮效果，弥补没有文字的空洞感
    btn.PointerEntered += (s, e) => btn.Background = SolidColorBrush.Parse(ScottPlot.Colors.GhostWhite.ToHex());
    btn.PointerExited += (s, e) => btn.Background = Brushes.Transparent;

    return btn;
}