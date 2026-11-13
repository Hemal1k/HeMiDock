using System.Runtime.InteropServices;

namespace HeMiDock;

/// <summary>
/// The picture of an icon that follows the mouse while it is dragged (an icon of the notification area): a small
/// click-through layered window over everything, so it can leave the window it came from (the grid of the arrow).
/// </summary>
static class DragGhost
{
    delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS { public uint style; public WndProc proc; public int cls, wnd; public IntPtr inst, icon, cursor, brush; public string menu, name; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; }
    [StructLayout(LayoutKind.Sequential)] struct BLEND { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public int Size, Width, Height; public short Planes, Bits; public int Compression, ImageSize, XPels, YPels, Used, Important; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClass(ref WNDCLASS c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string cls, string title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pos, ref SIZE size, IntPtr src, ref POINT srcPos, int key, ref BLEND blend, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

    static readonly WndProc Proc = DefWindowProc;
    static IntPtr _window;
    static int _size;

    /// <summary>Shows the picture <paramref name="sizePx"/> pixels big, its middle at the mouse.</summary>
    public static void Show(IconImage picture, int sizePx, int x, int y)
    {
        try
        {
            if (picture == null || picture.Width <= 0 || !Make()) return;
            _size = sizePx;
            Draw(Scale(picture, sizePx), x - sizePx / 2, y - sizePx / 2);
            ShowWindow(_window, 4);                                        // SW_SHOWNOACTIVATE
        }
        catch (Exception ex) { Log.Write("Drag picture", ex); }
    }

    /// <summary>Follows the mouse.</summary>
    public static void Move(int x, int y)
    {
        if (_window == IntPtr.Zero || _size == 0) return;
        SetWindowPos(_window, new IntPtr(-1), x - _size / 2, y - _size / 2, 0, 0, 0x1 | 0x10);   // NOSIZE NOACTIVATE, topmost
    }

    public static void Hide()
    {
        _size = 0;
        if (_window != IntPtr.Zero) ShowWindow(_window, 0);
    }

    static bool Make()
    {
        if (_window != IntPtr.Zero) return true;
        var inst = GetModuleHandle(null);
        var wc = new WNDCLASS { proc = Proc, inst = inst, name = "HeMiDock.DragGhost" };
        RegisterClass(ref wc);
        // layered, click-through, tool window, no activation, topmost
        _window = CreateWindowEx(0x80000 | 0x20 | 0x80 | 0x08000000 | 0x8, "HeMiDock.DragGhost", "", unchecked((int)0x80000000), 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        return _window != IntPtr.Zero;
    }

    /// <summary>The picture in the size of the icon on the screen (nearest pixel; it is small and moves).</summary>
    static IconImage Scale(IconImage src, int size)
    {
        if (src.Width == size && src.Height == size) return src;
        var dst = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                Array.Copy(src.Pixels, ((y * src.Height / size) * src.Width + x * src.Width / size) * 4, dst, (y * size + x) * 4, 4);
        return new IconImage(dst, size, size);
    }

    static void Draw(IconImage picture, int left, int top)
    {
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFOHEADER { Size = Marshal.SizeOf<BITMAPINFOHEADER>(), Width = picture.Width, Height = -picture.Height, Planes = 1, Bits = 32 };
        var bitmap = CreateDIBSection(dc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(dc, bitmap);
        try
        {
            Marshal.Copy(picture.Pixels, 0, bits, picture.Pixels.Length);
            var at = new POINT { X = left, Y = top };
            var size = new SIZE { W = picture.Width, H = picture.Height };
            var src = new POINT();
            var blend = new BLEND { Alpha = 235, Format = 1 };             // a little see-through, with its own alpha
            UpdateLayeredWindow(_window, screen, ref at, ref size, dc, ref src, 0, ref blend, 2);
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
