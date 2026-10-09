using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VPet.Plugin.WorkingPet;

/// <summary>AI / 周报提醒 / 钉钉 设置. 对应旧版 SettingsDialog 里 report_config 那部分.</summary>
public class AiSettingsWindow : Window
{
    private static readonly string[] Days = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };

    private readonly PasswordBox apiKey = new();
    private readonly TextBox endpoint = new(), apiUrl = new(), remindTime = new(), dingtalk = new();
    private readonly ComboBox remindDay = new();
    private readonly CheckBox dailyConfirm = new() { Content = "下班前 10 分钟弹出日报确认" };
    private readonly CheckBox collectActivity = new() { Content = "记录窗口活动并总结" };
    private readonly CheckBox collectVision = new() { Content = "定期看一眼屏幕并描述" };
    private readonly TextBox dataDir = new();
    private readonly TextBox background = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    public AiSettingsWindow(AiConfig cfg, Action onSaved)
    {
        Title = "AI 设置";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        apiKey.Password = cfg.ApiKey;
        endpoint.Text = cfg.EndpointId;
        apiUrl.Text = cfg.ApiUrl;
        remindTime.Text = cfg.RemindTime;
        dingtalk.Text = cfg.DingTalkPath;
        background.Text = cfg.WorkBackground;
        dailyConfirm.IsChecked = cfg.DailyConfirm;
        collectActivity.IsChecked = cfg.CollectActivity;
        collectVision.IsChecked = cfg.CollectVision;
        dataDir.Text = cfg.DataDir;
        dataDir.ToolTip = "留空 = " + DataPaths.DefaultRoot;
        foreach (var d in Days) remindDay.Items.Add(d);
        remindDay.SelectedIndex = Math.Max(0, Math.Min(cfg.RemindDay, 6));

        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        SettingsWindow.AddRow(grid, "豆包 API Key", apiKey);
        SettingsWindow.AddRow(grid, "推理接入点 ID", endpoint);
        SettingsWindow.AddRow(grid, "接口地址 (可留空)", apiUrl);
        SettingsWindow.AddRow(grid, "周报提醒 星期", remindDay);
        SettingsWindow.AddRow(grid, "周报提醒 时间", remindTime);
        SettingsWindow.AddRow(grid, "钉钉路径 (可留空)", dingtalk);
        SettingsWindow.AddRow(grid, "我的工作背景", background);
        SettingsWindow.AddRow(grid, "", dailyConfirm);
        SettingsWindow.AddRow(grid, "", collectActivity);
        SettingsWindow.AddRow(grid, "", collectVision);
        SettingsWindow.AddRow(grid, "数据目录 (留空=默认)", dataDir);

        var tip = new TextBlock
        {
            Text = "Key 只保存在本机 %AppData%\\VPet-WorkingPet\\ai_config.json，不会被同步。开启采集后，窗口标题会定期发送到上面配置的接口做总结，截图会上传到接口做描述（图片不保存，只存一句话）。修改数据目录需要重启游戏生效；数据都存在本机数据目录里（菜单「打开数据文件夹」），需要换电脑或备份时自己拷贝即可。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(0, 8, 0, 0),
        };
        Grid.SetRow(tip, grid.RowDefinitions.Count);
        Grid.SetColumnSpan(tip, 2);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(tip);

        var test = new Button { Content = "测试连接", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "保存", Padding = new Thickness(24, 6, 24, 6) };
        test.Click += async (_, _) => await TestAsync(test);
        save.Click += (_, _) =>
        {
            if (!WorkSchedule.TryParseTime(remindTime.Text, out _))
            {
                Show("周报提醒时间格式不正确，请填 HH:MM，如 11:00", false);
                return;
            }
            Apply(cfg);
            try { cfg.Save(); }
            catch (Exception e) { Show("保存失败：" + e.Message, false); return; }
            onSaved();
            Close();
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 0, 16, 16) };
        buttons.Children.Add(test);
        buttons.Children.Add(save);

        var root = new StackPanel();
        root.Children.Add(grid);
        root.Children.Add(status);
        root.Children.Add(buttons);
        Content = root;
    }

    /// <summary>把界面上的值写进配置对象 (不落盘)</summary>
    private void Apply(AiConfig cfg)
    {
        cfg.ApiKey = apiKey.Password.Trim();
        cfg.EndpointId = endpoint.Text.Trim();
        cfg.ApiUrl = apiUrl.Text.Trim();
        cfg.RemindDay = Math.Max(0, remindDay.SelectedIndex);
        cfg.RemindTime = remindTime.Text.Trim();
        cfg.DingTalkPath = dingtalk.Text.Trim();
        cfg.WorkBackground = background.Text.Trim();
        cfg.DailyConfirm = dailyConfirm.IsChecked == true;
        cfg.CollectActivity = collectActivity.IsChecked == true;
        cfg.CollectVision = collectVision.IsChecked == true;
        cfg.DataDir = dataDir.Text.Trim();
    }

    /// <summary>用当前填写的值(未保存)发一个最小请求, 验证 Key 和接入点是否可用</summary>
    private async Task TestAsync(Button btn)
    {
        var tmp = new AiConfig();
        Apply(tmp);
        if (!tmp.IsConfigured)
        {
            Show("请先填写 API Key 和推理接入点 ID", false);
            return;
        }
        btn.IsEnabled = false;
        Show("正在连接…", true);
        try
        {
            var reply = await DoubaoClient.ChatAsync(tmp, new[] { ("user", "回复“好”一个字即可") }, 20);
            Show("✓ 连接成功，AI 回复：" + reply.Trim(), true);
        }
        catch (Exception e)
        {
            Show("连接失败：" + e.Message, false);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    private void Show(string text, bool ok)
    {
        status.Foreground = ok ? Brushes.SeaGreen : Brushes.IndianRed;
        status.Text = text;
    }
}
