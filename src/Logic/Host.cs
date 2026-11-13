using System.Runtime.InteropServices;
using System.Text.Json;

namespace HeMiDock;

/// <summary>
/// What the logic asks of the user interface without knowing it: the app sets these when it starts. Unset, a request
/// does nothing (or gives the safe answer), so the logic also runs without any window.
/// </summary>
static class Host
{
    /// <summary>A command of a shortcut or a menu that only the interface can do ("control", "calendar", "settings", "spotlight", "powermenu", "setting:title\nplace").</summary>
    public static Action<string> Command { get; set; }
    /// <summary>A question for a line of text (title, question, the text so far); null = cancelled.</summary>
    public static Func<string, string, string, Task<string>> AskText { get; set; }
    /// <summary>A menu at the mouse.</summary>
    public static Action<IReadOnlyList<(string Text, Action Click)>> ShowMenu { get; set; }
    public static Action<string> PinToDock { get; set; }
    /// <summary>An app is being started (its icon in the dock can show it).</summary>
    public static Action<string> Launching { get; set; }
    /// <summary>Where the notifications go "at the clock": the place of the clock on that monitor, the gap and the screen edge of its bar.</summary>
    public static Func<MonInfo, (Native.RECT Anchor, double GapDip, int Edge)?> ToastAnchor { get; set; }
    /// <summary>The zones of a monitor while a window is dragged into them (the highlighted one), and their end.</summary>
    public static Action<MonInfo, Rect[], int> ShowZones { get; set; }
    public static Action HideZones { get; set; }
    /// <summary>A short text in a small window above the dock for a moment (the zone layout switched by a shortcut).</summary>
    public static Action<string> ShowInfo { get; set; }
    /// <summary>A backup of the settings and pins before a big change (the reason).</summary>
    public static Action<string> Backup { get; set; }
    /// <summary>The pins of the dock and Start go with exported settings: add them, find them in a file, take them over.</summary>
    public static Action<Dictionary<string, object>> AddPins { get; set; }
    public static Func<JsonElement, bool> HasPins { get; set; }
    public static Action<JsonElement> ApplyPins { get; set; }
    /// <summary>The settings of the app found by a text, for the search of Start.</summary>
    public static Func<string, IEnumerable<StartEntry>> SearchSettings { get; set; }
    /// <summary>A notification the user removed from the list of the clock (it does not count as new).</summary>
    public static Func<uint, bool> IsDismissed { get; set; }
}

/// <summary>Light / dark mode and the transparency effects of Windows (the app refreshes them when Windows changes them).</summary>
static class Theme
{
    public static bool IsLight { get; private set; }
    public static bool WindowsTransparency { get; private set; } = true;
    /// <summary>The colour of the app as a COLORREF (0x00BBGGRR): the own one from the settings or the accent of Windows.</summary>
    public static int AccentColorRef { get; private set; } = 0xD47800;
    public static event Action Changed;

    static Theme() => Refresh();

    public static void Refresh()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            int mode = DockSettings.Current?.ThemeMode ?? 0;
            IsLight = mode == 1 || mode == 0 && key?.GetValue("AppsUseLightTheme") is int light && light != 0;
            WindowsTransparency = key?.GetValue("EnableTransparency") is not int t || t != 0;
            var own = DockSettings.Current?.AccentMode == 1 ? DockSettings.Current.AccentColor : null;
            if (own is { Length: 7 } && int.TryParse(own[1..], System.Globalization.NumberStyles.HexNumber, null, out int rgb))
                AccentColorRef = (rgb >> 16 & 0xFF) | (rgb & 0xFF00) | (rgb & 0xFF) << 16;
            else
            {
                using var dwm = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (dwm?.GetValue("AccentColor") is int abgr) AccentColorRef = abgr & 0xFFFFFF;
            }
        }
        catch { }
        Changed?.Invoke();
    }
}

/// <summary>Text on the clipboard of Windows, without a UI framework.</summary>
static class ClipboardText
{
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
    const uint CF_UNICODETEXT = 13;

    public static bool Has(uint format) => IsClipboardFormatAvailable(format);

    public static string Get()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT) || !OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            IntPtr h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            IntPtr p = GlobalLock(h);
            try { return Marshal.PtrToStringUni(p); }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    public static void Set(string text)
    {
        if (text == null || !OpenClipboard(IntPtr.Zero)) return;
        try
        {
            EmptyClipboard();
            IntPtr h = GlobalAlloc(0x2, (UIntPtr)((text.Length + 1) * 2));   // GMEM_MOVEABLE; the clipboard owns it afterwards
            IntPtr p = GlobalLock(h);
            Marshal.Copy((text + "\0").ToCharArray(), 0, p, text.Length + 1);
            GlobalUnlock(h);
            SetClipboardData(CF_UNICODETEXT, h);
        }
        finally { CloseClipboard(); }
    }
}
