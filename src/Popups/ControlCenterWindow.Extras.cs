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
    // ───────────────────────── monitors, keep awake, cast ─────────────────────────

    void ShowMonitors()
    {
        ShowPage("display", "Monitors");
        Footer("Display settings", "ms-settings:display");
        var card = PageCard();
        string now = DisplayTopology.Current();
        foreach (var (glyph, name, arg) in new[] { ("", "PC screen only", "/internal"), ("", "Duplicate", "/clone"), ("", "Extend", "/extend"), ("", "Second screen only", "/external") })
            card.Children.Add(PageRow(glyph, name, arg == now, async () =>
            {
                Fg.Run("DisplaySwitch.exe", arg);
                await Task.Delay(2500);                                // the screens need a moment to switch
                if (_openPage == "display") ShowMonitors();
            }));
    }

    static string AwakeText() => KeepAwake.ShutdownAt is { } at
        ? L.T("Shut down in {0}", (at > DateTime.Now ? at - DateTime.Now : TimeSpan.Zero).ToString(@"h\:mm\:ss"))
        : L.T(KeepAwake.IsOn ? "On" : "Off");

    void ShowAwake() => BuildAwake(false);

    void BuildAwake(bool custom)
    {
        ShowPage("awake", "Scheduled shutdown");
        PageCard().Children.Add(PageRow("", KeepAwake.IsOn ? "Sleep is blocked – allow it" : "Block sleep", KeepAwake.IsOn, () => { KeepAwake.Set(!KeepAwake.IsOn); BuildAwake(false); }));
        var card = PageCard(KeepAwake.ShutdownAt is { } at ? L.T("Shut down at {0}", at.ToString("t")) : L.T("Shut down the PC in"));
        if (KeepAwake.ShutdownAt != null)
        {
            card.Children.Add(PageRow("", "Cancel the shutdown", false, () => { KeepAwake.CancelShutdown(); BuildAwake(false); }));
            return;
        }
        foreach (var (name, time) in new[] { ("30 minutes", TimeSpan.FromMinutes(30)), ("1 hour", TimeSpan.FromHours(1)), ("2 hours", TimeSpan.FromHours(2)), ("4 hours", TimeSpan.FromHours(4)) })
            card.Children.Add(PageRow("", name, false, () => { KeepAwake.ShutdownIn(time); BuildAwake(false); }));
        if (!custom) { card.Children.Add(PageRow("", "Custom interval", false, () => BuildAwake(true))); return; }
        // an own interval: 5 minutes to 12 hours by 5 minutes
        var value = new TextBlock { Width = 80, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var slider = new Slider { Minimum = 5, Maximum = 720, StepFrequency = 5, SnapsTo = SliderSnapsTo.StepValues, Value = 60, IsThumbToolTipEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        void Show() { var t = TimeSpan.FromMinutes(slider.Value); value.Text = t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{t.Minutes} min"; }
        slider.ValueChanged += (_, _) => Show();
        Show();
        var line = new Grid { Margin = new Thickness(8, 6, 8, 0), ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        line.Children.Add(slider);
        Grid.SetColumn(value, 1);
        line.Children.Add(value);
        var ok = new Button { Content = L.T("Confirm"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 8, 8, 6) };
        ok.Click += (_, _) => { KeepAwake.ShutdownIn(TimeSpan.FromMinutes(slider.Value)); BuildAwake(false); };
        card.Children.Add(line);
        card.Children.Add(ok);
    }

    async Task ShowCastAsync()
    {
        ShowPage("cast", "Cast");
        Searching("Searching for wireless displays…");                   // Windows takes a few seconds: not an empty page meanwhile
        try
        {
            var found = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(Windows.UI.ViewManagement.ProjectionManager.GetDeviceSelector());
            if (_openPage != "cast") return;
            _page.Children.Clear();
            var all = found.Where(x => !x.Name.StartsWith("Generic Monitor", StringComparison.OrdinalIgnoreCase)).ToList();   // the selector also returns the connected monitors
            if (all.Count > 0) { var card = PageCard(); foreach (var d in all) card.Children.Add(PageRow("", d.Name, false, () => { CloseNow(); AppShell.Tap(0x5B, 0x4B); })); }
            else Note("No wireless displays were found.");
        }
        catch (Exception ex) { Log.Write("Cast list", ex); _page.Children.Clear(); }
        if (_openPage != "cast") return;
        PageCard().Children.Add(PageRow("", "Connect through Windows", false, () => { CloseNow(); AppShell.Tap(0x5B, 0x4B); }));   // Win+K: only Windows can connect them
    }

    // ───────────────────────── display, power, media, battery ─────────────────────────

    async Task AddBrightnessAsync(Border holder)
    {
        var (level, auto, autoOn) = await Task.Run(() => { bool s = AutoBrightness.Supported; return (DisplayBrightness.Get(), s, s && AutoBrightness.IsOn); });
        if (level is not int l && !auto) return;
        var panel = new StackPanel { Spacing = 2 };
        var head = new Grid { Children = { Title(L.T("Display")) } };
        if (auto)
        {
            var sw = new ToggleSwitch { IsOn = autoOn, OnContent = L.T("Automatic"), OffContent = L.T("Automatic"), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 0 };
            sw.Toggled += (_, _) => { bool on = sw.IsOn; _ = Task.Run(() => AutoBrightness.Set(on)); };
            head.Children.Add(sw);
        }
        panel.Children.Add(head);
        if (level is int value)
        {
            var apply = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            Slider slider = null;
            apply.Tick += (_, _) => { apply.Stop(); int v = (int)slider.Value; _ = Task.Run(() => DisplayBrightness.Set(v)); };   // the monitor is slow: after the slider settles
            (slider, var text) = SliderRow(panel, "", _ => { apply.Stop(); apply.Start(); }, null);
            _updating = true;
            slider.Value = value;
            text.Text = value.ToString();
            _updating = false;
        }
        if (_openPage != null) return;
        holder.Child = _editing ? Editable(Card(panel), "brightness", tile: false) : Card(panel);
        Place();
    }

    FrameworkElement PowerCard()
    {
        var row = new Grid { ColumnSpacing = 6, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition(), new ColumnDefinition() } };
        var now = PowerMode.Current;
        int i = 0;
        foreach (var (glyph, name, mode) in new[] { ("", "Efficiency", PowerMode.Efficiency), ("", "Balanced", PowerMode.Balanced), ("", "Performance", PowerMode.Performance) })
        {
            var b = new ToggleButton { IsChecked = now == mode, HorizontalAlignment = HorizontalAlignment.Stretch, Content = new StackPanel { Spacing = 4, Children = { new FontIcon { Glyph = glyph, FontSize = 15 }, new TextBlock { Text = L.T(name), FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center } } } };
            b.Click += (_, _) => { PowerMode.Set(mode); BuildMain(); };
            Grid.SetColumn(b, i++);
            row.Children.Add(b);
        }
        return Card(new StackPanel { Spacing = 8, Children = { Title(L.T("Power mode")), row } });
    }

    FrameworkElement BatteryCard((int Percent, bool Plugged, bool Charging, bool Saver, int SecondsLeft) b)
    {
        string state = b.Plugged ? (b.Percent >= 100 ? L.T("Fully charged") : L.T("Charging"))
            : b.SecondsLeft > 0 ? L.T("{0} h {1} min remaining", b.SecondsLeft / 3600, b.SecondsLeft / 60 % 60) : L.T("On battery");
        var saver = new ToggleButton { IsChecked = BatterySaver.IsOn == true, IsEnabled = BatterySaver.IsOn != null, HorizontalAlignment = HorizontalAlignment.Right,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { new FontIcon { Glyph = "", FontSize = 13 }, new TextBlock { Text = L.T("Battery saver"), FontSize = 11.5 } } } };
        ToolTipService.SetToolTip(saver, BatterySaver.IsOn == null ? L.T("Battery saver works only on battery") : L.T(BatterySaver.IsOn == true ? "Battery saver: on" : "Battery saver: off"));
        saver.Click += async (_, _) => { if (BatterySaver.IsOn is bool on) { saver.IsEnabled = false; await Task.Run(() => BatterySaver.Set(!on)); await Task.Delay(400); BuildMain(); } };
        var head = new Grid { Children = { Title(L.T("Battery")) } };
        var settings = new Button { Style = DockButton, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(6, 2, 6, 2), Content = new FontIcon { Glyph = "", FontSize = 12 } };
        settings.Click += (_, _) => Open("ms-settings:powersleep");
        head.Children.Add(settings);
        var row = new Grid();
        row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children =
        {
            Battery(b.Percent, b.Plugged),
            new StackPanel { Children = { new TextBlock { Text = b.Percent + " %", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, new TextBlock { Text = state, FontSize = 11, Foreground = Res("TextFillColorSecondaryBrush") } } }
        } });
        row.Children.Add(saver);
        return Card(new StackPanel { Spacing = 6, Children = { head, row } });
    }

    /// <summary>
    /// The battery and the power mode in one card (a notebook shows both): the battery with its state and the saver on top,
    /// the three modes in one row under it - one card instead of two, without their titles (the user's wish, 2026-11-23).
    /// </summary>
    FrameworkElement PowerBatteryCard((int Percent, bool Plugged, bool Charging, bool Saver, int SecondsLeft) b)
    {
        string state = b.Plugged ? (b.Percent >= 100 ? L.T("Fully charged") : L.T("Charging"))
            : b.SecondsLeft > 0 ? L.T("{0} h {1} min remaining", b.SecondsLeft / 3600, b.SecondsLeft / 60 % 60) : L.T("On battery");
        var head = new Grid { ColumnSpacing = 6, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto } } };
        head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            Battery(b.Percent, b.Plugged),
            new StackPanel { Children = { new TextBlock { Text = b.Percent + " %", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, new TextBlock { Text = state, FontSize = 11, Foreground = Res("TextFillColorSecondaryBrush") } } }
        } });
        var saver = new ToggleButton { IsChecked = BatterySaver.IsOn == true, IsEnabled = BatterySaver.IsOn != null, Padding = new Thickness(8, 6, 8, 6), VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = "\uEBB4", FontSize = 14 } };
        ToolTipService.SetToolTip(saver, BatterySaver.IsOn == null ? L.T("Battery saver works only on battery") : L.T(BatterySaver.IsOn == true ? "Battery saver: on" : "Battery saver: off"));
        saver.Click += async (_, _) => { if (BatterySaver.IsOn is bool on) { saver.IsEnabled = false; await Task.Run(() => BatterySaver.Set(!on)); await Task.Delay(400); BuildMain(); } };
        Grid.SetColumn(saver, 1);
        head.Children.Add(saver);
        var settings = new Button { Style = DockButton, Padding = new Thickness(6, 4, 6, 4), VerticalAlignment = VerticalAlignment.Center, Content = new FontIcon { Glyph = "\uE9E9", FontSize = 12 } };
        ToolTipService.SetToolTip(settings, L.T("Power & battery"));
        settings.Click += (_, _) => Open("ms-settings:powersleep");
        Grid.SetColumn(settings, 2);
        head.Children.Add(settings);
        var panel = new StackPanel { Spacing = 10, Children = { head } };
        if (PowerMode.Current is { } now)
        {
            var row = new Grid { ColumnSpacing = 6, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition(), new ColumnDefinition() } };
            int i = 0;
            foreach (var (glyph, name, mode) in new[] { ("\uEC0A", "Efficiency", PowerMode.Efficiency), ("\uE945", "Balanced", PowerMode.Balanced), ("\uEC4A", "Performance", PowerMode.Performance) })
            {
                var m = new ToggleButton { IsChecked = now == mode, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4, 6, 4, 6),
                    Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = glyph, FontSize = 13 }, new TextBlock { Text = L.T(name), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis } } } };
                ToolTipService.SetToolTip(m, L.T("Power mode") + ": " + L.T(name));
                m.Click += (_, _) => { PowerMode.Set(mode); BuildMain(); };
                Grid.SetColumn(m, i++);
                row.Children.Add(m);
            }
            panel.Children.Add(row);
        }
        return Card(panel);
    }

    static BatteryIcon Battery(int percent, bool plugged) { var i = new BatteryIcon { Size = 20 }; i.Show(percent, plugged); return i; }

    FrameworkElement MediaCard()
    {
        var (title, playing) = BarMedia.Now;
        var media = BarMedia.Details;
        Button B(string glyph, Func<Task> click) { var b = new Button { Style = DockButton, Padding = new Thickness(10, 7, 10, 7), Content = new FontIcon { Glyph = glyph, FontSize = 16 } }; b.Click += async (_, _) => { await click(); await Task.Delay(300); BuildMain(); }; return b; }
        // like the player of a locked phone: a small picture of what plays (cover, frame of the video – else the icon of the
        // app), beside it the title and the artist, the icon of the app in the corner; the buttons in one row under them
        const double Pic = 52;
        var picture = new Border { Width = Pic, Height = Pic, CornerRadius = new CornerRadius(6), Background = Res("ControlFillColorDefaultBrush") };
        if (media?.Picture is { } bytes) _ = ShowPictureAsync(picture, bytes);
        else if (media?.AppKey is { } k && AppShell.Icon(k) is { } appIcon) picture.Child = new Image { Width = 32, Height = 32, Source = Pictures.ForSize(appIcon, 32) };
        else picture.Child = new FontIcon { Glyph = "", FontSize = 22, Foreground = Res("TextFillColorSecondaryBrush") };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        texts.Children.Add(new TextBlock { Text = media?.Title ?? title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (media?.Artist is { Length: > 0 } artist)
            texts.Children.Add(new TextBlock { Text = artist, FontSize = 12.5, Foreground = Res("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        else if (media?.App is { Length: > 0 } appName)
            texts.Children.Add(new TextBlock { Text = appName, FontSize = 12.5, Foreground = Res("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        var top = new Grid { ColumnSpacing = 12, ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        top.Children.Add(picture);
        Grid.SetColumn(texts, 1);
        top.Children.Add(texts);
        if (media?.AppKey is { } key && AppShell.Icon(key) is { } small)
        {
            var app = new Image { Width = 16, Height = 16, Source = Pictures.ForSize(small, 16), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 2, 0) };
            if (media.App is { Length: > 0 } name) ToolTipService.SetToolTip(app, name);
            Grid.SetColumn(app, 2);
            top.Children.Add(app);
        }
        var buttons = new Grid { Margin = new Thickness(0, 4, 0, -4), ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition(), new ColumnDefinition() } };
        var (prev, play, next) = (B("", BarMedia.PreviousAsync), B(playing ? "" : "", BarMedia.PlayPauseAsync), B("", BarMedia.NextAsync));
        foreach (var (b, i) in new[] { (prev, 0), (play, 1), (next, 2) }) { b.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(b, i); buttons.Children.Add(b); }
        var row = new StackPanel { Spacing = 4, Children = { top, TimeBar(), buttons } };
        // more players at once (YouTube and Spotify): each as a small button, the one shown marked; a click shows that one
        BarMedia.Relist();
        var players = BarMedia.Players;
        if (players.Count > 1)
        {
            var list = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
            for (int i = 0; i < players.Count; i++)
            {
                var pl = players[i];
                int at = i;
                var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                if (pl.AppKey is { } k && AppShell.Icon(k) is { } ic) content.Children.Add(new Image { Width = 14, Height = 14, Source = Pictures.ForSize(ic, 16) });
                content.Children.Add(new TextBlock { Text = pl.App ?? "?", FontSize = 12, MaxWidth = 120, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = pl.Shown ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal });
                if (pl.Playing) content.Children.Add(new FontIcon { Glyph = "", FontSize = 11, Foreground = Res("AccentTextFillColorPrimaryBrush") });
                var b = new Button { Style = DockButton, Padding = new Thickness(8, 4, 8, 4), Content = content, Background = pl.Shown ? Res("ControlFillColorSecondaryBrush") : null };
                ToolTipService.SetToolTip(b, pl.Playing ? L.T("{0} – plays", pl.App) : pl.App);
                b.Click += async (_, _) => { BarMedia.Select(at); await Task.Delay(250); BuildMain(); };
                list.Children.Add(b);
            }
            row.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Auto, Content = list });
        }
        return Card(row);
    }

    /// <summary>
    /// The time of what plays: a slider to move it (when the app lets it), the time gone under its left end and the time
    /// left under its right end; it goes on every second while the control center is open. Hidden while the app tells no time.
    /// </summary>
    static FrameworkElement TimeBar()
    {
        var slider = new Slider { Minimum = 0, Maximum = 1, StepFrequency = 1, IsThumbToolTipEnabled = false, Margin = new Thickness(0, 0, 0, -8) };
        TextBlock Time(HorizontalAlignment side) => new() { FontSize = 11, Foreground = Res("TextFillColorSecondaryBrush"), HorizontalAlignment = side };
        var (gone, left) = (Time(HorizontalAlignment.Left), Time(HorizontalAlignment.Right));
        var times = new Grid { Children = { gone, left } };
        var bar = new StackPanel { Spacing = 0, Visibility = Visibility.Collapsed, Children = { slider, times } };
        bool setting = false;
        DateTime touched = default;
        DispatcherTimer seek = null;
        static string Text(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

        void Show(TimeSpan position, TimeSpan length)
        {
            gone.Text = Text(position);
            left.Text = "-" + Text(length - position);
        }
        void Update()
        {
            if (BarMedia.Timeline() is not { } t) { bar.Visibility = Visibility.Collapsed; return; }
            bar.Visibility = Visibility.Visible;
            slider.IsEnabled = t.CanSeek;
            if ((DateTime.Now - touched).TotalSeconds < 1.5) return;   // the user moves it: not under their hand
            setting = true;
            slider.Maximum = Math.Max(1, Math.Round(t.Length.TotalSeconds));
            slider.Value = Math.Min(slider.Maximum, Math.Round(t.Position.TotalSeconds));
            setting = false;
            Show(t.Position, t.Length);
        }
        // moved by the user (drag, click, keys): the times follow at once, the app gets the place when the hand stops
        slider.ValueChanged += (_, e) =>
        {
            if (setting) return;
            touched = DateTime.Now;
            Show(TimeSpan.FromSeconds(e.NewValue), TimeSpan.FromSeconds(slider.Maximum));
            seek ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            seek.Tick -= Seek; seek.Tick += Seek;
            seek.Start();
        };
        void Seek(object s, object e) { seek.Stop(); _ = BarMedia.SeekAsync(TimeSpan.FromSeconds(slider.Value)); }
        void Tick() => Update();
        bar.Loaded += (_, _) => { SecondTick.Tick += Tick; Update(); };
        bar.Unloaded += (_, _) => { SecondTick.Tick -= Tick; seek?.Stop(); };
        Update();
        return bar;
    }

    /// <summary>The picture of the media from its bytes (a cover is a JPEG or PNG), filling the rounded place.</summary>
    static async Task ShowPictureAsync(Border place, byte[] bytes)
    {
        try
        {
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await image.SetSourceAsync(stream);
            place.Background = new ImageBrush { ImageSource = image, Stretch = Stretch.UniformToFill };
            place.Child = null;
        }
        catch (Exception ex) { Log.Write("Media picture", ex); }
    }
}
