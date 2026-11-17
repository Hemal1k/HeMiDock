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
    // ───────────────────────── sound ─────────────────────────

    /// <summary>The size of the arrow and the settings icon of the sound card.</summary>
    const double SoundIcons = 14;

    FrameworkElement SoundCard()
    {
        var panel = new StackPanel { Spacing = 2 };
        var head = new Grid();
        head.Children.Add(Title(L.T("Sound")));
        // the arrow to the devices and the volume mixer: the chevron of the rows under it, as big as the settings icon below
        var mixer = new Button { Style = DockButton, Padding = new Thickness(6, 4, 6, 4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Content = new FontIcon { Glyph = "\uE76C", FontSize = SoundIcons } };
        ToolTipService.SetToolTip(mixer, L.T("Devices and volume mixer"));
        mixer.Click += (_, _) => ShowSound();
        head.Children.Add(mixer);
        panel.Children.Add(head);
        (_volume, _volumeValue) = SliderRow(panel, "", v => AudioControl.SetLevel((float)(v / 100)), AudioControl.ToggleMute); _volumeGlyph = _lastIcon;
        if (AudioControl.Now() is { } now)
        {
            var device = new Button { Style = DockButton, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(26, 0, 0, 0),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = AudioDevices.Shown(now.Name), FontSize = 11, MaxWidth = 260, Foreground = Res("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis }, new FontIcon { Glyph = "", FontSize = 9 } } } };
            ToolTipService.SetToolTip(device, L.T("Choose a sound output"));
            device.Click += (_, _) => ShowSound();
            panel.Children.Add(device);
        }
        if (AudioControl.MicNow() != null)
        {
            (_mic, _micValue) = SliderRow(panel, "", v => AudioControl.SetMicLevel((float)(v / 100)), AudioControl.ToggleMicMute); _micGlyph = _lastIcon;
            // the name of the microphone under it, like the output above (a click opens the devices)
            if (AudioControl.MicName() is { Length: > 0 } micName)
            {
                var input = new Button { Style = DockButton, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(26, 0, 0, 0),
                    Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = AudioDevices.Shown(micName), FontSize = 11, MaxWidth = 260, Foreground = Res("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis }, new FontIcon { Glyph = "\uE76C", FontSize = 9 } } } };
                ToolTipService.SetToolTip(input, L.T("Choose a sound input"));
                input.Click += (_, _) => ShowSound();
                panel.Children.Add(input);
            }
        }
        // the sound settings of Windows in the bottom right corner, as big as the arrow above
        var settings = new Button { Style = DockButton, Padding = new Thickness(6, 4, 6, 4), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0), Content = new FontIcon { Glyph = "", FontSize = SoundIcons } };
        ToolTipService.SetToolTip(settings, L.T("Sound settings"));
        settings.Click += (_, _) => Open("ms-settings:sound");
        settings.Margin = new Thickness(0);
        settings.VerticalAlignment = VerticalAlignment.Center;
        // on the line of the last device name (the microphone), so the card does not get higher by a line of its own
        if (panel.Children.Count > 0 && panel.Children[^1] is Button last && last != settings)
        {
            panel.Children.Remove(last);
            var bottom = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            last.HorizontalAlignment = HorizontalAlignment.Left;
            last.VerticalAlignment = VerticalAlignment.Center;
            bottom.Children.Add(last);
            Grid.SetColumn(settings, 1);
            bottom.Children.Add(settings);
            panel.Children.Add(bottom);
        }
        else panel.Children.Add(settings);
        var card = Card(panel);
        card.PointerWheelChanged += (_, e) => { if (_volume != null) { _volume.Value = Math.Clamp(_volume.Value + (e.GetCurrentPoint(card).Properties.MouseWheelDelta > 0 ? 2 : -2), 0, 100); e.Handled = true; } };
        return card;
    }

    (Slider, TextBlock) SliderRow(Panel parent, string glyph, Action<double> set, Action mute)
    {
        var icon = _lastIcon = new FontIcon { Glyph = glyph, FontSize = 15 };
        var button = new Button { Style = DockButton, Padding = new Thickness(4), Content = icon };
        ToolTipService.SetToolTip(button, L.T("Mute / unmute"));
        if (mute != null) button.Click += (_, _) => { mute(); SyncAudio(); };
        // the number right behind the slider (aligned to the right it stood far from it with one digit), room for "100"
        var value = new TextBlock { Width = 28, TextAlignment = TextAlignment.Left, VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var slider = new Slider { Minimum = 0, Maximum = 100, Margin = new Thickness(4, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, IsThumbToolTipEnabled = false };
        slider.ValueChanged += (_, e) => { if (_updating) return; set(e.NewValue); value.Text = Math.Round(e.NewValue).ToString(); };
        var row = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        row.Children.Add(button);
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        parent.Children.Add(row);
        return (slider, value);
    }

    /// <summary>The volume may change by keys or in another app: the sliders follow (not while one is being dragged).</summary>
    void SyncAudio()
    {
        _updating = true;
        try
        {
            // the icons show the state like the ones of the dock: the level of the volume, muted (also while dragging)
            if (_volumeGlyph != null && AudioControl.Now() is { } now) _volumeGlyph.Glyph = AudioControl.Glyph(now.Level, now.Mute);
            if (_micGlyph != null && AudioControl.MicNow() is { } mic) _micGlyph.Glyph = mic.Mute ? "" : "";
            if (_volume != null && _volume.FocusState != FocusState.Pointer && AudioControl.Now() is { } v)
            {
                _volume.Value = Math.Round(v.Level * 100);
                _volumeValue.Text = v.Mute ? "×" : Math.Round(v.Level * 100).ToString();
            }
            if (_mic != null && _mic.FocusState != FocusState.Pointer && AudioControl.MicNow() is { } m)
            {
                _mic.Value = Math.Round(m.Level * 100);
                _micValue.Text = m.Mute ? "×" : Math.Round(m.Level * 100).ToString();
            }
            foreach (var (app, slider, value) in _apps)
                if (slider.FocusState != FocusState.Pointer) { slider.Value = Math.Round(app.Volume * 100); value.Text = app.Mute ? "×" : Math.Round(app.Volume * 100).ToString(); }
        }
        catch { }
        finally { _updating = false; }
    }

    readonly MMDeviceEnumerator _enum = new();

    void ShowSound()
    {
        ShowPage("sound", "Sound");
        Footer("More sound settings", "ms-settings:sound");
        string output = null, input = null;
        try { using var d = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); output = d.ID; } catch { }
        try { using var d = _enum.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); input = d.ID; } catch { }
        // the volume on top, the same slider as on the main page
        var master = PageCard();
        (_volume, _volumeValue) = SliderRow(master, "", v => AudioControl.SetLevel((float)(v / 100)), AudioControl.ToggleMute); _volumeGlyph = _lastIcon;
        ((FrameworkElement)master.Children[^1]).Margin = new Thickness(6, 4, 6, 4);
        DeviceList("Output", DataFlow.Render, output, "");
        var mixer = PageCard(L.T("Volume mixer"));
        _apps.Clear();
        MMDevice dev = null;
        try { dev = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); } catch { }
        var apps = dev != null ? AudioDevices.Apps(dev) : new();
        if (apps.Count == 0) mixer.Children.Add(new TextBlock { Text = L.T("No app is playing sound"), Margin = new Thickness(8, 6, 8, 6), FontSize = 12.5, Foreground = Res("TextFillColorSecondaryBrush") });
        foreach (var app in apps)
        {
            var row = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (app.IconPath != null) { var img = new Image { Width = 16, Height = 16 }; Pictures.Into(img, app.IconPath); name.Children.Add(img); }
            else name.Children.Add(new FontIcon { Glyph = app.System ? "" : "", FontSize = 13 });
            name.Children.Add(new TextBlock { Text = app.Name, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(name);
            var (slider, value) = SliderRow(row, "", v => { app.Volume = (float)(v / 100); if (app.Mute && v > 0) app.Mute = false; }, () => app.Mute = !app.Mute);
            _apps.Add((app, slider, value));
            mixer.Children.Add(row);
        }
        DeviceList("Input", DataFlow.Capture, input, "");
        SyncAudio();
    }

    /// <summary>The active devices; the default one has a check mark, clicking another makes it the default.</summary>
    void DeviceList(string title, DataFlow flow, string current, string glyph)
    {
        var devices = AudioDevices.Active(_enum, flow);
        if (devices.Count == 0) return;
        var card = PageCard(L.T(title));
        foreach (var d in devices)
        {
            string id = d.ID, name = AudioDevices.Shown(d.FriendlyName);
            d.Dispose();
            card.Children.Add(PageRow(glyph, name, id == current, id == current ? null : async () =>
            {
                // in the background: Windows switches the device and tells every app about it, which can take a moment
                await Task.Run(() => AudioDevices.SetDefault(id));
                if (_openPage == "sound") ShowSound();
            }));
        }
    }
}
