using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 环绕宠物的 HUD (v1): 头顶发光时钟 + 日期胶囊, 套在宠物身上的进度环(渐变弧 + 虚线外环 + 光晕),
/// 环内右侧的百分比, 脚边两张"已工作 / 还需"卡片, 以及星光/爱心/像素小猫点缀.
///
/// 坐标: 宠物画布是 500×500 单位 (头顶 y≈16, 脚 y≈490, 水平中心 x≈250), HUD 的画布在它四周留出余量
/// (x: -70..570, y: -160..560). 所有尺寸 = 单位 × s, s 随宠物窗口宽度和"面板大小"缩放;
/// 阴影/发光的半径也按 s 换算, 所以缩放后观感一致. s 变化时整体重建 (很少发生).
/// 窗口是透明且"鼠标穿透"的, 宠物照常响应点击/拖动.
/// </summary>
public class PetHud : Window, IPetPanel
{
    private record HudTheme(string Ring1, string Ring2, string Ring3, string Glow,
        string CardL1, string CardL2, string CardR1, string CardR2, bool Pulse);

    private static readonly HudTheme Working = new("#4FD8FF", "#8C7CFF", "#FF8AD8", "#5AA8FF", "#E04A68C0", "#E03A5AA6", "#E06A50C0", "#E05A46AA", false);
    private static readonly HudTheme Lunch = new("#FFD27A", "#FFA45C", "#FF7A9A", "#FFB060", "#E0B8803A", "#E0A06A2E", "#E0B8604A", "#E0A04E3C", false);
    private static readonly HudTheme Before = new("#A9D4E6", "#8FA6C9", "#B9A9E8", "#8FB4D8", "#E0586C8C", "#E04A5C7C", "#E06A62A0", "#E05A5290", false);
    private static readonly HudTheme Off = new("#FF7A7A", "#FF9A4A", "#FFC24A", "#FF5A5A", "#E0B04058", "#E0983048", "#E0B05A3A", "#E0984A2E", true);

    // 画布范围与环的默认几何 (单位)
    private const double X0 = -70, Y0 = -160, CanvasW = 640, CanvasH = 720;
    private const double RingCx = 250, RingCy = 205, RingR = 190, RingThickness = 16;

    private static readonly FontFamily DisplayFont = new("Segoe UI Black, Microsoft YaHei UI");
    private static readonly FontFamily TextFont = new("Microsoft YaHei UI, Segoe UI");
    private static readonly FontFamily SymbolFont = new("Segoe UI Symbol");

    private static readonly string[] CatRows =
    {
        "w.......w",
        "ww.....ww",
        "wwwwwwwww",
        "wwewwweww",
        "wwwwpwwww",
        "wwwwwwwww",
        ".wwwwwww.",
    };
    private static readonly Dictionary<char, string> CatPalette = new() { ['w'] = "#F6F1FF", ['e'] = "#7E8BFF", ['p'] = "#FFB4E0" };
    private static readonly string[] HeartRows =
    {
        ".pp.pp.",
        "ppppppp",
        "ppppppp",
        ".ppppp.",
        "..ppp..",
        "...p...",
    };
    private static readonly Dictionary<char, string> HeartPalette = new() { ['p'] = "#FF9AD5" };
    private static readonly Geometry SparkleGeometry = Geometry.Parse("M0,-1 Q0.08,-0.08 1,0 Q0.08,0.08 0,1 Q-0.08,0.08 -1,0 Q-0.08,-0.08 0,-1 Z");

    private readonly PluginSettings settings;
    private readonly Window? petWindow;
    private readonly DispatcherTimer timer;
    private readonly TechHud.BackLayer back = new(); // 背层: 光晕/底轨/虚线环/进度弧, 在宠物后面
    private Canvas? backRoot, target;

    private Canvas? root;
    private double s;                 // 单位 → 像素
    private string builtRingKey = ""; // 环参数变了也要重建
    private string? appliedKey;

    // 会被刷新/换色的元素
    private TextBlock tClock = null!, tDate = null!, tPercent = null!, tPercentCap = null!, tWorked = null!, tRemain = null!;
    private Path arc = null!;
    private GradientStop arc1 = null!, arc2 = null!, arc3 = null!, auraPeak = null!;
    private GradientStop cardL1 = null!, cardL2 = null!, cardR1 = null!, cardR2 = null!, border1 = null!, border2 = null!;
    private DropShadowEffect ringGlow = null!, textGlow = null!, cardGlow = null!;
    private double ringCy, ringR;

    public PetHud(PluginSettings settings, Window? petWindow)
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
        IsHitTestVisible = false;

        SourceInitialized += (_, _) => MakeClickThrough();
        timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); back.Show(); Restack(); timer.Start(); };
        Closed += (_, _) => { timer.Stop(); back.Close(); };

        if (petWindow != null)
        {
            var autoHide = new AutoHide(petWindow, settings, hide => SetHidden(hide));
            EventHandler onMoved = (_, _) => Reposition();
            SizeChangedEventHandler onSize = (_, _) => Reposition();
            petWindow.LocationChanged += onMoved;
            petWindow.SizeChanged += onSize;
            Closed += (_, _) =>
            {
                autoHide.Dispose();
                petWindow.LocationChanged -= onMoved;
                petWindow.SizeChanged -= onSize;
            };
        }
    }

    private void SetHidden(bool hide)
    {
        Visibility = hide ? Visibility.Hidden : Visibility.Visible;
        back.Visibility = Visibility;
        if (!hide) Restack();
    }

    /// <summary>前层置顶, 背层塞到宠物窗口正后方</summary>
    private void Restack()
    {
        try
        {
            var front = new WindowInteropHelper(this).Handle;
            if (front == IntPtr.Zero) return;
            WinZ.BringToTop(front);
            if (petWindow == null) return;
            var pet = new WindowInteropHelper(petWindow).Handle;
            var bk = new WindowInteropHelper(back).Handle;
            if (pet != IntPtr.Zero && bk != IntPtr.Zero) WinZ.PutBehind(bk, pet);
        }
        catch (Exception ex) { DebugLog.Write("PetHud.Restack: " + ex.Message); }
    }

    // ── 位置 / 缩放 ───────────────────────────────────────────

    public void ApplyPosition()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Reposition();
    }

    /// <summary>让 HUD 画布的 (250,250) 对准宠物窗口的中心; 缩放变了就重建</summary>
    private void Reposition()
    {
        double petW = petWindow?.ActualWidth ?? 0;
        if (petW <= 0) petW = 250; // 取不到宠物窗口时按默认缩放估一个
        double k = Math.Max(0.5, Math.Min(settings.PanelScale / 100.0, 3));
        double ns = petW / 500.0 * k;
        string ringKey = $"{settings.HudRingScale:0}|{settings.HudRingOffsetY:0}";

        if (root == null || Math.Abs(ns - s) > 0.01 * s || ringKey != builtRingKey)
        {
            Build(ns, ringKey);
            UpdateContent();
        }

        double cx, cy;
        if (petWindow != null)
        {
            cx = petWindow.Left + petW / 2;
            cy = petWindow.Top + petW / 2; // 宠物画布是正方形, 顶端对齐窗口
        }
        else
        {
            var area = SystemParameters.WorkArea;
            cx = area.Left + area.Width / 2;
            cy = area.Top + area.Height / 2;
        }
        Left = cx - (250 - X0) * s;
        Top = cy - (250 - Y0) * s;
        back.Left = Left; back.Top = Top; back.Width = Width; back.Height = Height;
    }

    // ── 界面搭建 ─────────────────────────────────────────────

    private double X(double x) => (x - X0) * s;
    private double Y(double y) => (y - Y0) * s;

    private void Build(double scale, string ringKey)
    {
        s = scale;
        builtRingKey = ringKey;
        appliedKey = null;
        ringR = RingR * Math.Max(0.6, Math.Min(settings.HudRingScale / 100.0, 1.6));
        ringCy = RingCy + Math.Max(-100, Math.Min(settings.HudRingOffsetY, 100));
        double cx = RingCx, cy = ringCy, R = ringR;

        Width = CanvasW * s;
        Height = CanvasH * s;
        root = new Canvas { Width = Width, Height = Height, IsHitTestVisible = false };
        Content = root;
        backRoot = new Canvas { Width = Width, Height = Height, IsHitTestVisible = false };
        back.Content = backRoot;
        target = backRoot; // 光晕/环/进度弧画在背层

        ringGlow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 24 * s, Opacity = 0.9 };
        textGlow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 16 * s, Opacity = 0.95 };
        cardGlow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 18 * s, Opacity = 0.8 };

        // 环周围的柔光带 (只在环附近有颜色, 环内不加色罩, 免得盖住宠物)
        auraPeak = new GradientStop(Colors.Transparent, 0.78);
        var aura = new Ellipse
        {
            Width = 2 * R * 1.3 * s, Height = 2 * R * 1.3 * s,
            Fill = new RadialGradientBrush(new GradientStopCollection
            {
                new GradientStop(Colors.Transparent, 0.0),
                new GradientStop(Colors.Transparent, 0.60),
                auraPeak,
                new GradientStop(Colors.Transparent, 1.0),
            }),
        };
        Put(aura, X(cx - R * 1.3), Y(cy - R * 1.3));

        // 虚线外环
        Ring(cx, cy, R * 1.13, new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), 2.2, dashed: true);
        // 底轨
        Ring(cx, cy, R, new SolidColorBrush(Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF)), RingThickness, dashed: false);

        // 进度弧
        arc1 = new GradientStop(Colors.White, 0); arc2 = new GradientStop(Colors.White, 0.5); arc3 = new GradientStop(Colors.White, 1);
        arc = new Path
        {
            Stroke = new LinearGradientBrush(new GradientStopCollection { arc1, arc2, arc3 }, 45),
            StrokeThickness = RingThickness * s,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Effect = ringGlow,
        };
        backRoot.Children.Add(arc);
        target = null;     // 其余 (时钟/卡片/点缀) 画在前层

        // 点缀: 星光 / 爱心 / 像素小猫
        Sparkle(111, 10, 26, "#D8B8FF");
        Sparkle(380, 10, 26, "#FFE08A");
        Sparkle(51, 207, 24, "#6FE3FF");
        Sparkle(363, 148, 12, "#FFFFFF");
        Sparkle(388, 300, 24, "#FF9AD5");
        PixelArt(CatRows, CatPalette, 30, 78 - 15, 158 - 12);
        PixelArt(CatRows, CatPalette, 30, 99 - 15, 254 - 12);
        PixelArt(HeartRows, HeartPalette, 22, 112 - 11, 205 - 9);

        // 头顶时钟 + 日期胶囊
        double ringTop = cy - R;
        tClock = Text("00:00:00", 64, DisplayFont, FontWeights.Black, new LinearGradientBrush(Colors.White, (Color)ColorConverter.ConvertFromString("#BFE9FF"), 90), 380);
        tClock.Effect = textGlow;
        PlaceText(tClock, 250, ringTop - 92, 64);

        tDate = Text("", 20, TextFont, FontWeights.Bold, Brushes.White, 200);
        var pill = new Border
        {
            Width = 200 * s, Height = 34 * s, CornerRadius = new CornerRadius(17 * s),
            Background = new LinearGradientBrush(Color.FromArgb(0xCC, 0x7C, 0x90, 0xD0), Color.FromArgb(0xCC, 0x68, 0x7A, 0xC0), 0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1 * s),
            Child = tDate,
        };
        tDate.VerticalAlignment = VerticalAlignment.Center;
        Put(pill, X(150), Y(ringTop - 32));

        // 环上 3 点钟位置的深色百分比牌: 深底白字, 在任何桌面背景下都看得清; 压在环的外沿, 不碰宠物/时钟/卡片
        border1 = new GradientStop(Colors.White, 0);
        border2 = new GradientStop(Colors.White, 1);
        tPercent = Text("0%", 30, DisplayFont, FontWeights.Black, Brushes.White, 104);
        tPercent.Effect = textGlow;
        tPercentCap = Text("今日进度", 12, TextFont, FontWeights.SemiBold, new SolidColorBrush(Color.FromArgb(0xE6, 0xD8, 0xE0, 0xFF)), 104);
        var pillStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        pillStack.Children.Add(tPercent);
        pillStack.Children.Add(tPercentCap);
        var percentPill = new Border
        {
            Width = 104 * s, Height = 60 * s, CornerRadius = new CornerRadius(18 * s),
            Background = new LinearGradientBrush(Color.FromArgb(0xE8, 0x1B, 0x22, 0x52), Color.FromArgb(0xE8, 0x2B, 0x2F, 0x6B), 90),
            BorderBrush = new LinearGradientBrush(new GradientStopCollection { border1, border2 }, 20),
            BorderThickness = new Thickness(2 * s),
            Child = pillStack,
            Effect = cardGlow,
        };
        Put(percentPill, X(cx + R - 52), Y(cy - 30));

        // 脚边两张卡片
        tWorked = BuildCard(-30, 355, "⏱", "已工作", right: false, out cardL1, out cardL2);
        tRemain = BuildCard(353 + 0, 355, "🏃", "还需", right: true, out cardR1, out cardR2);
    }

    private TextBlock BuildCard(double left, double top, string icon, string label, bool right, out GradientStop c1, out GradientStop c2)
    {
        var value = Text("--", 36, DisplayFont, FontWeights.Black, new LinearGradientBrush(Colors.White, (Color)ColorConverter.ConvertFromString("#CFEFFF"), 90), 140, TextAlignment.Left);
        value.Effect = textGlow;
        var iconText = new TextBlock { Text = icon, FontFamily = SymbolFont, FontSize = 20 * s, Foreground = Brushes.White };
        var labelText = Text(label, 18, TextFont, FontWeights.SemiBold, new SolidColorBrush(Color.FromArgb(0xF0, 0xF0, 0xF4, 0xFF)), 100, TextAlignment.Left);
        labelText.Margin = new Thickness(8 * s, 0, 0, 0);
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(iconText);
        head.Children.Add(labelText);
        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(value);

        c1 = new GradientStop(Colors.Gray, 0);
        c2 = new GradientStop(Colors.Gray, 1);
        var card = new Border
        {
            Width = 180 * s, Height = 95 * s, CornerRadius = new CornerRadius(20 * s),
            BorderThickness = new Thickness(2.5 * s),
            BorderBrush = new LinearGradientBrush(new GradientStopCollection { border1, border2 }, 20),
            Background = new LinearGradientBrush(new GradientStopCollection { c1, c2 }, 90),
            Padding = new Thickness(28 * s, 12 * s, 10 * s, 6 * s),
            Child = stack,
            Effect = cardGlow,
        };
        Put(card, X(left), Y(top));

        // 卡片角上的像素小猫 (左卡在左上角, 右卡在右上角)
        double catX = right ? left + 180 - 8 - 40 : left + 8;
        PixelArt(CatRows, CatPalette, 40, catX, top - 25);
        return value;
    }

    // ── 绘制小工具 ───────────────────────────────────────────

    private void Put(UIElement e, double px, double py)
    {
        Canvas.SetLeft(e, px);
        Canvas.SetTop(e, py);
        (target ?? root)!.Children.Add(e);
    }

    private TextBlock Text(string text, double sizeU, FontFamily font, FontWeight weight, Brush fg, double widthU, TextAlignment align = TextAlignment.Center) => new()
    {
        Text = text, FontFamily = font, FontSize = sizeU * s, FontWeight = weight, Foreground = fg,
        Width = widthU * s, TextAlignment = align,
    };

    /// <summary>文本水平居中于 centerX, 垂直居中于 centerY (按字号估算行高)</summary>
    private void PlaceText(TextBlock t, double centerX, double centerY, double sizeU) =>
        Put(t, X(centerX) - t.Width / 2, Y(centerY) - sizeU * 0.68 * s);

    /// <summary>以 (cx,cy) 为圆心, centerRadius 为描边中线半径的圆环</summary>
    private void Ring(double cx, double cy, double centerRadius, Brush stroke, double thicknessU, bool dashed)
    {
        double outer = centerRadius + thicknessU / 2;
        var e = new Ellipse
        {
            Width = 2 * outer * s, Height = 2 * outer * s,
            Stroke = stroke, StrokeThickness = thicknessU * s,
        };
        if (dashed)
        {
            e.StrokeDashArray = new DoubleCollection { 1.2, 4 };
            e.StrokeDashCap = PenLineCap.Round;
        }
        Put(e, X(cx - outer), Y(cy - outer));
    }

    private void Sparkle(double cx, double cy, double sizeU, string color)
    {
        var c = (Color)ColorConverter.ConvertFromString(color);
        var p = new Path
        {
            Data = SparkleGeometry, Stretch = Stretch.Fill, Fill = new SolidColorBrush(c),
            Width = sizeU * s, Height = sizeU * s,
            Effect = new DropShadowEffect { Color = c, ShadowDepth = 0, BlurRadius = sizeU * 0.7 * s, Opacity = 0.9 },
        };
        Put(p, X(cx - sizeU / 2), Y(cy - sizeU / 2));
    }

    /// <summary>用小方块拼像素画; (leftU, topU) 是左上角, widthU 是整幅宽度</summary>
    private void PixelArt(string[] rows, Dictionary<char, string> palette, double widthU, double leftU, double topU)
    {
        int w = rows[0].Length, h = rows.Length;
        double cell = widthU * s / w;
        var c = new Canvas
        {
            Width = w * cell, Height = h * cell,
            Effect = new DropShadowEffect { Color = Color.FromRgb(0xFF, 0xB4, 0xE0), ShadowDepth = 0, BlurRadius = 8 * s, Opacity = 0.7 },
        };
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (!palette.TryGetValue(rows[y][x], out var hex)) continue;
            var r = new Rectangle { Width = cell + 0.4, Height = cell + 0.4, Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) };
            Canvas.SetLeft(r, x * cell);
            Canvas.SetTop(r, y * cell);
            c.Children.Add(r);
        }
        Put(c, X(leftU), Y(topU));
    }

    // ── 刷新 ─────────────────────────────────────────────────

    public void Refresh()
    {
        Reposition(); // 兜底: 缩放等变化不一定触发窗口事件
        UpdateContent();
        Restack();
    }

    private void UpdateContent()
    {
        if (root == null) return;
        var sch = settings.Schedule;
        var now = DateTime.Now;
        var t = now.TimeOfDay;

        Apply(sch.IsOffWork(t) ? Off : sch.IsWorkTime(t) ? Working : t < sch.AmStart ? Before : Lunch);
        Opacity = Math.Max(0.2, Math.Min(settings.PanelOpacity / 100.0, 1));
        back.Opacity = Opacity;

        tClock.Text = now.ToString("HH:mm:ss");
        tDate.Text = now.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));

        double p = sch.Progress(t);
        tPercent.Text = $"{p * 100:0}%";
        SetArc(p);

        tWorked.Text = HM(sch.WorkedSeconds(t));
        tRemain.Text = sch.IsOffWork(t) ? "已下班" : HM(sch.RemainingWorkSeconds(t));
    }

    /// <summary>主题或自定义颜色变了才重新套用 (避免每秒重启发光动画)</summary>
    private void Apply(HudTheme th)
    {
        string key = $"{th.Ring1}|{settings.PanelColor}|{settings.RingColor}|{settings.GlowColor}";
        if (key == appliedKey) return;
        appliedKey = key;

        var customRing = Custom(settings.RingColor);
        var r1 = customRing ?? Col(th.Ring1);
        var r2 = customRing ?? Col(th.Ring2);
        var r3 = customRing ?? Col(th.Ring3);
        var glow = Custom(settings.GlowColor) ?? Col(th.Glow);
        var panel = Custom(settings.PanelColor);

        arc1.Color = r1; arc2.Color = r2; arc3.Color = r3;
        ringGlow.Color = glow;
        textGlow.Color = glow;
        cardGlow.Color = glow;
        auraPeak.Color = Color.FromArgb(0x46, glow.R, glow.G, glow.B);
        border1.Color = r1; border2.Color = r3;

        if (panel is { } pc)
        {
            var c = Color.FromArgb(0xE0, pc.R, pc.G, pc.B);
            cardL1.Color = cardL2.Color = cardR1.Color = cardR2.Color = c;
        }
        else
        {
            cardL1.Color = Col(th.CardL1); cardL2.Color = Col(th.CardL2);
            cardR1.Color = Col(th.CardR1); cardR2.Color = Col(th.CardR2);
        }

        // 下班后光晕做呼吸脉冲, 其他状态静止
        if (th.Pulse)
        {
            ringGlow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0.3, 1.0, TimeSpan.FromSeconds(0.9))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }
        else
        {
            ringGlow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            ringGlow.Opacity = 0.9;
        }
    }

    /// <summary>从 12 点钟方向顺时针画进度弧 (半径/圆心都是像素)</summary>
    private void SetArc(double progress)
    {
        progress = Math.Max(0, Math.Min(progress, 0.9999)); // 满圈起终点重合会画不出来
        if (progress <= 0.0001) { arc.Data = Geometry.Empty; return; }

        double r = ringR * s;
        double cx = X(RingCx), cy = Y(ringCy);
        double angle = progress * 2 * Math.PI;
        var start = new Point(cx, cy - r);
        var end = new Point(cx + r * Math.Sin(angle), cy - r * Math.Cos(angle));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, progress > 0.5, SweepDirection.Clockwise, true));
        arc.Data = new PathGeometry(new[] { fig });
    }

    private static string HM(double seconds)
    {
        int sec = (int)seconds;
        return $"{sec / 3600}h {sec % 3600 / 60:00}m";
    }

    private static Color? Custom(string hex) => PluginSettings.TryParseColor(hex, out var c) ? c : null;
    private static Color Col(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    // ── 鼠标穿透 ─────────────────────────────────────────────

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);

    /// <summary>加上 TRANSPARENT (鼠标事件穿透到下面的宠物) / NOACTIVATE (不抢焦点) / TOOLWINDOW (不出现在 Alt+Tab)</summary>
    private void MakeClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        const long add = WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        if (IntPtr.Size == 8)
            SetWindowLongPtr64(hwnd, GWL_EXSTYLE, (IntPtr)(GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64() | add));
        else
            SetWindowLong32(hwnd, GWL_EXSTYLE, GetWindowLong32(hwnd, GWL_EXSTYLE) | (int)add);
    }
}
