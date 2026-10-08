using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace VPet.Plugin.WorkingPet;

/// <summary>工作时间/月薪设置窗口 (对应旧版 SettingsDialog 的这一部分)</summary>
public class SettingsWindow : Window
{
    private readonly TextBox salary = new();
    private readonly TextBox amStart = new(), amEnd = new(), pmStart = new(), pmEnd = new();
    private readonly CheckBox showPanel = new() { Content = "显示薪资面板" };

    public SettingsWindow(PluginSettings settings, Action onSaved)
    {
        Title = "WorkingPet 设置";
        Width = 320;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        var s = settings.Schedule;
        salary.Text = s.MonthlySalary.ToString(CultureInfo.InvariantCulture);
        amStart.Text = WorkSchedule.FormatTime(s.AmStart);
        amEnd.Text = WorkSchedule.FormatTime(s.AmEnd);
        pmStart.Text = WorkSchedule.FormatTime(s.PmStart);
        pmEnd.Text = WorkSchedule.FormatTime(s.PmEnd);
        showPanel.IsChecked = settings.ShowPanel;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        AddRow(grid, "月薪 (元)", salary);
        AddRow(grid, "上午上班 (HH:MM)", amStart);
        AddRow(grid, "上午下班 (HH:MM)", amEnd);
        AddRow(grid, "下午上班 (HH:MM)", pmStart);
        AddRow(grid, "下午下班 (HH:MM)", pmEnd);
        AddRow(grid, "", showPanel);

        var save = new Button { Content = "保存", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(0, 6, 0, 6) };
        save.Click += (_, _) =>
        {
            var n = new WorkSchedule();
            if (!double.TryParse(salary.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sal)
                || !WorkSchedule.TryParseTime(amStart.Text, out var a1)
                || !WorkSchedule.TryParseTime(amEnd.Text, out var a2)
                || !WorkSchedule.TryParseTime(pmStart.Text, out var p1)
                || !WorkSchedule.TryParseTime(pmEnd.Text, out var p2))
            {
                MessageBox.Show(this, "月薪或时间格式不正确, 时间请填 HH:MM, 如 9:00", "WorkingPet");
                return;
            }
            n.MonthlySalary = sal; n.AmStart = a1; n.AmEnd = a2; n.PmStart = p1; n.PmEnd = p2;
            if (!n.IsValid(out var err))
            {
                MessageBox.Show(this, err, "WorkingPet");
                return;
            }
            settings.Schedule.MonthlySalary = n.MonthlySalary;
            settings.Schedule.AmStart = n.AmStart;
            settings.Schedule.AmEnd = n.AmEnd;
            settings.Schedule.PmStart = n.PmStart;
            settings.Schedule.PmEnd = n.PmEnd;
            settings.ShowPanel = showPanel.IsChecked == true;
            onSaved();
            Close();
        };

        var root = new StackPanel();
        root.Children.Add(grid);
        root.Children.Add(new Border { Padding = new Thickness(16, 0, 16, 16), Child = save });
        Content = root;
    }

    private static void AddRow(Grid grid, string label, UIElement input)
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
