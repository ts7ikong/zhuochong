using System.Windows;
using System.Windows.Threading;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// WorkingPet 插件入口: 把旧版 Python 桌宠的"打工人"功能移植到 VPet.
/// 第 1 步: 下班倒计时面板 + 下班提醒. 后续功能 (工作记录 / AI 周报日报 / 采集) 在此基础上扩展.
/// </summary>
public class WorkingPetPlugin : MainPlugin
{
    public override string PluginName => "WorkingPet";

    private readonly PluginSettings settings = new();
    private PetPanel? panel;
    private DispatcherTimer? reminderTimer;
    // 同一天同一事件只提醒一次 (key = 日期 + 事件名), 对应旧版 _proactive_flags
    private readonly HashSet<string> fired = new();

    public WorkingPetPlugin(IMainWindow mainwin) : base(mainwin) { }

    public override void GameLoaded()
    {
        settings.Load(MW.GameSavesData.Data);
        MW.Dispatcher.Invoke(() =>
        {
            ApplyPanelVisibility();
            // 每 30 秒检查一次提醒节点
            reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            reminderTimer.Tick += (_, _) => CheckReminders();
            reminderTimer.Start();
        });
    }

    public override void LoadDIY()
    {
        MW.Main.ToolBar.AddMenuButton(ToolBar.MenuType.DIY, "打工面板 开/关", () =>
        {
            settings.ShowPanel = !settings.ShowPanel;
            ApplyPanelVisibility();
        });
        MW.Main.ToolBar.AddMenuButton(ToolBar.MenuType.DIY, "打工设置", Setting);
    }

    public override void Setting()
    {
        var win = new SettingsWindow(settings, () =>
        {
            ApplyPanelVisibility();
            panel?.Refresh();
            fired.Clear(); // 改了时间后允许重新提醒
        });
        win.Show();
    }

    public override void Save() => settings.Save(MW.GameSavesData.Data);

    public override void EndGame()
    {
        reminderTimer?.Stop();
        MW.Dispatcher.Invoke(() => panel?.Close());
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

        // 下班前 10 分钟
        if (t >= s.PmEnd - TimeSpan.FromMinutes(10) && t < s.PmEnd && fired.Add(day + "end_soon"))
            MW.Main.SayRnd($"还有 {Math.Max(1, (int)Math.Ceiling(s.SecondsToOffWork(t) / 60))} 分钟下班，快收尾了！", true);

        // 到点下班 (启动时已过点则不再弹, 只在 5 分钟窗口内提醒)
        if (t >= s.PmEnd && t < s.PmEnd + TimeSpan.FromMinutes(5) && fired.Add(day + "off_work"))
            MW.Main.SayRnd("下班了！关电脑！回家！", true);
    }
}
