using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HeMiDock;

/// <summary>The pages of the settings.</summary>
sealed partial class SettingsWindow
{
    /// <summary>A time of a setting stored as "HH:mm" (24 hours).</summary>
    static TimePicker TimeBox(string property)
    {
        var prop = typeof(DockSettings).GetProperty(property);
        var picker = new TimePicker { ClockIdentifier = "24HourClock", MinuteIncrement = 5, MinWidth = 0 };
        if (TimeSpan.TryParse(prop.GetValue(DockSettings.Current) as string, System.Globalization.CultureInfo.InvariantCulture, out var t)) picker.Time = t;
        picker.TimeChanged += (_, e) => prop.SetValue(DockSettings.Current, e.NewTime.ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture));
        return picker;
    }

    void BuildPages()
    {
        var c = DockSettings.Current;

        // ───── General: everything that applies to the whole app ─────
        var general = NewPage("General", "\uE713", "general");
        var guide = new Button { Content = L.T("Open") };
        guide.Click += (_, _) => SetupWindow.Open();
        Card(general, "Setup guide", null, guide);                     // the looks are chosen there (the row of looks here was taken away)
        ChoiceRow(general, "Language", null, nameof(DockSettings.Language), "Automatic", "English", "Čeština");
        ToggleRow(general, "Start at sign-in", null, nameof(DockSettings.StartAtSignIn));
        ToggleRow(general, "Highlight of windows and the dock", null, nameof(DockSettings.DockOutline));
        ToggleRow(general, "Advanced editing", "Move the parts of the dock and the top bar with the mouse and change their size, rename apps in Start and change their icons",
            nameof(DockSettings.DockEditMode));
        // one choice for both: always, or only while the battery is low – that one only on a device with a battery
        bool hasBattery = Native.GetSystemPowerStatus(out var powerNow) && powerNow.BatteryFlag != 128 && powerNow.BatteryLifePercent != 255;
        ChoiceRow(general, "Lighter drawing", "Solid panels without shadows, frosted glass and animations", nameof(DockSettings.LowGraphicsChoice),
            hasBattery ? new[] { "Off", "On", "With a low battery" } : new[] { "Off", "On" });
        Depends(AddHint(general, "On by itself: the graphics card of this PC probably cannot draw the transparency."), () => DockSettings.LegacyGpu);
        ChoiceRow(general, "Notification position", "At the clock, top right with the top bar", nameof(DockSettings.ToastPosition),
            "Automatic", "At the clock", "Top right", "Top left", "Bottom right", "Bottom left");
        Header(general, "Colors and effects");
        ChoiceRow(general, "App theme", null, nameof(DockSettings.ThemeMode), "Automatic", "Light", "Dark");
        // the mode of Windows itself: light or dark at once, or by the time of day
        ChoiceRow(general, "Windows theme", null, nameof(DockSettings.OsTheme), "Light", "Dark", "Sunrise and sunset", "Own interval");
        Depends(Card(general, "Light from", null, TimeBox(nameof(DockSettings.OsThemeLightAt))), () => c.OsThemeSchedule == 2);
        Depends(Card(general, "Dark from", null, TimeBox(nameof(DockSettings.OsThemeDarkAt))), () => c.OsThemeSchedule == 2);
        ChoiceRow(general, "Color palette", null, nameof(DockSettings.AccentMode), "Automatic", "Custom");
        Depends(Card(general, "Color", null, AccentPicker()), () => c.AccentMode == 1);
        Depends(ChoiceRow(general, "Transparency", null, nameof(DockSettings.GlassMode), "Automatic", "On", "Off"), () => !c.LowGraphics);
        Depends(SliderRow(general, "Transparency intensity", null, nameof(DockSettings.Transparency), 0, 90, 1, "{0:0} %"), () => !c.NoTransparency && !c.LowGraphics);
        Depends(ChoiceRow(general, "Animation speed", null, nameof(DockSettings.AnimationSpeed), "Slow", "Normal", "Fast", "Off"), () => !c.LowGraphics);
        HotkeyRows(general, "general");

        // ───── Window manager ─────
        var windows = NewPage("Window manager", "\uE737", "windows");
        Header(windows, "Zones");
        ToggleRow(windows, "Window zones", "Drag a window with the chosen key held and drop it into a part of the screen", nameof(DockSettings.ZonesOn));
        Depends(ChoiceRow(windows, "Key for dragging into zones", null, nameof(DockSettings.ZoneKey), "Shift", "Ctrl", "Alt"), () => c.ZonesOn);
        var zoneRow = ChoiceRow(windows, "Zone layout", null, nameof(DockSettings.ZoneLayoutChoice), c.ZoneLayoutChoices());
        Depends(zoneRow, () => c.ZonesOn);
        // own layouts: any number of them, each with its name, chosen in the list above like the ready-made ones
        var zonesNew = new Button { Content = L.T("New…") };
        var zonesEdit = new Button { Content = L.T("Edit…"), Margin = new Thickness(6, 0, 0, 0) };
        var zonesRemove = new Button { Content = L.T("Remove"), Margin = new Thickness(6, 0, 0, 0) };
        void OwnZonesChanged() => SetOptions(zoneRow, c.ZoneLayoutChoices());
        void ZoneButtons() => zonesEdit.IsEnabled = zonesRemove.IsEnabled = c.OwnZoneLayout >= 0;
        ZoneButtons();
        _refresh.Add(ZoneButtons);
        zonesNew.Click += async (_, _) => { if (Content?.XamlRoot is { } root && await ZoneEditor.AskAsync(root, -1)) OwnZonesChanged(); };
        zonesEdit.Click += async (_, _) => { if (Content?.XamlRoot is { } root && await ZoneEditor.AskAsync(root, c.OwnZoneLayout)) OwnZonesChanged(); };
        zonesRemove.Click += async (_, _) =>
        {
            int own = c.OwnZoneLayout;
            if (own < 0 || !await Dialogs.Confirm(this, L.T("Remove the layout"), L.T("Remove the layout {0}?", L.T(c.ZoneLayoutChoices()[c.ZoneLayoutChoice])))) return;
            ZoneEditor.Remove(own);
            OwnZonesChanged();
        };
        Depends(Card(windows, "Own zone layouts", "Edit and Remove change the layout chosen above",
            new StackPanel { Orientation = Orientation.Horizontal, Children = { zonesNew, zonesEdit, zonesRemove } }), () => c.ZonesOn);
        Header(windows, "List of workspaces");
        var spaceName = new TextBox { Width = 220, VerticalAlignment = VerticalAlignment.Center };
        var saveSpace = new Button { Content = L.T("Save"), Margin = new Thickness(6, 0, 0, 0) };
        Card(windows, "Save the open apps and their places", null, new StackPanel { Orientation = Orientation.Horizontal, Children = { spaceName, saveSpace } });
        var spaces = new StackPanel();
        windows.Children.Add(spaces);
        void ListSpaces()
        {
            spaces.Children.Clear();
            foreach (var name in WindowManager.Workspaces().Keys)
            {
                var load = new Button { Content = L.T("Load") };
                load.Click += (_, _) => WindowManager.Open(name);
                var save = new Button { Content = L.T("Save"), Margin = new Thickness(6, 0, 0, 0) };
                save.Click += (_, _) => WindowManager.Save(name);
                var pin = new Button { Content = L.T("Pin to dock"), Margin = new Thickness(6, 0, 0, 0) };
                pin.Click += (_, _) => WindowManager.PinToDock(name);
                var del = new Button { Content = L.T("Remove"), Margin = new Thickness(6, 0, 0, 0) };
                del.Click += (_, _) => { WindowManager.Delete(name); ListSpaces(); };
                _noIndex = true;
                Card(spaces, name, null, new StackPanel { Orientation = Orientation.Horizontal, Children = { load, save, pin, del } });
                _noIndex = false;
            }
        }
        saveSpace.Click += (_, _) =>
        {
            string name = spaceName.Text.Trim();
            if (name.Length == 0) name = L.T("Workspace {0}", WindowManager.Workspaces().Count + 1);
            WindowManager.Save(name);
            spaceName.Text = "";
            ListSpaces();
        };
        ListSpaces();

        Header(windows, "Rules for apps");
        var addRule = new Button { Content = L.T("Add…") };
        Card(windows, "Where the windows of an app open", null, addRule);
        var rules = new StackPanel();
        windows.Children.Add(rules);
        void ListRules()
        {
            rules.Children.Clear();
            foreach (var (exe, place) in AppRules.All())
            {
                var choices = Enumerable.Range(1, WindowManager.ZoneCount).Select(i => L.T("Zone {0}", i)).Append(L.T("Maximized")).ToArray();
                var where = new ComboBox { ItemsSource = choices, MinWidth = 160, SelectedIndex = place == AppRules.Maximized ? choices.Length - 1 : int.TryParse(place, out int zone) ? Math.Min(zone, choices.Length - 1) : 0 };
                where.SelectionChanged += (_, _) => AppRules.Set(exe, where.SelectedIndex == choices.Length - 1 ? AppRules.Maximized : where.SelectedIndex.ToString());
                var remove = new Button { Content = L.T("Remove"), Margin = new Thickness(6, 0, 0, 0) };
                remove.Click += (_, _) => { AppRules.Set(exe, null); ListRules(); };
                _noIndex = true;
                Card(rules, exe, null, new StackPanel { Orientation = Orientation.Horizontal, Children = { where, remove } });
                _noIndex = false;
            }
        }
        addRule.Click += (_, _) =>
        {
            var menu = new MenuFlyout();
            foreach (var exe in WindowManager.OpenApps())
                menu.Items.Add(MenuItem(exe, () => { AppRules.Set(exe, "0"); ListRules(); }));
            menu.ShowAt(addRule);
        };
        ListRules();
        HotkeyRows(windows, "windows");

        // ───── Search: the same for the Start menu and the quick search ─────
        var search = NewPage("Search options", "\uE721", "search");
        Header(search, "Search results");
        ToggleRow(search, "Apps", null, nameof(DockSettings.SearchApps));
        ToggleRow(search, "Unit and currency conversion", null, nameof(DockSettings.SearchConversions));
        ToggleRow(search, "Windows settings and tools", null, nameof(DockSettings.SearchWindowsSettings));
        ToggleRow(search, "App settings", null, nameof(DockSettings.SearchDockSettings));
        ToggleRow(search, "Files and folders", "From the Windows search index", nameof(DockSettings.SearchFiles));
        ToggleRow(search, "Clipboard history", "Texts copied since the app started; kept only in memory", nameof(DockSettings.SearchClipboard));

        // the menu of the Start button (right click, Win + X): on the page of the bar the Start button is in now – the dock, or
        // the top bar while it is on. The items of "Shut down or sign out" are the one list of the power button of Start.
        var powerIds = new HashSet<string>(StartSide.Power.SelectMany(g => g).Select(p => p.Id));
        var powerMenuPage = OrderedItemsPage("Right click on Start or Win+X. The items of \"Shut down or sign out\" are the same as those of the power button of Start. The arrows change the order in the menu.",
            // in the order of the menu: the tools, the power items, Desktop
            () => PowerMenu.Ordered().Where(g => g.All(e => e.Id != "Desktop")).Select(g => g.Select(e => e.Id))
                .Concat(StartSide.OrderedPower().Select(g => g.Select(p => p.Id)))
                .Concat(PowerMenu.Ordered().Where(g => g.Any(e => e.Id == "Desktop")).Select(g => g.Select(e => e.Id))),
            id => powerIds.Contains(id) ? StartSide.IsPowerShown(id) : PowerMenu.IsShown(id),
            (id, on) => { if (powerIds.Contains(id)) StartSide.SetPowerShown(id, on); else PowerMenu.SetShown(id, on); },
            (id, delta) => { if (powerIds.Contains(id)) StartSide.MovePower(id, delta); else PowerMenu.Move(id, delta); });

        // ───── Dock ─────
        var dock = NewPage("Dock", "\uE8A9", "dock");
        int dockIndex = _pages.Count - 1;
        _subPages[powerMenuPage] = (dockIndex, "Start button menu");          // found by the search too
        Header(dock, "Placement");
        // monitor of the dock – only with more than one monitor
        var monitors = Monitors.All();
        Depends(ToggleRow(dock, "On all monitors", null, nameof(DockSettings.DockAllMonitors)), () => monitors.Count > 1);
        Depends(ChoiceRow(dock, "Monitor", null, nameof(DockSettings.DockMonitor),
            monitors.Select((m, i) => (i == 0 ? L.T("Main monitor") : L.T("Monitor {0}", i + 1))
                + $" ({m.Bounds.Right - m.Bounds.Left} × {m.Bounds.Bottom - m.Bounds.Top})").ToArray()), () => monitors.Count > 1 && !c.DockAllMonitors);
        Depends(ChoiceRow(dock, "Status icons and clock", "On which monitor; another monitor gets a dock of its own for them", nameof(DockSettings.StatusMonitor),
            new[] { L.T("On the main dock"), L.T("On all monitors") }.Concat(monitors.Select((m, i) => (i == 0 ? L.T("Main monitor") : L.T("Monitor {0}", i + 1))
                + $" ({m.Bounds.Right - m.Bounds.Left} × {m.Bounds.Bottom - m.Bounds.Top})")).ToArray()), () => monitors.Count > 1 && !c.TopBarOn);   // with the top bar they are in it
        // the top edge belongs to the top bar
        DisableOption(ChoiceRow(dock, "Position on screen", "At the top only without the top bar", nameof(DockSettings.DockPosition), "Bottom", "Left", "Right", "Top"), 3, () => c.TopBar);
        Depends(ChoiceRow(dock, "Dock width", null, nameof(DockSettings.DockFull), "Automatic, dock mode", "Full width, icons on the left", "Full width, icons in the middle"), () => !c.IsVertical);
        Depends(SliderRow(dock, "Distance from the screen edge", null, nameof(DockSettings.BottomMargin), 0, 60, 1, "{0:0} px"), () => c.IsVertical || c.DockFull == 0);
        Depends(SliderRow(dock, "Length of the dock", "0 = as long as its apps; longer: free room after the apps", nameof(DockSettings.DockMinLength), 0, 3000, 10, "{0:0} px"),
            () => c.IsVertical || c.DockFull == 0);
        Depends(ToggleRow(dock, "Widening spreads the icons", "Dragging the ends of the dock in Advanced editing changes the spacing of the icons instead of adding free room",
            nameof(DockSettings.WidenSpreadsIcons)), () => c.IsVertical || c.DockFull == 0);
        ToggleRow(dock, "Automatically hide the dock", null, nameof(DockSettings.AutoHide));
        Depends(ToggleRow(dock, "Only when a window covers it", null, nameof(DockSettings.AutoHideDodge)), () => c.AutoHide);
        Depends(SliderRow(dock, "Hide after", null, nameof(DockSettings.AutoHideDelay), 0.2, 3, 0.1, "{0:0.0} s"), () => c.AutoHide);

        Header(dock, "Size");
        SliderRow(dock, "Dock size", "Size of the dock with its icons", nameof(DockSettings.IconSize), 24, 96, 1, "{0:0} px");
        SliderRow(dock, "App icon size", null, nameof(DockSettings.IconScale), 50, 150, 1, "{0:0} %");
        SliderRow(dock, "Size of the Start, Search and Task view icons", null, nameof(DockSettings.SysIconScale), 50, 150, 1, "{0:0} %");
        SliderRow(dock, "Icon spacing", null, nameof(DockSettings.IconSpacing), 0, 40, 1, "{0:0} px");
        SliderRow(dock, "Padding at the ends", null, nameof(DockSettings.EndPadding), 0, 32, 1, "{0:0} px");

        Header(dock, "Look of the dock");
        Depends(SliderRow(dock, "Corner radius", null, nameof(DockSettings.Rounding), 0, 40, 1, "{0:0} px"), () => c.IsVertical || c.DockFull == 0);
        SliderRow(dock, "Shadow", null, nameof(DockSettings.DockShadow), 0, 100, 5, "{0:0} %");
        Depends(ChoiceRow(dock, "Animation under the mouse", null, nameof(DockSettings.HoverEffect), "Magnification", "Highlight", "Magnification and highlight"), () => c.IsVertical || c.DockFull == 0);
        Depends(SliderRow(dock, "Magnification", null, nameof(DockSettings.Magnification), 1.0, 2.0, 0.05, "{0:0.00}×"), () => !c.HighlightOnly);
        Depends(SliderRow(dock, "Width of the magnification under the mouse", null, nameof(DockSettings.MagnificationSpread), 1.5, 6, 0.5, "{0:0.0}"),
            () => !c.HighlightOnly && c.Magnification > 1.001 && !c.MagnifyInside);
        Depends(ToggleRow(dock, "Magnify only inside the dock", null, nameof(DockSettings.MagnifyInside)),
            () => !c.HighlightOnly && c.Magnification > 1.001);
        ToggleRow(dock, "Click and launch animation", null, nameof(DockSettings.LaunchAnimation));

        Header(dock, "Items on the dock");
        ToggleRow(dock, "Start button", null, nameof(DockSettings.ShowStartButton));
        Depends(ChoiceRow(dock, "Start button look", null, nameof(DockSettings.StartIconLook),
            "Default", "Default, monochrome", "App grid", "App grid, monochrome", "Own image"), () => c.ShowStartButton);
        Depends(PictureRow(dock, "Own image", null), () => c.ShowStartButton && c.StartIconLook == 4);
        Depends(NavRow(dock, "Start button menu", "Right click on Start or Win+X",
            () => ShowSubPage(dockIndex, "Start button menu", powerMenuPage)), () => !c.TopBarOn);
        // not in the dock while the top bar is on (it has its own search)
        ToggleRow(dock, "Search button", null, nameof(DockSettings.ShowSearchButtons));
        ToggleRow(dock, "Task view button", null, nameof(DockSettings.ShowTaskViewButton));
        ToggleRow(dock, "Recycle Bin", null, nameof(DockSettings.ShowRecycleBin));
        ToggleRow(dock, "Downloads", null, nameof(DockSettings.ShowDownloads));
        Depends(ChoiceRow(dock, "Place of these buttons", null, nameof(DockSettings.SysIconsPlace), "Dock", "Top bar"), () => c.TopBarOn);
        ToggleRow(dock, "Virtual desktops", null, nameof(DockSettings.ShowDesktops));
        ToggleRow(dock, "Show desktop button", "At the end of the dock, or of the top bar", nameof(DockSettings.ShowDesktopButton));
        ToggleRow(dock, "Divide the parts of the dock", null, nameof(DockSettings.ShowSeparators));
        ToggleRow(dock, "Running indicator", null, nameof(DockSettings.ShowRunningIndicator));
        ToggleRow(dock, "App names", null, nameof(DockSettings.ShowAppNames));

        Header(dock, "Behavior");
        ToggleRow(dock, "Window previews", null, nameof(DockSettings.ShowPreviews));
        HotkeyRows(dock, "dock");
        ToggleRow(dock, "Win + number", "Win+1 … 9 open the apps of the dock", nameof(DockSettings.AppShortcuts));

        // ───── Top bar (like macOS / GNOME) ─────
        var bar = NewPage("Top bar", "\uE7F4", "topbar");
        int barIndex = _pages.Count - 1;
        ToggleRow(bar, "Top bar", null, nameof(DockSettings.TopBar));
        Depends(ChoiceRow(bar, "Clock", null, nameof(DockSettings.TopBarClockPos), "Left", "In the middle", "Right"), () => c.TopBarOn);
        Depends(ToggleRow(bar, "Name of the active app", null, nameof(DockSettings.TopBarAppName)), () => c.TopBarOn);
        Depends(ToggleRow(bar, "System menu", null, nameof(DockSettings.TopBarSystemMenu)), () => c.TopBarOn);
        Depends(ToggleRow(bar, "System menu instead of Start", null, nameof(DockSettings.TopBarSystemStart)), () => c.TopBarOn && c.TopBarSystemMenu);
        Depends(NavRow(bar, "Start button menu", "Right click on Start or Win+X",
            () => ShowSubPage(barIndex, "Start button menu", powerMenuPage)), () => c.TopBarOn);
        Depends(SliderRow(bar, "Height", null, nameof(DockSettings.TopBarHeight), 24, 44, 1, "{0:0} px"), () => c.TopBarOn);
        Depends(ToggleRow(bar, "Automatically hide the top bar", null, nameof(DockSettings.TopBarAutoHide)), () => c.TopBarOn);
        Depends(AddHint(bar, "Double click on an empty place: maximize the window. Mouse wheel: volume control."), () => c.TopBarOn);

        var start = NewPage("Start menu", "\uE700", "start");
        ToggleRow(start, "Start menu", null, nameof(DockSettings.ReplaceStartMenu));
        Depends(ChoiceRow(start, "Look", null,
            nameof(DockSettings.StartLayout), "Modern", "Two columns", "Full screen"), () => c.ReplaceStartMenu);
        // options of one look are shown only while that look is chosen (0 default, 1 two columns, 2 full screen)
        Only(SideRow(start, "Side column with the user and places", nameof(DockSettings.ClassicSideLeft)), 1);
        Only(ToggleRow(start, "Recent files", null, nameof(DockSettings.StartShowRecent)), 0, 1);
        Only(ToggleRow(start, "All apps as a grid", null, nameof(DockSettings.StartAppsGrid)), 0);
        Only(ToggleRow(start, "User", null, nameof(DockSettings.StartShowUser)), 0, 1, 2);
        Only(ToggleRow(start, "Power button", null, nameof(DockSettings.StartShowPower)), 0, 1, 2);
        Header(start, "Size");
        Only(SliderRow(start, "Width", null, nameof(DockSettings.StartWidth), 460, 960, 10, "{0:0} px"), 0);
        Only(SliderRow(start, "Height", null, nameof(DockSettings.StartHeight), 440, 960, 10, "{0:0} px"), 0, 1);
        Depends(SliderRow(start, "Icon size in the Start menu", null, nameof(DockSettings.StartIconSize), 24, 56, 1, "{0:0} px"), () => c.ReplaceStartMenu);
        // lists of items have their own sub-pages (like in Windows 11 Settings)
        int startIndex = _pages.Count - 1;
        var sidePage = ItemsPage("Folders and links",
            StartSide.Places.Select(g => g.Select(p => p.Id)), StartSide.IsPlaceShown, StartSide.SetPlaceShown);
        Only(NavRow(start, "Start menu side column", "Folders and links",
            () => ShowSubPage(startIndex, "Start menu side column", sidePage)), 1);
        var startPowerPage = OrderedItemsPage("Shut down, restart, lock …",
            () => StartSide.OrderedPower().Select(g => g.Select(p => p.Id)), StartSide.IsPowerShown, StartSide.SetPowerShown, StartSide.MovePower);
        // one list for the power button of Start and the Start button menu (Win + X)
        NavRow(start, "Items of the power button", "Shut down, restart, lock … – in Start and in the Start button menu",
            () => ShowSubPage(startIndex, "Items of the power button", startPowerPage));
        _subPages[sidePage] = (startIndex, "Start menu side column");          // their items can be found by the search too
        _subPages[startPowerPage] = (startIndex, "Items of the power button");
        HotkeyRows(start, "start");
        StartKeyRows(start);

        // ───── Status icons: the same for the dock and the top bar ─────
        var right = NewPage("Status icons", "\uE9F5", "status");
        SliderRow(right, "Icon size", null, nameof(DockSettings.StatusAndTrayIconSize), 12, 28, 1, "{0:0} px");
        Header(right, "System tray");
        ToggleRow(right, "System tray icons", null, nameof(DockSettings.ShowTrayArea));
        int rightIndex = _pages.Count - 1;
        Depends(NavRow(right, "Choice of icons", "Which apps show their icon at all",
            () => ShowSubPage(rightIndex, "Choice of icons", TrayIconsPage())), () => c.ShowTrayArea);
        AddHint(right, "Drag an icon onto the arrow or back to hide or show it (or Ctrl + click).");
        Header(right, "Status icons");
        AddHint(right, "A click on them opens the control center.");
        ToggleRow(right, "Microphone in use", "Orange icon while an app uses it", nameof(DockSettings.ShowPrivacy));
        ToggleRow(right, "Camera in use", "Orange icon while an app uses it", nameof(DockSettings.ShowCameraUse));
        ToggleRow(right, "Location in use", "Orange icon while an app uses it", nameof(DockSettings.ShowLocationUse));
        ToggleRow(right, "Windows Security", "Shield of Microsoft Defender with its state; its icon in the notification area is hidden", nameof(DockSettings.ShowSecurity));
        ToggleRow(right, "Network", null, nameof(DockSettings.ShowWifi));
        Depends(ToggleRow(right, "Bluetooth", null, nameof(DockSettings.ShowBluetooth)), () => App.HasBluetooth);   // only on devices with Bluetooth
        Depends(ToggleRow(right, "Charge of Bluetooth devices", "On the top bar – next to the Bluetooth icon", nameof(DockSettings.ShowBluetoothBattery)),
            () => App.HasBluetooth && c.ShowBluetooth && c.TopBarOn);
        ToggleRow(right, "Volume", null, nameof(DockSettings.ShowVolume));
        Depends(ToggleRow(right, "Volume level", null, nameof(DockSettings.ShowVolumePercent)), () => c.ShowVolume);
        // battery options only on devices that have a battery
        bool battery = Native.GetSystemPowerStatus(out var power) && power.BatteryFlag != 128 && power.BatteryLifePercent != 255;
        Depends(ToggleRow(right, "Battery", null, nameof(DockSettings.ShowBattery)), () => battery);
        Depends(ToggleRow(right, "Battery percentage", null, nameof(DockSettings.ShowBatteryPercent)), () => battery && c.ShowBattery);
        // only with more than one keyboard (like Windows)
        Depends(ToggleRow(right, "Keyboard language", null, nameof(DockSettings.ShowInputLanguage)),
            () => InputLanguage.Layouts().Length > 1);
        ToggleRow(right, "Removable drives", null, nameof(DockSettings.ShowRemovable));
        Depends(ToggleRow(right, "Now playing", "On the top bar – title and play / pause", nameof(DockSettings.TopBarMedia)), () => c.TopBarOn);
        Header(right, "Weather");
        ToggleRow(right, "Weather", "From the service Open-Meteo; the name of the city is sent to it", nameof(DockSettings.WeatherOn));
        Depends(CityRow(right, "City"), () => c.WeatherOn);
        Header(right, "Signs");
        ToggleRow(right, "Status signs", "Do not disturb, VPN, muted microphone, restart for updates", nameof(DockSettings.ShowStatusSigns));
        ToggleRow(right, "Caps Lock", null, nameof(DockSettings.ShowCapsLock));
        ToggleRow(right, "Num Lock", null, nameof(DockSettings.ShowNumLock));
        Header(right, "System monitor");
        ToggleRow(right, "System monitor", null, nameof(DockSettings.ShowSystemMonitor));
        MonitorRows(right, () => c.ShowSystemMonitor);

        // ───── Control center: its parts and quick buttons (also editable in the control center itself) ─────
        _quickPage = NewPage("Control center", "\uE9E9", "quick");
        BuildQuickPage();

        var clock = NewPage("Clock and calendar", "\uE823", "clock");
        Header(clock, "Time");
        ToggleRow(clock, "Show clock", null, nameof(DockSettings.ShowClock));
        Depends(ChoiceRow(clock, "Time format", null, nameof(DockSettings.ClockMode), "Automatic", "24-hour", "12-hour"), () => c.ShowClock);
        Depends(ToggleRow(clock, "Show seconds", null, nameof(DockSettings.ShowSeconds)), () => c.ShowClock);
        Depends(SliderRow(clock, "Font size", null, nameof(DockSettings.ClockFontSize), 10, 28, 1, "{0:0} pt"), () => c.ShowClock);
        Header(clock, "Date");
        Depends(ToggleRow(clock, "Show date", null, nameof(DockSettings.ShowDate)), () => c.ShowClock);
        Depends(ChoiceRow(clock, "Date format", null, nameof(DockSettings.DateMode),
            "Weekday and date", "Long ", "Short ", "Custom"), () => c.ShowClock && c.ShowDate);
        Depends(ChoiceRow(clock, "Order of day and month", null, nameof(DockSettings.DateOrder), "Automatic", "Day first (10. 11.)", "Month first (11/10)"),
            () => c.ShowClock && c.ShowDate && c.DateMode != 3);
        Depends(TextRow(clock, "Custom format", "e.g. d MMM yyyy · dddd d. MMMM · MM/dd/yyyy", nameof(DockSettings.CustomDateFormat)),
            () => c.ShowClock && c.ShowDate && c.DateMode == 3);
        // the top bar has the clock always on one line (and the dock then shows none)
        Depends(ChoiceRow(clock, "Arrangement", null, nameof(DockSettings.ClockLine), "Date below, right-aligned", "One line"), () => c.ShowClock && c.ShowDate && !c.TopBarOn);
        Header(clock, "Calendar");
        ChoiceRow(clock, "Calendar data", null, nameof(DockSettings.CalendarSource), "Calendar only", "Outlook", "Internet calendar (.ics)");
        Depends(AddHint(clock, "Classic Outlook was not found. The new Outlook cannot be read – publish its calendar and use the internet calendar."), () => c.CalendarSource == 1 && !OutlookCalendar.Available);
        Depends(TextRow(clock, "Address of the calendar", "https://… or a file .ics", nameof(DockSettings.CalendarIcs)), () => c.CalendarSource == 2);
        var calendars = new Button { Content = L.T("Choose…") };
        calendars.Click += async (_, _) =>
        {
            var names = await OutlookCalendar.ListAsync();
            if (names == null) { _ = Dialogs.Info(this, L.T("Calendars"), L.T("Outlook did not answer.")); return; }
            var menu = new MenuFlyout();
            foreach (var name in names)
            {
                bool hidden = c.OutlookCalendarsHidden.Split('|').Contains(name, StringComparer.OrdinalIgnoreCase);
                var item = MenuItem(name, () => OutlookCalendar.SetHidden(name, !hidden));
                item.IsChecked = !hidden;
                menu.Items.Add(item);
            }
            menu.ShowAt(calendars);
        };
        Depends(Card(clock, "Calendars", null, calendars), () => OutlookCalendar.Available && c.CalendarEvents);
        ChoiceRow(clock, "First day of the week", null, nameof(DockSettings.CalendarFirstDay), "Automatic", "Monday", "Sunday");
        ToggleRow(clock, "Week numbers", null, nameof(DockSettings.CalendarWeekNumbers));
        Header(clock, "Notifications");
        Depends(ToggleRow(clock, "Notification count", null, nameof(DockSettings.ShowNotificationCount)), () => c.ShowClock);
        Depends(ToggleRow(clock, "Remove notifications of read mail", "Outlook", nameof(DockSettings.RemoveReadMail)), () => OutlookCalendar.Available);
        HotkeyRows(clock, "clock");

        // ───── Keyboard shortcuts: every shortcut of HeMiDock on one page ─────
        var keys = NewPage("Keyboard shortcuts", "\uE765", "keys");
        // every shortcut in one overview, by the part of the app it belongs to (each is also on the page of that part)
        _noIndex = true;                                               // the search finds each shortcut on its own page
        foreach (var (group, title) in new[] { ("start", "Start menu"), ("dock", "Dock"), ("quick", "Control center"), ("clock", "Clock and calendar"), ("windows", "Window manager"), ("general", "General") })
        {
            Header(keys, title);
            foreach (var a in Hotkeys.Actions.Where(a => a.Group == group)) HotkeyRow(keys, a.Id, a.Name, a.Description);
            if (group == "start") StartKeyRows(keys);
            if (group == "quick") ToggleRow(keys, "Win + A", "Opens the control center instead of the quick settings of Windows", nameof(DockSettings.KeyControl));
            if (group == "dock") ToggleRow(keys, "Win + number", "Win+1 … 9 open the apps of the dock", nameof(DockSettings.AppShortcuts));
            if (group == "windows") ChoiceRow(keys, "Key for dragging into zones", null, nameof(DockSettings.ZoneKey), "Shift", "Ctrl", "Alt");
        }
        _noIndex = false;

        var about = NewPage("About HeMiDock", "\uE946", "about");
        Header(about, "HeMiDock");
        InfoRow(about, "Author", new TextBlock { Text = "Richard Hemala" });
        InfoRow(about, "Version", new TextBlock { Text = AppInfo.Version });
        InfoRow(about, "Build date", new TextBlock { Text = AppInfo.BuildDate?.ToString("d", System.Globalization.CultureInfo.CurrentCulture) ?? "–" });
        var project = new Button { Content = L.T("Open") };
        project.Click += (_, _) => Fg.Open(Updater.RepoUrl);
        Card(about, "Project on GitHub", null, project);
        var report = new Button { Content = L.T("Open") };
        report.Click += (_, _) => Fg.Open(Updater.IssuesUrl);
        Card(about, "Report a problem", null, report);

        Header(about, "Updates");
        ToggleRow(about, "Check for updates automatically", null, nameof(DockSettings.CheckUpdates));
        var check = new Button { Content = L.T("Check now") };
        var checkText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Foreground = ThemeBrush.Get("TextFillColorSecondaryBrush") };
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            checkText.Text = L.T("Checking…");
            var release = await Updater.CheckAsync(quiet: false);
            check.IsEnabled = true;
            if (release == null) checkText.Text = L.T("GitHub could not be reached.");
            else if (release.Version <= Updater.Current) checkText.Text = L.T("HeMiDock is up to date.");
            else
            {
                checkText.Text = L.T("HeMiDock {0} is available.", release.Version.ToString(3));
                var install = new Button { Content = L.T(Updater.Installed ? "Install" : "Download") };
                install.Click += (_, _) => _ = Updater.InstallAsync(release);
                if (check.Parent is StackPanel row && row.Children.Count == 2) row.Children.Add(install);
            }
        };
        Card(about, "Check for updates", null, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { checkText, check } });
        var licenses = new Button { Content = L.T("Open") };
        // the file was never part of the build: the button opened nothing
        licenses.Click += (_, _) => Fg.Open(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "ThirdPartyNotices.txt"));
        Card(about, "Licenses", null, licenses);
        Header(about, "Diagnostics");
        ToggleRow(about, "Debug mode", null, nameof(DockSettings.DebugMode));
        var export = new Button { Content = L.T("Save to desktop") };
        export.Click += (_, _) =>
        {
            string zip = Diag.Export();
            if (zip != null) Fg.Run("explorer.exe", $"/select,\"{zip}\"");
            else _ = Dialogs.Info(this, L.T("Diagnostic data"), L.T("The diagnostic data could not be saved."));
        };
        Depends(Card(about, "Diagnostic data", "Logs, settings and system information in one zip file", export), () => DockSettings.Current.DebugMode);

        var more = _index.Where(x => AdvancedOptions.Any(o => L.T(o) == x.Title)).ToList();
        foreach (var hit in more) _advanced.Add(hit.Card);
        // the page each card is in: Parent of an element is known only once the window shows it
        CollectMore(more.Select(hit => ((UIElement)hit.Card, hit.Parent)));
    }

    /// <summary>
    /// The fine tuning (sizes in pixels, speeds …): at the bottom of its page under "More options", opened with a click. They
    /// were shown only in edit mode before – now every option is in the settings, these ones out of the way.
    /// </summary>
    static readonly string[] AdvancedOptions =
    {
        "Corner radius", "Shadow", "Width of the magnification under the mouse", "Magnify only inside the dock", "Padding at the ends", "Icon spacing", "App icon size",
        "Size of the Start, Search and Task view icons", "Divide the parts of the dock", "Click and launch animation", "Animation speed", "Transparency intensity",
        "In low battery mode", "Hide after", "Only when a window covers it", "Font size", "Arrangement", "Custom format", "Week numbers",
        "First day of the week", "Caps Lock", "Num Lock", "Status signs", "Removable drives",
        "Win + number", "Length of the dock", "Widening spreads the icons", "Distance from the screen edge", "Icon size in the Start menu", "Notification position"
        // the lists of the Start menu (side column, power button, Win + X menu) are always shown: without edit mode there was
        // no way to find where the Win + X menu is changed
    };
    readonly HashSet<UIElement> _advanced = new();
}
