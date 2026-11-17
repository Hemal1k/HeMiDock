using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NAudio.CoreAudioApi;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;

namespace HeMiDock;

sealed partial class ControlCenterWindow
{
    // ───────────────────────── pages ─────────────────────────

    /// <summary>
    /// A page in place of the main one, in the same window: it keeps at least the height of the main page (the window does
    /// not jump as if another one opened) and slides in from the side.
    /// </summary>
    void ShowPage(string id, string title)
    {
        bool fromMain = _openPage == null && _main.Visibility == Visibility.Visible;
        if (fromMain && _main.ActualHeight > 0) _pageView.MinHeight = _main.ActualHeight;
        _openPage = id;
        _pageTitle.Text = L.T(title);
        _page.Children.Clear();
        _pageActions.Children.Clear();
        _pageFooter.Content = null;
        _main.Visibility = Visibility.Collapsed;
        _pageView.Visibility = Visibility.Visible;
        if (fromMain) Motion.Slide(_pageView, forward: true);
    }

    void ShowMain()
    {
        bool fromPage = _openPage != null;
        _openPage = null;
        _apps.Clear();
        _pageView.Visibility = Visibility.Collapsed;
        _pageView.MinHeight = 0;
        _main.Visibility = Visibility.Visible;
        BuildMain();
        if (fromPage && _visibleNow) Motion.Slide(_main, forward: false);
    }

    bool _visibleNow;
    /// <summary>
    /// Takes the focus when the pop-up opens: otherwise WinUI gives it to the first tile, and after Win+A (a key) it showed
    /// the keyboard frame around it though nobody pressed Tab. Tab still goes on to the first tile.
    /// </summary>
    readonly ContentControl _focusSink = new() { IsTabStop = true, UseSystemFocusVisuals = false, Width = 0, Height = 0, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };

    protected override void Shown()
    {
        _visibleNow = true;
        _focusSink.Focus(FocusState.Programmatic);
    }

    void Footer(string text, string uri) => _pageFooter.Content = SettingsLink(text, uri);

    StackPanel PageCard(string title = null)
    {
        if (title != null) _page.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(4, _page.Children.Count == 0 ? 0 : 6, 0, 0) });
        var rows = new StackPanel { Spacing = 1 };
        _page.Children.Add(Card(rows, 4));
        return rows;
    }

    /// <summary>A row of a card: the chosen one has an accent symbol and a check mark.</summary>
    static Button PageRow(string glyph, string text, bool on, Action click, string sub = null, string backGlyph = null)
    {
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = L.T(text), FontSize = 12.5, FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
        if (sub != null) texts.Children.Add(new TextBlock { Text = L.T(sub), FontSize = 11, Foreground = Res("TextFillColorSecondaryBrush") });
        var grid = new Grid { ColumnSpacing = 10, ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(26) }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        // the signal of a network: the whole fan faint under the bars it has (one bar alone was just a dot)
        if (backGlyph != null) grid.Children.Add(new FontIcon { Glyph = backGlyph, FontSize = 15, Opacity = 0.3, Foreground = Res("TextFillColorPrimaryBrush") });
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 15, Foreground = on ? Res("AccentTextFillColorPrimaryBrush") : Res("TextFillColorPrimaryBrush") });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        if (on)
        {
            var check = new FontIcon { Glyph = "", FontSize = 12, Foreground = Res("AccentTextFillColorPrimaryBrush") };
            Grid.SetColumn(check, 2);
            grid.Children.Add(check);
        }
        var b = new Button { Style = DockButton, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 7, 8, 7), Content = grid };
        if (click != null) b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A turning ring with what is being looked for, while Windows searches.</summary>
    void Searching(string text) => _page.Children.Add(new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(6, 6, 6, 10),
        Children =
        {
            new ProgressRing { IsActive = true, Width = 18, Height = 18 },
            new TextBlock { Text = L.T(text), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("TextFillColorSecondaryBrush") }
        }
    });

    void Note(string text) => _page.Children.Add(new TextBlock { Text = L.T(text), Margin = new Thickness(6, 0, 6, 6), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextFillColorSecondaryBrush") });

    ToggleSwitch HeaderSwitch(bool on, Action<bool> set)
    {
        // only the switch itself: the control has room for its text after it, which pushed it out of the window (cut off)
        var s = new ToggleSwitch { IsOn = on, OnContent = null, OffContent = null, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        s.Toggled += (_, _) => set(s.IsOn);
        _pageActions.Children.Add(new Grid { Width = 42, VerticalAlignment = VerticalAlignment.Center, Children = { s } });
        return s;
    }
}
