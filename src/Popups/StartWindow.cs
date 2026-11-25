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
sealed partial class StartWindow : PopupWindow
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
        if (_main != null && !ReferenceEquals(_main.Content, _home)) { ShowHomePage(); return true; }   // letters, folder, all apps → back
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
        _folderLayer.Tapped += (_, e) => { if (ReferenceEquals(e.OriginalSource, _folderLayer) && !_tapEndedNaming) CloseFolder(); e.Handled = true; };
        _root.Children.Add(_folderLayer);
        // a click on empty space closes the whole Start
        _root.Tapped += (_, e) => { if (e.OriginalSource is Grid or ScrollViewer or StackPanel && !ReferenceEquals(e.OriginalSource, _folderLayer) && !_tapEndedNaming) CloseNow(); };
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
}
