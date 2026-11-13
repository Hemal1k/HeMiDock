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

/// <summary>The pop-ups opened from the dock and where they open.</summary>
sealed partial class DockWindow
{
    // ───────────────────────── pop-ups ─────────────────────────

    /// <summary>
    /// Where a pop-up opens: the point on the inner side of the dock in the middle of its button (pixels) and the edge of
    /// the dock (0 bottom, 1 left, 2 right, 3 top).
    /// </summary>
    public (int X, int Y, int Edge)? AnchorFor(string kind)
    {
        FrameworkElement el = kind switch { "control" => StatusButton, "calendar" => ClockButton, _ => StartButton };
        if (el.Visibility != Visibility.Visible || el.ActualWidth <= 0) el = Row;
        var r = AnchorRect(el);
        int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
        return Edge switch
        {
            1 => (_body.Right, cy, 1),
            2 => (_body.Left, cy, 2),
            3 => (cx, _body.Bottom, 3),
            _ => (cx, _body.Top, 0)
        };
    }

    public void ToggleStart(bool search)
    {
        if (search && PopupWindow.Current is StartWindow open) { open.FocusSearch(); return; }   // Win+S in an open Start: to its search
        App.Anchor = this;
        PopupWindow.Toggle("start", () => new StartWindow());          // its search box has the keyboard at every opening
    }
    // with the top bar on, its status icons and clock are there: the keys open them by it
    public void ToggleControlCenter() { if (!RightSide && App.TopBar is { } bar) { bar.ToggleControlCenter(); return; } App.Anchor = this; PopupWindow.Toggle("control", () => new ControlCenterWindow()); }
    public void ToggleCalendar() { if (!RightSide && App.TopBar is { } bar) { bar.ToggleCalendar(); return; } App.Anchor = this; NotificationBadge.MarkSeen(); PopupWindow.Toggle("calendar", () => new CalendarWindow()); }
    public void ShowSettings() => SettingsWindow.Open();

    void Start_Click(object sender, RoutedEventArgs e) => ToggleStart(false);
    void Search_Click(object sender, RoutedEventArgs e) => ToggleStart(true);
    void TaskView_Click(object sender, RoutedEventArgs e) => AppShell.Tap(0x5B, 0x09);   // Win+Tab
    void Desktop_Click(object sender, RoutedEventArgs e) => AppShell.Tap(0x5B, 0x44);    // Win+D
    void Status_Click(object sender, RoutedEventArgs e) => ToggleControlCenter();
    void Clock_Click(object sender, RoutedEventArgs e) => ToggleCalendar();
    void Bin_Click(object sender, RoutedEventArgs e) => RecycleBin.Open();
    void Downloads_Click(object sender, RoutedEventArgs e) => Fg.Open(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
    void Start_RightTapped(object sender, RightTappedRoutedEventArgs e) { e.Handled = true; ShowPowerMenu(aboveStart: false); }

    void Clock_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = Menu();
        menu.Items.Add(Item(L.T("Date and time settings"), () => Fg.Open("ms-settings:dateandtime")));
        menu.Items.Add(Item(L.T("Notifications"), () => Fg.Open("ms-settings:notifications")));
        menu.ShowAt(ClockButton, new FlyoutShowOptions { Placement = MenuPlacement });
    }

    void Bin_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = Menu();
        var open = Item(L.T("Open"), RecycleBin.Open);
        open.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        menu.Items.Add(open);
        var empty = Item(L.T("Empty Recycle Bin"), () => { RecycleBin.Empty(_hwnd); _binCount = -2; UpdateBin(); });
        empty.IsEnabled = RecycleBin.Count() != 0;
        menu.Items.Add(empty);
        menu.ShowAt(BinButton, new FlyoutShowOptions { Placement = MenuPlacement });
    }

    /// <summary>A right click on the dock itself: edit mode (spaces, lines), Task Manager, settings, quit.</summary>
    void Root_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.Handled || e.OriginalSource is not (Grid or StackPanel) || e.OriginalSource is FrameworkElement { Parent: Button }) return;
        e.Handled = true;
        var menu = Menu();
        if (S.DockEditMode)
        {
            // after the pinned apps – then dragged where they belong
            menu.Items.Add(Item(L.T("Add space"), () => EditPins(p => p.Add(new PinnedApp { Spacer = true }))));
            menu.Items.Add(Item(L.T("Add separator"), () => EditPins(p => p.Add(new PinnedApp { Spacer = true, Line = true }))));
            if (S.DockOrder.Length > 0) menu.Items.Add(Item(L.T("Original order of the parts"), () => S.DockOrder = ""));
            menu.Items.Add(Item(L.T("Finish editing"), () => S.DockEditMode = false));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        menu.Items.Add(Item(L.T("Task Manager"), () => Fg.Open("taskmgr.exe"), ""));
        menu.Items.Add(Item(L.T("Settings"), ShowSettings, ""));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(L.T("Quit"), App.Quit, ""));                  // closes the dock and brings back the Windows taskbar
        menu.ShowAt(Root, new FlyoutShowOptions { Position = e.GetPosition(Root) });
    }

    /// <summary>
    /// Own power user menu (like Win+X on the Start button of Windows), the items switched on in Settings → Start menu →
    /// Start button menu. From the keyboard (Win+X) it opens above the Start button, from a right click at the mouse.
    /// </summary>
    public void ShowPowerMenu(bool aboveStart = true)
    {
        var groups = PowerMenu.Shown();
        var power = PowerMenu.Power();
        if (groups.Count == 0 && power.Count == 0) return;             // every item switched off in the settings
        var menu = Menu();
        var last = groups.Where(g => g.Any(e => e.Id == "Desktop")).ToList();   // Desktop comes after the power submenu
        foreach (var group in groups.Except(last))
        {
            if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
            foreach (var e in group) menu.Items.Add(Item(L.T(e.Id), e.Run));
        }
        // like Windows: the power items in one submenu "Shut down or sign out", then Desktop
        if (power.Count > 0)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
            var sub = new MenuFlyoutSubItem { Text = L.T("Shut down or sign out") };
            foreach (var e in power) sub.Items.Add(Item(L.T(e.Id), e.Run));
            menu.Items.Add(sub);
        }
        foreach (var e in last.SelectMany(g => g)) menu.Items.Add(Item(L.T(e.Id), e.Run));
        if (aboveStart && StartButton.Visibility == Visibility.Visible) menu.ShowAt(StartButton, new FlyoutShowOptions { Placement = MenuPlacement });
        else ShowMenuAt(menu);
    }

    /// <summary>A menu of the logic (workspaces …) at the mouse.</summary>
    public void ShowMenuAtCursor(IReadOnlyList<(string Text, Action Click)> items)
    {
        var menu = Menu();
        foreach (var (text, click) in items) menu.Items.Add(Item(text, click));
        ShowMenuAt(menu);
    }

    void ShowMenuAt(MenuFlyout menu)
    {
        Native.GetCursorPos(out var p);
        double s = Scale;
        var pos = AppWindow.Position;
        Activate();
        menu.ShowAt(Root, new FlyoutShowOptions { Position = new Windows.Foundation.Point((p.X - pos.X) / s, (p.Y - pos.Y) / s) });
    }
}
