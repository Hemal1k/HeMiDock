using System.Runtime.InteropServices;

namespace HeMiDock;

/// <summary>
/// Reserves a strip along an edge of a monitor like the taskbar, so maximized windows stay out of it.
/// Edge as in the settings: 0 bottom, 1 left, 2 right, 3 top.
/// </summary>
sealed partial class AppBar : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public Win32.RECT rc; public IntPtr lParam; }

    [DllImport("shell32.dll")] static extern uint SHAppBarMessage(uint msg, ref APPBARDATA data);
    const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;

    readonly IntPtr _hwnd;
    bool _on;

    public AppBar(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>Reserves <paramref name="size"/> pixels along the edge of the monitor; returns the strip Windows gave.</summary>
    public Win32.RECT Reserve(int size, int edge, Native.RECT screen)
    {
        uint abe = edge switch { 1 => 0u, 2 => 2u, 3 => 1u, _ => 3u };       // ABE_LEFT, ABE_RIGHT, ABE_TOP, ABE_BOTTOM
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd, uEdge = abe };
        if (!_on) { SHAppBarMessage(ABM_NEW, ref data); _on = true; }
        // from the edge of the screen itself: the free part of it is already smaller by this strip after the first time
        data.rc = new Win32.RECT { Left = screen.Left, Top = screen.Top, Right = screen.Right, Bottom = screen.Bottom };
        switch (edge)
        {
            case 1: data.rc.Right = screen.Left + size; break;
            case 2: data.rc.Left = screen.Right - size; break;
            case 3: data.rc.Bottom = screen.Top + size; break;
            default: data.rc.Top = screen.Bottom - size; break;
        }
        SHAppBarMessage(ABM_QUERYPOS, ref data);
        switch (edge)                                                     // Windows may move the strip inwards (another bar): the size stays
        {
            case 1: data.rc.Right = data.rc.Left + size; break;
            case 2: data.rc.Left = data.rc.Right - size; break;
            case 3: data.rc.Bottom = data.rc.Top + size; break;
            default: data.rc.Top = data.rc.Bottom - size; break;
        }
        SHAppBarMessage(ABM_SETPOS, ref data);
        return data.rc;
    }

    public void Dispose()
    {
        if (!_on) return;
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };
        SHAppBarMessage(ABM_REMOVE, ref data);
        _on = false;
    }
}
