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
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);



    static readonly WinEvents.Handler Proc = (ev, h, obj, child) => OnEvent(ev, h, obj, child);
    // the place is read when a window shows, is moved by the user, comes to the front or hides – not with every change of a
    // location: that came hundreds of times a second (the cursor, the caret, scrolling in a browser) through the thread of the
    // interface
    static readonly uint[] Events = { WinEvents.Destroy, WinEvents.Show, WinEvents.Hide, WinEvents.MoveSizeEnd, WinEvents.Foreground };
    static bool _on;

    /// <summary>The place of each open window (its program and its last normal rectangle).</summary>
    static readonly Dictionary<IntPtr, (string Exe, Native.RECT Rect)> Open = new();
    /// <summary>The last place of a closed window, by its program (with the time it closed).</summary>
    static readonly Dictionary<string, (Native.RECT Rect, long At)> Closed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>On the thread of the interface (the events come by its message loop).</summary>
    public static void Start()
    {
        if (_on) return;
        _on = true;
        foreach (var ev in Events) WinEvents.On(ev, Proc);
    }

    public static void Stop()
    {
        if (!_on) return;
        _on = false;
        foreach (var ev in Events) WinEvents.Off(ev, Proc);
        Open.Clear();
        Closed.Clear();
    }

    /// <summary>A normal window of an app: top level, visible, with a title bar, no tool window, not maximized or minimized.</summary>
    static bool IsAppWindow(IntPtr h, bool visible = true) =>
        (!visible || Native.IsWindowVisible(h)) && GetAncestor(h, 2) == h                     // GA_ROOT
        && (Native.GetWindowLong(h, -16) & 0xC00000) == 0xC00000                              // WS_CAPTION
        && (Native.GetWindowLong(h, -20) & 0x80) == 0                                          // not WS_EX_TOOLWINDOW
        && !IsZoomed(h) && !Native.IsIconic(h);

    static void OnEvent(uint ev, IntPtr h, int idObject, int idChild)
    {
        if (idObject != 0 || idChild != 0 || h == IntPtr.Zero) return;   // the window itself, not a part of it
        try
        {
            switch (ev)
            {
                case WinEvents.MoveSizeEnd:
                case WinEvents.Foreground:
                    Remember(h, visible: true);
                    break;
                case WinEvents.Hide:
                    Remember(h, visible: false);                         // just hidden: its rectangle is still there
                    if (Open.Remove(h, out var hidden) && hidden.Exe != null) Closed[hidden.Exe] = (hidden.Rect, Environment.TickCount64);
                    break;
                case WinEvents.Destroy:
                    if (Open.Remove(h, out var last) && last.Exe != null) Closed[last.Exe] = (last.Rect, Environment.TickCount64);
                    break;
                case WinEvents.Show:
                    if (ExeOf(h) is { } exe && Closed.TryGetValue(exe, out var before)
                        && Environment.TickCount64 - before.At < 30 * 60_000) _ = PutBackAsync(h, exe, before.Rect);
                    else if (IsAppWindow(h, visible: false)) _ = RememberLaterAsync(h);   // its own place, once the app has put it there
                    break;
            }
        }
        catch (Exception ex) { Log.Write("Window places", ex); }
    }

    static void Remember(IntPtr h, bool visible)
    {
        if (IsAppWindow(h, visible) && Native.VisibleBounds(h, out var r))
            Open[h] = (Open.TryGetValue(h, out var known) ? known.Exe : ExeOf(h), r);
    }

    static async Task RememberLaterAsync(IntPtr h)
    {
        await Task.Delay(500);
        try { if (_on) Remember(h, visible: true); } catch { }
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
