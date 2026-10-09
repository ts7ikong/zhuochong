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
    private Window? panel; // PetHud (环绕宠物) 或 PetPanel (侧边面板), 都实现了 IPetPanel
    private DispatcherTimer? reminderTimer;
    private WorkLogStore? store;
    private WorkLogWindow? logWindow;
    private readonly AiConfig config = AiConfig.Load();
    private AiFeatures? ai;
    private OffWorkController? offWork;
    private Companion? companion;
    private PetStatusWindow? statusWindow;
    // 同一天同一事件只提醒一次 (key = 日期 + 事件名), 对应旧版 _proactive_flags
    private readonly HashSet<string> fired = new();

    /// <summary>工作记录存储, 第一次用到时才读文件</summary>
    private WorkLogStore Store => store ??= CreateStore();

    private WorkLogStore CreateStore()
    {
        DataPaths.MigrateLegacyWorkLog(config);
        return new WorkLogStore(DataPaths.WorkLog(config));
    }

    public WorkingPetPlugin(IMainWindow mainwin) : base(mainwin) { }

    public override void GameLoaded()
    {
        settings.Load(MW.GameSavesData.Data);
        ai = new AiFeatures(MW, settings, config, () => Store);
        offWork = new OffWorkController(MW, settings);
        companion = new Companion(MW, settings, offWork);
        ai.Collector.StateClassified += state => MW.Dispatcher.InvokeAsync(() => companion.OnClassified(state));
        ai.PetContext = () => PetInfo.Describe(MW, OwnerStateText());
        MW.Dispatcher.Invoke(() =>
        {
            ApplyPanelVisibility();
            // 每 30 秒检查一次提醒节点
            reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            reminderTimer.Tick += (_, _) => CheckReminders();
            reminderTimer.Start();
            ai.Start();
            offWork.Start();
            companion.Start();
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
        Add("宠物状态", OpenPetStatus);
        Add("陪伴模式 开/关", () =>
        {
            settings.Companion = !settings.Companion;
            MW.Main.SayRnd(settings.Companion ? "陪伴模式开启，我会跟着你一起工作/摸鱼～" : "陪伴模式关闭，我自己玩啦");
        });
        Add("记录工作", RecordWork);
        Add("工作日历", OpenCalendar);
        Add("AI对话", () => ai?.Chat());
        Add("生成周报", () => ai?.GenerateWeekly());
        Add("预览日报", () => ai?.DailyConfirm());
        Add("打开钉钉", () => ai?.LaunchDingTalk());
        Add("打开数据文件夹", () => ai?.OpenDataFolder());
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
            RebuildPanel(); // 样式/大小等可能变了, 重建面板
            fired.Clear(); // 改了时间后允许重新提醒
        }, () => ai?.OpenSettings(), offWork?.CreateHooks());
        win.Closed += (_, _) => MW.Windows.Remove(win);
        MW.Windows.Add(win); // 登记后游戏退出时会统一关闭
        win.Show();
    }

    /// <summary>AI 对你当前状态的判断, 放进对话提示词里</summary>
    private string OwnerStateText() => companion?.Judgement switch
    {
        "work" => "在工作",
        "slack" => "在摸鱼",
        _ => "的状态不明",
    };

    /// <summary>打开宠物状态窗口, 已打开则置前</summary>
    private void OpenPetStatus()
    {
        if (statusWindow != null)
        {
            statusWindow.Activate();
            return;
        }
        var win = new PetStatusWindow(MW, () => companion?.Line() ?? "", () => ai?.Activity.DayStats(DateTime.Today) ?? (0, 0, 0));
        statusWindow = win;
        win.Closed += (_, _) => { statusWindow = null; MW.Windows.Remove(win); };
        MW.Windows.Add(win);
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
        companion?.Stop();
        MW.Dispatcher.Invoke(() => { panel?.Close(); logWindow?.Close(); statusWindow?.Close(); });
        panel = null;
    }

    /// <summary>关掉旧面板并按当前设置重新创建 (切换样式时用)</summary>
    private void RebuildPanel()
    {
        panel?.Close();
        panel = null;
        ApplyPanelVisibility();
        (panel as IPetPanel)?.Refresh();
    }

    private void ApplyPanelVisibility()
    {
        if (settings.ShowPanel)
        {
            if (panel == null)
            {
                var pet = Window.GetWindow(MW.Main);
                panel = settings.PanelStyle switch
                {
                    "side" => new PetPanel(settings, pet),
                    "tech" => new TechHud(settings, pet),
                    "vector" => new VectorHud(settings, pet),
                    _ => new PetHud(settings, pet),
                };
                ((IPetPanel)panel).ApplyPosition();
                var created = panel;
                created.Closed += (_, _) => { if (panel == created) panel = null; };
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
