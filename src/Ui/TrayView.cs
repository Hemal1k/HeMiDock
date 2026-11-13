using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace HeMiDock;

/// <summary>
/// The icons of the notification area (from ManagedShell) in the dock and in the top bar: clicks go to their apps, Ctrl + click
/// moves an icon into / out of the hidden ones, Windows Security and Windows Update get menus of their own, the pop-up of an
/// app is moved next to its icon.
/// </summary>
static class TrayView
{
    /// <param name="height">the room of the bar (the top bar): the vertical padding shrinks so the icon is never cut</param>
    public static FrameworkElement Icon(TrayItem item, IPanelWindow host, double padding = 5, double height = 0)
    {
        double size = Math.Clamp(DockSettings.Current.TrayIconSize, 12, 32);
        if (height > 0) size = Math.Min(size, height - 2);
        double vertical = height > 0 ? Math.Clamp((height - size) / 2, 0, 6) : 6;
        var border = new Border { Padding = new Thickness(padding, vertical, padding, vertical), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        border.Child = new Image { Width = size, Height = size, Source = Pictures.ForSize(item.Picture, size) };
        ToolTipService.SetToolTip(border, item.Title);
        border.PointerEntered += (_, _) => { border.Background = ThemeBrush.Get("SubtleFillColorSecondaryBrush"); ShellHost.Hover(item, true); };
        border.PointerExited += (_, _) => { border.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); ShellHost.Hover(item, false); };
        static bool Ctrl() => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        border.Tag = item;
        // a left press waits: moved by a few pixels it becomes dragging (into the arrow, before another icon, back into the
        // area), let go in place it is the click of the app (down and up at once). The mouse is followed through Windows
        // itself: the grid of the arrow is a window of its own, and outside it its events stopped (the icon opened instead of
        // moving to the taskbar).
        void ClickApp(int b)
        {
            if (Ctrl()) { if (b == 0) { ShellHost.TogglePin(item); host.TrayChanged(); } return; }   // Ctrl + click: into / out of the hidden icons
            if (item.IsWindowsSecurity) { if (b == 0) Fg.Open("windowsdefender:"); else if (b == 1) SecurityMenu(border, host); return; }
            if (item.IsWindowsUpdate) { if (b == 0) Fg.Open("ms-settings:windowsupdate"); else if (b == 1) UpdateMenu(border, host); return; }
            if (b == 1 && !item.IsPinned) HoldOverflow();              // its menu takes the foreground: the hidden icons stay open
            Fg.Force(host.Hwnd);                                       // the app needs the foreground to show (and close) its menu
            ShellHost.SetPlacement(item, host.ScreenRect(border));
            if (b is 0 or 1) TrayPopup.Follow(item.Window, host.ScreenRect(border), host.Monitor, host.PanelEdge);   // its pop-up goes next to the icon
            if (b == 0) ShellHost.Click(item, 0, down: true);              // the press waited for the release
            ShellHost.Click(item, b, down: false);
        }
        Microsoft.UI.Dispatching.DispatcherQueueTimer follow = null;
        Native.POINT pressAt = default;
        bool dragging = false, pressedHere = false;
        void Follow()
        {
            if (!Native.GetCursorPos(out var now)) return;
            bool down = (Native.GetAsyncKeyState(0x01) & 0x8000) != 0;
            if (!dragging && Math.Abs(now.X - pressAt.X) + Math.Abs(now.Y - pressAt.Y) >= 8)
            {
                dragging = true;
                border.Opacity = 0.3;                                    // its place stays, faint
                int px = (int)Math.Round(size * 1.15 * (Native.MonitorDpi(host.Monitor.Handle) ?? 1));
                DragGhost.Show(item.Picture, px, now.X, now.Y);
                SetDragging(true);                                       // the arrow shows up as a place to drop it
            }
            else if (dragging) { DragGhost.Move(now.X, now.Y); ShowWhere(item, host); }   // the icon goes with the mouse, the place is shown
            if (down) return;
            follow.Stop();                                                // let go
            follow = null;
            if (!dragging) { ClickApp(0); return; }
            dragging = false;
            border.Opacity = 1;
            DragGhost.Hide();
            DropAt(item, host);                                          // first: the arrow is still there to be dropped on
            SetDragging(false);
        }
        border.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            int b = Pressed(e, border);
            pressedHere = false;
            if (b == 0 && !Ctrl() && Native.GetCursorPos(out var at))
            {
                pressAt = at;
                dragging = false;
                follow?.Stop();
                follow = border.DispatcherQueue.CreateTimer();
                follow.Interval = TimeSpan.FromMilliseconds(12);
                follow.Tick += (_, _) => Follow();
                follow.Start();
                return;
            }
            pressedHere = true;                                          // the other buttons (and Ctrl + left): the release clicks
            if (Ctrl() || item.IsWindowsSecurity || item.IsWindowsUpdate) return;
            ShellHost.SetPlacement(item, host.ScreenRect(border));
            ShellHost.Click(item, b, down: true);
        };
        border.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            // the left button: the timer above sees it let go; a press elsewhere (a drag let go over this icon) is no click
            if (follow != null || !pressedHere) return;
            pressedHere = false;
            ClickApp(Pressed(e, border));
        };
        border.DoubleTapped += (_, _) => OpenOwnerLater(item);
        return border;
    }

    /// <summary>A tray icon is being dragged (the arrow shows up as a place to hide it).</summary>
    public static bool Dragging { get; private set; }
    public static event Action DraggingChanged;

    static void SetDragging(bool on)
    {
        if (Dragging == on) return;
        Dragging = on;
        DraggingChanged?.Invoke();
    }
    static Flyout _overflow;
    static readonly Dictionary<IPanelWindow, (Panel Tray, FrameworkElement Arrow)> Targets = new();

    static bool Inside(Native.RECT r, Native.POINT p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    /// <summary>
    /// Where a dragged icon would go at the mouse: into the arrow (1, the rectangle of the arrow) or among the icons of the
    /// area (2, before <c>Before</c>, or at the end when it is null; the rectangle is where the line goes), or nowhere (0).
    /// </summary>
    static (int Kind, TrayItem Before, Native.RECT Mark) Where(TrayItem item, IPanelWindow host, Native.POINT p)
    {
        if (!Targets.TryGetValue(host, out var t)) return (0, null, default);
        bool vertical = host.PanelEdge is 1 or 2;
        int room = (int)Math.Round(14 * (Native.MonitorDpi(host.Monitor.Handle) ?? 1));
        var icons = host.ScreenRect(t.Tray);
        bool overArrow = false;
        if (t.Arrow.Visibility == Visibility.Visible)
        {
            // the arrow with some room around it, up to the first icon (it appears while dragging and moves the icons a bit)
            var arrow = host.ScreenRect(t.Arrow);
            var hit = arrow;
            if (vertical) { hit.Left -= room; hit.Right += room; hit.Top -= room; hit.Bottom = Math.Max(hit.Bottom, icons.Top); }
            else { hit.Top -= room; hit.Bottom += room; hit.Left -= room; hit.Right = Math.Max(hit.Right, icons.Left); }
            // an icon already hidden (dragged from the arrow's menu) has nothing to do there: over the arrow it goes to the
            // taskbar, before the first icon
            if (Inside(hit, p))
            {
                if (item.IsPinned) return (1, null, arrow);
                overArrow = true;
            }
        }
        var area = icons;
        if (vertical) { area.Left -= room; area.Right += room; area.Bottom += room; }
        else { area.Top -= room; area.Bottom += room; area.Right += room; }
        if (!t.Tray.Children.OfType<FrameworkElement>().Any(c => c.Tag is TrayItem other && other != item))
        {
            // no icons in the area yet: it is only a few pixels wide – a place as wide as some icons to drop the first one
            if (vertical) { area.Top -= 2 * room; area.Bottom += 2 * room; }
            else { area.Left -= 2 * room; area.Right += 2 * room; }
        }
        if (!overArrow && !Inside(area, p)) return (0, null, default);
        var others = t.Tray.Children.OfType<FrameworkElement>().Where(c => c.Tag is TrayItem other && other != item)
            .Select(c => (Item: (TrayItem)c.Tag, Rect: host.ScreenRect(c))).ToList();
        foreach (var (other, r) in others)
            if ((vertical ? (r.Top + r.Bottom) / 2 : (r.Left + r.Right) / 2) > (vertical ? p.Y : p.X))
                return (2, other, vertical ? new Native.RECT { Left = r.Left, Right = r.Right, Top = r.Top, Bottom = r.Top } : new Native.RECT { Left = r.Left, Right = r.Left, Top = r.Top, Bottom = r.Bottom });
        var last = others.Count > 0 ? others[^1].Rect : icons;
        return (2, null, vertical ? new Native.RECT { Left = last.Left, Right = last.Right, Top = last.Bottom, Bottom = last.Bottom } : new Native.RECT { Left = last.Right, Right = last.Right, Top = last.Top, Bottom = last.Bottom });
    }

    /// <summary>While dragging: the place it would go, shown in the window.</summary>
    static void ShowWhere(TrayItem item, IPanelWindow host)
    {
        if (host.Marker == null || !Native.GetCursorPos(out var p)) return;
        var (kind, _, mark) = Where(item, host, p);
        bool vertical = host.PanelEdge is 1 or 2;
        if (kind == 1) host.Marker.Frame(host, mark);
        else if (kind == 2) host.Marker.Line(host, mark.Left, mark.Top, vertical ? mark.Right - mark.Left : mark.Bottom - mark.Top, vertical);
        else host.Marker.Hide();
    }

    /// <summary>Let go after dragging: into the arrow it is hidden, among the icons it goes where the line was.</summary>
    static void DropAt(TrayItem item, IPanelWindow host)
    {
        host.Marker?.Hide();
        if (!Native.GetCursorPos(out var p)) return;
        var (kind, before, _) = Where(item, host, p);
        bool changed = false;
        if (kind == 1 && item.IsPinned) { ShellHost.TogglePin(item); changed = true; }
        else if (kind == 2)
        {
            ShellHost.PinAt(item, before);
            changed = true;
        }
        _overflow?.Hide();
        if (changed) host.TrayChanged();
    }

    /// <summary>Where icons can be dropped in this window: its notification area and its arrow.</summary>
    public static void AcceptDrops(Panel tray, FrameworkElement arrow, IPanelWindow host)
    {
        if (tray.Background == null) tray.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);   // an empty area takes the mouse too
        tray.MinWidth = Math.Max(tray.MinWidth, 8);
        Targets[host] = (tray, arrow);
    }

    /// <summary>The hidden icons (the arrow) in a small grid next to the button, as big as the icons in it.</summary>
    public static void ShowOverflow(FrameworkElement button, IPanelWindow host)
    {
        var items = ShellHost.Tray().Where(i => !i.IsPinned).ToList();
        if (items.Count == 0) return;
        double size = Math.Clamp(DockSettings.Current.TrayIconSize, 12, 32), cell = size + 2 * 5 + 2;
        int columns = Math.Min(5, items.Count);
        var wrap = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = columns, ItemWidth = cell, ItemHeight = size + 12 + 2 };
        foreach (var item in items) wrap.Children.Add(Icon(item, host));
        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6)));
        presenter.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
        presenter.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
        presenter.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        // not kept inside the thin window of the dock (there it could only open beside the arrow): above it, as in Windows
        var flyout = new Flyout { Content = wrap, FlyoutPresenterStyle = presenter, AreOpenCloseAnimationsEnabled = Motion.On, ShouldConstrainToRootBounds = false };
        // see-through like the dock (the acrylic of Windows behind it); solid with the transparency off or the lighter drawing
        if (DockSettings.Current.Blur)
        {
            flyout.SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
            presenter.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Microsoft.UI.Colors.Transparent)));
        }
        flyout.Closed += (_, _) => { if (_overflow == flyout) _overflow = null; };
        flyout.Closing += (_, e) => { if (_holding) e.Cancel = true; };
        _overflow = flyout;
        Fg.Force(host.Hwnd);                                          // without the foreground the flyout closes at once
        var side = host.PanelEdge switch { 1 => FlyoutPlacementMode.Right, 2 => FlyoutPlacementMode.Left, 3 => FlyoutPlacementMode.Bottom, _ => FlyoutPlacementMode.Top };
        flyout.ShowAt(button, new FlyoutShowOptions { Placement = side });
    }

    static bool _holding;
    static long _holdUntil;
    static Microsoft.UI.Dispatching.DispatcherQueueTimer _holdTimer;

    /// <summary>
    /// A right click on a hidden icon: the menu of its app takes the foreground, and the hidden icons closed with it (in
    /// Windows they stay). They are kept open while the menu is there; when it went and something else is in front, they close.
    /// </summary>
    static void HoldOverflow()
    {
        if (_overflow == null) return;
        _holding = true;
        _holdUntil = Environment.TickCount64 + 900;                    // the menu comes a moment after the click
        if (_holdTimer == null)
        {
            _holdTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _holdTimer.Interval = TimeSpan.FromMilliseconds(150);
            _holdTimer.Tick += (_, _) =>
            {
                var menu = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "#32768", null);   // a menu of Windows (the apps' menus)
                if (Environment.TickCount64 < _holdUntil || menu != IntPtr.Zero && Native.IsWindowVisible(menu)) return;
                _holdTimer.Stop();
                _holding = false;
                Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint front);
                if (front != (uint)Environment.ProcessId) _overflow?.Hide();   // a click elsewhere or an item chosen: closed
            };
        }
        _holdTimer.Start();
    }

    static int Pressed(PointerRoutedEventArgs e, UIElement on)
    {
        var kind = e.GetCurrentPoint(on).Properties.PointerUpdateKind;
        return kind is Microsoft.UI.Input.PointerUpdateKind.RightButtonPressed or Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased ? 1
            : kind is Microsoft.UI.Input.PointerUpdateKind.MiddleButtonPressed or Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased ? 2 : 0;
    }

    /// <summary>
    /// A double click on a tray icon: some apps (Teams) react to it only from the taskbar of Windows. When the app did not come
    /// to the front by itself, its window is brought up, or the app is started again, which shows the running one.
    /// </summary>
    static async void OpenOwnerLater(TrayItem item)
    {
        try
        {
            Native.GetWindowThreadProcessId(item.Window, out uint pid);
            await Task.Delay(700);
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint front);
            if (front == pid || pid == 0) return;                     // the app opened itself
            using var p = Process.GetProcessById((int)pid);
            if (p.MainWindowHandle != IntPtr.Zero && Native.IsWindowVisible(p.MainWindowHandle)) { Fg.Force(p.MainWindowHandle); return; }
            if (p.MainModule?.FileName is string path) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("Tray double click", ex); }
    }

    static MenuFlyoutItem Item(string text, Action click, bool bold = false)
    {
        var item = new MenuFlyoutItem { Text = text };
        if (bold) item.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        item.Click += (_, _) => click();
        return item;
    }

    static void SecurityMenu(FrameworkElement at, IPanelWindow host) => SecurityMenu(at, host.NewMenu(), host.MenuPlacement);

    /// <summary>The menu of the shield among the status icons (as by the icon of the notification area).</summary>
    public static void SecurityMenu(FrameworkElement at) => SecurityMenu(at, new MenuFlyout(), FlyoutPlacementMode.Auto);

    /// <summary>
    /// The menu of Windows Security as its own icon has it (it ignores clicks of any other host than Explorer): the dashboard,
    /// a quick scan and an update of the protection definitions (both by Defender itself, MpCmdRun), and its parts.
    /// </summary>
    static void SecurityMenu(FrameworkElement at, MenuFlyout menu, FlyoutPlacementMode placement)
    {
        string mp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Windows Defender\MpCmdRun.exe");
        menu.Items.Add(Item(L.T("View security dashboard"), () => Fg.Open("windowsdefender:"), bold: true));
        if (File.Exists(mp))
        {
            menu.Items.Add(Item(L.T("Run quick scan"), () => { Fg.Run(mp, "-Scan -ScanType 1"); Fg.Open("windowsdefender://threat"); }));
            menu.Items.Add(Item(L.T("Update protection definitions"), () => { Fg.Run(mp, "-SignatureUpdate"); Fg.Open("windowsdefender://threat"); }));
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(L.T("Virus & threat protection"), () => Fg.Open("windowsdefender://threat")));
        menu.Items.Add(Item(L.T("Firewall & network protection"), () => Fg.Open("windowsdefender://network")));
        menu.Items.Add(Item(L.T("App & browser control"), () => Fg.Open("windowsdefender://appbrowser")));
        menu.Items.Add(Item(L.T("Device security"), () => Fg.Open("windowsdefender://devicesecurity")));
        menu.ShowAt(at, new FlyoutShowOptions { Placement = placement });
    }

    static void UpdateMenu(FrameworkElement at, IPanelWindow host)
    {
        var menu = host.NewMenu();
        menu.Items.Add(Item(L.T("Open Windows Update"), () => Fg.Open("ms-settings:windowsupdate"), bold: true));
        menu.Items.Add(Item(L.T("Restart options"), () => Fg.Open("ms-settings:windowsupdate-restartoptions")));
        menu.ShowAt(at, new FlyoutShowOptions { Placement = host.MenuPlacement });
    }

    /// <summary>Like the icon of Windows: each device with its volumes to open, ejecting a device, the device settings.</summary>
    public static void ShowDrives(FrameworkElement at, IPanelWindow host, Action changed)
    {
        var menu = host.NewMenu();
        foreach (var device in RemovableDrives.Now())
        {
            menu.Items.Add(new MenuFlyoutItem { Text = device.Name, IsEnabled = false });
            foreach (var volume in device.Volumes)
            {
                string free = volume.Free >= 0 ? "  ·  " + L.T("{0} free", RemovableDrives.Size(volume.Free)) : "";
                menu.Items.Add(Item(L.T("Open {0}", volume.Label) + free, () => Fg.Open(volume.Root)));
            }
            menu.Items.Add(Item(L.T("Eject"), () => Eject(device, changed), bold: true));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        menu.Items.Add(Item(L.T("Device settings"), () => Fg.Open("ms-settings:connecteddevices")));
        menu.ShowAt(at, new FlyoutShowOptions { Placement = host.MenuPlacement });
    }

    /// <summary>Ejects all volumes of a device; says so when it is in use and stays connected.</summary>
    static async void Eject(RemovableDevice device, Action changed)
    {
        RemovableDrives.Eject(device);
        await Task.Delay(5000);
        changed();
        if (RemovableDrives.Now().Any(d => d.Number == device.Number))   // a message of Windows: a dialog would not fit into the dock
            MessageBox(IntPtr.Zero, L.T("{0} is in use and cannot be ejected. Close the files and apps that use it and try again.", device.Name), L.T("Eject"), 0x40 | 0x40000);   // information, topmost
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
