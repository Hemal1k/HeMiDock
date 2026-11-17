namespace HeMiDock;

/// <summary>
/// One place for what changes the status icons, for the dock, the top bar and the control center together: the events of Windows
/// (the network, the charger, the weather) and a reading every 2 s (the Wi-Fi signal, the battery, the microphone or camera in
/// use…). Each change is read once (<see cref="StatusData"/>) and handed to everyone – before, every dock and the top bar had their
/// own subscriptions and timers and asked for the same reading each. Only runs while someone listens.
/// </summary>
static class StatusHub
{
    static Action<StatusSnapshot> _changed;
    static DispatcherTimer _timer, _netQuiet;
    static bool _reading, _again;

    /// <summary>A fresh state of the status icons (on the thread of the interface).</summary>
    public static event Action<StatusSnapshot> Changed
    {
        add { _changed += value; Start(); }
        remove { _changed -= value; if (_changed == null && _network == null) Stop(); }
    }

    static Action _network;

    /// <summary>The network changed (connected, disconnected, another Wi-Fi…) – once for a burst of the events of Windows (on the thread of the interface).</summary>
    public static event Action NetworkChanged
    {
        add { _network += value; Start(); }
        remove { _network -= value; if (_changed == null && _network == null) Stop(); }
    }

    /// <summary>A reading now (a click, the wheel, a changed setting): joins a reading that runs, or reads once more after it.</summary>
    public static void Refresh() => Ui.Post(ReadNow);

    static async void ReadNow()
    {
        if (_reading) { _again = true; return; }
        _reading = true;
        try
        {
            if (await StatusData.ReadAsync() is { } now) _changed?.Invoke(now);
        }
        catch (Exception ex) { Log.Write("Status hub", ex); }
        finally
        {
            _reading = false;
            if (_again) { _again = false; ReadNow(); }
        }
    }

    static bool _started;

    static void Start()
    {
        if (_started) return;
        _started = true;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => { if (_changed != null) ReadNow(); };   // only while status icons are shown
        _timer.Start();
        // the network: three sources of Windows, often several events within a moment – one notice 150 ms after the last one
        _netQuiet = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _netQuiet.Tick += (_, _) => { _netQuiet.Stop(); _network?.Invoke(); if (_changed != null) ReadNow(); };
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetwork;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailability;
        Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += OnNetworkStatus;
        // the charger plugged in or out, the battery, sleep and wake
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPower;
        Weather.Changed += Refresh;
        Diag.Write("Status", "hub started");
    }

    static void Stop()
    {
        if (!_started) return;
        _started = false;
        _timer?.Stop();
        _netQuiet?.Stop();
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetwork;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailability;
        Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged -= OnNetworkStatus;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPower;
        Weather.Changed -= Refresh;
        Diag.Write("Status", "hub stopped");
    }

    static void OnNetwork(object sender, EventArgs e) => NetworkSoon();
    static void OnNetworkAvailability(object sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e) => NetworkSoon();
    static void OnNetworkStatus(object sender) => NetworkSoon();
    static void OnPower(object sender, Microsoft.Win32.PowerModeChangedEventArgs e) => Refresh();

    /// <summary>Raised on threads of Windows: the notice waits on the thread of the interface for the burst to end.</summary>
    static void NetworkSoon() => Ui.Post(() => { if (!_started) return; _netQuiet.Stop(); _netQuiet.Start(); });
}
