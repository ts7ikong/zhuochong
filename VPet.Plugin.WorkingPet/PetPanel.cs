using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
// ImplicitUsings 引入了 System.IO, 与 Shapes.Path 重名, 显式指定
using Path = System.Windows.Shapes.Path;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 打工信息悬浮面板: 大号时钟 + 今日进度环 + 已工作/还剩 + 状态.
/// 状态不同配色不同 (上班/午休/未上班/下班), 下班后发光脉冲.
/// 开启"跟随宠物"时贴在宠物窗口旁边, 否则可自由拖动.
/// </summary>
public class PetPanel : Window
{
    /// <summary>某个状态下的整套配色</summary>
    private record Theme(string Bg1, string Bg2, string Accent1, string Accent2, string Sub, bool Pulse);

    private static readonly Theme Working = new("#F0141B3D", "#F02A1458", "#00E5FF", "#B388FF", "#9FB4E8", false);
    private static readonly Theme Lunch = new("#F03A2A14", "#F05A3414", "#FFB74D", "#FF7043", "#E8C79F", false);
    private static readonly Theme Before = new("#F01C2630", "#F0263340", "#90A4AE", "#B0BEC5", "#9FB0BA", false);
    private static readonly Theme Off = new("#F03A0F14", "#F05A1418", "#FF5252", "#FF9100", "#E8A0A0", true);

    private const double RingSize = 132;
    private const double RingThickness = 10;

    private readonly PluginSettings settings;
    private readonly Window? petWindow;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly Border box = new();
    private readonly Border host = new();
    private double appliedScale = 1;
    private readonly DropShadowEffect glow = new() { ShadowDepth = 0, BlurRadius = 24, Opacity = 0.55 };
    private readonly TextBlock tTime = Text(36, FontWeights.Bold, "#FFFFFF", "Consolas");
    private readonly TextBlock tDate = Text(12, FontWeights.Normal, "#9FB4E8");
    private readonly TextBlock tPercent = Text(30, FontWeights.Bold, "#FFFFFF", "Consolas");
    private readonly TextBlock tPercentCap = Text(11, FontWeights.Normal, "#9FB4E8");
    private readonly TextBlock tStatus = Text(15, FontWeights.SemiBold, "#FFFFFF");
    private readonly TextBlock tWorked = Text(13, FontWeights.SemiBold, "#FFFFFF");
    private readonly TextBlock tRemain = Text(13, FontWeights.SemiBold, "#FFFFFF");
    private readonly Ellipse track = new();
    private readonly Path arc = new();
    private readonly GradientStop arcStop1 = new(), arcStop2 = new();
    private readonly GradientStop bgStop1 = new(), bgStop2 = new();
    private readonly GradientStop bdStop1 = new(), bdStop2 = new();
    private readonly Border chipWorked = new(), chipRemain = new();
    private string? appliedKey;

    /// <param name="petWindow">桌宠所在的主窗口, 开启"跟随宠物"时面板会贴在它旁边</param>
    public PetPanel(PluginSettings settings, Window? petWindow)
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

        BuildUi();

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

    // ── 界面搭建 ─────────────────────────────────────────────

    private void BuildUi()
    {
        // 背景和描边都是渐变, 颜色在 Apply(theme) 里按状态替换
        bgStop1.Offset = 0; bgStop2.Offset = 1;
        bdStop1.Offset = 0; bdStop2.Offset = 1;
        box.Background = new LinearGradientBrush(new GradientStopCollection { bgStop1, bgStop2 }, 45);
        box.BorderBrush = new LinearGradientBrush(new GradientStopCollection { bdStop1, bdStop2 }, 45);
        box.BorderThickness = new Thickness(1.5);
        box.CornerRadius = new CornerRadius(22);
        box.Padding = new Thickness(22, 16, 22, 18);
        box.MinWidth = 230;
        box.Effect = glow;
        // 给阴影留出空间, 否则发光会被窗口边缘裁掉
        host.Padding = new Thickness(20);
        host.Child = box;
        Content = host;

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var t in new[] { tTime, tDate, tStatus })
            t.HorizontalAlignment = HorizontalAlignment.Center;
        tDate.Margin = new Thickness(0, 0, 0, 10);
        tStatus.Margin = new Thickness(0, 10, 0, 10);

        stack.Children.Add(tTime);
        stack.Children.Add(tDate);
        stack.Children.Add(BuildRing());
        stack.Children.Add(tStatus);

        var chips = new UniformGrid { Columns = 2 };
        chips.Children.Add(Chip(chipWorked, "⏱ 已工作", tWorked));
        chips.Children.Add(Chip(chipRemain, "🏃 还需", tRemain));
        stack.Children.Add(chips);

        box.Child = stack;
    }

    /// <summary>进度环: 底轨 + 渐变进度弧 + 中间百分比</summary>
    private Grid BuildRing()
    {
        var grid = new Grid { Width = RingSize, Height = RingSize, HorizontalAlignment = HorizontalAlignment.Center };

        track.Stroke = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        track.StrokeThickness = RingThickness;
        track.Margin = new Thickness(RingThickness / 2);
        grid.Children.Add(track);

        arcStop1.Offset = 0; arcStop2.Offset = 1;
        arc.Stroke = new LinearGradientBrush(new GradientStopCollection { arcStop1, arcStop2 }, 45);
        arc.StrokeThickness = RingThickness;
        arc.StrokeStartLineCap = PenLineCap.Round;
        arc.StrokeEndLineCap = PenLineCap.Round;
        grid.Children.Add(arc);

        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        tPercent.HorizontalAlignment = HorizontalAlignment.Center;
        tPercentCap.HorizontalAlignment = HorizontalAlignment.Center;
        tPercentCap.Text = "今日进度";
        center.Children.Add(tPercent);
        center.Children.Add(tPercentCap);
        grid.Children.Add(center);
        return grid;
    }

    /// <summary>底部的小胶囊: 上面是说明, 下面是数值</summary>
    private static Border Chip(Border chip, string caption, TextBlock value)
    {
        var cap = Text(11, FontWeights.Normal, "#AAB6D8");
        cap.Text = caption;
        cap.HorizontalAlignment = HorizontalAlignment.Center;
        value.HorizontalAlignment = HorizontalAlignment.Center;
        var inner = new StackPanel();
        inner.Children.Add(cap);
        inner.Children.Add(value);
        chip.Child = inner;
        chip.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        chip.CornerRadius = new CornerRadius(12);
        chip.Padding = new Thickness(8, 6, 8, 6);
        chip.Margin = new Thickness(3, 0, 3, 0);
        return chip;
    }

    // ── 位置 ─────────────────────────────────────────────────

    /// <summary>
    /// 把面板贴到宠物窗口右侧; 右侧放不下就放左侧; 垂直方向与宠物窗口底部对齐并限制在屏幕内
    /// </summary>
    public void Follow()
    {
        if (!settings.FollowPet || petWindow == null || (!IsLoaded && !IsVisible)) return;
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var area = SystemParameters.WorkArea;
        const double gap = -8; // 面板自带 20 的透明外边距, 略负一点让视觉上贴紧宠物

        double left = petWindow.Left + petWindow.ActualWidth + gap;
        if (left + w > area.Right) left = petWindow.Left - w - gap;
        double top = petWindow.Top + petWindow.ActualHeight - h;

        Left = Math.Max(area.Left, Math.Min(left, area.Right - w));
        Top = Math.Max(area.Top, Math.Min(top, area.Bottom - h));
    }

    /// <summary>首次显示的位置: 跟随时贴宠物, 否则用保存的位置/屏幕右上角</summary>
    public void ApplyPosition()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (settings.FollowPet && petWindow != null)
        {
            Left = petWindow.Left + petWindow.ActualWidth;
            Top = petWindow.Top;
            return;
        }
        if (double.IsNaN(settings.PanelLeft) || double.IsNaN(settings.PanelTop)
            || settings.PanelLeft < 0 || settings.PanelTop < 0
            || settings.PanelLeft > SystemParameters.VirtualScreenWidth - 40
            || settings.PanelTop > SystemParameters.VirtualScreenHeight - 40)
        {
            Left = SystemParameters.WorkArea.Right - 300;
            Top = SystemParameters.WorkArea.Top + 20;
        }
        else
        {
            Left = settings.PanelLeft;
            Top = settings.PanelTop;
        }
    }

    // ── 刷新 ─────────────────────────────────────────────────

    public void Refresh()
    {
        var s = settings.Schedule;
        var now = DateTime.Now;
        var t = now.TimeOfDay;

        Apply(PickTheme(s, t));
        ApplyScale();
        Opacity = Math.Max(0.2, Math.Min(settings.PanelOpacity / 100.0, 1));
        Follow(); // 兜底: 缩放倍率等变化不一定触发事件

        tTime.Text = now.ToString("HH:mm:ss");
        tDate.Text = now.ToString("M月d日 dddd", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));

        double progress = s.Progress(t);
        tPercent.Text = $"{progress * 100:0}%";
        SetArc(progress);

        tWorked.Text = Duration(s.WorkedSeconds(t));
        tRemain.Text = s.IsOffWork(t) ? "已下班" : Duration(s.RemainingWorkSeconds(t));

        tStatus.Text = s.IsOffWork(t) ? "🚨 下班了！关电脑回家！"
            : s.IsWorkTime(t) ? (progress >= 0.75 ? "🔥 胜利在望，再坚持一下" : "💻 打工中...")
            : t < s.AmStart ? "☕ 还没上班" : "🍱 午休中，吃点好的";
    }

    /// <summary>按设置里的"面板大小"整体缩放, 缩放后窗口尺寸变化会触发 Follow 重新贴边</summary>
    private void ApplyScale()
    {
        double k = Math.Max(0.5, Math.Min(settings.PanelScale / 100.0, 3));
        if (Math.Abs(k - appliedScale) < 0.001) return;
        appliedScale = k;
        host.LayoutTransform = new ScaleTransform(k, k);
    }

    private static Theme PickTheme(WorkSchedule s, TimeSpan t) =>
        s.IsOffWork(t) ? Off : s.IsWorkTime(t) ? Working : t < s.AmStart ? Before : Lunch;

    private void Apply(Theme th)
    {
        // 状态主题或自定义颜色变了才重新套用, 避免每秒重启发光动画
        string key = $"{th.Accent1}|{settings.PanelColor}|{settings.RingColor}|{settings.GlowColor}";
        if (key == appliedKey) return;
        appliedKey = key;

        // 自定义颜色优先, 没设置(或无效)就用当前状态的配色
        var bg1 = Custom(settings.PanelColor) ?? Col(th.Bg1);
        var bg2 = Custom(settings.PanelColor) ?? Col(th.Bg2);
        var ring1 = Custom(settings.RingColor) ?? Col(th.Accent1);
        var ring2 = Custom(settings.RingColor) ?? Col(th.Accent2);
        var glowColor = Custom(settings.GlowColor) ?? Col(th.Accent1);

        bgStop1.Color = bg1; bgStop2.Color = bg2;
        bdStop1.Color = Col(th.Accent1); bdStop2.Color = Col(th.Accent2);
        arcStop1.Color = ring1; arcStop2.Color = ring2;
        glow.Color = glowColor;
        tTime.Foreground = new SolidColorBrush(ring1);
        tDate.Foreground = new SolidColorBrush(Col(th.Sub));
        tPercentCap.Foreground = new SolidColorBrush(Col(th.Sub));

        // 下班后外发光做呼吸脉冲, 其他状态静止
        if (th.Pulse)
        {
            glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0.25, 0.9, TimeSpan.FromSeconds(0.9))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }
        else
        {
            glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            glow.Opacity = 0.55;
        }
    }

    /// <summary>用 ArcSegment 画从 12 点钟方向顺时针的进度弧</summary>
    private void SetArc(double progress)
    {
        progress = Math.Max(0, Math.Min(progress, 0.9999)); // 满圈起终点重合会画不出来
        if (progress <= 0.0001) { arc.Data = Geometry.Empty; return; }

        double r = (RingSize - RingThickness) / 2;
        double c = RingSize / 2;
        double angle = progress * 2 * Math.PI;
        var start = new Point(c, c - r);
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));

        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, progress > 0.5, SweepDirection.Clockwise, true));
        arc.Data = new PathGeometry(new[] { fig });
    }

    private static string Duration(double seconds)
    {
        int sec = (int)seconds;
        return $"{sec / 3600}h {sec % 3600 / 60:00}m";
    }

    private static TextBlock Text(double size, FontWeight weight, string color, string font = "Microsoft YaHei UI") => new()
    {
        FontFamily = new FontFamily(font),
        FontSize = size,
        FontWeight = weight,
        Foreground = new SolidColorBrush(Col(color)),
    };

    private static Color? Custom(string hex) => PluginSettings.TryParseColor(hex, out var c) ? c : null;

    private static Color Col(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
