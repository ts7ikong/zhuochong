using System.Windows.Threading;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using static VPet_Simulator.Core.GraphHelper;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 陪伴模式: 宠物跟着你的状态做 VPet「互动」菜单里的活动.
///   AI 判断你在工作 → 随机一个"工作"(没有能做的就"学习")    AI 判断你在摸鱼 → 随机一个"玩耍"
///   下班后 → 睡觉 (点了"今天加班"则继续按 AI 判断走)
/// 只管理自己启动的活动: 你手动让宠物去工作/学习/玩耍/睡觉, 它不会打断你的选择.
/// 活动里的金钱/经验是游戏自己按效率实时结算的, 这里不改任何数值 (直接改数值会被游戏标成作弊存档).
/// </summary>
public class Companion
{
    private static readonly TimeSpan MinDwell = TimeSpan.FromMinutes(5);     // 切换活动的最短间隔 (防抖)
    private static readonly TimeSpan CareNoticeGap = TimeSpan.FromMinutes(30); // 饿了/渴了提醒的最小间隔
    private static readonly TimeSpan TeaseAfter = TimeSpan.FromMinutes(40);   // 摸鱼多久开始调侃, 以及两次调侃的最小间隔
    private static readonly string[] TeaseLines =
    {
        "你已经摸鱼 {0} 分钟啦，我都替你着急了～",
        "{0} 分钟了哦，要不要先把手头的活干完再玩？",
        "摸鱼 {0} 分钟，我都玩累了，你该回去干活啦！",
        "喂喂，{0} 分钟没干活了，被老板看到我可不帮你说话～",
        "休息 {0} 分钟差不多啦，来，我们继续加油！",
    };

    private readonly IMainWindow mw;
    private readonly PluginSettings settings;
    private readonly OffWorkController offWork;
    private readonly DispatcherTimer timer;
    private readonly Random rnd = new();
    private readonly Queue<string> recent = new(); // 最近两次 AI 判断

    private string judgement = "";      // 防抖后的结论: "" / "work" / "slack"
    private string? owned;              // 陪伴模式启动的活动名 (null = 当前没有我们的活动)
    private string? lastPicked;
    private bool ownsSleep;             // 睡眠是陪伴模式让它睡的
    private DateTime sleepSeen = DateTime.MinValue;
    private DateTime lastSwitch = DateTime.MinValue;
    private DateTime pauseUntil = DateTime.MinValue;   // 用户手动停掉活动后, 暂停陪伴一段时间
    private DateTime slackSince = DateTime.MinValue;
    private DateTime lastTease = DateTime.MinValue;
    private DateTime lastCareNotice = DateTime.MinValue;

    public Companion(IMainWindow mw, PluginSettings settings, OffWorkController offWork)
    {
        this.mw = mw;
        this.settings = settings;
        this.offWork = offWork;
        // 显式绑定 UI 线程的 Dispatcher (插件可能在非 UI 线程里创建, 默认绑定会让定时器永远不触发)
        timer = new DispatcherTimer(DispatcherPriority.Normal, mw.Dispatcher) { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        timer.Start();
        // 活动结束 (时间到/被停止/生病) 时回调; 用户自己点"停止"则暂停陪伴一阵, 尊重用户的选择
        mw.Main.Event_WorkEnd += info => mw.Dispatcher.InvokeAsync(() => OnWorkEnd(info));
    }

    public void Stop() => timer.Stop();

    /// <summary>防抖后的判断: "" / "work" / "slack"</summary>
    public string Judgement => judgement;

    /// <summary>给状态窗口显示的一句话</summary>
    public string Line()
    {
        if (!settings.Companion) return "陪伴模式：已关闭（宠物自己行动）";
        string j = judgement switch { "work" => "你在工作", "slack" => "你在摸鱼", _ => "还在观察（需要配置好 AI 并开启窗口活动记录）" };
        string what = owned != null ? $"，宠物正在陪你做「{owned}」" : ownsSleep ? "，宠物在睡觉" : "";
        return $"陪伴模式：已开启 · AI 判断：{j}{what}";
    }

    /// <summary>AI 给出新的判断 (必须在 UI 线程调用)</summary>
    public void OnClassified(string state)
    {
        if (state != "work" && state != "slack") return; // unknown 不改变现状
        recent.Enqueue(state);
        while (recent.Count > 2) recent.Dequeue();

        // 连续两次判断一致才改结论, 防止来回抽风
        if (recent.Count == 2 && recent.All(x => x == state))
        {
            if (judgement != state) DebugLog.Write($"陪伴: 判断变为 {state}");
            judgement = state;
        }
        if (state == "slack")
        {
            if (slackSince == DateTime.MinValue) slackSince = DateTime.Now;
        }
        else
        {
            slackSince = DateTime.MinValue;
        }
        Tick();
    }

    // ── 主循环 ───────────────────────────────────────────────

    private void Tick()
    {
        try
        {
            TickCore();
        }
        catch (Exception e)
        {
            DebugLog.Write("陪伴模式出错: " + e);
        }
    }

    private void TickCore()
    {
        if (!settings.Companion) return;
        var main = mw.Main;
        var now = DateTime.Now;

        // 记录睡眠开始时间, 用来区分"昨晚下班睡的"和"你白天自己让它睡的"
        if (main.State == VPet_Simulator.Core.Main.WorkingState.Sleep)
        {
            if (sleepSeen == DateTime.MinValue) sleepSeen = now;
        }
        else
        {
            sleepSeen = DateTime.MinValue;
            ownsSleep = false;
        }

        // 活动已经结束但没收到结束事件时, 清掉"自己启动的活动"标记, 免得把你手动开的活动当成自己的
        if (owned != null && main.State != VPet_Simulator.Core.Main.WorkingState.Work) owned = null;

        if (offWork.IsSequenceActive) return;            // 下班倒计时/跑到中央期间, 让它先演完
        if (now < pauseUntil) return;                    // 刚被你手动停掉活动
        if (main.isPress || main.State == VPet_Simulator.Core.Main.WorkingState.Travel) return;
        if (main.DisplayType.Type.ToString().StartsWith("Raised")) return;

        string want = Decide(now);
        MaybeTease(now);

        if (want == "sleep")
        {
            EnsureSleep(now);
            return;
        }
        if (want != "work" && want != "slack") return;

        WakeIfNeeded(now);
        if (main.State == VPet_Simulator.Core.Main.WorkingState.Sleep) return; // 白天你自己让它睡的, 不打扰

        if (NeedsCare(out var reason))
        {
            if (owned != null) StopOwned();
            if (now - lastCareNotice >= CareNoticeGap)
            {
                lastCareNotice = now;
                mw.Main.SayRnd($"我{reason}，先歇会儿，给我点吃的喝的吧～", true);
            }
            return;
        }
        EnsureActivity(want == "work" ? Work.WorkType.Work : Work.WorkType.Play, now);
    }

    /// <summary>此刻宠物该做什么: "work" / "slack" / "sleep" / "" (什么都不管)</summary>
    private string Decide(DateTime now)
    {
        var s = settings.Schedule;
        var t = now.TimeOfDay;
        if (t >= s.PmEnd)
        {
            if (offWork.IsOvertimeToday) return judgement;            // 你选了加班: 继续跟着你
            // 下班流程已经做完 (或错过了 5 分钟窗口) 才让它睡, 避免和下班动作抢
            if (offWork.FinishedToday || t >= s.PmEnd + TimeSpan.FromMinutes(5)) return "sleep";
            return "";
        }
        return judgement;
    }

    // ── 活动 ─────────────────────────────────────────────────

    private void EnsureActivity(Work.WorkType type, DateTime now)
    {
        var main = mw.Main;
        if (main.State == VPet_Simulator.Core.Main.WorkingState.Work)
        {
            if (owned == null) return;                        // 你手动开的活动, 不管
            var cur = main.NowWork;
            if (cur != null && Matches(cur.Type, type)) return;
            if (now - lastSwitch < MinDwell) return;          // 防抖: 刚切换过
            var next = Pick(type);
            if (next == null)
            {
                StopOwned();
                return;
            }
            lastSwitch = now;
            DebugLog.Write($"陪伴: 切换到 {next.Name}");
            // 先停掉当前活动 (已产出的金钱/经验不会丢, 只是没有"完成奖励"), 动画播完再开始新的
            main.WorkTimer.Stop(() => mw.Dispatcher.InvokeAsync(() => Start(next)), WorkTimer.FinishWorkInfo.StopReason.Other);
            return;
        }
        if (main.State != VPet_Simulator.Core.Main.WorkingState.Nomal) return;
        var w = Pick(type);
        if (w != null) Start(w);
    }

    private void Start(Work w)
    {
        var main = mw.Main;
        if (main.State == VPet_Simulator.Core.Main.WorkingState.Work) return;
        if (main.StartWork(w))
        {
            owned = w.Name;
            lastPicked = w.Name;
            lastSwitch = DateTime.Now;
            DebugLog.Write($"陪伴: 开始 {w.Name}");
        }
    }

    private static bool Matches(Work.WorkType current, Work.WorkType wanted) =>
        wanted == Work.WorkType.Work
            ? current is Work.WorkType.Work or Work.WorkType.Study // 没有能做的"工作"时会退回"学习"
            : current == wanted;

    /// <summary>随机挑一个当前能做的活动: 宠物没生病, 等级够, 有动画; 尽量不和上一个重复</summary>
    private Work? Pick(Work.WorkType type)
    {
        mw.Main.WorkList(out var works, out var studies, out var plays);
        var pool = (type == Work.WorkType.Work ? works : plays).Where(CanDo).ToList();
        if (pool.Count == 0 && type == Work.WorkType.Work) pool = studies.Where(CanDo).ToList();
        if (pool.Count == 0) return null;
        if (pool.Count > 1 && lastPicked != null) pool.RemoveAll(w => w.Name == lastPicked);
        return pool[rnd.Next(pool.Count)];
    }

    private bool CanDo(Work w)
    {
        if (string.IsNullOrWhiteSpace(w.Graph)) return false;
        var save = mw.Core.Save;
        var ctl = mw.Core.Controller;
        if (save == null || ctl == null) return false;
        return !ctl.EnableFunction || (save.Mode != IGameSave.ModeType.Ill && save.Level >= w.LevelLimit);
    }

    private void StopOwned()
    {
        var main = mw.Main;
        owned = null;
        if (main.State == VPet_Simulator.Core.Main.WorkingState.Work)
            main.WorkTimer.Stop(reason: WorkTimer.FinishWorkInfo.StopReason.Other);
    }

    private void OnWorkEnd(WorkTimer.FinishWorkInfo info)
    {
        bool wasOurs = owned != null && info.work.Name == owned;
        owned = null;
        if (wasOurs && info.Reason == WorkTimer.FinishWorkInfo.StopReason.MenualStop)
        {
            // 你点了"停止": 尊重你的选择, 半小时内不再自动开活动
            pauseUntil = DateTime.Now + TimeSpan.FromMinutes(30);
            DebugLog.Write("陪伴: 你手动停止了活动, 暂停 30 分钟");
        }
    }

    // ── 睡觉 ─────────────────────────────────────────────────

    private void EnsureSleep(DateTime now)
    {
        var main = mw.Main;
        if (main.State == VPet_Simulator.Core.Main.WorkingState.Sleep) return;
        if (main.State == VPet_Simulator.Core.Main.WorkingState.Work)
        {
            if (owned == null) return; // 你手动开的活动, 让它做完
            StopOwned();
        }
        if (main.State != VPet_Simulator.Core.Main.WorkingState.Nomal && main.State != VPet_Simulator.Core.Main.WorkingState.Work) return;
        main.DisplaySleep(true);
        ownsSleep = true;
        lastSwitch = now;
        DebugLog.Write("陪伴: 下班, 睡觉");
    }

    /// <summary>
    /// 要开始工作/玩耍了但宠物在睡觉: 只叫醒"陪伴模式让它睡的"和"跨天/下班后睡的";
    /// 白天你自己让它睡的不动.
    /// </summary>
    private void WakeIfNeeded(DateTime now)
    {
        var main = mw.Main;
        if (main.State != VPet_Simulator.Core.Main.WorkingState.Sleep) return;
        bool overnight = sleepSeen != DateTime.MinValue
            && (sleepSeen.Date < now.Date || sleepSeen.TimeOfDay >= settings.Schedule.PmEnd);
        if (!ownsSleep && !overnight) return;

        DebugLog.Write("陪伴: 叫醒宠物");
        ownsSleep = false;
        sleepSeen = DateTime.MinValue;
        if (!main.DisplayStop(main.DisplayToNomal))
        {
            main.State = VPet_Simulator.Core.Main.WorkingState.Nomal;
            main.DisplayToNomal();
        }
    }

    // ── 保护宠物 / 调侃 ───────────────────────────────────────

    /// <summary>宠物生病或太饿太渴时不该继续干活 (工作会持续消耗食物和饮水)</summary>
    private bool NeedsCare(out string reason)
    {
        reason = "";
        var save = mw.Core.Save;
        var ctl = mw.Core.Controller;
        if (save == null || ctl == null || !ctl.EnableFunction) return false;
        if (save.Mode == IGameSave.ModeType.Ill) { reason = "生病了"; return true; }
        double max = Math.Max(1, save.StrengthMax);
        if (save.StrengthFood / max < 0.3) { reason = "饿了"; return true; }
        if (save.StrengthDrink / max < 0.3) { reason = "渴了"; return true; }
        return false;
    }

    private void MaybeTease(DateTime now)
    {
        if (!settings.CompanionTease || judgement != "slack" || slackSince == DateTime.MinValue) return;
        if (!settings.Schedule.IsWorkTime(now.TimeOfDay)) return;  // 午休/下班后摸鱼是正当的, 不调侃
        if (now - slackSince < TeaseAfter || now - lastTease < TeaseAfter) return;
        lastTease = now;
        int minutes = (int)(now - slackSince).TotalMinutes;
        mw.Main.SayRnd(string.Format(TeaseLines[rnd.Next(TeaseLines.Length)], minutes));
    }
}
