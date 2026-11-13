using System.Runtime.InteropServices;

namespace HeMiDock;

/// <summary>The calls of Windows the logic needs.</summary>
static class Win32
{
    public delegate bool EnumProc(IntPtr h, IntPtr param);

    [StructLayout(LayoutKind.Sequential)] public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public Native.RECT ToNative() => new() { Left = Left, Top = Top, Right = Right, Bottom = Bottom };
    }

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr param);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder text, int size);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, out RECT rect, uint ini);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);

    public const int SW_RESTORE = 9, SW_MINIMIZE = 6;
    public const uint WM_CLOSE = 0x10;

    /// <summary>The window is hidden by Windows (another virtual desktop, a suspended app).</summary>
    public static bool IsCloaked(IntPtr h) => DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0;

    /// <summary>
    /// The app hid the window itself through DWM (DWM_CLOAKED_APP) – File Explorer keeps a closed window so, ready for the
    /// next one: it is no open window. A window on another virtual desktop is hidden by the shell (DWM_CLOAKED_SHELL) instead.
    /// </summary>
    public static bool IsCloakedByApp(IntPtr h) => DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && (cloaked & 1) != 0;

    /// <summary>The program of a process, also of one with higher rights (only its name is asked for).</summary>
    public static string ProcessPath(uint pid)
    {
        IntPtr p = OpenProcess(0x1000, false, pid);                   // PROCESS_QUERY_LIMITED_INFORMATION
        if (p == IntPtr.Zero) return null;
        try
        {
            var name = new System.Text.StringBuilder(1024);
            int size = name.Capacity;
            return QueryFullProcessImageName(p, 0, name, ref size) ? name.ToString() : null;
        }
        finally { CloseHandle(p); }
    }

    /// <summary>The free part of the main screen (pixels).</summary>
    public static RECT WorkArea()
    {
        SystemParametersInfo(0x30, 0, out var r, 0);                  // SPI_GETWORKAREA
        return r;
    }
}
