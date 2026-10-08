using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 工作日历: 左边月历选日期, 右边查看/补录/修改当天记录 (每行一条 "HH:MM  内容").
/// 对应旧版 WorkCalendarDialog + RecordsDialog 的"工作记录"页.
/// </summary>
public class WorkLogWindow : Window
{
    private readonly WorkLogStore store;
    private readonly MonthCalendar calendar;
    private readonly TextBlock dateLabel = new()
    {
        FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Ui.Brush("#A0B8E0"), Margin = new Thickness(0, 0, 0, 4),
    };
    private readonly TextBox editor = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Background = Ui.Brush("#111122"), Foreground = Ui.Brush("#C8D0F0"), BorderBrush = Ui.Brush("#333355"),
        CaretBrush = Brushes.White, FontSize = 13, FontFamily = new FontFamily("Microsoft YaHei UI, Consolas"), Padding = new Thickness(6),
    };
    private readonly TextBlock status = new() { Foreground = Ui.Brush("#60D080"), VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public WorkLogWindow(WorkLogStore store)
    {
        this.store = store;
        Title = "工作日历";
        Width = 760;
        Height = 480;
        MinWidth = 640;
        MinHeight = 400;
        Background = Ui.Brush("#181A30");
        Foreground = Ui.Brush("#C8D0F0");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        calendar = new MonthCalendar(store.HasLog);
        calendar.SelectedDateChanged += LoadDay;

        var legend = new TextBlock
        {
            Text = "● 绿色 = 有记录    ● 金色 = 今天", FontSize = 11, Foreground = Ui.Brush("#60D080"), Margin = new Thickness(2, 6, 0, 0),
        };
        var left = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        left.Children.Add(calendar);
        left.Children.Add(legend);

        var hint = new TextBlock
        {
            Text = "每行一条记录，格式：HH:MM  内容（时间可省略，保存时自动补全）",
            FontSize = 11, Foreground = Ui.Brush("#5060A0"), Margin = new Thickness(0, 0, 0, 6),
        };

        var save = new Button { Content = "保存", Width = 80, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(0, 4, 0, 4) };
        var import = new Button { Content = "导入旧版记录…", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        var close = new Button { Content = "关闭", Width = 80, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(0, 4, 0, 4) };
        save.Click += (_, _) => Save();
        import.Click += (_, _) => Import();
        close.Click += (_, _) => Close();

        var buttons = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
        btnRow.Children.Add(import);
        btnRow.Children.Add(save);
        btnRow.Children.Add(close);
        DockPanel.SetDock(btnRow, Dock.Right);
        buttons.Children.Add(btnRow);
        buttons.Children.Add(status);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.Children.Add(dateLabel);
        Grid.SetRow(hint, 1);
        right.Children.Add(hint);
        Grid.SetRow(editor, 2);
        right.Children.Add(editor);
        Grid.SetRow(buttons, 3);
        right.Children.Add(buttons);

        var root = new Grid { Margin = new Thickness(14) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(left);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        Content = root;

        statusTimer.Tick += (_, _) => { status.Text = ""; statusTimer.Stop(); };
        Closed += (_, _) => statusTimer.Stop();
        LoadDay(calendar.SelectedDate);
    }

    private void LoadDay(DateTime day)
    {
        var entries = store.GetDay(day);
        dateLabel.Text = entries.Count > 0
            ? $"{WorkLogStore.DayKey(day)}   共 {entries.Count} 条记录"
            : $"{WorkLogStore.DayKey(day)}   （暂无记录）";
        editor.Text = WorkLogStore.FormatLines(entries);
    }

    private void Save()
    {
        var day = calendar.SelectedDate;
        string defaultTime = day == DateTime.Today ? DateTime.Now.ToString("HH:mm") : "09:00";
        try
        {
            store.ReplaceDay(day, WorkLogStore.ParseLines(editor.Text, defaultTime));
        }
        catch (Exception e)
        {
            ShowStatus("保存失败：" + e.Message, false);
            return;
        }
        calendar.Rebuild();
        LoadDay(day);
        ShowStatus("✓ 已保存", true);
    }

    private void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择旧版 WorkingPet 的 work_log.json",
            Filter = "工作记录 (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            int n = store.ImportFrom(dlg.FileName);
            calendar.Rebuild();
            LoadDay(calendar.SelectedDate);
            ShowStatus($"✓ 已导入 {n} 条", true);
        }
        catch (Exception e)
        {
            ShowStatus("导入失败：" + e.Message, false);
        }
    }

    private void ShowStatus(string text, bool ok)
    {
        status.Foreground = Ui.Brush(ok ? "#60D080" : "#FF6060");
        status.Text = text;
        statusTimer.Stop();
        statusTimer.Start();
    }
}
