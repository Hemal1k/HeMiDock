using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace HeMiDock;

/// <summary>
/// The brushes of the theme for elements made in code. Application.Current.Resources gave the brush of the theme the app
/// started with – wrong in a window whose own theme is another (light / dark chosen in the settings), and a change of the
/// theme needed the window built again. Here every key has one brush of its own in the colour of the theme in use
/// (<see cref="Theme.IsLight"/>), shared by all elements; when the theme or the accent changes, <see cref="Refresh"/> gives
/// these brushes the new colours and every element follows at once.
/// </summary>
static class ThemeBrush
{
    static readonly Dictionary<string, Brush> Live = new();
    static bool _light = Theme.IsLight;

    public static Brush Get(string key)
    {
        if (Live.TryGetValue(key, out var known)) return known;
        var found = Find(key, _light);
        // a solid colour becomes an own brush (its colour is changed later); anything else is used as it is
        Brush brush = found is SolidColorBrush s ? new SolidColorBrush(s.Color) { Opacity = s.Opacity } : found as Brush;
        if (brush != null) Live[key] = brush;
        return brush;
    }

    /// <summary>
    /// The colour of the app as set in Settings → General (own or the accent of Windows), exactly – the accent brushes of
    /// WinUI keep the shades of the Windows accent they were made with.
    /// </summary>
    public static SolidColorBrush Accent { get; } = new(AccentNow());

    static global::Windows.UI.Color AccentNow()
    {
        int c = Theme.AccentColorRef;                                    // COLORREF: red in the low byte
        return global::Windows.UI.Color.FromArgb(255, (byte)c, (byte)(c >> 8), (byte)(c >> 16));
    }

    /// <summary>The theme (or the accent colour) changed: the brushes take the colours of the theme in use.</summary>
    public static void Refresh()
    {
        _light = Theme.IsLight;
        Accent.Color = AccentNow();
        foreach (var (key, brush) in Live)
            if (brush is SolidColorBrush live && Find(key, _light) is SolidColorBrush now)
            {
                live.Color = now.Color;
                live.Opacity = now.Opacity;
            }
    }

    /// <summary>The resource in the dictionary of the theme ("Light" or "Dark"), searched through the merged dictionaries.</summary>
    static object Find(string key, bool light)
    {
        string theme = light ? "Light" : "Dark";
        return Search(Application.Current.Resources, key, theme) ?? (Application.Current.Resources.TryGetValue(key, out var any) ? any : null);
    }

    static object Search(ResourceDictionary dictionary, string key, string theme)
    {
        foreach (var name in new[] { theme, theme == "Dark" ? "Default" : null })
            if (name != null && dictionary.ThemeDictionaries.TryGetValue(name, out var t) && t is ResourceDictionary themed && themed.TryGetValue(key, out var value))
                return value;
        foreach (var merged in dictionary.MergedDictionaries)
            if (Search(merged, key, theme) is { } value) return value;
        return null;
    }
}
