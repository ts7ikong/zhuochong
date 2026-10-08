using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 可编辑文本 + 一排按钮的通用窗口. 周报预览(复制/打开钉钉)和日报确认(跳过/确认保存)都用它.
/// 对应旧版 WeeklyReportDialog / DailyConfirmDialog.
/// </summary>
public class DraftWindow : Window
{
    private readonly TextBox editor = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Background = Ui.Brush("#111122"), Foreground = Ui.Brush("#C8D0F0"), BorderBrush = Ui.Brush("#333355"),
        CaretBrush = Brushes.White, FontSize = 13, FontFamily = new FontFamily("Microsoft YaHei UI"), Padding = new Thickness(8),
    };
    private readonly TextBlock count = new() { FontSize = 11, Foreground = Ui.Brush("#6070A0"), HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock status = new() { Foreground = Ui.Brush("#60D080"), VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public string Text => editor.Text;

    public DraftWindow(string title, string hint, string text, params (string Label, Action<DraftWindow> OnClick)[] buttons)
    {
        Title = title;
        Width = 560;
        Height = 460;
        MinWidth = 420;
        MinHeight = 320;
        Background = Ui.Brush("#181A30");
        Foreground = Ui.Brush("#C8D0F0");
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var head = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Ui.Brush("#00C8FF") };
        var hintText = new TextBlock
        {
            Text = hint, FontSize = 12, Foreground = Ui.Brush("#7080A0"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8),
        };

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, onClick) in buttons)
        {
            var b = new Button { Content = label, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6) };
            b.Click += (_, _) => onClick(this);
            btnRow.Children.Add(b);
        }
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
        DockPanel.SetDock(btnRow, Dock.Right);
        bottom.Children.Add(btnRow);
        bottom.Children.Add(status);

        var root = new Grid { Margin = new Thickness(16) };
        for (int i = 0; i < 5; i++)
            root.RowDefinitions.Add(new RowDefinition { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        root.Children.Add(head);
        Grid.SetRow(hintText, 1);
        root.Children.Add(hintText);
        Grid.SetRow(editor, 2);
        root.Children.Add(editor);
        Grid.SetRow(count, 3);
        root.Children.Add(count);
        Grid.SetRow(bottom, 4);
        root.Children.Add(bottom);
        Content = root;

        editor.Text = text;
        editor.TextChanged += (_, _) => UpdateCount();
        UpdateCount();
        statusTimer.Tick += (_, _) => { status.Text = ""; statusTimer.Stop(); };
        Closed += (_, _) => statusTimer.Stop();
    }

    public void ShowStatus(string text, bool ok = true)
    {
        status.Foreground = Ui.Brush(ok ? "#60D080" : "#FF6060");
        status.Text = text;
        statusTimer.Stop();
        statusTimer.Start();
    }

    private void UpdateCount() =>
        count.Text = $"共 {editor.Text.Split('\n').Count(l => l.Trim().Length > 0)} 行";
}
