using System.Runtime.InteropServices;

namespace HeMiDock;

/// <summary>
/// The icon of any file, shortcut or app as Windows shows it, as BGRA pixels (premultiplied, top row first) – the app
/// turns them into a picture of its own UI framework, so this stays without any user interface.
/// </summary>
static class ShellIcons
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap); }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bmp);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);

    const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    /// <summary>The icon at <paramref name="size"/> pixels (or the next bigger one); null when Windows has none.</summary>
    public static (byte[] Pixels, int Width, int Height)? Get(string path, int size)
    {
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbmp) != 0) return null;
            GetObject(hbmp, Marshal.SizeOf<BITMAP>(), out var bmp);
            var info = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = bmp.bmWidth, biHeight = -bmp.bmHeight, biPlanes = 1, biBitCount = 32 };
            var pixels = new byte[bmp.bmWidth * bmp.bmHeight * 4];
            IntPtr dc = GetDC(IntPtr.Zero);
            try { if (GetDIBits(dc, hbmp, 0, (uint)bmp.bmHeight, pixels, ref info, 0) == 0) return null; }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            return (pixels, bmp.bmWidth, bmp.bmHeight);
        }
        catch (Exception ex) { Log.Write("Icon " + path, ex); return null; }
        finally { if (hbmp != IntPtr.Zero) DeleteObject(hbmp); }
    }
}
