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
sealed class ControlCenterWindow : PopupWindow
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
    NetworkStatusChangedEventHandler _netChanged;

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
        _netChanged = _ => Ui.Post(async () => { if (_openPage == "wifi" && _adapter != null) await BuildNetworksAsync(); else if (_openPage == null) BuildMain(); });
        NetworkInformation.NetworkStatusChanged += _netChanged;
        // the sound at once when Windows tells a change (keys, the dock, another app), not with the next poll
        Action audio = () => { if (_audioPosted) return; _audioPosted = true; Ui.Post(() => { _audioPosted = false; if (_visibleNow) SyncAudio(); }); };
        AudioControl.Changed += audio;
        // another song or video (or its picture came): the main page shows it
        string mediaShown = null;
        Action media = () => Ui.Post(() =>
        {
            var d = BarMedia.Details;
            string now = d == null ? null : $"{d.Title}|{d.Artist}|{d.App}|{d.Playing}|{d.Picture?.Length}";
            if (now == mediaShown) return;
            mediaShown = now;
            if (_visibleNow && _openPage == null && !_editing) BuildMain();
        });
        BarMedia.Changed += media;
        Closed += (_, _) => { _poll.Stop(); NetworkInformation.NetworkStatusChanged -= _netChanged; AudioControl.Changed -= audio; BarMedia.Changed -= media; };
        _poll.Start();
        BuildMain();
        _ = LoadRadiosAsync();
        OpenRequested();
    }

    /// <summary>A widget of the top bar opens the control center at its own page (Monitors, Keep awake, Cast).</summary>
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
            "awake" => ("", L.T("Keep awake"), AwakeText(), KeepAwake.IsOn || KeepAwake.ShutdownAt != null, ShowAwake, null),
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
            new TextBlock { Text = tile.Name, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }, state
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
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 8, 6, 8), Content = row };
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

    // ───────────────────────── pages ─────────────────────────

    /// <summary>
    /// A page in place of the main one, in the same window: it keeps at least the height of the main page (the window does
    /// not jump as if another one opened) and slides in from the side.
    /// </summary>
    void ShowPage(string id, string title)
    {
        bool fromMain = _openPage == null && _main.Visibility == Visibility.Visible;
        if (fromMain && _main.ActualHeight > 0) _pageView.MinHeight = _main.ActualHeight;
        _openPage = id;
        _pageTitle.Text = L.T(title);
        _page.Children.Clear();
        _pageActions.Children.Clear();
        _pageFooter.Content = null;
        _main.Visibility = Visibility.Collapsed;
        _pageView.Visibility = Visibility.Visible;
        if (fromMain) Motion.Slide(_pageView, forward: true);
    }

    void ShowMain()
    {
        bool fromPage = _openPage != null;
        _openPage = null;
        _apps.Clear();
        _pageView.Visibility = Visibility.Collapsed;
        _pageView.MinHeight = 0;
        _main.Visibility = Visibility.Visible;
        BuildMain();
        if (fromPage && _visibleNow) Motion.Slide(_main, forward: false);
    }

    bool _visibleNow;
    protected override void Shown() => _visibleNow = true;

    void Footer(string text, string uri) => _pageFooter.Content = SettingsLink(text, uri);

    StackPanel PageCard(string title = null)
    {
        if (title != null) _page.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(4, _page.Children.Count == 0 ? 0 : 6, 0, 0) });
        var rows = new StackPanel { Spacing = 1 };
        _page.Children.Add(Card(rows, 4));
        return rows;
    }

    /// <summary>A row of a card: the chosen one has an accent symbol and a check mark.</summary>
    static Button PageRow(string glyph, string text, bool on, Action click, string sub = null)
    {
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = L.T(text), FontSize = 12.5, FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
        if (sub != null) texts.Children.Add(new TextBlock { Text = L.T(sub), FontSize = 11, Foreground = Res("TextFillColorSecondaryBrush") });
        var grid = new Grid { ColumnSpacing = 10, ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(26) }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 15, Foreground = on ? Res("AccentTextFillColorPrimaryBrush") : Res("TextFillColorPrimaryBrush") });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        if (on)
        {
            var check = new FontIcon { Glyph = "", FontSize = 12, Foreground = Res("AccentTextFillColorPrimaryBrush") };
            Grid.SetColumn(check, 2);
            grid.Children.Add(check);
        }
        var b = new Button { Style = DockButton, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 7, 8, 7), Content = grid };
        if (click != null) b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A turning ring with what is being looked for, while Windows searches.</summary>
    void Searching(string text) => _page.Children.Add(new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(6, 6, 6, 10),
        Children =
        {
            new ProgressRing { IsActive = true, Width = 18, Height = 18 },
            new TextBlock { Text = L.T(text), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("TextFillColorSecondaryBrush") }
        }
    });

    void Note(string text) => _page.Children.Add(new TextBlock { Text = L.T(text), Margin = new Thickness(6, 0, 6, 6), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextFillColorSecondaryBrush") });

    ToggleSwitch HeaderSwitch(bool on, Action<bool> set)
    {
        // only the switch itself: the control has room for its text after it, which pushed it out of the window (cut off)
        var s = new ToggleSwitch { IsOn = on, OnContent = null, OffContent = null, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        s.Toggled += (_, _) => set(s.IsOn);
        _pageActions.Children.Add(new Grid { Width = 42, VerticalAlignment = VerticalAlignment.Center, Children = { s } });
        return s;
    }

    // ───────────────────────── Wi-Fi ─────────────────────────

    readonly StackPanel _networks = new() { Spacing = 1 };
    readonly TextBlock _wifiStatus = new() { FontSize = 12, Margin = new Thickness(8, 0, 8, 4), TextWrapping = TextWrapping.Wrap };

    void ShowWifi()
    {
        ShowPage("wifi", "Wi-Fi");
        var refresh = new Button { Style = DockButton, Content = new FontIcon { Glyph = "", FontSize = 13 } };
        ToolTipService.SetToolTip(refresh, L.T("Scan again"));
        refresh.Click += (_, _) => _ = ScanAsync();
        _pageActions.Children.Add(refresh);
        HeaderSwitch(_wifi?.State == RadioState.On, async on => { if (_wifi != null) { await _wifi.SetStateAsync(on ? RadioState.On : RadioState.Off); await ScanAsync(); } });
        _wifiStatus.Foreground = Res("TextFillColorSecondaryBrush");
        _page.Children.Add(_wifiStatus);
        _page.Children.Add(Card(new ScrollViewer { MaxHeight = 340, Content = _networks }, 4));
        Footer("More Wi-Fi settings", "ms-settings:network-wifi");
        _ = ScanAsync();
    }

    Task _scanning;
    static bool _adapterWatched;

    /// <summary>
    /// The Wi-Fi adapter, asked for once – only when the Wi-Fi page is opened: asking for it can bring up a message of Windows
    /// (location turned off by the administrator), which must not come by itself.
    /// </summary>
    async Task<bool> EnsureAdapterAsync()
    {
        if (_adapter != null) return true;
        if (await WiFiAdapter.RequestAccessAsync() != WiFiAccessStatus.Allowed)
        {
            _wifiStatus.Text = L.T("Windows does not allow scanning for Wi-Fi networks. Turn on location access for desktop apps (Settings → Privacy & security → Location), or use More Wi-Fi settings.");
            return false;
        }
        _adapter ??= (await WiFiAdapter.FindAllAdaptersAsync()).FirstOrDefault();
        if (_adapter == null) { _wifiStatus.Text = L.T("No Wi-Fi adapter found."); return false; }
        if (!_adapterWatched)
        {
            _adapterWatched = true;
            // the networks as Windows finds them (also its own scans): the list fills while the scan still runs
            _adapter.AvailableNetworksChanged += (_, _) => Ui.Post(async () => { if (_openPage == "wifi") await BuildNetworksAsync(byWindows: true); });
        }
        return true;
    }

    /// <summary>Scans once at a time: asked again while it runs (the switch, a change of the network) it waits for the same scan.</summary>
    Task ScanAsync() => _scanning is { IsCompleted: false } ? _scanning : _scanning = DoScanAsync();

    async Task DoScanAsync()
    {
        if (_wifi?.State != RadioState.On) { _networks.Children.Clear(); _wifiStatus.Text = L.T("Wi-Fi is turned off."); return; }
        _wifiStatus.Text = L.T("Searching for networks…");
        try
        {
#if DEBUG
            if (Simulation.Wifi) { await BuildNetworksAsync(); return; }
#endif
            if (!await EnsureAdapterAsync()) return;
            if (_adapter.NetworkReport.AvailableNetworks.Count > 0)
            {
                await BuildNetworksAsync();                            // the last scan at once (the list stays), the new one replaces it
                _wifiStatus.Text = L.T("Searching for networks…");
            }
            // a scan can take long or hang with some drivers: after a few seconds the networks found so far
            var scan = _adapter.ScanAsync().AsTask();
            await Task.WhenAny(scan, Task.Delay(6000));
            if (_openPage == "wifi") await BuildNetworksAsync();
        }
        catch (Exception ex)
        {
            Log.Write("Wi-Fi scan", ex);
            if (_networks.Children.Count == 0) _wifiStatus.Text = L.T("Wi-Fi networks could not be scanned. Location access may be turned off for desktop apps.");
        }
    }
    sealed record WifiNetwork(string Ssid, byte Bars, bool Secured, WiFiAvailableNetwork Real, string Details);

    /// <summary>The list of networks; one refreshed by Windows is skipped while a network is unfolded (a key being typed).</summary>
    async Task BuildNetworksAsync(bool byWindows = false)
    {
        if (byWindows && _networks.Children.OfType<StackPanel>().Any(r => r.Children.Count > 1 && r.Children[1].Visibility == Visibility.Visible)) return;
        string connected = null;
        IEnumerable<WifiNetwork> available;
#if DEBUG
        if (Simulation.Wifi)
        {
            connected = Simulation.WifiConnected;
            available = Simulation.WifiNetworks.Select(n => new WifiNetwork(n.Ssid, n.Bars, n.Secured, null, n.Secured ? "WPA2-Personal" : L.T("open")));
        }
        else
#endif
        {
            try { connected = (await _adapter.NetworkAdapter.GetConnectedProfileAsync())?.ProfileName; } catch { }
            available = _adapter.NetworkReport.AvailableNetworks.Where(n => !string.IsNullOrEmpty(n.Ssid)).GroupBy(n => n.Ssid)
                .Select(g => g.OrderByDescending(n => n.NetworkRssiInDecibelMilliwatts).First())
                .Select(n => new WifiNetwork(n.Ssid, n.SignalBars, IsSecured(n), n, Describe(n)));
        }
        var networks = available.OrderByDescending(n => n.Ssid == connected).ThenByDescending(n => n.Bars).ToList();
        var known = KnownNetworks();
        _wifiStatus.Text = networks.Count == 0 ? L.T("No networks found.") : connected != null ? L.T("Connected to {0}", connected) : L.T("Not connected");
        _networks.Children.Clear();
        foreach (var n in networks) _networks.Children.Add(NetworkRow(n, n.Ssid == connected, known.Contains(n.Ssid)));
        Place();
    }

    static bool IsSecured(WiFiAvailableNetwork n) => n.SecuritySettings.NetworkAuthenticationType is not (NetworkAuthenticationType.Open80211 or NetworkAuthenticationType.None);

    static string SignalGlyph(byte bars) => bars <= 1 ? "" : bars == 2 ? "" : bars == 3 ? "" : "";

    /// <summary>"Wi-Fi 6, WPA3-Personal": the standard by the radio of the network and its security.</summary>
    static string Describe(WiFiAvailableNetwork n)
    {
        string phy = (int)n.PhyKind switch
        {
            11 => "Wi-Fi 7", 10 => n.ChannelCenterFrequencyInKilohertz > 5925000 ? "Wi-Fi 6E" : "Wi-Fi 6", 8 => "Wi-Fi 5", 7 => "Wi-Fi 4",
            6 => "802.11g", 5 => "802.11b", 4 => "802.11a", _ => null
        };
        string security = n.SecuritySettings.NetworkAuthenticationType.ToString() switch
        {
            "RsnaPsk" => "WPA2-Personal", "Rsna" => "WPA2-Enterprise", "WpaPsk" => "WPA-Personal", "Wpa" => "WPA-Enterprise", "Wpa3Sae" => "WPA3-Personal",
            "Wpa3" or "Wpa3Enterprise" or "Wpa3Enterprise192Bits" => "WPA3-Enterprise", "Owe" => "Enhanced Open", "SharedKey80211" => "WEP", _ => L.T("open")
        };
        return phy == null ? security : phy + ", " + security;
    }

    /// <summary>Networks Windows has a saved profile of: they connect without asking for the key.</summary>
    static HashSet<string> KnownNetworks()
    {
        try { return NetworkInformation.GetConnectionProfiles().Where(p => p.IsWlanConnectionProfile).Select(p => p.ProfileName).ToHashSet(); }
        catch { return new HashSet<string>(); }
    }

    /// <summary>A network; clicking it unfolds Connect (with the key when Windows has none saved) or Disconnect.</summary>
    FrameworkElement NetworkRow(WifiNetwork n, bool connected, bool known)
    {
        var details = new StackPanel { Margin = new Thickness(36, 6, 6, 6), Spacing = 6, Visibility = Visibility.Collapsed };
        var message = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextFillColorSecondaryBrush"), Visibility = Visibility.Collapsed };
        var header = PageRow(SignalGlyph(n.Bars), n.Ssid, connected, () => details.Visibility = details.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible,
            (connected ? L.T("Connected") + ", " : "") + n.Details);
        if (connected)
        {
            var disconnect = new Button { Content = L.T("Disconnect"), HorizontalAlignment = HorizontalAlignment.Right };
            disconnect.Click += async (_, _) =>
            {
#if DEBUG
                if (Simulation.Wifi) Simulation.WifiConnected = null;
                else
#endif
                try { _adapter.Disconnect(); } catch (Exception ex) { Log.Write("Wi-Fi disconnect", ex); }
                await Task.Delay(800);
                await BuildNetworksAsync();
            };
            details.Children.Add(disconnect);
        }
        else
        {
            PasswordBox password = null;
            TextBlock keyLabel = null;
            if (n.Secured)
            {
                keyLabel = new TextBlock { Text = L.T("Network security key"), FontSize = 12, Foreground = Res("TextFillColorSecondaryBrush") };
                password = new PasswordBox();
                if (known) keyLabel.Visibility = password.Visibility = Visibility.Collapsed;   // saved in Windows: asked only when it no longer works
                details.Children.Add(keyLabel);
                details.Children.Add(password);
            }
            var auto = new CheckBox { Content = L.T("Connect automatically"), IsChecked = true };
            var connect = new Button { Content = L.T("Connect"), Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right };
            var bottom = new Grid { Children = { auto, connect } };
            details.Children.Add(bottom);
            async void DoConnect()
            {
                connect.IsEnabled = false;
                message.Visibility = Visibility.Visible;
                message.Text = L.T("Connecting…");
                try
                {
#if DEBUG
                    if (n.Real == null)                                 // simulated: the same messages as with a real network
                    {
                        await Task.Delay(400);
                        if (n.Secured && password?.Password != Simulation.WifiPassword) { message.Text = L.T("The security key is not correct."); connect.IsEnabled = true; return; }
                        Simulation.WifiConnected = n.Ssid;
                        await BuildNetworksAsync();
                        return;
                    }
#endif
                    var kind = auto.IsChecked == true ? WiFiReconnectionKind.Automatic : WiFiReconnectionKind.Manual;
                    var result = password != null && password.Password.Length > 0
                        ? await _adapter.ConnectAsync(n.Real, kind, new PasswordCredential { Password = password.Password })
                        : await _adapter.ConnectAsync(n.Real, kind);
                    bool ok = result.ConnectionStatus == WiFiConnectionStatus.Success;
                    // refused without a clear reason (some versions of Windows do so for apps): the same through the tool of Windows
                    if (!ok && result.ConnectionStatus is WiFiConnectionStatus.AccessRevoked or WiFiConnectionStatus.UnspecifiedFailure)
                        ok = await WifiFallback.ConnectAsync(n.Real, password?.Password, auto.IsChecked == true);
                    switch (ok ? WiFiConnectionStatus.Success : result.ConnectionStatus)
                    {
                        case WiFiConnectionStatus.Success: await BuildNetworksAsync(); return;
                        case WiFiConnectionStatus.InvalidCredential when password != null && password.Visibility != Visibility.Visible:
                            keyLabel.Visibility = password.Visibility = Visibility.Visible;
                            message.Text = L.T("The saved key no longer works. Enter the network security key.");
                            break;
                        case WiFiConnectionStatus.InvalidCredential:
                            message.Text = (password?.Password.Length ?? 0) == 0 ? L.T("Enter the network security key.") : L.T("The security key is not correct.");
                            break;
                        case WiFiConnectionStatus.Timeout: message.Text = L.T("The network did not respond. Try again."); break;
                        case WiFiConnectionStatus.NetworkNotAvailable: message.Text = L.T("The network is no longer available."); break;
                        default: message.Text = L.T("Could not connect ({0}).", result.ConnectionStatus); break;
                    }
                }
                catch (Exception ex) { Log.Write("Wi-Fi connect", ex); message.Text = L.T("Could not connect to this network."); }
                connect.IsEnabled = true;
            }
            connect.Click += (_, _) => DoConnect();
            if (password != null) password.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; DoConnect(); } };
        }
        details.Children.Add(message);
        return new StackPanel { Children = { header, details } };
    }

    // ───────────────────────── Bluetooth ─────────────────────────

    void ShowBluetooth()
    {
        ShowPage("bt", "Bluetooth");
        _ = BuildBluetoothAsync();
    }

    int _btBuild;

    async Task BuildBluetoothAsync()
    {
        int build = ++_btBuild;                                          // a newer listing wins over a slower older one
        _bt ??= await RadioSwitch.FindAsync(RadioKind.Bluetooth);        // the radio may not have been known yet: the switch is always there
        if (build != _btBuild || _openPage != "bt") return;
        _pageActions.Children.Clear();
        bool on = _bt?.State == RadioState.On;
        HeaderSwitch(on, async v =>
        {
            if (_bt == null) { Open("ms-settings:bluetooth"); return; }
            await _bt.SetStateAsync(v ? RadioState.On : RadioState.Off);
            await BuildBluetoothAsync();
        });
        Footer("More Bluetooth settings", "ms-settings:bluetooth");
        if (!on)
        {
            _page.Children.Clear();
            Note(_bt == null ? "No Bluetooth adapter found." : "Bluetooth is turned off.");
            return;
        }
        if (_page.Children.Count == 0) Searching("Searching for devices…");   // the list of Windows takes a moment
        var devices = new List<(string Id, string Name, bool Connected, int? Battery)>();
        try
        {
#if DEBUG
            if (Simulation.Bluetooth) devices.AddRange(Simulation.BluetoothDevices.Select(d => (d.Name, d.Name, d.Connected, d.Connected ? (int?)80 : null)));
            else
#endif
            {
                // the selectors of Bluetooth describe association endpoints: without that kind Windows finds nothing
                var kind = Windows.Devices.Enumeration.DeviceInformationKind.AssociationEndpoint;
                var paired = new List<Windows.Devices.Enumeration.DeviceInformation>();
                foreach (var s in new[] { Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true), Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromPairingState(true) })
                    paired.AddRange(await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(s, new[] { ContainerKey }, kind));
                var connected = new HashSet<string>();
                foreach (var s in new[] { Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromConnectionStatus(Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected), Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected) })
                    foreach (var d in await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(s, null, kind)) connected.Add(d.Id);
                foreach (var d in paired.Where(d => !string.IsNullOrWhiteSpace(d.Name)).GroupBy(d => d.Name).Select(g => g.OrderByDescending(d => connected.Contains(d.Id)).First()))
                {
                    bool linked = connected.Contains(d.Id);
                    devices.Add((d.Id, d.Name, linked, linked ? await BatteryOf(d) : null));
                }
            }
        }
        catch (Exception ex) { Log.Write("Bluetooth list", ex); }
        if (_openPage != "bt" || build != _btBuild) return;
        _page.Children.Clear();
        if (devices.Count == 0) { Note("No paired devices."); return; }
        var card = PageCard();
        foreach (var d in devices.OrderByDescending(d => d.Connected).ThenBy(d => d.Name))
            card.Children.Add(PageRow("", d.Name, d.Connected, () => ConnectBluetooth(d.Id, d.Connected),
                L.T(d.Connected ? "Connected" : "Paired") + (d.Battery is { } percent ? $" · {L.T("Battery")} {percent} %" : "")));
        Place();
    }

    const string ContainerKey = BluetoothWatch.ContainerKey;
    static Task<int?> BatteryOf(Windows.Devices.Enumeration.DeviceInformation endpoint) => BluetoothWatch.BatteryOf(endpoint);

    /// <summary>
    /// Asks a paired device for its services, which makes Windows connect to it. Windows has no general call for connecting,
    /// so what does not connect this way, and a device already connected, goes to its settings.
    /// </summary>
    async void ConnectBluetooth(string id, bool connected)
    {
        if (!connected)
        {
            try
            {
#if DEBUG
                if (Simulation.Bluetooth)
                {
                    int at = Simulation.BluetoothDevices.FindIndex(d => d.Name == id);
                    Simulation.BluetoothDevices[at] = (id, true);
                    await BuildBluetoothAsync();
                    return;
                }
#endif
                using var device = await Windows.Devices.Bluetooth.BluetoothDevice.FromIdAsync(id);
                if (device != null)
                {
                    await device.GetRfcommServicesAsync(Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached);
                    if (device.ConnectionStatus == Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected) { await BuildBluetoothAsync(); return; }
                }
            }
            catch (Exception ex) { Log.Write("Bluetooth connect", ex); }
        }
        Open("ms-settings:bluetooth");
    }

    // ───────────────────────── sound ─────────────────────────

    /// <summary>The size of the arrow and the settings icon of the sound card.</summary>
    const double SoundIcons = 14;

    FrameworkElement SoundCard()
    {
        var panel = new StackPanel { Spacing = 2 };
        var head = new Grid();
        head.Children.Add(Title(L.T("Sound")));
        // the arrow to the devices and the volume mixer: the chevron of the rows under it, as big as the settings icon below
        var mixer = new Button { Style = DockButton, Padding = new Thickness(6, 4, 6, 4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Content = new FontIcon { Glyph = "\uE76C", FontSize = SoundIcons } };
        ToolTipService.SetToolTip(mixer, L.T("Devices and volume mixer"));
        mixer.Click += (_, _) => ShowSound();
        head.Children.Add(mixer);
        panel.Children.Add(head);
        (_volume, _volumeValue) = SliderRow(panel, "", v => AudioControl.SetLevel((float)(v / 100)), AudioControl.ToggleMute); _volumeGlyph = _lastIcon;
        if (AudioControl.Now() is { } now)
        {
            var device = new Button { Style = DockButton, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(26, 0, 0, 0),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = now.Name, FontSize = 11, MaxWidth = 260, Foreground = Res("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis }, new FontIcon { Glyph = "", FontSize = 9 } } } };
            ToolTipService.SetToolTip(device, L.T("Choose a sound output"));
            device.Click += (_, _) => ShowSound();
            panel.Children.Add(device);
        }
        if (AudioControl.MicNow() != null)
        {
            (_mic, _micValue) = SliderRow(panel, "", v => AudioControl.SetMicLevel((float)(v / 100)), AudioControl.ToggleMicMute); _micGlyph = _lastIcon;
            // the name of the microphone under it, like the output above (a click opens the devices)
            if (AudioControl.MicName() is { Length: > 0 } micName)
            {
                var input = new Button { Style = DockButton, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(26, 0, 0, 0),
                    Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = micName, FontSize = 11, MaxWidth = 260, Foreground = Res("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis }, new FontIcon { Glyph = "\uE76C", FontSize = 9 } } } };
                ToolTipService.SetToolTip(input, L.T("Choose a sound input"));
                input.Click += (_, _) => ShowSound();
                panel.Children.Add(input);
            }
        }
        // the sound settings of Windows in the bottom right corner, as big as the arrow above
        var settings = new Button { Style = DockButton, Padding = new Thickness(6, 4, 6, 4), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0), Content = new FontIcon { Glyph = "", FontSize = SoundIcons } };
        ToolTipService.SetToolTip(settings, L.T("Sound settings"));
        settings.Click += (_, _) => Open("ms-settings:sound");
        settings.Margin = new Thickness(0);
        settings.VerticalAlignment = VerticalAlignment.Center;
        // on the line of the last device name (the microphone), so the card does not get higher by a line of its own
        if (panel.Children.Count > 0 && panel.Children[^1] is Button last && last != settings)
        {
            panel.Children.Remove(last);
            var bottom = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            last.HorizontalAlignment = HorizontalAlignment.Left;
            last.VerticalAlignment = VerticalAlignment.Center;
            bottom.Children.Add(last);
            Grid.SetColumn(settings, 1);
            bottom.Children.Add(settings);
            panel.Children.Add(bottom);
        }
        else panel.Children.Add(settings);
        var card = Card(panel);
        card.PointerWheelChanged += (_, e) => { if (_volume != null) { _volume.Value = Math.Clamp(_volume.Value + (e.GetCurrentPoint(card).Properties.MouseWheelDelta > 0 ? 2 : -2), 0, 100); e.Handled = true; } };
        return card;
    }

    (Slider, TextBlock) SliderRow(Panel parent, string glyph, Action<double> set, Action mute)
    {
        var icon = _lastIcon = new FontIcon { Glyph = glyph, FontSize = 15 };
        var button = new Button { Style = DockButton, Padding = new Thickness(4), Content = icon };
        ToolTipService.SetToolTip(button, L.T("Mute / unmute"));
        if (mute != null) button.Click += (_, _) => { mute(); SyncAudio(); };
        // the number right behind the slider (aligned to the right it stood far from it with one digit), room for "100"
        var value = new TextBlock { Width = 28, TextAlignment = TextAlignment.Left, VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var slider = new Slider { Minimum = 0, Maximum = 100, Margin = new Thickness(4, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, IsThumbToolTipEnabled = false };
        slider.ValueChanged += (_, e) => { if (_updating) return; set(e.NewValue); value.Text = Math.Round(e.NewValue).ToString(); };
        var row = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        row.Children.Add(button);
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        parent.Children.Add(row);
        return (slider, value);
    }

    /// <summary>The volume may change by keys or in another app: the sliders follow (not while one is being dragged).</summary>
    void SyncAudio()
    {
        _updating = true;
        try
        {
            // the icons show the state like the ones of the dock: the level of the volume, muted (also while dragging)
            if (_volumeGlyph != null && AudioControl.Now() is { } now) _volumeGlyph.Glyph = AudioControl.Glyph(now.Level, now.Mute);
            if (_micGlyph != null && AudioControl.MicNow() is { } mic) _micGlyph.Glyph = mic.Mute ? "" : "";
            if (_volume != null && _volume.FocusState != FocusState.Pointer && AudioControl.Now() is { } v)
            {
                _volume.Value = Math.Round(v.Level * 100);
                _volumeValue.Text = v.Mute ? "×" : Math.Round(v.Level * 100).ToString();
            }
            if (_mic != null && _mic.FocusState != FocusState.Pointer && AudioControl.MicNow() is { } m)
            {
                _mic.Value = Math.Round(m.Level * 100);
                _micValue.Text = m.Mute ? "×" : Math.Round(m.Level * 100).ToString();
            }
            foreach (var (app, slider, value) in _apps)
                if (slider.FocusState != FocusState.Pointer) { slider.Value = Math.Round(app.Volume * 100); value.Text = app.Mute ? "×" : Math.Round(app.Volume * 100).ToString(); }
        }
        catch { }
        finally { _updating = false; }
    }

    readonly MMDeviceEnumerator _enum = new();

    void ShowSound()
    {
        ShowPage("sound", "Sound");
        Footer("More sound settings", "ms-settings:sound");
        string output = null, input = null;
        try { using var d = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); output = d.ID; } catch { }
        try { using var d = _enum.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); input = d.ID; } catch { }
        // the volume on top, the same slider as on the main page
        var master = PageCard();
        (_volume, _volumeValue) = SliderRow(master, "", v => AudioControl.SetLevel((float)(v / 100)), AudioControl.ToggleMute); _volumeGlyph = _lastIcon;
        ((FrameworkElement)master.Children[^1]).Margin = new Thickness(6, 4, 6, 4);
        DeviceList("Output", DataFlow.Render, output, "");
        var mixer = PageCard(L.T("Volume mixer"));
        _apps.Clear();
        MMDevice dev = null;
        try { dev = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); } catch { }
        var apps = dev != null ? AudioDevices.Apps(dev) : new();
        if (apps.Count == 0) mixer.Children.Add(new TextBlock { Text = L.T("No app is playing sound"), Margin = new Thickness(8, 6, 8, 6), FontSize = 12.5, Foreground = Res("TextFillColorSecondaryBrush") });
        foreach (var app in apps)
        {
            var row = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (app.IconPath != null) { var img = new Image { Width = 16, Height = 16 }; Pictures.Into(img, app.IconPath); name.Children.Add(img); }
            else name.Children.Add(new FontIcon { Glyph = app.System ? "" : "", FontSize = 13 });
            name.Children.Add(new TextBlock { Text = app.Name, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(name);
            var (slider, value) = SliderRow(row, "", v => { app.Volume = (float)(v / 100); if (app.Mute && v > 0) app.Mute = false; }, () => app.Mute = !app.Mute);
            _apps.Add((app, slider, value));
            mixer.Children.Add(row);
        }
        DeviceList("Input", DataFlow.Capture, input, "");
        SyncAudio();
    }

    /// <summary>The active devices; the default one has a check mark, clicking another makes it the default.</summary>
    void DeviceList(string title, DataFlow flow, string current, string glyph)
    {
        var devices = AudioDevices.Active(_enum, flow);
        if (devices.Count == 0) return;
        var card = PageCard(L.T(title));
        foreach (var d in devices)
        {
            string id = d.ID, name = d.FriendlyName;
            d.Dispose();
            card.Children.Add(PageRow(glyph, name, id == current, id == current ? null : async () =>
            {
                // in the background: Windows switches the device and tells every app about it, which can take a moment
                await Task.Run(() => AudioDevices.SetDefault(id));
                if (_openPage == "sound") ShowSound();
            }));
        }
    }

    // ───────────────────────── monitors, keep awake, cast ─────────────────────────

    void ShowMonitors()
    {
        ShowPage("display", "Monitors");
        Footer("Display settings", "ms-settings:display");
        var card = PageCard();
        string now = DisplayTopology.Current();
        foreach (var (glyph, name, arg) in new[] { ("", "PC screen only", "/internal"), ("", "Duplicate", "/clone"), ("", "Extend", "/extend"), ("", "Second screen only", "/external") })
            card.Children.Add(PageRow(glyph, name, arg == now, async () =>
            {
                Fg.Run("DisplaySwitch.exe", arg);
                await Task.Delay(2500);                                // the screens need a moment to switch
                if (_openPage == "display") ShowMonitors();
            }));
    }

    static string AwakeText() => KeepAwake.ShutdownAt is { } at
        ? L.T("Shut down in {0}", (at > DateTime.Now ? at - DateTime.Now : TimeSpan.Zero).ToString(@"h\:mm\:ss"))
        : L.T(KeepAwake.IsOn ? "On" : "Off");

    void ShowAwake() => BuildAwake(false);

    void BuildAwake(bool custom)
    {
        ShowPage("awake", "Keep awake");
        PageCard().Children.Add(PageRow("", KeepAwake.IsOn ? "Sleep is blocked – allow it" : "Block sleep", KeepAwake.IsOn, () => { KeepAwake.Set(!KeepAwake.IsOn); BuildAwake(false); }));
        var card = PageCard(KeepAwake.ShutdownAt is { } at ? L.T("Shut down at {0}", at.ToString("t")) : L.T("Shut down the PC in"));
        if (KeepAwake.ShutdownAt != null)
        {
            card.Children.Add(PageRow("", "Cancel the shutdown", false, () => { KeepAwake.CancelShutdown(); BuildAwake(false); }));
            return;
        }
        foreach (var (name, time) in new[] { ("30 minutes", TimeSpan.FromMinutes(30)), ("1 hour", TimeSpan.FromHours(1)), ("2 hours", TimeSpan.FromHours(2)), ("4 hours", TimeSpan.FromHours(4)) })
            card.Children.Add(PageRow("", name, false, () => { KeepAwake.ShutdownIn(time); BuildAwake(false); }));
        if (!custom) { card.Children.Add(PageRow("", "Custom interval", false, () => BuildAwake(true))); return; }
        // an own interval: 5 minutes to 12 hours by 5 minutes
        var value = new TextBlock { Width = 80, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var slider = new Slider { Minimum = 5, Maximum = 720, StepFrequency = 5, SnapsTo = SliderSnapsTo.StepValues, Value = 60, IsThumbToolTipEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        void Show() { var t = TimeSpan.FromMinutes(slider.Value); value.Text = t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{t.Minutes} min"; }
        slider.ValueChanged += (_, _) => Show();
        Show();
        var line = new Grid { Margin = new Thickness(8, 6, 8, 0), ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        line.Children.Add(slider);
        Grid.SetColumn(value, 1);
        line.Children.Add(value);
        var ok = new Button { Content = L.T("Confirm"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 8, 8, 6) };
        ok.Click += (_, _) => { KeepAwake.ShutdownIn(TimeSpan.FromMinutes(slider.Value)); BuildAwake(false); };
        card.Children.Add(line);
        card.Children.Add(ok);
    }

    async Task ShowCastAsync()
    {
        ShowPage("cast", "Cast");
        Searching("Searching for wireless displays…");                   // Windows takes a few seconds: not an empty page meanwhile
        try
        {
            var found = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(Windows.UI.ViewManagement.ProjectionManager.GetDeviceSelector());
            if (_openPage != "cast") return;
            _page.Children.Clear();
            var all = found.Where(x => !x.Name.StartsWith("Generic Monitor", StringComparison.OrdinalIgnoreCase)).ToList();   // the selector also returns the connected monitors
            if (all.Count > 0) { var card = PageCard(); foreach (var d in all) card.Children.Add(PageRow("", d.Name, false, () => { CloseNow(); AppShell.Tap(0x5B, 0x4B); })); }
            else Note("No wireless displays were found.");
        }
        catch (Exception ex) { Log.Write("Cast list", ex); _page.Children.Clear(); }
        if (_openPage != "cast") return;
        PageCard().Children.Add(PageRow("", "Connect through Windows", false, () => { CloseNow(); AppShell.Tap(0x5B, 0x4B); }));   // Win+K: only Windows can connect them
    }

    // ───────────────────────── display, power, media, battery ─────────────────────────

    async Task AddBrightnessAsync(Border holder)
    {
        var (level, auto, autoOn) = await Task.Run(() => { bool s = AutoBrightness.Supported; return (DisplayBrightness.Get(), s, s && AutoBrightness.IsOn); });
        if (level is not int l && !auto) return;
        var panel = new StackPanel { Spacing = 2 };
        var head = new Grid { Children = { Title(L.T("Display")) } };
        if (auto)
        {
            var sw = new ToggleSwitch { IsOn = autoOn, OnContent = L.T("Automatic"), OffContent = L.T("Automatic"), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 0 };
            sw.Toggled += (_, _) => { bool on = sw.IsOn; _ = Task.Run(() => AutoBrightness.Set(on)); };
            head.Children.Add(sw);
        }
        panel.Children.Add(head);
        if (level is int value)
        {
            var apply = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            Slider slider = null;
            apply.Tick += (_, _) => { apply.Stop(); int v = (int)slider.Value; _ = Task.Run(() => DisplayBrightness.Set(v)); };   // the monitor is slow: after the slider settles
            (slider, var text) = SliderRow(panel, "", _ => { apply.Stop(); apply.Start(); }, null);
            _updating = true;
            slider.Value = value;
            text.Text = value.ToString();
            _updating = false;
        }
        if (_openPage != null) return;
        holder.Child = _editing ? Editable(Card(panel), "brightness", tile: false) : Card(panel);
        Place();
    }

    FrameworkElement PowerCard()
    {
        var row = new Grid { ColumnSpacing = 6, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition(), new ColumnDefinition() } };
        var now = PowerMode.Current;
        int i = 0;
        foreach (var (glyph, name, mode) in new[] { ("", "Efficiency", PowerMode.Efficiency), ("", "Balanced", PowerMode.Balanced), ("", "Performance", PowerMode.Performance) })
        {
            var b = new ToggleButton { IsChecked = now == mode, HorizontalAlignment = HorizontalAlignment.Stretch, Content = new StackPanel { Spacing = 4, Children = { new FontIcon { Glyph = glyph, FontSize = 15 }, new TextBlock { Text = L.T(name), FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center } } } };
            b.Click += (_, _) => { PowerMode.Set(mode); BuildMain(); };
            Grid.SetColumn(b, i++);
            row.Children.Add(b);
        }
        return Card(new StackPanel { Spacing = 8, Children = { Title(L.T("Power mode")), row } });
    }

    FrameworkElement BatteryCard((int Percent, bool Plugged, bool Charging, bool Saver, int SecondsLeft) b)
    {
        string state = b.Plugged ? (b.Percent >= 100 ? L.T("Fully charged") : L.T("Charging"))
            : b.SecondsLeft > 0 ? L.T("{0} h {1} min remaining", b.SecondsLeft / 3600, b.SecondsLeft / 60 % 60) : L.T("On battery");
        var saver = new ToggleButton { IsChecked = BatterySaver.IsOn == true, IsEnabled = BatterySaver.IsOn != null, HorizontalAlignment = HorizontalAlignment.Right,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { new FontIcon { Glyph = "", FontSize = 13 }, new TextBlock { Text = L.T("Battery saver"), FontSize = 11.5 } } } };
        ToolTipService.SetToolTip(saver, BatterySaver.IsOn == null ? L.T("Battery saver works only on battery") : L.T(BatterySaver.IsOn == true ? "Battery saver: on" : "Battery saver: off"));
        saver.Click += async (_, _) => { if (BatterySaver.IsOn is bool on) { saver.IsEnabled = false; await Task.Run(() => BatterySaver.Set(!on)); await Task.Delay(400); BuildMain(); } };
        var head = new Grid { Children = { Title(L.T("Battery")) } };
        var settings = new Button { Style = DockButton, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(6, 2, 6, 2), Content = new FontIcon { Glyph = "", FontSize = 12 } };
        settings.Click += (_, _) => Open("ms-settings:powersleep");
        head.Children.Add(settings);
        var row = new Grid();
        row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children =
        {
            Battery(b.Percent, b.Plugged),
            new StackPanel { Children = { new TextBlock { Text = b.Percent + " %", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, new TextBlock { Text = state, FontSize = 11, Foreground = Res("TextFillColorSecondaryBrush") } } }
        } });
        row.Children.Add(saver);
        return Card(new StackPanel { Spacing = 6, Children = { head, row } });
    }

    static BatteryIcon Battery(int percent, bool plugged) { var i = new BatteryIcon { Size = 20 }; i.Show(percent, plugged); return i; }

    FrameworkElement MediaCard()
    {
        var (title, playing) = BarMedia.Now;
        var media = BarMedia.Details;
        Button B(string glyph, Func<Task> click) { var b = new Button { Style = DockButton, Padding = new Thickness(10, 7, 10, 7), Content = new FontIcon { Glyph = glyph, FontSize = 16 } }; b.Click += async (_, _) => { await click(); await Task.Delay(300); BuildMain(); }; return b; }
        // the picture of what plays (cover, frame of the video), else the icon of the app; beside it the app, the title and
        // the artist; the buttons under them
        const double Pic = 72;
        var picture = new Border { Width = Pic, Height = Pic, CornerRadius = new CornerRadius(6), Background = Res("ControlFillColorDefaultBrush"), VerticalAlignment = VerticalAlignment.Top };
        if (media?.Picture is { } bytes) _ = ShowPictureAsync(picture, bytes);
        else if (media?.AppKey is { } k && AppShell.Icon(k) is { } appIcon) picture.Child = new Image { Width = 40, Height = 40, Source = Pictures.ForSize(appIcon, 40) };
        else picture.Child = new FontIcon { Glyph = "", FontSize = 28, Foreground = Res("TextFillColorSecondaryBrush") };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        if (media?.App is { Length: > 0 } appName)
        {
            var source = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 2) };
            if (media.AppKey is { } key && AppShell.Icon(key) is { } small) source.Children.Add(new Image { Width = 14, Height = 14, Source = Pictures.ForSize(small, 14), VerticalAlignment = VerticalAlignment.Center });
            source.Children.Add(new TextBlock { Text = appName, FontSize = 11.5, Foreground = Res("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            texts.Children.Add(source);
        }
        texts.Children.Add(new TextBlock { Text = media?.Title ?? title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        if (media?.Artist is { Length: > 0 } artist)
            texts.Children.Add(new TextBlock { Text = artist, FontSize = 12, Foreground = Res("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        var top = new Grid { ColumnSpacing = 12, ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() } };
        top.Children.Add(picture);
        Grid.SetColumn(texts, 1);
        top.Children.Add(texts);
        var row = new StackPanel { Spacing = 6, Children = { top } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 8, Children = { B("", BarMedia.PreviousAsync), B(playing ? "" : "", BarMedia.PlayPauseAsync), B("", BarMedia.NextAsync) } };
        row.Children.Add(buttons);
        return Card(row);
    }

    /// <summary>The picture of the media from its bytes (a cover is a JPEG or PNG), filling the rounded place.</summary>
    static async Task ShowPictureAsync(Border place, byte[] bytes)
    {
        try
        {
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await image.SetSourceAsync(stream);
            place.Background = new ImageBrush { ImageSource = image, Stretch = Stretch.UniformToFill };
            place.Child = null;
        }
        catch (Exception ex) { Log.Write("Media picture", ex); }
    }
}
