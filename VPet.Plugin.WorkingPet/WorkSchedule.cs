using System.Globalization;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 上下班时间表 (纯逻辑, 不依赖 VPet, 便于单元测试)
/// 移植自 WorkingPet 的 _elapsed_in_period / _time_diff_seconds
/// </summary>
public class WorkSchedule
{
    public TimeSpan AmStart { get; set; } = new(9, 0, 0);
    public TimeSpan AmEnd { get; set; } = new(12, 0, 0);
    public TimeSpan PmStart { get; set; } = new(13, 0, 0);
    public TimeSpan PmEnd { get; set; } = new(18, 0, 0);

    /// <summary>一天的总工作秒数 (午休不计)</summary>
    public double DailyWorkSeconds => Span(AmStart, AmEnd) + Span(PmStart, PmEnd);

    /// <summary>当前时刻已工作的秒数 (午休不计入)</summary>
    public double WorkedSeconds(TimeSpan now) => Elapsed(now, AmStart, AmEnd) + Elapsed(now, PmStart, PmEnd);

    /// <summary>今日工作进度 0~1, 下班后为 1</summary>
    public double Progress(TimeSpan now) =>
        DailyWorkSeconds <= 0 ? 0 : Math.Min(1, WorkedSeconds(now) / DailyWorkSeconds);

    /// <summary>是否处于上班时段 (午休不算)</summary>
    public bool IsWorkTime(TimeSpan now) =>
        (now >= AmStart && now <= AmEnd) || (now >= PmStart && now < PmEnd);

    /// <summary>是否已到下班时间</summary>
    public bool IsOffWork(TimeSpan now) => now >= PmEnd;

    /// <summary>距离下班还剩多少秒 (按剩余工作时长算, 午休不计), 已下班返回 0</summary>
    public double RemainingWorkSeconds(TimeSpan now) => Math.Max(0, DailyWorkSeconds - WorkedSeconds(now));

    /// <summary>距离下班时刻还有多少秒 (含午休), 已下班返回 0</summary>
    public double SecondsToOffWork(TimeSpan now) => Math.Max(0, (PmEnd - now).TotalSeconds);

    private static double Span(TimeSpan start, TimeSpan end) => Math.Max(0, (end - start).TotalSeconds);

    private static double Elapsed(TimeSpan now, TimeSpan start, TimeSpan end)
    {
        if (now <= start) return 0;
        if (now >= end) return Span(start, end);
        return (now - start).TotalSeconds;
    }

    /// <summary>解析 "9:00" / "18:30" 这类时间, 失败返回 false</summary>
    public static bool TryParseTime(string? text, out TimeSpan result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!TimeSpan.TryParse(text.Trim(), CultureInfo.InvariantCulture, out result)) return false;
        return result >= TimeSpan.Zero && result < TimeSpan.FromDays(1);
    }

    public static string FormatTime(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}";

    /// <summary>时间配置是否合法 (上午上班 &lt; 上午下班 &lt;= 下午上班 &lt; 下班)</summary>
    public bool IsValid(out string error)
    {
        if (AmStart >= AmEnd) error = "上午上班时间必须早于上午下班时间";
        else if (AmEnd > PmStart) error = "下午上班时间不能早于上午下班时间";
        else if (PmStart >= PmEnd) error = "下午上班时间必须早于下班时间";
        else { error = ""; return true; }
        return false;
    }
}
