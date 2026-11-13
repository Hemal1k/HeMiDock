using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HeMiDock;

/// <summary>
/// Light / dark and the colour of the app: by Windows or by the settings (Settings → General → Colors), applied to every
/// window of the app at once when Windows or the settings change – without a restart. Also the transparency: with lighter
/// drawing or transparency switched off the windows get a solid background of the theme instead of the acrylic / Mica.
/// </summary>
static class ThemeWatch
{
    static readonly global::Windows.UI.ViewManagement.UISettings Settings = new();   // kept: its event lives only as long as the object
    static readonly List<WeakReference<Window>> Tracked = new();
    static (int Mode, int Accent, string Color, bool Glass) _shown;

    /// <summary>What a window had before it was made solid (to give it back).</summary>
    sealed class Original { public SystemBackdrop Backdrop; public Brush Background; }
    static readonly ConditionalWeakTable<Window, Original> Originals = new();

    public static void Start()
    {
        Settings.ColorValuesChanged += (_, _) => Ui.Post(Refresh);
        DockSettings.Current.PropertyChanged += (_, _) =>
        {
            var s = DockSettings.Current;
            if ((s.ThemeMode, s.AccentMode, s.AccentColor, s.Blur) != _shown) Refresh();
        };
        Refresh();
    }

    /// <summary>A window of the app follows the theme from now on.</summary>
    public static void Track(Window window)
    {
        if (!Tracked.Any(w => w.TryGetTarget(out var known) && known == window)) Tracked.Add(new WeakReference<Window>(window));
        Apply(window);
    }

    static void Refresh()
    {
        var s = DockSettings.Current;
        Theme.Refresh();
        _shown = (s.ThemeMode, s.AccentMode, s.AccentColor, s.Blur);
        int c = Theme.AccentColorRef;                                  // own colour or the accent of Windows (COLORREF)
        var accent = global::Windows.UI.Color.FromArgb(255, (byte)c, (byte)(c >> 8), (byte)(c >> 16));
        if (s.AccentMode == 1) Application.Current.Resources["SystemAccentColor"] = accent;
        ThemeBrush.Refresh();                                            // the colours of the elements made in code follow at once
        Tracked.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var w in Tracked) if (w.TryGetTarget(out var window)) Apply(window);
    }

    static void Apply(Window window)
    {
        if (window.Content is FrameworkElement root)
            // always the theme in use (also "automatic": the one of Windows now), the same the brushes of ThemeBrush have
            root.RequestedTheme = Theme.IsLight ? ElementTheme.Light : ElementTheme.Dark;
        (window.SystemBackdrop as AlwaysActiveAcrylic)?.Follow();       // the acrylic behind it in the same light / dark
        // the title bar and the frame of Windows in the same light / dark as the content (otherwise a light frame on a dark window)
        try { Native.SetDwmAttr(WinRT.Interop.WindowNative.GetWindowHandle(window), Native.DWMWA_USE_IMMERSIVE_DARK_MODE, Theme.IsLight ? 0 : 1); }
        catch { }
        ApplyGlass(window);
    }

    /// <summary>
    /// The acrylic of every window made again. Started at sign-in by its task, the dock comes before Explorer: the acrylic of
    /// Windows was not ready then, fell back to a plain colour and stayed so – once Explorer is up it is made anew.
    /// </summary>
    public static void RenewGlass()
    {
        if (!DockSettings.Current.Blur) return;
        Tracked.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var w in Tracked)
            if (w.TryGetTarget(out var window) && window.SystemBackdrop is AlwaysActiveAcrylic)
                try { window.SystemBackdrop = new AlwaysActiveAcrylic(); }
                catch (Exception ex) { Log.Write("Renew acrylic", ex); }
    }

    /// <summary>No transparency (lighter drawing, transparency off): the backdrop of the window goes, a solid background comes.</summary>
    static void ApplyGlass(Window window)
    {
        Brush Get() => window.Content switch { Panel p => p.Background, Border b => b.Background, _ => null };
        void Set(Brush brush) { if (window.Content is Panel p) p.Background = brush; else if (window.Content is Border b) b.Background = brush; }
        if (window.Content is not (Panel or Border)) return;
        bool solid = !DockSettings.Current.Blur;
        var original = Originals.GetValue(window, _ => new Original { Backdrop = window.SystemBackdrop, Background = Get() });
        if (solid)
        {
            if (window.SystemBackdrop != null) window.SystemBackdrop = null;
            Set(ThemeBrush.Get("SolidBackgroundFillColorBaseBrush"));
        }
        else
        {
            if (window.SystemBackdrop == null && original.Backdrop != null) window.SystemBackdrop = original.Backdrop is MicaBackdrop ? new MicaBackdrop() : new AlwaysActiveAcrylic();
            Set(original.Background);
        }
    }
}
