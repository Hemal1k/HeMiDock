using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace HeMiDock;

/// <summary>
/// The guide of the first start (later from Settings → General): a ready-made look or own choices, the basic options and an
/// overview of what the app can do. Everything applies at once on the dock behind it.
/// </summary>
sealed class SetupWindow : Window
{
    static SetupWindow _open;

    /// <summary>Opens the guide (or brings it to the front); <paramref name="closed"/> runs when it is closed.</summary>
    public static void Open(Action closed = null)
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new SetupWindow();
        _open.Closed += (_, _) => { _open = null; closed?.Invoke(); };
        _open.Activate();
    }

    readonly ContentControl _page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly Button _back, _next, _skip;
    readonly TextBlock _title = new() { FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(28, 24, 28, 12) };
    readonly TextBlock _steps = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly List<(Border Card, int Index)> _cards = new();
    int _step, _preset = -1;

    static DockSettings S => DockSettings.Current;
    static Brush Res(string key) => ThemeBrush.Get(key);

    SetupWindow()
    {
        Title = L.T("Setup guide");
        SystemBackdrop = new MicaBackdrop();
        // 860 × 680 points on the main monitor (it was a fixed size in pixels: too big at 100 %), at most 90 % of it
        var screen = Monitors.Primary();
        double scale = Native.MonitorDpi(screen.Handle) ?? 1;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min((int)(860 * scale), (screen.Work.Right - screen.Work.Left) * 9 / 10), Math.Min((int)(680 * scale), (screen.Work.Bottom - screen.Work.Top) * 9 / 10)));
        try { AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "HeMiDock.ico")); } catch { }
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p) p.IsAlwaysOnTop = true;
        // in the middle of the main monitor
        var work = Monitors.Primary().Work;
        AppWindow.Move(new Windows.Graphics.PointInt32(work.Left + (work.Right - work.Left - AppWindow.Size.Width) / 2, work.Top + (work.Bottom - work.Top - AppWindow.Size.Height) / 2));

        SettingsBackup.Save("setup guide");
        _back = new Button { Content = L.T("Back"), MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
        _next = new Button { Content = L.T("Next"), MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        _skip = new Button { Content = L.T("Skip"), MinWidth = 96 };
        _back.Click += (_, _) => Go(_step - 1);
        _next.Click += (_, _) => { if (_step >= 3) Finish(); else Go(_step + 1); };
        _skip.Click += (_, _) => Finish();
        Closed += (_, _) => { if (!S.SetupDone) S.SetupDone = true; S.Save(); };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _skip, _back, _next } };
        var bar = new Border { Padding = new Thickness(24, 14, 24, 14), Background = Res("LayerFillColorDefaultBrush"), Child = new Grid { Children = { _steps, buttons } } };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _page, Padding = new Thickness(28, 0, 28, 16) };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_title);
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Grid.SetRow(bar, 2);
        root.Children.Add(bar);
        Content = root;
        ThemeWatch.Track(this);
        Go(0);
    }

    void Finish()
    {
        S.SetupDone = true;
        Close();
    }

    void Go(int step)
    {
        _step = Math.Clamp(step, 0, 3);
        _back.Visibility = _step == 0 ? Visibility.Collapsed : Visibility.Visible;
        _skip.Visibility = _step == 3 ? Visibility.Collapsed : Visibility.Visible;
        _next.Content = L.T(_step == 3 ? "Finish" : "Next");
        _next.IsEnabled = _step != 1 || _preset >= 0;                 // a look must be chosen first
        _steps.Text = L.T("Step {0} of {1}", _step + 1, 4);
        _steps.Foreground = Res("TextFillColorSecondaryBrush");
        (_title.Text, _page.Content) = _step switch
        {
            0 => (L.T("Welcome to HeMiDock"), Welcome()),
            1 => (L.T("Choose a look"), LooksPage()),
            2 => (L.T("Basic options"), Options()),
            _ => (L.T("What HeMiDock can do"), Tour())
        };
    }

    static TextBlock Text(string text, double size = 13.5, bool dim = false, double after = 10) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, after), LineHeight = size * 1.4,
        Foreground = Res(dim ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush")
    };

    static Border Card(UIElement child, Thickness padding, Thickness margin) => new()
    {
        CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = padding, Margin = margin, Child = child,
        Background = Res("CardBackgroundFillColorDefaultBrush"), BorderBrush = Res("CardStrokeColorDefaultBrush")
    };

    static Grid Columns(int count, IEnumerable<FrameworkElement> items)
    {
        var grid = new Grid();
        for (int c = 0; c < count; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        int i = 0;
        foreach (var item in items)
        {
            if (i % count == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(item, i / count);
            Grid.SetColumn(item, i % count);
            grid.Children.Add(item);
            i++;
        }
        return grid;
    }

    FrameworkElement Welcome()
    {
        var logo = new Image { Width = 64, Height = 64, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Top };
        try { logo.Source = new BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "HeMiDock.png"))); } catch { }
        var intro = new Grid { Margin = new Thickness(0, 4, 0, 16) };
        intro.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        intro.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        intro.Children.Add(logo);
        var sentence = Text(L.T("The app replaces the Windows taskbar with its own panel with the Start menu, search and control center."), after: 0);
        sentence.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(sentence, 1);
        intro.Children.Add(sentence);

        FrameworkElement Feature(string glyph, string title, string text)
        {
            var head = Text(L.T(title), 13.5, after: 2);
            head.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            var card = Card(new StackPanel { Children = { new FontIcon { Glyph = glyph, FontSize = 22, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left, Foreground = Res("AccentTextFillColorPrimaryBrush") }, head, Text(L.T(text), 12, dim: true, after: 0) } },
                new Thickness(14, 12, 14, 12), new Thickness(0, 0, 8, 8));
            card.VerticalAlignment = VerticalAlignment.Stretch;
            return card;
        }
        // six things the app does, so the first step is not empty
        var next = Text(L.T("In the next steps you choose a look and the basic options – everything can be changed later in the settings."), 12.5, dim: true, after: 0);
        next.Margin = new Thickness(0, 8, 0, 0);
        return new StackPanel
        {
            Children =
            {
                intro,
                Columns(3, new[]
                {
                    Feature("", "Your own dock", "Pinned apps, open windows, the notification area and the clock."),
                    Feature("", "Start menu", "Your apps, groups and recent files in three looks."),
                    Feature("", "Quick search (Alt + Space)", "Apps, files, settings and calculations."),
                    Feature("", "Control center", "Wi-Fi, Bluetooth, sound, brightness and power mode."),
                    Feature("", "Top bar", "Menus, widgets and the status icons along the top edge."),
                    Feature("", "Window zones", "Drag a window with Shift held into a part of the screen.")
                }),
                next
            }
        };
    }

    // ───────────────────────── looks ─────────────────────────

    // the looks as cards only: the chosen one has the frame, the dock behind the guide shows it at once (the big picture
    // of the chosen look above the cards was taken away)
    FrameworkElement LooksPage()
    {
        _cards.Clear();
        return Columns(3, Enumerable.Range(0, Looks.Presets.Length).Select(LookCard));
    }

    FrameworkElement LookCard(int index)
    {
        var p = Looks.Presets[index];
        var name = Text(L.T(p.Name), 13.5, after: 0);
        name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var card = Card(new StackPanel { Children = { Picture(p.Bar, p.Full, p.Start, 0, p.Bar, p.Set == null), name } }, new Thickness(10), new Thickness(0, 0, 10, 10));
        card.BorderThickness = new Thickness(2);
        _cards.Add((card, index));
        Paint();
        card.Tapped += (_, _) =>
        {
            _preset = index;
            _next.IsEnabled = true;
            p.Set?.Invoke(S);                                            // at once on the dock behind the guide
            Paint();
        };
        return card;
    }

    void Paint()
    {
        foreach (var (card, i) in _cards) card.BorderBrush = Res(_preset == i ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush");
    }

    /// <summary>
    /// A little picture of the screen with the look, as the app really lays it out: the dock at its edge (full width with the
    /// icons on the left or in the middle, or as a dock), its icons with the Start button first, the clock at the end of the
    /// dock or in the top bar, and Start opened above its button (full screen over everything).
    /// </summary>
    static FrameworkElement Picture(bool bar, int full, int start, int edge, bool clockOnBar, bool custom, double W = 190, double H = 104, string caption = null)
    {
        var c = new Canvas { Width = W, Height = H, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        c.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, W, H) };
        double k = W / 190;                                              // the sizes below are for the small picture
        // a little screen in fixed colours (a blue desktop, a dark dock, light icons): drawn in the colours of the theme, its
        // parts ran together in the light mode
        static Brush Paint(string brush) => brush switch
        {
            "ControlFillColorSecondaryBrush" => Solid(0xFF, 0x4A, 0x78, 0xA8),       // the desktop
            "ControlStrongFillColorDefaultBrush" => Solid(0xE6, 0x1E, 0x1E, 0x22),   // the dock and the top bar
            "TextFillColorSecondaryBrush" => Solid(0xD9, 0xFF, 0xFF, 0xFF),          // icons, the clock
            "ControlFillColorTertiaryBrush" => Solid(0xF0, 0x2C, 0x2C, 0x32),        // Start
            "TextFillColorTertiaryBrush" => Solid(0x8C, 0xFF, 0xFF, 0xFF),           // inside Start
            _ => Res(brush)                                                          // the accent of the Start button
        };
        static Brush Solid(byte a, byte r, byte g, byte b) => new SolidColorBrush(global::Windows.UI.Color.FromArgb(a, r, g, b));
        void Box(double x, double y, double w, double h, string brush, double r = 0)
        {
            var e = new Rectangle { Width = Math.Max(0, w), Height = Math.Max(0, h), RadiusX = r, RadiusY = r, Fill = Paint(brush) };
            Canvas.SetLeft(e, x);
            Canvas.SetTop(e, y);
            c.Children.Add(e);
        }
        Box(0, 0, W, H, "ControlFillColorSecondaryBrush", 6 * k);
        if (custom)
        {
            var q = new FontIcon { Glyph = "", FontSize = 30 * k, Foreground = Paint("TextFillColorSecondaryBrush") };
            Canvas.SetLeft(q, W / 2 - 15 * k);
            Canvas.SetTop(q, H / 2 - 15 * k);
            c.Children.Add(q);
            return c;
        }
        double barH = bar ? 8 * k : 0;
        if (bar)
        {
            Box(0, 0, W, barH, "ControlStrongFillColorDefaultBrush");
            Box(4 * k, 2.5 * k, 3 * k, 3 * k, "TextFillColorSecondaryBrush", 1);                       // the menu
            if (clockOnBar) Box(W - 18 * k, 2.5 * k, 14 * k, 3 * k, "TextFillColorSecondaryBrush", 1);  // the clock
        }
        bool vertical = edge is 1 or 2;
        const int icons = 5;
        double size = 6 * k, gap = 3 * k, thick = (full != 0 ? 12 : 11) * k, margin = full != 0 ? 0 : 4 * k;
        bool clockInDock = !clockOnBar;
        double clockLen = clockInDock ? 20 * k : 0;
        double rowLen = icons * size + (icons - 1) * gap;
        double dockLen = full != 0 ? (vertical ? H - barH : W) : rowLen + 12 * k + (clockInDock ? clockLen + 8 * k : 0);
        // the dock and the place of its first icon (the Start button)
        double dx, dy, dw, dh, sx, sy;
        if (!vertical)
        {
            dw = dockLen; dh = thick;
            dx = full != 0 ? 0 : (W - dw) / 2;
            dy = edge == 3 ? barH + margin : H - dh - margin;
            sx = full == 1 ? dx + 6 * k : full == 2 ? (W - rowLen) / 2 : dx + 6 * k;
            sy = dy + (dh - size) / 2;
        }
        else
        {
            dw = thick; dh = full != 0 ? H - barH : dockLen;
            dy = full != 0 ? barH : barH + (H - barH - dh) / 2;
            dx = edge == 1 ? margin : W - dw - margin;
            sy = full == 1 ? dy + 6 * k : full == 2 ? barH + (H - barH - rowLen) / 2 : dy + 6 * k;
            sx = dx + (dw - size) / 2;
        }
        Box(dx, dy, dw, dh, "ControlStrongFillColorDefaultBrush", full != 0 ? 0 : 3 * k);
        for (int i = 0; i < icons; i++)
        {
            double along = i * (size + gap);
            Box(vertical ? sx : sx + along, vertical ? sy + along : sy, size, size, i == 0 ? "AccentFillColorDefaultBrush" : "TextFillColorSecondaryBrush", 1);
        }
        if (clockInDock)
        {
            if (!vertical) Box(dx + dw - clockLen - 4 * k, dy + (dh - 6 * k) / 2, clockLen, 6 * k, "TextFillColorSecondaryBrush", 1);
            else Box(dx + (dw - 6 * k) / 2, dy + dh - 14 * k, 6 * k, 10 * k, "TextFillColorSecondaryBrush", 1);
        }
        // Start, opened at its button (full screen: over the whole screen)
        double top = barH + 4 * k;
        if (start == 2) Box(8 * k, top + 2 * k, W - 16 * k, (edge == 0 ? dy : H) - top - 8 * k, "ControlFillColorTertiaryBrush", 3 * k);
        else
        {
            double sw = (start == 1 ? 64 : 70) * k, sh = 52 * k;
            double x, y;
            if (!vertical)
            {
                x = Math.Clamp(sx + size / 2 - sw / 2, 4 * k, W - sw - 4 * k);
                y = edge == 3 ? dy + dh + 4 * k : dy - sh - 4 * k;
            }
            else
            {
                x = edge == 1 ? dx + dw + 4 * k : dx - sw - 4 * k;
                y = top;
            }
            Box(x, y, sw, sh, "ControlFillColorTertiaryBrush", 3 * k);
            if (start == 1) Box(x + sw * 0.55, y + 4 * k, 1, sh - 8 * k, "TextFillColorTertiaryBrush");   // two columns
            else for (int i = 0; i < 6; i++) Box(x + 8 * k + i % 3 * 18 * k, y + 8 * k + i / 3 * 14 * k, 9 * k, 9 * k, "TextFillColorTertiaryBrush", 2 * k);   // the grid of apps
        }
        if (caption != null)
        {
            var label = new TextBlock { Text = caption, FontSize = 12, Foreground = Paint("TextFillColorSecondaryBrush") };
            Canvas.SetLeft(label, 10);
            Canvas.SetTop(label, barH + 6);
            c.Children.Add(label);
        }
        return c;
    }

    // ───────────────────────── options ─────────────────────────

    static PropertyInfo Prop(string name) => typeof(DockSettings).GetProperty(name);

    static Border Row(string label, string note, FrameworkElement control)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), Children = { Text(L.T(label), 13, after: 0) } };
        if (note != null) text.Children.Add(Text(L.T(note), 11.5, dim: true, after: 0));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        var card = Card(grid, new Thickness(14, 9, 14, 9), new Thickness(0, 0, 0, 3));
        card.CornerRadius = new CornerRadius(6);
        return card;
    }

    static Border Toggle(string label, string note, string property)
    {
        var prop = Prop(property);
        var t = new ToggleSwitch { IsOn = (bool)prop.GetValue(S), OnContent = "", OffContent = "", MinWidth = 0 };
        t.Toggled += (_, _) => prop.SetValue(S, t.IsOn);
        return Row(label, note, t);
    }

    static Border Choice(string label, string property, params string[] options)
    {
        var prop = Prop(property);
        var box = new ComboBox { ItemsSource = options.Select(o => L.T(o)).ToArray(), MinWidth = 210, SelectedIndex = (int)prop.GetValue(S) };
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) prop.SetValue(S, box.SelectedIndex); };
        return Row(label, null, box);
    }

    static Border Slide(string label, string property, double min, double max)
    {
        var prop = Prop(property);
        var s = new Slider { Minimum = min, Maximum = max, Width = 210, Value = (double)prop.GetValue(S) };
        s.ValueChanged += (_, e) => prop.SetValue(S, e.NewValue);
        return Row(label, null, s);
    }

    static TextBlock Section(string text)
    {
        var t = Text(L.T(text), 13, after: 6);
        t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        t.Margin = new Thickness(0, 10, 0, 6);
        return t;
    }

    FrameworkElement Options()
    {
        var p = new StackPanel
        {
            Children =
            {
                Toggle("Start at sign-in", null, nameof(DockSettings.StartAtSignIn)),
                Choice("App theme", nameof(DockSettings.ThemeMode), "Automatic", "Light", "Dark"),
                Choice("Color palette", nameof(DockSettings.AccentMode), "Automatic", "Custom"),
                Choice("Transparency", nameof(DockSettings.GlassMode), "Automatic", "On", "Off"),
                Slide("Transparency intensity", nameof(DockSettings.Transparency), 0, 90),
                Toggle("Highlight of windows and the dock", null, nameof(DockSettings.DockOutline)),
                Choice("Animation speed", nameof(DockSettings.AnimationSpeed), "Slow", "Normal", "Fast", "Off"),
            }
        };
        p.Children.Add(Choice("Lighter drawing", nameof(DockSettings.LowGraphicsChoice), "Off", "On", "With a low battery"));

        // the look in detail – always open for "Custom", otherwise the preset is just a starting point
        p.Children.Add(Section("Look in detail"));
        p.Children.Add(Toggle("Top bar", null, nameof(DockSettings.TopBar)));
        p.Children.Add(Choice("Position on screen", nameof(DockSettings.DockPosition), "Bottom", "Left", "Right", "Top"));
        p.Children.Add(Choice("Dock width", nameof(DockSettings.DockFull), "Automatic, dock mode", "Full width, icons on the left", "Full width, icons in the middle"));
        p.Children.Add(Choice("Animation under the mouse", nameof(DockSettings.HoverEffect), "Magnification", "Highlight", "Magnification and highlight"));
        p.Children.Add(Choice("Start menu", nameof(DockSettings.StartLayout), "Modern", "Two columns", "Full screen"));
        p.Children.Add(Choice("Notification position", nameof(DockSettings.ToastPosition), "Automatic", "At the clock", "Top right", "Top left", "Bottom right"));
        p.Children.Add(Toggle("System monitor", null, nameof(DockSettings.ShowSystemMonitor)));
        p.Children.Add(Toggle("Recycle Bin", null, nameof(DockSettings.ShowRecycleBin)));
        p.Children.Add(Section("Window manager"));
        p.Children.Add(Toggle("Window zones", "Drag a window with the chosen key held and drop it into a part of the screen", nameof(DockSettings.ZonesOn)));
        p.Children.Add(Choice("Key for dragging into zones", nameof(DockSettings.ZoneKey), "Shift", "Ctrl", "Alt"));
        p.Children.Add(Choice("Zone layout", nameof(DockSettings.ZoneLayoutChoice), DockSettings.Current.ZoneLayoutChoices()));
        return p;
    }

    // ───────────────────────── overview ─────────────────────────

    static FrameworkElement Tour()
    {
        Border Item(string glyph, string title, string text)
        {
            var head = Text(L.T(title), 13, after: 1);
            head.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            var row = new Grid { ColumnSpacing = 14 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 20, Width = 26, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0), Foreground = Res("AccentTextFillColorPrimaryBrush") });
            var texts = new StackPanel { Children = { head, Text(L.T(text), 12, dim: true, after: 0) } };
            Grid.SetColumn(texts, 1);
            row.Children.Add(texts);
            var card = Card(row, new Thickness(14, 9, 14, 9), new Thickness(0, 0, 0, 3));
            card.CornerRadius = new CornerRadius(6);
            return card;
        }
        return new StackPanel
        {
            Children =
            {
                Item("", "Quick search – Alt + Space", "Apps, files, settings, calculations and conversions like 100 EUR in CZK. Paths like %appdata% open in Explorer."),
                Item("", "Start – the Win key", "Drag apps to change their order, drop one on another to make a group, or on an empty place to remove it. Win + S starts searching, Win + X opens the system menu."),
                Item("", "Advanced editing – Settings → General", "Move the parts of the dock and the top bar and change their size with the mouse. Rename apps in Start and change their icons."),
                Item("", "Icons in the dock", "Right click: windows, recent files, pinning. The mouse wheel switches between the windows of an app."),
                Item("", "Control center and calendar", "A click on the status icons or the clock. Wi-Fi, sound, Do not disturb, VPN and notifications; the calendar can show Outlook appointments."),
                Item("", "Keyboard shortcuts", "Settings → Keyboard shortcuts: your own keys for the control center, calendar, mute, Do not disturb and more."),
                Item("", "Everything can be changed", "Settings: look of the dock, top bar, status icons, Start and search. Settings can be exported to another PC together with pinned apps."),
            }
        };
    }
}
