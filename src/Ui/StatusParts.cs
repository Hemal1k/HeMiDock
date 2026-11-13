using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace HeMiDock;

/// <summary>What the status icons show, read at one moment.</summary>
sealed record StatusSnapshot(
    List<string> Mic, List<string> Cam, List<string> Place, bool Dnd, string Vpn, bool MicMuted, IntPtr[] Layouts, IntPtr Layout, int Removable,
    (string Glyph, bool Connected, string Tip) Net, (float Level, bool Mute, string Name)? Audio,
    (int Percent, bool Plugged, bool Charging, bool Saver, int SecondsLeft)? Battery, bool UpdateRestart, bool Security);

/// <summary>
/// The state of the status icons, read in the background: it took 65 ms on the thread of the interface every few seconds
/// (network adapters, the microphone and the camera in the registry, the sound devices), which made every window of the app
/// stop for a moment – the highlight in the settings stuck, the animations jerked. The dock and the top bar asking at the
/// same time share one reading.
/// </summary>
static class StatusData
{
    static Task<StatusSnapshot> _reading;

    /// <summary>The last reading (null before the first one).</summary>
    public static StatusSnapshot Last { get; private set; }

    public static async Task<StatusSnapshot> ReadAsync()
    {
        if (_reading is not { IsCompleted: false }) _reading = Task.Run(Read);
        try { return Last = await _reading; }
        catch (Exception ex) { Log.Write("Status", ex); return Last; }
    }

    static StatusSnapshot Read()
    {
        var s = DockSettings.Current;
        return new StatusSnapshot(
            s.ShowPrivacy ? Privacy.InUse("microphone") : new List<string>(),
            s.ShowCameraUse ? Privacy.InUse("webcam") : new List<string>(),
            s.ShowLocationUse ? Privacy.InUse("location") : new List<string>(),
            Dnd.IsOn, Vpn.Active(), AudioControl.MicNow() is { Mute: true },
            InputLanguage.Layouts(), InputLanguage.Current,
            s.ShowRemovable ? RemovableDrives.Now().Count : 0,
            StatusInfo.Network(), AudioControl.Now(), StatusInfo.Battery(), s.ShowStatusSigns && SystemPower.UpdatePending,
            s.ShowSecurity && SecurityStatus.Refresh());
    }
}

/// <summary>
/// Bluetooth for the status icons and the control center: is there a radio, is it on, which devices are connected.
/// Watched once for the whole app.
/// </summary>
static class BluetoothWatch
{
    static RadioSwitch _radio;
    static bool _started, _checking;

    public static bool Present => _radio != null;
    public static bool On { get; private set; }
    /// <summary>Names of the connected devices.</summary>
    public static List<string> Connected { get; private set; } = new();
    /// <summary>The charge of the connected devices that tell it, by name.</summary>
    public static Dictionary<string, int> Battery { get; private set; } = new();
    public static event Action Changed;

    /// <summary>"Mi Silent Mouse 80 %" – the name with the charge when the device tells it.</summary>
    public static string WithBattery(string name) => Battery.TryGetValue(name, out int p) ? $"{name} {p} %" : name;

    public const string ContainerKey = "System.Devices.Aep.ContainerId";
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";   // the battery level Windows shows in its Bluetooth settings

    /// <summary>
    /// The charge of a connected device as Windows shows it (headphones, mice …): it is kept by the device nodes of the
    /// device (its container), not by the Bluetooth endpoint; null when the device does not tell it.
    /// </summary>
    public static async Task<int?> BatteryOf(Windows.Devices.Enumeration.DeviceInformation endpoint)
    {
        try
        {
            if (!endpoint.Properties.TryGetValue(ContainerKey, out var c) || c is not Guid container || container == Guid.Empty) return null;
            var nodes = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{{{container}}}\"",
                new[] { BatteryKey }, Windows.Devices.Enumeration.DeviceInformationKind.Device);
            foreach (var n in nodes)
                if (n.Properties.TryGetValue(BatteryKey, out var v) && v != null && Convert.ToInt32(v) is >= 0 and <= 100 and var p) return p;
        }
        catch (Exception ex) { Log.Write("Bluetooth battery", ex); }
        return null;
    }

    public static async void Start()
    {
        if (_started) return;
        _started = true;
        try
        {
            _radio = await RadioSwitch.FindAsync(Windows.Devices.Radios.RadioKind.Bluetooth);
            if (_radio != null) _radio.StateChanged += () => Ui.Post(() => _ = CheckAsync());
        }
        catch (Exception ex) { Log.Write("Bluetooth", ex); }
        await CheckAsync();
    }

    static long _checkedAt;

    /// <summary>
    /// With the reading of the status icons (every 5 seconds): the connected devices listed again, so a device connected or
    /// disconnected shows within 5 seconds; once for the dock and the top bar together (they read at the same moment). The
    /// radio switched on or off comes at once by its own event.
    /// </summary>
    public static Task RecheckAsync() => Environment.TickCount64 - _checkedAt < 4_500 ? Task.CompletedTask : CheckAsync();

    public static async Task CheckAsync()
    {
        if (_checking) return;
        _checking = true;
        _checkedAt = Environment.TickCount64;
        try
        {
            var names = new List<string>();
            var battery = new Dictionary<string, int>();
            bool on = _radio?.State == Windows.Devices.Radios.RadioState.On;
#if DEBUG
            if (Simulation.Bluetooth) { if (on) foreach (var d in Simulation.BluetoothDevices.Where(d => d.Connected)) { names.Add(d.Name); battery[d.Name] = 80; } }
            else
#endif
            if (on)
                foreach (var selector in new[]
                {
                    Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromConnectionStatus(Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected),
                    Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected)
                })
                    foreach (var d in await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector, new[] { ContainerKey }, Windows.Devices.Enumeration.DeviceInformationKind.AssociationEndpoint))
                    {
                        if (string.IsNullOrWhiteSpace(d.Name) || names.Contains(d.Name)) continue;
                        names.Add(d.Name);
                        if (await BatteryOf(d) is int p) battery[d.Name] = p;
                    }
            bool changed = on != On || !names.SequenceEqual(Connected) || battery.Count != Battery.Count || battery.Any(b => !Battery.TryGetValue(b.Key, out int was) || was != b.Value);
            On = on;
            Connected = names;
            Battery = battery;
            if (changed) Changed?.Invoke();
        }
        catch (Exception ex) { Log.Write("Bluetooth devices", ex); }
        _checking = false;
    }
}

/// <summary>
/// The status icons as one button (a click opens the control center): privacy, Do not disturb, VPN, muted microphone,
/// weather, network, Bluetooth, volume and battery – each by its setting. The one implementation for the top bar (its own
/// button) and the dock (inside its status button): they were written twice and every fix had to be made on both.
/// </summary>
sealed class StatusParts
{
    public Button Button { get; }
    readonly StackPanel _icons = new() { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
    readonly FontIcon _privacy = Glyph(""), _dnd = Glyph(""), _vpn = Glyph(""), _micMute = Glyph(""), _weather = Glyph(""), _net = Glyph(""), _bt = Glyph(""), _vol = Glyph("");
    readonly BatteryIcon _bat = new();
    readonly TextBlock _weatherText = Text(), _volText = Text(), _batText = Text();
    readonly Grid _btIcon = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly Line _btOff;
    // a device connected: the "connected" Bluetooth sign, a dot on each side of the rune (the brighter icon alone was hardly
    // seen, a dot under it looked like the mark of a running app)
    readonly Ellipse _btLeft = BtDot(HorizontalAlignment.Left), _btRight = BtDot(HorizontalAlignment.Right);
    readonly TextBlock _btText = Text();
    // updates of Windows wait for a restart: a circular arrow with a dot of the accent colour (the icon of Windows for it is
    // drawn by its own taskbar only, never given to another one)
    readonly FontIcon _update = Glyph("");
    // a new version of HeMiDock on GitHub: an arrow down with a dot of the accent colour; right click: install it
    readonly Grid _appUpdate = new() { VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    readonly FontIcon _appUpdateGlyph = Glyph("");
    // an app uses the location: its own orange pin beside the one of the microphone / camera (as Windows shows it)
    readonly FontIcon _place = Glyph("");
    // Windows Security: a shield with a dot of the state (yellow: an action recommended, red: needed); its own clicks
    readonly FontIcon _shield = Glyph("");
    readonly Ellipse _shieldDot = new() { Width = 6, Height = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -1), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly Grid _security = new() { VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    readonly Grid _updateIcon = new() { VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    readonly bool _topBar;

    static Ellipse BtDot(HorizontalAlignment side) => new()
    {
        Width = 3, Height = 3, HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
        IsHitTestVisible = false, Fill = ThemeBrush.Get("TextFillColorPrimaryBrush")
    };
    readonly StackPanel _batPanel = new() { Orientation = Orientation.Horizontal, Spacing = 5 };
    readonly StackPanel _btPanel = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    readonly StackPanel _volPanel = new() { Orientation = Orientation.Horizontal, Spacing = 4 };

    static DockSettings S => DockSettings.Current;

    static FontIcon Glyph(string g) => new() { Glyph = g, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
    // cut to the height of the letters (as the keyboard language): with the room of the line the numbers sat higher than the icons
    static TextBlock Text() => new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextLineBounds = TextLineBounds.Tight };

    public StatusParts(Button host = null)
    {
        _privacy.Foreground = _place.Foreground = ThemeBrush.Get("SystemFillColorCautionBrush");
        _btOff = new Line { X1 = 0, Y1 = 0, X2 = 1, Y2 = 1, Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, StrokeThickness = 1.4, IsHitTestVisible = false, Stroke = ThemeBrush.Get("TextFillColorPrimaryBrush") };
        _btIcon.Children.Add(_bt);
        _btIcon.Children.Add(_btOff);
        _btIcon.Children.Add(_btLeft);
        _btIcon.Children.Add(_btRight);
        _btPanel.Children.Add(_btIcon);
        _btPanel.Children.Add(_btText);
        _topBar = host == null;
        _volPanel.Children.Add(_vol);
        _volPanel.Children.Add(_volText);
        _batPanel.Children.Add(_bat);
        _batPanel.Children.Add(_batText);
        _updateIcon.Children.Add(_update);
        _updateIcon.Children.Add(new Ellipse
        {
            Width = 5, Height = 5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -1, -1), IsHitTestVisible = false, Fill = ThemeBrush.Get("AccentFillColorDefaultBrush")
        });
        _security.Children.Add(_shield);
        _security.Children.Add(_shieldDot);
        // left: the control center like the other status icons; right: the menu of Windows Security (not the control center,
        // which the right button of the status icons opens otherwise)
        _security.RightTapped += (_, e) => { e.Handled = true; TrayView.SecurityMenu(_security); };
        // the sign of updates waiting: left the control center, right a menu to finish them
        _updateIcon.RightTapped += (_, e) => { e.Handled = true; UpdateMenu(_updateIcon); };
        _appUpdate.Children.Add(_appUpdateGlyph);
        _appUpdate.Children.Add(new Ellipse
        {
            Width = 5, Height = 5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -1, -1), IsHitTestVisible = false, Fill = ThemeBrush.Get("AccentFillColorDefaultBrush")
        });
        _appUpdate.RightTapped += (_, e) => { e.Handled = true; AppUpdateMenu(_appUpdate); };
        foreach (var e in new UIElement[] { _appUpdate, _updateIcon, _security, _privacy, _place, _dnd, _vpn, _micMute, _weather, _weatherText, _net, _btPanel, _volPanel, _batPanel }) _icons.Children.Add(e);
        if (host != null) { Button = host; host.Content = _icons; }      // the dock: its own button (its place, click and wheel)
        else
        {
            Button = new Button { Style = (Style)Application.Current.Resources["DockButton"], Padding = new Thickness(8, 0, 8, 0), Content = _icons, VerticalAlignment = VerticalAlignment.Stretch };
            Button.PointerWheelChanged += (_, e) => { e.Handled = true; Wheel(e, Button); Update(); };
        }
        // Bluetooth and the volume at once when Windows tells a change (not with the next reading of all icons); while shown
        Action bluetooth = () => Ui.Post(ShowBluetooth);
        Action volume = () => { if (_volumePosted) return; _volumePosted = true; Ui.Post(() => { _volumePosted = false; ShowVolumeNow(); }); };
        Action update = ShowAppUpdate;
        _icons.Loaded += (_, _) => { BluetoothWatch.Changed += bluetooth; AudioControl.Changed += volume; Updater.Changed += update; ShowAppUpdate(); };
        _icons.Unloaded += (_, _) => { BluetoothWatch.Changed -= bluetooth; AudioControl.Changed -= volume; Updater.Changed -= update; };
    }

    /// <summary>Along the bar (the dock at a side edge: one under another).</summary>
    public Orientation Orientation { set => _icons.Orientation = value; }

    volatile bool _volumePosted;

    /// <summary>Like iOS: the icon is bright only with a device connected, dim without one, crossed out while off.</summary>
    public void ShowBluetooth()
    {
        _btPanel.Visibility = V(BluetoothWatch.Present && S.ShowBluetooth);
        bool linked = BluetoothWatch.On && BluetoothWatch.Connected.Count > 0;
        _bt.Opacity = !BluetoothWatch.On ? 0.5 : linked ? 1 : 0.6;
        _btOff.Visibility = V(!BluetoothWatch.On);
        _btLeft.Visibility = _btRight.Visibility = V(linked);
        // the top bar can show the charge of the connected devices next to the icon
        var charges = linked && _topBar && S.ShowBluetoothBattery
            ? BluetoothWatch.Connected.Where(BluetoothWatch.Battery.ContainsKey).Select(n => BluetoothWatch.Battery[n] + " %").ToList() : new List<string>();
        _btText.Text = string.Join(" · ", charges);
        _btText.Visibility = V(charges.Count > 0);
    }

    public void ShowVolumeNow()
    {
        if (AudioControl.Now() is not { } v) return;
        _vol.Glyph = AudioControl.Glyph(v.Level, v.Mute);
        _volText.Text = ((int)Math.Round(v.Level * 100)).ToString();
    }

    /// <summary>The wheel over the status icons sets the volume (by 2 %).</summary>
    public static void Wheel(PointerRoutedEventArgs e, UIElement on)
    {
        if (AudioControl.Now() is not { } v) return;
        AudioControl.SetLevel(Math.Clamp(v.Level + (e.GetCurrentPoint(on).Properties.MouseWheelDelta > 0 ? 0.02f : -0.02f), 0f, 1f));
    }

    static Visibility V(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The sign of a new version of HeMiDock (found on GitHub) with its tooltip.</summary>
    void ShowAppUpdate()
    {
        _appUpdateGlyph.FontSize = S.StatusIconSize;
        var release = Updater.Available;
        _appUpdate.Visibility = V(release != null);
        if (release != null) ToolTipService.SetToolTip(_appUpdate, L.T("HeMiDock {0} is available.", release.Version.ToString(3)) + "\n" + L.T("Right click: install"));
    }

    /// <summary>The menu of the sign of a new version: install it (or download it, portable), see what is new, skip it.</summary>
    static void AppUpdateMenu(FrameworkElement at)
    {
        if (Updater.Available is not { } release) return;
        var menu = new MenuFlyout();
        void Add(string text, Action click, bool bold = false)
        {
            var item = new MenuFlyoutItem { Text = text, FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
        }
        string v = release.Version.ToString(3);
        Add(L.T(Updater.Installed ? "Install HeMiDock {0}" : "Download HeMiDock {0}", v), () => _ = Updater.InstallAsync(release), bold: true);
        Add(L.T("What's new"), () => Fg.Open(release.Page));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(L.T("Skip this version"), Updater.Skip);
        menu.ShowAt(at, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Auto });
    }

    /// <summary>The menu of the sign of waiting updates: finish them with a restart or a shutdown, or open Windows Update.</summary>
    static void UpdateMenu(FrameworkElement at)
    {
        var menu = new MenuFlyout();
        void Add(string text, Action click, bool bold = false)
        {
            var item = new MenuFlyoutItem { Text = L.T(text), FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
        }
        Add(SystemPower.UpdateRestart, SystemPower.UpdateAndRestart, bold: true);
        Add(SystemPower.UpdateShutDown, () => SystemPower.ShutDown(restart: false, update: true));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Open Windows Update", () => Fg.Open("ms-settings:windowsupdate"));
        menu.ShowAt(at, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Auto });
    }

    /// <summary>
    /// The shield while Defender is on (and the switch is on): the state and the tooltip of the icon of Windows Security,
    /// which is left out of the notification area then.
    /// </summary>
    void ShowSecurity(bool on)
    {
        var item = on ? ShellHost.SecurityIcon() : null;
        _security.Visibility = V(item != null);
        if (item == null) return;
        int state = SecurityStatus.StateOf(item.Picture);
        _shieldDot.Visibility = V(state > 0);
        _shieldDot.Fill = ThemeBrush.Get(state == 2 ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush");
        ToolTipService.SetToolTip(_security, item.Title);
    }

    /// <summary>Read in the background (see <see cref="StatusData"/>), only shown here.</summary>
    public async void Update()
    {
        if (StatusData.Last is { } last) Show(last);
        if (await StatusData.ReadAsync() is { } now) Show(now);
    }

    public void Show(StatusSnapshot d)
    {
        foreach (var g in new[] { _privacy, _place, _dnd, _vpn, _micMute, _weather, _update, _shield }) g.FontSize = S.StatusIconSize;
        ShowSecurity(d.Security);
        _place.Visibility = V(d.Place.Count > 0);
        _updateIcon.Visibility = V(d.UpdateRestart);
        // the symbols of Wi-Fi, Bluetooth and the volume fill less of their square than the battery: a little larger, so they look
        // as big as the icons of the apps next to them; the cable network (a full monitor) at its size (larger, it outgrew them)
        var (glyph, connected, tip) = d.Net;
        bool wifi = tip?.StartsWith("Wi-Fi", StringComparison.OrdinalIgnoreCase) == true;
        _net.FontSize = Math.Round(S.StatusIconSize * (wifi ? 1.1 : 1.0));
        _bt.FontSize = Math.Round(S.StatusIconSize * 1.1);
        _vol.FontSize = Math.Round(S.StatusIconSize * 1.1);
        _bat.Size = S.StatusIconSize + 1;
        _btOff.Width = _btOff.Height = Math.Round(S.StatusIconSize * 0.85);   // as big as the icon, not the whole bar
        var (mic, cam) = (d.Mic, d.Cam);
        _privacy.Visibility = V(mic.Count + cam.Count > 0);
        _privacy.Glyph = mic.Count > 0 ? "" : "";
        _dnd.Visibility = V(S.ShowStatusSigns && d.Dnd);
        _vpn.Visibility = V(S.ShowStatusSigns && d.Vpn != null);
        _micMute.Visibility = V(S.ShowStatusSigns && d.MicMuted);
        _net.Glyph = glyph;
        _net.Opacity = connected ? 1 : 0.4;
        _net.Visibility = V(S.ShowWifi);
        _ = BluetoothWatch.RecheckAsync();
        ShowBluetooth();
        var tips = new List<string> { tip };
        if (d.UpdateRestart) tips.Insert(0, L.T("Updates of Windows: restart to finish them"));
        if (mic.Count > 0) tips.Add(L.T("Microphone") + ": " + string.Join(", ", mic.Distinct()));
        if (cam.Count > 0) tips.Add(L.T("Camera") + ": " + string.Join(", ", cam.Distinct()));
        if (d.Place.Count > 0) tips.Add(L.T("Location") + ": " + string.Join(", ", d.Place.Distinct()));
        if (BluetoothWatch.Connected.Count > 0) tips.Add("Bluetooth: " + string.Join(", ", BluetoothWatch.Connected.Select(BluetoothWatch.WithBattery)));
        ToolTipService.SetToolTip(Button, string.Join("\n", tips.Where(t => !string.IsNullOrEmpty(t))));
        if (d.Audio is { } v)
        {
            _vol.Glyph = AudioControl.Glyph(v.Level, v.Mute);
            _volPanel.Visibility = V(S.ShowVolume);
            _volText.Visibility = V(S.ShowVolumePercent);
            _volText.Text = ((int)Math.Round(v.Level * 100)).ToString();
        }
        if (d.Battery is { } b && S.ShowBattery)
        {
            _batPanel.Visibility = Visibility.Visible;
            _bat.Show(b.Percent, b.Plugged);
            _batText.Text = S.ShowBatteryPercent ? b.Percent + " %" : "";
        }
        else _batPanel.Visibility = Visibility.Collapsed;
        bool weather = S.WeatherOn && Weather.Text.Length > 0;
        _weather.Visibility = _weatherText.Visibility = V(weather);
        if (weather) { _weather.Glyph = Weather.Glyph; _weatherText.Text = Weather.Text; }
    }
}
