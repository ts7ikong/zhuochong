using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 上班时段每 5 分钟记一次前台窗口标题, 只存在内存里 (当天), 用于日报提示词. 对应旧版 _window_log 的标题部分.
/// 标题只会发给你自己配置的 AI 接口, 且仅在生成日报时.
/// </summary>
public class WindowTitleSampler
{
    private const int MaxItems = 200;
    private static readonly string[] SkipKeywords = { "VPet", "WorkingPet", "桌宠", "虚拟桌宠", "Program Manager" };

    private readonly Func<WorkSchedule> schedule;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly List<(string Time, string Title)> items = new();
    private readonly object gate = new();
    private string day = "";

    public WindowTitleSampler(Func<WorkSchedule> schedule) => this.schedule = schedule;

    public void Start()
    {
        timer.Tick += (_, _) => Sample();
        timer.Start();
    }

    public void Stop() => timer.Stop();

    /// <summary>今天已采样的标题 (跨天自动清空)</summary>
    public List<(string Time, string Title)> Today()
    {
        lock (gate)
        {
            ResetIfNewDay();
            return items.ToList();
        }
    }

    private void Sample()
    {
        var now = DateTime.Now;
        if (!schedule().IsWorkTime(now.TimeOfDay)) return;
        var title = GetForegroundTitle().Trim();
        if (title.Length == 0 || SkipKeywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase))) return;

        lock (gate)
        {
            ResetIfNewDay();
            if (items.Count > 0 && items[^1].Title == title) return; // 连续相同的不重复记
            items.Add((now.ToString("HH:mm"), title));
            if (items.Count > MaxItems) items.RemoveAt(0);
        }
    }

    private void ResetIfNewDay()
    {
        var today = WorkLogStore.DayKey(DateTime.Now);
        if (day == today) return;
        day = today;
        items.Clear();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    private static string GetForegroundTitle()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return "";
        var sb = new StringBuilder(512);
        return GetWindowText(h, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }
}
