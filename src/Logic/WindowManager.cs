using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HeMiDock;

/// <summary>
/// Settings → Window manager: zones (a window dragged with the chosen key held falls into a part of the screen), always on top
/// (shortcut) and workspaces (the open apps and their places saved under a name, opened again from the settings or the dock).
/// </summary>
static class WindowManager
{
    const uint EVENT_MOVESIZESTART = 0xA, EVENT_MOVESIZEEND = 0xB;
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Native.RECT r, int size);

    static readonly WinEventProc Proc = OnMoveSize;
    static IntPtr _hook;
    static bool _zonesShown;
    static IntPtr _moving;
    static DispatcherTimer _track;
    static int ZoneKey => DockSettings.Current.ZoneKey switch { 1 => 0x11, 2 => 0x12, _ => 0x10 };   // Ctrl, Alt, Shift

    public static void Apply()
    {
        RefreshMarks();
        bool on = DockSettings.Current.ZonesOn;
        if (on == (_hook != IntPtr.Zero)) return;
        if (on) _hook = SetWinEventHook(EVENT_MOVESIZESTART, EVENT_MOVESIZEEND, IntPtr.Zero, Proc, 0, 0, 2);   // out of context, not our windows' events only
        else { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        Diag.Write("Zones", on ? (_hook != IntPtr.Zero ? "on" : "on, but the hook failed") : "off");
    }

    /// <summary>Parts of the screen (share of its work area).</summary>
    static Rect[] Zones => DockSettings.Current.OwnZoneLayout is >= 0 and var n && n < DockSettings.Current.OwnZoneLayouts.Count
        && ParseZones(DockSettings.Current.OwnZoneLayouts[n].Zones) is { Count: > 0 } own ? own.ToArray() : DockSettings.Current.ZoneLayout switch
    {
        1 => new[] { new Rect(0, 0, 1 / 3.0, 1), new Rect(1 / 3.0, 0, 1 / 3.0, 1), new Rect(2 / 3.0, 0, 1 / 3.0, 1) },
        2 => new[] { new Rect(0, 0, .5, .5), new Rect(.5, 0, .5, .5), new Rect(0, .5, .5, .5), new Rect(.5, .5, .5, .5) },
        3 => new[] { new Rect(0, 0, 2 / 3.0, 1), new Rect(2 / 3.0, 0, 1 / 3.0, .5), new Rect(2 / 3.0, .5, 1 / 3.0, .5) },
        5 => new[] { new Rect(0, 0, 1 / 3.0, .5), new Rect(0, .5, 1 / 3.0, .5), new Rect(1 / 3.0, 0, 2 / 3.0, 1) },   // large on the right, two on the left
        _ => new[] { new Rect(0, 0, .5, 1), new Rect(.5, 0, .5, 1) }
    };

    static MonInfo MonitorAt(Native.POINT p) =>
        Monitors.All().FirstOrDefault(m => p.X >= m.Bounds.Left && p.X < m.Bounds.Right && p.Y >= m.Bounds.Top && p.Y < m.Bounds.Bottom) ?? Monitors.All()[0];

    static int ZoneAt(Native.POINT p, MonInfo m)
    {
        double x = (p.X - m.Work.Left) / (double)(m.Work.Right - m.Work.Left), y = (p.Y - m.Work.Top) / (double)(m.Work.Bottom - m.Work.Top);
        return Array.FindIndex(Zones, z => z.Contains(new Point(Math.Clamp(x, 0, .999), Math.Clamp(y, 0, .999))));
    }

    static void OnMoveSize(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
    {
        if (obj != 0 || child != 0) return;
        if (ev == EVENT_MOVESIZESTART)
        {
            Diag.Write("Zones", "a window is moved");
            _moving = hwnd;
            _track ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _track.Tick -= Track;
            _track.Tick += Track;
            _track.Start();
            return;
        }
        _track?.Stop();
        // the zone lit while the window was let go: Windows tells the end a moment later, when the key may already be up
        // (released together with the mouse, the window did not go into the zone); the key let go earlier hid the zones
        bool place = _zonesShown && hwnd == _moving && _hot >= 0 && _hotMonitor != null;
        var (m, i) = (_hotMonitor, _hot);
        HideOverlay();
        if (place && i < Zones.Length) PlaceInZone(hwnd, m, Zones[i]);
        _moving = IntPtr.Zero;
    }

    static MonInfo _hotMonitor;
    static int _hot = -1;

    static void Track(object s, EventArgs e)
    {
        if ((Native.GetAsyncKeyState(ZoneKey) & 0x8000) == 0 || !Native.GetCursorPos(out var p)) { HideOverlay(); return; }
        var m = MonitorAt(p);
        _zonesShown = true;
        _hotMonitor = m;
        _hot = ZoneAt(p, m);
        Host.ShowZones?.Invoke(m, Zones, _hot);
    }

    static void HideOverlay()
    {
        if (!_zonesShown) return;
        _zonesShown = false;
        _hot = -1;
        Host.HideZones?.Invoke();
    }

    static void PlaceInZone(IntPtr hwnd, MonInfo m, Rect z)
    {
        int w = m.Work.Right - m.Work.Left, h = m.Work.Bottom - m.Work.Top;
        // the window is larger than it looks by its invisible resize border
        int left = 0, top = 0, right = 0, bottom = 0;
        if (Native.GetWindowRect(hwnd, out var whole) && DwmGetWindowAttribute(hwnd, 9, out var seen, Marshal.SizeOf<Native.RECT>()) == 0)
        {
            left = seen.Left - whole.Left; top = seen.Top - whole.Top; right = whole.Right - seen.Right; bottom = whole.Bottom - seen.Bottom;
        }
        Native.SetWindowPos(hwnd, IntPtr.Zero, m.Work.Left + (int)(z.X * w) - left, m.Work.Top + (int)(z.Y * h) - top,
            (int)(z.Width * w) + left + right, (int)(z.Height * h) + top + bottom, 0x4 | 0x10);
    }

    /// <summary>Own zones: x,y,width,height in percent, separated by ;.</summary>
    public static List<Rect> ParseZones(string stored)
    {
        var list = new List<Rect>();
        foreach (var part in stored.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var v = part.Split(',').Select(x => double.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d / 100 : -1).ToArray();
            if (v.Length == 4 && v.All(x => x >= 0) && v[2] > 0 && v[3] > 0) list.Add(new Rect(v[0], v[1], v[2], v[3]));
        }
        return list;
    }

    static DispatcherTimer _hideShown;

    /// <summary>
    /// The shortcut "Switch the zone layout": the next layout in the order of the settings (the own ones after the ready-made
    /// ones), its name in a small window and its zones over the monitor of the mouse for a moment.
    /// </summary>
    public static void NextLayout()
    {
        var s = DockSettings.Current;
        var names = s.ZoneLayoutChoices();
        int choice = (s.ZoneLayoutChoice + 1) % names.Length;
        s.ZoneLayoutChoice = choice;
        Host.ShowInfo?.Invoke(L.T("Zones: {0}", L.T(names[choice])));
        if (_zonesShown || !Native.GetCursorPos(out var p)) return;      // not over a window being dragged
        Host.ShowZones?.Invoke(MonitorAt(p), Zones, -1);
        _hideShown ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
        _hideShown.Tick -= HideShown;
        _hideShown.Tick += HideShown;
        _hideShown.Stop();
        _hideShown.Start();
    }

    static void HideShown(object s, EventArgs e)
    {
        _hideShown.Stop();
        if (!_zonesShown) Host.HideZones?.Invoke();
    }

    /// <summary>The number of zones in the current layout.</summary>
    public static int ZoneCount => Zones.Length;

    /// <summary>A window into a zone of its monitor (for the rules of apps).</summary>
    public static void PlaceInZone(IntPtr hwnd, int zone)
    {
        if (zone < 0 || zone >= Zones.Length) return;
        var handle = Native.MonitorFromWindow(hwnd, 2);
        var all = Monitors.All();
        Native.ShowWindow(hwnd, 9);                                   // SW_RESTORE: a maximized window would not move
        PlaceInZone(hwnd, all.FirstOrDefault(m => m.Handle == handle) ?? all[0], Zones[zone]);
    }

    /// <summary>File names of the apps with an open window.</summary>
    public static List<string> OpenApps() => OpenWindows().Select(w => System.IO.Path.GetFileName(w.Path)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    // ───────────────────────── always on top ─────────────────────────

    const int GWL_EXSTYLE = -20, WS_EX_TOPMOST = 8;

    static readonly HashSet<IntPtr> Marked = new();

    /// <summary>The active window stays above the others with a line in the theme colour, or stops doing so.</summary>
    public static void ToggleTopmost()
    {
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero) return;
        bool on = (Native.GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
        Native.SetWindowPos(h, on ? new IntPtr(-2) : new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);   // NOSIZE | NOMOVE | NOACTIVATE
        if (on)
        {
            Marked.Remove(h);
            Native.SetDwmAttr(h, Native.DWMWA_BORDER_COLOR, DockSettings.Current.DockOutline ? unchecked((int)0xFFFFFFFF) : unchecked((int)0xFFFFFFFE));   // as all the others
            return;
        }
        Marked.Add(h);
        Mark(h);
    }

    /// <summary>A window made always on top here keeps its line (see <see cref="WindowOutlines"/>).</summary>
    public static bool IsMarked(IntPtr h) => Marked.Contains(h);

    static void Mark(IntPtr h)
    {
        Native.SetDwmAttr(h, Native.DWMWA_BORDER_COLOR, Theme.AccentColorRef);
    }

    /// <summary>The theme colour changed, or the app ends (the windows stay on top, only without the line).</summary>
    public static void RefreshMarks(bool end = false)
    {
        Marked.RemoveWhere(h => (Native.GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0);
        foreach (var h in Marked)
            if (end) Native.SetDwmAttr(h, Native.DWMWA_BORDER_COLOR, unchecked((int)0xFFFFFFFF));
            else Mark(h);
    }

    // ───────────────────────── shortcuts of the workspaces ─────────────────────────

    public static async void AskSave()
    {
        if (Host.AskText != null && await Host.AskText(L.T("Workspace"), L.T("Name of the workspace"), "") is { } name && name.Trim().Length > 0) Save(name.Trim());
    }

    /// <summary>A menu of the saved workspaces at the mouse.</summary>
    public static void ShowOpenMenu()
    {
        var items = Workspaces().Keys.Select(name => (name, (Action)(() => Open(name)))).ToList();
        if (items.Count == 0) items.Add((L.T("No saved workspaces"), null));
        Host.ShowMenu?.Invoke(items);
    }

    // ───────────────────────── workspaces ─────────────────────────

    public sealed class Place { public string Path { get; set; } public int X { get; set; } public int Y { get; set; } public int W { get; set; } public int H { get; set; } public bool Max { get; set; } }

    static string FilePath => AppPaths.Of("workspaces.json");

    public static Dictionary<string, List<Place>> Workspaces()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Dictionary<string, List<Place>>>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) { Log.Write("Workspaces", ex); }
        return new();
    }

    static void Write(Dictionary<string, List<Place>> all)
    {
        Directory.CreateDirectory(AppPaths.Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(all));
    }

    static IEnumerable<(IntPtr Handle, string Path)> OpenWindows()
    {
        foreach (var w in TaskWindows.List())
        {
            var h = w.Handle;
            if (!Native.IsWindowVisible(h) || Native.IsCloaked(h)) continue;
            string path = null;
            try
            {
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pid == Environment.ProcessId) continue;           // not the windows of this app
                using var p = Process.GetProcessById((int)pid);
                path = p.MainModule?.FileName;
            }
            catch { }
            if (path != null && !path.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) yield return (h, path);   // Store apps cannot be told apart
        }
    }

    /// <summary>The open apps and their places under a name.</summary>
    public static void Save(string name)
    {
        var places = new List<Place>();
        foreach (var (h, path) in OpenWindows())
            if (Native.GetWindowRect(h, out var r))
                places.Add(new Place { Path = path, X = r.Left, Y = r.Top, W = r.Right - r.Left, H = r.Bottom - r.Top, Max = IsZoomed(h) });
        var all = Workspaces();
        all[name] = places;
        Write(all);
    }

    public static void Delete(string name)
    {
        var all = Workspaces();
        all.Remove(name);
        Write(all);
    }

    /// <summary>Starts what is not running and moves the windows to their places (apps get some seconds to open).</summary>
    public static async void Open(string name)
    {
        if (!Workspaces().TryGetValue(name, out var places)) return;
        var open = OpenWindows().ToList();
        var pending = new List<Place>();
        var used = new HashSet<IntPtr>();
        foreach (var p in places)
        {
            var w = open.FirstOrDefault(o => !used.Contains(o.Handle) && o.Path.Equals(p.Path, StringComparison.OrdinalIgnoreCase));
            if (w.Handle != IntPtr.Zero) { used.Add(w.Handle); Move(w.Handle, p); continue; }
            try { Process.Start(new ProcessStartInfo(p.Path) { UseShellExecute = true }); pending.Add(p); }
            catch (Exception ex) { Log.Write("Workspace " + p.Path, ex); }
        }
        for (int i = 0; i < 40 && pending.Count > 0; i++)
        {
            await Task.Delay(500);
            foreach (var w in OpenWindows().ToList())
            {
                if (used.Contains(w.Handle)) continue;
                var p = pending.FirstOrDefault(x => x.Path.Equals(w.Path, StringComparison.OrdinalIgnoreCase));
                if (p == null) continue;
                used.Add(w.Handle);
                pending.Remove(p);
                Move(w.Handle, p);
            }
        }
    }

    static void Move(IntPtr h, Place p)
    {
        Native.ShowWindow(h, 9);                                      // SW_RESTORE
        Native.SetWindowPos(h, IntPtr.Zero, p.X, p.Y, p.W, p.H, 0x4 | 0x10);
        if (p.Max) Native.ShowWindow(h, 3);                           // SW_MAXIMIZE
    }

    /// <summary>A shortcut of the workspace for the dock: a click opens it through the running app.</summary>
    public static void PinToDock(string name)
    {
        string folder = AppPaths.Of("Workspaces");
        Directory.CreateDirectory(folder);
        string link = System.IO.Path.Combine(folder, string.Concat(name.Split(System.IO.Path.GetInvalidFileNameChars())) + ".lnk");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic s = shell.CreateShortcut(link);
        s.TargetPath = Environment.ProcessPath;
        s.Arguments = $"--workspace \"{name}\"";
        s.Save();
        Host.PinToDock?.Invoke(link);
    }

    public const string SignalName = "HeMiDock_WorkspaceSignal";
    static string RequestFile => AppPaths.Of("workspace-request.txt");

    public static void Request(string name)
    {
        Directory.CreateDirectory(AppPaths.Folder);
        File.WriteAllText(RequestFile, name);
    }

    public static void TakeRequest()
    {
        try
        {
            if (!File.Exists(RequestFile)) return;
            string name = File.ReadAllText(RequestFile);
            File.Delete(RequestFile);
            Open(name);
        }
        catch (Exception ex) { Log.Write("Workspace request", ex); }
    }

}

/// <summary>Control center: the PC does not fall asleep, and optionally shuts down after a time.</summary>
static class KeepAwake
{
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 1, ES_DISPLAY_REQUIRED = 2;

    public static bool IsOn { get; private set; }
    public static DateTime? ShutdownAt { get; private set; }

    public static void Set(bool on)
    {
        IsOn = on;
        SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS);
    }

    public static void ShutdownIn(TimeSpan time)
    {
        Fg.Run("shutdown.exe", $"/s /t {(int)time.TotalSeconds}");
        ShutdownAt = DateTime.Now + time;
        DockSettings.Current.PlannedShutdown = $"{DateTime.Now.Ticks}|{ShutdownAt.Value.Ticks}";
    }

    /// <summary>A shutdown planned before the dock restarted; one from before the last start of Windows or already past is forgotten.</summary>
    public static void Restore()
    {
        var parts = DockSettings.Current.PlannedShutdown.Split('|');
        if (parts.Length == 2 && long.TryParse(parts[0], out long set) && long.TryParse(parts[1], out long at)
            && new DateTime(set) > DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64) && new DateTime(at) > DateTime.Now)
            ShutdownAt = new DateTime(at);
        else DockSettings.Current.PlannedShutdown = "";
    }

    public static void CancelShutdown()
    {
        Fg.Run("shutdown.exe", "/a");
        ShutdownAt = null;
        DockSettings.Current.PlannedShutdown = "";
    }
}

/// <summary>Connecting to a Wi-Fi network with netsh: a temporary profile of the network, then a connect.</summary>
static class WifiFallback
{
    public static Task<bool> ConnectAsync(Windows.Devices.WiFi.WiFiAvailableNetwork n, string key, bool automatic) => Task.Run(async () =>
    {
        string file = null;
        try
        {
            string ssid = System.Security.SecurityElement.Escape(n.Ssid);
            bool open = n.SecuritySettings.NetworkAuthenticationType is Windows.Networking.Connectivity.NetworkAuthenticationType.Open80211 or Windows.Networking.Connectivity.NetworkAuthenticationType.None;
            if (!open && !string.IsNullOrEmpty(key))
            {
                string auth = n.SecuritySettings.NetworkAuthenticationType.ToString().Contains("Wpa3", StringComparison.OrdinalIgnoreCase) ? "WPA3SAE" : "WPA2PSK";
                string xml = $"<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><name>{ssid}</name><SSIDConfig><SSID><name>{ssid}</name></SSID></SSIDConfig><connectionType>ESS</connectionType><connectionMode>{(automatic ? "auto" : "manual")}</connectionMode><MSM><security><authEncryption><authentication>{auth}</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption><sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{System.Security.SecurityElement.Escape(key)}</keyMaterial></sharedKey></security></MSM></WLANProfile>";
                file = System.IO.Path.GetTempFileName();
                File.WriteAllText(file, xml);
                Netsh($"wlan add profile filename=\"{file}\" user=current");
            }
            if (Netsh($"wlan connect name=\"{n.Ssid}\" ssid=\"{n.Ssid}\"") != 0) return false;
            await Task.Delay(4000);
            return true;
        }
        catch (Exception ex) { Log.Write("Wi-Fi netsh", ex); return false; }
        finally { if (file != null) try { File.Delete(file); } catch { } }   // the key is in it
    });

    static int Netsh(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("netsh.exe", args) { UseShellExecute = false, CreateNoWindow = true });
        p.WaitForExit(8000);
        return p.ExitCode;
    }
}

/// <summary>
/// New mail: the envelope on the icon of Outlook. Outlook shows it on the taskbar of Windows, which is hidden – the count of
/// Windows, and the inbox of the classic Outlook while it runs (asked every minute, it is never started for that).
/// </summary>
static class UnreadMail
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHGetUnreadMailCountW(IntPtr key, string account, out int count, IntPtr time, string command, int size);

    static DispatcherTimer _timer;
    static int _outlook, _windows;
    static bool _checking;
    public static event Action Changed;
    /// <summary>The number of unread messages went down (some were read): their notifications may go too.</summary>
    public static event Action Read;

    /// <summary>
    /// Asked every few seconds (it was once a minute, so the envelope came and went late): the count of Windows always, the
    /// inbox of Outlook only while Outlook runs.
    /// </summary>
    public static void Start()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();
        Check();
    }

    static async void Check()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            // in the background: the count of Windows and whether the classic Outlook runs (it is never started for this)
            var (windows, running, front) = await Task.Run(() => (WindowsCount(), OutlookCalendar.Available && Process.GetProcessesByName("OUTLOOK").Length > 0, OutlookInFront()));
            int outlook = running ? await OutlookCalendar.UnreadAsync() : 0;
            // the count that is meant: with the classic Outlook running its inbox; the count of Windows has the counts of all
            // mail apps and accounts ever set up (an old one kept the envelope for ever)
            int now = running ? outlook : windows;
            bool fewer = windows < _windows || outlook < _outlook;
            // new mail is what came after Outlook was last in front: unread mail that was there before (seen, not read) did
            // not take the envelope away – like the overlay of Outlook on the taskbar of Windows
            // at the start, and when Outlook starts or ends (the count changes its source): what is there counts as seen
            int seen = front || _first || running != _running ? now : Math.Min(_seen, now);
            _first = false;
            _running = running;
            if (windows == _windows && outlook == _outlook && seen == _seen) return;
            _windows = windows;
            _outlook = outlook;
            _count = now;
            _seen = seen;
            Changed?.Invoke();
            if (fewer) Read?.Invoke();
        }
        catch (Exception ex) { Log.Write("Unread mail", ex); }
        finally { _checking = false; }
    }

    static int WindowsCount()
    {
        int count = 0;
        try { if (SHGetUnreadMailCountW(IntPtr.Zero, null, out count, IntPtr.Zero, null, 0) != 0) count = 0; } catch { }
        return count;
    }

    /// <summary>There is new mail (the app draws the envelope on the icon of Outlook): more unread than when it was last seen.</summary>
    public static bool HasMail => _count > _seen;

    static int _count, _seen;
    static bool _first = true, _running;

    /// <summary>The window in front is the one of Outlook (the mail is being looked at).</summary>
    static bool OutlookInFront()
    {
        try
        {
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid);
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.Equals("OUTLOOK", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Equals("olk", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

/// <summary>
/// Settings → Clock and calendar: a notification of Outlook goes from the notification center of Windows once its message
/// was read (matched by the subject among the messages of the last two days that are read now).
/// </summary>
static class ReadMailNotifications
{
    static bool _started, _busy;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        UnreadMail.Read += () => _ = RemoveAsync();
    }

    static async Task RemoveAsync()
    {
        if (!DockSettings.Current.RemoveReadMail || _busy) return;
        _busy = true;
        try
        {
            var listener = Windows.UI.Notifications.Management.UserNotificationListener.Current;
            if (listener.GetAccessStatus() != Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed) return;
            var outlook = (await listener.GetNotificationsAsync(Windows.UI.Notifications.NotificationKinds.Toast))
                .Where(n => { try { return n.AppInfo?.DisplayInfo?.DisplayName?.Contains("Outlook", StringComparison.OrdinalIgnoreCase) == true; } catch { return false; } })
                .ToList();
            if (outlook.Count == 0) return;
            var read = await OutlookCalendar.ReadSubjectsAsync();
            if (read.Count == 0) return;
            foreach (var n in outlook)
            {
                var texts = n.Notification?.Visual?.GetBinding(Windows.UI.Notifications.KnownNotificationBindings.ToastGeneric)?.GetTextElements().Select(t => t.Text?.Trim()) ?? Enumerable.Empty<string>();
                if (texts.Any(t => t != null && read.Contains(t)))
                    try { listener.RemoveNotification(n.Id); } catch (Exception ex) { Log.Write("Remove notification", ex); }
            }
        }
        catch (Exception ex) { Log.Write("Read mail notifications", ex); }
        finally { _busy = false; }
    }
}

