using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 科幻面板: 按设计贴图 (Assets/Hud/*.png, 已抠掉贴图里烘焙的文字和进度弧) 拼出的 HUD ——
/// 头顶发光时钟 + 日期, 状态胶囊, 套在宠物身上的进度环, 环边百分比, 脚边两张"已工作 / 还需"卡片.
/// 文字全部是实时绘制的; 进度弧是矢量绘制并带发光.
///
/// 两层窗口: 本窗口 (前层) 画环/卡片/文字, 压在宠物上面; 另有一个"背层"窗口只画环内的深色圆盘,
/// 被塞到宠物窗口的正后方, 这样宠物站在深色圆盘前面, 环和文字在宠物前面, 和设计图的层次一致.
/// 两个窗口都是透明且鼠标穿透的.
///
/// 主题 (上班/午休/上班前/下班) 通过对贴图做色相旋转得到, 自定义"发光颜色"也走同一套 (把蓝色旋转到指定色相).
/// 坐标: 单位同 PetHud (宠物画布 500 单位, 水平中心 x=250). 设计图的像素 → 单位的换算见 FromComp.
/// </summary>
public class TechHud : Window, IPetPanel
{
    internal record TechTheme(string Name, double Hue, double Sat, string Arc1, string Arc2, string Glow, bool Pulse);

    internal static readonly TechTheme Working = new("working", 0, 1.0, "#5FE6FF", "#C79BFF", "#4FA8FF", false);
    internal static readonly TechTheme Lunch = new("lunch", -95, 1.0, "#7DF2A0", "#FFE48A", "#5FE29A", false);
    internal static readonly TechTheme Before = new("before", 0, 0.35, "#A9D4E6", "#B9A9E8", "#8FB4D8", false);
    internal static readonly TechTheme Off = new("off", 150, 1.0, "#FF8A5A", "#FFC24A", "#FF7A5A", true);

    // 画布范围 (单位): 比宠物画布 (0..500) 向四周留出余量
    private const double X0 = -170, Y0 = -210, CanvasW = 860, CanvasH = 900;

    // 设计图 (1536x1024) → 单位: 宠物在设计图里中心 x=760, 头顶 y=315, 对应宠物画布 (250, 16), 缩放 0.8
    private const double CompK = 0.8;
    private static double FromCompX(double x) => (x - 760) * CompK + 250;
    private static double FromCompY(double y) => (y - 315) * CompK + 16;

    // 进度环在贴图 ring.png 里的圆心 (px) 和进度带半径/粗细 (px)
    private const double RingSrcCx = 295.4, RingSrcCy = 271.6, BandR = 217, BandW = 40;

    private static readonly FontFamily DisplayFont = new("Bahnschrift SemiBold, Segoe UI Black, Microsoft YaHei UI");
    private static readonly FontFamily TextFont = new("Microsoft YaHei UI, Segoe UI");

    private static readonly Dictionary<string, BitmapSource> Raw = new();
    private static readonly Dictionary<string, BitmapSource> Tinted = new();

    private readonly PluginSettings settings;
    private readonly Window? petWindow;
    private readonly DispatcherTimer timer;
    private readonly BackLayer back;

    private Canvas? root;
    private double s;
    private string? appliedKey;

    private readonly List<(Image img, string name)> sprites = new();
    private readonly List<(Image img, string name)> decorations = new();
    private TextBlock tClock = null!, tDate = null!, tStatus = null!, tPercent = null!,
        tLabelL = null!, tValueL = null!, tLabelR = null!, tValueR = null!;
    private Path arc = null!, track = null!;
    private Image ringImage = null!;
    private GradientStop arcA = null!, arcB = null!;
    private GradientStop clockB = null!, valueB = null!;
    private DropShadowEffect arcGlow = null!, textGlow = null!;
    private double ringCx, ringCy, bandR, bandW;

    public TechHud(PluginSettings settings, Window? petWindow)
    {
        this.settings = settings;
        this.petWindow = petWindow;
        back = new BackLayer();

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
        Loaded += (_, _) =>
        {
            Refresh();
            back.Show();
            Restack();
            timer.Start();
        };
        Closed += (_, _) => { timer.Stop(); back.Close(); };

        if (petWindow != null)
        {
            petWindow.LocationChanged += (_, _) => Reposition();
            petWindow.SizeChanged += (_, _) => Reposition();
        }
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
        if (petWindow != null)
        {
            cx = petWindow.Left + petW / 2;
            cy = petWindow.Top + petW / 2;
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

    /// <summary>每秒校正一次层次: 前层置顶, 背层塞到宠物窗口正后方</summary>
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
        catch (Exception ex) { DebugLog.Write("TechHud.Restack: " + ex.Message); }
    }

    // ── 贴图 ─────────────────────────────────────────────────

    private static BitmapSource Load(string name)
    {
        if (Raw.TryGetValue(name, out var bmp)) return bmp;
        using var st = typeof(TechHud).Assembly.GetManifestResourceStream("Hud." + name + ".png")
                       ?? throw new FileNotFoundException("缺少内嵌贴图 Hud." + name + ".png");
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = st;
        bi.EndInit();
        var conv = new FormatConvertedBitmap(bi, PixelFormats.Bgra32, null, 0);
        conv.Freeze();
        Raw[name] = conv;
        return conv;
    }

    /// <summary>对贴图做色相旋转 / 饱和度缩放 (按主题缓存)</summary>
    private static BitmapSource Tint(string name, double hue, double sat)
    {
        if (Math.Abs(hue) < 0.5 && Math.Abs(sat - 1) < 0.01) return Load(name);
        string key = $"{name}|{hue:0}|{sat:0.00}";
        if (Tinted.TryGetValue(key, out var cached)) return cached;

        var src = Load(name);
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        src.CopyPixels(px, stride, 0);
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] == 0) continue;
            Rgb2Hsv(px[i + 2], px[i + 1], px[i], out double hh, out double ss, out double vv);
            hh = (hh + hue + 360) % 360;
            ss = Math.Min(1, ss * sat);
            Hsv2Rgb(hh, ss, vv, out byte r, out byte g, out byte b);
            px[i] = b; px[i + 1] = g; px[i + 2] = r;
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        bmp.Freeze();
        Tinted[key] = bmp;
        return bmp;
    }

    internal static void Rgb2Hsv(byte rb, byte gb, byte bb, out double h, out double s, out double v)
    {
        double r = rb / 255.0, g = gb / 255.0, b = bb / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) h = 0;
        else if (max == r) h = 60 * (((g - b) / d + 6) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
    }

    internal static void Hsv2Rgb(double h, double s, double v, out byte r, out byte g, out byte b)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        double rr, gg, bb;
        switch ((int)(h / 60) % 6)
        {
            case 0: rr = c; gg = x; bb = 0; break;
            case 1: rr = x; gg = c; bb = 0; break;
            case 2: rr = 0; gg = c; bb = x; break;
            case 3: rr = 0; gg = x; bb = c; break;
            case 4: rr = x; gg = 0; bb = c; break;
            default: rr = c; gg = 0; bb = x; break;
        }
        r = (byte)Math.Round((rr + m) * 255); g = (byte)Math.Round((gg + m) * 255); b = (byte)Math.Round((bb + m) * 255);
    }

    // ── 界面搭建 ─────────────────────────────────────────────

    private double X(double x) => (x - X0) * s;
    private double Y(double y) => (y - Y0) * s;

    /// <summary>一张贴图: 以设计图坐标 (compCx, compCy) 为中心, 设计图缩放 compScale (设计图像素 / 贴图像素)</summary>
    private (double left, double top, double k) AddSprite(string name, double compCx, double compCy, double compScale, bool deco = false)
    {
        var src = Load(name);
        double k = compScale * CompK; // 贴图像素 → 单位
        double wU = src.PixelWidth * k, hU = src.PixelHeight * k;
        double left = FromCompX(compCx) - wU / 2, top = FromCompY(compCy) - hU / 2;
        var img = new Image
        {
            Source = src, Width = wU * s, Height = hU * s, Stretch = Stretch.Fill, IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        Put(img, X(left), Y(top));
        if (deco) decorations.Add((img, name)); else sprites.Add((img, name));
        return (left, top, k);
    }

    private void Build(double scale)
    {
        s = scale;
        appliedKey = null;
        sprites.Clear();
        decorations.Clear();

        Width = CanvasW * s;
        Height = CanvasH * s;
        root = new Canvas { Width = Width, Height = Height, IsHitTestVisible = false };
        Content = root;

        arcGlow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 14 * s, Opacity = 0.9 };
        textGlow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 10 * s, Opacity = 0.9 };

        // 进度环: 贴图圆心对准设计图 (775,470), 设计图缩放 0.81
        {
            var src = Load("ring");
            double kk = 0.81 * CompK;
            ringCx = FromCompX(775); ringCy = FromCompY(470);
            ringImage = new Image
            {
                Source = src, Width = src.PixelWidth * kk * s, Height = src.PixelHeight * kk * s,
                Stretch = Stretch.Fill, IsHitTestVisible = false,
            };
            RenderOptions.SetBitmapScalingMode(ringImage, BitmapScalingMode.HighQuality);
            Put(ringImage, X(ringCx - RingSrcCx * kk), Y(ringCy - RingSrcCy * kk));
            sprites.Add((ringImage, "ring"));
            bandR = BandR * kk; bandW = BandW * kk;
        }
        back.Build(s, X(ringCx), Y(ringCy), bandR * 0.93 * s);

        // 进度轨 (只补在贴图里被抠掉的那一段 -12°..80°) + 进度弧
        track = new Path
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0x2C, 0x8C, 0xC8, 0xFF)),
            StrokeThickness = bandW * 0.8 * s,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Data = ArcGeometry(-12, 80),
        };
        root.Children.Add(track);

        arcA = new GradientStop(Colors.White, 0);
        arcB = new GradientStop(Colors.White, 1);
        arc = new Path
        {
            Stroke = new LinearGradientBrush(new GradientStopCollection { arcA, arcB },
                new Point(X(ringCx), Y(ringCy - bandR)), new Point(X(ringCx + bandR), Y(ringCy)))
            { MappingMode = BrushMappingMode.Absolute },
            StrokeThickness = bandW * 0.78 * s,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Effect = arcGlow,
        };
        root.Children.Add(arc);

        // 点缀 (设计图里的加号 / 星光)
        AddSprite("plus_b", 618, 525, 0.9, deco: true);
        AddSprite("plus_a", 1066, 512, 0.9, deco: true);
        AddSprite("plus_a", 534, 660, 0.7, deco: true);
        AddSprite("spark_b", 1022, 345, 0.7, deco: true);
        AddSprite("spark_a", 545, 338, 0.6, deco: true);

        // 头顶时钟 (设计图 485..1025 × 55..215) + 日期
        clockB = new GradientStop(Colors.White, 1);
        var (cl, ct, ck) = AddSprite("clock", 755, 135, 0.81);
        tClock = Text("00:00:00", 66, DisplayFont, FontWeights.Normal,
            new LinearGradientBrush(new GradientStopCollection { new GradientStop(Colors.White, 0.2), clockB }, 90), 340);
        tClock.Effect = textGlow;
        PlaceCenter(tClock, cl + 333 * ck, ct + 78 * ck, 66);

        tDate = Text("", 19, TextFont, FontWeights.SemiBold, new SolidColorBrush(Color.FromArgb(0xEE, 0xE4, 0xEE, 0xFF)), 240);
        PlaceCenter(tDate, cl + 333 * ck, ct + 153 * ck, 19);
        double lineY = ct + 153 * ck;
        foreach (double dx in new[] { -112.0, 112.0 })
        {
            var line = new Rectangle
            {
                Width = 26 * s, Height = Math.Max(1, 1.2 * s),
                Fill = new SolidColorBrush(Color.FromArgb(0x90, 0xA8, 0xCC, 0xFF)),
            };
            Put(line, X(cl + 333 * ck + dx) - 13 * s, Y(lineY) - line.Height / 2);
        }

        // 状态胶囊 (设计图 645..885 × 222..270)
        var (sl, st, sk) = AddSprite("status", 765, 246, 0.82);
        tStatus = Text("打工中…", 19, TextFont, FontWeights.SemiBold, Brushes.White, 130);
        PlaceCenter(tStatus, sl + 171 * sk, st + 33 * sk, 19);

        // 百分比框 (环右侧, 压在深色圆盘的边上)
        var (pl, pt, pk) = AddSprite("percent", 965, 515, 0.62);
        tPercent = Text("0%", 40, DisplayFont, FontWeights.Normal, Brushes.White, 118);
        tPercent.Effect = textGlow;
        PlaceCenter(tPercent, pl + 125 * pk, pt + 87 * pk, 40);

        // 脚边两张卡片
        valueB = new GradientStop(Colors.White, 1);
        var (ll, lt, lk) = AddSprite("card_l", 455, 775, 0.9);
        tLabelL = Text("已工作", 20, TextFont, FontWeights.SemiBold, new SolidColorBrush(Color.FromArgb(0xF0, 0xE4, 0xF2, 0xFF)), 120, TextAlignment.Left);
        PlaceLeft(tLabelL, ll + 175 * lk, lt + 70 * lk, 20);
        tValueL = ValueText();
        PlaceLeft(tValueL, ll + 167 * lk, lt + 125 * lk, 42);

        var (rl, rt, rk) = AddSprite("card_r", 1095, 790, 0.9);
        tLabelR = Text("还需", 20, TextFont, FontWeights.SemiBold, new SolidColorBrush(Color.FromArgb(0xF0, 0xF0, 0xE6, 0xFF)), 120, TextAlignment.Left);
        PlaceLeft(tLabelR, rl + 145 * rk, rt + 62 * rk, 20);
        tValueR = ValueText();
        PlaceLeft(tValueR, rl + 128 * rk, rt + 115 * rk, 42);
    }

    private TextBlock ValueText()
    {
        var t = Text("--", 42, DisplayFont, FontWeights.Normal,
            new LinearGradientBrush(new GradientStopCollection { new GradientStop(Colors.White, 0.2), valueB }, 90), 190, TextAlignment.Left);
        t.Effect = textGlow;
        return t;
    }

    /// <summary>从 12 点钟起顺时针 [fromDeg, toDeg] 的圆弧 (半径 = 进度带中线)</summary>
    private Geometry ArcGeometry(double fromDeg, double toDeg)
    {
        double r = bandR * s, cx = X(ringCx), cy = Y(ringCy);
        Point P(double deg)
        {
            double a = deg * Math.PI / 180;
            return new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a));
        }
        var fig = new PathFigure { StartPoint = P(fromDeg), IsClosed = false };
        fig.Segments.Add(new ArcSegment(P(toDeg), new Size(r, r), 0, toDeg - fromDeg > 180, SweepDirection.Clockwise, true));
        return new PathGeometry(new[] { fig });
    }

    // ── 绘制小工具 ───────────────────────────────────────────

    private void Put(UIElement e, double px, double py)
    {
        Canvas.SetLeft(e, px);
        Canvas.SetTop(e, py);
        root!.Children.Add(e);
    }

    private TextBlock Text(string text, double sizeU, FontFamily font, FontWeight weight, Brush fg, double widthU, TextAlignment align = TextAlignment.Center) => new()
    {
        Text = text, FontFamily = font, FontSize = sizeU * s, FontWeight = weight, Foreground = fg,
        Width = widthU * s, TextAlignment = align, IsHitTestVisible = false,
    };

    /// <summary>文本水平居中于 (cx, cy) (单位), 垂直居中按字号估算</summary>
    private void PlaceCenter(TextBlock t, double cx, double cy, double sizeU) =>
        Put(t, X(cx) - t.Width / 2, Y(cy) - sizeU * 0.68 * s);

    /// <summary>文本左端对齐于 x, 垂直居中于 cy</summary>
    private void PlaceLeft(TextBlock t, double x, double cy, double sizeU) =>
        Put(t, X(x), Y(cy) - sizeU * 0.68 * s);

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
        var th = off ? Off : sch.IsWorkTime(t) ? Working : t < sch.AmStart ? Before : Lunch;
        Apply(th);
        double op = Math.Max(0.2, Math.Min(settings.PanelOpacity / 100.0, 1));
        Opacity = op;
        back.Opacity = op;

        tClock.Text = now.ToString("HH:mm:ss");
        tDate.Text = now.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
        tStatus.Text = th == Off ? "下班啦！" : th == Working ? "打工中…" : th == Lunch ? "午休中…" : "还没上班";

        double p = sch.Progress(t);
        tPercent.Text = $"{p * 100:0}%";
        SetArc(p);

        tValueL.Text = HM(sch.WorkedSeconds(t));
        tLabelR.Text = off ? "状态" : "还需";
        tValueR.Text = off ? "已下班" : HM(sch.RemainingWorkSeconds(t));
    }

    private void Apply(TechTheme th)
    {
        // 自定义发光颜色: 把贴图的蓝色 (色相约 215°) 旋转到指定色相
        double hue = th.Hue;
        var customGlow = Custom(settings.GlowColor);
        if (customGlow is { } gc)
        {
            Rgb2Hsv(gc.R, gc.G, gc.B, out double gh, out _, out _);
            hue = gh - 215;
        }
        string key = $"{th.Name}|{settings.RingColor}|{settings.GlowColor}";
        if (key == appliedKey) return;
        appliedKey = key;

        foreach (var (img, name) in sprites) img.Source = Tint(name, hue, th.Sat);
        foreach (var (img, name) in decorations) img.Source = Tint(name, hue, th.Sat);

        var ring = Custom(settings.RingColor);
        var a1 = ring ?? Col(th.Arc1);
        var a2 = ring ?? Col(th.Arc2);
        var glow = customGlow ?? Col(th.Glow);
        arcA.Color = a1; arcB.Color = a2;
        arcGlow.Color = ring ?? glow;
        textGlow.Color = glow;
        clockB.Color = Blend(glow, Colors.White, 0.55);
        valueB.Color = Blend(glow, Colors.White, 0.6);
        track.Stroke = new SolidColorBrush(Color.FromArgb(0x30, glow.R, glow.G, glow.B));

        // 下班后环呼吸闪烁, 其它状态静止
        if (th.Pulse)
        {
            ringImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1.0, TimeSpan.FromSeconds(0.9))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            });
        }
        else
        {
            ringImage.BeginAnimation(OpacityProperty, null);
            ringImage.Opacity = 1;
        }

        // 点缀轻微闪烁
        int i = 0;
        foreach (var (d, _) in decorations)
        {
            d.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 1.0, TimeSpan.FromSeconds(1.2 + 0.35 * i++))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            });
        }
    }

    private void SetArc(double progress)
    {
        progress = Math.Max(0, Math.Min(progress, 0.9999));
        if (progress <= 0.0001) { arc.Data = Geometry.Empty; return; }
        arc.Data = ArcGeometry(0, progress * 360);
    }

    private static string HM(double seconds)
    {
        int sec = (int)seconds;
        return $"{sec / 3600}h {sec % 3600 / 60:00}m";
    }

    private static Color? Custom(string hex) => PluginSettings.TryParseColor(hex, out var c) ? c : null;
    private static Color Col(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    // ── 背层: 环内深色圆盘 ───────────────────────────────────

    internal sealed class BackLayer : Window
    {
        public BackLayer()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            IsHitTestVisible = false;
            SourceInitialized += (_, _) => WinZ.ClickThrough(new WindowInteropHelper(this).Handle);
        }

        /// <summary>圆心 (cx, cy) 和半径 r 都是像素 (相对窗口左上角)</summary>
        public Canvas? Root { get; private set; }

        public void Build(double s, double cx, double cy, double r, double canvasW = CanvasW, double canvasH = CanvasH)
        {
            var c = new Canvas { Width = canvasW * s, Height = canvasH * s, IsHitTestVisible = false };
            Root = c;
            var disc = new Ellipse
            {
                Width = 2 * r, Height = 2 * r,
                Fill = new RadialGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0xB8, 0x16, 0x24, 0x52), 0.0),
                    new GradientStop(Color.FromArgb(0xC8, 0x0E, 0x16, 0x3A), 0.75),
                    new GradientStop(Color.FromArgb(0xD8, 0x0A, 0x10, 0x2C), 1.0),
                }),
                Effect = new DropShadowEffect { Color = Color.FromRgb(0x30, 0x60, 0xC0), ShadowDepth = 0, BlurRadius = 24 * s, Opacity = 0.7 },
            };
            Canvas.SetLeft(disc, cx - r); Canvas.SetTop(disc, cy - r);
            c.Children.Add(disc);

            var dash = new Ellipse
            {
                Width = 2 * r * 0.86, Height = 2 * r * 0.86,
                Stroke = new SolidColorBrush(Color.FromArgb(0x80, 0x7C, 0xC8, 0xFF)),
                StrokeThickness = Math.Max(1, 1.2 * s),
                StrokeDashArray = new DoubleCollection { 3, 5 },
            };
            Canvas.SetLeft(dash, cx - r * 0.86); Canvas.SetTop(dash, cy - r * 0.86);
            c.Children.Add(dash);
            Content = c;
        }
    }
}

/// <summary>窗口层次 / 鼠标穿透的 Win32 小工具</summary>
internal static class WinZ
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    public static void ClickThrough(IntPtr hwnd)
    {
        const long add = WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        if (IntPtr.Size == 8)
            SetWindowLongPtr64(hwnd, GWL_EXSTYLE, (IntPtr)(GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64() | add));
        else
            SetWindowLong32(hwnd, GWL_EXSTYLE, GetWindowLong32(hwnd, GWL_EXSTYLE) | (int)add);
    }

    public static void BringToTop(IntPtr hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>把 hwnd 放到 after 的正后方 (z 序中紧贴其下)</summary>
    public static void PutBehind(IntPtr hwnd, IntPtr after) =>
        SetWindowPos(hwnd, after, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
}
