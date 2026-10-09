using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VPet.Plugin.WorkingPet;

/// <summary>上下班时间设置窗口 (对应旧版 SettingsDialog 的这一部分)</summary>
public class SettingsWindow : Window
{
    private readonly TextBox amStart = new(), amEnd = new(), pmStart = new(), pmEnd = new();
    private static readonly string[] OffWorkLabels = { "跑到屏幕中央并放大", "假装逃跑（关机动画）", "睡觉", "说话动画", "只弹气泡" };
    private static readonly string[] PreLabels = { "思考", "说话表情", "随机待机动作", "无（只倒数）" };
    private readonly ComboBox offWork = new(), preAction = new();
    private readonly TextBox runScale = new(), runSeconds = new(), runStay = new();
    private readonly CheckBox sleepAfter = new() { Content = "跑回去之后直接睡觉" };
    private readonly CheckBox companion = new() { Content = "陪伴模式：宠物跟着我工作/摸鱼/下班" };
    private readonly CheckBox tease = new() { Content = "摸鱼太久时宠物调侃我" };
    private readonly CheckBox countdown = new() { Content = "下班前 3 秒倒数 3-2-1" };
    private static readonly string[] PanelStyleLabels = { "环绕宠物（时钟 + 进度环 + 卡片）", "侧边面板", "科幻面板（贴图设计）" };
    private readonly ComboBox panelStyle = new();
    private readonly TextBox ringScale = new(), ringOffset = new();
    private readonly TextBox scale = new(), opacity = new();
    private readonly ColorField panelColor = new(), ringColor = new(), glowColor = new();
    private readonly CheckBox showPanel = new() { Content = "显示打工面板" };
    private readonly CheckBox followPet = new() { Content = "面板跟随宠物移动" };

    public SettingsWindow(PluginSettings settings, Action onSaved, Action? openAiSettings = null, OffWorkHooks? offWorkHooks = null)
    {
        Title = "打工设置";
        Width = 360;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        var s = settings.Schedule;
        amStart.Text = WorkSchedule.FormatTime(s.AmStart);
        amEnd.Text = WorkSchedule.FormatTime(s.AmEnd);
        pmStart.Text = WorkSchedule.FormatTime(s.PmStart);
        pmEnd.Text = WorkSchedule.FormatTime(s.PmEnd);
        foreach (var l in PanelStyleLabels) panelStyle.Items.Add(l);
        panelStyle.SelectedIndex = Math.Max(0, Array.IndexOf(PluginSettings.PanelStyles, settings.PanelStyle));
        ringScale.Text = settings.HudRingScale.ToString("0", CultureInfo.InvariantCulture);
        ringOffset.Text = settings.HudRingOffsetY.ToString("0", CultureInfo.InvariantCulture);
        scale.Text = settings.PanelScale.ToString("0", CultureInfo.InvariantCulture);
        foreach (var l in OffWorkLabels) offWork.Items.Add(l);
        foreach (var l in PreLabels) preAction.Items.Add(l);
        preAction.SelectedIndex = Math.Max(0, Array.IndexOf(PluginSettings.PreActions, settings.PreAction));
        companion.IsChecked = settings.Companion;
        tease.IsChecked = settings.CompanionTease;
        countdown.IsChecked = settings.OffWorkCountdown;
        sleepAfter.IsChecked = settings.SleepAfterRun;
        runScale.Text = settings.RunScale.ToString("0.0#", CultureInfo.InvariantCulture);
        runSeconds.Text = settings.RunSeconds.ToString("0.0#", CultureInfo.InvariantCulture);
        runStay.Text = settings.RunStay.ToString("0", CultureInfo.InvariantCulture);
        offWork.SelectedIndex = Math.Max(0, Array.IndexOf(PluginSettings.OffWorkActions, settings.OffWorkAction));
        opacity.Text = settings.PanelOpacity.ToString("0", CultureInfo.InvariantCulture);
        panelColor.Value = settings.PanelColor;
        ringColor.Value = settings.RingColor;
        glowColor.Value = settings.GlowColor;
        showPanel.IsChecked = settings.ShowPanel;
        followPet.IsChecked = settings.FollowPet;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        AddRow(grid, "上午上班 (HH:MM)", amStart);
        AddRow(grid, "上午下班 (HH:MM)", amEnd);
        AddRow(grid, "下午上班 (HH:MM)", pmStart);
        AddRow(grid, "下午下班 (HH:MM)", pmEnd);
        AddRow(grid, "", companion);
        AddRow(grid, "", tease);
        AddRow(grid, "", countdown);
        AddRow(grid, "倒数期间动作", preAction);
        AddRow(grid, "到点下班时宠物", offWork);
        AddRow(grid, "跑到中央·放大倍数", runScale);
        AddRow(grid, "跑到中央·用时(秒)", runSeconds);
        AddRow(grid, "跑到中央·停留(秒)", runStay);
        AddRow(grid, "", sleepAfter);
        AddRow(grid, "面板样式", panelStyle);
        AddRow(grid, "面板大小 (50-300 %)", scale);
        AddRow(grid, "环绕·环大小 (60-160 %)", ringScale);
        AddRow(grid, "环绕·环上下位置 (-100~100)", ringOffset);
        AddRow(grid, "透明度 (20-100 %)", opacity);
        AddRow(grid, "面板颜色", panelColor);
        AddRow(grid, "进度环颜色", ringColor);
        AddRow(grid, "发光颜色", glowColor);
        AddRow(grid, "", showPanel);
        AddRow(grid, "", followPet);

        bool TryReadRun(out RunOptions ro)
        {
            ro = default;
            if (!double.TryParse(runScale.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sc)
                || !double.TryParse(runSeconds.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)
                || !double.TryParse(runStay.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var stay)
                || sc < 1.2 || sc > 6 || sec < 1 || sec > 10 || stay < 10 || stay > 3600)
            {
                MessageBox.Show(this, "放大倍数请填 1.2-6，用时 1-10 秒，停留 10-3600 秒", "WorkingPet");
                return false;
            }
            ro = new RunOptions(sc, sec, stay, sleepAfter.IsChecked == true);
            return true;
        }

        var save = new Button { Content = "保存", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(0, 6, 0, 6) };
        save.Click += (_, _) =>
        {
            var n = new WorkSchedule();
            if (!WorkSchedule.TryParseTime(amStart.Text, out var a1)
                || !WorkSchedule.TryParseTime(amEnd.Text, out var a2)
                || !WorkSchedule.TryParseTime(pmStart.Text, out var p1)
                || !WorkSchedule.TryParseTime(pmEnd.Text, out var p2))
            {
                MessageBox.Show(this, "时间格式不正确, 请填 HH:MM, 如 9:00", "WorkingPet");
                return;
            }
            if (!double.TryParse(scale.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sc)
                || sc < 50 || sc > 300)
            {
                MessageBox.Show(this, "面板大小请填 50 到 300 之间的数字", "WorkingPet");
                return;
            }
            if (!double.TryParse(opacity.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var op)
                || op < 20 || op > 100)
            {
                MessageBox.Show(this, "透明度请填 20 到 100 之间的数字", "WorkingPet");
                return;
            }
            foreach (var (name, f) in new[] { ("面板颜色", panelColor), ("进度环颜色", ringColor), ("发光颜色", glowColor) })
            {
                if (!f.IsValid)
                {
                    MessageBox.Show(this, name + "格式不正确, 请填 #RRGGBB (如 #FF6600), 留空表示自动", "WorkingPet");
                    return;
                }
            }
            if (!double.TryParse(ringScale.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rs) || rs < 60 || rs > 160
                || !double.TryParse(ringOffset.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ry) || ry < -100 || ry > 100)
            {
                MessageBox.Show(this, "环大小请填 60-160，环上下位置请填 -100 到 100", "WorkingPet");
                return;
            }
            if (!TryReadRun(out var ro)) return;
            n.AmStart = a1; n.AmEnd = a2; n.PmStart = p1; n.PmEnd = p2;
            if (!n.IsValid(out var err))
            {
                MessageBox.Show(this, err, "WorkingPet");
                return;
            }
            settings.Schedule.AmStart = n.AmStart;
            settings.Schedule.AmEnd = n.AmEnd;
            settings.Schedule.PmStart = n.PmStart;
            settings.Schedule.PmEnd = n.PmEnd;
            settings.OffWorkAction = PluginSettings.OffWorkActions[Math.Max(0, offWork.SelectedIndex)];
            settings.PreAction = PluginSettings.PreActions[Math.Max(0, preAction.SelectedIndex)];
            settings.OffWorkCountdown = countdown.IsChecked == true;
            settings.Companion = companion.IsChecked == true;
            settings.CompanionTease = tease.IsChecked == true;
            settings.RunScale = ro.Scale;
            settings.RunSeconds = ro.Seconds;
            settings.RunStay = ro.StaySeconds;
            settings.SleepAfterRun = ro.SleepAfter;
            settings.PanelStyle = PluginSettings.PanelStyles[Math.Max(0, panelStyle.SelectedIndex)];
            settings.HudRingScale = rs;
            settings.HudRingOffsetY = ry;
            settings.PanelScale = sc;
            settings.PanelOpacity = op;
            settings.PanelColor = panelColor.Value;
            settings.RingColor = ringColor.Value;
            settings.GlowColor = glowColor.Value;
            settings.ShowPanel = showPanel.IsChecked == true;
            settings.FollowPet = followPet.IsChecked == true;
            onSaved();
            Close();
        };

        // 预览用的是窗口里当前选的值, 不用先保存
        string Action_() => PluginSettings.OffWorkActions[Math.Max(0, offWork.SelectedIndex)];
        var previewFinal = new Button { Content = "预览：只看到点后的下班动作", Margin = new Thickness(16, 0, 16, 6), Padding = new Thickness(0, 6, 0, 6) };
        previewFinal.Click += (_, _) => { if (TryReadRun(out var r)) offWorkHooks?.PreviewFinal(Action_(), r); };
        previewFinal.IsEnabled = offWorkHooks != null;
        var previewSeq = new Button { Content = "预览：完整流程（3-2-1 + 下班动作）", Margin = new Thickness(16, 0, 16, 6), Padding = new Thickness(0, 6, 0, 6) };
        previewSeq.Click += (_, _) =>
        {
            if (TryReadRun(out var r))
                offWorkHooks?.PreviewSequence(PluginSettings.PreActions[Math.Max(0, preAction.SelectedIndex)], Action_(), r);
        };
        previewSeq.IsEnabled = offWorkHooks != null;

        var ai = new Button { Content = "AI 设置（豆包 / 周报 / 钉钉）…", Margin = new Thickness(16, 0, 16, 0), Padding = new Thickness(0, 6, 0, 6) };
        ai.Click += (_, _) => openAiSettings?.Invoke();
        ai.IsEnabled = openAiSettings != null;

        var root = new StackPanel();
        root.Children.Add(grid);
        root.Children.Add(previewSeq);
        root.Children.Add(previewFinal);
        root.Children.Add(ai);
        root.Children.Add(new Border { Padding = new Thickness(16, 0, 16, 16), Child = save });
        Content = root;
    }

    internal static void AddRow(Grid grid, string label, UIElement input)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var tb = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) };
        Grid.SetRow(tb, row);
        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);
        if (input is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 4);
        grid.Children.Add(tb);
        grid.Children.Add(input);
    }
}


/// <summary>
/// 颜色输入: 文本框填 #RRGGBB + 预览色块 + 一排常用色, 点常用色直接填入; 留空表示自动(跟随状态配色)
/// </summary>
public class ColorField : StackPanel
{
    private static readonly string[] Presets =
    {
        "#00E5FF", "#B388FF", "#FF4081", "#FF5252", "#FF9100", "#FFD740", "#69F0AE", "#40C4FF", "#FFFFFF", "#141B3D",
    };

    private readonly TextBox box = new() { ToolTip = "格式 #RRGGBB, 留空 = 跟随状态自动配色" };
    private readonly Border preview = new()
    {
        Width = 22, Height = 22, CornerRadius = new CornerRadius(4), Margin = new Thickness(6, 0, 0, 0),
        BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
    };

    public ColorField()
    {
        var top = new DockPanel();
        DockPanel.SetDock(preview, Dock.Right);
        top.Children.Add(preview);
        top.Children.Add(box);
        Children.Add(top);

        var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var hex in Presets)
        {
            var sw = new Border
            {
                Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0), CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Cursor = Cursors.Hand, ToolTip = hex,
            };
            sw.MouseLeftButtonDown += (_, _) => box.Text = hex;
            row.Children.Add(sw);
        }
        // 最后一个: 清空 = 自动
        var auto = new TextBlock { Text = "自动", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Foreground = Brushes.SteelBlue };
        auto.MouseLeftButtonDown += (_, _) => box.Text = "";
        row.Children.Add(auto);
        Children.Add(row);

        box.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    public string Value
    {
        get => box.Text.Trim();
        set => box.Text = value ?? "";
    }

    /// <summary>空 (自动) 或合法颜色都算有效</summary>
    public bool IsValid => string.IsNullOrWhiteSpace(box.Text) || PluginSettings.TryParseColor(box.Text, out _);

    private void UpdatePreview()
    {
        preview.Background = PluginSettings.TryParseColor(box.Text, out var c)
            ? new SolidColorBrush(c)
            : Brushes.Transparent;
    }
}
