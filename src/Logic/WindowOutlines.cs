using System.Runtime.InteropServices;

namespace HeMiDock;

/// <summary>
/// The thin line Windows 11 draws around windows (Settings → General → Outline). Off: every window of other apps gets no line,
/// when it appears and when it becomes active (an app may set its own again). Nothing is stored in Windows: back on, at the end
/// of the dock or when the window closes the line is as before. Windows 10 has no such line and ignores it.
/// </summary>
static class WindowOutlines
{
    const uint EVENT_SYSTEM_FOREGROUND = 0x3, EVENT_OBJECT_SHOW = 0x8002, WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    const int OBJID_WINDOW = 0, GWL_STYLE = -16, WS_CHILD = 0x40000000, WS_CAPTION = 0xC00000, WS_THICKFRAME = 0x40000;
    const int ColorNone = unchecked((int)0xFFFFFFFE), ColorDefault = unchecked((int)0xFFFFFFFF);

    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);

    static readonly WinEventProc Proc = OnEvent;                     // kept alive while Windows calls it
    static IntPtr _show, _foreground;

    /// <param name="hidden">No line around the windows.</param>
    public static void Apply(bool hidden)
    {
        bool on = _show != IntPtr.Zero;
        if (on == hidden) return;
        if (hidden)
        {
            _show = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, Proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            _foreground = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, Proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        }
        else
        {
            UnhookWinEvent(_show);
            UnhookWinEvent(_foreground);
            _show = _foreground = IntPtr.Zero;
        }
        EnumWindows((h, _) => { Set(h, hidden ? ColorNone : ColorDefault); return true; }, IntPtr.Zero);
    }

    static void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
    {
        if (obj == OBJID_WINDOW && child == 0) Set(hwnd, ColorNone);
    }

    /// <summary>Only windows with a frame of other apps: not menus, pop-ups or borderless full-screen games.</summary>
    static void Set(IntPtr hwnd, int color)
    {
        int style = Native.GetWindowLong(hwnd, GWL_STYLE);
        if ((style & WS_CHILD) != 0) return;
        if ((style & (WS_CAPTION | WS_THICKFRAME)) == 0)
        {
            // apps that draw their own frame have no caption: their main window counts too, but not menus, tool windows or full-screen games
            int ex = Native.GetWindowLong(hwnd, -20);
            if ((ex & (0x80 | 0x08000000)) != 0 || GetWindow(hwnd, 4) != IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return;   // tool window, no activation, owned
            var mon = Monitors.All().FirstOrDefault(m => m.Handle == Native.MonitorFromWindow(hwnd, 2));
            if (mon == null || r.Right - r.Left >= mon.Bounds.Right - mon.Bounds.Left && r.Bottom - r.Top >= mon.Bounds.Bottom - mon.Bounds.Top) return;
        }
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != Environment.ProcessId && !WindowManager.IsMarked(hwnd)) Native.SetDwmAttr(hwnd, Native.DWMWA_BORDER_COLOR, color);
    }
}