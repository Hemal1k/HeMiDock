using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace HeMiDock;

/// <summary>
/// The settings in the style of Windows 11 Settings: pages on the left, cards on the right, a search over all of them. The
/// pages are (<see cref="BuildPages"/> is taken over from it); the building blocks below make
/// WinUI controls bound to <see cref="DockSettings"/>, so every change applies and is saved at once.
/// </summary>
sealed partial class SettingsWindow : Window
{
    static SettingsWindow _open;

    /// <summary>The pages fill the window, only on a very wide one they stop growing (long lines are hard to read).</summary>
    const double PageWidth = 1500;

    readonly List<(UIElement Element, Func<bool> Enabled)> _dependent = new();
    readonly List<(string Title, StackPanel Page)> _pages = new();
    readonly Dictionary<string, int> _pageIds = new();
    readonly List<(string Title, string Description, string Section, Panel Parent, Border Card)> _index = new();
    readonly Dictionary<Panel, (int Parent, string Title)> _subPages = new();
    readonly List<Action> _refresh = new();
    string _section;
    bool _noIndex;

    readonly ListView _nav = new() { SelectionMode = ListViewSelectionMode.Single, Width = 270 - 24 };
    readonly TextBox _search = new() { PlaceholderText = L.T("Search"), Margin = new Thickness(12, 8, 12, 8) };
    readonly ScrollViewer _host = new() { Padding = new Thickness(28, 0, 28, 24) };
    readonly TextBlock _title = new() { FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(28, 18, 28, 14) };
    readonly Button _back = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(28, 18, 0, 14), Content = new FontIcon { Glyph = "", FontSize = 14 } };
    Button _recording;
    readonly List<Action> _hotkeyRows = new();
    StackPanel _quickPage;
    string _quickShown;
    int _language = DockSettings.Current.Language;

    public static void Open(string page = null)
    {
        if (!App.IsReady) { App.WhenReady(() => Open(page)); return; }   // right after the start: once the dock is drawn
        if (_open == null)
        {
            _open = new SettingsWindow();
            _open.Closed += (_, _) => _open = null;
        }
        _open.Activate();
        if (page != null) _open.OpenPage(page);
    }

    SettingsWindow()
    {
        Title = L.T("HeMiDock settings");
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = false;
        // the same size on every monitor: 1035 × 725 points times the scaling of the monitor under the mouse (where it was
        // opened from), at most 90 % of its work area, in its middle
        Native.GetCursorPos(out var cursor);
        var mon = Monitors.All().FirstOrDefault(m => Fg.Contains(m.Bounds, cursor.X, cursor.Y)) ?? Monitors.Primary();
        double scale = Native.MonitorDpi(mon.Handle) ?? 1;
        var work = mon.Work;
        int w = Math.Min((int)(1035 * scale), (work.Right - work.Left) * 9 / 10), h = Math.Min((int)(725 * scale), (work.Bottom - work.Top) * 9 / 10);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(work.Left + (work.Right - work.Left - w) / 2, work.Top + (work.Bottom - work.Top - h) / 2, w, h));
        try { AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "HeMiDock.ico")); } catch { }   // the icon of the app in the title bar
        // the left column: the search, the pages and the buttons under them on the same edges
        _search.Margin = new Thickness(16, 8, 16, 8);
        _nav.Margin = new Thickness(12, 0, 12, 0);

        var left = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition(), new RowDefinition { Height = GridLength.Auto } } };
        left.Children.Add(_search);
        Grid.SetRow(_nav, 1);
        left.Children.Add(_nav);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(16, 12, 16, 16) };
        var file = new Button { Width = 44, Padding = new Thickness(0), Content = new FontIcon { Glyph = "\uE8CB", FontSize = 15 } };   // arrows up / down: export and import
        ToolTipService.SetToolTip(file, L.T("Settings file"));
        file.Flyout = FileMenu();
        var reset = new Button { Content = L.T("Reset to defaults") };
        reset.Click += (_, _) => DockSettings.Current.ResetToDefaults();
        footer.Children.Add(file);
        footer.Children.Add(reset);
        _reset = reset;
        _footer = footer;
        Grid.SetRow(footer, 2);
        left.Children.Add(footer);

        var head = new StackPanel { Orientation = Orientation.Horizontal, Children = { _back, _title } };
        var right = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() } };
        right.Children.Add(head);
        Grid.SetRow(_host, 1);
        right.Children.Add(_host);
        var root = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() } };
        // rounder buttons, lists, fields and menus than the default of WinUI
        root.Resources["ControlCornerRadius"] = new CornerRadius(8);
        root.Resources["OverlayCornerRadius"] = new CornerRadius(10);
        // one look for all buttons of the settings: rounded, the same height and padding
        foreach (var (type, basedOn) in new[] { (typeof(Button), "DefaultButtonStyle"), (typeof(ToggleButton), "DefaultToggleButtonStyle") })
            root.Resources[type] = new Style(type)
            {
                BasedOn = Application.Current.Resources.TryGetValue(basedOn, out var b) ? b as Style : null,
                Setters =
                {
                    new Setter(Control.CornerRadiusProperty, new CornerRadius(8)),
                    new Setter(FrameworkElement.MinHeightProperty, 34.0),
                    new Setter(Control.PaddingProperty, new Thickness(14, 5, 14, 6))
                }
            };
        root.Resources[typeof(ComboBox)] = new Style(typeof(ComboBox))
        {
            BasedOn = Application.Current.Resources.TryGetValue("DefaultComboBoxStyle", out var cb) ? cb as Style : null,
            Setters = { new Setter(Control.CornerRadiusProperty, new CornerRadius(8)), new Setter(FrameworkElement.MinHeightProperty, 34.0) }
        };
        root.Children.Add(left);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        Content = root;
        root.SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);   // responsive: narrow windows get a narrow column

        _back.Click += (_, _) => ShowPage(_nav.SelectedIndex);
        _nav.SelectionChanged += (_, _) => { if (_search.Text.Length > 0) _search.Text = ""; ShowPage(_nav.SelectedIndex); };
        _search.TextChanged += (_, _) => Search();
        BuildPages();
        _nav.SelectedIndex = 0;
        ThemeWatch.Track(this);
        UpdateDependent();
        DockSettings.Current.PropertyChanged += OnSettingsChanged;
        Closed += (_, _) => { DockSettings.Current.PropertyChanged -= OnSettingsChanged; StopRecording(); DockSettings.Current.Save(); };
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) StopRecording(); };
    }

    Button _reset;
    StackPanel _footer;
    bool? _compact;

    /// <summary>
    /// The layout by the width of the window (points): below 760 the pages on the left only as icons (their names in the
    /// tooltips) and narrower edges of the content, otherwise the full column.
    /// </summary>
    void ApplyWidth(double width)
    {
        bool compact = width < 760;
        if (compact == _compact) return;
        _compact = compact;
        _nav.Width = compact ? 64 : 270 - 24;
        _nav.Margin = compact ? new Thickness(4, 8, 4, 0) : new Thickness(12, 0, 12, 0);
        _search.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        _reset.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        _footer.Margin = compact ? new Thickness(12) : new Thickness(16, 12, 16, 16);
        foreach (var item in _nav.Items.OfType<StackPanel>())
        {
            foreach (var text in item.Children.OfType<TextBlock>()) text.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ToolTipService.SetToolTip(item, compact ? item.Children.OfType<TextBlock>().FirstOrDefault()?.Text : null);
        }
        double edge = compact ? 16 : 28;
        _host.Padding = new Thickness(edge, 0, edge, 24);
        _title.Margin = new Thickness(edge, 18, edge, 14);
        _title.FontSize = compact ? 22 : 28;
    }

    void OpenPage(string id)
    {
        if (!_pageIds.TryGetValue(id, out int page)) return;
        _search.Text = "";
        if (_nav.SelectedIndex != page) _nav.SelectedIndex = page; else ShowPage(page);
    }

    // ───────────────────────── pages ─────────────────────────

    void ShowPage(int i)
    {
        if (i < 0 || i >= _pages.Count) return;
        _back.Visibility = Visibility.Collapsed;
        _title.Text = _pages[i].Title;
        _host.Content = _pages[i].Page;
        _host.ChangeView(null, 0, null, true);
    }

    void ShowSubPage(int parent, string title, StackPanel page)
    {
        _back.Visibility = Visibility.Visible;
        _title.Text = _pages[parent].Title + " › " + L.T(title);
        _host.Content = page;
        _host.ChangeView(null, 0, null, true);
    }

    /// <summary>
    /// "dock": a screen with the dock along its bottom edge (the symbol of a grid looked like the Start logo next to it);
    /// "topbar": the same screen with a bar along its top.
    /// </summary>
    static FrameworkElement DockPicture(bool top = false)
    {
        var brush = ThemeBrush.Get("TextFillColorPrimaryBrush");
        var screen = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 15, Height = 12.5, RadiusX = 2.2, RadiusY = 2.2, Stroke = brush, StrokeThickness = 1.2 };
        var bar = top
            ? new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 11, Height = 1.8, RadiusX = 0.9, RadiusY = 0.9, Fill = brush }
            : new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 9, Height = 2.6, RadiusX = 1.3, RadiusY = 1.3, Fill = brush };
        Canvas.SetLeft(screen, 0.5); Canvas.SetTop(screen, 1.75);
        Canvas.SetLeft(bar, top ? 2.5 : 3.5); Canvas.SetTop(bar, top ? 4 : 10);
        return new Viewbox { Width = 16, Height = 16, Child = new Canvas { Width = 16, Height = 16, Children = { screen, bar } } };
    }

    StackPanel NewPage(string title, string glyph, string id = null)
    {
        if (id != null) _pageIds[id] = _pages.Count;
        title = L.T(title);
        var page = new StackPanel { MaxWidth = PageWidth, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Stretch };
        _pages.Add((title, page));
        _section = null;
        // "start": the logo of the Start button of the dock (the symbol of a menu did not look like Start)
        FrameworkElement icon = id == "start"
            ? new Viewbox
            {
                Width = 14, Height = 14, Margin = new Thickness(1, 0, 1, 0),
                Child = new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = (Microsoft.UI.Xaml.Media.Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Microsoft.UI.Xaml.Media.Geometry), OwnPicture.LogoPath),
                    Fill = ThemeBrush.Get("TextFillColorPrimaryBrush")
                }
            }
            : id is "dock" or "topbar" ? DockPicture(top: id == "topbar")
            : new FontIcon { Glyph = glyph, FontSize = 16 };
        _nav.Items.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { icon, new TextBlock { Text = title } } });
        return page;
    }

    void Header(Panel parent, string text)
    {
        _section = L.T(text);
        parent.Children.Add(new TextBlock { Text = L.T(text), Tag = "header", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(2, parent.Children.Count == 0 ? 0 : 22, 0, 8) });
    }

    TextBlock AddHint(Panel parent, string text)
    {
        var t = new TextBlock { Text = L.T(text), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(2, 10, 0, 0), Foreground = ThemeBrush.Get("TextFillColorSecondaryBrush") };
        parent.Children.Add(t);
        return t;
    }

    /// <summary>A Windows 11 setting card: title (+ description) on the left, the control on the right.</summary>
    Border Card(Panel parent, string title, string description, FrameworkElement control)
    {
        title = L.T(title);
        description = description == null ? null : L.T(description);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(description)) text.Children.Add(new TextBlock { Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Foreground = ThemeBrush.Get("TextFillColorSecondaryBrush") });
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        var card = new Border
        {
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(18, 12, 18, 12), MinHeight = 66, Child = grid,
            Background = ThemeBrush.Get("CardBackgroundFillColorDefaultBrush"), BorderBrush = ThemeBrush.Get("CardStrokeColorDefaultBrush")
        };
        if (!_noIndex) _index.Add((title, description, _section, parent, card));
        parent.Children.Add(card);
        return card;
    }

    static PropertyInfo Prop(string name) => typeof(DockSettings).GetProperty(name);

    Border ToggleRow(Panel parent, string label, string description, string property)
    {
        var prop = Prop(property);
        var toggle = new ToggleSwitch { OnContent = L.T("On"), OffContent = L.T("Off"), MinWidth = 0 };
        void Sync() => toggle.IsOn = (bool)prop.GetValue(DockSettings.Current);
        Sync();
        _refresh.Add(Sync);
        toggle.Toggled += (_, _) => { if ((bool)prop.GetValue(DockSettings.Current) != toggle.IsOn) prop.SetValue(DockSettings.Current, toggle.IsOn); };
        return Card(parent, label, description, toggle);
    }

    Border ChoiceRow(Panel parent, string label, string description, string property, params string[] options)
    {
        var prop = Prop(property);
        var box = new ComboBox { MinWidth = 220 };
        foreach (var o in options) box.Items.Add(new ComboBoxItem { Content = L.T(o) });
        void Sync() => box.SelectedIndex = Math.Clamp((int)prop.GetValue(DockSettings.Current), -1, box.Items.Count - 1);
        Sync();
        _refresh.Add(Sync);
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0 && (int)prop.GetValue(DockSettings.Current) != box.SelectedIndex) prop.SetValue(DockSettings.Current, box.SelectedIndex); };
        var card = Card(parent, label, description, box);
        _choices[card] = box;
        return card;
    }

    readonly Dictionary<Border, ComboBox> _choices = new();

    /// <summary>New options of a choice (the own zone layouts added or removed); the stored value is chosen again.</summary>
    void SetOptions(Border row, string[] options)
    {
        if (!_choices.TryGetValue(row, out var box)) return;
        box.Items.Clear();
        foreach (var o in options) box.Items.Add(new ComboBoxItem { Content = L.T(o) });
        foreach (var r in _refresh) r();
    }

    /// <summary>One option of a choice greyed out while it would clash with another setting (e.g. the dock at the top with the top bar).</summary>
    void DisableOption(Border row, int index, Func<bool> disabled)
    {
        if (!_choices.TryGetValue(row, out var box) || index >= box.Items.Count) return;
        void Sync() => ((ComboBoxItem)box.Items[index]).IsEnabled = !disabled();
        Sync();
        _refresh.Add(Sync);
    }

    Border SliderRow(Panel parent, string label, string description, string property, double min, double max, double step, string format)
    {
        var prop = Prop(property);
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, SnapsTo = SliderSnapsTo.StepValues, Width = 220, IsThumbToolTipEnabled = false };
        var value = new TextBlock { Width = 64, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeBrush.Get("TextFillColorSecondaryBrush") };
        bool syncing = false;
        void Sync()
        {
            syncing = true;
            double v = Convert.ToDouble(prop.GetValue(DockSettings.Current));
            slider.Value = v;
            value.Text = string.Format(format, v);
            syncing = false;
        }
        Sync();
        _refresh.Add(Sync);
        slider.ValueChanged += (_, e) =>
        {
            if (syncing) return;
            prop.SetValue(DockSettings.Current, Convert.ChangeType(e.NewValue, prop.PropertyType));
            value.Text = string.Format(format, e.NewValue);
        };
        return Card(parent, label, description, new StackPanel { Orientation = Orientation.Horizontal, Children = { value, slider } });
    }

    Border TextRow(Panel parent, string label, string description, string property)
    {
        var prop = Prop(property);
        var box = new TextBox { Width = 260, Text = (string)prop.GetValue(DockSettings.Current) ?? "" };
        box.TextChanged += (_, _) => prop.SetValue(DockSettings.Current, box.Text);
        return Card(parent, label, description, box);
    }

    /// <summary>
    /// The city of the weather: while typing the places of that name are suggested (with their region and country), a chosen
    /// one is kept whole so the right town is used; under the box what the weather is there now.
    /// </summary>
    Border CityRow(Panel parent, string label)
    {
        var s = DockSettings.Current;
        var box = new AutoSuggestBox { Width = 320, Text = s.WeatherCity, PlaceholderText = L.T("City"), QueryIcon = new SymbolIcon(Symbol.Find) };
        var now = new TextBlock { FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320, Foreground = ThemeBrush.Get("TextFillColorSecondaryBrush") };
        void ShowNow() => now.Text = s.WeatherOn ? Weather.Tip : "";
        ShowNow();
        Action changed = () => DispatcherQueue.TryEnqueue(ShowNow);
        Weather.Changed += changed;
        Closed += (_, _) => Weather.Changed -= changed;
        CancellationTokenSource typing = null;
        box.TextChanged += async (_, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            typing?.Cancel();
            var cts = typing = new CancellationTokenSource();
            string text = box.Text;
            try
            {
                await Task.Delay(300, cts.Token);                    // after a short pause in typing
                var places = await Weather.SearchAsync(text);
                if (cts.IsCancellationRequested) return;
                box.ItemsSource = places.Select(p => p.Label).ToList();
                box.IsSuggestionListOpen = places.Count > 0;
            }
            catch (OperationCanceledException) { }
        };
        box.SuggestionChosen += (_, e) => box.Text = e.SelectedItem as string ?? box.Text;
        box.QuerySubmitted += (_, e) =>
        {
            string city = (e.ChosenSuggestion as string ?? e.QueryText ?? "").Trim();
            box.Text = city;
            s.WeatherCity = city;
            now.Text = L.T("Loading…");
        };
        box.LostFocus += (_, _) => { if (box.Text.Trim() != s.WeatherCity) s.WeatherCity = box.Text.Trim(); };
        return Card(parent, label, null, new StackPanel { Children = { box, now } });
    }

    /// <summary>Left / right for a yes / no setting (true = left).</summary>
    Border SideRow(Panel parent, string label, string property)
    {
        var prop = Prop(property);
        var leftButton = new ToggleButton { Content = L.T("Left"), MinWidth = 86 };
        var rightButton = new ToggleButton { Content = L.T("Right"), MinWidth = 86, Margin = new Thickness(4, 0, 0, 0) };
        void Sync() { bool left = (bool)prop.GetValue(DockSettings.Current); leftButton.IsChecked = left; rightButton.IsChecked = !left; }
        Sync();
        _refresh.Add(Sync);
        leftButton.Click += (_, _) => { prop.SetValue(DockSettings.Current, true); Sync(); };
        rightButton.Click += (_, _) => { prop.SetValue(DockSettings.Current, false); Sync(); };
        return Card(parent, label, null, new StackPanel { Orientation = Orientation.Horizontal, Children = { leftButton, rightButton } });
    }

    /// <summary>The own picture of the Start button: a preview and "Choose…" (an image, an .ico, or the icon of a program / shortcut).</summary>
    Border PictureRow(Panel parent, string label, string description)
    {
        var preview = new Image { Width = 28, Height = 28, Margin = new Thickness(0, 0, 10, 0), Source = OwnPicture.Load(DockSettings.Current.StartButtonImage) };
        var choose = new Button { Content = L.T("Choose…") };
        choose.Click += async (_, _) =>
        {
            if (await OwnPicture.AskAsync(WinRT.Interop.WindowNative.GetWindowHandle(this)) is not { } file) return;
            try
            {
                string path = OwnPicture.ImportStart(file);
                if (OwnPicture.Load(path) is not { } picture) { await Dialogs.Info(this, L.T("Unsuitable file"), L.T("This file cannot be used as a picture.")); return; }
                DockSettings.Current.StartButtonImage = path;
                preview.Source = picture;
            }
            catch (Exception ex) { Log.Write("Start picture", ex); }
        };
        return Card(parent, label, description, new StackPanel { Orientation = Orientation.Horizontal, Children = { preview, choose } });
    }

    Border NavRow(Panel parent, string label, string description, Action open)
    {
        var card = Card(parent, label, description, new FontIcon { Glyph = "", FontSize = 12 });
        card.Tapped += (_, _) => open();
        return card;
    }

    /// <summary>
    /// Like <see cref="ItemsPage"/>, and each item can move up and down in its group with two arrows (the order of a menu); the
    /// page is laid out again after every move. <paramref name="groups"/> gives the groups in the order now.
    /// </summary>
    StackPanel OrderedItemsPage(string hint, Func<IEnumerable<IEnumerable<string>>> groups, Func<string, bool> isShown, Action<string, bool> setShown, Action<string, int> move)
    {
        var page = new StackPanel { MaxWidth = PageWidth, Spacing = 3 };
        bool built = false;
        void Build()
        {
            bool wasNoIndex = _noIndex;
            _noIndex |= built;                                            // only the first build goes into the search of the settings
            page.Children.Clear();
            AddHint(page, hint);
            foreach (var group in groups().Select(g => g.ToList()))
            {
                page.Children.Add(new Border { Height = 10 });
                for (int i = 0; i < group.Count; i++)
                {
                    string id = group[i];
                    var toggle = new ToggleSwitch { OnContent = L.T("On"), OffContent = L.T("Off"), IsOn = isShown(id), MinWidth = 0 };
                    toggle.Toggled += (_, _) => setShown(id, toggle.IsOn);
                    Button Arrow(string glyph, int delta, bool enabled, string tip)
                    {
                        var b = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 12 }, Padding = new Thickness(8, 6, 8, 6), IsEnabled = enabled, Margin = new Thickness(4, 0, 0, 0) };
                        ToolTipService.SetToolTip(b, L.T(tip));
                        b.Click += (_, _) => { move(id, delta); Build(); };
                        return b;
                    }
                    var controls = new StackPanel { Orientation = Orientation.Horizontal, Children = { toggle, Arrow("", -1, i > 0, "Move up"), Arrow("", 1, i < group.Count - 1, "Move down") } };
                    Card(page, id, null, controls).MinHeight = 48;
                }
            }
            _noIndex = wasNoIndex;
            built = true;
        }
        Build();
        return page;
    }

    StackPanel ItemsPage(string hint, IEnumerable<IEnumerable<string>> groups, Func<string, bool> isShown, Action<string, bool> setShown)
    {
        var page = new StackPanel { MaxWidth = PageWidth, Spacing = 3 };
        AddHint(page, hint);
        foreach (var group in groups)
        {
            page.Children.Add(new Border { Height = 10 });
            foreach (var id in group)
            {
                var toggle = new ToggleSwitch { OnContent = L.T("On"), OffContent = L.T("Off"), IsOn = isShown(id) };
                toggle.Toggled += (_, _) => setShown(id, toggle.IsOn);
                var card = Card(page, id, null, toggle);
                card.MinHeight = 48;
            }
        }
        return page;
    }

    /// <summary>
    /// The apps with icons in the notification area now (also the hidden ones) and a switch for each: off hides its icons
    /// for good – not on the bar, not under the arrow (Steam, AMD, Spotify …). Built when it is opened.
    /// </summary>
    StackPanel TrayIconsPage()
    {
        var page = new StackPanel { MaxWidth = PageWidth, Spacing = 3 };
        AddHint(page, "Apps that show an icon in the notification area now. Turned off, the app has no icon there at all (not under the arrow either).");
        page.Children.Add(new Border { Height = 10 });
        var apps = ShellHost.All().Where(i => !string.IsNullOrEmpty(i.Path)).GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Path: g.Key, Name: AppShell.AppName(g.Key)))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (apps.Count == 0) AddHint(page, "No app shows an icon there now.");
        _noIndex = true;
        foreach (var (path, name) in apps)
        {
            var toggle = new ToggleSwitch { OnContent = L.T("Shown"), OffContent = L.T("Hidden"), IsOn = !DockSettings.Current.TrayHidden.Split('|').Contains(path, StringComparer.OrdinalIgnoreCase) };
            toggle.Toggled += (_, _) => ShellHost.SetHidden(path, !toggle.IsOn);
            var card = Card(page, name, path, toggle);
            card.MinHeight = 52;
        }
        _noIndex = false;
        return page;
    }

    void InfoRow(Panel parent, string label, FrameworkElement value)
    {
        if (value is TextBlock t) t.Foreground = ThemeBrush.Get("TextFillColorSecondaryBrush");
        Card(parent, label, null, value).MinHeight = 52;
    }

    void MonitorRows(Panel page, Func<bool> applies)
    {
        Depends(ToggleRow(page, "Processor", "CPU", nameof(DockSettings.MonitorCpu)), applies);
        Depends(ToggleRow(page, "Memory", "RAM", nameof(DockSettings.MonitorRam)), applies);
        Depends(ToggleRow(page, "Graphics card", "GPU", nameof(DockSettings.MonitorGpu)), applies);
        Depends(ToggleRow(page, "Video memory", "VRAM of the graphics card", nameof(DockSettings.MonitorVram)), applies);
        Depends(ToggleRow(page, "Network speed", "↓ download, ↑ upload", nameof(DockSettings.MonitorNet)), applies);
    }

    /// <summary>
    /// Ready-made colours and the colour picker of WinUI for any other one. The chosen colour has a check mark and a frame
    /// (it was not seen which one was chosen); an own colour shows on the button of the picker.
    /// </summary>
    FrameworkElement AccentPicker()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var boxes = new List<(string Hex, Button Box)>();
        var own = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(2) };
        void Mark()
        {
            string now = DockSettings.Current.AccentColor ?? "";
            bool listed = false;
            foreach (var (hex, box) in boxes)
            {
                bool chosen = hex.Equals(now, StringComparison.OrdinalIgnoreCase);
                listed |= chosen;
                box.Content = chosen ? new FontIcon { Glyph = "", FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) } : null;
                box.BorderThickness = new Thickness(chosen ? 2 : 0);
                box.BorderBrush = ThemeBrush.Get("TextFillColorPrimaryBrush");
            }
            own.Background = new SolidColorBrush(Color(now));
            own.BorderBrush = listed ? null : ThemeBrush.Get("TextFillColorPrimaryBrush");
        }
        foreach (string hex in new[] { "#0078D4", "#5B5FC7", "#8764B8", "#C239B3", "#D13438", "#F7630C", "#F7B500", "#107C10", "#00B294", "#69797E" })
        {
            var color = Color(hex);
            var box = new Button { Width = 26, Height = 26, MinHeight = 0, MinWidth = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(color) };
            box.Click += (_, _) => { DockSettings.Current.AccentColor = hex; Mark(); };
            boxes.Add((hex, box));
            row.Children.Add(box);
        }
        var picker = new ColorPicker { IsAlphaEnabled = false, Color = Color(DockSettings.Current.AccentColor) };
        picker.ColorChanged += (_, e) => { DockSettings.Current.AccentColor = $"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}"; Mark(); };
        var choose = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { own, new TextBlock { Text = L.T("Choose…") } } }, Flyout = new Flyout { Content = picker } };
        row.Children.Add(choose);
        Mark();
        return row;
    }

    static Windows.UI.Color Color(string hex)
    {
        try { int v = int.Parse(hex.TrimStart('#'), System.Globalization.NumberStyles.HexNumber); return Windows.UI.Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v); }
        catch { return Microsoft.UI.Colors.DodgerBlue; }
    }

    static ToggleMenuFlyoutItem MenuItem(string text, Action click)
    {
        var item = new ToggleMenuFlyoutItem { Text = text };
        item.Click += (_, _) => click();
        return item;
    }

    // ───────────────────────── shortcuts: recorded by pressing them ─────────────────────────

    /// <summary>The keys of Windows taken over for Start (only with the own Start menu).</summary>
    void StartKeyRows(Panel parent)
    {
        var c = DockSettings.Current;
        Depends(ToggleRow(parent, "Win", "Opens Start", nameof(DockSettings.KeyStart)), () => c.ReplaceStartMenu);
        Depends(ToggleRow(parent, "Win + S", "Opens Start with the search", nameof(DockSettings.KeySearch)), () => c.ReplaceStartMenu);
        Depends(ToggleRow(parent, "Win + X", "Menu of the Start button", nameof(DockSettings.KeyPowerMenu)), () => c.ReplaceStartMenu);
    }

    /// <summary>The shortcuts of a settings page (group of <see cref="Hotkeys.Actions"/>) under a header of their own.</summary>
    void HotkeyRows(Panel parent, string group)
    {
        Header(parent, "Keyboard shortcuts");
        foreach (var a in Hotkeys.Actions.Where(a => a.Group == group)) HotkeyRow(parent, a.Id, a.Name, a.Description);
    }

    /// <summary>
    /// A shortcut recorded by pressing it. The same action may be on two pages (its own and Keyboard shortcuts): both show the
    /// same keys. A shortcut taken from another action says so under the button.
    /// </summary>
    Border HotkeyRow(Panel parent, string id, string label, string description)
    {
        var key = new Button { MinWidth = 160 };
        var note = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed, Foreground = ThemeBrush.Get("TextFillColorSecondaryBrush") };
        var clear = new Button { Content = new FontIcon { Glyph = "", FontSize = 10 }, Margin = new Thickness(0, 0, 6, 0) };
        ToolTipService.SetToolTip(clear, L.T("Remove the shortcut"));
        void Show()
        {
            if (ReferenceEquals(_recording, key)) return;
            var combo = Hotkeys.Get(id);
            key.Content = combo.ToString();
            key.Opacity = combo.IsSet ? 1 : 0.6;
            clear.Visibility = combo.IsSet ? Visibility.Visible : Visibility.Collapsed;
        }
        Show();
        _hotkeyRows.Add(Show);
        _refresh.Add(Show);
        clear.Click += (_, _) => { StopRecording(); Hotkeys.Set(id, default); };
        key.Click += (_, _) =>
        {
            bool again = ReferenceEquals(_recording, key);
            StopRecording();
            if (!again) StartRecording(id, key, note);
        };
        var keys = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { clear, key } };
        return Card(parent, label, description, new StackPanel { Children = { keys, note } });
    }

    void StartRecording(string id, Button key, TextBlock note)
    {
        _recording = key;
        key.Content = L.T("Press the keys…");
        note.Visibility = Visibility.Collapsed;
        KeyboardHook.Recording = combo => Ui.Post(() =>
        {
            if (!ReferenceEquals(_recording, key)) return;
            if (combo.Mods == 0 && combo.Key == 0x1B) { StopRecording(); return; }                      // Esc: no change
            if (combo.Mods == 0 && combo.Key is 0x08 or 0x2E) { StopRecording(); Hotkeys.Set(id, default); return; }
            // one key alone only when it types nothing (F1 …, Pause, media keys …): a letter alone would stop working in every app
            if (combo.Mods == 0 && !Hotkeys.AllowedAlone(combo.Key))
            {
                note.Text = L.T("This key alone is used for typing: add Ctrl, Alt, Shift or Win. Alone: F1 … F24, Pause, Scroll Lock, Print Screen, the menu key or media keys.");
                note.Visibility = Visibility.Visible;
                return;
            }
            StopRecording();
            string taken = Hotkeys.Set(id, combo);
            note.Visibility = taken == null ? Visibility.Collapsed : Visibility.Visible;
            if (taken != null) note.Text = L.T("{0} was removed from “{1}”.", combo, L.T(Hotkeys.Actions.First(a => a.Id == taken).Name));
        });
        KeyboardHook.Start();
    }

    void StopRecording()
    {
        if (_recording == null) return;
        _recording = null;
        KeyboardHook.Recording = null;
        foreach (var show in _hotkeyRows) show();
    }

    // ───────────────────────── control center ─────────────────────────

    static string QuickSignature => DockSettings.Current.QuickSections + "/" + DockSettings.Current.QuickTiles + "/" + DockSettings.Current.QuickHidden;

    void BuildQuickPage()
    {
        _noIndex = true;
        _quickPage.Children.Clear();
        Header(_quickPage, "Parts");
        var sections = QuickLayout.SectionOrder;
        foreach (var id in sections) QuickRow(id, QuickLayout.Sections.First(x => x.Id == id).Name, sections.IndexOf(id), sections.Count);
        Header(_quickPage, "Quick options");
        var tiles = QuickLayout.TileOrder.Where(id => id != "cell" || App.HasCellular).ToList();   // mobile data only with a SIM / eSIM modem
        foreach (var id in tiles) QuickRow(id, QuickLayout.Tiles.First(x => x.Id == id).Name, tiles.IndexOf(id), tiles.Count);
        HotkeyRows(_quickPage, "quick");
        ToggleRow(_quickPage, "Win + A", "Opens the control center instead of the quick settings of Windows", nameof(DockSettings.KeyControl));
        _noIndex = false;
        _quickShown = QuickSignature;
    }

    void QuickRow(string id, string name, int index, int count)
    {
        Button Arrow(string glyph, int delta, bool enabled)
        {
            var b = new Button { IsEnabled = enabled, Padding = new Thickness(8, 6, 8, 6), Content = new FontIcon { Glyph = glyph, FontSize = 12 } };
            b.Click += (_, _) => { QuickLayout.Move(id, delta); BuildQuickPage(); };
            return b;
        }
        var toggle = new ToggleSwitch { IsOn = QuickLayout.IsShown(id), OnContent = "", OffContent = "", Margin = new Thickness(12, 0, -100, 0) };
        toggle.Toggled += (_, _) => QuickLayout.SetShown(id, toggle.IsOn);
        var card = Card(_quickPage, name, null, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { Arrow("", -1, index > 0), Arrow("", 1, index < count - 1), toggle } });
        card.MinHeight = 52;
    }

    // ───────────────────────── dependent options and the search ─────────────────────────

    void Depends(UIElement element, Func<bool> applies) => _dependent.Add((element, applies));

    void Only(UIElement element, params int[] layouts) =>
        Depends(element, () => DockSettings.Current.ReplaceStartMenu && layouts.Contains(DockSettings.Current.StartLayout));

    void OnSettingsChanged(object s, System.ComponentModel.PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        foreach (var r in _refresh) r();
        UpdateDependent();
        if (_quickPage != null && QuickSignature != _quickShown) BuildQuickPage();
        if (DockSettings.Current.Language == _language) return;
        _language = DockSettings.Current.Language;
        L.Apply(_language);                                            // the pages again in the new language, on the same page
        int page = _nav.SelectedIndex;
        _pages.Clear(); _dependent.Clear(); _index.Clear(); _subPages.Clear(); _refresh.Clear(); _hotkeyRows.Clear(); _advanced.Clear(); _pageIds.Clear(); _more.Clear(); _moreOf.Clear(); _choices.Clear();
        _nav.Items.Clear();
        BuildPages();
        UpdateDependent();
        _nav.SelectedIndex = Math.Max(0, page);
    });

    /// <summary>The block "More options" at the bottom of a page and its list, by page.</summary>
    readonly Dictionary<Panel, (Expander Box, StackPanel List)> _more = new();
    readonly Dictionary<UIElement, Expander> _moreOf = new();

    /// <summary>The options of <see cref="_advanced"/> go from their place to the block "More options" at the bottom of their page.</summary>
    void CollectMore(IEnumerable<(UIElement Card, Panel Page)> cards)
    {
        foreach (var (card, page) in cards)
        {
            if (page == null || !page.Children.Contains(card)) continue;
            if (!_more.TryGetValue(page, out var more))
            {
                var list = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Stretch };
                var box = new Expander
                {
                    Header = L.T("More options"), Content = list, Margin = new Thickness(0, 22, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                page.Children.Add(box);
                _more[page] = more = (box, list);
            }
            page.Children.Remove(card);
            more.List.Children.Add(card);
            _moreOf[card] = more.Box;
        }
    }

    void UpdateDependent()
    {
        foreach (var (el, applies) in _dependent) el.Visibility = applies() ? Visibility.Visible : Visibility.Collapsed;
        // a block with nothing to show (all its options do not apply now) is not there either
        foreach (var (box, list) in _more.Values) box.Visibility = list.Children.Any(c => c.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (_, page) in _pages)
        {
            TextBlock header = null;
            bool any = false;
            foreach (UIElement child in page.Children)
            {
                if (child is TextBlock { Tag: "header" } h)
                {
                    if (header != null) header.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
                    header = h;
                    any = false;
                }
                else if (child is Border { Child: not null } && child.Visibility == Visibility.Visible) any = true;
            }
            if (header != null) header.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    static string Fold(string s) =>
        new string((s ?? "").Normalize(System.Text.NormalizationForm.FormD)
            .Where(ch => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();

    string PlaceOf(Panel parent, string section)
    {
        int page = _pages.FindIndex(p => p.Page == parent);
        if (page >= 0) return section != null ? $"{_pages[page].Title} › {section}" : _pages[page].Title;
        return _subPages.TryGetValue(parent, out var sub) ? $"{_pages[sub.Parent].Title} › {L.T(sub.Title)}" : "";
    }

    void Search()
    {
        string query = _search.Text.Trim();
        // emptied by a click on a result: that already showed its page (the change of the text comes later and showed the
        // main page again – over the sub-page of the result, scrolled back to the top)
        if (query.Length == 0 && _wentTo) { _wentTo = false; return; }
        if (query.Length == 0) { ShowPage(Math.Max(0, _nav.SelectedIndex)); return; }
        var words = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = _index
            .Where(x => x.Card.Visibility == Visibility.Visible || _advanced.Contains(x.Card))
            .Select(x => (x, text: Fold(x.Title + " " + x.Description + " " + PlaceOf(x.Parent, x.Section))))
            .Where(h => words.All(w => h.text.Contains(w)))
            .OrderBy(h => Fold(h.x.Title).StartsWith(words[0]) ? 0 : 1)
            .Select(h => h.x).Take(40).ToList();
        var results = new StackPanel { MaxWidth = PageWidth, Spacing = 3 };
        if (hits.Count == 0) AddHint(results, "No setting found");
        foreach (var hit in hits)
        {
            var row = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(18, 10, 18, 10),
                Content = new StackPanel { Children = { new TextBlock { Text = hit.Title }, new TextBlock { Text = PlaceOf(hit.Parent, hit.Section), FontSize = 12, Foreground = ThemeBrush.Get("TextFillColorSecondaryBrush") } } } };
            row.Click += (_, _) => GoTo(hit.Parent, hit.Card);
            results.Children.Add(row);
        }
        _back.Visibility = Visibility.Collapsed;
        _title.Text = L.T("Search results");
        _host.Content = results;
    }

    void GoTo(Panel parent, Border card)
    {
        if (_moreOf.TryGetValue(card, out var more)) more.IsExpanded = true;   // an option under "More options": opened
        if (_search.Text.Length > 0) { _wentTo = true; _search.Text = ""; }
        int page = _pages.FindIndex(p => p.Page == parent);
        if (page >= 0) { if (_nav.SelectedIndex != page) _nav.SelectedIndex = page; else ShowPage(page); }
        else if (_subPages.TryGetValue(parent, out var sub)) { _nav.SelectedIndex = sub.Parent; ShowSubPage(sub.Parent, sub.Title, (StackPanel)parent); }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            card.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3, AnimationDesired = true });
            Flash(card);
        });
    }

    bool _wentTo;

    /// <summary>The setting found lights up for a moment in the theme colour, so it is seen at once on its page.</summary>
    static void Flash(Border card)
    {
        var old = (card.BorderBrush, card.BorderThickness);
        card.BorderBrush = ThemeBrush.Accent;
        card.BorderThickness = new Thickness(2);
        var timer = card.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(1600);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { card.BorderBrush = old.BorderBrush; card.BorderThickness = old.Item2; };
        timer.Start();
    }

    static SettingsWindow _index_only;

    /// <summary>
    /// The settings of the app found by a text, for the search of Start: from the pages of a window that is built once
    /// and never shown (the open one when there is one). Every word has to be in the name, the description or the place.
    /// </summary>
    public static IEnumerable<StartEntry> Find(string text)
    {
        var w = _open ?? (_index_only ??= new SettingsWindow());
        var words = Fold(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words[0].Length < 2) return Enumerable.Empty<StartEntry>();
        return w._index
            .Select(x => (x, place: w.PlaceOf(x.Parent, x.Section)))
            .Where(h => words.All(word => Fold(h.x.Title + " " + h.x.Description + " " + h.place).Contains(word)))
            .OrderBy(h => Fold(h.x.Title).StartsWith(words[0]) ? 0 : 1)
            .Take(4)
            .Select(h => new StartEntry { Kind = EntryKind.DockSetting, Name = h.x.Title, Subtitle = h.place, Key = h.x.Title + "\n" + h.place, Glyph = "\uE713", Group = L.T("App settings") })
            .ToList();
    }

    /// <summary>Opens the page of a setting found in Start and points at it.</summary>
    public static void OpenSetting(string title, string place)
    {
        Open();
        var w = _open;
        var hit = w._index.FirstOrDefault(x => x.Title == title && w.PlaceOf(x.Parent, x.Section) == place);
        if (hit.Card != null) w.GoTo(hit.Parent, hit.Card);
    }

    // ───────────────────────── settings file ─────────────────────────

    MenuFlyout FileMenu()
    {
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft };
        void Add(string text, Action click) { var i = new MenuFlyoutItem { Text = L.T(text) }; i.Click += (_, _) => click(); menu.Items.Add(i); }
        Add("Open config folder", () => { Directory.CreateDirectory(AppPaths.Folder); Fg.Run("explorer.exe", $"\"{AppPaths.Folder}\""); });
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Export…", async () =>
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "HeMiDock-settings" };
            picker.FileTypeChoices.Add("HeMiDock", new List<string> { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSaveFileAsync() is { } file) DockSettings.Current.Export(file.Path, pins: true);
        });
        if (DockSettings.Current.DebugMode && SettingsBackup.Newest is { } newest)
            Add(L.T("Back to the settings of {0}", newest.ToString("g")), async () =>
            {
                if (!SettingsBackup.RestoreNewest()) await Dialogs.Info(this, L.T("Restore settings"), L.T("The backup could not be restored."));
            });
        Add("Import…", async () =>
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSingleFileAsync() is { } file && !DockSettings.Current.Import(file.Path, pins: DockSettings.HasPins(file.Path)))
                await Dialogs.Info(this, L.T("Import settings"), L.T("This file does not contain HeMiDock settings."));
        });
        return menu;
    }
}

/// <summary>Short messages over a window of the app.</summary>
static class Dialogs
{
    public static async Task Info(Window owner, string title, string text)
    {
        if (owner.Content?.XamlRoot is not { } root) return;
        await new ContentDialog { Title = title, Content = text, CloseButtonText = L.T("OK"), XamlRoot = root }.ShowAsync();
    }

    /// <summary>A question with Yes / Cancel; true for Yes.</summary>
    public static async Task<bool> Confirm(Window owner, string title, string text)
    {
        if (owner.Content?.XamlRoot is not { } root) return false;
        return await new ContentDialog
        {
            Title = title, Content = text, PrimaryButtonText = L.T("Remove"), CloseButtonText = L.T("Cancel"), DefaultButton = ContentDialogButton.Close, XamlRoot = root
        }.ShowAsync() == ContentDialogResult.Primary;
    }
}
