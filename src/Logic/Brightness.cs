using System.Management;
using System.Runtime.InteropServices;

namespace HeMiDock;

/// <summary>
/// Display brightness for the control center: built-in displays (laptops, tablets) through WMI, external monitors through
/// DDC/CI when the monitor allows it. Everything here is slow (WMI / monitor bus) – call it off the UI thread.
/// </summary>
static class DisplayBrightness
{
    enum Kind { None, Wmi, Ddc }

    static Kind _kind;
    static readonly object Gate = new();                         // one monitor command at a time (DDC/CI is a slow bus)

    /// <summary>Current brightness in % – null when no display can be controlled.</summary>
    public static int? Get()
    {
        lock (Gate)
        {
            if (DisplayTopology.Current() != "/external" && WmiGet() is int w) { _kind = Kind.Wmi; return w; }   // the built-in display is off with "second screen only"
            if (DdcGet() is int d) { _kind = Kind.Ddc; return d; }
            _kind = Kind.None;
            return null;
        }
    }

    /// <summary>Sets the brightness (%) on the display(s) found by the last <see cref="Get"/>.</summary>
    public static void Set(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        lock (Gate)
        {
            try
            {
                if (_kind == Kind.Wmi) WmiSet((byte)percent);
                else if (_kind == Kind.Ddc) DdcSet(percent);
            }
            catch (Exception ex) { Log.Write("DisplayBrightness.Set", ex); }
        }
    }

    // ───────────────────────── built-in displays (WMI) ─────────────────────────

    static int? WmiGet()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in searcher.Get())
                using (o) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return null;
    }

    static void WmiSet(byte value)
    {
        using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
        foreach (ManagementObject o in searcher.Get())
            using (o) o.InvokeMethod("WmiSetBrightness", new object[] { 1u, value });
    }

    // ───────────────────────── external monitors (DDC/CI) ─────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PHYSICAL_MONITOR
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    delegate bool MonitorProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorProc proc, IntPtr data);
    [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] static extern bool GetMonitorBrightness(IntPtr monitor, out uint min, out uint current, out uint max);
    [DllImport("dxva2.dll")] static extern bool SetMonitorBrightness(IntPtr monitor, uint value);

    /// <summary>Runs <paramref name="action"/> for every physical monitor that answers brightness requests.</summary>
    static void ForEachDdc(Func<IntPtr, uint, uint, uint, bool> action)
    {
        var handles = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) => { handles.Add(h); return true; }, IntPtr.Zero);
        foreach (var hMon in handles)
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint n) || n == 0) continue;
            var monitors = new PHYSICAL_MONITOR[n];
            if (!GetPhysicalMonitorsFromHMONITOR(hMon, n, monitors)) continue;
            try
            {
                foreach (var m in monitors)
                    if (GetMonitorBrightness(m.Handle, out uint min, out uint cur, out uint max) && max > min
                        && !action(m.Handle, min, cur, max)) return;
            }
            finally { DestroyPhysicalMonitors(n, monitors); }
        }
    }

    static int? DdcGet()
    {
        int? level = null;
        try
        {
            ForEachDdc((_, min, cur, max) =>
            {
                level = (int)Math.Round((cur - min) * 100.0 / (max - min));
                return false;                                        // the first monitor is enough
            });
        }
        catch { }
        return level;
    }

    static void DdcSet(int percent) =>
        ForEachDdc((h, min, _, max) =>
        {
            SetMonitorBrightness(h, (uint)Math.Round(min + (max - min) * percent / 100.0));
            return true;                                             // all monitors that allow it
        });
}

/// <summary>
/// "Change brightness automatically when lighting changes" (power plan setting of Windows) – only on devices with a light
/// sensor, like in Windows Settings.
/// </summary>
static class AutoBrightness
{
    static Guid _subVideo = new("7516b95f-f776-4464-8c53-06167f40cc99");
    static Guid _adaptive = new("fbd9aa66-9553-4097-ba44-ed6e9d65eab8");

    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr mem);

    /// <summary>Is there an ambient light sensor (without one Windows has no automatic brightness)?</summary>
    public static bool Supported
    {
        get
        {
            try { return Windows.Devices.Sensors.LightSensor.GetDefault() != null; }
            catch { return false; }
        }
    }

    static bool ActiveScheme(out Guid scheme)
    {
        scheme = Guid.Empty;
        if (PowerGetActiveScheme(IntPtr.Zero, out var p) != 0 || p == IntPtr.Zero) return false;
        try { scheme = Marshal.PtrToStructure<Guid>(p); return true; }
        finally { LocalFree(p); }
    }

    static bool OnBattery =>
        Native.GetSystemPowerStatus(out var st) && st.ACLineStatus == 0;

    /// <summary>State for the current power source (plugged in / on battery).</summary>
    public static bool IsOn
    {
        get
        {
            try
            {
                if (!ActiveScheme(out var s)) return false;
                uint v;
                uint r = OnBattery ? PowerReadDCValueIndex(IntPtr.Zero, ref s, ref _subVideo, ref _adaptive, out v)
                                   : PowerReadACValueIndex(IntPtr.Zero, ref s, ref _subVideo, ref _adaptive, out v);
                return r == 0 && v != 0;
            }
            catch { return false; }
        }
    }

    /// <summary>Switches it for both power sources (like the switch in Windows Settings) and applies the plan.</summary>
    public static void Set(bool on)
    {
        try
        {
            if (!ActiveScheme(out var s)) return;
            PowerWriteACValueIndex(IntPtr.Zero, ref s, ref _subVideo, ref _adaptive, on ? 1u : 0u);
            PowerWriteDCValueIndex(IntPtr.Zero, ref s, ref _subVideo, ref _adaptive, on ? 1u : 0u);
            PowerSetActiveScheme(IntPtr.Zero, ref s);
        }
        catch (Exception ex) { Log.Write("AutoBrightness", ex); }
    }
}
