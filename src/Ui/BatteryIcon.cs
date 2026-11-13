using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HeMiDock;

/// <summary>
/// The battery of the status icons, drawn like the one of Windows 11: a rounded body filled by the charge and its little cap;
/// the charge green while it is plugged in, red below 20 % (no sign inside it: the bolt over the charge was hard to read).
/// </summary>
public sealed partial class BatteryIcon : Grid
{
    readonly Border _body = new() { CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1.2) };
    readonly Border _fill = new() { CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left };
    readonly Border _cap = new() { Width = 2, CornerRadius = new CornerRadius(0, 1, 1, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly FontIcon _probe = new() { Visibility = Visibility.Collapsed };   // the colour of the text around it (of its own theme)
    double _size = 15;
    int _percent = 100;
    bool _plugged;

    public BatteryIcon()
    {
        VerticalAlignment = VerticalAlignment.Center;
        ColumnSpacing = 0.5;
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _body.Child = _fill;
        Children.Add(_body);
        SetColumn(_cap, 1);
        Children.Add(_cap);
        Children.Add(_probe);
        ActualThemeChanged += (_, _) => Colors();
        Loaded += (_, _) => Colors();
        Apply();
    }

    /// <summary>The height of the icon in points (as the font size of the other status icons).</summary>
    public double Size { get => _size; set { _size = value; Apply(); } }

    public void Show(int percent, bool plugged)
    {
        _percent = Math.Clamp(percent, 0, 100);
        _plugged = plugged;
        Apply();
    }

    void Apply()
    {
        double h = Math.Round(_size * 0.72), w = Math.Round(_size * 1.5);
        _body.Width = w;
        _body.Height = h;
        _body.Padding = new Thickness(1.5);
        _cap.Height = Math.Round(h * 0.42);
        double inner = w - 2 * 1.2 - 2 * 1.5;
        _fill.Width = Math.Max(_percent > 0 ? 1.5 : 0, inner * _percent / 100.0);
        Colors();
    }

    // the green of Windows' charging battery, a little lighter on a dark background
    static readonly global::Windows.UI.Color GreenDark = global::Windows.UI.Color.FromArgb(255, 0x6C, 0xCB, 0x5F), GreenLight = global::Windows.UI.Color.FromArgb(255, 0x0F, 0x7B, 0x0F);

    void Colors()
    {
        var text = _probe.Foreground ?? ThemeBrush.Get("TextFillColorPrimaryBrush");
        _body.BorderBrush = _cap.Background = text;
        _cap.Opacity = 0.8;
        _fill.Background = _plugged ? new SolidColorBrush(ActualTheme == ElementTheme.Light ? GreenLight : GreenDark)
            : _percent < 20 ? ThemeBrush.Get("SystemFillColorCriticalBrush") : text;
    }
}
