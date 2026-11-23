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

sealed partial class ControlCenterWindow
{
    // ───────────────────────── Wi-Fi ─────────────────────────

    readonly StackPanel _networks = new() { Spacing = 1 };
    /// <summary>The card of the list: hidden while there is no network (Wi-Fi off) – an empty card was left before.</summary>
    Border _networksCard;
    ScrollViewer _networksScroll;
    readonly TextBlock _wifiStatus = new() { FontSize = 12, Margin = new Thickness(8, 0, 8, 4), TextWrapping = TextWrapping.Wrap };

    void ShowWifi()
    {
        ShowPage("wifi", "Wi-Fi");
        var refresh = new Button { Style = DockButton, Content = new FontIcon { Glyph = "", FontSize = 13 } };
        ToolTipService.SetToolTip(refresh, L.T("Scan again"));
        refresh.Click += (_, _) => _ = ScanAsync();
        _pageActions.Children.Add(refresh);
        HeaderSwitch(_wifi?.State == RadioState.On, async on =>
        {
            if (_wifi == null) return;
            if (on) _wifiStatus.Text = L.T("Searching for networks…");
            await SwitchRadio(_wifi, on);
            // a scan started before the switch (with Wi-Fi off) is followed by a new one (ScanAsync), never two at once
            await ScanAsync();
        });
        _wifiStatus.Foreground = Res("TextFillColorSecondaryBrush");
        // the list and the status are kept between the visits of the page: out of the old page first (else "Element is
        // already the child of another element" stopped the page half built – no list until it was opened again)
        // (the parent of the list is the presenter inside the old ScrollViewer once it was shown, not the ScrollViewer itself,
        // so the old one is kept to let go of the list)
        if (_networksScroll != null) _networksScroll.Content = null;
        if (_networks.Parent is ScrollViewer old) old.Content = null;
        if (_wifiStatus.Parent is Panel oldPanel) oldPanel.Children.Remove(_wifiStatus);
        _page.Children.Add(_wifiStatus);
        _networksScroll = new ScrollViewer { MaxHeight = 340, Content = _networks };
        _networksCard = Card(_networksScroll, 4);
        _networksCard.Visibility = _networks.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _page.Children.Add(_networksCard);
        Footer("More Wi-Fi settings", "ms-settings:network-wifi");
        _ = ScanAsync();
    }

    Task _scanning;
    static bool _adapterWatched;

    void OpenLocationSettings(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => Open("ms-settings:privacy-location");

    /// <summary>The location turned off by a policy of the PC (LocationAndSensors\DisableLocation).</summary>
    static bool LocationOffByPolicy()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors");
            return k?.GetValue("DisableLocation") is int v && v != 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// The Wi-Fi adapter, asked for once – only when the Wi-Fi page is opened: asking for it can bring up a message of Windows
    /// (location turned off by the administrator), which must not come by itself.
    /// </summary>
    async Task<bool> EnsureAdapterAsync()
    {
        if (_adapter != null) return true;
        if (await WiFiAdapter.RequestAccessAsync() != WiFiAccessStatus.Allowed)
        {
            // since Windows 11 24H2 the list of networks needs the location: a policy that turns it off (often set by
            // privacy tools) cannot be switched on in Settings, so it is named
            _wifiStatus.Tapped -= OpenLocationSettings;
            if (LocationOffByPolicy())
                _wifiStatus.Text = L.T("Windows does not allow scanning for Wi-Fi networks: the location is turned off by a policy (often set by a privacy tool). Without it only More Wi-Fi settings can list the networks.");
            else
            {
                _wifiStatus.Text = L.T("Windows does not allow scanning for Wi-Fi networks. Turn on location access for desktop apps (Settings → Privacy & security → Location), or use More Wi-Fi settings.")
                    + " " + L.T("Click to open the settings of the location.");
                _wifiStatus.Tapped += OpenLocationSettings;
            }
            return false;
        }
        _wifiStatus.Tapped -= OpenLocationSettings;
        _adapter ??= (await WiFiAdapter.FindAllAdaptersAsync()).FirstOrDefault();
        if (_adapter == null) { _wifiStatus.Text = L.T("No Wi-Fi adapter found."); return false; }
        if (!_adapterWatched)
        {
            _adapterWatched = true;
            // the networks as Windows finds them (also its own scans): the list fills while the scan still runs
            _adapter.AvailableNetworksChanged += (_, _) => Ui.Post(async () => { if (_openPage == "wifi") await BuildNetworksAsync(); });
        }
        return true;
    }

    bool _scanAgain;

    /// <summary>
    /// Scans once at a time: asked again while one runs (the switch, the radio changed, the button) one more follows it –
    /// two at once made the driver refuse the second (no list), and the one before a switch saw the old state.
    /// </summary>
    Task ScanAsync()
    {
        if (_scanning is { IsCompleted: false }) { _scanAgain = true; return _scanning; }
        return _scanning = ScanLoopAsync();
    }

    async Task ScanLoopAsync()
    {
        do { _scanAgain = false; await DoScanAsync(); }
        while (_scanAgain && _openPage == "wifi");
    }

    async Task DoScanAsync()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_wifi?.State != RadioState.On) { _networks.Children.Clear(); if (_networksCard != null) _networksCard.Visibility = Visibility.Collapsed; _wifiStatus.Text = L.T("Wi-Fi is turned off."); return; }
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
            if (scan.IsFaulted)
            {
                // refused right after the radio came on (the adapter not ready yet): once more a moment later
                Diag.Write("Wi-Fi", "scan refused: " + scan.Exception?.InnerException?.Message);
                await Task.Delay(2000);
                await Task.WhenAny(_adapter.ScanAsync().AsTask(), Task.Delay(6000));
            }
            if (_openPage == "wifi") await BuildNetworksAsync();
            // right after Wi-Fi is switched on the first scan often finds nothing yet: once more a moment later
            if (_openPage == "wifi" && _networks.Children.Count == 0 && _wifi?.State == RadioState.On)
            {
                _wifiStatus.Text = L.T("Searching for networks…");
                await Task.Delay(2500);
                await Task.WhenAny(_adapter.ScanAsync().AsTask(), Task.Delay(6000));
                if (_openPage == "wifi") await BuildNetworksAsync();
            }
            Diag.Write("Wi-Fi", $"scan: {_networks.Children.Count} networks in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms – {_wifiStatus.Text}");
        }
        catch (Exception ex)
        {
            Log.Write("Wi-Fi scan", ex);
            if (_networks.Children.Count == 0) _wifiStatus.Text = L.T("Wi-Fi networks could not be scanned. Location access may be turned off for desktop apps.");
        }
    }
    string _networksShown;
    (string Ssid, DateTime At)? _justConnected;

    /// <summary>
    /// The network as Windows sees it now: the object of an older scan (before a disconnect) was refused with "network not
    /// available" after seconds of waiting.
    /// </summary>
    WiFiAvailableNetwork FreshNetwork(WiFiAvailableNetwork old)
    {
        try
        {
            return _adapter.NetworkReport.AvailableNetworks.Where(a => a.Ssid == old.Ssid)
                .OrderByDescending(a => a.NetworkRssiInDecibelMilliwatts).FirstOrDefault() ?? old;
        }
        catch { return old; }
    }

    sealed record WifiNetwork(string Ssid, byte Bars, bool Secured, WiFiAvailableNetwork Real, string Details);

    /// <summary>
    /// The list of networks. While a network is unfolded (a key being typed, a connection running) the rows stay as they are
    /// – a scan, a change of the network or a new list of Windows rebuilt them before, and the typed key was gone; only the
    /// status line follows. <paramref name="force"/>: after connecting or disconnecting.
    /// </summary>
    async Task BuildNetworksAsync(bool force = false)
    {
        bool unfolded = _networks.Children.OfType<StackPanel>().Any(r => r.Children.Count > 1 && r.Children[1].Visibility == Visibility.Visible);
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
            connected = await Task.Run(ConnectedSsid);
            // right after a successful connect Windows reports the connection only a moment later: the network stays shown
            // as connected for a few seconds (it was shown as not connected, with Connect again – clicked again and again)
            if (connected == null && _justConnected is { } just && DateTime.UtcNow - just.At < TimeSpan.FromSeconds(10)) connected = just.Ssid;
            available = _adapter.NetworkReport.AvailableNetworks.Where(n => !string.IsNullOrEmpty(n.Ssid)).GroupBy(n => n.Ssid)
                .Select(g => g.OrderByDescending(n => n.NetworkRssiInDecibelMilliwatts).First())
                .Select(n => new WifiNetwork(n.Ssid, n.SignalBars, IsSecured(n), n, Describe(n)));
        }
        var networks = available.OrderByDescending(n => n.Ssid == connected).ThenByDescending(n => n.Bars).ToList();
        var known = KnownNetworks();
        _wifiStatus.Text = networks.Count == 0 ? L.T("No networks found.") : connected != null ? L.T("Connected to {0}", connected) : L.T("Not connected");
        if (unfolded && !force) return;
        // the same list again (a scan, a change of the network, a new list of Windows – several in a second after connecting)
        // is not rebuilt: every rebuild made the list jump
        string shown = string.Join("|", networks.Select(n => $"{n.Ssid}/{n.Bars}/{n.Ssid == connected}/{known.Contains(n.Ssid)}"));
        if (shown == _networksShown && !unfolded && _networks.Children.Count == networks.Count) return;
        _networksShown = shown;
        _networks.Children.Clear();
        foreach (var n in networks) _networks.Children.Add(NetworkRow(n, n.Ssid == connected, known.Contains(n.Ssid)));
        if (_networksCard != null) _networksCard.Visibility = networks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>
    /// The Wi-Fi network this PC is connected to (its name), null when none. Not GetConnectedProfileAsync of the adapter: with
    /// a cable plugged in too, Windows hands the profile of the Ethernet there ("Connected to Ethernet" on the Wi-Fi page).
    /// </summary>
    internal static string ConnectedSsid()
    {
        try
        {
            foreach (var p in NetworkInformation.GetConnectionProfiles())
            {
                if (!p.IsWlanConnectionProfile || p.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None) continue;
                string ssid = p.WlanConnectionProfileDetails?.GetConnectedSsid();
                if (!string.IsNullOrEmpty(ssid)) return ssid;
            }
        }
        catch { }
        return null;
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
            (connected ? L.T("Connected") + ", " : "") + n.Details, "");
        if (connected)
        {
            var disconnect = new Button { Content = L.T("Disconnect"), HorizontalAlignment = HorizontalAlignment.Right };
            disconnect.Click += async (_, _) =>
            {
#if DEBUG
                if (Simulation.Wifi) Simulation.WifiConnected = null;
                else
#endif
                _justConnected = null;
                try { _adapter.Disconnect(); } catch (Exception ex) { Log.Write("Wi-Fi disconnect", ex); }
                await Task.Delay(800);
                await BuildNetworksAsync(force: true);
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
                        await BuildNetworksAsync(force: true);
                        return;
                    }
#endif
                    var kind = auto.IsChecked == true ? WiFiReconnectionKind.Automatic : WiFiReconnectionKind.Manual;
                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    Task<WiFiConnectionResult> Connect(WiFiAvailableNetwork net) => (password != null && password.Password.Length > 0
                        ? _adapter.ConnectAsync(net, kind, new PasswordCredential { Password = password.Password })
                        : _adapter.ConnectAsync(net, kind)).AsTask();
                    var connecting = Connect(FreshNetwork(n.Real));
                    // some drivers never answer a wrong key or a network gone weak: after 25 s the user is told, not left waiting
                    if (await Task.WhenAny(connecting, Task.Delay(25000)) != connecting)
                    {
                        Diag.Write("Wi-Fi", "connect: no answer in 25 s");
                        message.Text = L.T("The network did not respond. Try again.");
                        connect.IsEnabled = true;
                        return;
                    }
                    var result = await connecting;
                    if (result.ConnectionStatus == WiFiConnectionStatus.NetworkNotAvailable)
                    {
                        // just disconnected: the adapter has to see the network once more – one scan, then again
                        Diag.Write("Wi-Fi", "connect: network not available – scanning and trying again");
                        await Task.WhenAny(_adapter.ScanAsync().AsTask(), Task.Delay(6000));
                        var again = Connect(FreshNetwork(n.Real));
                        if (await Task.WhenAny(again, Task.Delay(20000)) == again) result = await again;
                    }
                    Diag.Write("Wi-Fi", $"connect: {result.ConnectionStatus} in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
                    bool ok = result.ConnectionStatus == WiFiConnectionStatus.Success;
                    // refused without a clear reason (some versions of Windows do so for apps): the same through the tool of Windows
                    if (!ok && result.ConnectionStatus is WiFiConnectionStatus.AccessRevoked or WiFiConnectionStatus.UnspecifiedFailure)
                        ok = await WifiFallback.ConnectAsync(n.Real, password?.Password, auto.IsChecked == true);
                    switch (ok ? WiFiConnectionStatus.Success : result.ConnectionStatus)
                    {
                        case WiFiConnectionStatus.Success:
                            _justConnected = (n.Ssid, DateTime.UtcNow);
                            await BuildNetworksAsync(force: true);
                            return;
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

    /// <summary>
    /// Switches a radio and waits (at most 3 s) until Windows reports the new state: read at once, it was still the old one
    /// – the page then said "turned off" (and listed nothing) under a switch that was on.
    /// </summary>
    static async Task SwitchRadio(RadioSwitch radio, bool on)
    {
        var want = on ? RadioState.On : RadioState.Off;
        await radio.SetStateAsync(want);
        for (int i = 0; i < 30 && radio.State != want; i++) await Task.Delay(100);
    }
}
