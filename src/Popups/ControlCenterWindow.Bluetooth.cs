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
    // ───────────────────────── Bluetooth ─────────────────────────

    void ShowBluetooth()
    {
        ShowPage("bt", "Bluetooth");
        _ = BuildBluetoothAsync();
    }

    int _btBuild;

    async Task BuildBluetoothAsync()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int build = ++_btBuild;                                          // a newer listing wins over a slower older one
        _bt ??= await RadioSwitch.FindAsync(RadioKind.Bluetooth);        // the radio may not have been known yet: the switch is always there
        if (build != _btBuild || _openPage != "bt") return;
        _pageActions.Children.Clear();
        bool on = _bt?.State == RadioState.On;
        HeaderSwitch(on, async v =>
        {
            if (_bt == null) { Open("ms-settings:bluetooth"); return; }
            // the note "turned off" goes, the turning ring comes while Windows lists the devices
            if (v) { _page.Children.Clear(); Searching("Searching for devices…"); }
            await SwitchRadio(_bt, v);
            await BuildBluetoothAsync();
        });
        Footer("More Bluetooth settings", "ms-settings:bluetooth");
        if (!on)
        {
            StopBtDiscovery();
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
        Diag.Write("Bluetooth", $"{devices.Count} devices ({devices.Count(d => d.Connected)} connected) in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
        if (devices.Count == 0) Note("No paired devices.");
        var card = devices.Count == 0 ? null : PageCard();
        foreach (var d in devices.OrderByDescending(d => d.Connected).ThenBy(d => d.Name))
            card.Children.Add(PageRow("", d.Name, d.Connected, () => ConnectBluetooth(d.Id, d.Connected),
                L.T(d.Connected ? "Connected" : "Paired") + (d.Battery is { } percent ? $" · {L.T("Battery")} {percent} %" : "")));
        AddNearbySection();                                              // "Add device" or the devices found nearby
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
}
