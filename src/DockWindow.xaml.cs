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

/// <summary>
/// The dock: Start, the pinned and running apps, the Recycle Bin, the notification area, the status icons and the clock.
/// A borderless window with the acrylic of Windows along any edge of a monitor (one on each monitor when chosen). The
/// windows of apps come from ManagedShell and are followed by its events, without a timer.
/// The icons grow under the mouse (the window grows inwards for them while the mouse is over it), pinned icons are moved
/// by dragging, files dropped on the dock are pinned, on an app opened with it, on the Recycle Bin deleted.
/// </summary>
public sealed partial class DockWindow : Window, IPanelWindow
{
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);

    readonly IntPtr _hwnd;
    // the windows changed: built again a moment later (many changes come at once); it was 200 ms, the icons followed late
    readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(80) };
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    readonly DispatcherTimer _previewHide = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly AppBar _appBar;
    readonly bool _measure, _main;
    readonly HashSet<string> _launching = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _flashing = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Action> _unsubscribe = new();
    MonInfo _mon;
    List<PinnedApp> _pins = new();
    List<DockEntry> _entries = new();
    string _shown, _trayShown, _layoutShown;
    int _tick, _menusOpen, _bouncing, _awayMs;
    long _binCount = -2;
    bool _expanded, _autoHidden, _fullScreen, _visible = true, _closed;
    Native.RECT _body;                                                // the dock itself on the screen (without the room for magnified icons)
    DockEntry _previewFor;
    Button _previewButton;
    static PinnedApp _dragPin;                                       // a pin being dragged (between the docks too)
    TimeSpan _cpu;
    DateTime _cpuAt;

    DockSettings S => DockSettings.Current;
    int Edge => S.DockPosition;
    bool Vertical => Edge is 1 or 2;
    /// <summary>The notification area, status icons and clock are in this dock (not in the top bar, not a dock of another monitor).</summary>
    bool RightSide => !S.TopBarOn && HasStatus;

    static List<MonInfo> _monitors;
    static long _monitorsAt = long.MinValue / 2;

    /// <summary>The setting "Status icons and clock" with a dock on each monitor (the main dock otherwise).</summary>
    bool HasStatus
    {
        get
        {
            if (S.StatusMonitor == 0) return _main;
            if (S.StatusMonitor == 1) return true;
            if (Environment.TickCount64 - _monitorsAt > 2000) { _monitors = Monitors.All(); _monitorsAt = Environment.TickCount64; }   // asked often
            var all = _monitors;
            int i = S.StatusMonitor - 2;
            return i < all.Count ? all[i].Device == _mon.Device : _main;    // that monitor is not connected: the main dock
        }
    }
    bool SysOnBar => S.TopBarOn && S.SysIconsOnBar;

    internal MonInfo Monitor => _mon;

    internal DockWindow(MonInfo monitor, bool main, bool measure)
    {
        InitializeComponent();
        _status = new StatusParts(StatusButton);                        // the status icons, the same as on the top bar
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _mon = monitor;
        _main = main;
        _measure = measure;
        SystemBackdrop = new AlwaysActiveAcrylic();
        ExtendsContentIntoTitleBar = false;                         // true draws a dark line along the top edge
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        NoFrame.HideFromSwitchers(AppWindow, _hwnd);                         // not in Alt+Tab
        NoFrame.Apply(_hwnd);
        int exclude = 1;
        Win32.DwmSetWindowAttribute(_hwnd, 12, ref exclude, 4);       // stays while Aero Peek shows one window

        if (ShellHost.Running) _appBar = new AppBar(_hwnd); // as the taskbar it keeps maximized windows out of its place
        _refresh.Tick += (_, _) => { _refresh.Stop(); Rebuild(); };
        _clock.Tick += (_, _) => Tick();
        _clock.Start();
        _hideTimer.Tick += (_, _) => AutoHideTick();
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); ShowPreview(); };
        _previewHide.Tick += (_, _) =>
        {
            if (PreviewWindow.Hovered || _previewButton != null && Over(_previewButton)) return;
            _previewHide.Stop();
            PreviewWindow.HideNow();
        };

        // windows from ManagedShell; without it (it could not start) from window events of Windows
        if (ShellHost.Running)
        {
            Action trayChanged = () => { _trayShown = null; Later(); };
            ShellHost.WindowsChanged += Later; _unsubscribe.Add(() => ShellHost.WindowsChanged -= Later);
            ShellHost.TrayChanged += trayChanged; _unsubscribe.Add(() => ShellHost.TrayChanged -= trayChanged);
        }
        else { WindowEvents.Changed += Later; _unsubscribe.Add(() => WindowEvents.Changed -= Later); }
        DockState.Changed += Later; _unsubscribe.Add(() => DockState.Changed -= Later);
        UnreadMail.Changed += Later; _unsubscribe.Add(() => UnreadMail.Changed -= Later);   // the envelope on Outlook at once
        Action status = () => Ui.Post(UpdateStatus), badge = () => Ui.Post(UpdateBadge), desktops = () => Ui.Post(() => { UpdateDesktops(); Later(); });
        Weather.Changed += status; _unsubscribe.Add(() => Weather.Changed -= status);
        NotificationBadge.Changed += badge; _unsubscribe.Add(() => NotificationBadge.Changed -= badge);
        NotificationBadge.Watch(true);
        System.Net.NetworkInformation.NetworkAddressChangedEventHandler net = (_, _) => Ui.Post(UpdateStatus);
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += net;
        _unsubscribe.Add(() => System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= net);
        System.ComponentModel.PropertyChangedEventHandler settings = (_, _) => { _shown = null; _trayShown = null; _layoutShown = null; Later(); UpdateStatus(); UpdateClock(); UpdateDesktops(); };
        S.PropertyChanged += settings; _unsubscribe.Add(() => S.PropertyChanged -= settings);
        VirtualDesktops.Changed += desktops; _unsubscribe.Add(() => VirtualDesktops.Changed -= desktops);
        VirtualDesktops.Watch();
        Closed += (_, _) =>
        {
            _closed = true;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnMoveFrame;
            foreach (var off in _unsubscribe) off();
            _clock.Stop(); _hideTimer.Stop(); _refresh.Stop();
            _appBar?.Dispose();
            if (_main) WindowEvents.Stop();
        };

        Root.PointerMoved += Root_PointerMoved;
        Root.PointerExited += (_, _) => ResetMagnification();
        Root.RightTapped += Root_RightTapped;
        HookEdgeDrag();
        Marker = new DropMarker(Root);                                 // where a dragged icon will go
        TrayView.AcceptDrops(Tray, OverflowButton, this);              // hidden icons dragged into the notification area and back
        TrayView.DraggingChanged += () => { if (!_closed) RebuildTray(); };   // the arrow shows up while an icon is dragged
        // a part that changes its size by itself (the count of notifications at the clock, status icons, the language): the
        // dock is placed again – its content was wider than the window and the Start icon stuck out of it
        foreach (var (_, part) in Parts)
            part.SizeChanged += (_, e) =>
            {
                if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 0.5 && Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 0.5) return;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Place);
            };
        StatusButton.PointerWheelChanged += (_, e) => { e.Handled = true; StatusParts.Wheel(e, StatusButton); UpdateStatus(); };   // the wheel sets the volume
        StatusButton.RightTapped += (_, e) => { e.Handled = true; ToggleControlCenter(); };   // the right button opens the control center too (not the menu of the dock)
        DesktopButton.PointerEntered += (_, _) => DesktopButton.RenderTransform = new ScaleTransform { ScaleX = 1.25, ScaleY = 1.25, CenterX = DesktopButton.ActualWidth / 2, CenterY = DesktopButton.ActualHeight / 2 };
        DesktopButton.PointerExited += (_, _) => DesktopButton.RenderTransform = null;

        Diag.Mark("dock: window and events");
        UpdateDesktops();
        BluetoothWatch.Start();
        ThemeWatch.Track(this);
        UpdateClock();
        UpdateStatus();
        Diag.Mark("dock: clock, status");
        Rebuild();
        Diag.Mark("dock: apps");
        if (main)
        {
            EventHandler<object> drawn = null;
            drawn = (_, _) => { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= drawn; Diag.Mark("dock drawn"); App.DockDrawn(); };
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += drawn;
        }
        if (measure) { _cpu = Process.GetCurrentProcess().TotalProcessorTime; _cpuAt = DateTime.Now; Log.Note("Measure", "start"); }
    }

    double Scale => GetDpiForWindow(_hwnd) / 96.0;

    void Later()
    {
        if (_closed) return;
        _refresh.Stop();
        // the size dragged with the mouse: the dock follows in the same frame (waiting for a pause of the mouse made it jump)
        if (_sizing) { Rebuild(); return; }
        _refresh.Start();
    }

    /// <summary>A window shown in the dock no longer exists or was hidden (its running mark stayed for a long time).</summary>
    bool WindowGone()
    {
        // the open windows changed without a word of Windows (a window of File Explorer hidden or shown again by Explorer)
        int version = ShellHost.Running ? ShellHost.OpenVersion : 0;
        bool changed = version != _openSeen;
        _openSeen = version;
        return changed || _entries?.Any(e => e.Windows.Any(w => !Win32.IsWindow(w.Handle) || !Win32.IsWindowVisible(w.Handle) || Win32.IsCloakedByApp(w.Handle))) == true;
    }
    int _openSeen;

    void Tick()
    {
        UpdateClock();
        UpdateEverySecond();
        UpdateFullScreen();
        if (WindowGone()) Later();                                    // a window closed or hidden without a word from Windows: built again now
        if (_tick == 0) Place();                                      // once shown: placed again with its real size
        if (++_tick % 5 == 0) { UpdateStatus(); UpdateBin(); }
        if (_measure) Measure();
    }

    // ───────────────────────── layout ─────────────────────────

    double IconPx => Math.Round(Math.Clamp(S.IconSize, 24, 96) * S.IconScale / 100);
    double SysIconPx => S.DrawnSysIconSize;
    /// <summary>Room of one icon across the dock (the icon, the running indicator and a little air).</summary>
    double Slot => Math.Max(S.IconSize, IconPx) + 10;
    /// <summary>
    /// How far magnified icons reach out of the dock (DIP): never – the window of WinUI cannot be transparent around the dock,
    /// so the icons grow only into the room of their place (the window keeps its size).
    /// </summary>
    double Overflow => 0;

    /// <summary>The largest magnification that still fits into the place of an icon.</summary>
    double FitMagnification => Math.Max(1, Math.Min(S.EffectiveMagnification, (Slot - 4) / IconPx));

    /// <summary>The picture of a button (the icon of an app, the Start logo) – what grows and bounces, not its indicator.</summary>
    static FrameworkElement PictureOf(Button b) => b.Content is Grid { Children.Count: > 0 } slot && slot.Children[0] is FrameworkElement icon ? icon : b.Content as FrameworkElement;

    /// <summary>One transform for growing and bouncing, around the middle of the picture.</summary>
    static CompositeTransform TransformOf(FrameworkElement el)
    {
        if (el.RenderTransform is CompositeTransform t) return t;
        el.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        return (CompositeTransform)(el.RenderTransform = new CompositeTransform());
    }

    /// <summary>Directions, sizes and the parts shown – all from the settings.</summary>
    void ApplyLayout()
    {
        string key = $"{Edge}{S.DockFull}{S.IconSize}{S.IconScale}{S.SysIconScale}{S.IconSpacing}{S.EndPadding}{S.Rounding}{S.DockOutline}{S.TopBarOn}{S.SysIconsOnBar}{S.StartIconLook}{S.StartButtonImage}{S.AccentMode}"
            + $"{S.ShowStartButton}{S.ShowSearchButtons}{S.ShowTaskViewButton}{S.ShowRecycleBin}{S.ShowDownloads}{S.ShowDesktopButton}{S.StatusIconSize}{S.ClockFontSize}{S.DateFontSize}{S.HoverEffect}{S.DockOrder}{S.DockEditMode}{S.ShowSeparators}{S.TopBarSystemMenu}{S.TopBarSystemStart}";
        if (key == _layoutShown) return;
        _layoutShown = key;
        ApplyOrder();
        var o = Vertical ? Orientation.Vertical : Orientation.Horizontal;
        Row.Orientation = RowRight.Orientation = Apps.Orientation = Desktops.Orientation = Tray.Orientation = o;
        _status.Orientation = o;
        Apps.Spacing = S.IconSpacing;
        foreach (var (_, part) in Parts) part.HorizontalAlignment = HorizontalAlignment.Center;   // in the middle of a side dock
        ShowGrip();                                                     // edit mode: the parts are moved with the mouse (PressPart), the size by the grip
        // the lines: after Start 7 px on each side, before the notification area a gap of 28 px on each
        // side that stays also when the lines are switched off (only the line goes)
        foreach (var (line, gap) in new[] { (StartDivider, 7.0), (Divider, 28.0) })
        {
            if (Vertical) { line.Width = Slot * 0.8; line.Height = 1; line.Margin = new Thickness(0, gap, 0, gap); }
            else { line.Width = 1; line.Height = 36; line.Margin = new Thickness(gap, 0, gap, 0); }
            line.VerticalAlignment = VerticalAlignment.Center;
            line.Opacity = S.ShowSeparators ? 1 : 0;
        }
        double pad = S.EndPadding;
        Root.Padding = Vertical ? new Thickness(3, pad, 3, pad) : new Thickness(pad, 3, pad, 3);
        // the dock keeps to its edge, the room for magnified icons is on the inner side
        // in the middle of the dock across: the icons never reach out of it (no room kept on the inner side any more), and the
        // few pixels of spare room went to one side only – the icons were further from the top than from the bottom
        Row.VerticalAlignment = RowRight.VerticalAlignment = VerticalAlignment.Center;
        Row.HorizontalAlignment = Vertical ? HorizontalAlignment.Center : S.DockFull == 1 ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        // a dock as long as its content: the content from its start (half of the spare room before it), not centred – while
        // the dock grows or shrinks, the centring put the icons half a pixel this way and that in every frame (they shook)
        bool hugs = Vertical || S.DockFull == 0;
        if (hugs && Vertical) Row.VerticalAlignment = VerticalAlignment.Top;
        else if (hugs) Row.HorizontalAlignment = HorizontalAlignment.Left;
        // (the room of 3 px Place adds: half before the content; the other margin takes it back so the length stays the same)
        Row.Margin = !hugs ? new Thickness(0) : Vertical ? new Thickness(0, 1.5, 0, -1.5) : new Thickness(1.5, 0, -1.5, 0);
        ClockStack.HorizontalAlignment = Vertical ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        TimeText.HorizontalAlignment = DateText.HorizontalAlignment = Vertical ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        TimeText.FontSize = S.ClockFontSize;
        DateText.FontSize = S.DateFontSize;
        UsbGlyph.FontSize = S.StatusIconSize;                           // the status icons size themselves (StatusParts)
        LangText.FontSize = Math.Max(11, S.StatusIconSize - 3);
        if (StatusData.Last is { } shown) _status.Show(shown);

        // corners: Windows rounds the acrylic itself (none, small, normal)
        int corner = S.DockFull != 0 && !Vertical || S.Rounding < 2 ? Native.DWMWCP_DONOTROUND : S.Rounding < 6 ? 3 : Native.DWMWCP_ROUND;
        Native.SetDwmAttr(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, corner);
        Native.SetDwmAttr(_hwnd, Native.DWMWA_BORDER_COLOR, S.DockOutline ? unchecked((int)0xFFFFFFFF) : unchecked((int)0xFFFFFFFE));   // default line / none

        // the system menu of the top bar instead of Start: no Start button in the dock
        StartButton.Visibility = V(S.ShowStartButton && !(S.TopBarOn && S.TopBarSystemMenu && S.TopBarSystemStart));
        StartButton.Content = OwnPicture.StartIcon(S.StartIconLook, S.StartButtonImage, SysIconPx);
        SearchButton.Visibility = V(S.ShowSearchButtons && !SysOnBar);
        TaskViewButton.Visibility = V(S.ShowTaskViewButton && !SysOnBar);
        DownloadsButton.Visibility = V(S.ShowDownloads && !SysOnBar);
        BinButton.Visibility = V(S.ShowRecycleBin && !SysOnBar);
        SearchGlyph.FontSize = TaskViewGlyph.FontSize = DownloadsGlyph.FontSize = Math.Round(SysIconPx * 0.6);
        BinImage.Width = BinImage.Height = SysIconPx;
        foreach (var b in new[] { StartButton, SearchButton, TaskViewButton, BinButton, DownloadsButton }) { b.Width = b.Height = Slot; b.Padding = new Thickness(0); Hover(b); }
        // the gap after the Start buttons only exists together with its line; the gap before the notification area always stays
        StartDivider.Visibility = V((S.ShowStartButton || SearchButton.Visibility == Visibility.Visible || TaskViewButton.Visibility == Visibility.Visible) && S.ShowSeparators);
        Divider.Visibility = V(RightSide && S.DockFull == 0);           // full width: the free space itself separates them
        DesktopButton.Visibility = V(RightSide && S.ShowDesktopButton);
        if (Vertical) { DesktopButton.Padding = new Thickness(10, 3, 10, 3); ((Border)DesktopButton.Content).Width = 18; ((Border)DesktopButton.Content).Height = 2; }
        else { DesktopButton.Padding = new Thickness(3, 10, 3, 10); ((Border)DesktopButton.Content).Width = 2; ((Border)DesktopButton.Content).Height = 18; }
        _binCount = -2;
        UpdateBin();
    }

    /// <summary>Only the magnification under the mouse: the button keeps no highlight of its own.</summary>
    void Hover(Button b)
    {
        if (S.HoverEffect == 0 && !S.HighlightOnly)
        {
            b.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            b.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        else { b.Resources.Remove("ButtonBackgroundPointerOver"); b.Resources.Remove("ButtonBackgroundPressed"); }
    }

    static Visibility V(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
}
