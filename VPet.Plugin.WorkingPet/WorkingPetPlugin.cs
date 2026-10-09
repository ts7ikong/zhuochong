using System.Windows;
using System.Windows.Threading;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// WorkingPet 插件入口: 把旧版 Python 桌宠的"打工人"功能移植到 VPet.
/// 面板与下班流程 (PetPanel / OffWorkController) · 工作记录与日历 (WorkLogStore / WorkLogWindow)
/// · AI 对话/周报/日报 (AiFeatures). 后台采集 (截图/应用时长等) 尚未移植.
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
    private OffWorkController? offWork;
    // 同一天同一事件只提醒一次 (key = 日期 + 事件名), 对应旧版 _proactive_flags
    private readonly HashSet<string> fired = new();

    /// <summary>工作记录存储, 第一次用到时才读文件</summary>
    private WorkLogStore Store => store ??= new WorkLogStore();

    public WorkingPetPlugin(IMainWindow mainwin) : base(mainwin) { }

    public override void GameLoaded()
    {
        settings.Load(MW.GameSavesData.Data);
        ai = new AiFeatures(MW, settings, () => Store);
        offWork = new OffWorkController(MW, settings);
        MW.Dispatcher.Invoke(() =>
        {
            ApplyPanelVisibility();
            // 每 30 秒检查一次提醒节点
            reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            reminderTimer.Tick += (_, _) => CheckReminders();
            reminderTimer.Start();
            ai.Start();
            offWork.Start();
        });
    }

    public override void LoadDIY()
    {
        // 所有功能收进「自定」下的一个子菜单, 避免把自定菜单撑得很长
        var root = new System.Windows.Controls.MenuItem
        {
            Header = "打工宠物",
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        void Add(string name, Action action)
        {
            var item = new System.Windows.Controls.MenuItem { Header = name, HorizontalContentAlignment = HorizontalAlignment.Center };
            item.Click += (_, _) => action();
            root.Items.Add(item);
        }
        Add("打工面板 开/关", () =>
        {
            settings.ShowPanel = !settings.ShowPanel;
            ApplyPanelVisibility();
        });
        Add("记录工作", RecordWork);
        Add("工作日历", OpenCalendar);
        Add("AI对话", () => ai?.Chat());
        Add("生成周报", () => ai?.GenerateWeekly());
        Add("预览日报", () => ai?.DailyConfirm());
        Add("打开钉钉", () => ai?.LaunchDingTalk());
        Add("AI设置", () => ai?.OpenSettings());
        Add("打工设置", Setting);

        MW.Main.ToolBar.MenuDIY.Items.Add(root);
        MW.Main.ToolBar.LoadDIY(); // 刷新「自定」菜单的显示
    }

    public override void Setting()
    {
        var win = new SettingsWindow(settings, () =>
        {
            ApplyPanelVisibility();
            panel?.Refresh();
            fired.Clear(); // 改了时间后允许重新提醒
        }, () => ai?.OpenSettings(), offWork?.CreateHooks());
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
        offWork?.Stop();
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

    private void CheckReminders()
    {
        var s = settings.Schedule;
        var now = DateTime.Now;
        var t = now.TimeOfDay;
        string day = now.ToString("yyyyMMdd");

        // 下班前 10 分钟 (开了日报确认时由日报弹窗接管这个时间点, 避免两条气泡互相覆盖)
        if (ai?.Config.DailyConfirm != true && t >= s.PmEnd - TimeSpan.FromMinutes(10) && t < s.PmEnd && fired.Add(day + "end_soon"))
            MW.Main.SayRnd($"还有 {Math.Max(1, (int)Math.Ceiling(s.SecondsToOffWork(t) / 60))} 分钟下班，快收尾了！", true);

        // 到点下班(倒数 + 两段动作)由 OffWorkController 负责

        // AI 相关提醒: 日报确认 / 周报
        ai?.CheckReminders(now);
    }
}
