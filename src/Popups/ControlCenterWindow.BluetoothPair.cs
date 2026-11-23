using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Enumeration;

namespace HeMiDock;

/// <summary>
/// Adding a Bluetooth device on the Bluetooth page: "Add device" looks for devices nearby (classic and LE, those with a
/// name) for half a minute, a click pairs one. A PIN to compare is shown in the row with Pair / Cancel, a PIN to type on
/// the device is shown; a device that wants a PIN typed here goes to the settings of Windows.
/// </summary>
sealed partial class ControlCenterWindow
{
    Dictionary<string, (DeviceInformation Info, bool Le)> _btFound;
    readonly List<DeviceWatcher> _btWatchers = new();
    DispatcherTimer _btStopTimer;
    bool _btDiscovering;
    StackPanel _btNearbyRows;
    /// <summary>
    /// The turning ring of the search: one element for the whole search, shown or hidden – made anew with every device
    /// found (dozens of LE ones), it started over again and again and looked stuck.
    /// </summary>
    StackPanel _btSearchRow;
    string _btPairingId, _btPairNote, _btPairNoteId;
    (string Id, string Pin, TaskCompletionSource<bool> Answer)? _btConfirm;
    bool _btRenderPosted;

    const string SignalKey = "System.Devices.Aep.SignalStrength";

    /// <summary>The part under the paired devices: the button, or the devices found nearby.</summary>
    void AddNearbySection()
    {
        _btNearbyRows = null;
        if (_btFound == null)
        {
            var add = PageCard();
            add.Children.Add(PageRow("", "Add device", false, StartBtDiscovery, L.T("Looks for devices nearby that are ready to pair")));
            return;
        }
        _btNearbyRows = PageCard(L.T("Devices nearby"));
        RenderNearby();
    }

    void StartBtDiscovery()
    {
        StopBtDiscovery();
        _btFound = new();
        _btPairNote = _btPairNoteId = null;
        _btDiscovering = true;
        try
        {
            string[] props = { SignalKey, "System.Devices.Aep.IsPaired" };
            foreach (var (selector, le) in new[] { (Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(false), false), (Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromPairingState(false), true) })
            {
                var w = DeviceInformation.CreateWatcher(selector, props, DeviceInformationKind.AssociationEndpoint);
                w.Added += (_, d) => Ui.Post(() => { if (_btFound == null) return; _btFound[d.Id] = (d, le); RenderNearbySoon(); });
                // updates come all the time (the signal of every device): the list is redrawn only when a name came or changed
                w.Updated += (_, u) => Ui.Post(() =>
                {
                    if (_btFound == null || !_btFound.TryGetValue(u.Id, out var f)) return;
                    string before = f.Info.Name;
                    f.Info.Update(u);
                    if (f.Info.Name != before) RenderNearbySoon();
                });
                w.Removed += (_, u) => Ui.Post(() => { if (_btFound != null && u.Id != _btPairingId && _btFound.Remove(u.Id)) RenderNearbySoon(); });
                _btWatchers.Add(w);
                w.Start();
            }
        }
        catch (Exception ex) { Log.Write("Bluetooth search", ex); _btDiscovering = false; }
        // a minute: an iPhone shows itself only while its Bluetooth settings are open; the list found stays
        _btStopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _btStopTimer.Tick += (_, _) => { StopBtDiscovery(); RenderNearby(); };
        _btStopTimer.Start();
        Diag.Write("Bluetooth", "search for devices nearby started");
        _ = BuildBluetoothAsync();
    }

    /// <summary>Stops looking (leaving the page, the window hidden, after half a minute); the devices found stay listed.</summary>
    void StopBtDiscovery()
    {
        _btStopTimer?.Stop();
        _btStopTimer = null;
        foreach (var w in _btWatchers)
            try { if (w.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) w.Stop(); } catch { }
        _btWatchers.Clear();
        if (_btDiscovering && _btFound != null) Diag.Write("Bluetooth", $"search ended: {_btFound.Values.Count(f => !string.IsNullOrWhiteSpace(f.Info.Name))} devices with a name");
        _btDiscovering = false;
    }

    /// <summary>Forgets the search when the window is hidden: the next visit starts with the button again.</summary>
    void ResetBtDiscovery()
    {
        StopBtDiscovery();
        if (_btPairingId == null) { _btFound = null; _btConfirm = null; }
    }

    /// <summary>A burst of devices (dozens of LE ones at the start) redraws the list once.</summary>
    void RenderNearbySoon()
    {
        if (_btRenderPosted) return;
        _btRenderPosted = true;
        Ui.Post(() => { _btRenderPosted = false; RenderNearby(); });
    }

    void RenderNearby()
    {
        if (_openPage != "bt") { StopBtDiscovery(); return; }
        var rows = _btNearbyRows;
        if (rows == null || _btFound == null) return;
        _btSearchRow ??= new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(8, 6, 8, 6),
            Children =
            {
                new ProgressRing { IsActive = true, Width = 16, Height = 16 },
                new TextBlock { Text = L.T("Searching for devices nearby…"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("TextFillColorSecondaryBrush") }
            }
        };
        // everything but the ring goes; the ring stays where it is (moved between lists only when the page was rebuilt)
        for (int i = rows.Children.Count - 1; i >= 0; i--) if (rows.Children[i] != _btSearchRow) rows.Children.RemoveAt(i);
        if (_btSearchRow.Parent is Microsoft.UI.Xaml.Controls.Panel other && other != rows) other.Children.Remove(_btSearchRow);
        if (_btSearchRow.Parent == null) rows.Children.Insert(0, _btSearchRow);
        _btSearchRow.Visibility = _btDiscovering ? Visibility.Visible : Visibility.Collapsed;
        // a device seen as classic and as LE once, by its name (the classic one pairs the way Windows does)
        var shown = _btFound.Values.Where(f => !string.IsNullOrWhiteSpace(f.Info.Name))
            .GroupBy(f => f.Info.Name.Trim())
            .Select(g => g.OrderBy(f => f.Le).First())
            .OrderByDescending(f => f.Info.Id == _btPairingId)
            .ThenByDescending(f => f.Info.Properties.TryGetValue(SignalKey, out var s) && s is int v ? v : -200)
            .Take(20).ToList();
        foreach (var f in shown) rows.Children.Add(NearbyRow(f.Info, f.Le));
        if (shown.Count == 0 && !_btDiscovering)
        {
            rows.Children.Add(new TextBlock { Text = L.T("No devices found. Put the device into pairing mode (an iPhone: Settings → Bluetooth open) and search again."), FontSize = 12, Margin = new Thickness(8, 6, 8, 4), TextWrapping = TextWrapping.Wrap, Foreground = Res("TextFillColorSecondaryBrush") });
        }
        if (!_btDiscovering && _btPairingId == null) rows.Children.Add(PageRow("", "Search again", false, StartBtDiscovery));
        Place();
    }

    FrameworkElement NearbyRow(DeviceInformation d, bool le)
    {
        bool pairing = d.Id == _btPairingId;
        string sub = d.Id == _btPairNoteId && _btPairNote != null ? _btPairNote : pairing ? L.T("Pairing…") : le ? "Bluetooth LE" : "Bluetooth";
        var row = PageRow("", d.Name, false, () => { if (_btPairingId == null) _ = PairAsync(d); }, sub);
        if (_btConfirm is not { } ask || ask.Id != d.Id) return row;
        // a PIN shown on the device too: the user compares and says yes or no
        var yes = new Button { Content = L.T("Pair"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var no = new Button { Content = L.T("Cancel") };
        yes.Click += (_, _) => ask.Answer.TrySetResult(true);
        no.Click += (_, _) => ask.Answer.TrySetResult(false);
        return new StackPanel
        {
            Children =
            {
                row,
                new StackPanel
                {
                    Margin = new Thickness(36, 2, 8, 8), Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = L.T("Does the device show the PIN {0}?", ask.Pin), FontSize = 12.5, TextWrapping = TextWrapping.Wrap },
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } }
                    }
                }
            }
        };
    }

    async Task PairAsync(DeviceInformation d)
    {
        _btPairingId = d.Id;
        _btPairNote = _btPairNoteId = null;
        StopBtDiscovery();                                               // looking on while pairing slows it down
        RenderNearby();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var custom = d.Pairing.Custom;
        void Requested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs e)
        {
            switch (e.PairingKind)
            {
                case DevicePairingKinds.ConfirmOnly:
                    e.Accept();
                    break;
                case DevicePairingKinds.DisplayPin:
                    // the PIN is typed on the device (a keyboard): it is shown in the row
                    Ui.Post(() => { _btPairNote = L.T("Type {0} on the device and press Enter", e.Pin); _btPairNoteId = d.Id; RenderNearby(); });
                    e.Accept();
                    break;
                case DevicePairingKinds.ConfirmPinMatch:
                    var deferral = e.GetDeferral();
                    var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Ui.Post(() => { _btConfirm = (d.Id, e.Pin, answer); RenderNearby(); });
                    _ = answer.Task.ContinueWith(t =>
                    {
                        if (t.Result) e.Accept();
                        deferral.Complete();
                        Ui.Post(() => { _btConfirm = null; RenderNearby(); });
                    });
                    break;
            }
        }
        custom.PairingRequested += Requested;
        DevicePairingResultStatus status;
        try
        {
            var result = await custom.PairAsync(DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin | DevicePairingKinds.ConfirmPinMatch, DevicePairingProtectionLevel.Default);
            status = result.Status;
        }
        catch (Exception ex) { Log.Write("Bluetooth pair", ex); status = DevicePairingResultStatus.Failed; }
        finally { custom.PairingRequested -= Requested; }
        Diag.Write("Bluetooth", $"pair: {status} in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
        _btPairingId = null;
        _btConfirm?.Answer.TrySetResult(false);
        _btConfirm = null;
        switch (status)
        {
            case DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired:
                _btFound?.Remove(d.Id);
                await BuildBluetoothAsync();                             // now among the paired devices (Windows connects it)
                return;
            case DevicePairingResultStatus.PairingCanceled or DevicePairingResultStatus.RejectedByHandler:
                _btPairNote = L.T("Pairing cancelled.");
                break;
            case DevicePairingResultStatus.RequiredHandlerNotRegistered or DevicePairingResultStatus.AuthenticationNotAllowed:
                // a PIN to type here (old keyboards, car kits): Windows asks for it in its settings
                _btPairNote = L.T("This device pairs in the settings of Windows.");
                Open("ms-settings:bluetooth");
                break;
            case DevicePairingResultStatus.AuthenticationTimeout or DevicePairingResultStatus.ConnectionRejected or DevicePairingResultStatus.NoSupportedProfiles:
            default:
                _btPairNote = L.T("Could not pair ({0}). Put the device into pairing mode and try again.", status);
                break;
        }
        _btPairNoteId = d.Id;
        RenderNearby();
    }
}
