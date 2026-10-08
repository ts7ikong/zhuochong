using System.Windows;
using System.Windows.Threading;
using VPet_Simulator.Core;
using static VPet_Simulator.Core.GraphInfo;
using VPet_Simulator.Windows.Interface;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// WorkingPet 插件入口: 把旧版 Python 桌宠的"打工人"功能移植到 VPet.
/// 第 1 步: 下班倒计时面板 + 下班提醒. 第 2 步: 工作记录 + 工作日历. 后续 (AI 周报日报 / 采集) 在此基础上扩展.
/// </summary>
public class WorkingPetPlugin : MainPlugin
{
    public override string PluginName => "WorkingPet";

    private readonly PluginSettings settings = new();
    private PetPanel? panel;
    private DispatcherTimer? reminderTimer;
    private WorkLogStore? store;
    private WorkLogWindow? logWindow;
    private AiFeatures? ai;
    // 同一天同一事件只提醒一次 (key = 日期 + 事件名), 对应旧版 _proactive_flags
    private readonly HashSet<string> fired = new();

    /// <summary>工作记录存储, 第一次用到时才读文件</summary>
    private WorkLogStore Store => store ??= new WorkLogStore();

    public WorkingPetPlugin(IMainWindow mainwin) : base(mainwin) { }

    public override void GameLoaded()
    {
        settings.Load(MW.GameSavesData.Data);
        ai = new AiFeatures(MW, settings, () => Store);
        MW.Dispatcher.Invoke(() =>
        {
            ApplyPanelVisibility();
            // 每 30 秒检查一次提醒节点
            reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            reminderTimer.Tick += (_, _) => CheckReminders();
            reminderTimer.Start();
            ai.Start();
        });
    }

    public override void LoadDIY()
    {
        MW.Main.ToolBar.AddMenuButton(ToolBar.MenuType.DIY, "打工面板 开/关", () =>
        {
            settings.ShowPanel = !settings.ShowPanel;
            ApplyPanelVisibility();
        });
        MW.Main.ToolBar.AddMenuButton(ToolBar.MenuType.DIY, "记录工作", RecordWork);
        MW.Main.ToolBar.AddMenuButton(ToolBar.MenuType.DIY, "工作日历", OpenCalendar);
        MW.Main.ToolBar.AddMenuButton(ToolBar.MenuType.DIY, "AI对话", () => ai?.Chat());
        MW.Main.ToolBar.AddMenuButton(ToolBar.MenuType.DIY, "生成周报", () => ai?.GenerateWeekly());
        MW.Main.ToolBar.AddMenuButton(ToolBar.MenuType.DIY, "预览日报", () => ai?.DailyConfirm());
        MW.Main.ToolBar.AddMenuButton(ToolBar.MenuType.DIY, "打开钉钉", () => ai?.LaunchDingTalk());
        MW.Main.ToolBar.AddMenuButton(ToolBar.MenuType.DIY, "AI设置", () => ai?.OpenSettings());
        MW.Main.ToolBar.AddMenuButton(ToolBar.MenuType.DIY, "打工设置", Setting);
    }

    public override void Setting()
    {
        var win = new SettingsWindow(settings, () =>
        {
            ApplyPanelVisibility();
            panel?.Refresh();
            fired.Clear(); // 改了时间后允许重新提醒
        }, () => ai?.OpenSettings(), action => PlayOffWork(action, "下班了！关电脑！回家！"));
        win.Closed += (_, _) => MW.Windows.Remove(win);
        MW.Windows.Add(win); // 登记后游戏退出时会统一关闭
        win.Show();
    }

    /// <summary>用游戏自带的输入框快速记一条, 对应旧版悬浮输入框的"记录"</summary>
    private void RecordWork()
    {
        MW.ShowInputBox("记录工作", "刚刚做了什么？", "", text =>
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                int n = Store.Add(text);
                MW.Main.SayRnd($"已记录，今天第 {n} 条 ✓");
            }
            catch (Exception e)
            {
                MW.Main.SayRnd("记录失败：" + e.Message);
            }
        });
    }

    /// <summary>打开工作日历, 已打开则置前</summary>
    private void OpenCalendar()
    {
        if (logWindow != null)
        {
            logWindow.Activate();
            return;
        }
        try
        {
            logWindow = new WorkLogWindow(Store);
        }
        catch (Exception e)
        {
            MW.Main.SayRnd("打开工作日历失败：" + e.Message);
            return;
        }
        var win = logWindow;
        win.Closed += (_, _) => { logWindow = null; MW.Windows.Remove(win); };
        MW.Windows.Add(win);
        win.Show();
    }

    public override void Save() => settings.Save(MW.GameSavesData.Data);

    public override void EndGame()
    {
        reminderTimer?.Stop();
        ai?.Stop();
        MW.Dispatcher.Invoke(() => { panel?.Close(); logWindow?.Close(); });
        panel = null;
    }

    private void ApplyPanelVisibility()
    {
        if (settings.ShowPanel)
        {
            if (panel == null)
            {
                panel = new PetPanel(settings, Window.GetWindow(MW.Main));
                panel.ApplyPosition();
                panel.Closed += (_, _) => panel = null;
            }
            panel.Show();
        }
        else
        {
            panel?.Close();
        }
    }

    /// <summary>
    /// 到点下班时让宠物用自己的动作提醒你. 复用 VPet 现有动画:
    /// shutdown = "假装逃跑" (游戏里随机事件用的关机动画, 播完回到待机); sleep = 睡觉直到你点它; say = 说话表情.
    /// 宠物正在被拖拽/工作/学习时不打断它, 只弹气泡.
    /// </summary>
    private void PlayOffWork(string action, string text)
    {
        var main = MW.Main;
        if (action == "say")
        {
            main.SayRnd(text, true);
            return;
        }
        main.Say(text); // 只弹气泡, 不带动画
        if (!main.IsIdel) return;
        switch (action)
        {
            case "shutdown":
                main.Display(GraphType.Shutdown, AnimatType.Single, main.DisplayToNomal);
                break;
            case "sleep":
                main.DisplaySleep(true);
                break;
        }
    }

    private void CheckReminders()
    {
        var s = settings.Schedule;
        var now = DateTime.Now;
        var t = now.TimeOfDay;
        string day = now.ToString("yyyyMMdd");

        // 下班前 10 分钟 (开了日报确认时由日报弹窗接管这个时间点, 避免两条气泡互相覆盖)
        if (ai?.Config.DailyConfirm != true && t >= s.PmEnd - TimeSpan.FromMinutes(10) && t < s.PmEnd && fired.Add(day + "end_soon"))
            MW.Main.SayRnd($"还有 {Math.Max(1, (int)Math.Ceiling(s.SecondsToOffWork(t) / 60))} 分钟下班，快收尾了！", true);

        // 到点下班 (启动时已过点则不再弹, 只在 5 分钟窗口内提醒)
        if (t >= s.PmEnd && t < s.PmEnd + TimeSpan.FromMinutes(5) && fired.Add(day + "off_work"))
            PlayOffWork(settings.OffWorkAction, "下班了！关电脑！回家！");

        // AI 相关提醒: 日报确认 / 周报
        ai?.CheckReminders(now);
    }
}
