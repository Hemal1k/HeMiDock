using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace HeMiDock;

/// <summary>
/// Top bar (Settings → Top bar, off by default): a bar along the top edge like on macOS / GNOME, on the monitor of the dock.
/// The system menu, the name of the active app and the virtual desktops on the left; what is playing, the system monitor,
/// the notification area, the keyboard, the status icons and the clock on the right (the clock may be in the middle).
/// The same parts are hidden in the dock, which keeps Start and the apps. In edit mode the parts are dragged to any of the
/// three places of the bar.
/// </summary>
sealed class TopBarWindow : Window, IPanelWindow
{
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    readonly IntPtr _hwnd;
    readonly AppBar _appBar;
    readonly Grid _root = new();
    readonly Border _line = new() { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
    readonly StackPanel _left = Zone(HorizontalAlignment.Left), _center = Zone(HorizontalAlignment.Center), _right = Zone(HorizontalAlignment.Right);
    readonly List<(string Id, FrameworkElement El)> _parts = new();
    readonly List<Action> _unsubscribe = new();
    readonly DispatcherTimer _fast = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly DispatcherTimer _slow = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly StatusParts _status = new();
    readonly TextBlock _appName = new() { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 };
    readonly TextBlock _time = new() { FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _date = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    readonly TextBlock _badgeText = new() { FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _badge;
    readonly TextBlock _mediaText = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 280 };
    readonly FontIcon _mediaPlay = new() { FontSize = 12, Margin = new Thickness(8, 0, 0, 0) };
    readonly TextBlock _monitorText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _keys = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
    readonly TextBlock _langText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _tray = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _desks = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    readonly Button _overflow, _system, _app, _search, _taskView, _bin, _downloads, _lang, _clock, _desktop;
    readonly Border _media, _monitor;
    readonly FrameworkElement _trayPart, _keysPart;
    MonInfo _mon;
    IntPtr _appWindow;
    string _trayShown;
    int _menusOpen, _tick, _idleMs;
    bool _hidden, _fullScreen, _closed, _shownNow = true;
    readonly Dictionary<uint, string> _processNames = new();
    static FrameworkElement _dragPart;

    static DockSettings S => DockSettings.Current;

    public TopBarWindow()
    {
        Title = "HeMiDock top bar";
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _mon = Monitors.Dock();
        SystemBackdrop = new AlwaysActiveAcrylic();
        ExtendsContentIntoTitleBar = false;                         // true draws a dark line along the top edge
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        NoFrame.HideFromSwitchers(AppWindow, _hwnd);
        NoFrame.Apply(_hwnd);
        Native.SetDwmAttr(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_DONOTROUND);   // a straight bar along the whole edge
        Native.SetDwmAttr(_hwnd, Native.DWMWA_BORDER_COLOR, unchecked((int)0xFFFFFFFE));
        int exclude = 1;
        Win32.DwmSetWindowAttribute(_hwnd, 12, ref exclude, 4);
        if (ShellHost.Running) _appBar = new AppBar(_hwnd);

        // left: the system menu (like the activities indicator of GNOME: a pill and two dots), the app, the desktops
        _system = BarButton(null, L.T("System"));
        _system.Content = new Microsoft.UI.Xaml.Shapes.Path
        {
            Width = 25, Height = 6, Stretch = Stretch.Uniform, Fill = ThemeBrush.Get("TextFillColorPrimaryBrush"),
            Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), "M3,0 H11 A3,3 0 0 1 11,6 H3 A3,3 0 0 1 3,0 Z M19,0 A3,3 0 1 1 18.99,0 Z M28,0 A3,3 0 1 1 27.99,0 Z")
        };
        _system.Click += (_, _) => { if (S.TopBarSystemStart) { App.Anchor = this; PopupWindow.Toggle("start", () => new StartWindow()); } else App.Dock?.ShowPowerMenu(aboveStart: false); };
        _system.RightTapped += (_, e) => { e.Handled = true; App.Dock?.ShowPowerMenu(aboveStart: false); };
        _app = BarButton(null, null);
        _app.Content = _appName;
        _app.Padding = new Thickness(10, 0, 10, 0);
        _app.Click += (_, _) => { if (App.Dock?.MenuForWindow(_appWindow) is { } menu) { Track(menu); menu.ShowAt(_app, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom }); } };

        // right: what is playing and the system monitor on capsules, the system buttons, the widgets of the control center
        var note = new FontIcon { Glyph = "", FontSize = 12, Margin = new Thickness(0, 0, 8, 0) };
        _media = Capsule(new StackPanel { Orientation = Orientation.Horizontal, Children = { note, _mediaText, _mediaPlay } }, L.T("Play / pause – right click: previous / next"), async () => await BarMedia.PlayPauseAsync());
        ((Button)_media.Child).RightTapped += (_, e) =>
        {
            e.Handled = true;
            var menu = NewMenu();
            menu.Items.Add(Item(L.T("Previous"), async () => await BarMedia.PreviousAsync()));
            menu.Items.Add(Item(L.T("Play / pause"), async () => await BarMedia.PlayPauseAsync()));
            menu.Items.Add(Item(L.T("Next"), async () => await BarMedia.NextAsync()));
            menu.ShowAt(_media, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
        };
        _monitorText.Foreground = ThemeBrush.Get("TextFillColorSecondaryBrush");
        _monitor = Capsule(_monitorText, L.T("Task Manager"), () => Fg.Open("taskmgr.exe"));
        _search = BarButton("", L.T("Search"));
        _search.Click += (_, _) => SpotlightWindow.Toggle();
        _taskView = BarButton("", L.T("Task view"));
        _taskView.Click += (_, _) => AppShell.Tap(0x5B, 0x09);
        _bin = BarButton("", L.T("Recycle Bin"));
        _bin.Click += (_, _) => RecycleBin.Open();
        _downloads = BarButton("", L.T("Downloads"));
        _downloads.Click += (_, _) => Fg.Open(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        _lang = BarButton(null, null);
        _lang.Content = _langText;
        _lang.Click += (_, _) => LanguageMenu();
        _overflow = BarButton("", null, 11);
        _overflow.Padding = new Thickness(4, 0, 4, 0);
        _overflow.Click += (_, _) => TrayView.ShowOverflow(_overflow, this);
        TrayView.AcceptDrops(_tray, _overflow, this);
        Marker = new DropMarker(_root);
        _trayPart = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch, Children = { _tray, _overflow } };
        _keysPart = _keys;
        _status.Button.Click += (_, _) => ToggleControlCenter();
        _status.Button.RightTapped += (_, e) => { e.Handled = true; ToggleControlCenter(); };   // the right button too
        _badge = new Border
        {
            MinWidth = 16, Height = 16, CornerRadius = new CornerRadius(8), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = ThemeBrush.Get("AccentFillColorDefaultBrush"), Child = _badgeText, Visibility = Visibility.Collapsed
        };
        _badgeText.Foreground = ThemeBrush.Get("TextOnAccentFillColorPrimaryBrush");
        // the time bold and bright, the date lighter and dimmer: the two ran together as one text
        _time.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        _date.Foreground = ThemeBrush.Get("TextFillColorSecondaryBrush");
        _date.Margin = new Thickness(10, 0, 0, 0);
        _clock = BarButton(null, null);
        _clock.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { _time, _date, _badge } };   // one line like on macOS
        _clock.Padding = new Thickness(8, 0, 8, 0);
        _clock.Click += (_, _) => ToggleCalendar();
        // show the desktop: the thin strip at the very end, like in the dock (the dock has none while the top bar is on)
        _desktop = BarButton(null, L.T("Show desktop"));
        _desktop.Content = new Border { Width = 2, Height = 14, Background = ThemeBrush.Get("DividerStrokeColorDefaultBrush") };
        _desktop.Padding = new Thickness(5, 0, 5, 0);
        _desktop.Click += (_, _) => AppShell.Tap(0x5B, 0x44);          // Win+D

        _parts.AddRange(new (string, FrameworkElement)[]
        {
            ("system", _system), ("app", _app), ("desks", _desks), ("monitor", _monitor), ("media", _media), ("tray", _trayPart), ("keys", _keysPart), ("lang", _lang),
            ("search", _search), ("taskview", _taskView), ("sysbin", _bin), ("downloads", _downloads), ("status", _status.Button), ("clock", _clock), ("desktop", _desktop)
        });
        foreach (var (id, glyph, name) in new[] { ("night", "", "Night light"), ("cast", "", "Cast"), ("access", "", "Accessibility"), ("display", "", "Monitors"), ("awake", "", "Keep awake"), ("brightness", "", "Display"), ("power", "", "Power mode") })
        {
            var widget = BarButton(glyph, L.T(name));
            widget.Click += (_, _) => { ControlCenterWindow.OpenWith = id; App.Anchor = this; PopupWindow.Toggle("control", () => new ControlCenterWindow()); };
            widget.RightTapped += (_, e) =>
            {
                e.Handled = true;
                var menu = NewMenu();
                menu.Items.Add(Item(L.T("Back to the control center"), () => QuickLayout.SetOnBar(id, false)));
                menu.ShowAt(widget, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
            };
            _parts.Add(("w:" + id, widget));
        }
        foreach (var (_, el) in _parts) MakeMovable(el);

        _line.Background = ThemeBrush.Get("DividerStrokeColorDefaultBrush");
        _root.Children.Add(_left);
        _root.Children.Add(_center);
        _root.Children.Add(_right);
        _root.Children.Add(_line);
        _root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);   // clicks on empty places reach the bar
        _root.AllowDrop = true;
        _root.DragOver += (_, e) => { if (_dragPart != null) { e.AcceptedOperation = DataPackageOperation.Move; e.DragUIOverride.IsCaptionVisible = false; } };
        _root.Drop += (_, e) => DropPart(e.GetPosition(_root));
        _root.DoubleTapped += Root_DoubleTapped;
        _root.RightTapped += Root_RightTapped;
        _root.PointerWheelChanged += (_, e) => { if (e.Handled) return; e.Handled = true; StatusParts.Wheel(e, _root); _status.Update(); };   // the wheel sets the volume
        Content = _root;
        // edit mode: the lower edge (8 points) dragged changes the height of the bar
        (int Y, double Height)? resizeFrom = null;
        _root.PointerPressed += (_, e) =>
        {
            if (!S.DockEditMode || !e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed) return;
            if (e.GetCurrentPoint(_root).Position.Y < _root.ActualHeight - 8 || !Native.GetCursorPos(out var c)) return;
            resizeFrom = (c.Y, S.TopBarHeight);
            _root.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        _root.PointerMoved += (_, e) =>
        {
            if (resizeFrom is not { } r || !Native.GetCursorPos(out var c)) return;
            S.TopBarHeight = Math.Round(Math.Clamp(r.Height + (c.Y - r.Y) / Scale, 24, 44));
            e.Handled = true;
        };
        _root.PointerReleased += (_, _) =>
        {
            if (resizeFrom == null) return;
            resizeFrom = null;
            _root.ReleasePointerCaptures();
            S.Save();
        };
        ThemeWatch.Track(this);

        // what changes the bar
        Action tray = () => Ui.Post(() => { _trayShown = null; RebuildTray(); });
        Action status = () => Ui.Post(_status.Update), badge = () => Ui.Post(UpdateBadge), media = () => Ui.Post(UpdateMedia), desks = () => Ui.Post(UpdateDesktops);
        if (ShellHost.Running) { ShellHost.TrayChanged += tray; _unsubscribe.Add(() => ShellHost.TrayChanged -= tray); }
        Weather.Changed += status; _unsubscribe.Add(() => Weather.Changed -= status);
        NotificationBadge.Changed += badge; _unsubscribe.Add(() => NotificationBadge.Changed -= badge);
        BarMedia.Changed += media; _unsubscribe.Add(() => BarMedia.Changed -= media);
        VirtualDesktops.Changed += desks; _unsubscribe.Add(() => VirtualDesktops.Changed -= desks);
        System.Net.NetworkInformation.NetworkAddressChangedEventHandler net = (_, _) => Ui.Post(_status.Update);
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += net;
        _unsubscribe.Add(() => System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= net);
        System.ComponentModel.PropertyChangedEventHandler settings = (_, _) => Ui.Post(Apply);
        S.PropertyChanged += settings; _unsubscribe.Add(() => S.PropertyChanged -= settings);
        BluetoothWatch.Start();
        NotificationBadge.Watch(true);
        VirtualDesktops.Watch();
        _fast.Tick += (_, _) => FastTick();
        _slow.Tick += (_, _) => SlowTick();
        _fast.Start();
        _slow.Start();
        Closed += (_, _) =>
        {
            _closed = true;
            _fast.Stop(); _slow.Stop();
            foreach (var off in _unsubscribe) off();
            _appBar?.Dispose();
        };
        Apply();
    }

    static StackPanel Zone(HorizontalAlignment side) => new()
    {
        Orientation = Orientation.Horizontal, HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Stretch, Spacing = 2,
        Margin = side == HorizontalAlignment.Left ? new Thickness(6, 0, 0, 0) : side == HorizontalAlignment.Right ? new Thickness(0, 0, 6, 0) : new Thickness(0)
    };

    /// <summary>A button of the bar with a symbol (or own content), as high as the bar.</summary>
    static Button BarButton(string glyph, string tip, double size = 14)
    {
        var b = new Button { Style = (Style)Application.Current.Resources["DockButton"], Padding = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 3, 0, 3) };
        if (glyph != null) b.Content = new FontIcon { Glyph = glyph, FontSize = size };
        if (tip != null) ToolTipService.SetToolTip(b, tip);
        return b;
    }

    /// <summary>What is playing and the system monitor each on a slightly lighter surface, so their text does not run into the icons.</summary>
    static Border Capsule(FrameworkElement content, string tip, Action click)
    {
        var button = BarButton(null, tip);
        button.Content = content;
        button.Padding = new Thickness(10, 0, 10, 0);
        button.Margin = new Thickness(0);
        button.Click += (_, _) => click();
        return new Border
        {
            CornerRadius = new CornerRadius(6), Child = button, Margin = new Thickness(4, 4, 4, 4), Visibility = Visibility.Collapsed,
            Background = ThemeBrush.Get("ControlFillColorDefaultBrush")
        };
    }

    static MenuFlyoutItem Item(string text, Action click, string glyph = null)
    {
        var item = new MenuFlyoutItem { Text = text };
        if (glyph != null) item.Icon = new FontIcon { Glyph = glyph };
        item.Click += (_, _) => click();
        return item;
    }

    double Scale => GetDpiForWindow(_hwnd) / 96.0;
    static Visibility V(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    // ───────────────────────── parts ─────────────────────────

    /// <summary>After the settings: which parts are shown, their places, the height and the place of the bar.</summary>
    void Apply()
    {
        if (_closed) return;
        double item = Math.Max(18, S.TopBarHeight - 6);
        _system.Visibility = V(S.TopBarSystemMenu);
        _app.Visibility = V(S.TopBarAppName);
        bool sys = S.SysIconsOnBar;                                    // the same buttons as in the dock, never both
        _search.Visibility = V(sys && S.ShowSearchButtons);
        _taskView.Visibility = V(sys && S.ShowTaskViewButton);
        _bin.Visibility = V(sys && S.ShowRecycleBin);
        _downloads.Visibility = V(sys && S.ShowDownloads);
        _clock.Visibility = V(S.ShowClock);
        _date.Visibility = V(S.ShowDate);
        _desktop.Visibility = V(S.ShowDesktopButton);
        _time.FontSize = Math.Max(12, S.ClockFontSize + 1.5);
        _date.FontSize = Math.Max(11, S.DateFontSize + 1);
        foreach (var (id, el) in _parts.Where(p => p.Id.StartsWith("w:"))) el.Visibility = V(QuickLayout.OnBar(id[2..]));
        _line.Visibility = V(S.DockOutline || S.LowGraphics);          // a line along the lower edge (with the outline)
        foreach (var (_, el) in _parts) if (el is FrameworkElement fe && fe != _media && fe != _monitor) fe.MaxHeight = item;
        foreach (var (_, el) in _parts) if (el.CanDrag != S.DockEditMode) el.CanDrag = S.DockEditMode;
        Arrange();
        _trayShown = null;
        RebuildTray();
        _status.Update();
        UpdateMedia();
        UpdateMonitor();
        UpdateKeys();
        UpdateLanguage();
        UpdateDesktops();
        UpdateClock();
        UpdateBadge();
        Place();
    }

    /// <summary>
    /// The parts in their places: by default the system menu, app and desktops on the left, the rest on the right. The order
    /// moved in edit mode is kept; the clock is where the setting says (left, in the middle, right).
    /// </summary>
    void Arrange()
    {
        var stored = S.TopBarOrder.Split(';');
        var zones = new List<string>[3];
        var seen = new HashSet<string>();
        for (int z = 0; z < 3; z++)
            zones[z] = (z < stored.Length ? stored[z] : "").Split('|', StringSplitOptions.RemoveEmptyEntries).Where(id => _parts.Any(x => x.Id == id) && seen.Add(id)).ToList();
        foreach (var (id, _) in _parts)
            if (!seen.Contains(id)) zones[id is "system" or "app" or "desks" ? 0 : 2].Add(id);
        int clockZone = S.TopBarClockPos;
        if (!zones[clockZone].Contains("clock"))
        {
            foreach (var z in zones) z.Remove("clock");
            zones[clockZone].Add("clock");
        }
        var panels = new[] { _left, _center, _right };
        foreach (var p in panels) p.Children.Clear();
        for (int z = 0; z < 3; z++)
            foreach (var id in zones[z]) panels[z].Children.Add(_parts.First(x => x.Id == id).El);
    }

    void MakeMovable(FrameworkElement el)
    {
        el.DragStarting += (_, e) => { _dragPart = el; e.Data.RequestedOperation = DataPackageOperation.Move; e.Data.SetText("hemidock-bar-part"); };
        el.DropCompleted += (_, _) => _dragPart = null;
    }

    /// <summary>The dragged part goes to the third of the bar under the mouse, before the nearest part; the order is stored.</summary>
    void DropPart(Windows.Foundation.Point at)
    {
        var part = _dragPart;
        _dragPart = null;
        if (part == null) return;
        double width = _root.ActualWidth;
        int zone = at.X < width / 3 ? 0 : at.X < width * 2 / 3 ? 1 : 2;
        var panels = new[] { _left, _center, _right };
        var lists = panels.Select(p => p.Children.OfType<FrameworkElement>().Where(c => c != part).ToList()).ToArray();
        var before = lists[zone].FirstOrDefault(c => c.Visibility == Visibility.Visible && at.X < c.TransformToVisual(_root).TransformPoint(new Windows.Foundation.Point(c.ActualWidth / 2, 0)).X);
        int index = before != null ? lists[zone].IndexOf(before) : lists[zone].Count;
        lists[zone].Insert(index, part);
        string IdOf(FrameworkElement el) => _parts.First(x => x.El == el).Id;
        S.TopBarOrder = string.Join(";", lists.Select(l => string.Join("|", l.Select(IdOf))));
        if (part == _clock) S.TopBarClockPos = zone;
    }

    void RebuildTray()
    {
        var items = S.ShowTrayArea && ShellHost.Running ? ShellHost.Tray() : new List<TrayItem>();
        double room = Math.Max(18, S.TopBarHeight - 6);                 // as high as the other parts: a higher icon was cut
        string shown = string.Join("|", items.Select(i => i.Id + i.IsPinned + i.Title)) + S.TrayIconSize + "/" + room;
        if (shown == _trayShown) return;
        _trayShown = shown;
        _tray.Children.Clear();
        foreach (var item in items.Where(i => i.IsPinned)) _tray.Children.Add(TrayView.Icon(item, this, padding: 4, height: room));
        _overflow.Visibility = V(items.Any(i => !i.IsPinned) || TrayView.Dragging);   // with hidden icons, or as a place to drop one
        _trayPart.Visibility = V(items.Count > 0);
    }

    void UpdateMedia()
    {
        var (title, playing) = BarMedia.Now;
        bool show = S.TopBarMedia && !string.IsNullOrEmpty(title);
        _media.Visibility = V(show);
        if (!show) return;
        _mediaText.Text = title;
        _mediaPlay.Glyph = playing ? "" : "";                 // pause / play
    }

    /// <summary>The system monitor as one line (only when switched on).</summary>
    void UpdateMonitor()
    {
        bool any = S.MonitorCpu || S.MonitorRam || S.MonitorGpu || S.MonitorVram || S.MonitorNet;
        bool on = any && S.ShowSystemMonitor;
        _monitor.Visibility = V(on);
        if (!on) return;
        var (cpu, ram, gpu, down, up) = SystemMonitor.Sample(S.MonitorGpu || S.MonitorVram);
        var values = new List<string>();
        if (S.MonitorCpu) values.Add($"CPU {cpu:0} %");
        if (S.MonitorRam) values.Add($"RAM {ram:0} %");
        if (S.MonitorGpu) values.Add(double.IsNaN(gpu) ? "GPU –" : $"GPU {gpu:0} %");
        if (S.MonitorVram) values.Add(SystemMonitor.VramText());
        if (S.MonitorNet) { values.Add("↓ " + SystemMonitor.Speed(down)); values.Add("↑ " + SystemMonitor.Speed(up)); }
        _monitorText.Text = string.Join("   ", values);
    }

    void UpdateKeys()
    {
        var on = new List<string>();
        if (S.ShowCapsLock && (Native.GetKeyState(0x14) & 1) != 0) on.Add("CAPS");
        if (S.ShowNumLock && (Native.GetKeyState(0x90) & 1) != 0) on.Add("NUM");
        string text = string.Join(" ", on);
        if (_keys.Text != text) _keys.Text = text;
        _keysPart.Visibility = V(on.Count > 0);
    }

    void UpdateLanguage()
    {
        var layouts = InputLanguage.Layouts();
        _lang.Visibility = V(S.ShowInputLanguage && layouts.Length > 1);
        if (layouts.Length < 2) return;
        _langText.Text = InputLanguage.Short(InputLanguage.Current);
        ToolTipService.SetToolTip(_lang, InputLanguage.Name(InputLanguage.Current));
    }

    /// <summary>The keyboards to choose from; the window typed into keeps the focus.</summary>
    void LanguageMenu()
    {
        var target = Native.GetForegroundWindow();
        var current = InputLanguage.Current;
        var menu = NewMenu();
        foreach (var hkl in InputLanguage.Layouts())
        {
            var item = new ToggleMenuFlyoutItem { Text = $"{InputLanguage.Short(hkl)}   {InputLanguage.Name(hkl)}", IsChecked = hkl == current };
            item.Click += (_, _) => { InputLanguage.Switch(target, hkl); UpdateLanguage(); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(L.T("Language settings"), () => Fg.Open("ms-settings:regionlanguage")));
        menu.ShowAt(_lang, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
    }

    void UpdateDesktops()
    {
        _desks.Children.Clear();
        _desks.Visibility = V(S.ShowDesktops);
        if (!S.ShowDesktops) return;
        var all = VirtualDesktops.All();
        int current = VirtualDesktops.CurrentIndex(all);
        for (int i = 0; i < all.Count; i++)
        {
            int index = i;
            var b = BarButton(null, VirtualDesktops.Name(all, i));
            b.Content = new TextBlock { Text = (i + 1).ToString(), FontSize = 12, FontWeight = i == current ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal };
            if (i == current) b.Background = ThemeBrush.Get("SubtleFillColorSecondaryBrush");
            b.Click += (_, _) => VirtualDesktops.SwitchTo(index);
            b.PointerReleased += (_, e) => { if (e.GetCurrentPoint(b).Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased) VirtualDesktops.Close(index); };
            _desks.Children.Add(b);
        }
        var add = BarButton("", L.T("New desktop"), 11);
        add.Click += (_, _) => VirtualDesktops.New();
        _desks.Children.Add(add);
    }

    void UpdateClock()
    {
        var now = DateTime.Now;
        _time.Text = ClockText.Time(now);
        _date.Text = ClockText.Date(now, topBar: true);                  // on one line with the time, like on macOS
    }

    void UpdateBadge()
    {
        int n = NotificationBadge.Count;
        // its place stays while it is empty (invisible): the bar changed its parts' places whenever the last notification went
        _badge.Visibility = V(S.ShowNotificationCount);
        _badge.Opacity = n > 0 ? 1 : 0;
        _badgeText.Text = n > 99 ? "99+" : n > 0 ? n.ToString() : "0";
    }

    // ───────────────────────── the active app ─────────────────────────

    /// <summary>The app of the active window (only when the active window changes).</summary>
    void UpdateAppName()
    {
        if (!S.TopBarAppName) return;
        var fg = Native.GetForegroundWindow();
        if (fg == _appWindow) return;
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (fg == IntPtr.Zero || pid == Environment.ProcessId) return;   // Start, pop-ups, settings: the name of the app stays
        _appWindow = fg;
        var cls = new System.Text.StringBuilder(32);
        GetClassName(fg, cls, cls.Capacity);
        _appName.Text = cls.ToString() is "Progman" or "WorkerW" ? L.T("Desktop") : App.Dock?.AppNameOf(fg) ?? ProcessName(pid);
    }

    /// <summary>The description of a program ("Visual Studio Code"), else the name of its process.</summary>
    string ProcessName(uint pid)
    {
        if (_processNames.TryGetValue(pid, out var name)) return name;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
            try { if (p.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } d && !string.IsNullOrWhiteSpace(d)) name = d; }
            catch { /* elevated / protected process: only its name */ }
        }
        catch { name = ""; }
        if (_processNames.Count > 200) _processNames.Clear();
        return _processNames[pid] = name;
    }

    /// <summary>A double click on an empty place of the bar maximizes the active window or restores it (like its title bar).</summary>
    void Root_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource != _root && e.OriginalSource is not StackPanel) return;
        var w = _appWindow;
        if (w == IntPtr.Zero || !Native.IsWindowVisible(w) || _appName.Text == L.T("Desktop")) return;
        e.Handled = true;
        Win32.ShowWindow(w, IsZoomed(w) ? 9 : 3);                       // SW_RESTORE / SW_MAXIMIZE
    }

    void Root_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.Handled || e.OriginalSource != _root && e.OriginalSource is not StackPanel) return;
        e.Handled = true;
        var menu = NewMenu();
        if (S.DockEditMode)
        {
            if (S.TopBarOrder.Length > 0) menu.Items.Add(Item(L.T("Original order of the parts"), () => S.TopBarOrder = ""));
            menu.Items.Add(Item(L.T("Finish editing"), () => S.DockEditMode = false));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        menu.Items.Add(Item(L.T("Task Manager"), () => Fg.Open("taskmgr.exe"), "\uE9D9"));
        menu.Items.Add(Item(L.T("Settings"), () => SettingsWindow.Open("topbar"), "\uE713"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(L.T("Quit"), App.Quit, "\uE7E8"));
        menu.ShowAt(_root, new FlyoutShowOptions { Position = e.GetPosition(_root) });
    }

    // ───────────────────────── pop-ups ─────────────────────────

    public void ToggleControlCenter() { App.Anchor = this; PopupWindow.Toggle("control", () => new ControlCenterWindow()); }
    public void ToggleCalendar() { App.Anchor = this; NotificationBadge.MarkSeen(); PopupWindow.Toggle("calendar", () => new CalendarWindow()); }

    public (int X, int Y, int Edge)? AnchorFor(string kind)
    {
        FrameworkElement el = kind switch { "control" => _status.Button, "calendar" => _clock, _ => _system };
        if (el.Visibility != Visibility.Visible || el.ActualWidth <= 0) el = kind == "start" ? _left : _right;
        var r = ScreenRect(el);
        var pos = AppWindow.Position;
        return ((r.Left + r.Right) / 2, pos.Y + AppWindow.Size.Height, 3);
    }

    /// <summary>The clock for the notifications "at the clock" (below the bar).</summary>
    internal (Native.RECT Anchor, double GapDip, int Edge)? ClockAnchor(MonInfo mon) =>
        _clock.Visibility == Visibility.Visible && _shownNow ? (ScreenRect(_clock) with { Bottom = AppWindow.Position.Y + AppWindow.Size.Height }, 6, 3) : null;

    public Native.RECT ScreenRect(FrameworkElement el)
    {
        var p = el.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        double s = Scale;
        var pos = AppWindow.Position;
        return new Native.RECT
        {
            Left = pos.X + (int)Math.Round(p.X * s), Top = pos.Y + (int)Math.Round(p.Y * s),
            Right = pos.X + (int)Math.Round((p.X + el.ActualWidth) * s), Bottom = pos.Y + (int)Math.Round((p.Y + el.ActualHeight) * s)
        };
    }

    public IntPtr Hwnd => _hwnd;
    public MonInfo Monitor => _mon;
    public int PanelEdge => 3;
    public FlyoutPlacementMode MenuPlacement => FlyoutPlacementMode.Bottom;
    public void TrayChanged() { _trayShown = null; RebuildTray(); }

    public DropMarker Marker { get; private set; }

    public MenuFlyout NewMenu()
    {
        var menu = new MenuFlyout();
        Track(menu);
        return menu;
    }

    void Track(FlyoutBase flyout)
    {
        flyout.Opening += (_, _) => { flyout.AreOpenCloseAnimationsEnabled = Motion.On; Fg.Force(_hwnd); };   // without the foreground a menu of WinUI closes at once
        flyout.Opened += (_, _) => _menusOpen++;
        flyout.Closed += (_, _) => _menusOpen = Math.Max(0, _menusOpen - 1);
    }

    // ───────────────────────── place, auto-hide, full screen ─────────────────────────

    /// <summary>Along the whole top edge of the monitor of the dock, as high as the bar; the strip is reserved like for the dock.</summary>
    void Place()
    {
        _mon = Monitors.Dock();
        double s = Native.MonitorDpi(_mon.Handle) ?? Scale;
        int h = (int)Math.Ceiling(S.TopBarHeight * s);
        var b = _mon.Bounds;
        if (_appBar != null && !S.TopBarAutoHide) _appBar.Reserve(h, 3, b);
        else _appBar?.Dispose();
        int top = _appBar != null ? b.Top : _mon.Work.Top;              // floating: below what reserves the top
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(b.Left, top, b.Right - b.Left, h));
    }

    void FastTick()
    {
        UpdateAppName();
        UpdateKeys();
        if (!S.TopBarAutoHide) { if (_hidden) { _hidden = false; ApplyVisible(); } return; }
        if (!Native.GetCursorPos(out var c)) return;
        var m = _mon.Bounds;
        int bottom = AppWindow.Position.Y + AppWindow.Size.Height;
        bool busy = _menusOpen > 0 || PopupWindow.Current != null;
        if (_hidden) { if (c.Y <= m.Top + 1 && c.X >= m.Left && c.X < m.Right || busy) { _hidden = false; _idleMs = 0; ApplyVisible(); } return; }
        bool inBar = c.X >= m.Left && c.X < m.Right && c.Y >= m.Top && c.Y < bottom + 4;
        if (inBar || busy) { _idleMs = 0; return; }
        if ((_idleMs += 150) < 600) return;
        _hidden = true;
        ApplyVisible();
    }

    void SlowTick()
    {
        UpdateClock();
        UpdateMonitor();
        if (++_tick % 5 == 0) { _status.Update(); UpdateLanguage(); }
        if (_tick == 1) Place();
        // a full-screen app on this monitor: the bar steps away
        bool full = false;
        var fg = Native.GetForegroundWindow();
        if (PopupWindow.Current == null && _menusOpen == 0 && fg != IntPtr.Zero && Native.MonitorFromWindow(fg, 2) == _mon.Handle)
        {
            Native.GetWindowThreadProcessId(fg, out uint pid);
            var cls = new System.Text.StringBuilder(32);
            GetClassName(fg, cls, cls.Capacity);
            if (pid != Environment.ProcessId && cls.ToString() is not ("Progman" or "WorkerW"))
            {
                if (SHQueryUserNotificationState(out int state) == 0 && state is 3 or 4) full = true;
                else if (Native.GetWindowRect(fg, out var r))
                {
                    var m = _mon.Bounds;
                    full = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom && (Native.GetWindowLong(fg, -16) & 0x00C00000) != 0x00C00000;
                }
            }
        }
        if (full != _fullScreen) { _fullScreen = full; ApplyVisible(); }
    }

    void ApplyVisible()
    {
        bool show = !_hidden && !_fullScreen;
        if (show == _shownNow) return;
        _shownNow = show;
        if (show) { AppWindow.Show(false); Place(); } else AppWindow.Hide();
    }
}
