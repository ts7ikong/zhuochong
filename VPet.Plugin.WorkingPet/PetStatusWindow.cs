using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VPet_Simulator.Windows.Interface;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 宠物状态窗口: 等级/经验/金钱/心情, 体力-饱腹-口渴-心情值-健康-好感度, 正在做什么,
/// 以及今天"工作 / 摸鱼"各多少分钟 (AI 判断的估算值) 和陪伴模式的当前判断. 每 2 秒刷新.
/// </summary>
public class PetStatusWindow : Window
{
    private readonly IMainWindow mw;
    private readonly Func<string> companionLine;
    private readonly Func<(int Work, int Slack, int Unknown)> stats;
    private readonly DispatcherTimer timer;

    private readonly TextBlock title = Lbl(18, FontWeights.Bold, "#00C8FF");
    private readonly TextBlock activity = Lbl(13, FontWeights.Normal, "#C8D0F0");
    private readonly TextBlock money = Lbl(13, FontWeights.Normal, "#FFD76A");
    private readonly TextBlock today = Lbl(13, FontWeights.Normal, "#C8D0F0");
    private readonly TextBlock judge = Lbl(13, FontWeights.Normal, "#9FB4E8");
    private readonly Dictionary<string, (ProgressBar Bar, TextBlock Value)> rows = new();

    public PetStatusWindow(IMainWindow mw, Func<string> companionLine, Func<(int Work, int Slack, int Unknown)> stats)
    {
        this.mw = mw;
        this.companionLine = companionLine;
        this.stats = stats;

        Title = "宠物状态";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Background = Br("#181A30");
        Foreground = Br("#C8D0F0");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new StackPanel { Margin = new Thickness(18, 14, 18, 16) };
        title.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(title);

        foreach (var (key, name, color) in new[]
        {
            ("exp", "经验", "#7C9CFF"), ("strength", "体力", "#6FE3A0"), ("food", "饱腹", "#FFB060"),
            ("drink", "口渴", "#5AC8FF"), ("feeling", "心情值", "#FF8AD8"), ("health", "健康", "#FF6B6B"),
            ("like", "好感度", "#FFD76A"),
        })
        {
            var bar = new ProgressBar { Height = 12, Maximum = 100, Foreground = Brush(color), Background = Br("#262A4A"), BorderThickness = new Thickness(0) };
            var value = Lbl(12, FontWeights.Normal, "#9FB4E8");
            value.Width = 90;
            value.TextAlignment = TextAlignment.Right;
            var label = Lbl(13, FontWeights.Normal, "#C8D0F0");
            label.Text = name;
            label.Width = 56;

            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(bar, 1);
            Grid.SetColumn(value, 2);
            bar.Margin = new Thickness(0, 0, 8, 0);
            bar.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(label);
            grid.Children.Add(bar);
            grid.Children.Add(value);
            root.Children.Add(grid);
            rows[key] = (bar, value);
        }

        var info = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var t in new[] { money, activity, today, judge })
        {
            t.TextWrapping = TextWrapping.Wrap;
            t.Margin = new Thickness(0, 3, 0, 3);
            info.Children.Add(t);
        }
        root.Children.Add(info);
        Content = root;

        // 显式绑定本窗口的 Dispatcher (窗口在 UI 线程创建)
        timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); timer.Start(); };
        Closed += (_, _) => timer.Stop();
    }

    private void Refresh()
    {
        try
        {
            var p = PetInfo.Read(mw);
            if (p == null)
            {
                title.Text = "宠物还没有加载好";
                return;
            }
            title.Text = $"{p.Name}  Lv.{p.Level}  ·  {p.MoodText}";
            Set("exp", p.ExpNeed <= 0 ? 0 : p.Exp / p.ExpNeed * 100, $"{p.Exp:0}/{p.ExpNeed:0}");
            Set("strength", p.Strength);
            Set("food", p.Food);
            Set("drink", p.Drink);
            Set("feeling", p.Feeling);
            Set("health", p.Health);
            Set("like", p.Likability);
            money.Text = $"金钱：{p.Money:N2}";
            activity.Text = p.ActivityMinutesLeft > 0
                ? $"正在：{p.Activity}，还剩约 {p.ActivityMinutesLeft:0} 分钟"
                : $"正在：{p.Activity}";

            var (work, slack, unknown) = stats();
            today.Text = $"今天（AI 估算）：工作 {Hm(work)}  ·  摸鱼 {Hm(slack)}" + (unknown > 0 ? $"  ·  未判断 {Hm(unknown)}" : "");
            judge.Text = companionLine();
        }
        catch (Exception e)
        {
            DebugLog.Write("刷新宠物状态窗口出错: " + e.Message);
        }
    }

    private void Set(string key, double percent, string? text = null)
    {
        var (bar, value) = rows[key];
        bar.Value = Math.Max(0, Math.Min(percent, 100));
        value.Text = text ?? $"{percent:0}%";
    }

    private static string Hm(int minutes) => minutes >= 60 ? $"{minutes / 60} 小时 {minutes % 60} 分" : $"{minutes} 分钟";

    private static TextBlock Lbl(double size, FontWeight weight, string color) => new()
    {
        FontSize = size, FontWeight = weight, Foreground = Brush(color), VerticalAlignment = VerticalAlignment.Center,
    };

    private static SolidColorBrush Br(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
