using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;

namespace HeMiDock;

sealed partial class StartWindow
{
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
        if (e.Kind == EntryKind.Control) return QuickControlView.Build(e, () => Launch(e));
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
        // a system control: a switch or a choice stays in the window, an action closes it and runs
        if (e.Kind == EntryKind.Control)
        {
            if (!QuickControlView.Activate(e)) return;
            CloseNow();
            try { e.Control.Run?.Invoke(); } catch (Exception ex) { Log.Write("Quick control " + e.Key, ex); }
            return;
        }
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
}
