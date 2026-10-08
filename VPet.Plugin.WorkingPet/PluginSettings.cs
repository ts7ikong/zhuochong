using System.Globalization;
using LinePutScript;
using LinePutScript.Dictionary;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 插件设置, 存在 VPet 存档 GameSavesData.Data 的 "workingpet" 行里, 随游戏存档一起保存
/// </summary>
public class PluginSettings
{
    private const string LineName = "workingpet";

    public WorkSchedule Schedule { get; private set; } = new();
    /// <summary>是否显示薪资面板</summary>
    public bool ShowPanel { get; set; } = true;
    /// <summary>面板是否跟随宠物移动</summary>
    public bool FollowPet { get; set; } = true;
    /// <summary>面板位置, NaN 表示还没拖动过, 使用默认位置</summary>
    public double PanelLeft { get; set; } = double.NaN;
    public double PanelTop { get; set; } = double.NaN;

    public void Load(LPS_D data)
    {
        var line = data[LineName];
        var s = new WorkSchedule();
        if (WorkSchedule.TryParseTime(line.GetString("am_start", "9:00"), out var t)) s.AmStart = t;
        if (WorkSchedule.TryParseTime(line.GetString("am_end", "12:00"), out t)) s.AmEnd = t;
        if (WorkSchedule.TryParseTime(line.GetString("pm_start", "13:00"), out t)) s.PmStart = t;
        if (WorkSchedule.TryParseTime(line.GetString("pm_end", "18:00"), out t)) s.PmEnd = t;
        s.MonthlySalary = ParseDouble(line.GetString("monthly_salary", "6000"), 6000);
        // 存档里的配置不合法时回退默认值, 避免除零/负数
        Schedule = s.IsValid(out _) ? s : new WorkSchedule();
        ShowPanel = line.GetString("show_panel", "true") != "false";
        FollowPet = line.GetString("follow_pet", "true") != "false";
        PanelLeft = ParseDouble(line.GetString("panel_left", "NaN"), double.NaN);
        PanelTop = ParseDouble(line.GetString("panel_top", "NaN"), double.NaN);
    }

    public void Save(LPS_D data)
    {
        var line = data[LineName];
        line.SetString("am_start", WorkSchedule.FormatTime(Schedule.AmStart));
        line.SetString("am_end", WorkSchedule.FormatTime(Schedule.AmEnd));
        line.SetString("pm_start", WorkSchedule.FormatTime(Schedule.PmStart));
        line.SetString("pm_end", WorkSchedule.FormatTime(Schedule.PmEnd));
        line.SetString("monthly_salary", Schedule.MonthlySalary.ToString(CultureInfo.InvariantCulture));
        line.SetString("show_panel", ShowPanel ? "true" : "false");
        line.SetString("follow_pet", FollowPet ? "true" : "false");
        line.SetString("panel_left", PanelLeft.ToString(CultureInfo.InvariantCulture));
        line.SetString("panel_top", PanelTop.ToString(CultureInfo.InvariantCulture));
    }

    private static double ParseDouble(string? s, double def) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
}
