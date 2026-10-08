using System.Windows;
using System.Windows.Threading;
using VPet_Simulator.Core;
using VPet_Simulator.Windows.Interface;
using static VPet_Simulator.Core.GraphInfo;

namespace VPet.Plugin.WorkingPet;

/// <summary>跑到屏幕中央并放大的参数</summary>
public readonly record struct RunOptions(double Scale, double Seconds, double StaySeconds);

/// <summary>
/// 宠物边跑边放大地移动到屏幕中央, 再按原样跑回去.
/// 做法: 播放宠物自带的 walk 动画, 同时每帧把宠物区域(PetGrid)的宽度放大、把窗口中心沿直线挪向屏幕中心.
/// 直接改 PetGrid.Width 而不调用 SetZoomLevel, 所以不会改动用户保存的缩放设置.
/// </summary>
public class RunToCenterEffect
{
    private readonly IMainWindow mw;
    private readonly DispatcherTimer frame = new() { Interval = TimeSpan.FromMilliseconds(16) };

    private Window? win;
    private double origWidth;   // 开始前 PetGrid 宽度 (= 500 × 缩放)
    private Point origCenter;   // 开始前窗口中心
    private DateTime startedAt;
    private double duration;
    private double fromW, toW;
    private Point fromC, toC;
    private Action? onFinished;

    /// <summary>已经开始(跑/停留/回去中), 还没完全复原</summary>
    public bool IsActive { get; private set; }

    public RunToCenterEffect(IMainWindow mw)
    {
        this.mw = mw;
        frame.Tick += (_, _) => Step();
    }

    /// <summary>开始跑向屏幕中央并放大; 到达后调用 arrived. 条件不满足(找不到窗口等)返回 false</summary>
    public bool Begin(double scale, double seconds, Action arrived)
    {
        if (IsActive) return false;
        win = Window.GetWindow(mw.Main);
        var grid = mw.PetGrid;
        if (win == null || win.ActualWidth <= 0 || win.ActualHeight <= 0 || double.IsNaN(grid.Width)) return false;

        var area = SystemParameters.WorkArea;
        origWidth = grid.Width;
        origCenter = CenterOf(win);
        // 放大后窗口高度最多占工作区的 85%, 避免超出屏幕
        double k = Math.Max(1, Math.Min(scale, 0.85 * area.Height / win.ActualHeight));
        var target = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);

        IsActive = true;
        PlayWalk(target.X - origCenter.X);
        Animate(origWidth, origWidth * k, origCenter, target, seconds, arrived);
        return true;
    }

    /// <summary>缩小并跑回原来的位置, 结束后恢复待机</summary>
    public void Return(double seconds, Action? finished = null)
    {
        if (!IsActive || win == null)
        {
            finished?.Invoke();
            return;
        }
        frame.Stop();
        var current = CenterOf(win);
        PlayWalk(origCenter.X - current.X);
        Animate(mw.PetGrid.Width, origWidth, current, origCenter, seconds, () =>
        {
            Restore();
            mw.Main.DisplayToNomal();
            finished?.Invoke();
        });
    }

    /// <summary>立刻复原大小和位置 (退出游戏、被打断时用)</summary>
    public void Restore()
    {
        frame.Stop();
        onFinished = null;
        if (!IsActive || win == null) return;
        IsActive = false;
        mw.PetGrid.Width = origWidth;
        win.UpdateLayout();
        win.Left = origCenter.X - win.ActualWidth / 2;
        win.Top = origCenter.Y - win.ActualHeight / 2;
    }

    // ── 动画 ─────────────────────────────────────────────────

    private void Animate(double fromWidth, double toWidth, Point fromCenter, Point toCenter, double seconds, Action? done)
    {
        fromW = fromWidth;
        toW = toWidth;
        fromC = fromCenter;
        toC = toCenter;
        duration = Math.Max(0.2, seconds);
        onFinished = done;
        startedAt = DateTime.Now;
        frame.Start();
    }

    private void Step()
    {
        if (win == null) { frame.Stop(); return; }
        double u = Math.Min(1, (DateTime.Now - startedAt).TotalSeconds / duration);
        double e = u * u * (3 - 2 * u); // 先加速后减速, 比匀速自然
        Place(fromW + (toW - fromW) * e,
              new Point(fromC.X + (toC.X - fromC.X) * e, fromC.Y + (toC.Y - fromC.Y) * e));
        if (u < 1) return;
        frame.Stop();
        var done = onFinished;
        onFinished = null;
        done?.Invoke();
    }

    /// <summary>设置宠物区域宽度, 并让窗口(SizeToContent 会随之变大)的中心落在 center</summary>
    private void Place(double width, Point center)
    {
        mw.PetGrid.Width = width;
        win!.UpdateLayout(); // 立刻按新宽度重新计算窗口大小, 再据此定位
        win.Left = center.X - win.ActualWidth / 2;
        win.Top = center.Y - win.ActualHeight / 2;
    }

    private static Point CenterOf(Window w) => new(w.Left + w.ActualWidth / 2, w.Top + w.ActualHeight / 2);

    /// <summary>按移动方向播放宠物自带的行走动画 (faster 只有开心状态有), 找不到就只滑动不播动画</summary>
    private void PlayWalk(double dx)
    {
        var core = mw.Core;
        if (core.Graph == null || core.Save == null) return;
        string dir = dx < 0 ? "left" : "right";
        foreach (var name in new[] { $"walk.{dir}.faster", $"walk.{dir}" })
        {
            if (core.Graph.FindGraph(name, AnimatType.A_Start, core.Save.Mode) == null) continue;
            mw.Main.Display(name, AnimatType.A_Start, mw.Main.DisplayBLoopingForce);
            return;
        }
    }
}
