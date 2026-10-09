using System.Windows.Threading;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 主动说话 (移植自旧版 WorkingPet 的"上下文感知主动说话"):
///   固定节点: 午饭前、下午开工、离下班一小时 (同一天同一节点只说一次, 用预设台词)
///   上下文闲聊: 隔一阵子结合"你最近在做什么"(窗口活动摘要 + 屏幕描述 + AI 判断的状态)让 AI 生成一句宠物口吻的话;
///   连续工作太久时改成提醒休息/喝水. 没配 AI 时用预设台词.
/// 只在宠物醒着、没在下班流程里、没被你按住时说; 每天最多说 MaxPerDay 句, 两次之间随机间隔 25~45 分钟.
/// </summary>
public class Chatter
{
    private const int MaxPerDay = 12;
    private static readonly TimeSpan RestAfter = TimeSpan.FromMinutes(90);

    private static readonly string[] LunchLines =
    {
        "快到饭点啦，把手头的收个尾，我们去吃饭吧～",
        "肚子咕咕叫了，再过一会儿就能吃饭啦！",
    };
    private static readonly string[] AfternoonLines =
    {
        "午休结束，下午也要加油哦～",
        "新的下午开始啦，先从最简单的事做起吧！",
    };
    private static readonly string[] HourLeftLines =
    {
        "离下班只剩一个小时啦，再坚持一下！",
        "还有一小时就能下班了，冲冲冲～",
    };
    private static readonly string[] RestLines =
    {
        "你已经连续忙了好久了，起来动一动、喝口水吧～",
        "眼睛该休息一下啦，看看远处，伸个懒腰～",
    };
    private static readonly string[] ChatLines =
    {
        "我在旁边陪着你呢，有事叫我～",
        "今天也辛苦啦，我会一直在这里的。",
    };

    private readonly IMainWindow mw;
    private readonly PluginSettings settings;
    private readonly AiFeatures ai;
    private readonly Companion companion;
    private readonly OffWorkController offWork;
    private readonly DispatcherTimer timer;
    private readonly Random rnd = new();
    private readonly HashSet<string> fired = new();

    private DateTime nextChat;
    private DateTime lastTalk = DateTime.MinValue;
    private DateTime countDay = DateTime.MinValue;
    private int todayCount;
    private bool inFlight;

    public Chatter(IMainWindow mw, PluginSettings settings, AiFeatures ai, Companion companion, OffWorkController offWork)
    {
        this.mw = mw;
        this.settings = settings;
        this.ai = ai;
        this.companion = companion;
        this.offWork = offWork;
        timer = new DispatcherTimer(DispatcherPriority.Normal, mw.Dispatcher) { Interval = TimeSpan.FromSeconds(60) };
        timer.Tick += (_, _) => Tick();
        ScheduleNext(TimeSpan.FromMinutes(15)); // 刚启动先安静一阵
    }

    public void Start() => timer.Start();
    public void Stop() => timer.Stop();

    private void ScheduleNext(TimeSpan? after = null) =>
        nextChat = DateTime.Now + (after ?? TimeSpan.FromMinutes(25 + rnd.Next(21)));

    private void Tick()
    {
        try { TickCore(); }
        catch (Exception e) { DebugLog.Write("主动说话出错: " + e); }
    }

    private void TickCore()
    {
        if (!settings.ProactiveTalk || inFlight) return;
        var now = DateTime.Now;
        if (countDay != now.Date) { countDay = now.Date; todayCount = 0; }
        if (todayCount >= MaxPerDay) return;
        if (!CanSpeak()) return;

        var s = settings.Schedule;
        var t = now.TimeOfDay;
        string day = now.ToString("yyyyMMdd");

        // 固定节点 (午休/下班后不说)
        if (t >= s.AmEnd - TimeSpan.FromMinutes(8) && t < s.AmEnd && fired.Add(day + "lunch")) { Speak(Pick(LunchLines)); return; }
        if (t >= s.PmStart && t < s.PmStart + TimeSpan.FromMinutes(10) && fired.Add(day + "afternoon")) { Speak(Pick(AfternoonLines)); return; }
        if (t >= s.PmEnd - TimeSpan.FromMinutes(60) && t < s.PmEnd - TimeSpan.FromMinutes(50) && fired.Add(day + "hourleft")) { Speak(Pick(HourLeftLines)); return; }

        // 上下文闲聊: 只在上班时段, 或者你点了"今天加班"之后
        bool active = s.IsWorkTime(t) || (t >= s.PmEnd && offWork.IsOvertimeToday);
        if (!active || now < nextChat) return;

        bool needRest = companion.Judgement == "work" && now - companion.JudgementSince >= RestAfter;
        _ = ChatAsync(needRest, now);
    }

    /// <summary>宠物醒着、没在下班流程里、没被你按住/拖着</summary>
    private bool CanSpeak()
    {
        var main = mw.Main;
        if (offWork.IsSequenceActive) return false;
        if (main.isPress) return false;
        if (main.State != VPet_Simulator.Core.Main.WorkingState.Nomal && main.State != VPet_Simulator.Core.Main.WorkingState.Work) return false;
        if (main.DisplayType.Type.ToString().StartsWith("Raised")) return false;
        if (DateTime.Now - lastTalk < TimeSpan.FromMinutes(5)) return false;
        return true;
    }

    private async Task ChatAsync(bool needRest, DateTime now)
    {
        inFlight = true;
        ScheduleNext(); // 无论成败都排下一次, 免得 AI 出错时每分钟重试
        try
        {
            string line;
            if (!ai.Config.IsConfigured)
            {
                line = needRest ? Pick(RestLines) : Pick(ChatLines);
            }
            else
            {
                string prompt = BuildPrompt(needRest, now);
                string raw = await DoubaoClient.ChatAsync(ai.Config, new[] { ("user", prompt) }, 120);
                line = Clean(raw);
                if (line.Length == 0) line = needRest ? Pick(RestLines) : "";
            }
            if (line.Length == 0) return;
            await mw.Dispatcher.InvokeAsync(() =>
            {
                if (CanSpeak()) Speak(line);
            });
        }
        catch (Exception e)
        {
            DebugLog.Write("主动说话生成失败: " + e.Message);
        }
        finally
        {
            inFlight = false;
        }
    }

    private string BuildPrompt(bool needRest, DateTime now)
    {
        var today = DateTime.Today;
        var recent = ai.Activity.ReadActivity(today)
            .Where(e => e.Summary.Trim().Length > 0).TakeLast(3).Select(e => e.Time + " " + e.Summary).ToList();
        var vision = ai.Activity.ReadVision(today).LastOrDefault();
        string pet = "";
        try { pet = ai.PetContext(); } catch (Exception) { }
        string state = companion.Judgement switch { "work" => "在工作", "slack" => "在摸鱼", _ => "状态不明" };
        string minutes = companion.Judgement == "work" && companion.JudgementSince != DateTime.MinValue
            ? $"（已经连续工作约 {(int)(now - companion.JudgementSince).TotalMinutes} 分钟）" : "";

        string task = needRest
            ? "主人已经连续工作很久了，请用一句话温柔地提醒他起来活动、喝水或让眼睛休息一下，可以结合他在做的事。"
            : "请结合主人最近在做的事，说一句贴心、自然、有点俏皮的话（比如夸一句、打趣一句、或关心一句）。不要复述原文，不要罗列。";

        return $@"你是主人的桌面宠物，说话要像宠物在说话：口语化、可爱、简短。
现在是 {now:HH:mm}。
你的状态：{(pet.Length > 0 ? pet : "正常")}
主人当前：{state}{minutes}
主人最近在做的事：
{(recent.Count > 0 ? string.Join("\n", recent.Select(r => "- " + r)) : "- （暂无记录）")}
{(vision != null ? "屏幕观察：" + vision.Description : "")}

{task}
只输出这一句话，不超过30个字，不加引号、前缀或解释。如果实在没什么可说的，只输出「无」。";
    }

    private static string Clean(string raw)
    {
        var s = raw.Trim().Trim('"', '「', '」', '“', '”', '\'').Trim();
        if (s == "无" || s == "无。") return "";
        if (s.Length > 60) s = s[..60];
        return s;
    }

    private string Pick(string[] lines) => lines[rnd.Next(lines.Length)];

    private void Speak(string text)
    {
        lastTalk = DateTime.Now;
        todayCount++;
        mw.Main.SayRnd(text);
        DebugLog.Write("主动说话: " + text);
    }
}
