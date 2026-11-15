using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HeMiDock;

/// <summary>
/// A system control as a row of the search (in Start and in the quick search): the icon, the name and right there its
/// slider, switch or choice; an action only says Enter. The row stays a Button with the entry in its Tag, so the arrows
/// and the highlight of the results work as for the other rows. Enter switches a switch, moves a choice to the next item
/// and runs an action (Restart and Shut down after a second Enter).
/// </summary>
static class QuickControlView
{
    static Brush Res(string key) => ThemeBrush.Get(key);

    public static Button Build(StartEntry e, Action click)
    {
        var c = e.Control;
        var icon = new FontIcon { Glyph = c.Glyph, FontSize = 20, Width = 28, VerticalAlignment = VerticalAlignment.Center };
        var sub = new TextBlock { Text = e.Subtitle ?? "", FontSize = 12, Foreground = Res("TextFillColorSecondaryBrush"), Visibility = e.Subtitle == null ? Visibility.Collapsed : Visibility.Visible };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = e.Name, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis }, sub } };
        var grid = new Grid { ColumnSpacing = 12, MinHeight = 36 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        bool loading = true;
        FrameworkElement right = null;
        switch (c.Type)
        {
            case ControlType.Slider:
            {
                var value = new TextBlock { Width = 32, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("TextFillColorSecondaryBrush") };
                // the slider takes what the name leaves (a narrow Start cut the name to "Vol…")
                var slider = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 1, IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
                slider.ValueChanged += (_, a) =>
                {
                    value.Text = ((int)Math.Round(a.NewValue)).ToString();
                    if (!loading) _ = c.Set(a.NewValue);
                };
                var pair = new Grid { ColumnSpacing = 8 };
                pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });                                 // empty: pushes it right
                pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100, GridUnitType.Star), MinWidth = 80, MaxWidth = 220 });
                pair.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(slider, 1);
                pair.Children.Add(slider);
                Grid.SetColumn(value, 2);
                pair.Children.Add(value);
                right = pair;
                grid.ColumnDefinitions[1].Width = GridLength.Auto;             // the name whole, the slider in the rest
                grid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
                c.Widget = slider;
                break;
            }
            case ControlType.Toggle:
            {
                var toggle = new ToggleSwitch { OnContent = L.T("On"), OffContent = L.T("Off"), MinWidth = 0, IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
                toggle.Toggled += (_, _) => { if (!loading) _ = c.Set(toggle.IsOn ? 1 : 0); };
                right = toggle;
                c.Widget = toggle;
                break;
            }
            case ControlType.Choice:
            {
                var options = c.Options?.Invoke() ?? new List<string>();
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Tag = -1 };
                for (int i = 0; i < options.Count; i++)
                {
                    int index = i;
                    var b = new Button { Content = new TextBlock { Text = options[i], TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 }, Padding = new Thickness(10, 4, 10, 4), IsEnabled = false };
                    b.Click += (_, _) => Choose(c, row, index);
                    row.Children.Add(b);
                }
                right = row;
                c.Widget = row;
                break;
            }
            default:
                c.Widget = sub;                                      // the action: its line says what Enter does
                break;
        }
        if (right != null)
        {
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
        }

        var button = new Button
        {
            Style = (Style)Application.Current.Resources["DockButton"], HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 5, 10, 5), Tag = e, Content = grid
        };
        button.Click += (_, _) => click();

        // the value of now: asked when the row is shown (some take a moment – Wi-Fi, brightness)
        if (c.Type != ControlType.Action) Load();
        return button;

        async void Load()
        {
            double? v = null;
            try { v = await c.Get(); } catch (Exception ex) { Log.Write("Quick control " + c.Id, ex); }
            if (v is not double now)
            {
                sub.Text = L.T("Not available on this PC");
                sub.Visibility = Visibility.Visible;
                if (right != null) right.Visibility = Visibility.Collapsed;   // no switch that does nothing
                loading = false;
                return;
            }
            switch (c.Widget)
            {
                case Slider s: s.Value = now; s.IsEnabled = true; break;
                case ToggleSwitch t: t.IsOn = now > 0.5; t.IsEnabled = true; break;
                case StackPanel p: Mark(p, (int)now); foreach (var b in p.Children.OfType<Button>()) b.IsEnabled = true; break;
            }
            loading = false;
        }
    }

    static void Mark(StackPanel row, int index)
    {
        row.Tag = index;
        int i = 0;
        foreach (var b in row.Children.OfType<Button>())
        {
            bool on = i++ == index;
            b.Style = on ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;
        }
    }

    static void Choose(QuickControl c, StackPanel row, int index)
    {
        Mark(row, index);
        _ = c.Set(index);
    }

    /// <summary>Enter or a click on the row: true when the action should run now (the window closes first).</summary>
    public static bool Activate(StartEntry e)
    {
        var c = e.Control;
        switch (c.Type)
        {
            case ControlType.Toggle when c.Widget is ToggleSwitch t && t.IsEnabled:
                t.IsOn = !t.IsOn;
                return false;
            case ControlType.Choice when c.Widget is StackPanel row && row.Children.Count > 0 && row.Children[0].IsHitTestVisible && ((Button)row.Children[0]).IsEnabled:
                Choose(c, row, ((row.Tag as int? ?? -1) + 1) % row.Children.Count);
                return false;
            case ControlType.Action:
                if (c.Confirm && DateTime.Now - c.Armed > TimeSpan.FromSeconds(5))
                {
                    c.Armed = DateTime.Now;
                    if (c.Widget is TextBlock t)
                    {
                        t.Text = L.T("Press Enter again to confirm");
                        t.Foreground = Res("SystemFillColorCautionBrush");
                    }
                    return false;
                }
                return true;
            default:
                return false;
        }
    }
}
