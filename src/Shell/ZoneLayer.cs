using System.Runtime.InteropServices;

namespace HeMiDock;

/// <summary>
/// The zones of a monitor over everything while a window is dragged into them (the one under the mouse stronger): a
/// layered window of Windows that is click-through and never active, its picture drawn here pixel by pixel – no framework
/// (a window of WinUI cannot be see-through).
/// </summary>
static class ZoneLayer
{
    const int WS_POPUP = unchecked((int)0x80000000), WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;

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
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pos, ref SIZE size, IntPtr src, ref POINT srcPos, int key, ref BLEND blend, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

    static readonly WndProc Proc = DefWindowProc;                   // kept: the window calls it for as long as it lives
    static IntPtr _window;
    static string _shown;

    /// <summary>The zones (shares of the work area) on a monitor; <paramref name="accent"/> as 0xAARRGGBB.</summary>
    public static void Show(MonInfo m, Rect[] zones, int hot, uint accent)
    {
        try
        {
            string key = $"{m.Work.Left},{m.Work.Top},{m.Work.Right},{m.Work.Bottom},{hot},{zones.Length},{accent}";
            if (key == _shown) return;
            _shown = key;
            if (!Make()) return;
            int w = m.Work.Right - m.Work.Left, h = m.Work.Bottom - m.Work.Top;
            double scale = Native.MonitorDpi(m.Handle) ?? 1;
            Draw(w, h, zones, hot, accent, scale, new POINT { X = m.Work.Left, Y = m.Work.Top });
            ShowWindow(_window, 4);                                    // SW_SHOWNOACTIVATE
        }
        catch (Exception ex) { Log.Write("Zones", ex); }
    }

    public static void Hide()
    {
        if (_window == IntPtr.Zero || _shown == null) return;
        _shown = null;
        ShowWindow(_window, 0);
    }

    static bool Make()
    {
        if (_window != IntPtr.Zero) return true;
        var inst = GetModuleHandle(null);
        var wc = new WNDCLASS { proc = Proc, inst = inst, name = "HeMiDock.Zones" };
        RegisterClass(ref wc);
        _window = CreateWindowEx(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST, "HeMiDock.Zones", "HeMiDock zones", WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        return _window != IntPtr.Zero;
    }

    /// <summary>The picture: each zone a rounded field in the accent (stronger under the mouse) with a line around it.</summary>
    static void Draw(int w, int h, Rect[] zones, int hot, uint accent, double scale, POINT at)
    {
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFOHEADER { Size = Marshal.SizeOf<BITMAPINFOHEADER>(), Width = w, Height = -h, Planes = 1, Bits = 32 };
        var bitmap = CreateDIBSection(dc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(dc, bitmap);
        try
        {
            var pixels = new int[w * h];                                // premultiplied BGRA, transparent around the zones
            byte r = (byte)(accent >> 16), g = (byte)(accent >> 8), b = (byte)accent;
            int Premul(byte alpha) => alpha << 24 | r * alpha / 255 << 16 | g * alpha / 255 << 8 | b * alpha / 255;
            int line = Premul(0x90), radius = (int)Math.Round(8 * scale), margin = (int)Math.Round(6 * scale), thick = Math.Max(1, (int)Math.Round(1.5 * scale));
            for (int i = 0; i < zones.Length; i++)
            {
                var z = zones[i];
                int fill = Premul((byte)(i == hot ? 0x48 : 0x12));                  // soft: a hint of the accent, the zone under the mouse a little more
                int x0 = (int)(z.X * w) + margin, y0 = (int)(z.Y * h) + margin, x1 = (int)((z.X + z.Width) * w) - margin, y1 = (int)((z.Y + z.Height) * h) - margin;
                for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y++)
                    for (int x = Math.Max(0, x0); x < Math.Min(w, x1); x++)
                    {
                        // the distance into the rounded corner (0 outside the corners)
                        int cx = x < x0 + radius ? x0 + radius - x : x >= x1 - radius ? x - (x1 - radius - 1) : 0;
                        int cy = y < y0 + radius ? y0 + radius - y : y >= y1 - radius ? y - (y1 - radius - 1) : 0;
                        double d = cx > 0 && cy > 0 ? Math.Sqrt(cx * cx + cy * cy) : Math.Max(cx, cy);
                        if (d > radius) continue;
                        bool edge = d > radius - thick || x < x0 + thick || x >= x1 - thick || y < y0 + thick || y >= y1 - thick;
                        pixels[y * w + x] = edge ? line : fill;
                    }
            }
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var size = new SIZE { W = w, H = h };
            var src = new POINT();
            var blend = new BLEND { Op = 0, Alpha = 255, Format = 1 };     // AC_SRC_OVER, AC_SRC_ALPHA
            UpdateLayeredWindow(_window, screen, ref at, ref size, dc, ref src, 0, ref blend, 2);   // ULW_ALPHA
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
