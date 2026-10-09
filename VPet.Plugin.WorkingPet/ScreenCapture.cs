using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 截每块显示器并编码成 JPEG (base64). 只用 Win32 + WPF 自带的图像类, 不依赖 System.Drawing.
/// 缩到最宽 maxWidth 像素再编码, 上传体积小很多. 图片只在内存里, 不写盘.
/// </summary>
internal static class ScreenCapture
{
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    private const uint SRCCOPY = 0x00CC0020;

    /// <summary>每块显示器各截一张 (从左到右), 每张缩到最宽 maxWidth. 全部失败返回空列表</summary>
    public static List<string> CaptureAllJpegBase64(int maxWidth = 1600, int quality = 75)
    {
        var rects = new List<RECT>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref RECT r, IntPtr d) => { rects.Add(r); return true; }, IntPtr.Zero);
        rects.Sort((p, q) => p.Left.CompareTo(q.Left));

        var result = new List<string>();
        foreach (var r in rects)
        {
            var img = CaptureRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, maxWidth, quality);
            if (img != null) result.Add(img);
        }
        return result;
    }

    /// <summary>截屏幕上的一块区域 (虚拟桌面坐标). 失败返回 null (比如屏幕被锁定时 BitBlt 会失败)</summary>
    private static string? CaptureRect(int x, int y, int w, int h, int maxWidth, int quality)
    {
        if (w <= 0 || h <= 0) return null;

        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(mem, bmp);
        try
        {
            if (!BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY)) return null;

            BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            if (w > maxWidth)
            {
                double k = (double)maxWidth / w;
                src = new TransformedBitmap(src, new ScaleTransform(k, k));
            }
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return Convert.ToBase64String(ms.ToArray());
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
