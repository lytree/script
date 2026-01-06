#:package MySqlConnector@2.5.0
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishTrimmed=false
#:package FreeSql@3.5.305
#:package FreeSql.Provider.MySqlConnector@3.5.305
#:package SixLabors.ImageSharp@3.1.12
#:package SixLabors.ImageSharp.Drawing@2.1.7
#:package ScottPlot@5.1.57
#:package DotNetty.Buffers@0.7.6
#:property Imports=../Helper/Json.cs;../Helper/DateTime.cs;../Helper/Images.cs;../Helper/Plot.cs;../Data/Config.cs;../Data/Data.cs;../Data/WaveObject.cs




using System.IO;
using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using FreeSql;
using MySqlConnector;
using Helper;
using Data;
using SkiaSharp;

IFreeSql config = new FreeSql.FreeSqlBuilder()
   .UseConnectionString(FreeSql.DataType.MySql, "Data Source=10.100.0.108;Port=3306;User ID=root;Password=test;database=qacs2000_config_2010;Charset=utf8;SslMode=none;Max pool size=1;Connection Timeout=10")
   // .UseMonitorCommand(cmd => Console.WriteLine($"Sql：{cmd.CommandText}"))//监听SQL语句
   .Build(); //请务必定义成 Singleton 单例模式

IFreeSql data = new FreeSql.FreeSqlBuilder()
   .UseConnectionString(FreeSql.DataType.MySql, "Data Source=10.100.0.91;Port=3306;User ID=root;Password=test;database=dgm2000_1_2010;Charset=utf8;SslMode=none;Max pool size=1;Connection Timeout=10")
    .UseMonitorCommand(cmd => Console.WriteLine($"Sql：{cmd.CommandText}"))//监听SQL语句
   .Build(); //请务必定义成 Singleton 单例模式


Console.WriteLine(Helpers.JsonSerialize(SKFontManager.Default.FontFamilies));
var machines = Data.Config.GetAllMachine(config, 1595557239848L);


foreach (var machine in machines)
{
    var vibpositions = Data.Config.GetAllVibPosition(config, machine.MachineId);
    foreach (var position in vibpositions)
    {
        createImage(machine, position, new DateTime(2025, 12, 01), new DateTime(2025, 12, 22), new DateTime(2025, 12, 01), new DateTime(2025, 12, 22), 1000, 1800);
    }
    var rockpositions = Data.Config.GetAllRockPosition(config, machine.MachineId);
    foreach (var position in rockpositions)
    {
        createImage(machine, position, new DateTime(2025, 12, 01), new DateTime(2025, 12, 22), new DateTime(2025, 12, 01), new DateTime(2025, 12, 22), 1000, 1800);
    }
}




void createImage(Data.Machine machine, Data.MachinePosition position, DateTime startTime, DateTime endTime, DateTime screenshotsStartTime, DateTime screenshotsEndTime, float startSpeed, float endSpeed)
{
    var vib = Data.Vib.GetVibOneByMaxSpeed(data, machine.MachineId, screenshotsStartTime, screenshotsEndTime, position.PositionId, startSpeed, endSpeed);
    if (vib is null)
    {
        return;
    }
    var wave = new Data.WaveObject(vib?.VibWave);
    (float[] xfft, float[] yfft) = wave.WaveFFT();
    double[] wave_x = new double[wave.Wave.Length];
    double[] wave_y = new double[wave.Wave.Length];
    float freq = 0.0f;
    freq = wave.Freq;
    for (int i = 0; i < wave.Wave.Length; i++)
    {
        wave_x[i] = Math.Floor(i / freq * 10000) / 10000;
        wave_y[i] = wave.Wave[i];
    }

    List<Data.Tendency>? list = Data.Vib.GetVibTendency(data, machine.MachineId, startTime, endTime, position.PositionId).AsEnumerable().ToList<Tendency>();
    var x = new List<double>();
    var y = new List<double>();
    foreach (var tendency in list)
    {
        x.Add(Helpers.ToDateTime(tendency.SaveTime).ToOADate());
        y.Add(Convert.ToDouble(tendency.Value));
    }


    var title = $"{machine.MName}-{position.PositionName}-波形频谱图    {Helpers.ToDateTime(vib.SaveTimeCom):yyyy-MM-dd hh:mm:ss}  rpm: {vib.Speed}  rms: {vib.VibRms?.ToString("f4")}g  p: {vib.VibP?.ToString("f4")}g  pp: {vib.VibPp?.ToString("f4")}g";
    Helpers.VerticalMergeImage($"./image/{MyRegex().Replace(machine.MName, "")}#机组{position.PositionName}波形频谱图.png", Helpers.WaveformChart([.. wave_x], [.. wave_y], title), Helpers.SpectrumChart([.. xfft.ToList().Select(Convert.ToDouble)], [.. yfft.ToList().Select(Convert.ToDouble)]), Helpers.TrendChartLine([(x, y, "")]));

}

partial class Program
{
    [GeneratedRegex("[^0-9]")]
    private static partial Regex MyRegex();
}