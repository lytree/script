

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;


namespace Helper;


/// <summary>
/// 时间工具类
/// </summary>
public static partial class Helpers
{
    /// <summary>
    /// 时间戳起始日期
    /// </summary>
    public static readonly DateTime TimestampStart = new(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// 时间戳转换为DateTime 时区+8（Asia/Shanghai）
    /// </summary>
    /// <param name="timeStamp"></param>
    /// <returns></returns>
    public static DateTime ToDateTime(long timeStamp)
    {
        DateTimeOffset utcDateTime = DateTimeOffset.FromUnixTimeMilliseconds(timeStamp);
        var chTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");

        return TimeZoneInfo.ConvertTimeFromUtc(utcDateTime.UtcDateTime, chTimeZone); // 当地时区
    }
    /// <summary>
    /// 获取某一年有多少周
    /// </summary>
    /// <param name="now"></param>
    /// <returns>该年周数</returns>
    public static int GetWeekAmount(this in DateTime now)
    {
        var end = new DateTime(now.Year, 12, 31); //该年最后一天
        var gc = new GregorianCalendar();
        return gc.GetWeekOfYear(end, CalendarWeekRule.FirstDay, DayOfWeek.Monday); //该年星期数
    }

    /// <summary>
    /// 返回年度第几个星期   默认星期日是第一天
    /// </summary>
    /// <param name="date">时间</param>
    /// <returns>第几周</returns>
    public static int WeekOfYear(this in DateTime date)
    {
        var gc = new GregorianCalendar();
        return gc.GetWeekOfYear(date, CalendarWeekRule.FirstDay, DayOfWeek.Sunday);
    }

    /// <summary>
    /// 返回年度第几个星期
    /// </summary>
    /// <param name="date">时间</param>
    /// <param name="week">一周的开始日期</param>
    /// <returns>第几周</returns>
    public static int WeekOfYear(this in DateTime date, DayOfWeek week)
    {
        var gc = new GregorianCalendar();
        return gc.GetWeekOfYear(date, CalendarWeekRule.FirstDay, week);
    }

    /// <summary>
    /// 得到一年中的某周的起始日和截止日
    /// 周数 nNumWeek
    /// </summary>
    /// <param name="now"></param>
    /// <param name="nNumWeek">第几周</param>
    public static DateTimeRange GetWeekTime(this DateTime now, int nNumWeek)
    {
        var dt = new DateTime(now.Year, 1, 1);
        dt += new TimeSpan((nNumWeek - 1) * 7, 0, 0, 0);
        return new DateTimeRange(dt.AddDays(-(int)dt.DayOfWeek + (int)DayOfWeek.Monday), dt.AddDays((int)DayOfWeek.Saturday - (int)dt.DayOfWeek + 1));
    }

    /// <summary>
    /// 得到当前周的起始日和截止日
    /// </summary>
    /// <param name="dt"></param>
    public static DateTimeRange GetCurrentWeek(this DateTime dt)
    {
        return new DateTimeRange(dt.AddDays(-(int)dt.DayOfWeek + (int)DayOfWeek.Monday).Date, dt.AddDays((int)DayOfWeek.Saturday - (int)dt.DayOfWeek + 1).Date.AddSeconds(86399));
    }

    /// <summary>
    /// 得到当前月的起始日和截止日
    /// </summary>
    /// <param name="dt"></param>
    public static DateTimeRange GetCurrentMonth(this DateTime dt)
    {
        return new DateTimeRange(new DateTime(dt.Year, dt.Month, 1), new DateTime(dt.Year, dt.Month, GetDaysOfMonth(dt), 23, 59, 59));
    }

    /// <summary>
    /// 得到当前年的起始日和截止日
    /// </summary>
    /// <param name="dt"></param>
    public static DateTimeRange GetCurrentYear(this DateTime dt)
    {
        return new DateTimeRange(new DateTime(dt.Year, 1, 1), new DateTime(dt.Year, 12, 31, 23, 59, 59));
    }

    /// <summary>
    /// 得到当前季度的起始日和截止日
    /// </summary>
    /// <param name="dt"></param>
    public static DateTimeRange GetCurrentQuarter(this DateTime dt)
    {
        return dt.Month switch
        {
            >= 1 and <= 3 => new DateTimeRange(new DateTime(dt.Year, 1, 1), new DateTime(dt.Year, 3, 31, 23, 59, 59)),
            >= 4 and <= 6 => new DateTimeRange(new DateTime(dt.Year, 4, 1), new DateTime(dt.Year, 6, 30, 23, 59, 59)),
            >= 7 and <= 9 => new DateTimeRange(new DateTime(dt.Year, 7, 1), new DateTime(dt.Year, 9, 30, 23, 59, 59)),
            >= 10 and <= 12 => new DateTimeRange(new DateTime(dt.Year, 10, 1), new DateTime(dt.Year, 12, 31, 23, 59, 59)),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    /// <summary>
    /// 得到当前范围的起始日和截止日
    /// </summary>
    /// <param name="dt"></param>
    /// <param name="type"></param>
    public static DateTimeRange GetCurrentRange(this DateTime dt, DateRangeType type)
    {
        return type switch
        {
            DateRangeType.Week => GetCurrentWeek(dt),
            DateRangeType.Month => GetCurrentMonth(dt),
            DateRangeType.Quarter => GetCurrentQuarter(dt),
            DateRangeType.Year => GetCurrentYear(dt),

            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
    }

    /// <summary>
    /// 返回相对于当前时间的相对天数
    /// </summary>
    /// <param name="dt"></param>
    /// <param name="relativeday">相对天数</param>
    public static string GetDateTime(this in DateTime dt, int relativeday)
    {
        return dt.AddDays(relativeday).ToString("yyyy-MM-dd HH:mm:ss");
    }

    /// <summary>
    /// 获取该时间相对于1970-01-01T00:00:00Z的秒数
    /// </summary>
    /// <param name="dt"></param>
    /// <returns></returns>
    public static long GetTotalSeconds(this in DateTime dt) => new DateTimeOffset(dt).UtcDateTime.Ticks / 10_000_000L - 62135596800L;

    /// <summary>
    /// 获取该时间相对于1970-01-01T00:00:00Z的毫秒数
    /// </summary>
    /// <param name="dt"></param>
    /// <returns></returns>
    public static long GetTotalMilliseconds(this in DateTime dt) => new DateTimeOffset(dt).UtcDateTime.Ticks / 10000L - 62135596800000L;

    /// <summary>
    /// 获取该时间相对于1970-01-01T00:00:00Z的微秒时间戳
    /// </summary>
    /// <param name="dt"></param>
    /// <returns></returns>
    public static long GetTotalMicroseconds(this in DateTime dt) => (new DateTimeOffset(dt).UtcTicks - 621355968000000000) / 10;

    /// <summary>
    /// 获取该时间相对于1970-01-01T00:00:00Z的纳秒时间戳
    /// </summary>
    /// <param name="dt"></param>
    /// <returns></returns>
    public static long GetTotalNanoseconds(this in DateTime dt)
    {
        var ticks = (new DateTimeOffset(dt).UtcTicks - 621355968000000000) * 100;
        return ticks + Stopwatch.GetTimestamp() % 100;
    }

    /// <summary>
    /// 获取该时间相对于1970-01-01T00:00:00Z的分钟数
    /// </summary>
    /// <param name="dt"></param>
    /// <returns></returns>
    public static double GetTotalMinutes(this in DateTime dt) => new DateTimeOffset(dt).Offset.TotalMinutes;

    /// <summary>
    /// 获取该时间相对于1970-01-01T00:00:00Z的小时数
    /// </summary>
    /// <param name="dt"></param>
    /// <returns></returns>
    public static double GetTotalHours(this in DateTime dt) => new DateTimeOffset(dt).Offset.TotalHours;

    /// <summary>
    /// 获取该时间相对于1970-01-01T00:00:00Z的天数
    /// </summary>
    /// <param name="dt"></param>
    /// <returns></returns>
    public static double GetTotalDays(this in DateTime dt) => new DateTimeOffset(dt).Offset.TotalDays;

    /// <summary>本年有多少天</summary>
    /// <param name="dt">日期</param>
    /// <returns>本天在当年的天数</returns>
    public static int GetDaysOfYear(this in DateTime dt)
    {
        //取得传入参数的年份部分，用来判断是否是闰年
        int n = dt.Year;
        return DateTime.IsLeapYear(n) ? 366 : 365;
    }

    /// <summary>本月有多少天</summary>
    /// <param name="now"></param>
    /// <returns>天数</returns>
    public static int GetDaysOfMonth(this DateTime now)
    {
        return now.Month switch
        {
            1 => 31,
            2 => DateTime.IsLeapYear(now.Year) ? 29 : 28,
            3 => 31,
            4 => 30,
            5 => 31,
            6 => 30,
            7 => 31,
            8 => 31,
            9 => 30,
            10 => 31,
            11 => 30,
            12 => 31,
            _ => 0
        };
    }

    /// <summary>返回当前日期的星期名称</summary>
    /// <param name="now">日期</param>
    /// <returns>星期名称</returns>
    public static string GetWeekNameOfDay(this in DateTime now)
    {
        return now.DayOfWeek switch
        {
            DayOfWeek.Monday => "星期一",
            DayOfWeek.Tuesday => "星期二",
            DayOfWeek.Wednesday => "星期三",
            DayOfWeek.Thursday => "星期四",
            DayOfWeek.Friday => "星期五",
            DayOfWeek.Saturday => "星期六",
            DayOfWeek.Sunday => "星期日",
            _ => ""
        };
    }

    /// <summary>
    /// 判断时间是否在区间内
    /// </summary>
    /// <param name="this"></param>
    /// <param name="start">开始</param>
    /// <param name="end">结束</param>
    /// <param name="mode">模式</param>
    /// <returns></returns>
    public static bool In(this in DateTime @this, DateTime start, DateTime end, RangeMode mode = RangeMode.Close)
    {
        return mode switch
        {
            RangeMode.Open => start < @this && end > @this,
            RangeMode.Close => start <= @this && end >= @this,
            RangeMode.OpenClose => start < @this && end >= @this,
            RangeMode.CloseOpen => start <= @this && end > @this,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
    }

    /// <summary>
    /// 返回某年某月最后一天
    /// </summary>
    /// <param name="now"></param>
    /// <returns>日</returns>
    public static int GetMonthLastDate(this DateTime now)
    {
        DateTime lastDay = new DateTime(now.Year, now.Month, new GregorianCalendar().GetDaysInMonth(now.Year, now.Month));
        return lastDay.Day;
    }

    /// <summary>
    /// 获得一段时间内有多少小时
    /// </summary>
    /// <param name="start">起始时间</param>
    /// <param name="end">终止时间</param>
    /// <returns>小时差</returns>
    public static string GetTimeDelay(this in DateTime start, DateTime end)
    {
        return (end - start).ToString("c");
    }

    /// <summary>
    /// 返回时间差
    /// </summary>
    /// <param name="dateTime1">时间1</param>
    /// <param name="dateTime2">时间2</param>
    /// <returns>时间差</returns>
    public static string DateDiff(this in DateTime dateTime1, in DateTime dateTime2)
    {
        string dateDiff;
        var ts = dateTime2 - dateTime1;
        if (ts.TotalDays >= 1)
        {
            dateDiff = ts.TotalDays >= 30 ? (ts.TotalDays / 30) + "个月前" : ts.TotalDays + "天前";
        }
        else
        {
            dateDiff = ts.Hours > 1 ? ts.Hours + "小时前" : ts.Minutes + "分钟前";
        }

        return dateDiff;
    }

    /// <summary>
    /// 计算2个时间差
    /// </summary>
    /// <param name="beginTime">开始时间</param>
    /// <param name="endTime">结束时间</param>
    /// <returns>时间差</returns>
    public static string GetDiffTime(this in DateTime beginTime, in DateTime endTime)
    {
        string strResout = string.Empty;

        //获得2时间的时间间隔秒计算
        TimeSpan span = endTime.Subtract(beginTime);
        if (span.Days >= 365)
        {
            strResout += span.Days / 365 + "年";
        }
        if (span.Days >= 30)
        {
            strResout += span.Days % 365 / 30 + "个月";
        }
        if (span.Days >= 1)
        {
            strResout += (int)(span.TotalDays % 30.42) + "天";
        }
        if (span.Hours >= 1)
        {
            strResout += span.Hours + "小时";
        }
        if (span.Minutes >= 1)
        {
            strResout += span.Minutes + "分钟";
        }
        if (span.Seconds >= 1)
        {
            strResout += span.Seconds + "秒";
        }
        return strResout;
    }


    /// <summary>
    /// 等间隔拆分两个时段
    /// </summary>
    /// <param name="startTime">开始时间</param>
    /// <param name="endTime">结束时间</param>
    /// <param name="type">拆分间隔</param>
    /// <returns></returns>
    public static Dictionary<DateTime, DateTimeRange> GetDateTimeRanges(DateTime startTime, DateTime endTime, DateTimeRangeType type)
    {
        Dictionary<DateTime, DateTimeRange> timeDic = [];
        switch (type)
        {
            case DateTimeRangeType.Solar:
            case DateTimeRangeType.Day://天分割
                var nextTime = startTime.AddDays(1).AddHours(-startTime.Hour).AddMinutes(-startTime.Minute).AddSeconds(-startTime.Second);
                timeDic.Add(startTime, new(startTime, nextTime));
                while (DateTime.Compare(startTime, endTime) <= 0)
                {

                    var tmpTime = nextTime;
                    nextTime = nextTime.AddDays(1).AddHours(-startTime.Hour).AddMinutes(-startTime.Minute).AddSeconds(-startTime.Second);
                    timeDic.Add(tmpTime, new(tmpTime, nextTime));
                    startTime = nextTime;
                }

                break;
            case DateTimeRangeType.LunarMonth:
            case DateTimeRangeType.Month://月分割

                var start = startTime;
                var nextTime2 = startTime.AddDays(1 - startTime.Day).AddMonths(1).AddHours(-startTime.Hour).AddMinutes(-startTime.Minute).AddSeconds(-startTime.Second);
                var end = nextTime2;
                timeDic.Add(startTime, new(start, end));
                while (DateTime.Compare(startTime, endTime) <= 0)
                {
                    var tmpTime = nextTime2;
                    nextTime2 = nextTime2.AddDays(1 - startTime.Day).AddMonths(1).AddHours(-startTime.Hour).AddMinutes(-startTime.Minute).AddSeconds(-startTime.Second);
                    timeDic.Add(tmpTime, new(tmpTime, nextTime2));
                    startTime = nextTime2;
                }
                break;
            case DateTimeRangeType.LunarQuarter:
            case DateTimeRangeType.Quarter://季度分割
                var nextTime1 = startTime.AddDays(1 - startTime.Day).AddMonths(3).AddHours(-startTime.Hour).AddMinutes(-startTime.Minute).AddSeconds(-startTime.Second);
                timeDic.Add(startTime, new(startTime, nextTime1));
                while (DateTime.Compare(startTime, endTime) < 0)
                {
                    var tmpTime = nextTime1;
                    nextTime1 = nextTime1.AddDays(1 - startTime.Day).AddMonths(3).AddHours(-startTime.Hour).AddMinutes(-startTime.Minute).AddSeconds(-startTime.Second);
                    timeDic.Add(tmpTime, new(tmpTime, nextTime1));
                    startTime = nextTime1;
                }
                break;

            case DateTimeRangeType.LunarYear:
            case DateTimeRangeType.Year:
                var nextTime3 = startTime.AddDays(1 - startTime.Day).AddYears(1).AddHours(-startTime.Hour).AddMinutes(-startTime.Minute).AddSeconds(-startTime.Second);
                timeDic.Add(startTime, new(startTime, nextTime3));
                while (DateTime.Compare(startTime, endTime) < 0)
                {
                    var tmpTime = nextTime3;
                    nextTime1 = nextTime3.AddDays(1 - startTime.Day).AddYears(1).AddHours(-startTime.Hour).AddMinutes(-startTime.Minute).AddSeconds(-startTime.Second);
                    timeDic.Add(tmpTime, new(tmpTime, nextTime3));
                    startTime = nextTime1;
                }
                break;


        }
        return timeDic;
    }
    /// <summary>
    /// 时间段
    /// </summary>
    public class DateTimeRange
    {

        public DateTimeRange(DateTime start, DateTime end)
        {
            if (start > end)
            {
                throw new Exception("开始时间不能大于结束时间");
            }

            Start = start;
            End = end;
        }
        /// <summary>
        /// 起始时间
        /// </summary>
        public DateTime Start { get; set; }

        /// <summary>
        /// 结束时间
        /// </summary>
        public DateTime End { get; set; }

        /// <summary>
        /// 是否相交
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        /// <returns></returns>
        public bool HasIntersect(DateTime start, DateTime end)
        {
            return HasIntersect(new DateTimeRange(start, end));
        }

        /// <summary>
        /// 是否相交
        /// </summary>
        /// <param name="range"></param>
        /// <returns></returns>
        public bool HasIntersect(DateTimeRange range)
        {
            return Start.In(range.Start, range.End) || End.In(range.Start, range.End);
        }

        /// <summary>
        /// 相交时间段
        /// </summary>
        /// <param name="range"></param>
        /// <returns></returns>
        public (bool intersected, DateTimeRange range) Intersect(DateTimeRange range)
        {
            if (HasIntersect(range.Start, range.End))
            {
                var list = new List<DateTime>() { Start, range.Start, End, range.End };
                list.Sort();
                return (true, new DateTimeRange(list[1], list[2]));
            }

            return (false, null);
        }

        /// <summary>
        /// 相交时间段
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        /// <returns></returns>
        public (bool intersected, DateTimeRange range) Intersect(DateTime start, DateTime end)
        {
            return Intersect(new DateTimeRange(start, end));
        }

        /// <summary>
        /// 是否包含时间段
        /// </summary>
        /// <param name="range"></param>
        /// <returns></returns>
        public bool Contains(DateTimeRange range)
        {
            return range.Start.In(Start, End) && range.End.In(Start, End);
        }

        /// <summary>
        /// 是否包含时间段
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        /// <returns></returns>
        public bool Contains(DateTime start, DateTime end)
        {
            return Contains(new DateTimeRange(start, end));
        }

        /// <summary>
        /// 是否在时间段内
        /// </summary>
        /// <param name="range"></param>
        /// <returns></returns>
        public bool In(DateTimeRange range)
        {
            return Start.In(range.Start, range.End) && End.In(range.Start, range.End);
        }

        /// <summary>
        /// 是否在时间段内
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        /// <returns></returns>
        public bool In(DateTime start, DateTime end)
        {
            return In(new DateTimeRange(start, end));
        }

        /// <summary>
        /// 合并时间段
        /// </summary>
        /// <param name="range"></param>
        /// <returns></returns>
        public DateTimeRange Union(DateTimeRange range)
        {
            if (HasIntersect(range))
            {
                var list = new List<DateTime>() { Start, range.Start, End, range.End };
                list.Sort();
                return new DateTimeRange(list[0], list[3]);
            }

            throw new Exception("不相交的时间段不能合并");
        }

        /// <summary>
        /// 合并时间段
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        /// <returns></returns>
        public DateTimeRange Union(DateTime start, DateTime end)
        {
            return Union(new DateTimeRange(start, end));
        }

        /// <summary>返回一个表示当前对象的 string。</summary>
        /// <returns>表示当前对象的字符串。</returns>
        public override string ToString()
        {
            return $"{Start:yyyy-MM-dd HH:mm:ss}~{End:yyyy-MM-dd HH:mm:ss}";
        }

    }
    /// <summary>
    /// 拆分间隔
    /// </summary>

    public enum DateTimeRangeType
    {
        /// <summary>
        /// 分钟
        /// </summary>
        Minutes = 0,
        /// <summary>
        /// 小时
        /// </summary>
        Hour = 1,
        /// <summary>
        /// 天
        /// </summary>
        Day = 2,
        /// <summary>
        /// 月
        /// </summary>
        Month = 3,
        /// <summary>
        /// 季
        /// </summary>
        Quarter = 4,
        /// <summary>
        /// 年
        /// </summary>
        Year = 5,
        /// <summary>
        /// 农历月
        /// </summary>
        LunarMonth,
        /// <summary>
        /// 农历季
        /// </summary>
        LunarQuarter,
        /// <summary>
        /// 农历日
        /// </summary>
        Solar,
        /// <summary>
        /// 农历年
        /// </summary>
        LunarYear,

    }

    /// <summary>
    /// 区间模式
    /// </summary>
    public enum RangeMode
    {
        /// <summary>
        /// 开区间
        /// </summary>
        Open,

        /// <summary>
        /// 闭区间
        /// </summary>
        Close,

        /// <summary>
        /// 左开右闭区间
        /// </summary>
        OpenClose,

        /// <summary>
        /// 左闭右开区间
        /// </summary>
        CloseOpen
    }
}


public enum DateRangeType
{
    Week,
    Month,
    Quarter,
    Year,
    LunarMonth,
    LunarQuarter,
    Solar,
    LunarYear,
}