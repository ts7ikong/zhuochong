using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using static VPet_Simulator.Core.GraphInfo;

namespace VPet.Plugin.WorkingPet;

/// <summary>设置窗口用来预览下班流程/列出可选玩耍项目的回调</summary>
public class OffWorkHooks
{
    /// <summary>参数: 下班动作, 跑到中央的参数</summary>
    public Action<string, RunOptions> PreviewFinal { get; init; } = (_, _) => { };
    /// <summary>参数: 预备动作, 下班动作, 跑到中央的参数</summary>
    public Action<string, string, RunOptions> PreviewSequence { get; init; } = (_, _, _) => { };
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
    private readonly DispatcherTimer timer;
    private DispatcherTimer? previewTimer;
    private DispatcherTimer? stayTimer;
    private readonly RunToCenterEffect run;

    private string trackedDay = "";   // 当前跟踪的日期, 跨天时重置状态
    private string cancelledDay = ""; // 点了"今天加班"的日期
    private int shown = 4;            // 倒数已经显示到几 (4 = 还没开始)
    private bool finished;            // 今天的下班动作已经做过
    private bool preActive;           // 预备动作正在播放 (到点时需要覆盖它)

    public OffWorkController(IMainWindow mw, PluginSettings settings)
    {
        this.mw = mw;
        this.settings = settings;
        run = new RunToCenterEffect(mw);
        timer = new DispatcherTimer(DispatcherPriority.Normal, mw.Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
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
        stayTimer?.Stop();
        run.Restore(); // 退出游戏时如果还大着, 立刻复原
    }

    public OffWorkHooks CreateHooks() => new()
    {
        PreviewFinal = (action, ro) => { DebugLog.Write($"预览(只看下班动作) action={action}"); Final(action, FinalText, ro); },
        PreviewSequence = PreviewSequence,
    };

    // ── 真实日程 ─────────────────────────────────────────────

    private void Tick()
    {
        try
        {
            TickCore();
        }
        catch (Exception e)
        {
            finished = true; // 出错就不要每 250 毫秒重复报错
            mw.Main.Say("下班提醒出错了：" + e.Message);
        }
    }

    private void TickCore()
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
                Final(settings.OffWorkAction, FinalText, settings.Run);
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
            Final(settings.OffWorkAction, FinalText, settings.Run);
        }
    }

    // ── 预览 (立刻走一遍, 不影响真实日程) ─────────────────────────

    private void PreviewSequence(string pre, string action, RunOptions ro)
    {
        DebugLog.Write($"预览(完整流程) pre={pre} action={action}");
        previewTimer?.Stop();
        StartPre(pre);
        int n = 3;
        ShowCount(n, preview: true);

        var pt = new DispatcherTimer(DispatcherPriority.Normal, mw.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
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
            Final(action, FinalText, ro);
        };
        pt.Start();
    }

    // ── 两段动作 ─────────────────────────────────────────────

    /// <summary>
    /// 宠物当前能不能被打断去做动作. 待机/走路/说话这类随机动作都可以打断,
    /// 只有 被你拖着/举起、工作或学习中、旅行中 才不打断 (只弹气泡).
    /// (之前用 IsIdel 太严格: 宠物只要正在做待机或走路动画就会被当成忙, 动作整个被跳过)
    /// </summary>
    private bool CanAct(VPet_Simulator.Core.Main main) =>
        !main.isPress
        && main.State != VPet_Simulator.Core.Main.WorkingState.Work
        && main.State != VPet_Simulator.Core.Main.WorkingState.Travel
        && !main.DisplayType.Type.ToString().StartsWith("Raised");

    /// <summary>第一段 (倒数期间): 只播动画, 不弹气泡, 气泡留给倒数数字</summary>
    private void StartPre(string pre)
    {
        var main = mw.Main;
        if (!CanAct(main)) return;
        switch (pre)
        {
            case "think":
                // "think" 在引擎里是通用动画(靠名字查找), 不是内置类型
                preActive = PlayLooping("think");
                break;
            case "say":
                var g = mw.Core.Graph?.FindName(GraphType.Say);
                preActive = g != null && PlayLooping(g);
                break;
            case "idle":
                preActive = main.DisplayIdel();
                break;
        }
    }

    /// <summary>
    /// 按名字播放"开始 → 循环"动画, 一直循环到被下一个动画覆盖. 宠物形象里没有这个动画就返回 false
    /// (不先检查的话, 找不到动画会反复触发回调)
    /// </summary>
    private bool PlayLooping(string name)
    {
        var core = mw.Core;
        if (core.Graph == null || core.Save == null) return false;
        if (core.Graph.FindGraph(name, AnimatType.A_Start, core.Save.Mode) == null) return false;
        mw.Main.Display(name, AnimatType.A_Start, mw.Main.DisplayBLoopingForce);
        return true;
    }

    /// <summary>第二段 (到点): 气泡 + 动作. 跑到中央失败时退回"假装逃跑"</summary>
    private void Final(string action, string text, RunOptions ro)
    {
        var main = mw.Main;
        // 预备动作还在播的话允许覆盖它; 否则宠物忙(拖拽/工作/睡觉)就只弹气泡
        bool canAct = CanAct(main);
        preActive = false;

        DebugLog.Write($"Final: action={action} canAct={canAct} state={main.State} display={main.DisplayType.Type} press={main.isPress}");
        if (action == "say" && canAct)
        {
            main.SayRnd(text, true);
            return;
        }
        main.Say(text);
        if (!canAct) return;

        switch (action)
        {
            case "run":
                if (!StartRun(ro, text))
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

    // ── 跑到屏幕中央并放大 ─────────────────────────────────────

    private bool StartRun(RunOptions ro, string text)
    {
        if (run.IsActive) return true; // 上一次还没回去, 不重复开始
        bool ok = run.Begin(ro.Scale, ro.Seconds, () =>
        {
            // 到达后循环一个说话表情: 宠物处于"非闲置"状态, 游戏不会随机让它走开
            var g = mw.Core.Graph?.FindName(GraphType.Say);
            if (g != null) PlayLooping(g);
            mw.Main.Say(text, ReturnButton(ro));

            stayTimer?.Stop();
            stayTimer = new DispatcherTimer(DispatcherPriority.Normal, mw.Dispatcher) { Interval = TimeSpan.FromSeconds(Math.Max(10, ro.StaySeconds)) };
            stayTimer.Tick += (_, _) => GoBack(ro);
            stayTimer.Start();
        }, out var info);

        if (!ok) mw.Main.Say("没能跑到屏幕中央（" + info + "），改用逃跑动画");
        return ok;
    }

    private Button ReturnButton(RunOptions ro)
    {
        var b = new Button { Content = "知道啦，回去吧", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 6, 0, 0) };
        b.Click += (_, _) => GoBack(ro);
        return b;
    }

    /// <summary>缩小并跑回原来的位置 (点按钮或停留超时触发)</summary>
    private void GoBack(RunOptions ro)
    {
        stayTimer?.Stop();
        stayTimer = null;
        if (!run.IsActive) return;
        mw.Main.Say(ro.SleepAfter ? "好的，回去睡觉啦～ 😴" : "好的，回去啦～");
        run.Return(Math.Max(1, ro.Seconds * 0.7), ro.SleepAfter ? () => mw.Main.DisplaySleep(true) : null);
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
