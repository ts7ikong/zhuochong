using System.Globalization;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 上下班时间表 + 薪资计算 (纯逻辑, 不依赖 VPet, 便于单元测试)
/// 移植自 WorkingPet 的 _calc_earned / _elapsed_in_period
/// </summary>
public class WorkSchedule
{
    /// <summary>月薪换算日薪的工作日数 (与旧版一致)</summary>
    public const double WorkDaysPerMonth = 21.75;

    public TimeSpan AmStart { get; set; } = new(9, 0, 0);
    public TimeSpan AmEnd { get; set; } = new(12, 0, 0);
    public TimeSpan PmStart { get; set; } = new(13, 0, 0);
    public TimeSpan PmEnd { get; set; } = new(18, 0, 0);
    public double MonthlySalary { get; set; } = 6000;

    /// <summary>一天的总工作秒数</summary>
    public double DailyWorkSeconds => Span(AmStart, AmEnd) + Span(PmStart, PmEnd);

    /// <summary>每秒收入</summary>
    public double SalaryPerSecond => DailyWorkSeconds <= 0 ? 0 : MonthlySalary / WorkDaysPerMonth / DailyWorkSeconds;

    /// <summary>当前时刻已工作的秒数 (午休不计入)</summary>
    public double WorkedSeconds(TimeSpan now) => Elapsed(now, AmStart, AmEnd) + Elapsed(now, PmStart, PmEnd);

    /// <summary>当前时刻已赚金额, 下班后固定为当日总收入</summary>
    public double EarnedAt(TimeSpan now) => WorkedSeconds(now) * SalaryPerSecond;

    /// <summary>今日总收入</summary>
    public double DailyEarning => DailyWorkSeconds * SalaryPerSecond;

    /// <summary>是否处于上班时段 (午休不算)</summary>
    public bool IsWorkTime(TimeSpan now) =>
        (now >= AmStart && now <= AmEnd) || (now >= PmStart && now < PmEnd);

    /// <summary>是否已到下班时间</summary>
    public bool IsOffWork(TimeSpan now) => now >= PmEnd;

    /// <summary>距离下班还剩多少秒, 已下班返回 0</summary>
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

    /// <summary>时间配置是否合法 (上午 &lt; 午休 &lt; 下午 且月薪 &gt; 0)</summary>
    public bool IsValid(out string error)
    {
        if (AmStart >= AmEnd) error = "上午上班时间必须早于上午下班时间";
        else if (AmEnd > PmStart) error = "下午上班时间不能早于上午下班时间";
        else if (PmStart >= PmEnd) error = "下午上班时间必须早于下班时间";
        else if (MonthlySalary < 0) error = "月薪不能为负数";
        else { error = ""; return true; }
        return false;
    }
}
