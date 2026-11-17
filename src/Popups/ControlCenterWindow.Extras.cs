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
        ShowPage("awake", "Keep awake");
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

    static BatteryIcon Battery(int percent, bool plugged) { var i = new BatteryIcon { Size = 20 }; i.Show(percent, plugged); return i; }

    FrameworkElement MediaCard()
    {
        var (title, playing) = BarMedia.Now;
        var media = BarMedia.Details;
        Button B(string glyph, Func<Task> click) { var b = new Button { Style = DockButton, Padding = new Thickness(10, 7, 10, 7), Content = new FontIcon { Glyph = glyph, FontSize = 16 } }; b.Click += async (_, _) => { await click(); await Task.Delay(300); BuildMain(); }; return b; }
        // the picture of what plays (cover, frame of the video), else the icon of the app; beside it the app, the title and
        // the artist; the buttons under them
        const double Pic = 72;
        var picture = new Border { Width = Pic, Height = Pic, CornerRadius = new CornerRadius(6), Background = Res("ControlFillColorDefaultBrush"), VerticalAlignment = VerticalAlignment.Top };
        if (media?.Picture is { } bytes) _ = ShowPictureAsync(picture, bytes);
        else if (media?.AppKey is { } k && AppShell.Icon(k) is { } appIcon) picture.Child = new Image { Width = 40, Height = 40, Source = Pictures.ForSize(appIcon, 40) };
        else picture.Child = new FontIcon { Glyph = "", FontSize = 28, Foreground = Res("TextFillColorSecondaryBrush") };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        if (media?.App is { Length: > 0 } appName)
        {
            var source = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 2) };
            if (media.AppKey is { } key && AppShell.Icon(key) is { } small) source.Children.Add(new Image { Width = 14, Height = 14, Source = Pictures.ForSize(small, 14), VerticalAlignment = VerticalAlignment.Center });
            source.Children.Add(new TextBlock { Text = appName, FontSize = 11.5, Foreground = Res("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            texts.Children.Add(source);
        }
        texts.Children.Add(new TextBlock { Text = media?.Title ?? title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        if (media?.Artist is { Length: > 0 } artist)
            texts.Children.Add(new TextBlock { Text = artist, FontSize = 12, Foreground = Res("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        var top = new Grid { ColumnSpacing = 12, ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() } };
        top.Children.Add(picture);
        Grid.SetColumn(texts, 1);
        top.Children.Add(texts);
        var row = new StackPanel { Spacing = 6, Children = { top } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 8, Children = { B("", BarMedia.PreviousAsync), B(playing ? "" : "", BarMedia.PlayPauseAsync), B("", BarMedia.NextAsync) } };
        row.Children.Add(buttons);
        return Card(row);
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
