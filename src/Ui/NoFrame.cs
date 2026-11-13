using System.Runtime.InteropServices;

namespace HeMiDock;

/// <summary>
/// A window of WinUI without a title bar keeps a thin frame of Windows (WS_DLGFRAME, put back by the presenter): it takes a
/// few pixels of the content and draws a light line. The whole window is made its content instead (WM_NCCALCSIZE), and it
/// may be as small as it needs.
/// </summary>
static class NoFrame
{
    delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    const uint WM_NCCALCSIZE = 0x0083, WM_GETMINMAXINFO = 0x0024, WM_STYLECHANGING = 0x007C;
    const int Frame = 0x00C00000 | 0x00040000;                       // WS_CAPTION (border + dialog frame), WS_THICKFRAME
    static readonly SubclassProc Proc = (h, msg, w, l, _, _) =>   // kept alive: Windows calls it
    {
        if (msg == WM_NCCALCSIZE && w != IntPtr.Zero) return IntPtr.Zero;
        // the presenter of WinUI puts the frame back whenever it likes: it never gets it (the frame drew a dark line on top)
        if (msg == WM_STYLECHANGING && (int)w == -16) Marshal.WriteInt32(l, 4, Marshal.ReadInt32(l, 4) & ~Frame);   // STYLESTRUCT.styleNew
        var result = DefSubclassProc(h, msg, w, l);
        if (msg == WM_GETMINMAXINFO)
        {
            // no smallest size of Windows (about 140 px wide): a dock at a side edge is narrower
            Marshal.WriteInt32(l, 24, 1);                            // ptMinTrackSize.x
            Marshal.WriteInt32(l, 28, 1);                            // ptMinTrackSize.y
        }
        return result;
    };

    /// <summary>
    /// Not in Alt+Tab, not on a taskbar and not offered as a window of an app to share (Teams and other pickers of windows list
    /// every window that is no tool window – IsShownInSwitchers alone left the dock there): always a tool window, without the
    /// mark of an app window. IsShownInSwitchers as well; Windows refuses it (not implemented) while the taskbar of Explorer is
    /// starting again – the tool window does the same then.
    /// </summary>
    public static void HideFromSwitchers(Microsoft.UI.Windowing.AppWindow window, IntPtr hwnd)
    {
        const int WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
        Native.SetWindowLong(hwnd, -20, (Native.GetWindowLong(hwnd, -20) | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
        try { window.IsShownInSwitchers = false; }
        catch { }
    }

    public static void Apply(IntPtr hwnd)
    {
        SetWindowSubclass(hwnd, Proc, (UIntPtr)0x484D, UIntPtr.Zero);
        Native.SetWindowLong(hwnd, -16, Native.GetWindowLong(hwnd, -16) & ~Frame);
        Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x1 | 0x2 | 0x4 | 0x10 | 0x20);   // NOSIZE NOMOVE NOZORDER NOACTIVATE FRAMECHANGED
    }
}
