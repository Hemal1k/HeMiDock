using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace HeMiDock;

/// <summary>
/// What the control center shows and in which order: its parts (quick buttons, brightness, sound …) and the quick buttons
/// themselves – set in Settings → Status area → Control center.
/// </summary>
static class QuickLayout
{
    /// <summary>Parts of the control center (id, name) in the default order.</summary>
    public static readonly (string Id, string Name)[] Sections =
    {
        ("tiles", "Quick buttons"), ("brightness", "Display"), ("sound", "Sound"), ("media", "Now playing"), ("power", "Power mode"), ("battery", "Battery")
    };

    /// <summary>Quick buttons (id, name) in the default order (two per row).</summary>
    public static readonly (string Id, string Name)[] Tiles =
    {
        ("net", "Network"), ("cell", "Mobile data"), ("dnd", "Do not disturb"), ("bt", "Bluetooth"), ("airplane", "Airplane mode"), ("night", "Night light"), ("cast", "Cast"), ("access", "Accessibility"),
        ("vpn", "VPN"), ("display", "Monitors"), ("awake", "Keep awake")
    };

    static List<string> Split(string s) => (s ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>The stored order; ids that are not stored (e.g. new in a later version) at their default place at the end.</summary>
    static List<string> Order(string stored, (string Id, string Name)[] all)
    {
        var ids = all.Select(a => a.Id).ToList();
        var order = Split(stored).Where(ids.Contains).Distinct().ToList();
        order.AddRange(ids.Where(i => !order.Contains(i)));
        return order;
    }

    public static List<string> SectionOrder => Order(DockSettings.Current.QuickSections, Sections);
    public static List<string> TileOrder => Order(DockSettings.Current.QuickTiles, Tiles);

    /// <summary>What can move from the control center to the top bar: only what has no icon of its own there (network, Bluetooth, sound, battery, Do not disturb, VPN and what is playing do).</summary>
    public static readonly string[] Movable = { "night", "cast", "access", "display", "awake", "brightness", "power" };

    public static bool OnBar(string id) => DockSettings.Current.TopBarOn && Movable.Contains(id) && Split(DockSettings.Current.TopBarWidgets).Contains(id);

    public static void SetOnBar(string id, bool on)
    {
        var list = Split(DockSettings.Current.TopBarWidgets);
        list.Remove(id);
        if (on) list.Add(id);
        DockSettings.Current.TopBarWidgets = string.Join("|", list);
    }

    public static bool IsShown(string id) => !Split(DockSettings.Current.QuickHidden).Contains(id);

    public static void SetShown(string id, bool shown)
    {
        var hidden = Split(DockSettings.Current.QuickHidden);
        hidden.Remove(id);
        if (!shown) hidden.Add(id);
        DockSettings.Current.QuickHidden = string.Join("|", hidden);
    }

    /// <summary>Moves a part / quick button one place up (-1) or down (+1).</summary>
    public static void Move(string id, int delta)
    {
        bool tile = Tiles.Any(t => t.Id == id);
        var order = tile ? TileOrder : SectionOrder;
        int i = order.IndexOf(id), j = i + delta;
        if (i < 0 || j < 0 || j >= order.Count) return;
        (order[i], order[j]) = (order[j], order[i]);
        if (tile) DockSettings.Current.QuickTiles = string.Join("|", order);
        else DockSettings.Current.QuickSections = string.Join("|", order);
    }
}

/// <summary>
/// "Pin to Start (HeMiDock)" in the right-click menu of files and folders in Explorer (current user only). Windows does
/// not tell other apps about its own "Pin to Start", so HeMiDock adds its own item: it starts HeMiDock.exe --pin-start
/// "file", which hands the file to the running dock.
/// </summary>
static class ExplorerVerb
{
    const string Name = "HeMiDock.PinToStart";
    static readonly string[] Classes = { @"Software\Classes\*\shell\", @"Software\Classes\Directory\shell\" };

    /// <summary>Adds (with the path of this HeMiDock.exe) or removes the menu item.</summary>
    public static void Apply(bool on)
    {
        // the item takes the place of "Pin to Start" of Windows (which pins to the Start of Windows)
        const string Blocked = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked", PinToStart = "{470C0EBD-5D73-4d58-9CED-E91E22E23282}";
        try
        {
            using var b = Registry.CurrentUser.CreateSubKey(Blocked);
            if (on) b.SetValue(PinToStart, "HeMiDock");
            else b.DeleteValue(PinToStart, false);
        }
        catch (Exception ex) { Log.Write("ExplorerVerb", ex); }
        foreach (var cls in Classes)
        {
            try
            {
                if (!on) { Registry.CurrentUser.DeleteSubKeyTree(cls + Name, false); continue; }
                using var k = Registry.CurrentUser.CreateSubKey(cls + Name);
                k.SetValue("MUIVerb", L.T("Pin to Start"));
                k.SetValue("Icon", $"\"{Environment.ProcessPath}\"");
                using var c = k.CreateSubKey("command");
                c.SetValue("", $"\"{Environment.ProcessPath}\" --pin-start \"%1\"");
            }
            catch (Exception ex) { Log.Write("ExplorerVerb", ex); }
        }
    }

    // handing the file from the second HeMiDock.exe to the running one
    public const string SignalName = "HeMiDock_PinSignal";
    static string RequestFile => AppPaths.Of("pin-requests.txt");

    /// <summary>Second instance: leaves the file for the running dock (it is signalled afterwards).</summary>
    public static void Request(string path)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Folder);
            File.AppendAllLines(RequestFile, new[] { path });
        }
        catch (Exception ex) { Log.Write("Pin request", ex); }
    }

    /// <summary>Running dock: pins the files that were handed over.</summary>
    public static void TakeRequests()
    {
        try
        {
            if (!File.Exists(RequestFile)) return;
            var paths = File.ReadAllLines(RequestFile);
            File.Delete(RequestFile);
            foreach (var p in paths.Where(p => File.Exists(p) || Directory.Exists(p))) StartPins.Add(p);
        }
        catch (Exception ex) { Log.Write("Pin requests", ex); }
    }
}

/// <summary>Recycle Bin on the dock: its state (empty / full), icons, emptying and deleting files dropped on it.</summary>
static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHSTOCKICONINFO
    {
        public int cbSize; public IntPtr hIcon; public int iSysImageIndex; public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPath;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBin(string root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);
    [DllImport("shell32.dll")] static extern int SHGetStockIconInfo(uint id, uint flags, ref SHSTOCKICONINFO info);


    /// <summary>Number of items in the Recycle Bin of all drives (-1 when unknown).</summary>
    public static long Count()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.i64NumItems : -1;
    }

    /// <summary>The Windows icon of the Recycle Bin as it is now (empty / full).</summary>
    public static IconImage Icon() => AppShell.FileTypeIcon("::{645FF040-5081-101B-9F08-00AA002F954E}");

    public static void Open() => Fg.Open("explorer.exe", "shell:RecycleBinFolder");

    /// <summary>Empties the Recycle Bin (Windows asks for confirmation).</summary>
    public static void Empty(IntPtr owner)
    {
        try { SHEmptyRecycleBin(owner, null, 0); }
        catch (Exception ex) { Log.Write("RecycleBin.Empty", ex); }
    }

    /// <summary>Moves files / folders to the Recycle Bin (Windows shows its usual errors).</summary>
    public static void Delete(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            try
            {
                if (Directory.Exists(p))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
                else if (File.Exists(p))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
            }
            catch (Exception ex) { Log.Write("RecycleBin.Delete " + p, ex); }
        }
    }
}

/// <summary>
/// Which apps use the microphone / camera right now – Windows notes the start and the end of every use in the registry
/// (the same data as the privacy icon of the taskbar): a use without an end is running.
/// </summary>
static class Privacy
{
    const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    public static List<string> InUse(string capability)                 // "microphone" / "webcam"
    {
        var apps = new List<string>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Store + capability);
            if (root == null) return apps;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                if (k == null) continue;
                if (name == "NonPackaged")
                {
                    foreach (var exe in k.GetSubKeyNames())
                    {
                        using var e = k.OpenSubKey(exe);
                        string path = exe.Replace('#', '\\');
                        // an app that crashed never writes the end of its use – only a program that still runs counts
                        if (Running(e) && IsRunning(path)) apps.Add(Path.GetFileNameWithoutExtension(path));
                    }
                }
                else if (Running(k) && Recent(k)) apps.Add(name.Split('_')[0].Split('.').Last());   // Microsoft.WindowsCamera_8wekyb3d8bbwe → WindowsCamera
            }
        }
        catch (Exception ex) { Log.Write("Privacy " + capability, ex); }
        return apps;
    }

    static bool Running(RegistryKey k) =>
        k?.GetValue("LastUsedTimeStart") is long start && start > 0 && k.GetValue("LastUsedTimeStop") is long stop && stop == 0;

    /// <summary>A Store app use that started within the last day (an older one without an end is left over from a crash).</summary>
    static bool Recent(RegistryKey k) =>
        k.GetValue("LastUsedTimeStart") is long start && DateTime.Now - DateTime.FromFileTime(start) < TimeSpan.FromDays(1);

    /// <summary>Does a process run from exactly this program file?</summary>
    static bool IsRunning(string exe)
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            try
            {
                using (p)
                    if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { return true; }                                    // cannot look into it (other user / elevated) – it runs
        }
        return false;
    }
}

/// <summary>
/// Battery saver of Windows (energy saver). Windows has no public switch for it: it starts by itself when the battery
/// falls below a threshold – a threshold of 100 % turns it on at once, the previous threshold back turns it off.
/// </summary>
static class BatterySaver
{
    const string Threshold = "ESBATTTHRESHOLD";                      // power setting: battery level when the saver starts (%)
    static int _previous = 20;                                       // the threshold before we turned the saver on

    /// <summary>On / off; null while it cannot be used (plugged in – Windows saves only on battery).</summary>
    public static bool? IsOn
    {
        get
        {
            try
            {
                return Windows.System.Power.PowerManager.EnergySaverStatus switch
                {
                    Windows.System.Power.EnergySaverStatus.On => true,
                    Windows.System.Power.EnergySaverStatus.Off => false,
                    _ => null
                };
            }
            catch { return null; }
        }
    }

    public static void Set(bool on)
    {
        try
        {
            if (on && ReadThreshold() is int now && now < 100) _previous = now;
            PowerCfg($"/setdcvalueindex SCHEME_CURRENT SUB_ENERGYSAVER {Threshold} {(on ? 100 : Math.Min(_previous, 99))}");
            PowerCfg("/setactive SCHEME_CURRENT");                    // applies it right away
        }
        catch (Exception ex) { Log.Write("BatterySaver", ex); }
    }

    static int? ReadThreshold()
    {
        // "Current DC Power Setting Index: 0x00000014" – the last hex number of the output
        string text = PowerCfg($"/query SCHEME_CURRENT SUB_ENERGYSAVER {Threshold}");
        var m = System.Text.RegularExpressions.Regex.Matches(text ?? "", "0x([0-9a-fA-F]{8})");
        return m.Count > 0 ? Convert.ToInt32(m[^1].Groups[1].Value, 16) : null;
    }

    static string PowerCfg(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("powercfg.exe", args)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true
        };
        using var p = System.Diagnostics.Process.Start(psi);
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(3000);
        return output;
    }
}

/// <summary>Power mode of Windows 11 (Settings → Power: best power efficiency / balanced / best performance).</summary>
static class PowerMode
{
    [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid mode);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(Guid mode);

    public static readonly Guid Efficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid Balanced = Guid.Empty;
    public static readonly Guid Performance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    static Guid? _known;
    static long _readAt = long.MinValue;
    static int _reading;

    /// <summary>
    /// The current mode, null when this device / power plan has no power modes. Asking the power service takes about
    /// 100 ms, which made the control center slow to open: the last answer is used, a new one is asked in the background
    /// when it is older than a few seconds.
    /// </summary>
    public static Guid? Current
    {
        get
        {
            if (_readAt == long.MinValue) Read();                         // the very first time: at once
            else if (Environment.TickCount64 - _readAt > 3000 && Interlocked.Exchange(ref _reading, 1) == 0)
                Task.Run(() => { try { Read(); } finally { _reading = 0; } });
            return _known;
        }
    }

    static void Read()
    {
        try { _known = PowerGetEffectiveOverlayScheme(out var g) == 0 ? g : null; }
        catch { _known = null; }
        _readAt = Environment.TickCount64;
    }

    public static bool Set(Guid mode)
    {
        try
        {
            bool ok = PowerSetActiveOverlayScheme(mode) == 0;
            if (ok) { _known = mode; _readAt = Environment.TickCount64; }
            return ok;
        }
        catch (Exception ex) { Log.Write("PowerMode", ex); return false; }
    }
}

