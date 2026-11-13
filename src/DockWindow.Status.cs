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

/// <summary>The notification area, the status icons, the keyboard language, drives, desktops and the clock.</summary>
sealed partial class DockWindow
{
    // ───────────────────────── notification area ─────────────────────────

    void RebuildTray()
    {
        bool show = S.ShowTrayArea && ShellHost.Running && RightSide;
        var items = show ? ShellHost.Tray() : new List<TrayItem>();
        // the arrow only with hidden icons in it – and while an icon is dragged, so it can be dropped there to hide it
        bool hidden = items.Any(i => !i.IsPinned), forDrag = !hidden && show && TrayView.Dragging;
        OverflowButton.Visibility = V(hidden || forDrag);
        OverflowButton.Margin = new Thickness(0);
        if (forDrag && OverflowButton.Parent is StackPanel parent && PreviousShown(parent, OverflowButton) == Divider)
        {
            // only for the drag: in the empty room after the line, over it – the dock got wider while dragging and narrower
            // again after it
            OverflowButton.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double along = (Vertical ? OverflowButton.DesiredSize.Height : OverflowButton.DesiredSize.Width) + parent.Spacing;
            OverflowButton.Margin = Vertical ? new Thickness(0, -along, 0, 0) : new Thickness(-along, 0, 0, 0);
        }
        string shown = string.Join("|", items.Select(i => i.Id + i.IsPinned + i.Title)) + S.TrayIconSize + Edge;
        if (shown == _trayShown) return;
        _trayShown = shown;
        Tray.Children.Clear();
        foreach (var item in items.Where(i => i.IsPinned)) Tray.Children.Add(TrayView.Icon(item, this));
        // a chevron drawn with a thicker line than the glyph of the font, pointing where the hidden icons open
        string data = Edge switch { 1 => "M0,0 L4.5,4.5 L0,9", 2 => "M4.5,0 L0,4.5 L4.5,9", 3 => "M0,0 L4.5,4.5 L9,0", _ => "M0,4.5 L4.5,0 L9,4.5" };
        OverflowGlyph.Data = (Microsoft.UI.Xaml.Media.Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Microsoft.UI.Xaml.Media.Geometry), data);
    }

    static UIElement PreviousShown(Panel parent, UIElement el)
    {
        for (int i = parent.Children.IndexOf(el) - 1; i >= 0; i--)
            if (parent.Children[i].Visibility == Visibility.Visible) return parent.Children[i];
        return null;
    }

    void Overflow_Click(object sender, RoutedEventArgs e) => TrayView.ShowOverflow(OverflowButton, this);

    // the parts shared with the top bar
    IntPtr IPanelWindow.Hwnd => _hwnd;
    int IPanelWindow.PanelEdge => Edge;
    MenuFlyout IPanelWindow.NewMenu() => Menu();
    MonInfo IPanelWindow.Monitor => _mon;
    Native.RECT IPanelWindow.ScreenRect(FrameworkElement el) => ScreenRect(el);
    FlyoutPlacementMode IPanelWindow.MenuPlacement => MenuPlacement;
    void IPanelWindow.TrayChanged() { _trayShown = null; Later(); }

    /// <summary>Where an element is on the screen (pixels).</summary>
    internal Native.RECT ScreenRect(FrameworkElement el)
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

    /// <summary>An element along the dock, across it the dock itself (pop-ups keep the same distance from every icon).</summary>
    Native.RECT AnchorRect(FrameworkElement el)
    {
        var r = ScreenRect(el);
        if (Vertical) { r.Left = _body.Left; r.Right = _body.Right; }
        else { r.Top = _body.Top; r.Bottom = _body.Bottom; }
        return r;
    }

    bool Over(FrameworkElement el)
    {
        if (!Native.GetCursorPos(out var c) || el.ActualWidth <= 0) return false;
        try { return Fg.Contains(ScreenRect(el), c.X, c.Y); } catch { return false; }
    }

    // ───────────────────────── status icons and clock ─────────────────────────

    /// <summary>Names of the connected Bluetooth devices (also for the control center).</summary>
    internal static List<string> ConnectedBluetooth => BluetoothWatch.Connected;

    readonly StatusParts _status;

    /// <summary>Caps Lock and Num Lock while they are on, and the system monitor: every second while shown.</summary>
    void UpdateEverySecond()
    {
        if (!RightSide) { LocksText.Visibility = MonitorText.Visibility = Visibility.Collapsed; return; }
        var locks = new List<string>();
        if (S.ShowCapsLock && (Native.GetKeyState(0x14) & 1) != 0) locks.Add("CAPS");
        if (S.ShowNumLock && (Native.GetKeyState(0x90) & 1) != 0) locks.Add("NUM");
        LocksText.Text = string.Join(Vertical ? "\n" : " ", locks);
        LocksText.Visibility = V(locks.Count > 0);
        if (!S.ShowSystemMonitor) { MonitorText.Visibility = Visibility.Collapsed; return; }
        var m = SystemMonitor.Sample(S.MonitorGpu);
        var parts = new List<string>();
        if (S.MonitorCpu) parts.Add($"CPU {m.Cpu,3:0}%");
        if (S.MonitorRam) parts.Add($"RAM {m.Ram,3:0}%");
        if (S.MonitorGpu) parts.Add($"GPU {m.Gpu,3:0}%");
        if (S.MonitorVram) parts.Add(SystemMonitor.VramText());       // "VRAM 10 %": the name is in it already
        if (S.MonitorNet) parts.Add($"↓{SystemMonitor.Speed(m.Down)} ↑{SystemMonitor.Speed(m.Up)}");
        MonitorText.Text = string.Join(Vertical ? "\n" : "  ", parts);
        MonitorText.Visibility = Visibility.Visible;
    }

    void UpdateDesktops()
    {
        Desktops.Children.Clear();
        if (!S.ShowDesktops || !RightSide) { Desktops.Visibility = Visibility.Collapsed; return; }
        var all = VirtualDesktops.All();
        int current = VirtualDesktops.CurrentIndex(all);
        for (int i = 0; i < all.Count; i++)
        {
            int index = i;
            var b = new Button { Style = (Style)Application.Current.Resources["DockButton"], Padding = new Thickness(8, 4, 8, 4), Content = new TextBlock { Text = (i + 1).ToString(), FontSize = 12, FontWeight = i == current ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal } };
            if (i == current) b.Background = ThemeBrush.Get("SubtleFillColorSecondaryBrush");
            ToolTipService.SetToolTip(b, VirtualDesktops.Name(all, i));
            b.Click += (_, _) => VirtualDesktops.SwitchTo(index);
            b.PointerReleased += (_, e) => { if (e.GetCurrentPoint(b).Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased) VirtualDesktops.Close(index); };
            Desktops.Children.Add(b);
        }
        var add = new Button { Style = (Style)Application.Current.Resources["DockButton"], Padding = new Thickness(6, 4, 6, 4), Content = new FontIcon { Glyph = "", FontSize = 11 } };
        ToolTipService.SetToolTip(add, L.T("New desktop"));
        add.Click += (_, _) => VirtualDesktops.New();
        Desktops.Children.Add(add);
        Desktops.Visibility = Visibility.Visible;
    }

    /// <summary>The keyboards to choose from; the window typed into keeps the focus.</summary>
    void Lang_Click(object sender, RoutedEventArgs e)
    {
        var target = Native.GetForegroundWindow();
        var current = InputLanguage.Current;
        var menu = Menu();
        foreach (var hkl in InputLanguage.Layouts())
        {
            var item = new ToggleMenuFlyoutItem { Text = $"{InputLanguage.Short(hkl)}   {InputLanguage.Name(hkl)}", IsChecked = hkl == current };
            item.Click += (_, _) => { InputLanguage.Switch(target, hkl); UpdateStatus(); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(L.T("Language settings"), () => Fg.Open("ms-settings:regionlanguage")));
        menu.ShowAt(LangButton, new FlyoutShowOptions { Placement = MenuPlacement });
    }

    void Usb_Click(object sender, RoutedEventArgs e) => TrayView.ShowDrives(UsbButton, this, UpdateStatus);

    /// <summary>The status icons: read in the background (see <see cref="StatusData"/>), only shown here.</summary>
    async void UpdateStatus()
    {
        if (_closed) return;
        bool here = RightSide;
        StatusButton.Visibility = V(here);
        if (!here) { LangButton.Visibility = UsbButton.Visibility = Visibility.Collapsed; return; }
        if (StatusData.Last is { } last) ShowStatus(last);           // at once with what is known, then the fresh state
        var now = await StatusData.ReadAsync();
        if (!_closed && now != null) ShowStatus(now);
    }

    /// <summary>The keyboard language and removable drives here; the status icons themselves by StatusParts.</summary>
    void ShowStatus(StatusSnapshot d)
    {
        var layouts = d.Layouts;
        LangButton.Visibility = V(S.ShowInputLanguage && layouts.Length > 1);
        if (layouts.Length > 1) { LangText.Text = InputLanguage.Short(d.Layout); ToolTipService.SetToolTip(LangButton, InputLanguage.Name(d.Layout)); }
        UsbButton.Visibility = V(S.ShowRemovable && d.Removable > 0);
        _status.Show(d);
    }

    void UpdateClock()
    {
        ClockButton.Visibility = V(S.ShowClock && (RightSide || !_main && !S.TopBarOn));
        if (ClockButton.Visibility != Visibility.Visible) return;
        var now = DateTime.Now;
        TimeText.Text = ClockText.Time(now);
        DateText.Visibility = V(S.ShowDate);
        DateText.Text = ClockText.Date(now);
    }

    void UpdateBadge()
    {
        int n = NotificationBadge.Count;
        // its place stays while it is empty (invisible): the dock changed its length whenever the last notification went
        Badge.Visibility = V(S.ShowNotificationCount);
        Badge.Opacity = n > 0 ? 1 : 0;
        BadgeText.Text = n > 99 ? "99+" : n > 0 ? n.ToString() : "0";
    }

    /// <summary>The Recycle Bin: its icon empty / full and the number of items in the tooltip.</summary>
    void UpdateBin()
    {
        if (BinButton.Visibility != Visibility.Visible) return;
        long n = RecycleBin.Count();
        if (n == _binCount) return;
        _binCount = n;
        BinImage.Source = Pictures.ForSize(RecycleBin.Icon(), SysIconPx);
        ToolTipService.SetToolTip(BinButton, n > 0 ? $"{L.T("Recycle Bin")} ({n})" : L.T("Recycle Bin"));
    }

    /// <summary>The clock on this monitor for the notifications "at the clock".</summary>
    internal (Native.RECT Anchor, double GapDip, int Edge)? ClockAnchor(MonInfo mon) =>
        ClockButton.Visibility == Visibility.Visible && _visible ? (AnchorRect(ClockButton), 10 + Overflow * 0.3, Edge) : null;
}
