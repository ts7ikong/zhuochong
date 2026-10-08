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
    /// <summary>下班前 3 秒开始倒数 (期间宠物做预备动作, 气泡里 3-2-1, 带"今天加班"按钮)</summary>
    public bool OffWorkCountdown { get; set; } = true;
    /// <summary>倒数期间的预备动作: think=思考 / say=说话表情 / idle=随机待机动作 / none=不做动作</summary>
    public string PreAction { get; set; } = "think";
    public static readonly string[] PreActions = { "think", "say", "idle", "none" };
    /// <summary>
    /// 到点下班时宠物做什么: play=玩耍(互动里的玩耍项目) / shutdown=假装逃跑(关机动画) / sleep=睡觉 / say=说话动画 / none=只弹气泡
    /// </summary>
    public string OffWorkAction { get; set; } = "run";
    public static readonly string[] OffWorkActions = { "run", "play", "shutdown", "sleep", "say", "none" };
    /// <summary>"run" 时: 放大倍数(相对当前大小) / 跑到中央用时(秒) / 到中央后停留多久自动回去(秒)</summary>
    public double RunScale { get; set; } = 2.5;
    public double RunSeconds { get; set; } = 3;
    public double RunStay { get; set; } = 180;
    /// <summary>跑回原位之后直接睡觉 (否则恢复待机)</summary>
    public bool SleepAfterRun { get; set; } = true;
    public RunOptions Run => new(RunScale, RunSeconds, RunStay, SleepAfterRun);
    /// <summary>"play" 时玩哪个项目, 留空 = 用第一个当前能玩的; 项目不可用会退回假装逃跑</summary>
    public string OffWorkPlay { get; set; } = "玩水";
    /// <summary>面板大小百分比, 100 为默认</summary>
    public double PanelScale { get; set; } = 100;
    /// <summary>面板整体不透明度百分比 20~100</summary>
    public double PanelOpacity { get; set; } = 100;
    /// <summary>自定义颜色 (#RRGGBB / #AARRGGBB), 空字符串表示跟随状态自动配色</summary>
    public string PanelColor { get; set; } = "";
    public string RingColor { get; set; } = "";
    public string GlowColor { get; set; } = "";
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
        // 存档里的配置不合法时回退默认值, 避免除零/负数
        Schedule = s.IsValid(out _) ? s : new WorkSchedule();
        ShowPanel = line.GetString("show_panel", "true") != "false";
        PanelScale = Math.Max(50, Math.Min(ParseDouble(line.GetString("panel_scale", "100"), 100), 300));
        var act = line.GetString("offwork_action", "run");
        OffWorkAction = OffWorkActions.Contains(act) ? act! : "run";
        SleepAfterRun = line.GetString("sleep_after_run", "true") != "false";
        RunScale = Math.Max(1.2, Math.Min(ParseDouble(line.GetString("run_scale", "2.5"), 2.5), 6));
        RunSeconds = Math.Max(1, Math.Min(ParseDouble(line.GetString("run_seconds", "3"), 3), 10));
        RunStay = Math.Max(10, Math.Min(ParseDouble(line.GetString("run_stay", "180"), 180), 3600));
        var pre = line.GetString("pre_action", "think");
        PreAction = PreActions.Contains(pre) ? pre! : "think";
        OffWorkCountdown = line.GetString("offwork_countdown", "true") != "false";
        OffWorkPlay = line.GetString("offwork_play", "玩水") ?? "玩水";
        PanelOpacity = Math.Max(20, Math.Min(ParseDouble(line.GetString("panel_opacity", "100"), 100), 100));
        PanelColor = ValidColor(line.GetString("panel_color", ""));
        RingColor = ValidColor(line.GetString("ring_color", ""));
        GlowColor = ValidColor(line.GetString("glow_color", ""));
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
        line.SetString("show_panel", ShowPanel ? "true" : "false");
        line.SetString("panel_scale", PanelScale.ToString(CultureInfo.InvariantCulture));
        line.SetString("offwork_action", OffWorkAction);
        line.SetString("sleep_after_run", SleepAfterRun ? "true" : "false");
        line.SetString("run_scale", RunScale.ToString(CultureInfo.InvariantCulture));
        line.SetString("run_seconds", RunSeconds.ToString(CultureInfo.InvariantCulture));
        line.SetString("run_stay", RunStay.ToString(CultureInfo.InvariantCulture));
        line.SetString("pre_action", PreAction);
        line.SetString("offwork_countdown", OffWorkCountdown ? "true" : "false");
        line.SetString("offwork_play", OffWorkPlay);
        line.SetString("panel_opacity", PanelOpacity.ToString(CultureInfo.InvariantCulture));
        line.SetString("panel_color", PanelColor);
        line.SetString("ring_color", RingColor);
        line.SetString("glow_color", GlowColor);
        line.SetString("follow_pet", FollowPet ? "true" : "false");
        line.SetString("panel_left", PanelLeft.ToString(CultureInfo.InvariantCulture));
        line.SetString("panel_top", PanelTop.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>读存档时的容错: 颜色写坏了就当没设置</summary>
    private static string ValidColor(string? s) => TryParseColor(s, out _) ? s!.Trim() : "";

    /// <summary>解析 #RGB / #RRGGBB / #AARRGGBB, 空串返回 false</summary>
    public static bool TryParseColor(string? s, out System.Windows.Media.Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        if (s.Length is not (4 or 7 or 9)) return false;
        try
        {
            color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(s);
            return true;
        }
        catch (Exception) { return false; } // ColorConverter 对非法输入可能抛 FormatException 或 NotSupportedException
    }

    private static double ParseDouble(string? s, double def) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
}
