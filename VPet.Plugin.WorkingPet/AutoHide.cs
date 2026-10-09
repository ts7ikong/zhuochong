using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace VPet.Plugin.WorkingPet;

/// <summary>
/// 宠物自己走动 (窗口位置在变且鼠标左键没按住) 时隐藏面板, 停下一小会儿后再显示.
/// 用户按住鼠标拖动宠物不算, 面板照常跟随.
/// </summary>
internal sealed class AutoHide
{
    private const int VK_LBUTTON = 0x01;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private readonly PluginSettings settings;
    private readonly Action<bool> setHidden;
    private readonly DispatcherTimer restore;
    private readonly Window pet;
    private readonly EventHandler moved;
    private bool hidden, disposed;

    public AutoHide(Window pet, PluginSettings settings, Action<bool> setHidden)
    {
        this.pet = pet;
        this.settings = settings;
        this.setHidden = setHidden;
        restore = new DispatcherTimer(DispatcherPriority.Normal, pet.Dispatcher) { Interval = TimeSpan.FromMilliseconds(700) };
        restore.Tick += (_, _) => Set(false);
        moved = (_, _) => OnMoved();
        pet.LocationChanged += moved;
    }

    /// <summary>面板关闭时调用: 停掉定时器并取消订阅, 之后不再碰已关闭的窗口</summary>
    public void Dispose()
    {
        disposed = true;
        restore.Stop();
        pet.LocationChanged -= moved;
    }

    private void OnMoved()
    {
        if (disposed) return;
        if (!settings.HideWhenMoving) { if (hidden) Set(false); return; }
        if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0) return; // 用户在拖动
        Set(true);
        restore.Stop();
        restore.Start();
    }

    private void Set(bool hide)
    {
        if (disposed) return;
        if (!hide) restore.Stop();
        if (hidden == hide) return;
        hidden = hide;
        try { setHidden(hide); }
        catch (InvalidOperationException) { disposed = true; restore.Stop(); } // 窗口已关闭
    }
}
