using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace HeMiDock;

/// <summary>
/// Quick search in the middle of the screen (like Spotlight on a Mac), opened by a keyboard shortcut or the search button of
/// the top bar. It searches the same as Start: apps, calculator, settings, files … Enter opens the selected result (the
/// arrows move it), Esc clears the text or closes it.
/// </summary>
sealed class SpotlightWindow : PopupWindow
{
    readonly TextBox _box = new() { PlaceholderText = L.T("Search"), FontSize = 20, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _results = new() { Spacing = 2 };
    readonly ScrollViewer _scroll;
    readonly Border _line = new() { Height = 1, Visibility = Visibility.Collapsed };
    CancellationTokenSource _files;
    int _selected;

    SpotlightWindow() : base("spotlight")
    {
        var glass = new FontIcon { Glyph = "", FontSize = 18, Margin = new Thickness(18, 0, 10, 0), Foreground = ThemeBrush.Get("TextFillColorSecondaryBrush") };
        var row = new Grid { Height = 56 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_box, 1);
        _box.Margin = new Thickness(0, 0, 16, 0);
        row.Children.Add(glass);
        row.Children.Add(_box);
        _line.Background = ThemeBrush.Get("DividerStrokeColorDefaultBrush");
        _scroll = new ScrollViewer { Content = _results, MaxHeight = 440, Margin = new Thickness(8, 8, 8, 8), Visibility = Visibility.Collapsed, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Content = new StackPanel { Width = 680, Children = { row, _line, _scroll } };
        _box.TextChanged += (_, _) => Search();
        _box.KeyDown += Box_KeyDown;
    }

    /// <summary>Opens / closes the quick search on the monitor of the active window (kept between openings, like Start).</summary>
    public static void Toggle() => PopupWindow.Toggle("spotlight", () => new SpotlightWindow());

    /// <summary>Made ahead while the app is idle (see <see cref="PopupWindow.Prepare"/>).</summary>
    public static void Prepare() => Prepare("spotlight", () => new SpotlightWindow());

    protected override void Shown() => _box.Focus(FocusState.Programmatic);

    protected override void Reopen()
    {
        _files?.Cancel();
        _box.Text = "";
    }

    protected override bool OnEscape()
    {
        if (_box.Text.Length == 0) return false;
        _box.Text = "";
        return true;
    }

    /// <summary>In the upper third of the monitor of the active window, in its middle.</summary>
    protected override void Place()
    {
        var size = ContentSize;
        if (size.Width <= 0) return;
        double s = Scale;
        int w = (int)Math.Ceiling(size.Width * s), h = (int)Math.Ceiling(size.Height * s);
        var handle = Native.MonitorFromWindow(Native.GetForegroundWindow(), 2);
        var mon = Monitors.All().FirstOrDefault(m => m.Handle == handle) ?? Monitors.Primary();
        var a = mon.Work;
        MoveTo(new Windows.Graphics.RectInt32((a.Left + a.Right - w) / 2, a.Top + (a.Bottom - a.Top) / 4, w, h));
    }

    async void Search()
    {
        string q = _box.Text;
        _files?.Cancel();
        if (q.Trim().Length == 0) { Show(new()); return; }
        var local = SearchEngine.Local(q);
        Show(local);
        var cts = _files = new CancellationTokenSource();
        try
        {
            await Task.Delay(220, cts.Token);
            var files = await SearchEngine.FilesAsync(q, cts.Token);
            if (!cts.IsCancellationRequested && files.Count > 0) Show(local.Concat(files).ToList());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write("Spotlight files", ex); }
    }

    void Show(List<StartEntry> results)
    {
        _selected = 0;
        _results.Children.Clear();
        bool any = _box.Text.Trim().Length > 0;
        _scroll.Visibility = _line.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        string group = null;
        foreach (var r in results)
        {
            if (r.Group != group) { group = r.Group; if (group != null) _results.Children.Add(new TextBlock { Text = group, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(8, 6, 0, 2) }); }
            _results.Children.Add(Row(r));
        }
        if (any && results.Count == 0) _results.Children.Add(new TextBlock { Text = L.T("No results"), Margin = new Thickness(8), Foreground = ThemeBrush.Get("TextFillColorSecondaryBrush") });
        Highlight();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Place);   // the height follows the results
    }

    FrameworkElement Row(StartEntry e)
    {
        FrameworkElement icon;
        if (e.IconKey != null)
        {
            var image = new Image { Width = 28, Height = 28 };
            Pictures.Into(image, e.IconKey, e.IconIsFile);
            icon = image;
        }
        else icon = new FontIcon { Glyph = e.Glyph ?? "", FontSize = 20, Width = 28 };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = e.Name, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(e.Subtitle)) texts.Children.Add(new TextBlock { Text = e.Subtitle, FontSize = 12, Foreground = ThemeBrush.Get("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["DockButton"], HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 7, 10, 7), Tag = e, Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { icon, texts } }
        };
        button.Click += (_, _) => Run(e);
        return button;
    }

    void Highlight()
    {
        var rows = _results.Children.OfType<Button>().ToList();
        for (int i = 0; i < rows.Count; i++)
            rows[i].Background = i == _selected ? ThemeBrush.Get("SubtleFillColorSecondaryBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        if (_selected < rows.Count) rows[_selected].StartBringIntoView();
    }

    void Box_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        int count = _results.Children.OfType<Button>().Count();
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Down when count > 0: _selected = (_selected + 1) % count; Highlight(); e.Handled = true; break;
            case Windows.System.VirtualKey.Up when count > 0: _selected = (_selected - 1 + count) % count; Highlight(); e.Handled = true; break;
            case Windows.System.VirtualKey.Enter:
                if (_results.Children.OfType<Button>().ElementAtOrDefault(_selected)?.Tag is StartEntry entry) { e.Handled = true; Run(entry); }
                break;
        }
    }

    void Run(StartEntry e)
    {
        CloseNow();
        e.Run(false);
    }
}
