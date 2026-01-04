#:package Avalonia@11.3.10
#:package Avalonia.Desktop@11.3.10
#:package Avalonia.Themes.Fluent@11.3.10
#:package Avalonia.Markup.Declarative@11.3.7-beta05
#:package ScottPlot@5.1.57
#:package ScottPlot.Avalonia@5.1.57
#:package Avalonia.Skia@11.3.10
#:package SkiaSharp@3.119.1
#:package SkiaSharp.NativeAssets.Linux@3.119.1
#:package SkiaSharp.NativeAssets.Linux.NoDependencies@3.119.1
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Data;
using Avalonia.Themes.Fluent;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Declarative;
using ScottPlot.Avalonia;
using ScottPlot;
using SkiaSharp;
using Avalonia.Input;
using ScottPlot.Plottables;








#region Avalonia 加载字体
using var simheiStream = File.OpenRead("./simhei.ttf");
using var simsunStream = File.OpenRead("./simsun.ttc");
var simhei = SKTypeface.FromStream(simheiStream);
var simsun = SKTypeface.FromStream(simsunStream);
int fontCount = SKFontManager.Default.FontFamilies.Count();
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



#region 画图工具
// --- 定义一个生成“带工具栏图表”的辅助函数 ---
Grid CreateChartWithToolbars(out AvaPlot plot, List<(double[], double[])> datas, string title, Action<IPlotMenu?>? func = default)
{



    var currentIndex = 0;
    var newPlot = new AvaPlot()
        .HorizontalAlignment(Avalonia.Layout.HorizontalAlignment.Stretch)
        .VerticalAlignment(Avalonia.Layout.VerticalAlignment.Stretch);




    foreach (var data in datas)
    {
        var scatter = newPlot.Plot.Add.SignalXY(data.Item1, data.Item2);
        scatter.MarkerShape = ScottPlot.MarkerShape.None;
    }

    func?.Invoke(newPlot.Menu);
    var vLine = newPlot.Plot.Add.VerticalLine(0);
    var hLine = newPlot.Plot.Add.HorizontalLine(0);
    var text = newPlot.Plot.Add.Text("", 0, 0);
    vLine.IsVisible = false;
    hLine.IsVisible = false;
    text.IsVisible = false;
    text.LabelFontName = GetSafeFont();
    newPlot.PointerPressed += (s, e) =>
    {
        var pos = newPlot.Plot.GetCoordinates(new Pixel((float)e.GetPosition(newPlot).X, (float)e.GetPosition(newPlot).Y));
        if (e.GetCurrentPoint(newPlot).Properties.IsLeftButtonPressed)
        {
            vLine.X = pos.X;
            hLine.Y = pos.Y;
            text.Location = pos;
            text.LabelText = $"X: {pos.X:0.2f}\nY: {pos.Y:0.2f}";
            vLine.IsVisible = true;
            hLine.IsVisible = true;
            text.IsVisible = true;
            newPlot.Refresh();
        }

    };
    newPlot.KeyDown += (s, e) =>
    {
        int oldIndex = currentIndex;

        // if (e.Key == Key.Left && currentIndex > 0) currentIndex--;
        // else if (e.Key == Key.Right && currentIndex < dataY.Length - 1) currentIndex++;

        // if (currentIndex != oldIndex)
        // {
        //     // 从原始数组中提取精确坐标
        //     double x = dataX[currentIndex]; // 如果是 Signal，x 通常是 currentIndex * period + offset
        //     double y = dataY[currentIndex];

        //     vLine.X = x;
        //     hLine.Y = y;
        //     text.Location = new Coordinates(x, y);
        //     text.LabelText = $"Index: {currentIndex}\nX: {x:0.2f}\nY: {y:0.2f}";

        //     newPlot.Refresh();
        // }
    };
    plot = newPlot; // 传出引用以便后续操作数据
    plot.Plot.Axes.Bottom.TickLabelStyle.FontName = GetSafeFont();
    plot.Plot.Axes.Left.TickLabelStyle.FontName = GetSafeFont();
    plot.Plot.Legend.FontName = GetSafeFont();
    return new Grid()
        .Rows("Auto, *") // 行：顶部工具栏, 绘图区
        .Cols("*, Auto") // 列：绘图区, 右侧工具栏
        .Children(
            // 1. 顶部工具栏 (横跨两列)
            new StackPanel()
                .Row(0).ColSpan(2).Background(Brushes.DarkSlateGray)
                .Orientation(Avalonia.Layout.Orientation.Horizontal).HorizontalAlignment(Avalonia.Layout.HorizontalAlignment.Center)
                .Children(new TextBlock().Text(title).Foreground(Brushes.White).Margin(5)),

            // 2. 绘图区域 (左下角)
            newPlot.Row(1).Col(0).Margin(5),

            // 3. 右侧工具栏 (右下角)
            new StackPanel()
                .Row(1).Col(1).Spacing(5).Margin(5)
                .VerticalAlignment(Avalonia.Layout.VerticalAlignment.Center)
                .Children(
                    new Button().Content("+"),
                    new Button().Content("-"),
                    new Button().Content("R").OnClick(_ =>
                    {
                        newPlot.Plot.Axes.AutoScale();
                        vLine.IsVisible = false;
                        hLine.IsVisible = false;
                        text.IsVisible = false;

                        newPlot.Refresh();
                    })
                )
        );
}

#endregion

#region 右键目录


#endregion

#region 鼠标事件


#endregion



// avaPlot.Plot.Axes.Bottom.TickLabelStyle.FontName = simhei.FamilyName;

ScottPlot.Avalonia.AvaPlot plot1, plot2;




decimal? counter = 0;

// 2. 构建主窗口内容
lifetime.MainWindow = new Window()
    .Width(1280).Height(800)
    .Content(
        new Grid()
            .Rows("*,*") // 关键修改：定义两个等高的行，而非列
            .Children(
                // 左侧图表组合
                CreateChartWithToolbars(out plot1, [(Generate.Consecutive(1000), Generate.RandomWalk(1000))] "波形图").Row(0),

                // 右侧图表组合
                CreateChartWithToolbars(out plot2, [(Generate.Consecutive(1000), Generate.RandomWalk(1000))] "频谱图·").Row(1),

                // --- 底部状态栏 (横跨两列) ---
                new StackPanel()
                    .Row(2).ColSpan(2)
                    .HorizontalAlignment(Avalonia.Layout.HorizontalAlignment.Center)
                    .Children(new TextBlock().Text("底部状态信息"))
            )
    );

plot1.Refresh();
plot2.Refresh();


lifetime.Start(args);
