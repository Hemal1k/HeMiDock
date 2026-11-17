using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HeMiDock;

/// <summary>
/// Where the windows of chosen apps open: in a zone of the current layout or maximized (Settings → Window manager).
/// Stored as "chrome.exe=1|code.exe=max" in <see cref="DockSettings.AppRules"/>. Windows has no way to move a window of another app to a virtual desktop.
/// </summary>
static class AppRules
{
    public const string Maximized = "max";
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);

    const uint EVENT_OBJECT_SHOW = 0x8002;
    const int WS_CAPTION = 0xC00000;
    static readonly WinEventProc Proc = OnShow;
    static IntPtr _hook;
    static readonly HashSet<IntPtr> Seen = new();

    public static Dictionary<string, string> All()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in DockSettings.Current.AppRules.Split('|', StringSplitOptions.RemoveEmptyEntries))
            if (part.IndexOf('=') is > 0 and var eq) map[part[..eq]] = part[(eq + 1)..];
        return map;
    }

    public static void Set(string exe, string place)
    {
        var map = All();
        if (place == null) map.Remove(exe); else map[exe] = place;
        DockSettings.Current.AppRules = string.Join("|", map.Select(r => $"{r.Key}={r.Value}"));
    }

    public static void Apply()
    {
        bool on = DockSettings.Current.AppRules.Length > 0;
        if (on == (_hook != IntPtr.Zero)) return;
        if (on) _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, Proc, 0, 0, 2);   // out of context, not our own windows
        else { UnhookWinEvent(_hook); _hook = IntPtr.Zero; Seen.Clear(); }
    }

    static void OnShow(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
    {
        if (obj != 0 || child != 0 || hwnd == IntPtr.Zero || Seen.Contains(hwnd)) return;
        try
        {
            if ((Native.GetWindowLong(hwnd, -16) & WS_CAPTION) != WS_CAPTION || GetWindow(hwnd, 4) != IntPtr.Zero) return;   // only main windows
            if (Seen.Count > 2000) Seen.Clear();
            Seen.Add(hwnd);
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            if (!All().TryGetValue(p.ProcessName + ".exe", out var place)) return;
            // The window is seen only at its place: it is made invisible at once and placed, placed again after the app has put it
            // where it wanted (some do that a moment after they show it), and only then made visible – before, it was seen
            // opening at its own place and jumping to the rule 400 ms later.
            var hidden = Invisible(hwnd);
            Place(hwnd, place);
            int ticks = 0;
            var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            later.Tick += (_, _) =>
            {
                ticks++;
                if (ticks is 1 or 3) Place(hwnd, place);                // at 120 ms and at 360 ms
                if (ticks < 3) return;
                later.Stop();
                Visible(hwnd, hidden);
            };
            later.Start();
        }
        catch { }                                                      // a window of an app that already ended, or of a higher privilege
    }

    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(IntPtr h, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr h, IntPtr rect, IntPtr region, uint flags);
    [DllImport("user32.dll", SetLastError = true)] static extern int SetWindowLong(IntPtr h, int index, int value);
    const int GWL_EXSTYLE = -20, WS_EX_LAYERED = 0x80000;
    const uint LWA_ALPHA = 2;

    /// <summary>What a window was like before it was made invisible: null when it could not be (a window of an administrator app,
    /// a window that draws its own transparency) – it is then placed without it, as before.</summary>
    sealed record Hidden(bool WasLayered, byte Alpha, uint Flags, uint Key);

    static Hidden Invisible(IntPtr hwnd)
    {
        try
        {
            int ex = Native.GetWindowLong(hwnd, GWL_EXSTYLE);
            bool layered = (ex & WS_EX_LAYERED) != 0;
            byte alpha = 255; uint flags = 0, key = 0;
            if (layered && !GetLayeredWindowAttributes(hwnd, out key, out alpha, out flags)) return null;   // per-pixel transparency of its own
            if (!layered && SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED) == 0) return null;        // refused (higher privilege)
            if (!SetLayeredWindowAttributes(hwnd, key, 0, flags | LWA_ALPHA))
            {
                if (!layered) SetWindowLong(hwnd, GWL_EXSTYLE, ex);
                return null;
            }
            return new Hidden(layered, (flags & LWA_ALPHA) != 0 ? alpha : (byte)255, flags, key);
        }
        catch { return null; }
    }

    /// <summary>The window as it was: its own transparency back, or not layered again (and drawn anew).</summary>
    static void Visible(IntPtr hwnd, Hidden was)
    {
        if (was == null) return;
        try
        {
            if (was.WasLayered) SetLayeredWindowAttributes(hwnd, was.Key, was.Alpha, was.Flags);
            else
            {
                SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
                SetWindowLong(hwnd, GWL_EXSTYLE, Native.GetWindowLong(hwnd, GWL_EXSTYLE) & ~WS_EX_LAYERED);
                RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, 0x1 | 0x4 | 0x80 | 0x100);   // INVALIDATE | ERASE | ALLCHILDREN | FRAME
            }
        }
        catch { }
    }

    static void Place(IntPtr hwnd, string place)
    {
        if (place == Maximized) Native.ShowWindow(hwnd, 3);
        else if (int.TryParse(place, out int zone)) WindowManager.PlaceInZone(hwnd, zone);
    }
}

/// <summary>The weather of a chosen city among the status icons, from the free service Open-Meteo (only while it is switched on).</summary>
static class Weather
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static DispatcherTimer _timer;
    static (string City, double Lat, double Lon)? _place;

    public static string Text { get; private set; } = "";
    public static string Glyph { get; private set; } = "";
    public static string Tip { get; private set; } = "";
    public static event Action Changed;

    public static void Apply()
    {
        var s = DockSettings.Current;
        bool on = s.WeatherOn && s.WeatherCity.Trim().Length > 0;
        if (!on)
        {
            _timer?.Stop();
            _timer = null;
            if (Text.Length > 0) { Text = ""; Changed?.Invoke(); }
            return;
        }
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            _timer.Tick += (_, _) => _ = RefreshAsync();
            _timer.Start();
            _ = RefreshAsync();
        }
        else if (_place?.City != s.WeatherCity.Trim()) _ = RefreshAsync();
    }

    static async Task RefreshAsync()
    {
        string city = DockSettings.Current.WeatherCity.Trim();
        try
        {
            if (_place?.City != city)
            {
                _place = (city, 0, 0);                                 // the city changes letter by letter: asked only once for each text
                await Task.Delay(1500);
                if (DockSettings.Current.WeatherCity.Trim() != city) return;
                // "Name, Region, Country" chosen from the suggestions: the place of that region (several towns share a name)
                var parts = city.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var places = await SearchAsync(parts.FirstOrDefault() ?? city, 10);
                var place = places.FirstOrDefault(p => p.Label.Equals(city, StringComparison.OrdinalIgnoreCase)) ?? places.FirstOrDefault();
                if (place == null) { Show("", L.T("City not found: {0}", city), ""); return; }
                _place = (city, place.Lat, place.Lon);
            }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.open-meteo.com/v1/forecast?current=temperature_2m,weather_code&latitude={_place.Value.Lat.ToString(inv)}&longitude={_place.Value.Lon.ToString(inv)}"));
            var now = doc.RootElement.GetProperty("current");
            int temp = (int)Math.Round(now.GetProperty("temperature_2m").GetDouble());
            var (glyph, what) = Describe(now.GetProperty("weather_code").GetInt32());
            Show($"{temp} °C", $"{city}: {temp} °C, {L.T(what)}", glyph);
        }
        catch (Exception ex)
        {
            Log.Write("Weather", ex);
            Show("", L.T("The weather could not be loaded."), "");
        }
    }

    /// <summary>A place found by the service: "Praha, Hlavní město Praha, Česko" and where it is.</summary>
    public sealed record Place(string Label, double Lat, double Lon);

    /// <summary>Places whose name starts with the text (in Czech), for the suggestions of the settings.</summary>
    public static async Task<List<Place>> SearchAsync(string name, int count = 8)
    {
        var list = new List<Place>();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2) return list;
        try
        {
            using var found = JsonDocument.Parse(await Http.GetStringAsync($"https://geocoding-api.open-meteo.com/v1/search?count={count}&language=cs&name={Uri.EscapeDataString(name.Trim())}"));
            if (!found.RootElement.TryGetProperty("results", out var results)) return list;
            foreach (var r in results.EnumerateArray())
            {
                string Part(string key) => r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                string label = string.Join(", ", new[] { Part("name"), Part("admin1"), Part("country") }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct());
                if (list.Any(p => p.Label == label)) continue;
                list.Add(new Place(label, r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble()));
            }
        }
        catch (Exception ex) { Log.Write("Weather search", ex); }
        return list;
    }

    static void Show(string text, string tip, string glyph)
    {
        Text = text;
        Tip = tip;
        Glyph = glyph;
        Changed?.Invoke();
    }

    /// <summary>The weather codes of WMO, which the service uses.</summary>
    static (string Glyph, string What) Describe(int code) => code switch
    {
        0 or 1 => ("", "clear"),
        2 => ("", "partly cloudy"),
        3 => ("", "cloudy"),
        45 or 48 => ("", "fog"),
        >= 71 and <= 77 or 85 or 86 => ("", "snow"),
        >= 95 => ("", "storm"),
        _ => ("", "rain")
    };
}

/// <summary>Texts copied since the app started, for the search (only while switched on, kept only in memory; what apps mark as private is skipped).</summary>
static class ClipboardHistory
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    const int Limit = 30;

    static readonly uint Excluded = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"), NoHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    static readonly List<string> Items = new();
    static DispatcherTimer _timer;
    static uint _seen;

    public static void Apply()
    {
        bool on = DockSettings.Current.SearchClipboard;
        if (on == (_timer != null)) return;
        if (on)
        {
            _seen = ClipboardText.GetClipboardSequenceNumber();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Take();
            _timer.Start();
            return;
        }
        _timer.Stop();
        _timer = null;
        Items.Clear();
    }

    static void Take()
    {
        uint now = ClipboardText.GetClipboardSequenceNumber();
        if (now == _seen) return;
        _seen = now;
        try
        {
            if (ClipboardText.Has(Excluded) || ClipboardText.Has(NoHistory) || ClipboardText.Get() is not { } text || string.IsNullOrWhiteSpace(text)) return;
            Items.Remove(text);
            Items.Insert(0, text);
            if (Items.Count > Limit) Items.RemoveAt(Limit);
        }
        catch { }                                                      // another app has the clipboard open just now
    }

    public static IEnumerable<string> Find(Func<string, bool> matches) => Items.Where(matches).Take(5);

    /// <summary>The history here and the one of Windows (Win+V) emptied, and the clipboard itself.</summary>
    public static void ClearAll()
    {
        Items.Clear();
        try { Windows.ApplicationModel.DataTransfer.Clipboard.ClearHistory(); Windows.ApplicationModel.DataTransfer.Clipboard.Clear(); }
        catch (Exception ex) { Log.Write("Clipboard clear", ex); }
    }
}

/// <summary>How Windows uses the monitors now: the same choices as the page Monitors of the control center.</summary>
static class DisplayTopology
{
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, IntPtr pathArray, ref uint modes, IntPtr modeArray, out int topology);
    const uint QDC_DATABASE_CURRENT = 4;

    /// <summary>The argument of DisplaySwitch.exe for the current state; null when Windows does not say.</summary>
    public static string Current()
    {
        IntPtr pathArray = IntPtr.Zero, modeArray = IntPtr.Zero;
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_DATABASE_CURRENT, out uint paths, out uint modes) != 0) return null;
            pathArray = Marshal.AllocHGlobal((int)paths * 72);        // DISPLAYCONFIG_PATH_INFO
            modeArray = Marshal.AllocHGlobal((int)modes * 64);        // DISPLAYCONFIG_MODE_INFO
            if (QueryDisplayConfig(QDC_DATABASE_CURRENT, ref paths, pathArray, ref modes, modeArray, out int topology) != 0) return null;
            return topology switch { 1 => "/internal", 2 => "/clone", 4 => "/extend", 8 => "/external", _ => null };
        }
        catch { return null; }
        finally
        {
            if (pathArray != IntPtr.Zero) Marshal.FreeHGlobal(pathArray);
            if (modeArray != IntPtr.Zero) Marshal.FreeHGlobal(modeArray);
        }
    }
}
