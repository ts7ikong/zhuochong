using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 薪资悬浮面板: 当前时间 / 已工作时长 / 距下班 / 今日已赚. 可拖动, 位置会保存.
/// 对应旧版 DesktopPet 的 info_label
/// </summary>
public class SalaryPanel : Window
{
    private readonly PluginSettings settings;
    private readonly TextBlock tTime = Make(20, FontWeights.Bold, "#00D4FF");
    private readonly TextBlock tStatus = Make(12, FontWeights.Normal, "#6090C0");
    private readonly TextBlock tWorked = Make(11, FontWeights.Normal, "#8090B0");
    private readonly TextBlock tEarned = Make(16, FontWeights.Bold, "#FFDD57");
    private readonly Border box = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public SalaryPanel(PluginSettings settings)
    {
        this.settings = settings;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var t in new[] { tTime, tStatus, tWorked, tEarned })
        {
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.Margin = new Thickness(0, 2, 0, 2);
            stack.Children.Add(t);
        }
        box.Child = stack;
        box.CornerRadius = new CornerRadius(14);
        box.Padding = new Thickness(16, 8, 16, 8);
        box.BorderThickness = new Thickness(1);
        Content = box;

        // 拖动面板, 松手后保存位置
        MouseLeftButtonDown += (_, _) => DragMove();
        LocationChanged += (_, _) => { settings.PanelLeft = Left; settings.PanelTop = Top; };

        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); timer.Start(); };
        Closed += (_, _) => timer.Stop();
    }

    /// <summary>首次显示的默认位置: 屏幕右上角</summary>
    public void ApplyPosition()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (double.IsNaN(settings.PanelLeft) || double.IsNaN(settings.PanelTop)
            || settings.PanelLeft < 0 || settings.PanelTop < 0
            || settings.PanelLeft > SystemParameters.VirtualScreenWidth - 40
            || settings.PanelTop > SystemParameters.VirtualScreenHeight - 40)
        {
            Left = SystemParameters.WorkArea.Right - 220;
            Top = SystemParameters.WorkArea.Top + 20;
        }
        else
        {
            Left = settings.PanelLeft;
            Top = settings.PanelTop;
        }
    }

    public void Refresh()
    {
        var s = settings.Schedule;
        var now = DateTime.Now;
        var t = now.TimeOfDay;
        tTime.Text = now.ToString("HH:mm:ss");

        double worked = s.WorkedSeconds(t);
        tWorked.Text = $"⏱ {(int)(worked / 3600)}h {(int)(worked % 3600 / 60)}m  ·  🏃 {RemainText(s, t)}";
        tEarned.Text = $"💰 ¥ {s.EarnedAt(t):N2}";

        bool off = s.IsOffWork(t);
        tStatus.Text = off ? "🚨 下班了！关电脑！回家！"
            : s.IsWorkTime(t) ? "💻 打工中..." : (t < s.AmStart ? "☕ 还没上班" : "🍱 午休中");
        tStatus.Foreground = Brush(off ? "#FFEE00" : "#6090C0");
        box.Background = Brush(off ? "#DC3A0808" : "#DC040612");
        box.BorderBrush = Brush(off ? "#80FF4040" : "#46006EC8");
    }

    private static string RemainText(WorkSchedule s, TimeSpan now)
    {
        if (s.IsOffWork(now)) return "已下班";
        var sec = (int)s.SecondsToOffWork(now);
        return $"{sec / 3600}h {sec % 3600 / 60}m";
    }

    private static TextBlock Make(double size, FontWeight weight, string color) => new()
    {
        FontFamily = new FontFamily("Microsoft YaHei, Arial"),
        FontSize = size,
        FontWeight = weight,
        Foreground = Brush(color),
    };

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
