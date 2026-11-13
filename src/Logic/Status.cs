using System.Net.NetworkInformation;
using NAudio.CoreAudioApi;

namespace HeMiDock;

/// <summary>The time and the date of the clock, the same for the dock and the top bar.</summary>
static class ClockText
{
    static DockSettings S => DockSettings.Current;

    public static string Time(DateTime now)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        string format = S.ClockMode switch
        {
            1 => S.ShowSeconds ? "HH:mm:ss" : "HH:mm",
            2 => S.ShowSeconds ? "h:mm:ss tt" : "h:mm tt",
            _ => S.ShowSeconds ? culture.DateTimeFormat.LongTimePattern : culture.DateTimeFormat.ShortTimePattern
        };
        return now.ToString(format, culture);
    }

    /// <summary>Is the month before the day: by the setting, or by the region of Windows.</summary>
    public static bool MonthFirst => S.DateOrder switch
    {
        1 => false,
        2 => true,
        _ => System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern.TrimStart().StartsWith('M')
    };

    /// <summary>The date by its format; the top bar (one line) shows the short date with its weekday.</summary>
    public static string Date(DateTime now, bool topBar = false)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        bool month = MonthFirst, byRegion = S.DateOrder == 0;
        string format = S.DateMode switch
        {
            0 => month ? "ddd M/d" : "ddd d. M.",
            1 => byRegion ? "D" : month ? "dddd, MMMM d, yyyy" : "dddd d. MMMM yyyy",
            3 => S.CustomDateFormat,
            _ when topBar => month ? "ddd M/d" : "ddd d. M.",
            _ => byRegion ? "d" : month ? "MM/dd/yyyy" : "d. M. yyyy"
        };
        try { return now.ToString(format, culture); } catch { return now.ToString("d", culture); }
    }
}

/// <summary>The state behind the status icons (network, battery), the same in the dock, the top bar and the control center.</summary>
static class StatusInfo
{
    /// <summary>The symbol of the connection now (Segoe Fluent Icons), whether there is one and its description.</summary>
    public static (string Glyph, bool Connected, string Tip) Network()
    {
        bool hasWifi = false;
        NetworkInterface wlan = null, eth = null;
        try
        {
            var all = NetworkInterface.GetAllNetworkInterfaces();
            hasWifi = all.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
            bool Connected(NetworkInterface n) => n.OperationalStatus == OperationalStatus.Up
                && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.GetAddressBytes().Any(b => b != 0));
            wlan = all.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && Connected(n));
            eth = all.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet && Connected(n));
        }
        catch { }

        if (wlan != null)
        {
            byte bars = 4;
            string ssid = null;
            try
            {
                var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
                if (profile?.IsWlanConnectionProfile == true) { bars = profile.GetSignalBars() ?? 4; ssid = profile.ProfileName; }
            }
            catch { }
            return (bars <= 1 ? "" : bars == 2 ? "" : bars == 3 ? "" : "", true, "Wi-Fi: " + (ssid ?? wlan.Name));
        }
        if (eth != null) return ("", true, eth.Name);
        return (hasWifi ? "" : "", false, hasWifi ? L.T("Wi-Fi: not connected") : L.T("No connection"));
    }

    /// <summary>The battery: null on a PC without one.</summary>
    public static (int Percent, bool Plugged, bool Charging, bool Saver, int SecondsLeft)? Battery()
    {
        if (!Native.GetSystemPowerStatus(out var st) || st.BatteryFlag == 128 || st.BatteryLifePercent == 255) return null;
        return (Math.Clamp((int)st.BatteryLifePercent, 0, 100), st.ACLineStatus == 1, (st.BatteryFlag & 8) != 0, st.SystemStatusFlag == 1, st.BatteryLifeTime);
    }
}

/// <summary>The volume of the default output and the microphone (Core Audio).</summary>
static class AudioControl
{
    static readonly MMDeviceEnumerator Devices = new();
    static readonly object Gate = new();

    /// <summary>
    /// The default output or input, kept with its last state: Windows says when the volume changes and when another device
    /// becomes the default. Before, every reading opened the device, its volume and its name again – twice a second while
    /// the control center was open, which kept the thread of the interface busy (12 % of a core).
    /// </summary>
    sealed class Endpoint
    {
        readonly DataFlow _flow;
        readonly Role _role;
        MMDevice _device;
        (float Level, bool Mute, string Name)? _state;

        public Endpoint(DataFlow flow, Role role) { _flow = flow; _role = role; }

        readonly object _stateGate = new();

        public MMDevice Device
        {
            get
            {
                lock (Gate)
                {
                    if (_device != null) return _device;
                    MMDevice device = null;
                    try
                    {
                        device = Devices.GetDefaultAudioEndpoint(_flow, _role);
                        string name = device.FriendlyName;
                        var volume = device.AudioEndpointVolume;
                        lock (_stateGate) _state = (volume.MasterVolumeLevelScalar, volume.Mute, name);
                        // only the short lock of the state here: Windows waits for this to end when the device is let go
                        volume.OnVolumeNotification += data =>
                        {
                            lock (_stateGate) { if (_device == device) _state = (data.MasterVolume, data.Muted, name); }
                            Changed?.Invoke();
                        };
                        _device = device;
                    }
                    catch { _device = null; lock (_stateGate) _state = null; }
                    return _device;
                }
            }
        }

        public (float Level, bool Mute, string Name)? State { get { _ = Device; lock (_stateGate) return _state; } }

        /// <summary>
        /// Another device is the default now (or one was removed): taken again at the next use. The old one is let go in the
        /// background and outside the lock: letting it go waits for its volume notification to end, which waited for the lock
        /// – the app stopped responding when the sound device was switched.
        /// </summary>
        public void Forget()
        {
            MMDevice old;
            lock (Gate)
            {
                old = _device;
                _device = null;
                lock (_stateGate) _state = null;
            }
            if (old != null) Task.Run(() => { try { old.Dispose(); } catch { } });
            Changed?.Invoke();
        }
    }

    /// <summary>The volume, the mute or the default device changed (raised on a thread of Windows, not of the interface).</summary>
    public static event Action Changed;

    static readonly Endpoint Out = new(DataFlow.Render, Role.Multimedia), Mic = new(DataFlow.Capture, Role.Console);
    static readonly DefaultWatch Watch = Register();

    sealed class DefaultWatch : NAudio.CoreAudioApi.Interfaces.IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { Out.Forget(); Mic.Forget(); }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { Out.Forget(); Mic.Forget(); }
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { if (flow == DataFlow.Render) Out.Forget(); else Mic.Forget(); }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    static DefaultWatch Register()
    {
        var w = new DefaultWatch();
        try { Devices.RegisterEndpointNotificationCallback(w); } catch { }
        return w;
    }

    public static (float Level, bool Mute, string Name)? Now() => Out.State;

    public static (float Level, bool Mute)? MicNow() => Mic.State is { } m ? (m.Level, m.Mute) : null;

    /// <summary>The name of the default microphone (null when there is none).</summary>
    public static string MicName() => Mic.State?.Name;

    static void Use(Endpoint e, Action<AudioEndpointVolume> act)
    {
        try { if (e.Device is { } d) act(d.AudioEndpointVolume); }
        catch { e.Forget(); }
    }

    public static void SetLevel(float level) => Use(Out, v =>
    {
        v.MasterVolumeLevelScalar = Math.Clamp(level, 0, 1);
        if (v.Mute && level > 0) v.Mute = false;
    });

    public static void SetMicLevel(float level) => Use(Mic, v => v.MasterVolumeLevelScalar = Math.Clamp(level, 0, 1));

    public static void ToggleMute() => Use(Out, v => v.Mute = !v.Mute);

    public static void ToggleMicMute() => Use(Mic, v => v.Mute = !v.Mute);

    /// <summary>The symbol of the volume (Segoe Fluent Icons).</summary>
    public static string Glyph(float level, bool mute) => mute || level <= 0.001f ? "" : level < 0.34f ? "" : level < 0.67f ? "" : "";
}
