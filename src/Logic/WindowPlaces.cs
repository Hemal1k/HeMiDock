using System.Runtime.InteropServices;

namespace HeMiDock;

/// <summary>
/// Windows keeps a window that opens out of the strip the dock reserves: a window that was closed reaching into it (Outlook,
/// File Explorer by the bottom edge) came back moved up – it seemed the apps forgot their places. The last place of every
/// window is kept here; a new window of the same program that Windows moved only for the dock (the same size and left edge,
/// its bottom now right at the edge of the work area, the old one below it) goes back to that place. Maximized windows still
/// stop at the dock; nothing else is moved.
/// </summary>
static class WindowPlaces
{
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);

    const uint EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;

    static readonly WinEventProc Proc = OnEvent;                          // kept alive: Windows calls it
    static IntPtr _hook;

    /// <summary>The place of each open window (its program and its last normal rectangle).</summary>
    static readonly Dictionary<IntPtr, (string Exe, Native.RECT Rect)> Open = new();
    /// <summary>The last place of a closed window, by its program (with the time it closed).</summary>
    static readonly Dictionary<string, (Native.RECT Rect, long At)> Closed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>On the thread of the interface (the events come by its message loop).</summary>
    public static void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, Proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    public static void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        Open.Clear();
        Closed.Clear();
    }

    /// <summary>A normal window of an app: top level, visible, with a title bar, no tool window, not maximized or minimized.</summary>
    static bool IsAppWindow(IntPtr h) =>
        Native.IsWindowVisible(h) && GetAncestor(h, 2) == h                                   // GA_ROOT
        && (Native.GetWindowLong(h, -16) & 0xC00000) == 0xC00000                              // WS_CAPTION
        && (Native.GetWindowLong(h, -20) & 0x80) == 0                                          // not WS_EX_TOOLWINDOW
        && !IsZoomed(h) && !Native.IsIconic(h);

    static void OnEvent(IntPtr hook, uint ev, IntPtr h, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || h == IntPtr.Zero) return;   // the window itself, not a part of it
        try
        {
            switch (ev)
            {
                case EVENT_OBJECT_LOCATIONCHANGE:
                    if (IsAppWindow(h) && Native.VisibleBounds(h, out var r))
                        Open[h] = (Open.TryGetValue(h, out var known) ? known.Exe : ExeOf(h), r);
                    break;
                case EVENT_OBJECT_HIDE:
                case EVENT_OBJECT_DESTROY:
                    if (Open.Remove(h, out var last) && last.Exe != null) Closed[last.Exe] = (last.Rect, Environment.TickCount64);
                    break;
                case EVENT_OBJECT_SHOW:
                    if (ExeOf(h) is { } exe && Closed.TryGetValue(exe, out var before)
                        && Environment.TickCount64 - before.At < 30 * 60_000) _ = PutBackAsync(h, exe, before.Rect);
                    break;
            }
        }
        catch (Exception ex) { Log.Write("Window places", ex); }
    }

    static string ExeOf(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out uint pid);
        return Win32.ProcessPath(pid);
    }

    /// <summary>A moment after it shows (the app places it first): back where it was, when Windows moved it only for the dock.</summary>
    static async Task PutBackAsync(IntPtr h, string exe, Native.RECT was)
    {
        await Task.Delay(300);
        if (!IsAppWindow(h) || !Native.VisibleBounds(h, out var now) || !Native.GetWindowRect(h, out var outer)) return;
        var mon = Monitors.All().FirstOrDefault(m => Fg.Contains(m.Bounds, (now.Left + now.Right) / 2, (now.Top + now.Bottom) / 2));
        if (mon == null) return;
        // the visible frames compared (the size, the left edge); Windows keeps the whole rectangle in the work area, with the
        // invisible resize border (some pixels below the visible frame): its bottom is right at the edge then
        bool sameSize = Math.Abs((now.Right - now.Left) - (was.Right - was.Left)) <= 2 && Math.Abs((now.Bottom - now.Top) - (was.Bottom - was.Top)) <= 2;
        bool pushedUp = Math.Abs(now.Left - was.Left) <= 2 && now.Top < was.Top
                        && Math.Abs(outer.Bottom - mon.Work.Bottom) <= 2 && was.Bottom > now.Bottom
                        && was.Top < mon.Bounds.Bottom;                     // the old place was still on the screen
        if (!sameSize || !pushedUp) return;
        Native.SetWindowPos(h, IntPtr.Zero, outer.Left + (was.Left - now.Left), outer.Top + (was.Top - now.Top), 0, 0, 0x1 | 0x4 | 0x10);   // NOSIZE NOZORDER NOACTIVATE
        Closed.Remove(exe);
        Diag.Write("Window places", $"{Path.GetFileName(exe)} back to {was.Left},{was.Top}");
    }
}
