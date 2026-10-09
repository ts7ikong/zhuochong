using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VPet.Plugin.WorkingPet;

/// <summary>前台窗口 / 空闲时间 的 Win32 调用</summary>
internal static class WinNative
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    /// <summary>前台窗口的 (进程名, 标题); 取不到返回空字符串</summary>
    public static (string Process, string Title) Foreground()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return ("", "");
        var sb = new StringBuilder(512);
        string title = GetWindowText(h, sb, sb.Capacity) > 0 ? sb.ToString().Trim() : "";
        string process = "";
        try
        {
            GetWindowThreadProcessId(h, out var pid);
            using var p = Process.GetProcessById((int)pid);
            process = p.ProcessName;
        }
        catch (Exception) { /* 进程刚好退出/无权限: 只有标题也可以 */ }
        return (process, title);
    }

    /// <summary>距离上一次键盘/鼠标操作过去了多少秒 (人不在电脑前时用来跳过采集)</summary>
    public static double IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        uint nowTick = unchecked((uint)Environment.TickCount);
        return unchecked(nowTick - info.dwTime) / 1000.0;
    }
}
