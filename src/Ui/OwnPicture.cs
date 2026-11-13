using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace HeMiDock;

/// <summary>
/// Own pictures: of the Start button and of pinned icons. Images are copied into the settings folder (so they stay when the
/// original is moved or deleted), programs and shortcuts are kept by path (their icon is used).
/// </summary>
static class OwnPicture
{
    static readonly string[] IconFiles = { ".exe", ".lnk", ".dll", ".ico" };
    static readonly string[] Types = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico", ".exe", ".lnk" };

    /// <summary>Asks for a picture; the chosen file, null when cancelled.</summary>
    public static async Task<string> AskAsync(IntPtr owner)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        foreach (var t in Types) picker.FileTypeFilter.Add(t);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, owner);
        try { return (await picker.PickSingleFileAsync())?.Path; }
        catch (Exception ex) { Log.Write("Picture picker", ex); return null; }
    }

    /// <summary>The picture of the Start button: the old one is replaced.</summary>
    public static string ImportStart(string file)
    {
        string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
        if (IconFiles.Contains(ext) && ext != ".ico") return file;
        Directory.CreateDirectory(AppPaths.Folder);
        foreach (var old in Directory.GetFiles(AppPaths.Folder, "start-button.*")) try { File.Delete(old); } catch { }
        string target = AppPaths.Of("start-button" + ext);
        File.Copy(file, target, true);
        return target;
    }

    /// <summary>A picture of a dock icon: a copy with its own name in the Icons folder.</summary>
    public static string ImportIcon(string file)
    {
        string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
        if (IconFiles.Contains(ext) && ext != ".ico") return file;
        string dir = AppPaths.Of("Icons");
        Directory.CreateDirectory(dir);
        string target = System.IO.Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
        File.Copy(file, target, true);
        return target;
    }

    /// <summary>An image file, or the icon of a program / shortcut / .ico; null when it cannot be read.</summary>
    public static ImageSource Load(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return null;
        try
        {
            if (IconFiles.Contains(System.IO.Path.GetExtension(file).ToLowerInvariant())) return Pictures.ForSize(AppShell.Icon(file), 56);   // the largest place of an own picture (dock, Start)
            return new BitmapImage(new Uri(file)) { DecodePixelWidth = 160, CreateOptions = BitmapCreateOptions.IgnoreImageCache };
        }
        catch (Exception ex) { Log.Write("Picture " + file, ex); return null; }
    }

    internal const string LogoPath = "M15,0 H30 A15,15 0 0 1 45,15 V30 A15,15 0 0 1 30,45 H15 A15,15 0 0 1 0,30 V15 A15,15 0 0 1 15,0 Z M70,0 H85 A15,15 0 0 1 100,15 V30 A15,15 0 0 1 85,45 H70 A15,15 0 0 1 55,30 V15 A15,15 0 0 1 70,0 Z M15,55 H30 A15,15 0 0 1 45,70 V85 A15,15 0 0 1 30,100 H15 A15,15 0 0 1 0,85 V70 A15,15 0 0 1 15,55 Z M77.5,55 A22.5,22.5 0 1 1 77.49,55 Z";
    static readonly uint[] Dots = { 0xFFFF5F57, 0xFFFEBC2E, 0xFF28C840, 0xFF4CA3FF, 0xFFAF7BFF, 0xFFFF7AB6, 0xFF36D1C4, 0xFFFF9F1A, 0xFF8E8E93 };

    /// <summary>
    /// The Start button: 0 = the logo (blue gradient), 1 = the logo in one colour, 2 = the app grid
    /// (3 × 3 coloured dots), 3 = the grid in one colour, 4 = the own picture (the logo while there is none).
    /// </summary>
    public static FrameworkElement StartIcon(int look, string picture, double size)
    {
        if (look == 4 && Load(picture) is { } own) return new Image { Source = own, Width = size, Height = size };
        if (look == 4) look = 0;
        Brush mono = DockSettings.Current.AccentMode == 1
            ? ThemeBrush.Get("AccentFillColorDefaultBrush")
            : ThemeBrush.Get("TextFillColorPrimaryBrush");
        if (look is 0 or 1)
        {
            var path = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), LogoPath),
                Fill = look == 1 ? mono : new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1),
                    GradientStops = { new GradientStop { Color = Color(0xFF60CDFF), Offset = 0 }, new GradientStop { Color = Color(0xFF0078D4), Offset = 1 } }
                }
            };
            return new Viewbox { Width = size * 0.86, Height = size * 0.86, Child = path };
        }
        var canvas = new Canvas { Width = 22, Height = 22 };
        for (int i = 0; i < 9; i++)
        {
            var dot = new Ellipse { Width = 5.5, Height = 5.5, Fill = look == 3 ? mono : new SolidColorBrush(Color(Dots[i])) };
            Canvas.SetLeft(dot, i % 3 * 8.25);
            Canvas.SetTop(dot, i / 3 * 8.25);
            canvas.Children.Add(dot);
        }
        return new Viewbox { Width = size * 0.72, Height = size * 0.72, Child = canvas };
    }

    static global::Windows.UI.Color Color(uint argb) => global::Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
