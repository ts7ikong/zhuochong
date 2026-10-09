using System.Globalization;
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
/// 矢量科幻面板: 用 desktop_pet_hud_assets 素材包里的 SVG 路径数据直接构造 WPF 几何, 任意缩放都不失真,
/// 不依赖位图. 边框/光环/粒子是素材包原样 (路径、渐变、描边宽度都来自 SVG), 文字、进度弧实时绘制.
/// 层次同 TechHud: 背层窗口画环内圆盘和粒子 (在宠物后面), 前层窗口画光环、边框、文字 (在宠物前面).
/// 主题色 / 自定义发光色用 HSV 色相旋转得到.
/// </summary>
public class VectorHud : Window, IPetPanel
{
    // 画布 (单位, 宠物画布 0..500, 水平中心 250)
    private const double X0 = -90, Y0 = -190, CanvasW = 680, CanvasH = 760;
    // 光环: 圆心和 SVG→单位缩放 (SVG 半径 220 → 约 150 单位)
    private const double HaloCx = 250, HaloCy = 150, HaloK = 0.68, HaloR = 220;

    // ── 素材包里的路径 (原样) ──
    private const string ClockBody = "M48 14 H675 L735 60 V130 L680 176 H45 L15 145 V48 Z";
    private const string ClockInner = "M64 29 H661 L715 66 M45 158 H665 L699 136";
    private const string ClockChevron = "M62 151 l22 0 -12 10 M94 151 l22 0 -12 10 M640 34 l22 0 -12 10 M672 34 l22 0 -12 10";
    private const string PillBody = "M34 8 H345 L370 30 V62 L345 84 H34 L10 62 V30 Z";
    private const string PillChevron = "M25 25 l16 0 -8 8 M339 59 l16 0 -8 8";
    private const string PanelBody = "M42 10 H360 L414 48 V143 L370 180 H42 L12 151 V43 Z";
    private const string PanelInner = "M55 25 H350 M37 158 H364";
    private const string PanelChevron = "M68 28 l26 0 -12 12 M101 28 l26 0 -12 12 M319 150 l25 0 -12 12";
    private const string PartCross1 = "M55 32 V62 M40 47 H70", PartCross2 = "M410 210 V244 M393 227 H427";
    private const string PartDia1 = "M270 55 l12 12 -12 12 -12 -12 Z", PartDia2 = "M100 200 l10 10 -10 10 -10 -10 Z";
    private const string PartStar1 = "M440 52 l5 12 12 5 -12 5 -5 12 -5 -12 -12 -5 12 -5 Z";
    private const string PartStar2 = "M65 255 l4 10 10 4 -10 4 -4 10 -4 -10 -10 -4 10 -4 Z";

    private static readonly FontFamily DisplayFont = new("Bahnschrift SemiBold, Segoe UI Black, Microsoft YaHei UI");
    private static readonly FontFamily TextFont = new("Microsoft YaHei UI, Segoe UI");
    private static readonly FontFamily SymbolFont = new("Segoe UI Symbol");

    private readonly PluginSettings settings;
    private readonly Window? petWindow;
    private readonly DispatcherTimer timer;
    private readonly TechHud.BackLayer back = new();

    private Canvas? root;
    private double s;
    private string? appliedKey;

    // 主题相关: 每个 "颜色槽" 记录原始色, Apply 时按主题旋转
    private readonly List<(Action<Color> set, Color baseColor)> slots = new();
    private TextBlock tClock = null!, tDate = null!, tStatus = null!, tPercent = null!,
        tLabelL = null!, tValueL = null!, tLabelR = null!, tValueR = null!;
    private Path arc = null!;
    private GradientStop arcA = null!, arcM = null!, arcB = null!;
    private DropShadowEffect arcGlow = null!, textGlow = null!;
    private Ellipse haloTrack = null!;
    private Canvas haloLayer = null!;
    private double ringCx, ringCy, ringR, ringW;

    public VectorHud(PluginSettings settings, Window? petWindow)
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

        SourceInitialized += (_, _) => WinZ.ClickThrough(new WindowInteropHelper(this).Handle);
        timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); back.Show(); Restack(); timer.Start(); };
        Closed += (_, _) => { timer.Stop(); back.Close(); };

        if (petWindow != null)
        {
            new AutoHide(petWindow, settings, hide => SetHidden(hide));
            petWindow.LocationChanged += (_, _) => Reposition();
            petWindow.SizeChanged += (_, _) => Reposition();
        }
    }

    private void SetHidden(bool hide)
    {
        Visibility = hide ? Visibility.Hidden : Visibility.Visible;
        back.Visibility = Visibility;
        if (!hide) Restack();
    }

    // ── 位置 / 缩放 ───────────────────────────────────────────

    public void ApplyPosition()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Reposition();
    }

    private void Reposition()
    {
        double petW = petWindow?.ActualWidth ?? 0;
        if (petW <= 0) petW = 250;
        double k = Math.Max(0.5, Math.Min(settings.PanelScale / 100.0, 3));
        double ns = petW / 500.0 * k;
        if (root == null || Math.Abs(ns - s) > 0.01 * s)
        {
            Build(ns);
            UpdateContent();
        }

        double cx, cy;
        if (petWindow != null) { cx = petWindow.Left + petW / 2; cy = petWindow.Top + petW / 2; }
        else
        {
            var area = SystemParameters.WorkArea;
            cx = area.Left + area.Width / 2; cy = area.Top + area.Height / 2;
        }
        Left = cx - (250 - X0) * s;
        Top = cy - (250 - Y0) * s;
        back.Left = Left; back.Top = Top; back.Width = Width; back.Height = Height;
    }

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
        catch (Exception ex) { DebugLog.Write("VectorHud.Restack: " + ex.Message); }
    }

    // ── 绘制 ─────────────────────────────────────────────────

    private double X(double x) => (x - X0) * s;
    private double Y(double y) => (y - Y0) * s;

    private static Color Col(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Round(a * 255), c.R, c.G, c.B);

    /// <summary>登记一个随主题旋转的颜色槽, 返回初始值</summary>
    private Color Slot(string hex, double alpha, Action<Color> set)
    {
        var c = WithAlpha(Col(hex), alpha);
        slots.Add((set, c));
        return c;
    }

    /// <summary>渐变停靠点: 颜色随主题旋转</summary>
    private GradientStop Stop(string hex, double offset)
    {
        var gs = new GradientStop(Col(hex), offset);
        slots.Add((c => gs.Color = c, Col(hex)));
        return gs;
    }

    private LinearGradientBrush Grad(params (string hex, double off)[] stops)
    {
        var col = new GradientStopCollection();
        foreach (var (hex, off) in stops) col.Add(Stop(hex, off));
        return new LinearGradientBrush(col, new Point(0, 0), new Point(1, 1));
    }

    private SolidColorBrush Solid(string hex, double alpha)
    {
        var b = new SolidColorBrush(WithAlpha(Col(hex), alpha));
        slots.Add((c => b.Color = c, b.Color));
        return b;
    }

    /// <summary>把 SVG 路径 (SVG 像素坐标) 摆到画布上: 左上角 (leftU, topU), 缩放 k (SVG 像素 → 单位)</summary>
    private Path SvgPath(Canvas into, string d, double leftU, double topU, double k, Brush? fill, Brush? stroke, double strokeW, double glowStd = 0, double opacity = 1, bool glowColored = true)
    {
        var g = Geometry.Parse(d).Clone();
        g.Transform = new MatrixTransform(k * s, 0, 0, k * s, X(leftU), Y(topU));
        var p = new Path
        {
            Data = g, Fill = fill, Stroke = stroke, StrokeThickness = strokeW * k * s,
            StrokeLineJoin = PenLineJoin.Miter, Opacity = opacity, IsHitTestVisible = false,
        };
        if (glowStd > 0 && glowColored)
        {
            var eff = new DropShadowEffect { ShadowDepth = 0, BlurRadius = glowStd * 2 * k * s, Opacity = 0.9, Color = Col("#3A9BFF") };
            slots.Add((c => eff.Color = Color.FromRgb(c.R, c.G, c.B), Col("#3A9BFF")));
            p.Effect = eff;
        }
        into.Children.Add(p);
        return p;
    }

    private TextBlock Text(string text, double sizeU, FontFamily font, FontWeight weight, Brush fg, double widthU, TextAlignment align) => new()
    {
        Text = text, FontFamily = font, FontSize = sizeU * s, FontWeight = weight, Foreground = fg,
        Width = widthU * s, TextAlignment = align, IsHitTestVisible = false,
    };

    private void PutAt(UIElement e, double xU, double cyU, double sizeU, double widthU, bool center)
    {
        Canvas.SetLeft(e, X(center ? xU - widthU / 2 : xU));
        Canvas.SetTop(e, Y(cyU) - sizeU * 0.68 * s);
        root!.Children.Add(e);
    }

    private void Build(double scale)
    {
        s = scale;
        appliedKey = null;
        slots.Clear();

        Width = CanvasW * s;
        Height = CanvasH * s;
        root = new Canvas { Width = Width, Height = Height, IsHitTestVisible = false };
        Content = root;

        textGlow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 10 * s, Opacity = 0.9, Color = Col("#4FA8FF") };
        slots.Add((c => textGlow.Color = Color.FromRgb(c.R, c.G, c.B), Col("#4FA8FF")));

        // ── 光环 (halo) ──
        ringCx = HaloCx; ringCy = HaloCy; ringR = HaloR * HaloK; ringW = 18 * HaloK;
        double hl = HaloCx - 300 * HaloK, ht = HaloCy - 300 * HaloK; // SVG 600x600 的左上角
        back.Build(s, X(ringCx), Y(ringCy), ringR * 0.92 * s, CanvasW, CanvasH);
        AddParticles(hl, ht);

        haloLayer = new Canvas { Width = Width, Height = Height, IsHitTestVisible = false };
        back.Root!.Children.Add(haloLayer);
        haloTrack = Circle(haloLayer, 220, 20, Solid("#354D91", 0.28), null);
        Circle(haloLayer, 220, 2, Solid("#9EB8FF", 0.55), new DoubleCollection { 2.5, 5 });
        Circle(haloLayer, 197, 3, Solid("#4DDFFF", 0.7), new DoubleCollection { 0.667, 4 });

        arcA = Stop("#39E7FF", 0); arcM = Stop("#438BFF", 0.52); arcB = Stop("#C05CFF", 1);
        arcGlow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 14 * HaloK * s, Opacity = 0.9, Color = Col("#3A9BFF") };
        slots.Add((c => arcGlow.Color = Color.FromRgb(c.R, c.G, c.B), Col("#3A9BFF")));
        arc = new Path
        {
            Stroke = new LinearGradientBrush(new GradientStopCollection { arcA, arcM, arcB },
                new Point(X(ringCx - ringR * 0.3), Y(ringCy - ringR)), new Point(X(ringCx + ringR), Y(ringCy + ringR * 0.2)))
            { MappingMode = BrushMappingMode.Absolute },
            StrokeThickness = ringW * s,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Effect = arcGlow, IsHitTestVisible = false,
        };
        back.Root.Children.Add(arc);

        // ── 时钟 (760x190) ──
        double ck = 0.45, cl = 250 - 380 * ck, ct = -165;
        SvgPath(root, ClockBody, cl, ct, ck, Solid("#08142F", 0.93), Grad(("#2DE6FF", 0), ("#467DFF", 0.55), ("#C45BFF", 1)), 3, 5);
        SvgPath(root, ClockInner, cl, ct, ck, null, Solid("#69E8FF", 1), 2, opacity: 0.8);
        SvgPath(root, ClockChevron, cl, ct, ck, null, Grad(("#2DE6FF", 0), ("#467DFF", 0.55), ("#C45BFF", 1)), 5);
        var clockB = Stop("#BFE9FF", 1);
        tClock = Text("00:00:00", 54, DisplayFont, FontWeights.Normal,
            new LinearGradientBrush(new GradientStopCollection { new GradientStop(Colors.White, 0.2), clockB }, 90), 300, TextAlignment.Center);
        tClock.Effect = textGlow;
        PutAt(tClock, 250, ct + 82 * ck, 54, 300, true);
        tDate = Text("", 16, TextFont, FontWeights.SemiBold, new SolidColorBrush(Color.FromArgb(0xEE, 0xE4, 0xEE, 0xFF)), 220, TextAlignment.Center);
        PutAt(tDate, 250, ct + 140 * ck, 16, 220, true);

        // ── 状态胶囊 (380x92) ──
        double pk = 0.45, pl = 250 - 190 * pk, pt = -70;
        SvgPath(root, PillBody, pl, pt, pk, Solid("#08142F", 0.94), Grad(("#2CE6FF", 0), ("#9B61FF", 1)), 3, 4);
        SvgPath(root, PillChevron, pl, pt, pk, null, Solid("#52E7FF", 1), 3);
        tStatus = Text("打工中…", 17, TextFont, FontWeights.SemiBold, Brushes.White, 130, TextAlignment.Center);
        PutAt(tStatus, 250, pt + 46 * pk, 17, 130, true);

        // ── 百分比牌 (环 3 点钟位置) ──
        tPercent = Text("0%", 28, DisplayFont, FontWeights.Normal, Brushes.White, 100, TextAlignment.Center);
        tPercent.Effect = textGlow;
        var cap = Text("今日进度", 11, TextFont, FontWeights.SemiBold, new SolidColorBrush(Color.FromArgb(0xE6, 0xD8, 0xE0, 0xFF)), 100, TextAlignment.Center);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(tPercent); stack.Children.Add(cap);
        var pill = new Border
        {
            Width = 100 * s, Height = 56 * s, CornerRadius = new CornerRadius(16 * s),
            Background = new LinearGradientBrush(Color.FromArgb(0xEA, 0x0B, 0x16, 0x35), Color.FromArgb(0xEA, 0x14, 0x1A, 0x44), 90),
            BorderBrush = Grad(("#2DE6FF", 0), ("#C45BFF", 1)),
            BorderThickness = new Thickness(2 * s), Child = stack, IsHitTestVisible = false,
        };
        Canvas.SetLeft(pill, X(ringCx + ringR - 50)); Canvas.SetTop(pill, Y(ringCy - 28));
        root.Children.Add(pill);

        // ── 脚边两张面板 (430x190) ──
        double ak = 0.5, aw = 430 * ak;
        double lLeft = -40, rLeft = 325, aTop = 335;
        SvgPath(root, PanelBody, lLeft, aTop, ak, Solid("#09152F", 0.94), Grad(("#29E6FF", 0), ("#497BFF", 1)), 3, 4);
        SvgPath(root, PanelInner, lLeft, aTop, ak, null, Solid("#55E7FF", 1), 2, opacity: 0.75);
        SvgPath(root, PanelChevron, lLeft, aTop, ak, null, Solid("#2EDCFF", 1), 5);
        SvgPath(root, PanelBody, rLeft, aTop, ak, Solid("#10102F", 0.94), Grad(("#4D82FF", 0), ("#D34DFF", 1)), 3, 4);
        SvgPath(root, PanelInner, rLeft, aTop, ak, null, Solid("#B779FF", 1), 2, opacity: 0.75);
        SvgPath(root, PanelChevron, rLeft, aTop, ak, null, Solid("#D05CFF", 1), 5);

        BuildCardText(lLeft, aTop, ak, "⏱", "#6FE8FF", "已工作", out tLabelL, out tValueL, "#CFF6FF");
        BuildCardText(rLeft, aTop, ak, "⌛", "#D9A8FF", "还需", out tLabelR, out tValueR, "#EBD9FF");
    }

    private void BuildCardText(double left, double top, double k, string icon, string iconColor, string label, out TextBlock tLabel, out TextBlock tValue, string valueTint)
    {
        var ic = new TextBlock { Text = icon, FontFamily = SymbolFont, FontSize = 26 * k * s * 1.5, IsHitTestVisible = false };
        var iconBrush = Solid(iconColor, 1);
        ic.Foreground = iconBrush;
        ic.Effect = textGlow;
        Canvas.SetLeft(ic, X(left + 62 * k)); Canvas.SetTop(ic, Y(top + 40 * k));
        root!.Children.Add(ic);

        tLabel = Text(label, 14, TextFont, FontWeights.SemiBold, new SolidColorBrush(Color.FromArgb(0xF0, 0xE4, 0xF2, 0xFF)), 120, TextAlignment.Left);
        PutAt(tLabel, left + 118 * k, top + 66 * k, 14, 120, false);

        var vb = Stop(valueTint, 1);
        tValue = Text("--", 30, DisplayFont, FontWeights.Normal,
            new LinearGradientBrush(new GradientStopCollection { new GradientStop(Colors.White, 0.2), vb }, 90), 180, TextAlignment.Left);
        tValue.Effect = textGlow;
        PutAt(tValue, left + 62 * k, top + 124 * k, 30, 180, false);
    }

    /// <summary>光环素材里的圆 (半径 / 描边都是 SVG 像素)</summary>
    private Ellipse Circle(Canvas into, double svgR, double strokeW, Brush stroke, DoubleCollection? dash)
    {
        double r = svgR * HaloK;
        var e = new Ellipse
        {
            Width = 2 * r * s, Height = 2 * r * s, Stroke = stroke, StrokeThickness = strokeW * HaloK * s, IsHitTestVisible = false,
        };
        if (dash != null) e.StrokeDashArray = dash;
        Canvas.SetLeft(e, X(ringCx - r)); Canvas.SetTop(e, Y(ringCy - r));
        into.Children.Add(e);
        return e;
    }

    /// <summary>素材包的粒子 (500x300): 放到背层窗口 (在宠物后面), 摆在光环周围</summary>
    private void AddParticles(double haloLeft, double haloTop)
    {
        var layer = back.Root;
        if (layer == null) return;
        double k = 0.7, left = HaloCx - 250 * k, top = HaloCy - 160 * k;
        var grad = Grad(("#32E9FF", 0), ("#D15CFF", 1));
        foreach (var d in new[] { PartCross1, PartCross2, PartDia1, PartDia2 })
            SvgPath(layer, d, left, top, k, null, grad, 3, 3);
        var star = Solid("#D7A4FF", 1);
        foreach (var d in new[] { PartStar1, PartStar2 })
            SvgPath(layer, d, left, top, k, star, null, 0, 3);
    }

    private Geometry ArcGeometry(double fromDeg, double toDeg)
    {
        double r = ringR * s, cx = X(ringCx), cy = Y(ringCy);
        Point P(double deg)
        {
            double a = deg * Math.PI / 180;
            return new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a));
        }
        var fig = new PathFigure { StartPoint = P(fromDeg), IsClosed = false };
        fig.Segments.Add(new ArcSegment(P(toDeg), new Size(r, r), 0, toDeg - fromDeg > 180, SweepDirection.Clockwise, true));
        return new PathGeometry(new[] { fig });
    }

    // ── 刷新 ─────────────────────────────────────────────────

    public void Refresh()
    {
        Reposition();
        UpdateContent();
        Restack();
    }

    private void UpdateContent()
    {
        if (root == null) return;
        var sch = settings.Schedule;
        var now = DateTime.Now;
        var t = now.TimeOfDay;

        bool off = sch.IsOffWork(t);
        var th = off ? TechHud.Off : sch.IsWorkTime(t) ? TechHud.Working : t < sch.AmStart ? TechHud.Before : TechHud.Lunch;
        Apply(th);
        double op = Math.Max(0.2, Math.Min(settings.PanelOpacity / 100.0, 1));
        Opacity = op; back.Opacity = op;

        tClock.Text = now.ToString("HH:mm:ss");
        tDate.Text = now.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
        tStatus.Text = off ? "下班啦！" : th == TechHud.Working ? "打工中…" : th == TechHud.Lunch ? "午休中…" : "还没上班";

        double p = sch.Progress(t);
        tPercent.Text = $"{p * 100:0}%";
        p = Math.Max(0, Math.Min(p, 0.9999));
        arc.Data = p <= 0.0001 ? Geometry.Empty : ArcGeometry(0, p * 360);

        tValueL.Text = HM(sch.WorkedSeconds(t));
        tLabelR.Text = off ? "状态" : "还需";
        tValueR.Text = off ? "已下班" : HM(sch.RemainingWorkSeconds(t));
    }

    private void Apply(TechHud.TechTheme th)
    {
        double hue = th.Hue;
        var customGlow = Custom(settings.GlowColor);
        if (customGlow is { } gc)
        {
            TechHud.Rgb2Hsv(gc.R, gc.G, gc.B, out double gh, out _, out _);
            hue = gh - 215;
        }
        var ring = Custom(settings.RingColor);
        string key = $"{th.Name}|{settings.RingColor}|{settings.GlowColor}";
        if (key == appliedKey) return;
        appliedKey = key;

        foreach (var (set, baseColor) in slots) set(Rotate(baseColor, hue, th.Sat));
        if (ring is { } rc)
        {
            arcA.Color = rc; arcM.Color = rc; arcB.Color = rc;
            arcGlow.Color = rc;
        }

        if (th.Pulse)
        {
            haloLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 1.0, TimeSpan.FromSeconds(0.9))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            arc.BeginAnimation(OpacityProperty, new DoubleAnimation(0.5, 1.0, TimeSpan.FromSeconds(0.9))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            haloLayer.BeginAnimation(OpacityProperty, null); haloLayer.Opacity = 1;
            arc.BeginAnimation(OpacityProperty, null); arc.Opacity = 1;
        }
    }

    private static Color Rotate(Color c, double hue, double sat)
    {
        if (Math.Abs(hue) < 0.5 && Math.Abs(sat - 1) < 0.01) return c;
        TechHud.Rgb2Hsv(c.R, c.G, c.B, out double h, out double sv, out double v);
        TechHud.Hsv2Rgb((h + hue + 360) % 360, Math.Min(1, sv * sat), v, out byte r, out byte g, out byte b);
        return Color.FromArgb(c.A, r, g, b);
    }

    private static string HM(double seconds)
    {
        int sec = (int)seconds;
        return $"{sec / 3600}h {sec % 3600 / 60:00}m";
    }

    private static Color? Custom(string hex) => PluginSettings.TryParseColor(hex, out var c) ? c : null;
}
