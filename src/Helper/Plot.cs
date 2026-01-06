#:package ScottPlot@5.1.57

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ScottPlot;
using ScottPlot.TickGenerators;
using SkiaSharp;


namespace Helper;

public static partial class Helpers
{
    public static readonly LabelStyle defaultLabelStyle = new()
    {
        FontName = "SimHei",
        FontSize = 18,

    };
    public static readonly DateTimeAutomatic defaultTimeFormat = new()
    {
        LabelFormatter = (dt) => dt.ToString("yyyy-MM-dd")
    };
    public static readonly NumericAutomatic defaultNumberFormat = new()
    {
        LabelFormatter = (dt) => dt.ToString("F3")

    };
    /// <summary>
    /// 时序图
    /// </summary>
    /// <param name="datas"></param>
    /// <returns></returns>
    public static byte[] SequenceChartLine(List<(List<DateTime>, List<double>, string)> datas, int width = 2250, int height = 350)
    {
        Plot plt = new();
        plt.Font.Automatic();
        plt.Font.Set("SimHei");
        // plt.Axes.Bottom.TickLabelStyle = defaultLabelStyle;
        plt.Axes.Bottom.TickGenerator = defaultTimeFormat;
        // plt.Axes.Left.TickLabelStyle = defaultLabelStyle;
        plt.Axes.Left.TickGenerator = defaultNumberFormat;
        foreach (var data in datas)
        {
            var scatter = plt.Add.SignalXY([.. data.Item1.Select(d => d.ToOADate())], [.. data.Item2]);
            scatter.LegendText = data.Item3;
            scatter.MarkerShape = MarkerShape.None;

        }
        return plt.GetImageBytes(width, height, ImageFormat.Png);
    }
    /// <summary>
    /// 趋势图
    /// </summary>
    /// <param name="datas"></param>
    /// <returns></returns>
    public static byte[] TrendChartLine(List<(List<double>, List<double>, string)> datas, int width = 2250, int height = 350)
    {
        Plot plt = new();
        plt.Font.Automatic();
        plt.Font.Set("SimHei");
        // plt.Axes.Bottom.TickLabelStyle = defaultLabelStyle;
        plt.Axes.Bottom.TickGenerator = defaultTimeFormat;
        // plt.Axes.Left.TickLabelStyle = defaultLabelStyle;
        plt.Axes.Left.TickGenerator = defaultNumberFormat;
        foreach (var data in datas)
        {
            var scatter = plt.Add.SignalXY([.. data.Item1], [.. data.Item2]);
            scatter.LegendText = data.Item3;
            scatter.MarkerShape = MarkerShape.None;
        }
        return plt.GetImageBytes(width, height, ImageFormat.Png);
    }
    /// <summary>
    /// 频谱图
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <returns></returns>
    public static byte[] SpectrumChart(List<double> x, List<double> y, int width = 2250, int height = 350)
    {
        Plot plt = new();
        plt.Font.Automatic();
        plt.Font.Set("SimHei");
        plt.Axes.Left.Min = 0;
        plt.Axes.Left.Max = y.Max() * 1.1;
        // plt.Axes.Bottom.TickLabelStyle = defaultLabelStyle;
        plt.Axes.Bottom.TickGenerator = defaultNumberFormat;
        // plt.Axes.Left.TickLabelStyle = defaultLabelStyle;
        plt.Axes.Left.TickGenerator = defaultNumberFormat;

        var scatter = plt.Add.SignalXY([.. x], [.. y], color: new(System.Drawing.Color.FromArgb(61, 119, 255)));
        scatter.MarkerShape = MarkerShape.None;
        return plt.GetImageBytes(width, height, ImageFormat.Png);
    }

    /// <summary>
    /// 波形图
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <returns></returns>
    public static byte[] WaveformChart(List<double> x, List<double> y, int width = 2250, int height = 350)
    {
        Plot plt = new(); plt.Font.Automatic(); plt.Font.Set("SimHei");
        // plt.Axes.Bottom.TickLabelStyle = defaultLabelStyle;
        plt.Axes.Bottom.TickGenerator = defaultNumberFormat;
        // plt.Axes.Left.TickLabelStyle = defaultLabelStyle;
        plt.Axes.Left.TickGenerator = defaultNumberFormat;
        // 固定 Y 轴最小值为0
        plt.Axes.Left.Min = y.Min() * 1.1;

        // 可选：最大值自动计算或手动设置
        plt.Axes.Left.Max = y.Max() * 1.1;

        var scatter = plt.Add.SignalXY([.. x], [.. y], color: new(System.Drawing.Color.FromArgb(61, 119, 255)));
        scatter.MarkerShape = MarkerShape.None;
        return plt.GetImageBytes(width, height, ImageFormat.Png);
    }
    /// <summary>
    /// 波形图
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <returns></returns>
    public static byte[] WaveformChart(List<double> x, List<double> y, string title, int width = 2250, int height = 350)
    {
        Plot plt = new();
        plt.Font.Automatic();

        plt.Font.Set("SimHei");
        plt.Title(title, size: 20);
        // plt.Axes.Bottom.TickLabelStyle = defaultLabelStyle;
        plt.Axes.Bottom.TickGenerator = defaultNumberFormat;
        // plt.Axes.Left.TickLabelStyle = defaultLabelStyle;
        plt.Axes.Left.TickGenerator = defaultNumberFormat;
        // 固定 Y 轴最小值为0
        plt.Axes.Left.Min = y.Min() * 1.1;

        // 可选：最大值自动计算或手动设置
        plt.Axes.Left.Max = y.Max() * 1.1;

        var scatter = plt.Add.SignalXY([.. x], [.. y], color: new(System.Drawing.Color.FromArgb(61, 119, 255)));
        scatter.MarkerShape = MarkerShape.None;
        return plt.GetImageBytes(width, height, ImageFormat.Png);
    }
}



public class FixedNumericManual : NumericManual
{
    /// <summary>
    /// 构造函数：固定刻度数量
    /// </summary>
    /// <param name="tickCount">刻度总数（最少2个）</param>
    /// <param name="min">最小值</param>
    /// <param name="max">最大值</param>
    /// <param name="integerOnly">是否只显示整数刻度</param>
    /// <param name="decimalPlaces">小数位数（非整数刻度时使用）</param>
    public FixedNumericManual(int tickCount, double min, double max, bool integerOnly = false, int decimalPlaces = 2)
    {
        tickCount = Math.Max(2, tickCount);

        if (min > max) (min, max) = (max, min);

        double spacing = (max - min) / (tickCount - 1);

        if (integerOnly)
        {
            min = Math.Floor(min);
            max = Math.Ceiling(max);
            spacing = Math.Max(1, Math.Ceiling((max - min) / (tickCount - 1)));
        }

        for (int i = 0; i < tickCount; i++)
        {
            double value = min + i * spacing;
            string label = integerOnly ? ((int)value).ToString() : value.ToString($"F{decimalPlaces}");
            AddMajor(value, label);
        }
    }
}