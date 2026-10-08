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
    private readonly Window? petWindow;
    private readonly TextBlock tTime = Make(20, FontWeights.Bold, "#00D4FF");
    private readonly TextBlock tStatus = Make(12, FontWeights.Normal, "#6090C0");
    private readonly TextBlock tWorked = Make(11, FontWeights.Normal, "#8090B0");
    private readonly TextBlock tEarned = Make(16, FontWeights.Bold, "#FFDD57");
    private readonly Border box = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <param name="petWindow">桌宠所在的主窗口, 面板开启"跟随宠物"时会贴在它旁边</param>
    public SalaryPanel(PluginSettings settings, Window? petWindow)
    {
        this.settings = settings;
        this.petWindow = petWindow;
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

        // 不跟随宠物时可拖动面板并记住位置; 跟随时位置由宠物决定
        MouseLeftButtonDown += (_, _) => { if (!settings.FollowPet) DragMove(); };
        LocationChanged += (_, _) =>
        {
            if (settings.FollowPet) return;
            settings.PanelLeft = Left;
            settings.PanelTop = Top;
        };

        // 宠物被拖动/缩放时立即跟上
        if (petWindow != null)
        {
            petWindow.LocationChanged += (_, _) => Follow();
            petWindow.SizeChanged += (_, _) => Follow();
        }
        SizeChanged += (_, _) => Follow();

        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); timer.Start(); };
        Closed += (_, _) => timer.Stop();
    }

    /// <summary>
    /// 把面板贴到宠物窗口右侧; 右侧放不下就放左侧; 垂直方向与宠物窗口底部对齐并限制在屏幕内
    /// </summary>
    public void Follow()
    {
        if (!settings.FollowPet || petWindow == null || (!IsLoaded && !IsVisible)) return;
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var area = SystemParameters.WorkArea;
        double petW = petWindow.ActualWidth, petH = petWindow.ActualHeight;
        const double gap = 4;

        double left = petWindow.Left + petW + gap;
        if (left + w > area.Right) left = petWindow.Left - w - gap;
        double top = petWindow.Top + petH - h;

        Left = Math.Max(area.Left, Math.Min(left, area.Right - w));
        Top = Math.Max(area.Top, Math.Min(top, area.Bottom - h));
    }

    /// <summary>首次显示的默认位置: 跟随时贴宠物, 否则用保存的位置/屏幕右上角</summary>
    public void ApplyPosition()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (settings.FollowPet && petWindow != null)
        {
            Left = petWindow.Left + petWindow.ActualWidth + 4;
            Top = petWindow.Top;
            return;
        }
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
        Follow(); // 兜底: 缩放倍率等变化不一定触发事件

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
