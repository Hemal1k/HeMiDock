using Windows.Devices.Radios;

namespace HeMiDock;

#if DEBUG
/// <summary>
/// Debug build only. Hardware this PC does not have, for testing: HeMiDock.exe --simulate battery=34,charging,saver,wifi,bluetooth,cellular.
/// Without the argument nothing here has any effect.
/// </summary>
static class Simulation
{
    static readonly string[] Parts = Read();

    public static readonly bool Battery = Has("battery"), Wifi = Has("wifi"), Bluetooth = Has("bluetooth"), Cellular = Has("cellular");
    static readonly bool Charging = Has("charging"), Saver = Has("saver");
    static readonly byte Percent = byte.TryParse(Parts.FirstOrDefault(p => p.StartsWith("battery="))?[8..], out byte b) ? Math.Min(b, (byte)100) : (byte)50;

    /// <summary>The password of the secured networks below.</summary>
    public const string WifiPassword = "heslo1234";
    public static string WifiConnected { get; set; } = "Domácí síť";
    public static readonly (string Ssid, byte Bars, bool Secured)[] WifiNetworks = { ("Domácí síť", 4, true), ("Kavárna", 2, false), ("Soused 5G", 1, true) };
    public static readonly List<(string Name, bool Connected)> BluetoothDevices = new() { ("Sluchátka", true), ("Myš", false) };

    static string[] Read()
    {
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "--simulate");
        return at >= 0 && at + 1 < args.Length ? args[at + 1].ToLowerInvariant().Split(',') : Array.Empty<string>();
    }

    static bool Has(string part) => Parts.Any(p => p == part || p.StartsWith(part + "="));

    public static Native.SYSTEM_POWER_STATUS PowerStatus() => new()
    {
        ACLineStatus = (byte)(Charging ? 1 : 0),
        BatteryFlag = (byte)(Charging ? 8 : Percent <= 20 ? 2 : 1),
        BatteryLifePercent = Percent,
        SystemStatusFlag = (byte)(Saver ? 1 : 0),
        BatteryLifeTime = Charging ? -1 : Percent * 216,
        BatteryFullLifeTime = -1
    };
}

#endif

/// <summary>The Wi-Fi, Bluetooth or mobile (SIM / eSIM) radio of Windows; in the debug build it can be a simulated one.</summary>
sealed class RadioSwitch
{
    readonly Radio _real;
#if DEBUG
    bool _on = true;
    static readonly Dictionary<RadioKind, RadioSwitch> Simulated = new();
#endif

    public event Action StateChanged;

    RadioSwitch(Radio real)
    {
        _real = real;
        if (real != null) real.StateChanged += (_, _) => StateChanged?.Invoke();
    }

#if DEBUG
    public RadioState State => _real?.State ?? (_on ? RadioState.On : RadioState.Off);
#else
    public RadioState State => _real.State;
#endif

    public async Task SetStateAsync(RadioState state)
    {
        if (_real != null) { await _real.SetStateAsync(state); return; }
#if DEBUG
        _on = state == RadioState.On;
        StateChanged?.Invoke();
#endif
    }

    /// <summary>The radio of that kind; null when the device has none or Windows does not allow access to it.</summary>
    public static async Task<RadioSwitch> FindAsync(RadioKind kind)
    {
#if DEBUG
        if (kind == RadioKind.WiFi && Simulation.Wifi || kind == RadioKind.Bluetooth && Simulation.Bluetooth || kind == RadioKind.MobileBroadband && Simulation.Cellular)
        {
            if (!Simulated.TryGetValue(kind, out var fake)) Simulated[kind] = fake = new RadioSwitch(null);
            return fake;
        }
#endif
        if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return null;
        var radio = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind);
        return radio == null ? null : new RadioSwitch(radio);
    }
}
