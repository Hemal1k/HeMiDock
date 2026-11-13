using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HeMiDock;

static class Native
{
    public delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, IntPtr rc, IntPtr data);
    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }
    public const uint DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public int dsBitfields0, dsBitfields1, dsBitfields2;
        public IntPtr dshSection;
        public int dsOffset;
    }

    [DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);
    [DllImport("shell32.dll")]
    public static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, [MarshalAs(UnmanagedType.LPWStr)] out string path);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] public static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] public static extern bool SendNotifyMessage(IntPtr h, uint msg, UIntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFOEX mi);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(uint pid);
    public const uint ASFW_ANY = unchecked((uint)-1);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern short GetKeyState(int vKey);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc cb, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", EntryPoint = "GetSystemPowerStatus")] static extern bool RealPowerStatus(out SYSTEM_POWER_STATUS s);
    public static bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s)
    {
#if DEBUG
        if (Simulation.Battery) { s = Simulation.PowerStatus(); return true; }
#endif
        return RealPowerStatus(out s);
    }

    [DllImport("gdi32.dll")] public static extern int GetObject(IntPtr h, int cb, out DIBSECTION ds);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);

    [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
    [DllImport("dwmapi.dll")] public static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);

    /// <summary>The visible frame of a window (without the invisible resize borders of Windows 10 / 11).</summary>
    public static bool VisibleBounds(IntPtr h, out RECT r) =>
        DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf<RECT>()) == 0 || GetWindowRect(h, out r);   // DWMWA_EXTENDED_FRAME_BOUNDS

    /// <summary>Hidden by DWM – e.g. a window on another virtual desktop or a suspended Store app.</summary>
    public static bool IsCloaked(IntPtr h) => DwmGetWindowAttribute(h, 14, out int c, sizeof(int)) == 0 && c != 0;   // DWMWA_CLOAKED

    // ───────────────────────── window styles ─────────────────────────

    public const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
    public const int DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2;

    public static void SetDwmAttr(IntPtr hwnd, int attr, int value) => DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int));

    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);   // 2 = MONITOR_DEFAULTTONEAREST

    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    /// <summary>Scaling of a monitor (1 = 100 %, 1.5 = 150 % …), null when unknown.</summary>
    public static double? MonitorDpi(IntPtr monitor)
    {
        try { return GetDpiForMonitor(monitor, 0, out uint x, out _) == 0 && x > 0 ? x / 96.0 : null; }   // MDT_EFFECTIVE_DPI
        catch { return null; }
    }

    // ───────────────────────── keyboard ─────────────────────────

    public const byte VK_LWIN = 0x5B, VK_TAB = 0x09, VK_D = 0x44;

    public static void KeyCombo(params byte[] keys)
    {
        foreach (var k in keys) keybd_event(k, 0, 0, UIntPtr.Zero);
        for (int i = keys.Length - 1; i >= 0; i--) keybd_event(keys[i], 0, 2 /*KEYUP*/, UIntPtr.Zero);
    }
}

[StructLayout(LayoutKind.Sequential)]
struct SIZE { public int cx, cy; }

[ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellItemImageFactory
{
    [PreserveSig] int GetImage(SIZE size, uint flags, out IntPtr phbm);
}

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellItem
{
    void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    void GetParent(out IShellItem ppsi);
    void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
}

[StructLayout(LayoutKind.Sequential)]
struct PROPERTYKEY
{
    public Guid fmtid;
    public uint pid;
    public PROPERTYKEY(Guid f, uint p) { fmtid = f; pid = p; }
}

/// <summary>Full method table (IShellItem + IShellItem2) – order must match the COM interface exactly.</summary>
[ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellItem2
{
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetParent(out IShellItem ppsi);
    [PreserveSig] int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
    [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetPropertyStoreWithCreateObject(int flags, IntPtr punkCreateObject, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, int flags, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetPropertyDescriptionList(ref PROPERTYKEY keyType, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int Update(IntPtr pbc);
    [PreserveSig] int GetProperty(ref PROPERTYKEY key, IntPtr ppropvar);
    [PreserveSig] int GetCLSID(ref PROPERTYKEY key, out Guid pclsid);
    [PreserveSig] int GetFileTime(ref PROPERTYKEY key, out long pft);
    [PreserveSig] int GetInt32(ref PROPERTYKEY key, out int pi);
    [PreserveSig] int GetString(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.LPWStr)] out string ppsz);
    [PreserveSig] int GetUInt32(ref PROPERTYKEY key, out uint pui);
    [PreserveSig] int GetUInt64(ref PROPERTYKEY key, out ulong pull);
    [PreserveSig] int GetBool(ref PROPERTYKEY key, out int pf);
}

[ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IEnumShellItems
{
    [PreserveSig] int Next(uint celt, out IShellItem rgelt, out uint pceltFetched);
    [PreserveSig] int Skip(uint celt);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out IEnumShellItems ppenum);
}

record MonInfo(IntPtr Handle, string Device, Native.RECT Bounds, bool Primary, Native.RECT Work);

static class Monitors
{
    /// <summary>All monitors, primary first. Coordinates are physical pixels of the virtual desktop.</summary>
    public static List<MonInfo> All()
    {
        var list = new List<MonInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(h, ref mi))
                list.Add(new MonInfo(h, mi.szDevice, mi.rcMonitor, (mi.dwFlags & 1) != 0, mi.rcWork));
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(m => m.Primary).ThenBy(m => m.Bounds.Left).ToList();
    }

    public static MonInfo Primary() => All().First();

    /// <summary>The monitor chosen for the dock (Settings), the main one when that monitor is not connected.</summary>
    public static MonInfo Dock()
    {
        var all = All();
        int i = DockSettings.Current.DockMonitor;
        return i >= 0 && i < all.Count ? all[i] : all[0];
    }
}

/// <summary>Emergency restore of the Explorer taskbar from another process (the watchdog, when the dock crashed).</summary>
static class TaskbarController
{
    const uint ABM_SETSTATE = 10;

    static bool IsOurs(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out uint pid);
        return pid == Environment.ProcessId;
    }

    public static void Restore()
    {
        IntPtr tray = IntPtr.Zero;
        while ((tray = Native.FindWindowEx(IntPtr.Zero, tray, "Shell_TrayWnd", null)) != IntPtr.Zero && IsOurs(tray)) { }

        var d = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = tray, lParam = (IntPtr)2 };   // ABS_ALWAYSONTOP
        Native.SHAppBarMessage(ABM_SETSTATE, ref d);
        foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            IntPtr h = IntPtr.Zero;
            while ((h = Native.FindWindowEx(IntPtr.Zero, h, cls, null)) != IntPtr.Zero)
                if (!IsOurs(h)) Native.ShowWindow(h, 5);                // SW_SHOW
        }
        uint msg = Native.RegisterWindowMessage("TaskbarCreated");     // apps re-add their tray icons to Explorer
        if (msg != 0) Native.SendNotifyMessage((IntPtr)0xFFFF, msg, UIntPtr.Zero, IntPtr.Zero);
    }
}

/// <summary>
/// Icons, names and launching of apps used by the Start menu.
/// Apps are identified by a key: path to .exe, or "shell:AppsFolder\AUMID" for packaged (Store) apps.
/// </summary>
static class AppShell
{
    static readonly ConcurrentDictionary<string, IconImage> IconCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The icon of an app (a program or "shell:AppsFolder\…"), remembered; null when Windows has none.</summary>
    public static IconImage Icon(string key)
    {
        if (IconCache.TryGetValue(key, out var cached)) return cached;
        IconImage icon = null;
        try { icon = ShellItemIcon(key); } catch { }
        if (icon != null) IconCache[key] = icon;                       // a failure is not kept: the shell may be busy then (the icon stayed empty for good)
        return icon;
    }

    /// <summary>Forgets the loaded icons: they are read from Windows again.</summary>
    public static void ForgetIcons() => IconCache.Clear();

    public static bool IsShellKey(string key) => key.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Icon of any file / folder through the shell (documents, shortcuts, folders…).</summary>
    public static IconImage FileTypeIcon(string path)
    {
        if (IconCache.TryGetValue("f:" + path, out var cached)) return cached;
        IconImage icon = null;
        try { icon = ShellItemIcon(path); } catch { }
        if (icon != null) IconCache["f:" + path] = icon;
        return icon;
    }

    static readonly ConcurrentDictionary<int, IconImage> Generic = new();

    /// <summary>
    /// The picture Windows gives when it cannot read the icon of an app (a plain window): the icon of an empty program, taken
    /// once. File Explorer got it for a while and kept it – it is no icon then (not remembered, read again next time).
    /// </summary>
    static bool IsGeneric(IconImage icon)
    {
        try
        {
            if (!Generic.TryGetValue(icon.Width, out var generic))
            {
                string empty = Path.Combine(Path.GetTempPath(), "HeMiDock-generic.exe");   // no icon of its own: the generic one
                if (!File.Exists(empty)) File.WriteAllBytes(empty, Array.Empty<byte>());
                if (ShellIcons.Get(empty, icon.Width) is not { } got) return false;
                Generic[icon.Width] = generic = new IconImage(got.Pixels, got.Width, got.Height);
            }
            if (generic.Width != icon.Width || generic.Height != icon.Height) return false;
            int differ = 0;
            for (int i = 0; i < icon.Pixels.Length; i++)
                if (Math.Abs(icon.Pixels[i] - generic.Pixels[i]) > 24 && ++differ > icon.Pixels.Length / 100) return false;
            return true;
        }
        catch { return false; }
    }

    static IconImage ShellItemIcon(string parsingName)
    {
        var icon = ShellItemIconAnySize(parsingName);
        return icon != null && IsGeneric(icon) ? null : icon;
    }

    static IconImage ShellItemIconAnySize(string parsingName)
    {
        // An app without big icons (e.g. 7-Zip) comes back at 128 px as its small icon in the middle of a thin frame –
        // then a smaller size is asked for, which the shell gives without the frame.
        IconImage result = null;
        foreach (int size in new[] { 128, 96, 64, 48, 32 })
        {
            if (ShellIcons.Get(parsingName, size) is not { } got) break;
            result = new IconImage(got.Pixels, got.Width, got.Height);
            if (!IsFramedSmallIcon(result)) break;
        }
        return result;
    }

    /// <summary>
    /// The shell's picture of a small icon blown up to a big size: opaque frame along the edges, an empty ring inside it
    /// and the small icon in the middle.
    /// </summary>
    static bool IsFramedSmallIcon(IconImage bmp)
    {
        try
        {
            int w = bmp.Width, h = bmp.Height;
            if (w < 64 || h < 64) return false;
            var px = bmp.Pixels;
            byte A(int x, int y) => px[(y * w + x) * 4 + 3];

            // from every edge at 1/4, 1/2 and 3/4 of it inwards: a thin line at the same depth (a round or a rounded logo has none
            // at 1/4), then an empty gap. With other display scaling the frame lies a few pixels inside, is fainter and the small
            // icon is larger, so only the gap right behind the line counts, not a fixed place.
            int limit = Math.Max(3, w / 10), gap = Math.Max(3, w / 40);
            bool Side(Func<int, int, (int x, int y)> at)
            {
                int first = -1;
                foreach (int along in new[] { w / 4, w / 2, w * 3 / 4 })
                {
                    int d = 0;
                    while (d < limit && A(at(along, d).x, at(along, d).y) <= 4) d++;
                    if (d >= limit || (first >= 0 && Math.Abs(d - first) > 2)) return false;
                    if (first < 0) first = d;
                    while (d < limit + 3 && A(at(along, d).x, at(along, d).y) > 4) d++;      // the line itself
                    if (d - first > 4) return false;
                    for (int i = 0; i < gap; i++) if (A(at(along, d + i).x, at(along, d + i).y) > 8) return false;
                }
                return true;
            }
            return Side((a, d) => (a, d)) && Side((a, d) => (a, h - 1 - d)) && Side((a, d) => (d, a)) && Side((a, d) => (w - 1 - d, a));
        }
        catch { return false; }
    }

    public static string AppName(string key)
    {
        if (NameCache.TryGetValue(key, out var cached)) return cached;
        string name = null;
        try
        {
            if (IsShellKey(key))
            {
                var iid = typeof(IShellItem).GUID;
                Native.SHCreateItemFromParsingName(key, IntPtr.Zero, ref iid, out var obj);
                try { ((IShellItem)obj).GetDisplayName(0, out name); }   // SIGDN_NORMALDISPLAY
                finally { Marshal.ReleaseComObject(obj); }
            }
            else name = FileVersionInfo.GetVersionInfo(key).FileDescription;
        }
        catch { }
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(key);
        return NameCache[key] = name;
    }

    /// <summary>Display name of a shell item (shortcut, folder, shell:AppsFolder\…), null when unknown.</summary>
    public static string DisplayName(string parsingName)
    {
        try
        {
            var iid = typeof(IShellItem).GUID;
            Native.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var obj);
            try { ((IShellItem)obj).GetDisplayName(0, out string name); return name; }   // SIGDN_NORMALDISPLAY
            finally { Marshal.ReleaseComObject(obj); }
        }
        catch { return null; }
    }
    /// <summary>Resolves a .lnk file: target path, arguments and AppUserModelID (if any).</summary>
    public static (string target, string args, string appId) ResolveShortcut(string lnkPath)
    {
        string target = null, args = null, appId = null;
        try
        {
            dynamic wsh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic sc = wsh.CreateShortcut(lnkPath);
            target = string.IsNullOrEmpty((string)sc.TargetPath) ? null : Environment.ExpandEnvironmentVariables((string)sc.TargetPath);   // %systemroot%\…
            args = sc.Arguments;
            Marshal.FinalReleaseComObject(sc);
            Marshal.FinalReleaseComObject(wsh);
        }
        catch { }
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            dynamic folder = shell.NameSpace(Path.GetDirectoryName(lnkPath));
            dynamic item = folder?.ParseName(Path.GetFileName(lnkPath));
            if (item != null) appId = item.ExtendedProperty("System.AppUserModel.ID") as string;
        }
        catch { }
        return (string.IsNullOrWhiteSpace(target) ? null : target, args, string.IsNullOrWhiteSpace(appId) ? null : appId);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out string path);

    /// <summary>
    /// What "Open file location" shows for an app: its program file, otherwise its shortcut in the Start menu folders
    /// (what Windows Start shows too), otherwise the install folder of a Store app. null when there is nothing to show.
    /// </summary>
    public static string FileLocation(string key, string exePath, string name)
    {
        if (exePath != null && File.Exists(exePath)) return exePath;
        if (key == null) return null;
        if (!IsShellKey(key)) return File.Exists(key) || Directory.Exists(key) ? key : null;
        try
        {
            string id = key.Substring(key.IndexOf('\\') + 1);           // after shell:AppsFolder\
            // a program registered by its path inside a known folder: {GUID}\Folder\app.exe
            if (id.StartsWith("{") && id.IndexOf('}') is int end and > 0
                && Guid.TryParse(id.Substring(0, end + 1), out var folder)
                && SHGetKnownFolderPath(folder, 0, IntPtr.Zero, out var root) == 0)
            {
                string path = Path.Combine(root, id.Substring(end + 1).TrimStart('\\'));
                if (File.Exists(path)) return path;
            }
            else if (File.Exists(id)) return id;

            // its shortcut in the Start menu (all users and the current user)
            string display = name ?? AppName(key);
            foreach (var programs in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
            {
                if (!Directory.Exists(programs)) continue;
                var lnk = Directory.EnumerateFiles(programs, "*.lnk", SearchOption.AllDirectories)
                    .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(display, StringComparison.OrdinalIgnoreCase));
                if (lnk != null) return lnk;
            }

            // a Store app: its install folder
            int bang = id.IndexOf('!');
            if (bang > 0)
            {
                var package = new Windows.Management.Deployment.PackageManager().FindPackagesForUser(string.Empty, id.Substring(0, bang)).FirstOrDefault();
                string folder2 = package?.InstalledLocation?.Path;
                if (folder2 != null && Directory.Exists(folder2)) return folder2;
            }
        }
        catch (Exception ex) { Log.Write("FileLocation " + key, ex); }
        return null;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> ElevatablePackages = new();

    /// <summary>
    /// Can a Store app run as administrator? Only a desktop app in a package (Terminal, …): its manifest says
    /// "Windows.FullTrustApplication" or a classic run (packagedClassicApp / win32App) – as the Start menu of Windows offers it.
    /// </summary>
    public static bool PackageCanElevate(string key)
    {
        string id = key.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase) ? key["shell:AppsFolder\\".Length..] : key;
        int bang = id.IndexOf('!');
        if (bang <= 0) return false;
        return ElevatablePackages.GetOrAdd(id, _ =>
        {
            try
            {
                var package = new Windows.Management.Deployment.PackageManager().FindPackagesForUser(string.Empty, id[..bang]).FirstOrDefault();
                string manifest = package?.InstalledLocation?.Path is { } folder ? Path.Combine(folder, "AppxManifest.xml") : null;
                if (manifest == null || !File.Exists(manifest)) return false;
                string xml = File.ReadAllText(manifest);
                var app = System.Text.RegularExpressions.Regex.Match(xml, $"<Application\\b[^>]*\\bId=\"{System.Text.RegularExpressions.Regex.Escape(id[(bang + 1)..])}\"[^>]*>");
                string tag = app.Success ? app.Value : xml;
                return tag.Contains("Windows.FullTrustApplication") || tag.Contains("packagedClassicApp") || tag.Contains("win32App");
            }
            catch { return false; }
        });
    }

    /// <summary>Opens Explorer with the file selected (or the folder itself).</summary>
    public static void ShowInExplorer(string location)
    {
        if (Directory.Exists(location)) Fg.Open("explorer.exe", $"\"{location}\"");
        else Fg.Open("explorer.exe", $"/select,\"{location}\"");
    }

    /// <summary>Does a file / installed Store app with this key exist?</summary>
    public static bool Exists(string key)
    {
        if (!IsShellKey(key)) return File.Exists(key);
        try
        {
            var iid = typeof(IShellItem).GUID;
            Native.SHCreateItemFromParsingName(key, IntPtr.Zero, ref iid, out var obj);
            Marshal.ReleaseComObject(obj);
            return true;
        }
        catch { return false; }
    }

    public static void Launch(string key, bool admin = false)
    {
        Diag.Write("Launch", admin ? key + " (as administrator)" : key);
        try
        {
            if (IsShellKey(key))
            {
                // the shell opens shell:AppsFolder\… itself; through Explorer the name must be quoted – remote apps (RDP)
                // have spaces and braces in it, unquoted they were cut into several arguments and nothing started
                if (admin) { Elevate(key); return; }
                try { Process.Start(new ProcessStartInfo(key) { UseShellExecute = true }); }
                catch
                {
                    // the name of an app reinstalled in another way is no longer known: the program itself, when it is still there.
                    // Explorer only for what Windows knows; for an unknown name it would open the Documents folder
                    if (File.Exists(StartMenuFile(key))) Process.Start(new ProcessStartInfo(StartMenuFile(key)) { UseShellExecute = true });
                    else if (DisplayName(key) != null) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{key}\"") { UseShellExecute = true });
                    else Diag.Write("Launch", "unknown app " + key);
                }
                return;
            }
            var psi = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(key)) { UseShellExecute = true };   // %systemroot%\…
            if (admin) psi.Verb = "runas";
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception) { /* UAC prompt cancelled / nothing to open */ }
        catch (Exception ex) { Log.Write("Launch " + key, ex); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out string path);

    /// <summary>
    /// A program known only by its place in the Start menu ("{known folder}\dsa.msc" or a path) as administrator: the file itself
    /// with the verb of Windows, a console through its host.
    /// </summary>
    static void Elevate(string key)
    {
        string name = StartMenuFile(key);
        var psi = new ProcessStartInfo(key) { UseShellExecute = true, Verb = "runas" };
        if (File.Exists(name))
            psi = name.EndsWith(".msc", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo("mmc.exe", $"\"{name}\"") { UseShellExecute = true, Verb = "runas" }
                : new ProcessStartInfo(name) { UseShellExecute = true, Verb = "runas" };
        Process.Start(psi);
    }

    /// <summary>The file path in the name of an app: "shell:AppsFolder\{known folder}\dsa.msc" or a path.</summary>
    internal static string StartMenuFile(string key)
    {
        string name = key.Substring("shell:AppsFolder\\".Length);
        if (name.StartsWith("{") && name.IndexOf('}') is > 0 and var close && Guid.TryParse(name[..(close + 1)], out var id)
            && SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var folder) == 0)
            name = folder + name[(close + 1)..];
        return name;
    }

    /// <summary>Presses and releases a key combination (e.g. Win+N).</summary>
    public static void Tap(params byte[] keys) => Native.KeyCombo(keys);
}
