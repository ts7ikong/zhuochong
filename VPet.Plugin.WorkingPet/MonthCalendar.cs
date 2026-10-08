using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 自绘月历: 有记录的日期标绿, 今天用金色(无记录)/亮绿(有记录)加粗, 不能选未来日期.
/// WPF 自带 Calendar 没法按日期单独着色, 所以自己画. 对应旧版 WorkCalendarDialog 的左半边.
/// </summary>
public class MonthCalendar : Border
{
    private static readonly string[] WeekNames = { "一", "二", "三", "四", "五", "六", "日" };
    private static ControlTemplate? flat;

    private readonly Func<DateTime, bool> hasLog;
    private readonly TextBlock title = new()
    {
        FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Ui.Brush("#A0B8E0"),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly UniformGrid days = new() { Columns = 7, Rows = 6 };
    private readonly Button prev = Nav("‹"), next = Nav("›");

    public DateTime DisplayMonth { get; private set; }
    public DateTime SelectedDate { get; private set; }
    public event Action<DateTime>? SelectedDateChanged;

    public MonthCalendar(Func<DateTime, bool> hasLog)
    {
        this.hasLog = hasLog;
        SelectedDate = DateTime.Today;
        DisplayMonth = new DateTime(SelectedDate.Year, SelectedDate.Month, 1);

        Background = Ui.Brush("#111122");
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(8);
        Width = 292;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 顶部: ‹  2026年10月  ›
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
        DockPanel.SetDock(prev, Dock.Left);
        DockPanel.SetDock(next, Dock.Right);
        header.Children.Add(prev);
        header.Children.Add(next);
        header.Children.Add(title);
        prev.Click += (_, _) => { DisplayMonth = DisplayMonth.AddMonths(-1); Rebuild(); };
        next.Click += (_, _) => { DisplayMonth = DisplayMonth.AddMonths(1); Rebuild(); };
        root.Children.Add(header);

        // 星期行
        var week = new UniformGrid { Columns = 7 };
        foreach (var w in WeekNames)
            week.Children.Add(new TextBlock
            {
                Text = w, FontSize = 12, Foreground = Ui.Brush("#6070A0"),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4),
            });
        Grid.SetRow(week, 1);
        root.Children.Add(week);

        Grid.SetRow(days, 2);
        root.Children.Add(days);
        Child = root;
        Rebuild();
    }

    /// <summary>选中某天; 跳到该日期所在月份</summary>
    public void Select(DateTime day, bool raiseEvent)
    {
        SelectedDate = day.Date;
        DisplayMonth = new DateTime(day.Year, day.Month, 1);
        Rebuild();
        if (raiseEvent) SelectedDateChanged?.Invoke(SelectedDate);
    }

    /// <summary>记录有变化后重画 (刷新绿色标记)</summary>
    public void Rebuild()
    {
        title.Text = $"{DisplayMonth.Year}年{DisplayMonth.Month}月";
        next.IsEnabled = DisplayMonth < new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

        days.Children.Clear();
        int lead = ((int)DisplayMonth.DayOfWeek + 6) % 7; // 周一开头
        int count = DateTime.DaysInMonth(DisplayMonth.Year, DisplayMonth.Month);
        for (int i = 0; i < lead; i++) days.Children.Add(new Border());
        for (int d = 1; d <= count; d++)
            days.Children.Add(MakeDay(new DateTime(DisplayMonth.Year, DisplayMonth.Month, d)));
        while (days.Children.Count < 42) days.Children.Add(new Border());
    }

    private Button MakeDay(DateTime day)
    {
        bool future = day > DateTime.Today;
        bool has = hasLog(day);
        bool today = day == DateTime.Today;
        bool selected = day == SelectedDate;

        string bg = "#111122", fg = "#C8D0F0";
        if (future) { fg = "#383858"; }
        else if (today) { bg = has ? "#1E4A30" : "#3A2A10"; fg = has ? "#90F0A0" : "#F0C060"; }
        else if (has) { bg = "#1A3A28"; fg = "#60D080"; }

        var b = new Button
        {
            Content = day.Day.ToString(),
            Template = FlatTemplate(),
            Background = Ui.Brush(bg),
            Foreground = Ui.Brush(fg),
            BorderBrush = Ui.Brush("#8AA4FF"),
            BorderThickness = new Thickness(selected ? 2 : 0),
            FontWeight = today ? FontWeights.Bold : FontWeights.Normal,
            FontSize = 13,
            Margin = new Thickness(1.5),
            Height = 30,
            IsEnabled = !future,
            Cursor = future ? Cursors.Arrow : Cursors.Hand,
        };
        b.Click += (_, _) => Select(day, true);
        return b;
    }

    private static Button Nav(string text) => new()
    {
        Content = text, Template = FlatTemplate(), Width = 30, Height = 26, FontSize = 16,
        Background = Ui.Brush("#252540"), Foreground = Ui.Brush("#C8D0F0"), Cursor = Cursors.Hand,
    };

    /// <summary>去掉系统按钮的蓝色悬停/按下效果, 只显示背景色和选中描边</summary>
    private static ControlTemplate FlatTemplate()
    {
        if (flat != null) return flat;
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        flat = new ControlTemplate(typeof(Button)) { VisualTree = border };
        return flat;
    }
}

/// <summary>小工具: 从 #RRGGBB 创建画刷</summary>
internal static class Ui
{
    public static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
