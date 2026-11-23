using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NAudio.CoreAudioApi;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;

namespace HeMiDock;

/// <summary>
/// The control center: the quick buttons in the order and choice of the settings, sound and microphone, display brightness,
/// power mode, what is playing, the battery and the apps using the microphone or camera. Its pages (Wi-Fi, Bluetooth, sound
/// devices and the volume of the apps, monitors, keep awake, cast) open inside the same window with one header and a link to
/// the settings of Windows.
/// </summary>
sealed partial class ControlCenterWindow : PopupWindow
{
    readonly Grid _host = new();
    readonly StackPanel _main = new() { Spacing = 10 };
    readonly StackPanel _page = new() { Spacing = 8 };
    readonly TextBlock _pageTitle = new() { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _pageActions = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
    readonly ContentControl _pageFooter = new() { HorizontalContentAlignment = HorizontalAlignment.Right };
    readonly Grid _pageView = new() { Visibility = Visibility.Collapsed };
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    RadioSwitch _wifi, _bt, _cell;
    Slider _volume, _mic;
    FontIcon _volumeGlyph, _micGlyph, _lastIcon;                      // the icons beside the sliders follow the state of the sound
    volatile bool _audioPosted;
    TextBlock _volumeValue, _micValue, _awakeState;
    readonly List<(AudioDevices.AppSound App, Slider Slider, TextBlock Value)> _apps = new();
    string _openPage;
    bool _updating;
    static WiFiAdapter _adapter;                                     // kept: its last scan is shown at once next time
    Action _netChanged;

    static Brush Res(string key) => ThemeBrush.Get(key);
    static Style DockButton => (Style)Application.Current.Resources["DockButton"];

    public ControlCenterWindow() : base("control")
    {
        var head = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        var back = new Button { Style = DockButton, Content = new FontIcon { Glyph = "", FontSize = 13 } };
        back.Click += (_, _) => ShowMain();
        head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { back, _pageTitle } });
        head.Children.Add(_pageActions);
        // the page takes the rest: the link to the settings of Windows at the bottom of the window (a page keeps the height
        // of the main one, and the link stood in the middle of it under a short page)
        _pageView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _pageView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _pageView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _pageFooter.VerticalAlignment = VerticalAlignment.Bottom;
        _pageView.Children.Add(head);
        Grid.SetRow(_page, 1);
        _pageView.Children.Add(_page);
        Grid.SetRow(_pageFooter, 2);
        _pageView.Children.Add(_pageFooter);
        _host.Children.Add(_focusSink);
        _host.Children.Add(_main);
        _host.Children.Add(_pageView);
        Content = new Border { Width = 372, Padding = new Thickness(14), Child = _host };

        // the volume and the microphone come by the notifications of Windows (AudioControl.Changed); only the apps of the
        // mixer (their sessions tell nothing) and the countdown of a planned shutdown are read once a second
        _poll.Tick += (_, _) =>
        {
            if (_apps.Count > 0) SyncAudio();
            if (_awakeState != null) _awakeState.Text = AwakeText();
        };
        // the network through the one StatusHub of the app (a burst of the events of Windows comes as one notice)
        _netChanged = async () => { if (_openPage == "wifi" && _adapter != null) await BuildNetworksAsync(); else if (_openPage == null) BuildMain(); };
        StatusHub.NetworkChanged += _netChanged;
        // the sound at once when Windows tells a change (keys, the dock, another app), not with the next poll
        Action audio = () => { if (_audioPosted) return; _audioPosted = true; Ui.Post(() => { _audioPosted = false; if (_visibleNow) SyncAudio(); }); };
        AudioControl.Changed += audio;
        // another song or video (or its picture came): the main page shows it
        string mediaShown = null;
        Action media = () => Ui.Post(() =>
        {
            var d = BarMedia.Details;
            string now = d == null ? null : $"{d.Title}|{d.Artist}|{d.App}|{d.Playing}|{d.Picture?.Length}|{string.Join(",", BarMedia.Players.Select(p => p.App + p.Playing + p.Shown))}";
            if (now == mediaShown) return;
            mediaShown = now;
            if (_visibleNow && _openPage == null && !_editing) BuildMain();
        });
        BarMedia.Changed += media;
        Closed += (_, _) => { _poll.Stop(); StatusHub.NetworkChanged -= _netChanged; AudioControl.Changed -= audio; BarMedia.Changed -= media; };
        _poll.Start();
        BuildMain();
        _ = LoadRadiosAsync();
        OpenRequested();
    }

    /// <summary>A widget of the top bar opens the control center at its own page (Monitors, Scheduled shutdown, Cast).</summary>
    internal static string OpenWith;

    void OpenRequested()
    {
        string id = OpenWith;
        OpenWith = null;
        switch (id)
        {
            case "display": ShowMonitors(); break;
            case "awake": ShowAwake(); break;
            case "cast": _ = ShowCastAsync(); break;
        }
    }

    protected override void Reopen()
    {
        _editing = false;
        ShowMain();
        _poll.Start();
        OpenRequested();
    }

    protected override void Hidden() { _poll.Stop(); _visibleNow = false; }

    protected override bool OnEscape()
    {
        if (_editing) { _editing = false; BuildMain(); return true; }     // Esc ends the editing first
        if (_openPage == null) return false;
        ShowMain();
        return true;
    }

    async Task LoadRadiosAsync()
    {
        _wifi = await RadioSwitch.FindAsync(RadioKind.WiFi);
        _bt = await RadioSwitch.FindAsync(RadioKind.Bluetooth);
        _cell = await RadioSwitch.FindAsync(RadioKind.MobileBroadband);   // a SIM / eSIM modem (only some notebooks and tablets)
        if (_cell != null) _cell.StateChanged += () => Ui.Post(Refresh);
        if (_wifi != null) _wifi.StateChanged += () => Ui.Post(Refresh);
        if (_bt != null) _bt.StateChanged += () => Ui.Post(Refresh);
        Refresh();
    }

    void Refresh()
    {
        if (_openPage == null) BuildMain();
        else if (_openPage == "bt") _ = BuildBluetoothAsync();
        else if (_openPage == "wifi") _ = ScanAsync();
    }

    static Border Card(UIElement child, double padding = 12) => new()
    {
        Background = Res("CardBackgroundFillColorDefaultBrush"), BorderBrush = Res("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), Padding = new Thickness(padding, 8, padding, 10), Child = child
    };

    static TextBlock Title(string text) => new() { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };

    // ───────────────────────── main page ─────────────────────────

    (string Glyph, bool Connected, string Tip)? _net;

    void BuildMain()
    {
        _net = null;
        _main.Children.Clear();
        _volume = _mic = null;
        _volumeGlyph = _micGlyph = null;

        // the microphone / camera from the last reading of the status icons (reading the registry here made the opening slow)
        var mic = (StatusData.Last?.Mic ?? new List<string>()).Distinct().ToList();
        var cam = (StatusData.Last?.Cam ?? new List<string>()).Distinct().ToList();
        var place = (StatusData.Last?.Place ?? new List<string>()).Distinct().ToList();   // each list is empty while its switch is off
        if (mic.Count + cam.Count + place.Count > 0 && !_editing)
        {
            var privacy = new StackPanel { Spacing = 4 };
            if (mic.Count > 0) privacy.Children.Add(PrivacyRow("", L.T("Microphone") + ": " + string.Join(", ", mic)));
            if (cam.Count > 0) privacy.Children.Add(PrivacyRow("", L.T("Camera") + ": " + string.Join(", ", cam)));
            if (place.Count > 0) privacy.Children.Add(PrivacyRow("", L.T("Location") + ": " + string.Join(", ", place)));
            var card = Card(privacy);
            card.Tapped += (_, _) => Open(mic.Count + cam.Count == 0 ? "ms-settings:privacy-location" : "ms-settings:privacy-microphone");
            _main.Children.Add(card);
        }

        var tiles = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        tiles.ColumnDefinitions.Add(new ColumnDefinition());
        tiles.ColumnDefinitions.Add(new ColumnDefinition());
        int n = 0;
        // while editing the hidden ones are there too (dimmed), so they can be added back
        foreach (var id in QuickLayout.TileOrder.Where(id => (QuickLayout.IsShown(id) || _editing) && !QuickLayout.OnBar(id)))
        {
            if (Tile(id) is not { } tile) continue;
            var item = _editing ? Editable(tile, id, tile: true) : tile;
            Grid.SetColumn(item, n % 2);
            if (n % 2 == 0) tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(item, n / 2);
            tiles.Children.Add(item);
            n++;
        }
        // every tile as high as the highest one (a long name goes on two lines – the others grow with it, all alike)
        tiles.Loaded += (_, _) =>
        {
            double high = tiles.Children.OfType<FrameworkElement>().Select(c => c.ActualHeight).DefaultIfEmpty(0).Max();
            if (high > 0) foreach (var r in tiles.RowDefinitions) r.Height = new GridLength(high);
        };

        FrameworkElement Part(FrameworkElement card, string id) => _editing ? Editable(card, id, tile: false) : card;
        foreach (var id in QuickLayout.SectionOrder.Where(id => (QuickLayout.IsShown(id) || _editing) && !QuickLayout.OnBar(id)))
            switch (id)
            {
                case "tiles": if (n > 0) _main.Children.Add(tiles); break;
                case "sound": _main.Children.Add(Part(SoundCard(), id)); break;
                case "brightness":
                    var holder = new Border();                                          // its place in the order; filled when the brightness is known
                    _main.Children.Add(holder);
                    _ = AddBrightnessAsync(holder);
                    break;
                case "power": if (PowerMode.Current != null) _main.Children.Add(Part(PowerCard(), id)); break;
                case "battery": if (StatusInfo.Battery() is { } b) _main.Children.Add(Part(BatteryCard(b), id)); break;
                case "media":
                    if (BarMedia.Now.Title is { Length: > 0 }) _main.Children.Add(Part(MediaCard(), id));
                    else if (_editing) _main.Children.Add(Part(Card(Title(L.T("Now playing"))), id));   // nothing plays: an empty part to move / hide
                    break;
            }

        // the pencil: editing right here (drag to move, − hides, + adds)
        var footer = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        var editContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = _editing ? "\uE73E" : "\uE70F", FontSize = 14 } } };
        if (_editing) editContent.Children.Add(new TextBlock { Text = L.T("Done"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var edit = new Button { Style = DockButton, Padding = new Thickness(8, 6, 8, 6), Content = editContent };
        if (_editing) { edit.Background = Res("AccentFillColorDefaultBrush"); edit.Foreground = Res("TextOnAccentFillColorPrimaryBrush"); }
        ToolTipService.SetToolTip(edit, L.T("Edit the control center"));
        edit.Click += (_, _) => { _editing = !_editing; BuildMain(); };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { edit } };
        if (_editing) left.Children.Add(new TextBlock { Text = L.T("Drag to move, − hides, + adds"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap, MaxWidth = 250 });
        footer.Children.Add(left);
        if (!_editing) footer.Children.Add(SettingsLink("Windows Settings", "ms-settings:"));
        _main.Children.Add(footer);
        SyncAudio();
    }

    bool _editing;
    string _dragId;

    /// <summary>
    /// A quick button or a part while editing: it does nothing when clicked, a round − hides it (+ adds a hidden one back),
    /// dragging puts it in the place of another one of the same kind; with the top bar on, dragged onto the bar it becomes
    /// its widget.
    /// </summary>
    FrameworkElement Editable(FrameworkElement content, string id, bool tile)
    {
        bool shown = QuickLayout.IsShown(id);
        content.IsHitTestVisible = false;
        content.Opacity = shown ? 1 : 0.4;
        var badge = new Button
        {
            Width = 22, Height = 22, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -7, -7, 0),
            Background = Res(shown ? "SystemFillColorCriticalBrush" : "AccentFillColorDefaultBrush"),
            Content = new FontIcon { Glyph = shown ? "\uE738" : "\uE710", FontSize = 10, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) }
        };
        badge.Click += (_, _) => { QuickLayout.SetShown(id, !shown); BuildMain(); };
        var slot = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), CanDrag = true, AllowDrop = true, Children = { content, badge } };
        slot.DragStarting += (_, e) => { _dragId = id; e.Data.SetText(id); e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move; };
        slot.DragOver += (_, e) =>
        {
            if (_dragId == null || _dragId == id || IsTile(_dragId) != tile) return;
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            e.DragUIOverride.IsCaptionVisible = false;
        };
        slot.Drop += (_, _) =>
        {
            string moved = _dragId;
            _dragId = null;
            if (moved == null || moved == id || IsTile(moved) != tile) return;
            var order = tile ? QuickLayout.TileOrder : QuickLayout.SectionOrder;
            order.Remove(moved);
            order.Insert(order.IndexOf(id), moved);                               // in the place of the one it was dropped on
            if (tile) DockSettings.Current.QuickTiles = string.Join("|", order);
            else DockSettings.Current.QuickSections = string.Join("|", order);
            BuildMain();
        };
        slot.DropCompleted += (_, _) =>
        {
            // dropped on the top bar: it goes there
            if (_dragId == id && DockSettings.Current.TopBarOn && QuickLayout.Movable.Contains(id) && Native.GetCursorPos(out var p) && p.Y <= Monitors.Dock().Work.Top + 4)
            {
                QuickLayout.SetOnBar(id, true);
                BuildMain();
            }
            _dragId = null;
        };
        return slot;
    }

    static bool IsTile(string id) => QuickLayout.Tiles.Any(t => t.Id == id);

    static FrameworkElement PrivacyRow(string glyph, string text) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 10,
        Children = { new FontIcon { Glyph = glyph, FontSize = 15, Foreground = Res("SystemFillColorCautionBrush") }, new TextBlock { Text = text, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis } }
    };

    Button SettingsLink(string text, string uri)
    {
        var b = new Button { Style = DockButton, HorizontalAlignment = HorizontalAlignment.Right, Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = L.T(text), FontSize = 12, Foreground = Res("TextFillColorSecondaryBrush") }, new FontIcon { Glyph = "", FontSize = 14 } } } };
        b.Click += (_, _) => Open(uri);
        return b;
    }

    /// <summary>A quick button: a round symbol (accent while on), the name and the state under it; some open their page by the arrow.</summary>
    FrameworkElement Tile(string id)
    {
        var net = _net ??= StatusInfo.Network();                        // once for all the tiles of one opening (each asked it again)
        (string Glyph, string Name, string State, bool On, Action Click, string Page)? t = id switch
        {
            "net" => _wifi != null
                ? ("", "Wi-Fi", _wifi.State == RadioState.On ? (net.Connected ? net.Tip.Replace("Wi-Fi: ", "") : L.T("Not connected")) : L.T("Off"), _wifi.State == RadioState.On, async () => { await _wifi.SetStateAsync(_wifi.State == RadioState.On ? RadioState.Off : RadioState.On); }, "wifi")
                : (net.Glyph, L.T("Network"), net.Connected ? net.Tip : L.T("Not connected"), net.Connected, () => Open("ms-settings:network"), null),
            "bt" when _bt != null && DockSettings.Current.ShowBluetooth => ("", L.T("Bluetooth"), _bt.State != RadioState.On ? L.T("Off") : MainBluetoothNames() ?? L.T("On"), _bt.State == RadioState.On, async () => { await _bt.SetStateAsync(_bt.State == RadioState.On ? RadioState.Off : RadioState.On); }, "bt"),
            // mobile data of a SIM / eSIM: on / off; its settings (operator, eSIM profiles) by the right button
            "cell" when _cell != null => ("", L.T("Mobile data"), L.T(_cell.State == RadioState.On ? "On" : "Off"), _cell.State == RadioState.On,
                async () => { await _cell.SetStateAsync(_cell.State == RadioState.On ? RadioState.Off : RadioState.On); }, null),
            "airplane" when (_wifi != null || _bt != null || _cell != null) && AirplaneMode.IsOn is bool a => ("", L.T("Airplane mode"), L.T(a ? "On" : "Off"), a, () => { AirplaneMode.Set(!a); BuildMain(); }, null),
            "dnd" => ("", L.T("Do not disturb"), L.T(Dnd.IsOn ? "On" : "Off"), Dnd.IsOn, () => { Dnd.Set(!Dnd.IsOn); BuildMain(); }, null),
            "night" => ("", L.T("Night light"), L.T("Settings"), false, () => Open("ms-settings:nightlight"), null),
            "cast" => ("", L.T("Cast"), L.T("Wireless display"), false, () => _ = ShowCastAsync(), null),
            "access" => ("", L.T("Accessibility"), L.T("Settings"), false, () => Open("ms-settings:easeofaccess"), null),
            "vpn" => ("", "VPN", Vpn.Active() ?? (Vpn.Profiles().Count > 0 ? L.T("Not connected") : L.T("Settings")), Vpn.Active() != null, () => { if (Vpn.Active() == null && Vpn.Profiles().Count == 0) { CloseNow(); Vpn.Toggle(); } else { Vpn.Toggle(); BuildMain(); } }, null),
            "display" => ("", L.T("Monitors"), L.T("Settings"), false, ShowMonitors, null),
            "awake" => ("", L.T("Scheduled shutdown"), AwakeText(), KeepAwake.IsOn || KeepAwake.ShutdownAt != null, ShowAwake, null),
            _ => null
        };
        if (t is not { } tile) return null;
        var circle = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = tile.On ? Res("AccentFillColorDefaultBrush") : Res("ControlAltFillColorSecondaryBrush"),
            Child = new FontIcon { Glyph = tile.Glyph, FontSize = 14, Foreground = tile.On ? Res("TextOnAccentFillColorPrimaryBrush") : Res("TextFillColorPrimaryBrush") }
        };
        var state = new TextBlock { Text = tile.State, FontSize = 11, Foreground = Res("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
        if (id == "awake") _awakeState = state;
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new TextBlock { Text = tile.Name, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.WrapWholeWords, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis }, state
        } };
        var row = new Grid { ColumnSpacing = 10, ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        row.Children.Add(circle);
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        if (tile.Page != null)
        {
            var arrow = new Button { Style = DockButton, Padding = new Thickness(6, 4, 6, 4), Content = new FontIcon { Glyph = "", FontSize = 10 } };
            arrow.Click += (_, _) => { if (tile.Page == "wifi") ShowWifi(); else ShowBluetooth(); };
            Grid.SetColumn(arrow, 2);
            row.Children.Add(arrow);
        }
        // 10 on the right: with 6 the arrow of Wi-Fi / Bluetooth was cut off at 125 % (the user, 2026-11-23)
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 8, 10, 8), Content = row };
        button.Click += (_, _) => tile.Click();
        if (id == "net") button.RightTapped += (_, e) => { e.Handled = true; Open(_wifi != null ? "ms-settings:network-wifi" : "ms-settings:network"); };
        if (id == "bt") button.RightTapped += (_, e) => { e.Handled = true; Open("ms-settings:bluetooth"); };
        if (id == "cell") { button.RightTapped += (_, e) => { e.Handled = true; Open("ms-settings:network-cellular"); }; ToolTipService.SetToolTip(button, L.T("Right click: SIM and eSIM settings")); }
        return button;
    }

    static string MainBluetoothNames() => DockWindow.ConnectedBluetooth.Count > 0 ? string.Join(", ", DockWindow.ConnectedBluetooth.Select(n => BluetoothWatch.Battery.TryGetValue(n, out int p) ? $"{p} % · {n}" : n)) : null;   // the charge first: a long name is cut off on the tile

    void Open(string uri)
    {
        CloseNow();
        Fg.Open(uri);
    }
}
