using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using static VPet_Simulator.Core.GraphHelper;
using static VPet_Simulator.Core.GraphInfo;

namespace VPet.Plugin.WorkingPet;

/// <summary>设置窗口用来预览下班流程/列出可选玩耍项目的回调</summary>
public class OffWorkHooks
{
    public Func<IReadOnlyList<string>> PlayNames { get; init; } = () => Array.Empty<string>();
    /// <summary>参数: 下班动作, 玩耍项目名</summary>
    public Action<string, string> PreviewFinal { get; init; } = (_, _) => { };
    /// <summary>参数: 预备动作, 下班动作, 玩耍项目名</summary>
    public Action<string, string, string> PreviewSequence { get; init; } = (_, _, _) => { };
}

/// <summary>
/// 下班流程: 下班前 3 秒进入「预备动作」并在气泡里倒数 3-2-1 (带"今天加班"按钮), 到点切到「下班动作」.
/// 全部复用 VPet 宠物自带的动画/互动 (思考、说话、待机、玩耍活动、关机动画、睡觉), 不引入新素材.
/// </summary>
public class OffWorkController
{
    private const string FinalText = "下班啦！关电脑！回家！";

    private readonly IMainWindow mw;
    private readonly PluginSettings settings;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DispatcherTimer? previewTimer;

    private string trackedDay = "";   // 当前跟踪的日期, 跨天时重置状态
    private string cancelledDay = ""; // 点了"今天加班"的日期
    private int shown = 4;            // 倒数已经显示到几 (4 = 还没开始)
    private bool finished;            // 今天的下班动作已经做过
    private bool preActive;           // 预备动作正在播放 (到点时需要覆盖它)

    public OffWorkController(IMainWindow mw, PluginSettings settings)
    {
        this.mw = mw;
        this.settings = settings;
    }

    public void Start()
    {
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    public void Stop()
    {
        timer.Stop();
        previewTimer?.Stop();
    }

    public OffWorkHooks CreateHooks() => new()
    {
        PlayNames = AvailablePlayNames,
        PreviewFinal = (action, play) => Final(action, play, FinalText),
        PreviewSequence = PreviewSequence,
    };

    // ── 真实日程 ─────────────────────────────────────────────

    private void Tick()
    {
        var now = DateTime.Now;
        string day = WorkLogStore.DayKey(now);
        if (day != trackedDay)
        {
            trackedDay = day;
            shown = 4;
            finished = false;
            preActive = false;
        }
        if (finished || cancelledDay == day) return;

        double remaining = (settings.Schedule.PmEnd - now.TimeOfDay).TotalSeconds;

        if (!settings.OffWorkCountdown)
        {
            // 不倒数: 到点直接做下班动作 (晚启动的话 5 分钟内仍会做一次)
            if (remaining <= 0 && remaining > -300)
            {
                finished = true;
                Final(settings.OffWorkAction, settings.OffWorkPlay, FinalText);
            }
            return;
        }

        if (remaining > 3) return;
        if (remaining > 0)
        {
            // remaining 在 (2,3] → 3, (1,2] → 2, (0,1] → 1; 每个数字只显示一次
            int n = (int)Math.Ceiling(remaining);
            if (n < shown)
            {
                if (shown == 4) StartPre(settings.PreAction);
                ShowCount(n, preview: false);
                shown = n;
            }
        }
        else if (remaining > -300)
        {
            finished = true;
            Final(settings.OffWorkAction, settings.OffWorkPlay, FinalText);
        }
    }

    // ── 预览 (立刻走一遍, 不影响真实日程) ─────────────────────────

    private void PreviewSequence(string pre, string action, string play)
    {
        previewTimer?.Stop();
        StartPre(pre);
        int n = 3;
        ShowCount(n, preview: true);

        var pt = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        previewTimer = pt;
        pt.Tick += (_, _) =>
        {
            n--;
            if (n > 0)
            {
                ShowCount(n, preview: true);
                return;
            }
            pt.Stop();
            previewTimer = null;
            Final(action, play, FinalText);
        };
        pt.Start();
    }

    // ── 两段动作 ─────────────────────────────────────────────

    /// <summary>宠物当前能不能被打断去做动作: 没被拖拽、没在睡觉/旅行/工作中</summary>
    private bool CanAct(VPet_Simulator.Core.Main main) =>
        main.IsIdel && !main.isPress && main.State != VPet_Simulator.Core.Main.WorkingState.Work;

    /// <summary>第一段 (倒数期间): 只播动画, 不弹气泡, 气泡留给倒数数字</summary>
    private void StartPre(string pre)
    {
        var main = mw.Main;
        if (!CanAct(main)) return;
        switch (pre)
        {
            case "think":
                main.Display(GraphType.Think, AnimatType.A_Start, main.DisplayBLoopingForce);
                preActive = true;
                break;
            case "say":
                var g = mw.Core.Graph?.FindName(GraphType.Say);
                if (g != null)
                {
                    main.Display(g, AnimatType.A_Start, main.DisplayBLoopingForce);
                    preActive = true;
                }
                break;
            case "idle":
                preActive = main.DisplayIdel();
                break;
        }
    }

    /// <summary>第二段 (到点): 气泡 + 动作. 玩耍项目不可用时退回"假装逃跑"</summary>
    private void Final(string action, string playWork, string text)
    {
        var main = mw.Main;
        // 预备动作还在播的话允许覆盖它; 否则宠物忙(拖拽/工作/睡觉)就只弹气泡
        bool canAct = (preActive && !main.isPress && main.State != VPet_Simulator.Core.Main.WorkingState.Work) || CanAct(main);
        preActive = false;

        if (action == "say" && canAct)
        {
            main.SayRnd(text, true);
            return;
        }
        main.Say(text);
        if (!canAct) return;

        switch (action)
        {
            case "play":
                if (!TryStartPlay(playWork))
                    main.Display(GraphType.Shutdown, AnimatType.Single, main.DisplayToNomal);
                break;
            case "shutdown":
                main.Display(GraphType.Shutdown, AnimatType.Single, main.DisplayToNomal);
                break;
            case "sleep":
                main.DisplaySleep(true);
                break;
        }
    }

    // ── 玩耍 (VPet「互动 → 玩耍」里的活动) ──────────────────────────

    /// <summary>当前能玩的玩耍项目: 有动画, 且宠物没生病、等级够 (与游戏自己的检查一致, 避免弹出游戏的报错框)</summary>
    private bool CanPlay(Work w)
    {
        if (string.IsNullOrWhiteSpace(w.Graph)) return false;
        var save = mw.Core.Save;
        var ctl = mw.Core.Controller;
        if (save == null || ctl == null) return false;
        return !ctl.EnableFunction || (save.Mode != IGameSave.ModeType.Ill && save.Level >= w.LevelLimit);
    }

    private IReadOnlyList<string> AvailablePlayNames()
    {
        try
        {
            mw.Main.WorkList(out _, out _, out var plays);
            return plays.Where(CanPlay).Select(w => w.Name).ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private bool TryStartPlay(string name)
    {
        try
        {
            mw.Main.WorkList(out _, out _, out var plays);
            var candidates = plays.Where(CanPlay).ToList();
            var work = candidates.FirstOrDefault(w => w.Name == name) ?? candidates.FirstOrDefault();
            return work != null && mw.Main.StartWork(work);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── 倒数气泡 + 加班按钮 ─────────────────────────────────────

    private void ShowCount(int n, bool preview) => mw.Main.Say(n.ToString(), OvertimeButton(preview));

    private Button OvertimeButton(bool preview)
    {
        var b = new Button { Content = "今天加班", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 6, 0, 0) };
        b.Click += (_, _) => Overtime(preview);
        return b;
    }

    private void Overtime(bool preview)
    {
        if (preview)
        {
            previewTimer?.Stop();
            previewTimer = null;
        }
        else
        {
            cancelledDay = WorkLogStore.DayKey(DateTime.Now);
        }
        if (preActive)
        {
            preActive = false;
            mw.Main.DisplayToNomal();
        }
        mw.Main.Say(preview ? "（预览已取消）" : "好的，今天加班，辛苦啦 💪");
    }
}
