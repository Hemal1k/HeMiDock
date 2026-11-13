using System.Collections.Specialized;
using System.IO;
using System.ComponentModel;
using System.Text.Json;
using ManagedShell;
using ManagedShell.Common.Enums;
using ManagedShell.Common.Helpers;
using ManagedShell.WindowsTasks;
using ManagedShell.WindowsTray;
using Microsoft.Win32;

namespace HeMiDock;

/// <summary>An icon of the notification area as the app sees it (its picture as pixels).</summary>
sealed class TrayItem
{
    internal NotifyIcon Icon { get; init; }
    public string Id { get; init; }
    public string Title => Icon.Title;
    public IconImage Picture => ShellHost.ToImage(Icon.Icon);
    public bool IsPinned => Icon.IsPinned;
    /// <summary>Its place among the pinned icons.</summary>
    public int PinOrder => Icon.PinOrder;
    /// <summary>The program of the icon.</summary>
    public string Path => Icon.Path;
    /// <summary>The window the icon belongs to (its app).</summary>
    public IntPtr Window => (IntPtr)Icon.HWnd;
    /// <summary>Windows Security ignores clicks of any other host than Explorer: the dock handles it itself.</summary>
    public bool IsWindowsSecurity => Path?.EndsWith("SecurityHealthSystray.exe", StringComparison.OrdinalIgnoreCase) == true;
    /// <summary>Its menu asks Windows for the place of the icon and gets the hidden taskbar: an own menu instead.</summary>
    public bool IsWindowsUpdate => Path?.IndexOf("MoUso", StringComparison.OrdinalIgnoreCase) >= 0 || Path?.IndexOf("WindowsUpdate", StringComparison.OrdinalIgnoreCase) >= 0
        || Title?.IndexOf("Windows Update", StringComparison.OrdinalIgnoreCase) >= 0;
}

/// <summary>
/// ManagedShell for the app: started once, it hides the taskbar of Windows, hosts the notification area and lists the
/// windows of apps. Changes come as events on the thread of the user interface.
/// </summary>
static class ShellHost
{
    static ShellManager _shell;
    static readonly Dictionary<NotifyIcon, TrayItem> Items = new();

    /// <summary>Icons or windows changed (added, removed, a new picture, title, progress …).</summary>
    public static event Action TrayChanged, WindowsChanged;

    public static bool Running => _shell != null;

    public static void Start(bool hideTaskbar)
    {
        if (_shell != null) return;
        _shell = new ShellManager(TrayPins.Load(), IconSize.Large);
        _shell.AppBarManager.AutoHideActive = () => DockSettings.Current.AutoHide;   // apps that ask whether the taskbar hides
        _shell.Tasks.Initialize(false);
        if (hideTaskbar) _shell.ExplorerHelper.HideExplorerTaskbar = true;

        var tray = _shell.NotificationArea;
        ((INotifyCollectionChanged)tray.TrayIcons).CollectionChanged += (_, e) =>
        {
            foreach (NotifyIcon icon in e.NewItems ?? Array.Empty<NotifyIcon>())
            {
                if (TrayPins.FirstRun) TrayPins.ImportIfPromoted(icon);
                icon.PropertyChanged += (_, _) => TrayChanged?.Invoke();
            }
            TrayChanged?.Invoke();
        };
        foreach (var icon in tray.TrayIcons.ToList()) icon.PropertyChanged += (_, _) => TrayChanged?.Invoke();

        _shell.Tasks.Windows.CollectionChanged += (_, e) =>
        {
            foreach (ApplicationWindow w in e.NewItems ?? Array.Empty<ApplicationWindow>()) w.PropertyChanged += Window_Changed;
            WindowsChanged?.Invoke();
        };
        foreach (var w in Windows()) w.PropertyChanged += Window_Changed;
        Diag.Write("Shell", "started");
    }

    static void Window_Changed(object s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ApplicationWindow.Title) or nameof(ApplicationWindow.State) or nameof(ApplicationWindow.ProgressState)
            or nameof(ApplicationWindow.ProgressValue) or nameof(ApplicationWindow.OverlayIcon) or nameof(ApplicationWindow.Icon))
            WindowsChanged?.Invoke();
    }

    public static void Stop()
    {
        if (_shell == null) return;
        try { _shell.ExplorerHelper.HideExplorerTaskbar = false; } catch { }
        try { _shell.Dispose(); } catch { }                           // removes the own Shell_TrayWnd; apps give their icons back to Explorer
        _shell = null;
    }

    static uint ProcessOf(IntPtr window) { Win32.GetWindowThreadProcessId(window, out uint pid); return pid; }

    static IEnumerable<ApplicationWindow> Windows() =>
        _shell?.Tasks.Windows.ToList() ?? new List<ApplicationWindow>();

    /// <summary>A window of the taskbar that is open now: it exists, is shown, and its app did not hide it through DWM.</summary>
    static bool IsOpen(ApplicationWindow w) =>
        w.ShowInTaskbar && Win32.IsWindow(w.Handle) && Win32.IsWindowVisible(w.Handle) && !Win32.IsCloakedByApp(w.Handle);

    static HashSet<IntPtr> _open = new();
    static long _openCheckedAt;

    /// <summary>
    /// Goes up whenever the open windows change (checked at most twice a second): a window shown again or hidden by its app
    /// through DWM comes without an event of ManagedShell – each dock builds itself again when it sees a new number.
    /// </summary>
    public static int OpenVersion
    {
        get
        {
            if (Environment.TickCount64 - _openCheckedAt >= 500)
            {
                _openCheckedAt = Environment.TickCount64;
                var now = Windows().Where(IsOpen).Select(w => w.Handle).ToHashSet();
                if (!now.SetEquals(_open)) { _open = now; _openVersion++; }
            }
            return _openVersion;
        }
    }
    static int _openVersion;

    /// <summary>The windows of the taskbar with what the apps report on them (Store apps included, unlike a plain list of windows).</summary>
    public static List<TaskWindow> TaskWindows() => Windows()
        // a window gone or hidden is left out at once: ManagedShell keeps it until Windows tells it is destroyed, and an
        // app that only hides its window (a browser running on in the background) kept its running mark for a long time
        .Where(IsOpen)
        // a Store app runs in ApplicationFrameHost.exe: the app itself instead (its icon and name, not the host's); an app
        // installed as a package (the new Photos: WindowsApps\…\Photos.exe) too – its program has no icon of its own
        .Select(w => (Window: w, Pid: ProcessOf(w.Handle)))
        .Select(x => (x.Window, x.Pid, Aumid: !string.IsNullOrEmpty(x.Window.AppUserModelID) ? x.Window.AppUserModelID : PackagedAumid(x.Window.WinFileName, x.Pid)))
        .Select(x => new TaskWindow(x.Window.Handle, x.Window.Title,
            !string.IsNullOrEmpty(x.Aumid) && (x.Window.WinFileName?.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) == true || IsPackaged(x.Window.WinFileName))
                ? "shell:AppsFolder\\" + x.Aumid : x.Window.WinFileName, x.Pid)
        {
            AppId = x.Aumid,
            Overlay = ToImage(x.Window.OverlayIcon),
            Progress = (int)x.Window.ProgressState == 0 ? -1 : x.Window.ProgressValue / 100.0,
            Flashing = x.Window.State == ApplicationWindow.WindowState.Flashing,
            Active = x.Window.State == ApplicationWindow.WindowState.Active
        }).ToList();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int GetApplicationUserModelId(IntPtr process, ref int length, System.Text.StringBuilder id);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static readonly Dictionary<uint, string> Aumids = new();

    /// <summary>A program installed as a package of Windows (in WindowsApps).</summary>
    static bool IsPackaged(string exe) => exe?.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The app id of a packaged program's process (its window has none of its own); remembered by the process.</summary>
    static string PackagedAumid(string exe, uint pid)
    {
        if (!IsPackaged(exe)) return null;
        if (Aumids.TryGetValue(pid, out var known)) return known;
        string id = null;
        IntPtr h = OpenProcess(0x1000, false, pid);                      // PROCESS_QUERY_LIMITED_INFORMATION
        if (h != IntPtr.Zero)
        {
            try
            {
                int length = 256;
                var sb = new System.Text.StringBuilder(length);
                if (GetApplicationUserModelId(h, ref length, sb) == 0) id = sb.ToString();
            }
            finally { CloseHandle(h); }
        }
        if (Aumids.Count > 300) Aumids.Clear();
        return Aumids[pid] = id;
    }

    /// <summary>
    /// The visible icons of the notification area (without the network, volume and battery icons the dock draws itself and
    /// without the apps hidden in Settings → Status icons → Icons in the notification area).
    /// </summary>
    public static List<TrayItem> Tray() => All().Where(i => !IsHidden(i)).ToList();

    /// <summary>All icons of the notification area, also of the apps hidden in the settings (for that list).</summary>
    public static List<TrayItem> All() => (_shell?.NotificationArea.TrayIcons.ToList() ?? new List<NotifyIcon>())
        .Where(TrayPins.IsVisible)
        .OrderBy(n => n.IsPinned ? n.PinOrder : int.MaxValue)                // the pinned ones in the order they were put in
        .Select(n => Items.TryGetValue(n, out var item) ? item : Items[n] = new TrayItem { Icon = n, Id = n.GUID != Guid.Empty ? n.GUID.ToString() : n.Path + "|" + n.UID })
        .ToList();

    /// <summary>The icon of Windows Security also while it is left out of the area (its state and its tooltip for the shield).</summary>
    public static TrayItem SecurityIcon()
    {
        var n = _shell?.NotificationArea.TrayIcons.FirstOrDefault(i => i.Path?.EndsWith("SecurityHealthSystray.exe", StringComparison.OrdinalIgnoreCase) == true);
        return n == null ? null : Items.TryGetValue(n, out var item) ? item : Items[n] = new TrayItem { Icon = n, Id = n.GUID != Guid.Empty ? n.GUID.ToString() : n.Path + "|" + n.UID };
    }

    /// <summary>The icons of an app (by its program) are hidden for good: not in the area, not under the arrow.</summary>
    public static bool IsHidden(TrayItem item) =>
        item.Path is { Length: > 0 } path && DockSettings.Current.TrayHidden.Split('|', StringSplitOptions.RemoveEmptyEntries).Contains(path, StringComparer.OrdinalIgnoreCase);

    public static void SetHidden(string path, bool hidden)
    {
        var list = DockSettings.Current.TrayHidden.Split('|', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hidden) list.Add(path);
        DockSettings.Current.TrayHidden = string.Join("|", list);
        TrayChanged?.Invoke();
    }

    /// <summary>A click of the mouse on a tray icon, sent to its app like the taskbar of Windows does (button: 0 left, 1 right, 2 middle).</summary>
    public static void Click(TrayItem item, int button, bool down)
    {
        var b = button switch { 1 => MouseButton.Right, 2 => MouseButton.Middle, _ => MouseButton.Left };
        uint at = MouseHelper.GetCursorPositionParam();
        int dbl = (int)Native.GetDoubleClickTime();
        if (down) item.Icon.IconMouseDown(b, at, dbl); else item.Icon.IconMouseUp(b, at, dbl);
    }

    public static void Hover(TrayItem item, bool enter)
    {
        uint at = MouseHelper.GetCursorPositionParam();
        if (enter) item.Icon.IconMouseEnter(at); else item.Icon.IconMouseLeave(at);
    }

    public static void TogglePin(TrayItem item) => TrayPins.Toggle(item.Icon, _shell.NotificationArea);

    /// <summary>The icon pinned at the place of another one (dragged before it).</summary>
    public static void PinAt(TrayItem item, TrayItem before)
    {
        // places in the whole list of pinned icons, with those of apps that are not running now
        bool pinned = item.Icon.IsPinned;
        int position = before is { IsPinned: true } && before != item
            ? before.PinOrder - (pinned && item.PinOrder < before.PinOrder ? 1 : 0)
            : _shell.NotificationArea.PinnedNotifyIcons.Length - (pinned ? 1 : 0);
        if (pinned) item.Icon.Unpin();
        item.Icon.Pin(Math.Max(0, position));
        TrayPins.Save(_shell.NotificationArea);
    }

    /// <summary>Where the icon is drawn, so the app places its menu next to it (pixels on the screen).</summary>
    public static void SetPlacement(TrayItem item, Native.RECT r) =>
        item.Icon.Placement = new ManagedShell.Interop.NativeMethods.Rect { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };

    /// <summary>A picture of the shell part (premultiplied BGRA) for the app; null for none.</summary>
    internal static IconImage ToImage(ShellImage source) =>
        source == null || source.Width <= 0 ? null : new IconImage(source.Pixels, source.Width, source.Height);
}

/// <summary>
/// Which notification-area icons are shown in the dock (ManagedShell pins, stored in tray.json).
/// On first run, icons that Windows shows on the taskbar ("Other system tray icons" switched on) get pinned.
/// </summary>
static class TrayPins
{
    static string FilePath => AppPaths.Of("tray.json");
    static HashSet<string> _promoted;

    // Network, volume, battery and removable drives are drawn by the dock itself, so the legacy system icons are hidden.
    static readonly HashSet<string> Duplicates = new(StringComparer.OrdinalIgnoreCase)
    {
        NotificationArea.NETWORK_GUID, NotificationArea.POWER_GUID, NotificationArea.VOLUME_GUID,
        "7820ae78-23e3-4229-82c1-e41cb67d5b9c"                       // "Safely remove hardware": the dock has its own icon for it
    };

    public static bool FirstRun => !File.Exists(FilePath);

    public static string[] Load()
    {
        try { return JsonSerializer.Deserialize<string[]>(File.ReadAllText(FilePath)) ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    public static void Save(NotificationArea area)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(area.PinnedNotifyIcons));
        }
        catch (Exception ex) { Log.Write("TrayPins.Save", ex); }
    }

    public static bool IsVisible(NotifyIcon n) => n != null && !n.IsHidden && !Duplicates.Contains(n.GUID.ToString()) && !IsUpdateIcon(n) && !IsSecurityShownApart(n);

    /// <summary>Windows Security drawn among the status icons (its shield): its own icon is left out of the area then.</summary>
    static bool IsSecurityShownApart(NotifyIcon n) =>
        DockSettings.Current.ShowSecurity && SecurityStatus.DefenderActive
        && n.Path?.EndsWith("SecurityHealthSystray.exe", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The icon of Windows Update ("restart to finish the updates"): the dock shows its own sign among the status icons
    /// (Status signs), so this one is left out then. Windows 11 shows it by MoNotificationUx.exe, Windows 10 by MusNotifyIcon.exe.
    /// </summary>
    static bool IsUpdateIcon(NotifyIcon n) =>
        DockSettings.Current.ShowStatusSigns && Path.GetFileName(n.Path ?? "") is { } exe
        && (exe.Equals("MoNotificationUx.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("MusNotifyIcon.exe", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("MusNotification.exe", StringComparison.OrdinalIgnoreCase));

    public static void Toggle(NotifyIcon icon, NotificationArea area)
    {
        if (icon.IsPinned) icon.Unpin(); else icon.Pin();
        Save(area);
    }

    /// <summary>First run: pin the icon if Windows shows it on the taskbar.</summary>
    public static void ImportIfPromoted(NotifyIcon icon)
    {
        if (icon.IsPinned || string.IsNullOrEmpty(icon.Path)) return;
        _promoted ??= ReadPromoted();
        if (_promoted.Contains(icon.Path)) icon.Pin();
    }

    static HashSet<string> ReadPromoted()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            foreach (var name in root?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var k = root.OpenSubKey(name);
                if (k?.GetValue("IsPromoted") is int p && p == 1 && k.GetValue("ExecutablePath") is string exe)
                    set.Add(ExpandKnownFolder(exe));
            }
        }
        catch (Exception ex) { Log.Write("TrayPins.ReadPromoted", ex); }
        return set;
    }

    // Paths are stored like "{6D809377-...}\Steam\steam.exe" (known folder GUID + relative path).
    static string ExpandKnownFolder(string path)
    {
        int end = path.IndexOf('}');
        if (path.StartsWith("{") && end > 0 && Guid.TryParse(path.Substring(0, end + 1), out var id)
            && Native.SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out string folder) == 0)
            return folder + path.Substring(end + 1);
        return path;
    }
}
