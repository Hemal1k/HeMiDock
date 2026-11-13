using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HeMiDock;

/// <summary>
/// The pop-ups of the dock (Start, control center, calendar): a borderless window with the acrylic of Windows over the
/// dock, hidden by Esc or when another window is activated. One is open at a time (<see cref="Current"/>).
/// Each kind is created once and only hidden and shown again: a window of WinUI that is closed does not give all of its
/// memory back, so creating them anew grew the app by megabytes with every opening.
/// </summary>
class PopupWindow : Window
{
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);

    static readonly Dictionary<string, PopupWindow> Pool = new();
    public static PopupWindow Current { get; private set; }

    protected IntPtr Hwnd { get; }
    readonly string _kind;
    readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(200) };
    bool _visible;
    Windows.Graphics.RectInt32 _placed;

    protected PopupWindow(string kind)
    {
        _kind = kind;
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SystemBackdrop = new AlwaysActiveAcrylic();
        ExtendsContentIntoTitleBar = false;                         // true draws a dark line along the top edge
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        NoFrame.HideFromSwitchers(AppWindow, Hwnd);
        NoFrame.Apply(Hwnd);
        int round = 2;
        Win32.DwmSetWindowAttribute(Hwnd, 33, ref round, 4);
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated && _visible && !KeepOpen) CloseNow(); };
        // opened by a key the window does not always get the focus (and so never loses it): while it is open, a click of the
        // mouse beside it closes it – unless the app in front is this one (a menu of the pop-up itself)
        _watch.Tick += (_, _) =>
        {
            if (!_visible) { _watch.Stop(); return; }
            if (KeepOpen) return;
            bool down = (Native.GetAsyncKeyState(0x01) & 0x8001) != 0 || (Native.GetAsyncKeyState(0x02) & 0x8001) != 0;
            if (!down || !Native.GetCursorPos(out var c) || !Native.GetWindowRect(Hwnd, out var r) || Fg.Contains(r, c.X, c.Y)) return;
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid);
            if (pid != Environment.ProcessId) CloseNow();
        };
        Closed += (_, _) => { Pool.Remove(_kind); if (Current == this) Current = null; };
    }

    /// <summary>While true, losing the focus does not hide it (a menu or dialog of its own is open).</summary>
    protected bool KeepOpen { get; set; }

    protected double Scale => GetDpiForWindow(Hwnd) / 96.0;

    static string _closedKind;
    static long _closedAt;

    const int DwmCloak = 13;                                         // DWMWA_CLOAK

    /// <summary>
    /// Uncloaks the window after two frames of it were drawn (its new content is on the screen buffer by then) and starts
    /// its coming-in animation; at the latest after 250 ms, should no frame come.
    /// </summary>
    void ShowWhenDrawn(string kind)
    {
        int frames = 0;
        bool done = false;
        EventHandler<object> onFrame = null;
        Microsoft.UI.Dispatching.DispatcherQueueTimer late = null;
        void Uncloak()
        {
            if (done) return;
            done = true;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= onFrame;
            late?.Stop();
            Native.SetDwmAttr(Hwnd, DwmCloak, 0);
            if (_visible && Content is UIElement content) Motion.In(content, (App.Anchor ?? App.Dock)?.AnchorFor(kind)?.Edge ?? 0, FillsWindow ? 28 : 14);
        }
        onFrame = (_, _) => { if (++frames >= 2) Uncloak(); };
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += onFrame;
        late = DispatcherQueue.CreateTimer();
        late.Interval = TimeSpan.FromMilliseconds(250);
        late.IsRepeating = false;
        late.Tick += (_, _) => Uncloak();
        late.Start();
    }

    /// <summary>Opens a pop-up of this kind, or hides it when it is the one open.</summary>
    public static void Toggle(string kind, Func<PopupWindow> create)
    {
        if (!App.IsReady) { App.WhenReady(() => Toggle(kind, create)); return; }   // a pop-up right after the start crashed like the settings
        if (Current?._kind == kind) { Current.CloseNow(); return; }
        // a click on the button of the open pop-up: the pop-up lost the foreground to the dock first and closed itself – the
        // click then opened it again instead of closing it
        if (Current == null && _closedKind == kind && Environment.TickCount64 - _closedAt < 350) { _closedKind = null; return; }
        Current?.CloseNow();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (!Pool.TryGetValue(kind, out var w)) w = Make(kind, create);
        else w.Reopen();
        Diag.Write("Popup", $"{kind} built in {watch.ElapsedMilliseconds} ms");   // debug mode only
        Current = w;
        w._visible = true;
        w.Place();
        // shown cloaked (invisible to the screen, but drawn) until its new content has been drawn, then it comes in from the
        // dock, as the flyouts of Windows: a window shown again showed its old content or an empty frame for a moment first
        // (the control center and Start flashed now and then)
        Native.SetDwmAttr(w.Hwnd, DwmCloak, 1);
        w.Activate();
        Fg.Force(w.Hwnd);
        w.ShowWhenDrawn(kind);
        Native.GetAsyncKeyState(0x01);                                // forget the click that opened it
        Native.GetAsyncKeyState(0x02);
        w._watch.Start();
        w.Shown();
    }

    static PopupWindow Make(string kind, Func<PopupWindow> create)
    {
        var w = Pool[kind] = create();
        // the content in a host that measures it without limits (see PopupHost): the window follows its real size – before,
        // the content was cut to the window and a list that grew later (Wi-Fi networks) was taller than its window
        if (w.Content is FrameworkElement body && w.Content is not PopupHost)
        {
            w.Content = null;
            w.Content = new PopupHost { Child = body, Fill = w.FillsWindow };
        }
        ThemeWatch.Track(w);
        if (w.Content is PopupHost root)
        {
            root.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape && !w.OnEscape()) w.CloseNow(); };
            // parts that come later (the calendar, a list loaded in the background) change the size: placed again
            root.Loaded += (_, _) => w.Place();
            root.ContentSizeChanged += _ => { if (w._visible) w.Place(); };
        }
        return w;
    }

    /// <summary>
    /// Made ahead while the app is idle (not shown): the first opening paid for building the whole pop-up, which made the
    /// control center slow to appear. The next opening only shows it.
    /// </summary>
    public static void Prepare(string kind, Func<PopupWindow> create)
    {
        if (Pool.ContainsKey(kind)) return;
        try { Make(kind, create).Hidden(); }                         // hidden: its timers and watching wait for the opening
        catch (Exception ex) { Log.Write("Prepare " + kind, ex); }
    }

    /// <summary>Shown again: the content is brought up to date (it may be hours old).</summary>
    protected virtual void Reopen() { }

    /// <summary>Hidden: timers and watching stop until it is shown again.</summary>
    protected virtual void Hidden() { }

    /// <summary>Esc: a page of the pop-up may go back first (true = handled).</summary>
    protected virtual bool OnEscape() => false;

    protected virtual void Shown() { }

    /// <summary>The size of the content, above the dock by its button (or in the middle of the free part of the main screen).</summary>
    /// <summary>The content fills the window (full-screen Start); otherwise it keeps its own size and the window follows it.</summary>
    protected virtual bool FillsWindow => false;

    /// <summary>The size the content wants (points), not cut to the window.</summary>
    protected Windows.Foundation.Size ContentSize
    {
        get
        {
            if (Content is PopupHost host) { host.Fill = FillsWindow; return host.ContentSize; }
            if (Content is not FrameworkElement fe) return default;
            fe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            return fe.DesiredSize;
        }
    }

    protected virtual void Place()
    {
        // its own height: a page that grows (Sound) or a notification removed changes it, and the window is placed again –
        // the side at the dock stays where it is (the calendar does not move)
        var size = ContentSize;
        if (size.Width <= 0) return;
        double s = Scale;
        int w = (int)Math.Ceiling(size.Width * s), h = (int)Math.Ceiling(size.Height * s);
        IPanelWindow dock = App.Anchor ?? App.Dock;
        var anchor = dock?.AnchorFor(_kind);
        if (Content is PopupHost host) host.AtBottom = anchor is not { } placed || placed.Edge == 0;   // above a dock at the bottom
        var area = dock?.Monitor.Work ?? Win32.WorkArea().ToNative();
        int gap = (int)Math.Round(10 * s);
        h = Math.Min(h, area.Bottom - area.Top - 2 * gap);
        w = Math.Min(w, area.Right - area.Left - 2 * gap);
        int x, y;
        if (anchor is not { } a) { x = (area.Left + area.Right - w) / 2; y = area.Bottom - h - gap; }
        else
        {
            // next to the dock on its inner side, in the middle of the button, kept on the monitor
            (x, y) = a.Edge switch
            {
                1 => (a.X + gap, a.Y - h / 2),
                2 => (a.X - w - gap, a.Y - h / 2),
                3 => (a.X - w / 2, a.Y + gap),
                _ => (a.X - w / 2, a.Y - h - gap)
            };
            if (a.Edge is 1 or 2 && _kind == "start") y = area.Top + gap;   // Start at a side dock: from the top, like a menu
        }
        x = Math.Clamp(x, area.Left + gap, Math.Max(area.Left + gap, area.Right - w - gap));
        y = Math.Clamp(y, area.Top + gap, Math.Max(area.Top + gap, area.Bottom - h - gap));
        MoveTo(new Windows.Graphics.RectInt32(x, y, w, h));
    }

    /// <summary>Moves the window only when the place changed (placing runs after every layout of the content).</summary>
    protected void MoveTo(Windows.Graphics.RectInt32 rect)
    {
        if (rect.Equals(_placed) && AppWindow.Position.X == rect.X && AppWindow.Position.Y == rect.Y && AppWindow.Size.Width == rect.Width && AppWindow.Size.Height == rect.Height) return;
        _placed = rect;
        AppWindow.MoveAndResize(rect);
    }

    /// <summary>Hides the pop-up (it stays for the next opening).</summary>
    public void CloseNow()
    {
        if (!_visible) return;
        _visible = false;
        if (Current == this) Current = null;
        _closedKind = _kind;
        _closedAt = Environment.TickCount64;
        AppWindow.Hide();
        Hidden();
    }
}
