using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace HeMiDock;

/// <summary>
/// The pictures of the logic (pixels) as images of WinUI, and the icons of apps loaded in the background. An icon comes from
/// Windows at 128 px; WinUI shrinks a picture without smoothing, which left bright specks along the edges of the icons – so it
/// is shrunk here first, to the pixels it is drawn with (halved by averaging, then the rest bilinear).
/// </summary>
static class Pictures
{
    static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The scaling of the main monitor (for pictures not yet on screen).</summary>
    static double Scale => Native.MonitorDpi(Monitors.Primary().Handle) ?? 1;

    /// <summary>Forgets the remembered icons (see <see cref="AppShell.ForgetIcons"/>).</summary>
    public static void Forget() => Cache.Clear();

    /// <summary>A picture, shrunk to <paramref name="sizePx"/> pixels when it is bigger (0 = as it is).</summary>
    public static ImageSource From(IconImage icon, int sizePx = 0)
    {
        if (icon == null || icon.Width <= 0) return null;
        if (sizePx > 0 && icon.Width > sizePx) icon = Shrink(icon, sizePx);
        var bmp = new WriteableBitmap(icon.Width, icon.Height);
        using (var stream = bmp.PixelBuffer.AsStream()) stream.Write(icon.Pixels, 0, icon.Pixels.Length);
        bmp.Invalidate();
        return bmp;
    }

    /// <summary>A picture drawn <paramref name="sizeDip"/> points big: its pixels on the main monitor.</summary>
    public static ImageSource ForSize(IconImage icon, double sizeDip) => From(icon, (int)Math.Ceiling(sizeDip * Scale));

    /// <summary>The icon of an app (a program, a shortcut or "shell:AppsFolder\…") or of a file (<paramref name="isFile"/>), remembered.</summary>
    public static async Task<ImageSource> OfAsync(string key, bool isFile = false, int sizePx = 0)
    {
        if (string.IsNullOrEmpty(key)) return null;
        string id = (isFile ? "f:" : "") + key + "|" + sizePx;
        if (Cache.TryGetValue(id, out var known)) return known;
        var icon = await Task.Run(() =>
        {
            var full = isFile ? AppShell.FileTypeIcon(key) : AppShell.Icon(key);
            return full != null && sizePx > 0 && full.Width > sizePx ? Shrink(full, sizePx) : full;
        });
        if (icon == null) return null;                                  // not remembered: asked again next time
        return Cache[id] = From(icon);
    }

    /// <summary>
    /// Loads the icon into an image without waiting for it, in the pixels the image is drawn with. Without an icon of its own
    /// (an app Windows gives none for): the icon of the program it runs, else <paramref name="missing"/> puts a symbol there – never an empty place.
    /// </summary>
    public static async void Into(Microsoft.UI.Xaml.Controls.Image image, string key, bool isFile = false, string program = null, Action missing = null)
    {
        try
        {
            double scale = image.XamlRoot?.RasterizationScale ?? Scale;
            int px = double.IsNaN(image.Width) || image.Width <= 0 ? 0 : (int)Math.Ceiling(image.Width * scale);
            image.Source = await OfAsync(key, isFile, px)
                ?? (program != null && !string.Equals(program, key, StringComparison.OrdinalIgnoreCase) ? await OfAsync(program, false, px) : null);
            if (image.Source == null) missing?.Invoke();
        }
        catch (Exception ex) { Log.Write("Icon " + key, ex); }
    }

    /// <summary>The picture without its transparent margin, kept square (a logo drawn small in the middle of its tile).</summary>
    public static IconImage Trim(IconImage src)
    {
        int w = src.Width, h = src.Height, left = w, top = h, right = -1, bottom = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (src.Pixels[(y * w + x) * 4 + 3] > 12)
                {
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
        if (right < 0) return src;
        int side = Math.Max(right - left + 1, bottom - top + 1);
        if (side >= w - 2) return src;
        int cx = (left + right + 1) / 2, cy = (top + bottom + 1) / 2;
        int x0 = Math.Clamp(cx - side / 2, 0, w - side), y0 = Math.Clamp(cy - side / 2, 0, h - side);
        side = Math.Min(side, Math.Min(w - x0, h - y0));
        var dst = new byte[side * side * 4];
        for (int y = 0; y < side; y++) Array.Copy(src.Pixels, ((y0 + y) * w + x0) * 4, dst, y * side * 4, side * 4);
        return new IconImage(dst, side, side);
    }

    /// <summary>Smaller, smoothly: halved by averaging 2 × 2 pixels while it is at least twice as big, then bilinear.</summary>
    static IconImage Shrink(IconImage src, int size)
    {
        var cur = src;
        while (cur.Width >= size * 2 && cur.Height >= 2) cur = Half(cur);
        if (cur.Width == size) return cur;
        int w = size, h = Math.Max(1, (int)Math.Round((double)cur.Height * size / cur.Width));
        var dst = new byte[w * h * 4];
        double sx = (double)cur.Width / w, sy = (double)cur.Height / h;
        for (int y = 0; y < h; y++)
        {
            double fy = Math.Max(0, (y + 0.5) * sy - 0.5);
            int y0 = Math.Min((int)fy, cur.Height - 1), y1 = Math.Min(y0 + 1, cur.Height - 1);
            double ty = fy - y0;
            for (int x = 0; x < w; x++)
            {
                double fx = Math.Max(0, (x + 0.5) * sx - 0.5);
                int x0 = Math.Min((int)fx, cur.Width - 1), x1 = Math.Min(x0 + 1, cur.Width - 1);
                double tx = fx - x0;
                for (int c = 0; c < 4; c++)                          // premultiplied: every channel the same way
                {
                    double a = cur.Pixels[(y0 * cur.Width + x0) * 4 + c] * (1 - tx) + cur.Pixels[(y0 * cur.Width + x1) * 4 + c] * tx;
                    double b = cur.Pixels[(y1 * cur.Width + x0) * 4 + c] * (1 - tx) + cur.Pixels[(y1 * cur.Width + x1) * 4 + c] * tx;
                    dst[(y * w + x) * 4 + c] = (byte)Math.Round(a * (1 - ty) + b * ty);
                }
            }
        }
        return new IconImage(dst, w, h);
    }

    static IconImage Half(IconImage src)
    {
        int w = src.Width / 2, h = Math.Max(1, src.Height / 2);
        var dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 4; c++)
                {
                    int i = ((y * 2) * src.Width + x * 2) * 4 + c, j = i + src.Width * 4;
                    dst[(y * w + x) * 4 + c] = (byte)((src.Pixels[i] + src.Pixels[i + 4] + src.Pixels[j] + src.Pixels[j + 4] + 2) / 4);
                }
        return new IconImage(dst, w, h);
    }
}
