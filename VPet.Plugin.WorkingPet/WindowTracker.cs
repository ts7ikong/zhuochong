using System.Windows;
using VPet_Simulator.Windows.Interface;

namespace VPet.Plugin.WorkingPet;

internal static class WindowTracker
{
    /// <summary>登记到游戏的窗口列表: 游戏退出时会统一关闭, 窗口自己关掉时再移除</summary>
    public static void Track(IMainWindow mw, Window win)
    {
        mw.Windows.Add(win);
        win.Closed += (_, _) => mw.Windows.Remove(win);
    }
}
