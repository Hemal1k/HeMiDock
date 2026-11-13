using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HeMiDock;

/// <summary>
/// Notifications of Windows at the clock or in another corner of the screen (Settings → General, automatic by
/// default). Windows shows them in its own window ("New notification" of ShellExperienceHost) and puts it back into the
/// corner for every notification – it is watched with window events and moved at once, to where the calendar opens.
/// </summary>
static class ToastMover
{
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);

    const uint EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_LOCATIONCHANGE = 0x800B, EVENT_OBJECT_UNCLOAKED = 0x8018;
    const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    const uint SwpMoveOnly = 0x1 | 0x4 | 0x10;                        // NOSIZE | NOZORDER | NOACTIVATE

    static readonly WinEventProc Proc = OnEvent;                     // kept alive while hooked
    static IntPtr _show, _move, _uncloak;
    static readonly Dictionary<IntPtr, bool> Known = new();          // window → is the notification window (it lives long)
    static IntPtr _toast;                                            // the notification window last moved

    /// <summary>The windows of notifications that are moved, with the shadow room around them (DIP): another app needs only one more line here.</summary>
    static readonly (Func<IntPtr, bool> IsToast, int ShadowDip)[] Sources =
    {
        (IsToastWindow, 14),
        (IsSteamToast, 0)
    };

    public static void Apply(bool on)
    {
        if (on == (_show != IntPtr.Zero)) return;
        if (on)
        {
            _show = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, Proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            _move = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, Proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            // the window of the notifications is shown while still cloaked (invisible) and uncloaked only later – it was not
            // a notification at the moment it was shown, and stayed in the corner (another notebook)
            _uncloak = SetWinEventHook(EVENT_OBJECT_UNCLOAKED, EVENT_OBJECT_UNCLOAKED, IntPtr.Zero, Proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            Diag.Write("Notifications", "shown at the dock");
            return;
        }
        UnhookWinEvent(_show);
        UnhookWinEvent(_move);
        UnhookWinEvent(_uncloak);
        _show = _move = _uncloak = IntPtr.Zero;
        // back into the corner right away (Windows itself would do it only with the next notification)
        if (_toast != IntPtr.Zero && Native.IsWindowVisible(_toast) && Native.GetWindowRect(_toast, out var r))
        {
            var work = MonitorOf(_toast).Work;
            Native.SetWindowPos(_toast, IntPtr.Zero, work.Right - (r.Right - r.Left), work.Bottom - (r.Bottom - r.Top), 0, 0, SwpMoveOnly);
        }
        Diag.Write("Notifications", "shown in the corner");
    }

    static MonInfo MonitorOf(IntPtr h)
    {
        var handle = Native.MonitorFromWindow(h, 2);
        var all = Monitors.All();
        return all.FirstOrDefault(m => m.Handle == handle) ?? all[0];
    }

    static void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
    {
        if (obj != 0 || child != 0 || hwnd == IntPtr.Zero) return;   // only whole windows (not the cursor, carets …)
        try
        {
            foreach (var (isToast, shadowDip) in Sources)
                if (isToast(hwnd)) { Place(hwnd, shadowDip); break; }
        }
        catch (Exception ex) { Log.Write("ToastMover", ex); }
    }

    /// <summary>The window of the notification banners: a CoreWindow of ShellExperienceHost, always on top, not the full-height notification center.</summary>
    static bool IsToastWindow(IntPtr h)
    {
        if (!Known.TryGetValue(h, out bool toast))
        {
            var cls = new System.Text.StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            toast = false;
            if (cls.ToString() == "Windows.UI.Core.CoreWindow")
            {
                Native.GetWindowThreadProcessId(h, out uint pid);
                try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); toast = p.ProcessName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Equals("ShellHost", StringComparison.OrdinalIgnoreCase); }   // newer Windows 11 shows them from ShellHost
                catch { }
            }
            if (Known.Count > 500) Known.Clear();
            Known[h] = toast;
        }
        // always on top is checked every time: Windows creates the window without it (a first look would be remembered wrongly)
        if (!toast || (Native.GetWindowLong(h, -20) & 0x8) == 0 || !Native.IsWindowVisible(h) || Native.IsCloaked(h) || !Native.GetWindowRect(h, out var r)) return false;
        var work = MonitorOf(h).Work;
        return r.Bottom - r.Top < (work.Bottom - work.Top) * 0.9;      // the notification center (Win+N) is as high as the screen; a screenshot in a notification makes it tall
    }

    /// <summary>Steam draws its notifications itself, in a window of its own named "notificationtoasts_…"; its name is read every time, Steam sets it after creating the window.</summary>
    static bool IsSteamToast(IntPtr h)
    {
        var text = new System.Text.StringBuilder(64);
        GetClassName(h, text, text.Capacity);
        if (text.ToString() != "SDL_app" || !Native.IsWindowVisible(h)) return false;
        GetWindowText(h, text, text.Capacity);
        return text.ToString().StartsWith("notificationtoasts", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>In the chosen corner, or at the clock of the bottom dock / the top bar, like the calendar (its shadow room overlaps).</summary>
    static void Place(IntPtr h, int shadowDip)
    {
        var mon = MonitorOf(h);
        int place = DockSettings.Current.ToastPlace;
        // at the clock, but no clock to keep to (hidden, another monitor): the corner at the bottom right instead of nothing
        if (place == 1 && Host.ToastAnchor?.Invoke(mon) == null) place = 4;
        if (place != 1)                                                 // a corner of the free part of the screen
        {
            if (!Native.GetWindowRect(h, out var now)) return;
            _toast = h;
            double dpi = Native.MonitorDpi(mon.Handle) ?? 1;
            int inset = (int)Math.Round(8 * dpi) - (int)Math.Round(shadowDip * dpi);   // 8 DIP from the edge, without the shadow room of the window
            var free = mon.Work;
            int tx = place is 3 or 5 ? free.Left + inset : free.Right - (now.Right - now.Left) - inset;
            int ty = place is 2 or 3 ? free.Top + inset : free.Bottom - (now.Bottom - now.Top) - inset;
            if (now.Left != tx || now.Top != ty) Native.SetWindowPos(h, IntPtr.Zero, tx, ty, 0, 0, SwpMoveOnly);
            return;
        }
        if (Host.ToastAnchor?.Invoke(mon) is not { } at || !Native.GetWindowRect(h, out var r)) return;
        _toast = h;
        double scale = Native.MonitorDpi(mon.Handle) ?? 1;
        int w = r.Right - r.Left, ht = r.Bottom - r.Top;
        int shadow = (int)Math.Round(shadowDip * scale), gap = (int)Math.Round(at.GapDip * scale);
        var a = at.Anchor;
        var work = mon.Work;
        int x = (a.Left + a.Right) / 2 - w / 2;
        int y = at.Edge == 3 ? a.Bottom + gap - shadow : a.Top - gap - ht + shadow;   // below the top bar / above the dock
        x = Math.Clamp(x, work.Left - shadow, Math.Max(work.Left - shadow, work.Right - w + shadow));
        y = Math.Clamp(y, work.Top - shadow, Math.Max(work.Top - shadow, work.Bottom - ht + shadow));
        if (r.Left == x && r.Top == y) return;                           // already there (our own move comes back as an event)
        Native.SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SwpMoveOnly);
    }
}

/// <summary>
/// What is playing (Spotify, a video in the browser …) for the top bar: the media session Windows shows in its own volume
/// flyout, its title and whether it plays; play / pause, previous, next.
/// </summary>
static class BarMedia
{
    static Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager _manager;
    static Windows.Media.Control.GlobalSystemMediaTransportControlsSession _session;
    static bool _watching;

    /// <summary>"Artist – title" (empty = nothing playing) and whether it plays.</summary>
    public static (string Title, bool Playing) Now { get; private set; }

    /// <summary>What plays, for the control center: its title and artist, its picture (cover or the frame of the video,
    /// the bytes of an image; null without one) and the app that plays it (its name and key for the icon).</summary>
    public sealed record Media(string Title, string Artist, byte[] Picture, string App, string AppKey, bool Playing);

    /// <summary>Null when nothing plays.</summary>
    public static Media Details { get; private set; }

    static readonly Dictionary<string, (string Name, string Key)> Apps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The app behind a media session, by the ID Windows gives it: a program ("Spotify.exe") or the app ID of a Store app or
    /// a browser ("MSEdge", "Chrome", …); its name as Windows shows it, otherwise the ID itself.
    /// </summary>
    static (string Name, string Key) AppOf(string id)
    {
        if (string.IsNullOrEmpty(id)) return (null, null);
        if (Apps.TryGetValue(id, out var known)) return known;
        string name = null, key = null;
        try
        {
            if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(id)))
                    using (p)
                    {
                        try { key ??= p.MainModule?.FileName; } catch { }
                    }
                name = key != null ? AppShell.AppName(key) : Path.GetFileNameWithoutExtension(id);
            }
            else
            {
                key = @"shell:AppsFolder\" + id;
                name = AppShell.DisplayName(key);
                if (name == null)
                {
                    key = null;
                    name = id switch { "MSEdge" => "Microsoft Edge", "Chrome" => "Google Chrome", _ => id.Split('!')[0].Split('_')[0] };
                }
            }
        }
        catch (Exception ex) { Log.Write("BarMedia app", ex); }
        return Apps[id] = (string.IsNullOrWhiteSpace(name) ? id : name, key);
    }

    static async Task<byte[]> PictureOf(Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        try
        {
            if (props.Thumbnail == null) return null;
            using var stream = await props.Thumbnail.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > 8 * 1024 * 1024) return null;
            var bytes = new byte[stream.Size];
            using var reader = new Windows.Storage.Streams.DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch { return null; }
    }

    /// <summary>Raised on the UI thread when <see cref="Now"/> changed.</summary>
    public static event Action Changed;

    static void OnUi(Action a) => Ui.Post(a);

    public static async void Watch(bool on)
    {
        if (on == _watching) return;
        _watching = on;
        try
        {
            if (!on)
            {
                if (_manager != null) _manager.CurrentSessionChanged -= SessionChanged;
                Use(null);
                Now = default;
                Details = null;
                Changed?.Invoke();
                return;
            }
            _manager ??= await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += SessionChanged;
            Use(_manager.GetCurrentSession());
        }
        catch (Exception ex) { Log.Write("BarMedia", ex); }
    }

    static void SessionChanged(Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager m, object e) =>
        OnUi(() => { if (_watching) Use(m.GetCurrentSession()); });

    static void Use(Windows.Media.Control.GlobalSystemMediaTransportControlsSession session)
    {
        if (_session != null) { _session.MediaPropertiesChanged -= MediaChanged; _session.PlaybackInfoChanged -= MediaChanged; }
        _session = session;
        if (_session != null) { _session.MediaPropertiesChanged += MediaChanged; _session.PlaybackInfoChanged += MediaChanged; }
        Refresh();
    }

    static void MediaChanged(Windows.Media.Control.GlobalSystemMediaTransportControlsSession s, object e) => OnUi(Refresh);

    static async void Refresh()
    {
        var session = _session;
        (string, bool) now = default;
        Media details = null;
        try
        {
            if (session != null && await session.TryGetMediaPropertiesAsync() is { } props && !string.IsNullOrWhiteSpace(props.Title))
            {
                string artist = !string.IsNullOrWhiteSpace(props.Artist) ? props.Artist : props.AlbumTitle;
                bool playing = session.GetPlaybackInfo()?.PlaybackStatus == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                now = (string.IsNullOrWhiteSpace(artist) ? props.Title : $"{artist} – {props.Title}", playing);
                // the same picture again (only play / pause changed): not read once more
                byte[] picture = Details is { } old && old.Title == props.Title && old.Artist == artist && old.Picture != null ? old.Picture : await PictureOf(props);
                var (app, key) = AppOf(session.SourceAppUserModelId);
                details = new Media(props.Title, artist, picture, app, key, playing);
            }
        }
        catch (Exception ex) { Log.Write("BarMedia refresh", ex); }
        if (session != _session) return;                               // another session meanwhile
        Now = now;
        Details = details;
        Changed?.Invoke();
    }

    public static async Task PlayPauseAsync() { try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { } }
    public static async Task NextAsync() { try { if (_session != null) await _session.TrySkipNextAsync(); } catch { } }
    public static async Task PreviousAsync() { try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { } }
}

/// <summary>
/// VPN: whether a connection is up (the sign among the status icons, the quick button of the control center) and the VPN
/// connections of Windows (Settings → Network → VPN), which are connected / disconnected with rasdial / rasphone.
/// VPN apps with their own adapter (WireGuard, OpenVPN …) are recognized as connected, they are controlled in the app.
/// </summary>
static class Vpn
{
    static List<string> _profiles;
    static long _profilesAt;

    static string _active;
    static long _activeAt = -10_000;

    /// <summary>
    /// The name of the VPN connection that is up, null when none – kept for 3 s: listing the network adapters is slow and the control center,
    /// the dock and the top bar ask several times in a row.
    /// </summary>
    public static string Active()
    {
        if (Environment.TickCount64 - _activeAt < 3000) return _active;
        _active = ReadActive();
        _activeAt = Environment.TickCount64;
        return _active;
    }

    static string ReadActive()
    {
        try
        {
            var profiles = Profiles();
            var up = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                && (n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ppp
                    || profiles.Contains(n.Name, StringComparer.OrdinalIgnoreCase)
                    || new[] { "VPN", "WireGuard", "OpenVPN", "Wintun", "TAP-Windows" }.Any(k => n.Description.Contains(k, StringComparison.OrdinalIgnoreCase))));
            return up?.Name;
        }
        catch { return null; }
    }

    /// <summary>The VPN connections set up in Windows (names from the phone books of the user and of all users).</summary>
    public static List<string> Profiles()
    {
        if (_profiles != null && Environment.TickCount64 - _profilesAt < 60_000) return _profiles;   // read from disk once a minute
        var names = new List<string>();
        foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.CommonApplicationData })
        {
            var book = Path.Combine(Environment.GetFolderPath(root), @"Microsoft\Network\Connections\Pbk\rasphone.pbk");
            try
            {
                if (!File.Exists(book)) continue;
                foreach (var line in File.ReadLines(book))
                    if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2) names.Add(line[1..^1]);
            }
            catch { }
        }
        _profilesAt = Environment.TickCount64;
        return _profiles = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The quick button: a connected Windows VPN is disconnected; otherwise the first Windows VPN is connected through its
    /// own dialog (it asks for the password when it is not saved) – without any, the VPN settings open.
    /// </summary>
    public static void Toggle()
    {
        var profiles = Profiles();
        _activeAt = -10_000;                                          // the state now, and again after the change
        var active = Active();
        _activeAt = -10_000;
        if (active != null && profiles.Contains(active, StringComparer.OrdinalIgnoreCase)) Fg.Run("rasdial.exe", $"\"{active}\" /disconnect");
        else if (active == null && profiles.Count > 0) Fg.Run("rasphone.exe", $"-d \"{profiles[0]}\"");
        else Fg.Open("ms-settings:network-vpn");
    }
}

/// <summary>The graphics chip that drives the main monitor (the app is drawn on it) – for the diagnostic data.</summary>
static class GraphicsChip
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE info, uint flags);

    /// <summary>Name of the chip, "" when unknown.</summary>
    public static string Name { get; } = Read();

    static string Read()
    {
        try
        {
            var d = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            for (uint i = 0; EnumDisplayDevices(null, i, ref d, 0); i++)
                if ((d.StateFlags & 4) != 0) return d.DeviceString ?? "";   // DISPLAY_DEVICE_PRIMARY_DEVICE
        }
        catch (Exception ex) { Log.Write("Graphics chip", ex); }
        return "";
    }

    /// <summary>Known by the name: Intel UHD / Iris / Arc without a model number, AMD Radeon without RX / R7 … – everything else counts as dedicated.</summary>
    public static bool Integrated { get; } =
        System.Text.RegularExpressions.Regex.IsMatch(Name, "Intel|AMD|Radeon", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        && !System.Text.RegularExpressions.Regex.IsMatch(Name, @"NVIDIA|GeForce|Arc(\(TM\))?\s+[AB]\d|Radeon(\(TM\))?\s+(RX\s*\d|R[579]\b|HD\b|Pro\b|VII|Fury)|FirePro",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

/// <summary>A volume of an external drive: its root ("E:\"), name for the menu and free space in bytes (-1 unknown).</summary>
sealed record RemovableVolume(string Root, string Label, long Free);

/// <summary>An external drive with its volumes (a disk may have several).</summary>
sealed record RemovableDevice(int Number, string Name, List<RemovableVolume> Volumes);

/// <summary>External drives for the icon among the status icons: flash drives, memory cards and disks on USB, FireWire, SD.</summary>
static class RemovableDrives
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, byte[] input, int inSize, byte[] output, int outSize, out int returned, IntPtr overlapped);

    const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400, IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080;

    /// <summary>Bus (USB 7, FireWire 4, SD 12, MMC 13), name and number of the device of a volume; false when it cannot be asked.</summary>
    static bool Ask(string letter, out int bus, out string name, out int number)
    {
        bus = 0; name = null; number = -1;
        using var h = CreateFile(@"\\.\" + letter, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);   // no access needed, share read / write, open existing
        if (h.IsInvalid) return false;
        var outp = new byte[1024];
        if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, new byte[12], 12, outp, outp.Length, out _, IntPtr.Zero)) return false;
        bus = BitConverter.ToInt32(outp, 28);
        string Text(int at)
        {
            int off = BitConverter.ToInt32(outp, at);
            if (off <= 0 || off >= outp.Length) return "";
            int end = Array.IndexOf(outp, (byte)0, off);
            return System.Text.Encoding.ASCII.GetString(outp, off, (end < 0 ? outp.Length : end) - off).Trim();
        }
        name = $"{Text(12)} {Text(16)}".Trim();
        var num = new byte[12];
        if (DeviceIoControl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, null, 0, num, num.Length, out _, IntPtr.Zero)) number = BitConverter.ToInt32(num, 4);
        return true;
    }

    public static List<RemovableDevice> Now()
    {
        var list = new List<RemovableDevice>();
        try
        {
            string system = Path.GetPathRoot(Environment.SystemDirectory);
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType is not (DriveType.Removable or DriveType.Fixed or DriveType.CDRom) || !d.IsReady
                    || d.Name.Equals(system, StringComparison.OrdinalIgnoreCase)) continue;
                string letter = d.Name.TrimEnd('\\');
                if (!Ask(letter, out int bus, out string name, out int number) && d.DriveType != DriveType.Removable) continue;
                if (d.DriveType != DriveType.Removable && bus is not (7 or 4 or 12 or 13)) continue;   // an inner disk
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? letter : $"{d.VolumeLabel} ({letter})";
                long free = -1;
                try { free = d.AvailableFreeSpace; } catch { }
                var device = number >= 0 ? list.FirstOrDefault(x => x.Number == number) : null;
                if (device == null) list.Add(device = new RemovableDevice(number >= 0 ? number : -1 - list.Count, string.IsNullOrWhiteSpace(name) ? label : name, new()));
                device.Volumes.Add(new RemovableVolume(d.Name, label, free));
            }
        }
        catch (Exception ex) { Log.Write("Removable drives", ex); }
        return list;
    }

    /// <summary>Safe removal through the shell, like "Eject" in Explorer ("This PC" = namespace 17).</summary>
    public static void Eject(RemovableDevice device)
    {
        foreach (var volume in device.Volumes)
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                shell.NameSpace(17).ParseName(volume.Root).InvokeVerb("Eject");
            }
            catch (Exception ex) { Log.Write("Eject " + volume.Root, ex); }
    }

    /// <summary>Bytes as "850 MB", "12,4 GB".</summary>
    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.CurrentCulture) + " GB"
        : (bytes / (double)(1L << 20)).ToString("0", CultureInfo.CurrentCulture) + " MB";
}

/// <summary>Processor and memory load and the network speed for the top bar (sampled once a second, shared by all bars).</summary>
static class SystemMonitor
{
    [StructLayout(LayoutKind.Sequential)] struct FILETIME { public uint Low, High; public ulong Value => (ulong)High << 32 | Low; }
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX { public uint Length, MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    static ulong _idle, _total;
    static long _received, _sent, _at;
    static (double Cpu, double Ram, double Gpu, double Down, double Up) _last;

    // graphics card: the 3D engines of all processes, from the performance counters of Windows (like Task Manager)
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);

    static IntPtr _gpuQuery, _gpuCounter, _vramCounter;
    static bool _gpuFailed;
    static double _vramUsed = double.NaN;
    static ulong? _vramTotal;

    /// <summary>Video memory of the graphics card in use (bytes, NaN unknown) and its size (0 unknown) – read with <see cref="Gpu"/>.</summary>
    public static (double Used, ulong Total) Vram => (_vramUsed, _vramTotal ??= VramTotal());

    /// <summary>The dedicated memory of the largest graphics card (from its driver entry in the registry).</summary>
    static ulong VramTotal()
    {
        ulong best = 0;
        try
        {
            using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var name in cls?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                if (!name.All(char.IsDigit)) continue;
                using var k = cls.OpenSubKey(name);
                object v = null;
                try { v = k?.GetValue("HardwareInformation.qwMemorySize"); } catch { }
                ulong size = v switch { long l => (ulong)l, byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b, 0), _ => 0 };
                best = Math.Max(best, size);
            }
        }
        catch { }
        return best;
    }

    /// <summary>Sum of a wildcard counter per instance (largest instance with <paramref name="max"/>), NaN when it cannot be read.</summary>
    static double ReadArray(IntPtr counter, bool max)
    {
        const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;
        uint size = 0;
        PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero);   // the size needed
        if (size == 0) return 0;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, buffer) != 0) return double.NaN;
            // PDH_FMT_COUNTERVALUE_ITEM: name pointer, then status (+ padding) and the double value
            int item = IntPtr.Size + 16;
            double result = 0;
            for (int i = 0; i < count; i++)
            {
                var at = buffer + i * item;
                if (Marshal.ReadInt32(at, IntPtr.Size) != 0) continue;
                double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(at, IntPtr.Size + 8));
                result = max ? Math.Max(result, v) : result + v;
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Use of the graphics card in % (the sum of its 3D engines), NaN when it cannot be read.</summary>
    static double Gpu()
    {
        if (_gpuFailed) return double.NaN;
        try
        {
            if (_gpuQuery == IntPtr.Zero)
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out _gpuQuery) != 0
                    || PdhAddEnglishCounter(_gpuQuery, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) != 0)
                { _gpuFailed = true; return double.NaN; }
                // video memory in use: the graphics card that uses the most (a laptop also has the one in the processor)
                if (PdhAddEnglishCounter(_gpuQuery, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _vramCounter) != 0) _vramCounter = IntPtr.Zero;
                PdhCollectQueryData(_gpuQuery);                          // a rate needs two samples – the first value comes a second later
                return 0;
            }
            if (PdhCollectQueryData(_gpuQuery) != 0) return double.NaN;
            if (_vramCounter != IntPtr.Zero) _vramUsed = ReadArray(_vramCounter, max: true);
            double sum = ReadArray(_gpuCounter, max: false);
            return double.IsNaN(sum) ? sum : Math.Clamp(sum, 0, 100);
        }
        catch { _gpuFailed = true; return double.NaN; }
    }

    /// <summary>Processor %, memory %, graphics card % (only when <paramref name="gpu"/>; NaN when unknown), bytes per second down / up.</summary>
    public static (double Cpu, double Ram, double Gpu, double Down, double Up) Sample(bool gpu)
    {
        long now = Environment.TickCount64;
        if (_at != 0 && now - _at < 500) return _last;                // several bars: one sample a second
        double cpu = _last.Cpu, ram = _last.Ram, down = 0, up = 0;
        double gpuUse = gpu ? Gpu() : double.NaN;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            ulong total = kernel.Value + user.Value;                   // kernel time includes the idle time
            if (_total != 0 && total > _total) cpu = 100.0 * (1 - (double)(idle.Value - _idle) / (total - _total));
            _idle = idle.Value;
            _total = total;
        }
        var mem = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem)) ram = mem.MemoryLoad;
        try
        {
            long rx = 0, tx = 0;
            foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                    || n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                var s = n.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
            if (_at != 0)
            {
                double seconds = Math.Max(0.5, (now - _at) / 1000.0);
                down = Math.Max(0, rx - _received) / seconds;
                up = Math.Max(0, tx - _sent) / seconds;
            }
            _received = rx;
            _sent = tx;
        }
        catch { }
        _at = now;
        return _last = (Math.Clamp(cpu, 0, 100), ram, gpuUse, down, up);
    }

    /// <summary>Video memory in use: "VRAM 35 %" with the size known, else "VRAM 2,1 GB"; "VRAM –" when unknown.</summary>
    public static string VramText()
    {
        var (used, total) = Vram;
        if (double.IsNaN(used)) return "VRAM –";
        return total > 0 ? $"VRAM {Math.Min(100, used * 100 / total):0} %"
            : "VRAM " + (used / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " GB";
    }

    /// <summary>Bytes per second as "350 kB/s", "1,2 MB/s".</summary>
    public static string Speed(double bytes) =>
        bytes >= 1024 * 1024 ? (bytes / (1024 * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " MB/s"
        : (bytes / 1024).ToString("0", CultureInfo.CurrentCulture) + " kB/s";
}

/// <summary>
/// Language of the keyboard (the hidden Windows taskbar had it): the layout of the active window, the installed layouts and
/// switching between them.
/// </summary>
static class InputLanguage
{
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[] list);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    const uint WM_INPUTLANGCHANGEREQUEST = 0x50;

    /// <summary>The installed keyboard layouts (Settings → Time and language).</summary>
    public static IntPtr[] Layouts()
    {
        int n = GetKeyboardLayoutList(0, null);
        var list = new IntPtr[Math.Max(0, n)];
        if (n > 0) GetKeyboardLayoutList(n, list);
        return list;
    }

    /// <summary>The layout of the window the user types into.</summary>
    public static IntPtr Current => GetKeyboardLayout(Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _));

    static CultureInfo Culture(IntPtr hkl)
    {
        try { return CultureInfo.GetCultureInfo((int)((long)hkl & 0xFFFF)); }
        catch { return CultureInfo.InvariantCulture; }
    }

    /// <summary>"CES", "ENG" … (like Windows shows it).</summary>
    public static string Short(IntPtr hkl) => Culture(hkl).ThreeLetterISOLanguageName.ToUpperInvariant();

    /// <summary>"čeština (Česko)" – the name of the language in itself.</summary>
    public static string Name(IntPtr hkl)
    {
        var c = Culture(hkl);
        return string.IsNullOrEmpty(c.NativeName) ? Short(hkl) : c.NativeName;
    }

    /// <summary>Asks a window (the one that was active before the menu opened) to type with another layout, as the Windows switcher does.</summary>
    public static void Switch(IntPtr window, IntPtr hkl)
    {
        if (window != IntPtr.Zero) PostMessage(window, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
    }
}

/// <summary>
/// Number of new notifications for the badge at the clock: those that came since the calendar was last opened (and were not
/// dismissed there). Read only when Windows already allowed the access – the dock itself never asks for it.
/// </summary>
static class NotificationBadge
{
    static readonly string SeenFile = AppPaths.Of("notifications-seen.txt");
    static DateTimeOffset? _seen;
    static DispatcherTimer _timer;
    static bool _busy;

    public static int Count { get; private set; }
    public static event Action Changed;

    static DateTimeOffset Seen
    {
        get
        {
            if (_seen != null) return _seen.Value;
            try { _seen = new DateTimeOffset(long.Parse(File.ReadAllText(SeenFile)), TimeSpan.Zero); }
            catch { MarkSeen(); }                                   // first run: the old ones are not new
            return _seen.Value;
        }
    }

    /// <summary>Checks every few seconds while some dock shows the badge.</summary>
    public static void Watch(bool on)
    {
        if (!on) { _timer?.Stop(); return; }
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _timer.Tick += (_, _) => Refresh();
        }
        if (!_timer.IsEnabled) { _timer.Start(); Refresh(); }
    }

    /// <summary>The notifications were looked at (calendar or the Windows notification center opened).</summary>
    public static void MarkSeen()
    {
        _seen = DateTimeOffset.UtcNow;
        try
        {
            Directory.CreateDirectory(AppPaths.Folder);
            File.WriteAllText(SeenFile, _seen.Value.UtcTicks.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) { Log.Write("NotificationBadge", ex); }
        Set(0);
    }

    public static async void Refresh()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var listener = Windows.UI.Notifications.Management.UserNotificationListener.Current;
            if (listener.GetAccessStatus() != Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed) { Set(0); return; }
            var list = await listener.GetNotificationsAsync(Windows.UI.Notifications.NotificationKinds.Toast);
            var seen = Seen;
            Set(list.Count(n => n.CreationTime > seen && Host.IsDismissed?.Invoke(n.Id) != true));
        }
        catch (Exception ex) { Log.Write("NotificationBadge.Refresh", ex); _timer?.Stop(); }   // not available: no badge
        finally { _busy = false; }
    }

    static void Set(int n)
    {
        if (n == Count) return;
        Count = n;
        Changed?.Invoke();
    }
}

/// <summary>
/// Virtual desktops of Windows. Windows has no public interface for them, so the list is read from the registry (where
/// Explorer keeps it) and switching uses the Windows shortcuts Ctrl+Win+← / → / D / F4.
/// </summary>
static class VirtualDesktops
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    [DllImport("advapi32.dll")]
    static extern int RegNotifyChangeKeyValue(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, bool subtree, uint filter, IntPtr ev, bool async);

    public static event Action Changed;
    static Thread _watcher;

    /// <summary>Ids of the desktops in their order (one desktop when Windows has not stored any).</summary>
    public static List<Guid> All()
    {
        var list = new List<Guid>();
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (k?.GetValue("VirtualDesktopIDs") is byte[] ids)
                for (int i = 0; i + 16 <= ids.Length; i += 16) list.Add(new Guid(ids.AsSpan(i, 16)));
        }
        catch (Exception ex) { Log.Write("VirtualDesktops", ex); }
        if (list.Count == 0) list.Add(Guid.Empty);
        return list;
    }

    /// <summary>Index of the current desktop in <see cref="All"/>.</summary>
    public static int CurrentIndex(List<Guid> all)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (k?.GetValue("CurrentVirtualDesktop") is byte[] { Length: 16 } cur) return Math.Max(0, all.IndexOf(new Guid(cur)));
        }
        catch { }
        return 0;
    }

    /// <summary>The name given in Task view, or "Desktop 2".</summary>
    public static string Name(List<Guid> all, int index)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"{KeyPath}\Desktops\{all[index]:B}");
            if (k?.GetValue("Name") is string n && n.Length > 0) return n;
        }
        catch { }
        return L.T("Desktop {0}", index + 1);
    }

    /// <summary>Watches the registry (no polling) and raises <see cref="Changed"/> on the UI thread.</summary>
    public static void Watch()
    {
        if (_watcher != null) return;
        _watcher = new Thread(() =>
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(KeyPath);
                while (RegNotifyChangeKeyValue(k.Handle, true, 0x1 | 0x4, IntPtr.Zero, false) == 0)   // names, values
                    Ui.Post(() => Changed?.Invoke());
            }
            catch (Exception ex) { Log.Write("VirtualDesktops.Watch", ex); }
        }) { IsBackground = true, Name = "VirtualDesktops" };
        _watcher.Start();
    }

    const byte VK_LCONTROL = 0xA2, VK_LWIN = 0x5B, VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_D = 0x44, VK_F4 = 0x73;

    /// <summary>Ctrl+Win with keys (the keyboard hook ignores injected keys, so nothing of HeMiDock reacts to them).</summary>
    static void CtrlWin(byte key, int times = 1, bool extended = false)
    {
        uint ext = extended ? 1u : 0u;
        Native.keybd_event(VK_LCONTROL, 0, 0, UIntPtr.Zero);
        Native.keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        for (int i = 0; i < times; i++)
        {
            Native.keybd_event(key, 0, ext, UIntPtr.Zero);
            Native.keybd_event(key, 0, ext | 2, UIntPtr.Zero);
        }
        Native.keybd_event(VK_LWIN, 0, 2, UIntPtr.Zero);
        Native.keybd_event(VK_LCONTROL, 0, 2, UIntPtr.Zero);
    }

    public static void SwitchTo(int index)
    {
        var all = All();
        int delta = Math.Clamp(index, 0, all.Count - 1) - CurrentIndex(all);
        if (delta != 0) CtrlWin(delta > 0 ? VK_RIGHT : VK_LEFT, Math.Abs(delta), extended: true);
    }

    /// <summary>The next (+1) / previous (−1) desktop.</summary>
    public static void Move(int delta) => CtrlWin(delta > 0 ? VK_RIGHT : VK_LEFT, 1, extended: true);

    public static void New() => CtrlWin(VK_D);

    public static void CloseCurrent() => CtrlWin(VK_F4);

    /// <summary>Closes any desktop (not the last one): Windows closes only the current one, so it goes there and back. Its windows move to the desktop next to it.</summary>
    public static async void Close(int index)
    {
        var all = All();
        if (all.Count < 2 || index < 0 || index >= all.Count) return;
        int current = CurrentIndex(all);
        if (index != current)
        {
            SwitchTo(index);
            await Task.Delay(350);
        }
        CloseCurrent();
        if (index == current) return;
        await Task.Delay(350);
        SwitchTo(current > index ? current - 1 : current);
    }
}
