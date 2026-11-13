using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;

namespace HeMiDock;

/// <summary>The window on the screen: its place and length (with the animation), auto-hide and full-screen apps.</summary>
sealed partial class DockWindow
{
    // ───────────────────────── place, auto-hide, full screen ─────────────────────────

    /// <summary>
    /// Along its edge as long as its parts (or the whole edge), at the chosen distance from it; as the taskbar it reserves its
    /// strip. While the mouse is over it the window is thicker inwards by the room of the magnified icons.
    /// </summary>
    void Place()
    {
        if (_closed) return;
        if (_menusOpen > 0) { _placeLater = true; return; }          // moving the window would close the open menu
        _mon = Monitors.All().FirstOrDefault(m => m.Device == _mon.Device) ?? Monitors.Primary();
        Row.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        RowRight.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double s = Scale;
        bool full = S.DockFull != 0 && !Vertical;
        var pad = Root.Padding;
        // a dock made longer by hand: the apps stay at its start and the free room comes after them (before the notification
        // area), like the taskbar of Windows; more apps than fit make it longer again
        double fill = 0;
        if (!full && S.DockMinLength > 0)
        {
            double natural = (Vertical ? Row.DesiredSize.Height + pad.Top + pad.Bottom : Row.DesiredSize.Width + pad.Left + pad.Right) - _fill;
            fill = Math.Max(0, Math.Round(S.DockMinLength - natural));
        }
        if (fill != _fill)
        {
            _fill = fill;
            Apps.Padding = Vertical ? new Thickness(0, 0, 0, fill) : new Thickness(0, 0, fill, 0);
            Row.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        // + a little room: rounding to pixels at 125 / 140 % scaling took the bottom pixels of the icons (with the indicators)
        int body = (int)Math.Ceiling((Vertical ? Row.DesiredSize.Width + pad.Left + pad.Right : Math.Max(Row.DesiredSize.Height, RowRight.DesiredSize.Height) + pad.Top + pad.Bottom) * s + 3 * s);
        int length = (int)Math.Ceiling((Vertical ? Row.DesiredSize.Height + pad.Top + pad.Bottom : Row.DesiredSize.Width + pad.Left + pad.Right) * s + 3 * s);
        int gap = full ? 0 : (int)Math.Round(S.BottomMargin * s);
        int extra = _expanded ? (int)Math.Ceiling(Math.Max(Overflow, _bouncing > 0 ? IconPx * 0.4 : 0) * s) : 0;

        // the part of the monitor the dock lies in: what is left by the other bars
        Native.RECT area;
        if (_appBar != null && !S.AutoHide) area = _appBar.Reserve(body + gap, Edge, _mon.Bounds).ToNative();
        else { _appBar?.Dispose(); area = _mon.Work; }
        var b = new Native.RECT();
        switch (Edge)
        {
            case 1: b.Left = area.Left + gap; b.Right = b.Left + body; break;
            case 2: b.Right = area.Right - gap; b.Left = b.Right - body; break;
            case 3: b.Top = area.Top + gap; b.Bottom = b.Top + body; break;
            default: b.Bottom = area.Bottom - gap; b.Top = b.Bottom - body; break;
        }
        var work = _mon.Work;
        if (Vertical)
        {
            int h = Math.Min(length, work.Bottom - work.Top);
            b.Top = (work.Top + work.Bottom - h) / 2; b.Bottom = b.Top + h;
        }
        else
        {
            int span = area.Right - area.Left;
            int w = full ? span : Math.Min(length, span);
            b.Left = area.Left + (span - w) / 2; b.Right = b.Left + w;
        }
        if (full)
        {
            // full width: from edge to edge of the monitor, at its very edge – the area Windows gives an AppBar can be a few
            // pixels smaller (the hidden taskbar of Windows keeps its strip), and the dock then did not cover the screen
            b.Left = _mon.Bounds.Left; b.Right = _mon.Bounds.Right;
            if (Edge == 0) { b.Bottom = _mon.Bounds.Bottom; b.Top = b.Bottom - body; }
            else if (Edge == 3 && !S.TopBarOn) { b.Top = _mon.Bounds.Top; b.Bottom = b.Top + body; }
        }
        _body = b;
        var win = b;
        switch (Edge)
        {
            case 1: win.Right += extra; break;
            case 2: win.Left -= extra; break;
            case 3: win.Bottom += extra; break;
            default: win.Top -= extra; break;
        }
        // any frame left (see NoFrame) is added around, so the content has exactly its size
        int fx = Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width), fy = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
        MoveTo(new Windows.Graphics.RectInt32(win.Left - fx / 2, win.Top - fy / 2, win.Right - win.Left + fx, win.Bottom - win.Top + fy));
        _hideTimer.IsEnabled = S.AutoHide;
        if (!S.AutoHide && _autoHidden) { _autoHidden = false; ApplyVisible(); }
    }

    double _fill;                                                      // the free room after the apps (see DockMinLength)

    Windows.Graphics.RectInt32 _from, _to;
    double _moveStart;
    bool _moving, _placedOnce;

    /// <summary>
    /// The window to its new place. Only its length changing (an app opened or closed) goes smoothly in a short animation; the
    /// first placement, another edge or thickness, lighter drawing and animations switched off go at once.
    /// </summary>
    void MoveTo(Windows.Graphics.RectInt32 to)
    {
        var now = new Windows.Graphics.RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        bool lengthOnly = Vertical ? now.X == to.X && now.Width == to.Width : now.Y == to.Y && now.Height == to.Height;
        // while its size is dragged with the mouse the window follows the mouse directly: an animation started again with every
        // move of the mouse lagged behind and jerked
        bool animate = _placedOnce && _visible && lengthOnly && !_sizing && S.AnimationSpeed != 3 && !S.LowGraphics && (now.Width != to.Width || now.Height != to.Height);
        _placedOnce = true;
        if (!animate)
        {
            if (_moving) { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnMoveFrame; _moving = false; }
            AppWindow.MoveAndResize(to);
            return;
        }
        // the dock is placed again after every change of a window (a title, the active one): the same goal lets the running
        // animation go on – starting it again each time made it jerk and crawl
        // while places of icons open or close, the window follows the content in the same frame (no own animation)
        if (_sliding)
        {
            if (_moving) { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnMoveFrame; _moving = false; }
            if (!to.Equals(_shownRect)) { _shownRect = to; AppWindow.MoveAndResize(to); }
            return;
        }
        if (_moving && to.Equals(_to)) return;
        _retarget = _moving;                                           // a new goal in the middle: on from here, without a new slow start
        _from = now;
        _to = to;
        _moveStart = NowMs;
        if (_moving) return;
        _moving = true;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnMoveFrame;
    }

    bool _retarget;
    Windows.Graphics.RectInt32 _shownRect;

    void OnMoveFrame(object sender, object e)
    {
        double ms = Motion.Length(_retarget ? 220 : 300).TotalMilliseconds;
        double t = Math.Min(1, (NowMs - _moveStart) / ms);
        // a soft start and stop; continuing a change already under way only slows down at the end
        double k = _retarget ? 1 - Math.Pow(1 - t, 3) : t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        int L(int a, int b) => (int)Math.Round(a + (b - a) * k);
        var rect = new Windows.Graphics.RectInt32(L(_from.X, _to.X), L(_from.Y, _to.Y), L(_from.Width, _to.Width), L(_from.Height, _to.Height));
        if (!rect.Equals(_shownRect)) { _shownRect = rect; AppWindow.MoveAndResize(rect); }   // the same pixels again: no new layout
        if (t < 1 && !_closed) return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnMoveFrame;
        _moving = false;
        _retarget = false;
    }

    /// <summary>Auto-hide: away after a moment without the mouse (with dodge only while a window covers it), back at the edge.</summary>
    void AutoHideTick()
    {
        if (!S.AutoHide) { _hideTimer.Stop(); if (_autoHidden) { _autoHidden = false; ApplyVisible(); } return; }
        if (!Native.GetCursorPos(out var c)) return;
        var m = _mon.Bounds;
        bool atEdge = Edge switch
        {
            1 => c.X <= m.Left + 1 && c.Y >= _body.Top && c.Y < _body.Bottom,
            2 => c.X >= m.Right - 2 && c.Y >= _body.Top && c.Y < _body.Bottom,
            3 => c.Y <= m.Top + 1 && c.X >= _body.Left && c.X < _body.Right,
            _ => c.Y >= m.Bottom - 2 && c.X >= _body.Left && c.X < _body.Right
        };
        bool busy = _menusOpen > 0 || PopupWindow.Current != null || PreviewWindow.Shown != null || _dragPin != null;
        if (_autoHidden)
        {
            if (atEdge || busy || S.AutoHideDodge && !Covered()) { _autoHidden = false; _awayMs = 0; ApplyVisible(); }
            return;
        }
        Native.GetWindowRect(_hwnd, out var r);
        if (Fg.Contains(r, c.X, c.Y) || busy || S.AutoHideDodge && !Covered()) { _awayMs = 0; return; }
        _awayMs += 150;
        if (_awayMs < S.AutoHideDelay * 1000) return;
        _autoHidden = true;
        ApplyVisible();
    }

    /// <summary>The window in front lies over the place of the dock (not the desktop, not the app itself).</summary>
    bool Covered()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || IsShellOrOwn(fg) || Native.IsIconic(fg) || !Native.VisibleBounds(fg, out var r)) return false;
        return r.Left < _body.Right && r.Right > _body.Left && r.Top < _body.Bottom && r.Bottom > _body.Top;
    }

    static bool IsShellOrOwn(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out uint pid);
        if (pid == Environment.ProcessId) return true;
        var cls = new System.Text.StringBuilder(64);
        GetClassName(h, cls, cls.Capacity);
        return cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    /// <summary>A full-screen app (a video, a game, a presentation) on this monitor: the dock steps away.</summary>
    void UpdateFullScreen()
    {
        bool full = false;
        if (PopupWindow.Current == null && _menusOpen == 0)
        {
            if (SHQueryUserNotificationState(out int state) == 0 && state is 3 or 4 && Native.MonitorFromWindow(Native.GetForegroundWindow(), 2) == _mon.Handle) full = true;   // D3D full screen, presentation
            else
            {
                var fg = Native.GetForegroundWindow();
                if (fg != IntPtr.Zero && !IsShellOrOwn(fg) && Native.GetWindowRect(fg, out var r) && Native.MonitorFromWindow(fg, 2) == _mon.Handle)
                {
                    var m = _mon.Bounds;
                    full = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom && (Native.GetWindowLong(fg, -16) & 0x00C00000) != 0x00C00000;   // no caption
                }
            }
        }
        if (full == _fullScreen) return;
        _fullScreen = full;
        if (full) PreviewWindow.HideNow();
        ApplyVisible();
    }

    void ApplyVisible()
    {
        bool show = !_fullScreen && !_autoHidden;
        if (show == _visible) return;
        _visible = show;
        if (show) { AppWindow.Show(false); Place(); }
        else { ResetMagnification(); AppWindow.Hide(); }
    }

    /// <summary>--measure: the processor time every 10 s into the log.</summary>
    void Measure()
    {
        if ((DateTime.Now - _cpuAt).TotalSeconds < 10) return;
        using var p = Process.GetCurrentProcess();
        var cpu = p.TotalProcessorTime;
        double percent = (cpu - _cpu).TotalMilliseconds / (DateTime.Now - _cpuAt).TotalMilliseconds / Environment.ProcessorCount * 100;
        Log.Note("Measure", $"CPU {percent:0.00} %, memory {p.PrivateMemorySize64 / 1048576} MB private, {p.WorkingSet64 / 1048576} MB working set");
        _cpu = cpu;
        _cpuAt = DateTime.Now;
    }
}
