using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;

namespace HeMiDock;

/// <summary>
/// Start in three looks (Settings → Start menu → Look):
/// 0 modern – the search at the top, the pinned apps as tiles and all apps right below them (grid or list), user and power at the bottom;
/// 1 two columns (the default) – pinned programs and "All apps" as a tree whose folders open in place, the search at the
///   bottom, the user, places and power in the side column;
/// 2 full screen – an app overview on the whole monitor, search at the top, folders as a card in the middle.
/// What they share: the search (apps, settings, calculator at once, files a moment later; arrows, Tab, Enter,
/// Ctrl+Shift+Enter as administrator), the letter index, groups of pins, moving pins by dragging (onto another pin: a group),
/// own names and pictures of pins in edit mode and the menu of an entry.
/// </summary>
sealed class StartWindow : PopupWindow
{
    readonly TextBox _search = new() { PlaceholderText = L.T("Search") };
    readonly StackPanel _results = new() { Spacing = 2 };
    readonly ScrollViewer _resultsView;
    readonly HashSet<string> _openGroups = new();
    readonly Grid _frame = new();                                    // always the content: the look inside it changes
    readonly Dictionary<string, FrameworkElement> _letterHeads = new();
    CancellationTokenSource _files;
    List<StartEntry> _local = new(), _found = new();
    int _selected;
    bool _stale = true;
    int _layout;

    // the parts of the current look
    Grid _root;
    ContentControl _main;                                            // home / all apps / letters / folder (modern, two columns)
    FrameworkElement _home, _all;
    StackPanel _pinned, _allList;
    Grid _folderLayer;                                               // full screen: the opened folder as a card
    TextBlock _switchText;
    FontIcon _switchGlyph;
    bool _showAll;
    DropMarker _marker;

    static DockSettings S => DockSettings.Current;
    bool FullScreen => _layout == 2;

    public StartWindow() : base("start")
    {
        _resultsView = new ScrollViewer { Content = _results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
        _search.TextChanged += (_, _) => Search();
        _search.KeyDown += Search_KeyDown;
        AppCatalog.Loaded += OnAppsLoaded;
        StartPins.Changed += OnPinsChanged;
        S.PropertyChanged += OnSettings;
        Closed += (_, _) => { AppCatalog.Loaded -= OnAppsLoaded; StartPins.Changed -= OnPinsChanged; S.PropertyChanged -= OnSettings; _files?.Cancel(); };
        _frame.RightTapped += Frame_RightTapped;
        // a click anywhere outside the box for the name of a group keeps the name (an empty place takes no focus, so the
        // box did not lose it) – and does nothing else (it would close Start or the opened folder)
        _frame.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
        {
            _tapEndedNaming = false;
            if (_naming == null || _nameBox == null || Within(e.OriginalSource as DependencyObject, _nameBox)) return;
            _tapEndedNaming = true;
            EndNaming(_nameBox.Text);
        }), true);
        Content = _frame;
        // full screen: Windows sizes the window again when it comes onto a monitor with another scale (it was left small in a
        // corner of the screen of a notebook) – it is spread over the monitor again
        AppWindow.Changed += (_, a) =>
        {
            if (a.DidSizeChange && FullScreen && Current == this) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Place);
        };
        Build();
    }

    void OnSettings(object s, System.ComponentModel.PropertyChangedEventArgs e) => _stale = true;   // built again at the next opening
    void OnAppsLoaded() => Ui.Post(() => { BuildAll(); if (_search.Text.Length > 0) Search(); });
    void OnPinsChanged() => Ui.Post(BuildPinned);

    protected override void Shown() => _search.Focus(FocusState.Programmatic);

    /// <summary>Puts the cursor into the search box (Win+S while Start is already open).</summary>
    public void FocusSearch()
    {
        Fg.Force(Hwnd);
        _search.Focus(FocusState.Programmatic);
        _search.SelectAll();
    }

    protected override void Reopen()
    {
        _files?.Cancel();
        _search.Text = "";
        _naming = null;                                                  // a name left unfinished when Start closed
        if (_stale) { Build(); return; }
        _openGroups.Clear();
        foreach (var e in _treeFolders) e.Expanded = false;          // folders closed again, like a fresh Start menu
        ShowAllApps(false);
        BuildPinned();
        if (_allList.Children.Count == 0) BuildAll();                 // never open with an empty list
        CloseFolder();
    }

    protected override bool OnEscape()
    {
        if (_naming != null) { EndNaming(null); return true; }           // the name of a group being written: only that is cancelled
        if (_search.Text.Length > 0) { _search.Text = ""; return true; }
        if (_folderLayer?.Visibility == Visibility.Visible) { CloseFolder(); return true; }
        if (_main != null && _main.Content != _home) { ShowHomePage(); return true; }   // letters, folder, all apps → back
        return false;
    }

    // ───────────────────────── the looks ─────────────────────────

    void Build()
    {
        _stale = false;
        _layout = S.StartLayout;
        _showAll = false;
        _letterHeads.Clear();
        (_search.Parent as Panel)?.Children.Remove(_search);
        if (_search.Parent is Border holder) holder.Child = null;
        (_resultsView.Parent as Panel)?.Children.Remove(_resultsView);
        // the results are shared by the looks: the full-screen one makes them 640 wide in the middle – back to the column
        // (otherwise the results of the other looks were cut on both sides)
        _resultsView.Width = double.NaN;
        _resultsView.HorizontalAlignment = HorizontalAlignment.Stretch;
        // the search box keeps the row and column of the look it was in (two columns: at the bottom) – in the modern look
        // it then lay over the user at the bottom
        foreach (var shared in new FrameworkElement[] { _search, _resultsView })
        {
            Grid.SetRow(shared, 0);
            Grid.SetColumn(shared, 0);
            Grid.SetRowSpan(shared, 1);
            Grid.SetColumnSpan(shared, 1);
            shared.Margin = new Thickness(0);
        }
        _pinned = new StackPanel();
        _allList = new StackPanel { Spacing = 1 };
        _folderLayer = null;
        Native.SetDwmAttr(Hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, FullScreen ? Native.DWMWCP_DONOTROUND : Native.DWMWCP_ROUND);
        _frame.Children.Clear();
        _frame.Children.Add(_layout switch { 0 => BuildModern(), 2 => BuildFullScreen(), _ => BuildClassic() });
        _marker = new DropMarker(_frame);                                 // where a dragged pin will go
        BuildPinned();
        BuildAll();
    }

    /// <summary>The search box in a rounded field with a magnifier.</summary>
    FrameworkElement SearchField(double fontSize)
    {
        _search.FontSize = fontSize;
        _search.HorizontalAlignment = HorizontalAlignment.Stretch;
        return _search;
    }

    FrameworkElement BuildModern()
    {
        _root = new Grid { Width = Math.Clamp(S.StartWidth, 460, 960), Height = S.StartHeight, Padding = new Thickness(20, 18, 20, 12) };
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var field = SearchField(14);
        field.Margin = new Thickness(0, 0, 0, 12);
        _root.Children.Add(field);

        var page = new StackPanel { Spacing = 8 };
        var pinnedHead = new Grid();
        pinnedHead.Children.Add(Heading(L.T("Pinned")));
        page.Children.Add(pinnedHead);
        page.Children.Add(_pinned);
        var allHead = new Grid();
        allHead.Children.Add(Heading(L.T("All apps")));
        var view = new Button { Style = DockButton, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(8, 4, 8, 4), Content = new TextBlock { Text = L.T(S.StartAppsGrid ? "View: Grid" : "View: List"), FontSize = 12 } };
        var viewMenu = new MenuFlyout();
        foreach (var (text, grid) in new[] { ("Grid", true), ("List", false) })
        {
            var item = new ToggleMenuFlyoutItem { Text = L.T(text), IsChecked = S.StartAppsGrid == grid };
            item.Click += (_, _) => { S.StartAppsGrid = grid; S.Save(); Build(); };
            viewMenu.Items.Add(item);
        }
        view.Flyout = viewMenu;
        allHead.Children.Add(view);
        page.Children.Add(allHead);
        page.Children.Add(_allList);
        if (S.StartShowRecent && RecentFiles.Load(6) is { Count: > 0 } recent)
        {
            page.Children.Add(Heading(L.T("Recent files")));
            foreach (var r in recent) page.Children.Add(Row(r));
        }
        _home = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _main = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, Content = _home };
        var middle = new Grid { Children = { _main, _resultsView } };
        Grid.SetRow(middle, 1);
        _root.Children.Add(middle);
        if (S.StartShowUser || S.StartShowPower)
        {
            var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            if (S.StartShowUser) bottom.Children.Add(UserButton(32));
            var power = new Button { Style = DockButton, HorizontalAlignment = HorizontalAlignment.Right, Content = new FontIcon { Glyph = "", FontSize = 18 } };
            ToolTipService.SetToolTip(power, L.T("Power"));
            power.Click += (_, _) => PowerMenu(power);
            if (S.StartShowPower) bottom.Children.Add(power);
            Grid.SetRow(bottom, 2);
            _root.Children.Add(bottom);
        }
        AddResizeGrip(width: true);
        return Wrap(_root);
    }

    FrameworkElement BuildClassic()
    {
        bool sideLeft = S.ClassicSideLeft;
        _root = new Grid { Width = 560, Height = S.StartHeight };
        var side = new GridLength(Math.Clamp(S.ClassicSideWidth, 160, 320));
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = sideLeft ? side : new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = sideLeft ? new GridLength(1, GridUnitType.Star) : side });

        // programs: pinned / all apps / letters, "All apps ›", the search at the bottom
        var programs = new Grid { Margin = new Thickness(10, 12, 10, 12) };
        programs.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        programs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        programs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var home = new StackPanel { Children = { _pinned } };
        if (S.StartShowRecent && RecentFiles.Load(6) is { Count: > 0 } recent)
        {
            home.Children.Add(new Border { Height = 1, Margin = new Thickness(8, 8, 8, 8), Background = Res("DividerStrokeColorDefaultBrush") });
            home.Children.Add(new TextBlock { Text = L.T("Recent"), FontSize = 12.5, Margin = new Thickness(10, 0, 0, 6), Foreground = Res("TextFillColorSecondaryBrush") });
            foreach (var r in recent) home.Children.Add(Row(r));
        }
        _home = new ScrollViewer { Content = home, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _main = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, Content = _home };
        programs.Children.Add(new Grid { Children = { _main, _resultsView } });

        _switchText = new TextBlock { Text = L.T("All apps"), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _switchGlyph = new FontIcon { Glyph = "", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right };
        var switchButton = new Button { Style = DockButton, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 7, 10, 7), Content = new Grid { Children = { _switchText, _switchGlyph } } };
        switchButton.Click += (_, _) => ShowAllApps(!_showAll);
        var switchRow = new Border { BorderBrush = Res("DividerStrokeColorDefaultBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(4, 6, 4, 0), Padding = new Thickness(0, 6, 0, 0), Child = switchButton, Tag = "switch" };
        Grid.SetRow(switchRow, 1);
        programs.Children.Add(switchRow);
        var field = SearchField(13.5);
        field.Margin = new Thickness(2, 8, 2, 0);
        Grid.SetRow(field, 2);
        programs.Children.Add(field);
        Grid.SetColumn(programs, sideLeft ? 1 : 0);
        _root.Children.Add(programs);

        // side column: user, places, power
        var sidePanel = new Grid { Margin = new Thickness(10, 14, 10, 12) };
        sidePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidePanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sidePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (S.StartShowUser)
        {
            var user = UserButton(40);
            sidePanel.Children.Add(new StackPanel { Children = { user, new Border { Height = 1, Margin = new Thickness(6, 10, 6, 8), Background = Res("DividerStrokeColorDefaultBrush") } } });
        }
        var places = new StackPanel();
        foreach (var group in StartSide.ShownPlaces())
        {
            if (places.Children.Count > 0) places.Children.Add(new Border { Height = 1, Margin = new Thickness(6), Background = Res("DividerStrokeColorDefaultBrush") });
            foreach (var place in group)
            {
                var b = new Button
                {
                    Style = DockButton, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 8, 10, 8),
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 10,
                        Children = { new FontIcon { Glyph = place.Glyph, FontSize = 15, Width = 22, Foreground = Res("TextFillColorSecondaryBrush") }, new TextBlock { Text = L.T(place.Id), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis } }
                    }
                };
                b.Click += (_, _) => { CloseNow(); try { place.Open(); } catch (Exception ex) { Log.Write("Start place " + place.Id, ex); } };
                places.Children.Add(b);
            }
        }
        var placesView = new ScrollViewer { Content = places, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(placesView, 1);
        sidePanel.Children.Add(placesView);
        if (S.StartShowPower && PowerRow() is { } power)
        {
            Grid.SetRow(power, 2);
            sidePanel.Children.Add(power);
        }
        var sideBorder = new Border
        {
            Child = sidePanel, Background = Res("LayerFillColorDefaultBrush"), BorderBrush = Res("DividerStrokeColorDefaultBrush"),
            BorderThickness = sideLeft ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0)
        };
        Grid.SetColumn(sideBorder, sideLeft ? 0 : 1);
        _root.Children.Add(sideBorder);

        if (S.DockEditMode)                                          // edit mode: the width of the side column with the mouse
        {
            var line = new Thumb { Width = 6, Opacity = 0.7, HorizontalAlignment = sideLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left, Background = Res("AccentFillColorDefaultBrush") };
            Grid.SetColumn(line, sideLeft ? 0 : 1);
            var col = _root.ColumnDefinitions[sideLeft ? 0 : 1];
            line.DragDelta += (_, e) => col.Width = new GridLength(Math.Clamp(col.ActualWidth + (sideLeft ? e.HorizontalChange : -e.HorizontalChange), 160, 320));
            line.DragCompleted += (_, _) => S.ClassicSideWidth = (int)col.ActualWidth;
            _root.Children.Add(line);
        }
        AddResizeGrip(width: false);
        return Wrap(_root);
    }

    /// <summary>The width of the lock and of the arrow: the same for both, as high as the main button is.</summary>
    const double SmallPower = 34;

    /// <summary>The main power action (shut down), lock, and an arrow with all items – each only when there is something for it.</summary>
    FrameworkElement PowerRow()
    {
        var shown = StartSide.ShownPower().SelectMany(g => g).ToList();
        if (shown.Count == 0) return null;
        var main = shown.FirstOrDefault(p => p.Id == "Shut down") ?? shown.FirstOrDefault(p => p.Id != "Lock");
        bool hasLock = shown.Any(p => p.Id == "Lock");
        var row = new Grid { ColumnSpacing = 4 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Brush field = Res("ControlFillColorDefaultBrush");
        if (main != null)
        {
            string glyph = main.Id switch { "Restart" => "", "Sleep" => "", "Sign out" => "", "Hibernate" => "", _ => "" };
            var b = new Button { Style = DockButton, Background = field, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(9, 9, 9, 9), MinWidth = 0,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = glyph, FontSize = 15 }, new TextBlock { Text = L.T(main.Id), FontSize = 13 } } } };
            b.Click += (_, _) => { CloseNow(); main.Run(); };
            row.Children.Add(b);
        }
        if (hasLock)
        {
            var b = new Button { Style = DockButton, Background = field, Width = SmallPower, MinWidth = 0, VerticalAlignment = VerticalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(0, 9, 0, 9), Content = new FontIcon { Glyph = "", FontSize = 15 } };
            ToolTipService.SetToolTip(b, L.T("Lock"));
            b.Click += (_, _) => { CloseNow(); shown.First(p => p.Id == "Lock").Run(); };
            Grid.SetColumn(b, 1);
            row.Children.Add(b);
        }
        if (shown.Count > (main != null ? 1 : 0) + (hasLock ? 1 : 0))
        {
            var b = new Button { Style = DockButton, Background = field, Width = SmallPower, MinWidth = 0, VerticalAlignment = VerticalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(0, 9, 0, 9), Content = new FontIcon { Glyph = "", FontSize = 10 } };
            ToolTipService.SetToolTip(b, L.T("Power"));
            b.Click += (_, _) => PowerMenu(b);
            Grid.SetColumn(b, 2);
            row.Children.Add(b);
        }
        return row;
    }

    FrameworkElement BuildFullScreen()
    {
        // always dark, also when the rest of HeMiDock is light – the apps stand out best on it; a dark veil over the acrylic
        _root = new Grid { RequestedTheme = ElementTheme.Dark, Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0x8C, 0x14, 0x14, 0x18)) };
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var field = SearchField(15);
        field.Width = 460;
        field.HorizontalAlignment = HorizontalAlignment.Center;
        field.Margin = new Thickness(24, 48, 24, 24);
        _root.Children.Add(field);
        var body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 8 };
        body.Children.Add(_pinned);
        body.Children.Add(new Border { Height = 1, Margin = new Thickness(40, 16, 40, 16), Background = Res("DividerStrokeColorDefaultBrush") });
        body.Children.Add(_allList);
        _home = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _resultsView.HorizontalAlignment = HorizontalAlignment.Center;
        _resultsView.Width = 640;
        var middle = new Grid { Children = { _home, _resultsView } };
        Grid.SetRow(middle, 1);
        _root.Children.Add(middle);
        if (S.StartShowUser || S.StartShowPower)
        {
            var bottom = new Grid { Margin = new Thickness(48, 8, 48, 24) };
            if (S.StartShowUser) bottom.Children.Add(UserButton(36));
            var power = new Button { Style = DockButton, HorizontalAlignment = HorizontalAlignment.Right, Content = new FontIcon { Glyph = "", FontSize = 20 } };
            power.Click += (_, _) => PowerMenu(power);
            if (S.StartShowPower) bottom.Children.Add(power);
            Grid.SetRow(bottom, 2);
            _root.Children.Add(bottom);
        }
        // the opened folder as a card in the middle; a click beside it closes it
        _folderLayer = new Grid { Visibility = Visibility.Collapsed, Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0x60, 0, 0, 0)) };
        Grid.SetRowSpan(_folderLayer, 3);
        _folderLayer.Tapped += (_, e) => { if (e.OriginalSource == _folderLayer && !_tapEndedNaming) CloseFolder(); e.Handled = true; };
        _root.Children.Add(_folderLayer);
        // a click on empty space closes the whole Start
        _root.Tapped += (_, e) => { if (e.OriginalSource is Grid or ScrollViewer or StackPanel && e.OriginalSource != _folderLayer && !_tapEndedNaming) CloseNow(); };
        return _root;
    }

    /// <summary>The look in a frame of its own (so the theme of the window applies to it).</summary>
    static FrameworkElement Wrap(FrameworkElement look) => look;

    /// <summary>A right click on empty space: the settings of Start (pinning a folder from here was taken away).</summary>
    void Frame_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.Handled || e.OriginalSource is TextBox || e.OriginalSource is FrameworkElement { Parent: TextBox }) return;
        e.Handled = true;
        var menu = new MenuFlyout();
        KeepWhileOpen(menu);
        var settings = new MenuFlyoutItem { Text = L.T("Start menu settings") };
        settings.Click += (_, _) => { CloseNow(); SettingsWindow.Open("start"); };
        menu.Items.Add(settings);
        menu.ShowAt(_frame, new FlyoutShowOptions { Position = e.GetPosition(_frame) });
    }

    /// <summary>Edit mode: Start is resized with the mouse by its upper corner.</summary>
    void AddResizeGrip(bool width)
    {
        if (!S.DockEditMode) return;
        var grip = new Thumb { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Opacity = 0.8, Background = Res("AccentFillColorDefaultBrush") };
        grip.DragDelta += (_, e) =>
        {
            _root.Height = Math.Clamp(_root.ActualHeight - e.VerticalChange, 440, 960);
            if (width) _root.Width = Math.Clamp(_root.ActualWidth + e.HorizontalChange, 460, 960);
            Place();
        };
        grip.DragCompleted += (_, _) => { S.StartHeight = _root.Height; if (width) S.StartWidth = _root.Width; };
        Grid.SetRowSpan(grip, 9);
        Grid.SetColumnSpan(grip, 9);
        Canvas.SetZIndex(grip, 900);
        _root.Children.Add(grip);
    }

    /// <summary>
    /// Full screen: the whole monitor of the dock. Otherwise the set height, smaller only while it does not fit the monitor –
    /// from the set height on every opening, so it grows back when the resolution goes back up.
    /// </summary>
    protected override bool FillsWindow => FullScreen;

    protected override void Place()
    {
        var dock = (IPanelWindow)App.Anchor ?? App.Dock;
        var mon = dock?.Monitor ?? Monitors.Primary();
        if (FullScreen)
        {
            var b = mon.Bounds;
            // the host made while Start had another look (prepared ahead, or the look changed in the settings) still kept the
            // content at its own size: the apps sat in a strip at the left edge of the screen
            if (Content is PopupHost host) host.Fill = true;
            _frame.VerticalAlignment = VerticalAlignment.Stretch;
            _frame.HorizontalAlignment = HorizontalAlignment.Stretch;
            MoveTo(new Windows.Graphics.RectInt32(b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top));
            FitFullScreen((b.Right - b.Left) / Scale);
            return;
        }
        double avail = (mon.Work.Bottom - mon.Work.Top) / Scale - (S.IsVertical ? 40 : 110);
        if (_root != null) _root.Height = Math.Max(400, Math.Min(S.StartHeight, avail));
        base.Place();
    }

    double _fullWidth = 1600;

    void FitFullScreen(double width)
    {
        if (Math.Abs(width - _fullWidth) < 1) return;
        _fullWidth = width;
        BuildPinned();
        BuildAll();
    }

    /// <summary>Tiles per row of the full-screen look: whole tiles, at most 10, centred with even margins.</summary>
    int FullColumns => Math.Clamp((int)(Math.Min(_fullWidth * 0.82, FullTile * 10) / FullTile), 3, 10);
    double FullTile => Math.Round(Math.Max(56, S.StartIconSize * 1.6) * 2.1) + 8;

    // ───────────────────────── pinned and all apps ─────────────────────────

    void BuildPinned()
    {
        if (_pinned == null) return;
        _pinButtons.Clear();
        _pinned.Children.Clear();
        var pins = StartPins.Keys.Where(k => StartPins.IsGroup(k) || AppShell.IsShellKey(k) || File.Exists(k) || Directory.Exists(k)).Select(StartEntry.FromKey).ToList();
        if (_layout == 1)
        {
            // two columns: rows; groups of pins open in place, like the folders of all apps
            foreach (var e in pins)
            {
                _pinned.Children.Add(Row(e, pinned: true));
                if (!StartPins.IsGroup(e.Key) || !_openGroups.Contains(e.Key)) continue;
                e.Expanded = true;
                foreach (var child in e.Children) _pinned.Children.Add(Row(child, indent: 22, pinned: true));
            }
            return;
        }
        var grid = TileGrid();
        foreach (var e in pins) grid.Children.Add(Tile(e, pinned: true));
        _pinned.Children.Add(grid);
    }

    static readonly global::Windows.UI.ViewManagement.UISettings TextSettings = new();
    static double TextScale { get { try { return Math.Max(1, TextSettings.TextScaleFactor); } catch { return 1; } } }

    VariableSizedWrapGrid TileGrid()
    {
        // full screen: whole tiles per row, every row as wide as a full one (the grid stays centred with even margins)
        if (FullScreen) return new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = FullTile, ItemHeight = FullTile * 0.85, MaximumRowsOrColumns = FullColumns, Width = FullColumns * FullTile, HorizontalAlignment = HorizontalAlignment.Center };
        double available = Math.Clamp(S.StartWidth, 460, 960) - 40 - 8;
        int columns = Math.Max(3, (int)(available / (S.StartIconSize * 2.6)));
        double w = Math.Floor(available / columns);
        // room for two lines of the name in the text size of Windows (Accessibility → Text size): with a fixed height the
        // names of one row reached into the next one
        double lines = 2 * Math.Ceiling(12 * 1.34 * TextScale);
        return new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = w, ItemHeight = Math.Ceiling(S.StartIconSize + 6 + lines + 14), MaximumRowsOrColumns = columns };
    }

    readonly List<StartEntry> _treeFolders = new();
    List<(string Letter, List<StartEntry> Items)> _groups = new();

    /// <summary>All apps alphabetically under their letters; apps of a Start menu folder are in one folder entry (like Windows 11).</summary>
    void BuildAll()
    {
        if (_allList == null) return;
        var inFolder = AppCatalog.Folders.SelectMany(f => f.Apps).ToHashSet();
        var all = AppCatalog.Apps.Where(a => !inFolder.Contains(a)).Select(StartEntry.From).Concat(AppCatalog.Folders.Select(StartEntry.From))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _groups = all.GroupBy(e => LetterOf(e.Name)).OrderBy(g => g.Key == "#" ? "" : g.Key, StringComparer.Ordinal).Select(g => (g.Key, g.ToList())).ToList();
        _treeFolders.Clear();
        _treeFolders.AddRange(all.Where(e => e.Kind == EntryKind.AppFolder));
        RefreshAll();
    }

    void RefreshAll()
    {
        _allList.Children.Clear();
        _letterHeads.Clear();
        if (!AppCatalog.IsLoaded) _allList.Children.Add(new TextBlock { Text = L.T("Loading apps…"), Margin = new Thickness(12, 8, 0, 0), Foreground = Res("TextFillColorTertiaryBrush") });
        if (FullScreen)                                              // an app overview: one grid, no letters
        {
            var overview = TileGrid();
            foreach (var e in _groups.SelectMany(g => g.Items)) overview.Children.Add(Tile(e));
            _allList.Children.Add(overview);
            return;
        }
        bool grid = _layout == 0 && S.StartAppsGrid;
        foreach (var (letter, items) in _groups)
        {
            // the letter opens the letter index
            var head = new Button { Style = DockButton, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(4, 10, 0, 2), Content = new TextBlock { Text = letter, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = FullScreen ? 16 : 14 } };
            if (FullScreen) head.HorizontalAlignment = HorizontalAlignment.Left;
            head.Click += (_, _) => ShowLetters();
            _letterHeads[letter] = head;
            _allList.Children.Add(head);
            if (grid)
            {
                var tiles = TileGrid();
                foreach (var e in items) tiles.Children.Add(Tile(e));
                _allList.Children.Add(tiles);
                continue;
            }
            foreach (var e in items)
            {
                _allList.Children.Add(Row(e));
                if (_layout == 1 && e.Kind == EntryKind.AppFolder && e.Expanded)   // two columns: a tree, folders open in place
                    foreach (var child in e.Children) _allList.Children.Add(Row(child, indent: 22));
            }
        }
    }

    public static string LetterOf(string name)
    {
        foreach (var ch in (name ?? "").Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetter(ch)) return char.ToUpperInvariant(ch).ToString();
            if (char.IsDigit(ch)) return "#";
        }
        return "#";
    }

    // ───────────────────────── pages ─────────────────────────

    void ShowHomePage()
    {
        if (_layout == 1) { ShowAllApps(false); return; }
        if (_main != null) _main.Content = _home;
    }

    /// <summary>Two columns: all apps instead of the pinned ones ("All apps ›" / "‹ Back").</summary>
    void ShowAllApps(bool all)
    {
        if (_layout != 1) return;
        _showAll = all;
        _all ??= new ScrollViewer { Content = _allList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _switchText.Text = L.T(all ? "Back" : "All apps");
        _switchGlyph.Glyph = all ? "" : "";
        _switchGlyph.HorizontalAlignment = all ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        _switchText.Margin = all ? new Thickness(22, 0, 0, 0) : new Thickness(0);
        _main.Content = all ? _all : _home;
        if (all) ((ScrollViewer)_all).ChangeView(null, 0, null, true);
    }

    /// <summary>The letter index: the letters with apps are bright, a click goes back to all apps at that letter.</summary>
    void ShowLetters()
    {
        var present = _groups.Select(g => g.Letter).ToHashSet();
        var letters = new[] { "#" }.Concat(Enumerable.Range('A', 26).Select(c => ((char)c).ToString()))
            .Concat(present.Where(l => l != "#" && (l.Length != 1 || l[0] < 'A' || l[0] > 'Z')).OrderBy(l => l, StringComparer.Ordinal)).ToList();   // other alphabets
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = FullScreen ? 9 : 5, ItemWidth = 58, ItemHeight = 50, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var l in letters)
        {
            bool on = present.Contains(l);
            var b = new Button { Style = DockButton, Width = 52, Height = 44, IsEnabled = on, Opacity = on ? 1 : 0.3, Content = new TextBlock { Text = l, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold } };
            b.Click += (_, _) => JumpTo(l);
            grid.Children.Add(b);
        }
        var page = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Children = { grid } };
        page.Tapped += (_, e) => { if (e.OriginalSource == page && !_tapEndedNaming) ShowHomePage(); };
        if (FullScreen) { ShowCard(page, null); return; }
        _main.Content = page;
    }

    void JumpTo(string letter)
    {
        CloseFolder();
        if (_layout == 1) ShowAllApps(true); else if (_main != null) _main.Content = _home;
        if (_letterHeads.TryGetValue(letter, out var head))
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => head.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false }));
    }

    /// <summary>A folder (or a group of pins) – each look its own way: a page, in place, a card in the middle.</summary>
    void OpenFolder(StartEntry folder)
    {
        if (_layout == 1)
        {
            if (StartPins.IsGroup(folder.Key)) { if (!_openGroups.Remove(folder.Key)) _openGroups.Add(folder.Key); BuildPinned(); return; }
            var tree = _treeFolders.FirstOrDefault(f => f.Key == folder.Key) ?? folder;
            tree.Expanded = !tree.Expanded;
            RefreshAll();
            return;
        }
        var list = FullScreen ? (Panel)TileGridFor(folder.Children.Count) : new StackPanel { Spacing = 1 };
        foreach (var c in folder.Children) list.Children.Add(FullScreen ? Tile(c, pinned: StartPins.IsGroup(folder.Key)) : Row(c, pinned: StartPins.IsGroup(folder.Key)));
        if (FullScreen) { ShowCard(list, folder.Name); return; }
        var back = new Button { Style = DockButton, Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = "", FontSize = 13 }, new TextBlock { Text = folder.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold } } } };
        back.Click += (_, _) => ShowHomePage();
        _main.Content = new ScrollViewer { Content = new StackPanel { Spacing = 8, Children = { back, list } }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    VariableSizedWrapGrid TileGridFor(int count)
    {
        var g = TileGrid();
        g.MaximumRowsOrColumns = Math.Clamp(count, 1, 5);
        if (FullScreen) g.Width = g.MaximumRowsOrColumns * FullTile;
        return g;
    }

    /// <summary>Full screen: a card over the apps (a folder, the letter index).</summary>
    void ShowCard(FrameworkElement content, string title)
    {
        var stack = new StackPanel { Spacing = 12 };
        if (title != null) stack.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(content);
        var card = new Border
        {
            Child = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 640 },
            Padding = new Thickness(24), CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0xE6, 0x2A, 0x2A, 0x30)), BorderBrush = Res("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1)
        };
        card.Tapped += (_, e) => { if (e.OriginalSource != card) return; e.Handled = true; };
        _folderLayer.Children.Clear();
        _folderLayer.Children.Add(card);
        _folderLayer.Visibility = Visibility.Visible;
    }

    void CloseFolder()
    {
        if (_folderLayer == null) return;
        _folderLayer.Visibility = Visibility.Collapsed;
        _folderLayer.Children.Clear();
    }

    // ───────────────────────── entries ─────────────────────────

    static Style DockButton => (Style)Application.Current.Resources["DockButton"];
    static Brush Res(string key) => ThemeBrush.Get(key);

    static TextBlock Heading(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(4, 6, 0, 4), VerticalAlignment = VerticalAlignment.Center };

    static Image IconOf(StartEntry e, double size)
    {
        var image = new Image { Width = size, Height = size };
        if (e.OwnIcon != null && OwnPicture.Load(e.OwnIcon) is { } own) image.Source = own;
        else if (e.IconKey != null) Pictures.Into(image, e.IconKey, e.IconIsFile);
        return image;
    }

    /// <summary>An app as a tile: its icon and name under it.</summary>
    FrameworkElement Tile(StartEntry e, bool pinned = false)
    {
        double size = FullScreen ? Math.Max(56, S.StartIconSize * 1.6) : S.StartIconSize;
        FrameworkElement icon = e.IconKey != null || e.OwnIcon != null ? IconOf(e, size) : new FontIcon { Glyph = e.Glyph ?? "", FontSize = size * 0.7 };
        if (e.Kind == EntryKind.AppFolder && e.Children is { Count: > 0 } kids && StartPins.IsGroup(e.Key))
        {
            // a group shows the icons of its first apps, like a folder of a phone
            var mini = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 2, ItemWidth = size / 2, ItemHeight = size / 2, Width = size, Height = size };
            foreach (var k in kids.Take(4)) mini.Children.Add(IconOf(k, size / 2 - 4));
            icon = new Border { Child = mini, CornerRadius = new CornerRadius(8), Background = Res("ControlFillColorDefaultBrush") };
        }
        FrameworkElement name = e.Key == _naming && e.Key != null
            ? NameEditor(e, FullScreen ? 13 : 12)
            : new TextBlock { Text = e.Name, FontSize = FullScreen ? 13 : 12, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center };
        var content = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Children = { icon, name } };
        if (name is TextBox) return new Border { Padding = new Thickness(4), Margin = new Thickness(2), Child = content };   // being named: no button around it
        var button = new Button
        {
            Style = DockButton, Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(2),
            Content = content
        };
        ToolTipService.SetToolTip(button, e.Name);
        Wire(button, e, pinned);
        return button;
    }

    /// <summary>A row of a list: icon (or symbol), name and a second line; a folder has its chevron.</summary>
    FrameworkElement Row(StartEntry e, double indent = 0, bool pinned = false)
    {
        double size = _layout == 1 ? Math.Round(S.StartIconSize * 0.78) : 24;
        FrameworkElement icon = e.IconKey != null || e.OwnIcon != null ? IconOf(e, size) : new FontIcon { Glyph = e.Glyph ?? "", FontSize = 18, Width = size };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        bool naming = e.Key == _naming && e.Key != null;
        texts.Children.Add(naming ? NameEditor(e, 13) : new TextBlock { Text = e.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(e.Subtitle)) texts.Children.Add(new TextBlock { Text = e.Subtitle, FontSize = 12, Foreground = Res("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        var row = new Grid { ColumnSpacing = 11 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(icon);
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        if (e.Kind == EntryKind.AppFolder)
        {
            var chevron = new FontIcon { Glyph = _layout == 1 ? (e.Expanded ? "" : "") : "", FontSize = 10, Foreground = Res("TextFillColorSecondaryBrush") };
            Grid.SetColumn(chevron, 2);
            row.Children.Add(chevron);
        }
        var padding = new Thickness(8, _layout == 1 ? 9 : 6, 8, _layout == 1 ? 9 : 6);
        if (naming) return new Border { Padding = padding, Margin = new Thickness(indent, 0, 0, 0), Child = row };   // being named: no button around it
        var button = new Button
        {
            Style = DockButton, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = row,
            Padding = new Thickness(8, _layout == 1 ? 9 : 6, 8, _layout == 1 ? 9 : 6), Margin = new Thickness(indent, 0, 0, 0)
        };
        ToolTipService.SetToolTip(button, e.Name);
        Wire(button, e, pinned);
        return button;
    }

    /// <summary>Click, right click and – for pins – dragging (onto another pin: they make a group).</summary>
    void Wire(Button button, StartEntry e, bool pinned)
    {
        button.Tag = e;
        button.Click += (_, _) => { if (!_pinDragged) Launch(e, AdminKeys); };   // not after a drag
        button.ContextRequested += (_, a) => { a.Handled = true; EntryMenu(e, pinned).ShowAt(button, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft }); };
        if (!pinned) return;
        _pinButtons.Add(button);
        // the drag of WinUI did not start on the buttons: the mouse is followed through Windows itself, the picture of the
        // app goes with it, a line / frame shows where it goes
        button.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, a) =>
        {
            if (!a.GetCurrentPoint(button).Properties.IsLeftButtonPressed || !Native.GetCursorPos(out var from)) return;
            var follow = DispatcherQueue.CreateTimer();
            follow.Interval = TimeSpan.FromMilliseconds(12);
            bool dragging = false;
            follow.Tick += (_, _) =>
            {
                if (!Native.GetCursorPos(out var now)) return;
                bool down = (Native.GetAsyncKeyState(0x01) & 0x8000) != 0;
                if (!dragging && Math.Abs(now.X - from.X) + Math.Abs(now.Y - from.Y) >= 8)
                {
                    dragging = _pinDragged = true;
                    KeepOpen = true;
                    button.Opacity = 0.3;
                    var picture = e.IconKey == null ? null : e.IconIsFile ? AppShell.FileTypeIcon(e.IconKey) : AppShell.Icon(e.IconKey);
                    DragGhost.Show(picture, (int)Math.Round(S.StartIconSize * Scale), now.X, now.Y);
                }
                var target = dragging ? PinUnder(now, button) : default;
                // an app of a group let go on an empty place anywhere in Start (also under the group): out of the group,
                // right after it
                string group = dragging && target.Button == null ? StartPins.GroupOf(e.Key) : null;
                bool dropOut = group != null && Native.GetWindowRect(Hwnd, out var win) && now.X >= win.Left && now.X < win.Right && now.Y >= win.Top && now.Y < win.Bottom;
                if (dragging)
                {
                    DragGhost.Move(now.X, now.Y);
                    if (target.Button != null) _marker?.At(target.Button, target.Where, Rows(target.Button));
                    else if (dropOut && ShownPin(group) is { } groupButton) _marker?.At(groupButton, 1, Rows(groupButton));
                    else _marker?.Hide();
                }
                if (down) return;
                follow.Stop();
                if (!dragging) return;                                    // a click: the button opens the app itself
                DragGhost.Hide();
                _marker?.Hide();
                button.Opacity = 1;
                KeepOpen = false;
                string openGroup = StartPins.GroupOf(e.Key);
                if (target.Button?.Tag is StartEntry other)
                {
                    if (target.Where == 0)
                    {
                        if (StartPins.Group(e.Key, other.Key) is { } made) NameNewGroup(made);   // a new group: its name is asked at once
                    }
                    else StartPins.Move(e.Key, other.Key, after: target.Where > 0);
                }
                else if (dropOut) StartPins.RemoveFromGroup(e.Key);
                if (openGroup != null) RefreshOpenGroup(openGroup);
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _pinDragged = false);
            };
            follow.Start();
        }), true);
    }

    bool _pinDragged;
    readonly List<Button> _pinButtons = new();

    /// <summary>The pins are one under another (rows), not tiles side by side.</summary>
    static bool Rows(FrameworkElement b) => b.Parent is StackPanel;

    /// <summary>The button of a pin as it is shown now (null when it is not on the screen).</summary>
    Button ShownPin(string key) => _pinButtons.FirstOrDefault(b => b.IsLoaded && b.Tag is StartEntry x && x.Key == key);

    /// <summary>The group whose name is being written right in Start (null: none), and its box.</summary>
    string _naming;
    TextBox _nameBox;
    bool _tapEndedNaming;

    static bool Within(DependencyObject el, DependencyObject container)
    {
        for (; el != null; el = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(el))
            if (el == container) return true;
        return false;
    }

    /// <summary>Two apps just made a group: its name is written straight away in its place (Esc: it keeps "Group").</summary>
    void NameNewGroup(string id) => StartNaming(id);

    // after the pins are built again for the change itself (queued before): that build took the box away again
    void StartNaming(string id) => Ui.Post(() =>
    {
        _naming = id;
        BuildPinned();
    });

    /// <summary>A box for the name in place of the name: Enter or a click elsewhere keeps it, Esc leaves the old one.</summary>
    TextBox NameEditor(StartEntry e, double fontSize)
    {
        var box = new TextBox { Text = e.Name ?? "", FontSize = fontSize, MinWidth = 60, PlaceholderText = L.T("Name of the group"), HorizontalAlignment = HorizontalAlignment.Stretch };
        _nameBox = box;
        box.Loaded += (_, _) =>
        {
            Fg.Force(Hwnd);                                              // typing goes to Start
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        box.KeyDown += (_, a) =>
        {
            if (a.Key == Windows.System.VirtualKey.Enter) { a.Handled = true; EndNaming(box.Text); }
            else if (a.Key == Windows.System.VirtualKey.Escape) { a.Handled = true; EndNaming(null); }
        };
        box.LostFocus += (_, _) => EndNaming(box.Text);
        return box;
    }

    /// <summary>The name written (null: cancelled) – the group shows as before, with it.</summary>
    void EndNaming(string name)
    {
        if (_naming is not { } id) return;                               // already ended (Enter, then the box lost its focus)
        _naming = null;
        _nameBox = null;
        if (!string.IsNullOrWhiteSpace(name) && name.Trim() != StartEntry.FromKey(id).Name) StartPins.Rename(id, name);
        else Ui.Post(BuildPinned);
    }

    /// <summary>An opened group (its page, its card) shows its apps again after a change, or closes when it is gone.</summary>
    void RefreshOpenGroup(string id)
    {
        if (_layout == 1) return;                                        // two columns: the list of pins is built again anyway
        bool open = FullScreen ? _folderLayer?.Visibility == Visibility.Visible : _main != null && _main.Content != _home;
        if (!open) return;
        Ui.Post(() =>
        {
            if (StartPins.Keys.Contains(id)) OpenFolder(StartEntry.FromKey(id));
            else if (FullScreen) CloseFolder();
            else ShowHomePage();
        });
    }

    /// <summary>The pin under a screen point and where on it: −1 before, 1 after, 0 its middle (a group with it).</summary>
    (Button Button, int Where) PinUnder(Native.POINT p, Button dragged)
    {
        if (Content is not FrameworkElement root || root.XamlRoot == null) return default;
        if (!Native.GetWindowRect(Hwnd, out var win)) return default;
        double scale = root.XamlRoot.RasterizationScale;
        var at = new Windows.Foundation.Point((p.X - win.Left) / scale, (p.Y - win.Top) / scale);
        foreach (var b in _pinButtons)
        {
            if (b == dragged || !b.IsLoaded || b.XamlRoot == null || b.Tag is not StartEntry other) continue;
            Windows.Foundation.Rect r;
            try { r = b.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, b.ActualWidth, b.ActualHeight)); } catch { continue; }
            if (!r.Contains(at)) continue;
            double along = Rows(b) ? (at.Y - r.Y) / Math.Max(1, r.Height) : (at.X - r.X) / Math.Max(1, r.Width);
            var dragKey = (dragged.Tag as StartEntry)?.Key;
            int where = along is > 0.3 and < 0.7 && dragKey != null && !StartPins.IsGroup(dragKey) ? 0 : along >= 0.5 ? 1 : -1;
            return (b, where);
        }
        return default;
    }

    static bool AdminKeys
    {
        get
        {
            static bool Down(Windows.System.VirtualKey k) => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            return Down(Windows.System.VirtualKey.Control) && Down(Windows.System.VirtualKey.Shift);
        }
    }

    void Launch(StartEntry e, bool admin = false)
    {
        if (e.Kind == EntryKind.AppFolder) { OpenFolder(e); return; }   // a folder opens inside Start
        CloseNow();
        try { e.Run(admin); }
        catch (Exception ex) { Log.Write("Start launch " + e.Name, ex); }
    }

    /// <summary>Right-click menu of an app / file / result: open, as administrator, pin to Start / dock, file location; groups and own looks.</summary>
    MenuFlyout EntryMenu(StartEntry en, bool pinnedTile)
    {
        var menu = new MenuFlyout();
        KeepWhileOpen(menu);
        void Add(string text, Action click, bool bold = false)
        {
            var i = new MenuFlyoutItem { Text = text };
            if (bold) i.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            i.Click += (_, _) => click();
            menu.Items.Add(i);
        }
        Add(en.Kind == EntryKind.Calc ? L.T("Copy result") : L.T("Open"), () => Launch(en), bold: true);
        if (StartPins.IsGroup(en.Key))
        {
            Add(L.T("Rename…"), () => StartNaming(en.Key));               // written right in its place
            Add(L.T("Ungroup"), () => StartPins.Ungroup(en.Key));
            return menu;
        }
        if (en.IsApp)
        {
            if (en.CanElevate) Add(L.T("Run as administrator"), () => Launch(en, admin: true));
            menu.Items.Add(new MenuFlyoutSeparator());
            bool pinnedStart = StartPins.IsPinned(en.Key) || en.ExePath != null && StartPins.IsPinned(en.ExePath);
            if (pinnedStart)
            {
                if (pinnedTile && S.DockEditMode)
                {
                    // own name and picture of a pinned app (edit mode, like the dock)
                    StartPins.Own.TryGetValue(en.Key, out var own);
                    Add(L.T("Rename…"), async () => { if (await AskAsync(L.T("Rename"), L.T("Name in Start (empty = the app's name)"), en.Name) is { } name) StartPins.Rename(en.Key, name); });
                    Add(L.T("Change icon…"), async () =>
                    {
                        KeepOpen = true;
                        try { if (await OwnPicture.AskAsync(Hwnd) is { } file) { string path = OwnPicture.ImportIcon(file); if (OwnPicture.Load(path) != null) StartPins.SetIcon(en.Key, path); } }
                        catch (Exception ex) { Log.Write("Start icon", ex); }
                        KeepOpen = false;
                    });
                    if (own?.IconPath != null) Add(L.T("Original icon"), () => StartPins.SetIcon(en.Key, null));
                }
                Add(L.T("Unpin from Start"), () => { StartPins.Remove(en.Key); if (en.ExePath != null) StartPins.Remove(en.ExePath); });
            }
            else Add(L.T("Pin to Start"), () => StartPins.Add(en.Key));
            if (StartPins.GroupOf(en.Key) != null) Add(L.T("Remove from group"), () => StartPins.RemoveFromGroup(en.Key));
            if (App.Dock?.IsPinned(en.PinKey) == true) Add(L.T("Unpin from dock"), () => App.Dock?.Unpin(en.PinKey));
            else Add(L.T("Pin to dock"), () => App.Dock?.Pin(en.PinKey));
        }
        // files and folders can be pinned too
        if (!en.IsApp && en.Key != null && (Directory.Exists(en.Key) || File.Exists(en.Key)))
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            if (StartPins.IsPinned(en.Key)) Add(L.T("Unpin from Start"), () => StartPins.Remove(en.Key));
            else Add(L.T("Pin to Start"), () => StartPins.Add(en.Key));
        }
        if (en.LocationPath is { } location && (File.Exists(location) || Directory.Exists(location)))
        {
            if (!en.IsApp) menu.Items.Add(new MenuFlyoutSeparator());
            Add(L.T("Open file location"), () =>
            {
                CloseNow();
                if (en.IsApp) AppShell.ShowInExplorer(location);          // an app's install folder is opened, a file is selected in its folder
                else Fg.Open("explorer.exe", $"/select,\"{location}\"");
            });
        }
        return menu;
    }

    void KeepWhileOpen(FlyoutBase flyout)
    {
        flyout.Opening += (_, _) => flyout.AreOpenCloseAnimationsEnabled = Motion.On;
        flyout.Opened += (_, _) => KeepOpen = true;
        flyout.Closed += (_, _) => KeepOpen = false;
    }

    /// <summary>A name asked in a small window of its own (Start stays open meanwhile).</summary>
    async Task<string> AskAsync(string title, string label, string text)
    {
        KeepOpen = true;
        try { return await TextPrompt.AskAsync(title, label, text); }
        finally { KeepOpen = false; }
    }

    void PowerMenu(FrameworkElement at)
    {
        var menu = new MenuFlyout();
        KeepWhileOpen(menu);
        foreach (var group in StartSide.ShownPower())
        {
            if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
            foreach (var p in group) { var i = new MenuFlyoutItem { Text = L.T(p.Id) }; i.Click += (_, _) => { CloseNow(); p.Run(); }; menu.Items.Add(i); }
        }
        if (menu.Items.Count > 0) menu.ShowAt(at);
    }

    /// <summary>The account: the picture of Windows in a circle (or a profile symbol) and the full name; a click opens its settings.</summary>
    Button UserButton(double size)
    {
        var circle = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = Res("ControlFillColorDefaultBrush") };
        if (UserPicture() is { } picture) circle.Background = new ImageBrush { ImageSource = picture, Stretch = Stretch.UniformToFill };
        else circle.Child = new FontIcon { Glyph = "", FontSize = Math.Round(size * 0.45) };
        var name = new TextBlock { Text = UserName, VerticalAlignment = VerticalAlignment.Center, FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 };
        // a grid, not a row: in a row the name had all the width it wanted and was cut at the edge of the column instead of
        // going on to a second line
        var content = new Grid { ColumnSpacing = 11 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(name, 1);
        content.Children.Add(circle);
        content.Children.Add(name);
        var button = new Button { Style = DockButton, HorizontalAlignment = _layout == 1 ? HorizontalAlignment.Stretch : HorizontalAlignment.Left, HorizontalContentAlignment = _layout == 1 ? HorizontalAlignment.Stretch : HorizontalAlignment.Left, Padding = new Thickness(8),
            Content = content };
        ToolTipService.SetToolTip(button, L.T("Account settings"));
        button.Click += (_, _) => { CloseNow(); Fg.Open("ms-settings:yourinfo"); };
        return button;
    }

    [System.Runtime.InteropServices.DllImport("secur32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool GetUserNameEx(int nameFormat, System.Text.StringBuilder name, ref uint size);
    static string _userName;

    /// <summary>Full name of the account (falls back to the login name).</summary>
    static string UserName => _userName ??= ReadUserName();

    static string ReadUserName()
    {
        try
        {
            var sb = new System.Text.StringBuilder(256);
            uint size = (uint)sb.Capacity;
            if (GetUserNameEx(3, sb, ref size) && sb.Length > 0) return sb.ToString();     // NameDisplay
        }
        catch { }
        return Environment.UserName;
    }

    /// <summary>The account picture Windows shows in Start and Settings (its path is in the registry under the SID of the user).</summary>
    static ImageSource UserPicture()
    {
        try
        {
            string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\" + sid);
            foreach (var size in new[] { "Image192", "Image208", "Image240", "Image96", "Image448" })
                if (k?.GetValue(size) is string path && File.Exists(path)) return new BitmapImage(new Uri(path)) { DecodePixelWidth = 96 };
        }
        catch (Exception ex) { Log.Write("UserPicture", ex); }
        return null;
    }

    // ───────────────────────── search ─────────────────────────

    async void Search()
    {
        string q = _search.Text;
        _files?.Cancel();
        bool active = q.Trim().Length > 0;
        _resultsView.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (_main != null) _main.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        if (FullScreen && _home != null) _home.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        foreach (var c in _root.Children.OfType<Border>().Concat(_root.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Border>())))
            if (c.Tag as string == "switch") c.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        if (!active) { _local = new(); _found = new(); _results.Children.Clear(); return; }
        _local = SearchEngine.Local(q);
        _found = new();
        ShowResults();
        var cts = _files = new CancellationTokenSource();
        try
        {
            await Task.Delay(220, cts.Token);                         // files after a short pause in typing
            if (q.Trim().Length < 2) return;
            var files = await SearchEngine.FilesAsync(q.Trim(), cts.Token);
            if (cts.IsCancellationRequested || q != _search.Text) return;
            _found = files;
            ShowResults();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write("Search files", ex); }
    }

    List<StartEntry> ShownResults => _local.Concat(_found).ToList();

    void ShowResults()
    {
        var results = ShownResults;
        _results.Children.Clear();
        string group = null;
        foreach (var r in results)
        {
            if (r.Group != group) { group = r.Group; if (group != null) _results.Children.Add(Heading(group)); }
            _results.Children.Add(Row(r));
        }
        if (results.Count == 0) _results.Children.Add(new TextBlock { Text = L.T("No results"), Margin = new Thickness(8), Foreground = Res("TextFillColorSecondaryBrush") });
        _selected = Math.Clamp(_selected, 0, Math.Max(0, results.Count - 1));
        Highlight();
    }

    void Highlight()
    {
        var rows = _results.Children.OfType<Button>().ToList();
        for (int i = 0; i < rows.Count; i++)
            rows[i].Background = i == _selected ? Res("SubtleFillColorSecondaryBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        if (_selected < rows.Count) rows[_selected].StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    void Move(int delta)
    {
        int count = _results.Children.OfType<Button>().Count();
        if (count == 0 || _resultsView.Visibility != Visibility.Visible) return;
        _selected = Math.Clamp(_selected + delta, 0, count - 1);
        Highlight();
    }

    void Search_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Down: Move(1); e.Handled = true; break;
            case Windows.System.VirtualKey.Up: Move(-1); e.Handled = true; break;
            case Windows.System.VirtualKey.PageDown: Move(6); e.Handled = true; break;
            case Windows.System.VirtualKey.PageUp: Move(-6); e.Handled = true; break;
            case Windows.System.VirtualKey.Tab when _resultsView.Visibility == Visibility.Visible: Move(shift ? -1 : 1); e.Handled = true; break;
            case Windows.System.VirtualKey.Enter:
                if (_results.Children.OfType<Button>().ElementAtOrDefault(_selected)?.Tag is StartEntry en) { e.Handled = true; Launch(en, AdminKeys); }
                break;
        }
    }
}
