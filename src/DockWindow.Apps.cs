using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;

namespace HeMiDock;

/// <summary>The apps of the dock: their icons, opening and closing places, clicks and menus, and the magnification.</summary>
sealed partial class DockWindow
{
    // ───────────────────────── apps ─────────────────────────

    /// <summary>When an icon was pressed (0: none held now).</summary>
    long _pressedAt;

    void Rebuild()
    {
        if (_closed) return;
        // an icon held down: the dock is built again after the click – built meanwhile (another window became active, a running
        // mark came …), the pressed button was gone at the release and the first click was lost (it took a second one)
        if (!_sizing && _pressedAt != 0 && Environment.TickCount64 - _pressedAt < 1500) { Later(); return; }
        ApplyLayout();
        var windows = ShellHost.Running ? ShellHost.TaskWindows() : TaskWindows.List();
        _pins = DockState.Load();
        _entries = DockModel.Build(_pins, windows);
        string shown = string.Join("|", _entries.Select(e => e.IsGap ? (e.Pin.Line ? "L" : "S") : $"{e.IconPath}:{e.Pin?.IconPath}:{e.Name}:{e.Windows.Count}:{e.Windows.Any(w => w.Active)}:{e.Windows.Any(w => w.Flashing)}:{e.Windows.Select(w => w.Progress).DefaultIfEmpty(-1).Max():0.00}:{e.Windows.Any(w => w.Overlay != null)}:{_launching.Contains(e.ExePath ?? "")}"))
            + $"|{S.ShowRunningIndicator}{S.ShowAppNames}{S.DockEditMode}{UnreadMail.HasMail}{_layoutShown}";
        if (shown != _shown)
        {
            _shown = shown;
            // where the icons were (a closed app leaves a place that closes smoothly)
            var oldViews = Apps.Children.OfType<FrameworkElement>().ToList();
            var oldOrder = oldViews.Select(c => c.Tag is DockEntry { IsGap: false } de ? KeyOf(de) : c.Tag is string closingKey && closingKey.StartsWith("closing:") ? closingKey[8..] : null).ToList();
            Apps.Children.Clear();
            // an app new in the dock opens its place from nothing and grows in; the dock follows the width of its content
            // frame by frame (it widened first and cut the icons at both ends, then jumped)
            var before = _iconKeys;
            _iconKeys = new HashSet<string>(_entries.Where(e => !e.IsGap).Select(KeyOf), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _entries)
            {
                var view = entry.IsGap ? GapView(entry) : AppIcon(entry);
                Apps.Children.Add(view);
                if (entry.IsGap) continue;
                string key = KeyOf(entry);
                if (_slides.TryGetValue(key, out var running) && !running.Closing) { running.El = view; ApplySlide(running); }   // built again while it opens
                else if (before != null && !before.Contains(key) && SlidesOn) { StartSlide(key, view, closing: false); Motion.Grow(view); }
            }
            // the places of apps that are gone close; one still closing keeps closing
            if (before != null && SlidesOn)
                for (int i = 0; i < oldOrder.Count; i++)
                    if (oldOrder[i] is { } gone && !_iconKeys.Contains(gone))
                    {
                        // the icon of the closed app stays in its place while it closes and fades out (it vanished at once)
                        var picture = oldViews[i] is Button { Content: UIElement content } old ? content : (oldViews[i] as Border)?.Child;
                        if (oldViews[i] is Button oldButton) oldButton.Content = null;
                        else if (oldViews[i] is Border oldPlace) oldPlace.Child = null;
                        var place = new Border { Width = Vertical ? Slot : SlotAlong(), Height = Vertical ? SlotAlong() : Slot, Tag = "closing:" + gone, Child = picture, IsHitTestVisible = false };
                        Apps.Children.Insert(Math.Min(i, Apps.Children.Count), place);
                        if (_slides.TryGetValue(gone, out var closing) && closing.Closing) { closing.El = place; ApplySlide(closing); }
                        else StartSlide(gone, place, closing: true);
                    }
            BounceFlashing();
        }
        if (PreviewWindow.Shown is { } open)
        {
            var now = _entries.FirstOrDefault(e => e.ExePath != null && string.Equals(e.ExePath, open.ExePath, StringComparison.OrdinalIgnoreCase));
            if (now is not { IsRunning: true }) PreviewWindow.HideNow();
            else if (!now.Windows.Select(w => w.Handle).SequenceEqual(open.Windows.Select(w => w.Handle)) && ButtonOf(now) is { } b) PreviewWindow.Show(now, AnchorRect(b), Edge, _mon);
        }
        RebuildTray();
        Place();
    }

    internal DropMarker Marker { get; private set; }
    DropMarker IPanelWindow.Marker => Marker;

    HashSet<string> _iconKeys;
    static string KeyOf(DockEntry e) => e.ExePath ?? e.IconPath ?? e.Name ?? "";

    /// <summary>The place of an icon opening (a new app) or closing (an app gone), frame by frame.</summary>
    sealed class Slide { public FrameworkElement El; public bool Closing; public double Start; public bool Drawn; }

    /// <summary>
    /// Time for the animations in milliseconds, exact: the tick count of Windows goes in steps of about 16 ms – some frames
    /// got the same time and the next one a double step (the dock grew and shrank unevenly).
    /// </summary>
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    static double NowMs => Clock.Elapsed.TotalMilliseconds;
    readonly Dictionary<string, Slide> _slides = new(StringComparer.OrdinalIgnoreCase);
    bool _sliding;

    bool SlidesOn => _placedOnce && _visible && Motion.On;

    /// <summary>How long a place opens or closes.</summary>
    static double SlideMs => Motion.Length(340).TotalMilliseconds;

    /// <summary>The length of the place of one icon along the dock.</summary>
    double SlotAlong() => Slot;

    void StartSlide(string key, FrameworkElement el, bool closing)
    {
        _slides[key] = new Slide { El = el, Closing = closing, Start = NowMs };
        ApplySlide(_slides[key]);
        if (_sliding) return;
        _sliding = true;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnSlideFrame;
    }

    /// <summary>The share of the place now (0 = none, 1 = the whole), with a soft start and stop.</summary>
    static double SlideShare(Slide s)
    {
        double t = Math.Min(1, (NowMs - s.Start) / SlideMs);
        double k = (1 - Math.Cos(Math.PI * t)) / 2;                     // the gentlest start and stop (sine)
        return s.Closing ? 1 - k : k;
    }

    void ApplySlide(Slide s)
    {
        var (len, gap) = SlideSizes(s);
        if (s.El is Control c) c.MinWidth = c.MinHeight = 0;
        if (s.Closing) s.El.Opacity = SlideShare(s);                   // the icon of a closed app fades with its place
        // the place grows from nothing, and so does the gap before it (the spacing of the row would jump at once); an
        // element is never shorter than nothing with its margins – what it cannot take back goes to the row (ApplyRest)
        double back = Math.Min(gap, len);
        if (Vertical) { s.El.Height = len; s.El.Margin = new Thickness(0, -back, 0, 0); }
        else { s.El.Width = len; s.El.Margin = new Thickness(-back, 0, 0, 0); }
        ApplyRest();
    }

    (double Len, double Gap) SlideSizes(Slide s)
    {
        double k = SlideShare(s);
        return (SlotAlong() * k, Apps.Spacing * (1 - k));
    }

    /// <summary>
    /// The spacing of places only beginning (or ending) to open, more than their own length: taken back from the end of the
    /// apps, so the dock grows from exactly its length (it jumped by the spacing first, then grew).
    /// </summary>
    void ApplyRest()
    {
        double rest = 0;
        foreach (var s in _slides.Values) { var (len, gap) = SlideSizes(s); rest += Math.Max(0, gap - len); }
        var m = Vertical ? new Thickness(0, 0, 0, -rest) : new Thickness(0, 0, -rest, 0);
        if (!Apps.Margin.Equals(m)) Apps.Margin = m;
    }

    void OnSlideFrame(object sender, object e)
    {
        foreach (var (key, s) in _slides.ToList())
        {
            // the time runs from the first frame drawn: building the new icon (its picture) held the first frame back by
            // about 100 ms, and the place then opened by a third at once
            if (!s.Drawn) { s.Drawn = true; s.Start = NowMs; }
            ApplySlide(s);
            if (NowMs - s.Start < SlideMs) continue;
            _slides.Remove(key);
            if (s.Closing) Apps.Children.Remove(s.El);
            else { s.El.ClearValue(Vertical ? FrameworkElement.HeightProperty : FrameworkElement.WidthProperty); s.El.Margin = new Thickness(0); }
        }
        ApplyRest();                                                     // the ones finished give their part back
        Place();                                                         // the window has the width of the content in this frame
        if (_slides.Count > 0 && !_closed) return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnSlideFrame;
        _sliding = false;
    }

    Button ButtonOf(DockEntry entry) => Apps.Children.OfType<Button>().FirstOrDefault(b => b.Tag == entry);

    /// <summary>A space or a line between icons; outlined in edit mode so it can be found.</summary>
    FrameworkElement GapView(DockEntry entry)
    {
        bool line = entry.Pin.Line;
        double along = line ? 13 : Slot * 0.5;
        var inner = line
            ? new Border { Background = ThemeBrush.Get("DividerStrokeColorDefaultBrush"), Width = Vertical ? Slot * 0.6 : 1, Height = Vertical ? 1 : Slot * 0.6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            : new Border();
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["DockButton"], Padding = new Thickness(0), Tag = entry, Content = inner,
            Width = Vertical ? Slot : along, Height = Vertical ? along : Slot, CanDrag = true
        };
        if (S.DockEditMode) { button.BorderThickness = new Thickness(1); button.BorderBrush = ThemeBrush.Get("ControlStrokeColorSecondaryBrush"); }
        button.DragStarting += (_, e) => StartDrag(entry, e);
        button.DropCompleted += (_, _) => _dragPin = null;
        var menu = Menu();
        menu.Items.Add(line ? Item(L.T("Change to a space"), () => EditPins(p => entry.Pin.Line = false)) : Item(L.T("Change to a separator"), () => EditPins(p => entry.Pin.Line = true)));
        menu.Items.Add(Item(line ? L.T("Remove separator") : L.T("Remove space"), () => EditPins(p => p.Remove(entry.Pin))));
        button.ContextFlyout = menu;
        return button;
    }

    FrameworkElement AppIcon(DockEntry entry)
    {
        double px = IconPx;
        var image = new Image { Width = px, Height = px };
        if (entry.Pin?.IconPath is { } own && OwnPicture.Load(own) is { } ownPicture) image.Source = ownPicture;
        var icon = new Grid { Children = { image }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (image.Source == null)                                         // no icon at all: the symbol of an app instead of an empty place
            Pictures.Into(image, entry.IconPath, isFile: entry.Pin != null && entry.Pin.AppId == null, program: entry.ExePath,
                missing: () => icon.Children.Insert(0, new FontIcon { Glyph = "", FontSize = Math.Round(px * 0.7), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }));
        if (entry.Windows.Select(w => w.Overlay).FirstOrDefault(o => o != null) is { } overlay)
            icon.Children.Add(new Image { Width = px * 0.5, Height = px * 0.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -3, -3), Source = Pictures.ForSize(overlay, px * 0.5) });
        else if (entry.IsMail && UnreadMail.HasMail)                   // the overlay of Outlook goes to the hidden taskbar of Windows: the own envelope
            icon.Children.Add(new Border
            {
                Width = px * 0.48, Height = px * 0.48, CornerRadius = new CornerRadius(px * 0.24), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -3, -3, 0), Background = ThemeBrush.Get("AccentFillColorDefaultBrush"),
                Child = new FontIcon { Glyph = "", FontSize = px * 0.26, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) }
            });

        // the running indicator (or the progress) on the side of the screen edge
        double progress = entry.Windows.Select(w => w.Progress).DefaultIfEmpty(-1).Max();
        FrameworkElement mark = null;
        if (progress >= 0)
            mark = new ProgressBar { Value = progress * 100, Maximum = 100, Width = Vertical ? double.NaN : px, MinHeight = 3, Height = Vertical ? px : double.NaN, MinWidth = Vertical ? 3 : 0 };
        else if (S.ShowRunningIndicator && (entry.IsRunning || _launching.Contains(entry.ExePath ?? "")))
        {
            bool active = entry.Windows.Any(w => w.Active);
            double len = active || entry.Windows.Any(w => w.Flashing) ? 14 : 5;
            mark = new Rectangle
            {
                Width = Vertical ? 3 : len, Height = Vertical ? len : 3, RadiusX = 1.5, RadiusY = 1.5,
                Fill = ThemeBrush.Get(entry.Windows.Any(w => w.Flashing) ? "SystemFillColorCautionBrush" : active ? "AccentFillColorDefaultBrush" : "TextFillColorSecondaryBrush")
            };
        }
        var slot = new Grid { Width = Slot, Height = Slot };
        // a window asks for attention: an orange place behind the icon that pulses (like the taskbar of Windows; the colour of
        // the small mark alone was easily missed)
        if (entry.Windows.Any(w => w.Flashing)) slot.Children.Add(AttentionBack());
        slot.Children.Add(icon);
        if (mark != null)
        {
            mark.HorizontalAlignment = Edge switch { 1 => HorizontalAlignment.Left, 2 => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
            mark.VerticalAlignment = Edge switch { 0 => VerticalAlignment.Bottom, 3 => VerticalAlignment.Top, _ => VerticalAlignment.Center };
            mark.Margin = new Thickness(1);
            slot.Children.Add(mark);
        }

        var button = new Button
        {
            Style = (Style)Application.Current.Resources["DockButton"], Content = slot, Tag = entry, Padding = new Thickness(0),
            AllowDrop = true
        };
        Hover(button);
        if (S.ShowAppNames) ToolTipService.SetToolTip(button, entry.Windows.Count > 1 ? $"{entry.Name} ({entry.Windows.Count})" : entry.Name);
        // the button takes the press itself: seen also when handled
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => { _pressedAt = Environment.TickCount64; PressPin(entry, button, e); }), true);
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => _pressedAt = 0), true);
        button.PointerCaptureLost += (_, _) => _pressedAt = 0;
        button.Click += (_, _) => { if (!_pinDragged) Open(entry, button, newWindow: (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0); };   // no opening after a drag
        // the middle button: a new window of the app – seen also when handled (the button took the release itself, and the
        // middle click did nothing)
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.GetCurrentPoint(button).Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased) Open(entry, button, newWindow: true);
        }), true);
        button.PointerWheelChanged += (_, e) =>
        {
            if (entry.Windows.Count == 0) return;
            e.Handled = true;
            int at = entry.Windows.FindIndex(w => w.Active);
            var next = entry.Windows[((at < 0 ? 0 : at) + (e.GetCurrentPoint(button).Properties.MouseWheelDelta < 0 ? 1 : -1) + entry.Windows.Count) % entry.Windows.Count];
            if (Win32.IsIconic(next.Handle)) Win32.ShowWindow(next.Handle, Win32.SW_RESTORE);
            Fg.Force(next.Handle);
        };
        button.PointerEntered += (_, _) =>
        {
            if (!S.ShowPreviews || !entry.IsRunning) { _previewTimer.Stop(); return; }
            _previewFor = entry;
            _previewButton = button;
            _previewHide.Stop();
            _previewTimer.Interval = TimeSpan.FromMilliseconds(PreviewWindow.Shown != null ? 120 : 600);   // from one preview to the next at once
            _previewTimer.Stop();
            _previewTimer.Start();
        };
        button.PointerExited += (_, _) => { _previewTimer.Stop(); if (PreviewWindow.Shown != null) _previewHide.Start(); };
        button.ContextRequested += (_, e) => { e.Handled = true; PreviewWindow.HideNow(); AppMenu(entry).ShowAt(button, new FlyoutShowOptions { Placement = MenuPlacement }); };

        // dragging: a pin moves, files dropped on an app open with it
        button.DragStarting += (_, e) => { PreviewWindow.HideNow(); StartDrag(entry, e); };
        button.DropCompleted += (_, _) => _dragPin = null;
        button.DragOver += (_, e) =>
        {
            if (_dragPin != null || !e.DataView.Contains(StandardDataFormats.StorageItems) || (entry.ExePath == null || !File.Exists(entry.ExePath)) && !entry.IsFolder) return;   // left to the dock
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = entry.IsFolder ? L.T("Open {0}", entry.Name) : entry.Name;
            e.Handled = true;
        };
        button.Drop += async (_, e) =>
        {
            if (_dragPin != null || !e.DataView.Contains(StandardDataFormats.StorageItems) || (entry.ExePath == null || !File.Exists(entry.ExePath)) && !entry.IsFolder) return;
            e.Handled = true;
            var deferral = e.GetDeferral();
            try
            {
                var files = (await e.DataView.GetStorageItemsAsync()).Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
                if (entry.IsFolder) Fg.Open("explorer.exe", $"\"{entry.Pin.Path}\"");
                else if (files.Count > 0)
                {
                    MarkLaunching(entry.ExePath);
                    Process.Start(new ProcessStartInfo(entry.ExePath, string.Join(" ", files.Select(f => $"\"{f}\""))) { UseShellExecute = true });
                }
            }
            catch (Exception ex) { Log.Write("Open with " + entry.Name, ex); }
            deferral.Complete();
        };
        return button;
    }

    FlyoutPlacementMode MenuPlacement => Edge switch { 1 => FlyoutPlacementMode.Right, 2 => FlyoutPlacementMode.Left, 3 => FlyoutPlacementMode.Bottom, _ => FlyoutPlacementMode.Top };

    void Open(DockEntry entry, Button button, bool newWindow)
    {
        PreviewWindow.HideNow();
        _previewTimer.Stop();
        if (entry.IsFolder && !newWindow)
        {
            var stack = StackFlyout.Create(entry.Pin.Path, entry.Name);
            Track(stack);
            stack.ShowAt(button, new FlyoutShowOptions { Placement = MenuPlacement });
            return;
        }
        if (entry.IsRunning && !newWindow)
        {
            if (entry.Windows.Count == 1) { Launcher.Activate(entry.Windows[0]); return; }
            if (S.ShowPreviews) { PreviewWindow.Show(entry, AnchorRect(button), Edge, _mon); return; }
            var menu = Menu();
            foreach (var w in entry.Windows) menu.Items.Add(Item(w.Title, () => Launcher.Activate(w)));
            menu.ShowAt(button, new FlyoutShowOptions { Placement = MenuPlacement });
            return;
        }
        MarkLaunching(entry.ExePath ?? entry.IconPath);
        Launcher.Start(entry, newWindow);
    }

    void ShowPreview()
    {
        if (_previewFor is not { IsRunning: true } entry || _previewButton is not { } b || !Over(b) || _menusOpen > 0) return;
        PreviewWindow.Show(entry, AnchorRect(b), Edge, _mon);
    }

    MenuFlyout AppMenu(DockEntry entry)
    {
        var menu = Menu();
        var first = Item(entry.Name ?? L.T("Open"), () => { MarkLaunching(entry.ExePath ?? entry.IconPath); Launcher.Start(entry); });
        first.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        menu.Items.Add(first);
        if (entry.IsRunning)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            foreach (var w in entry.Windows)
            {
                string title = string.IsNullOrWhiteSpace(w.Title) ? entry.Name : w.Title;
                if (title.Length > 48) title = title[..47] + "…";
                MenuFlyoutItem window = w.Active ? new ToggleMenuFlyoutItem { Text = title, IsChecked = true } : Item(title, () => Launcher.Activate(w));
                if (window is ToggleMenuFlyoutItem t) t.Click += (_, _) => Launcher.Activate(w);
                window.Icon = MenuPicture(entry.Pin?.IconPath ?? entry.IconPath, isFile: entry.Pin?.IconPath != null || entry.Pin is { AppId: null });   // the icon of the app
                menu.Items.Add(window);
            }
        }
        // recent files of the app (like the jump list of the taskbar)
        var recent = RecentForApp.Files(entry.ExePath);
        if (recent.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(new MenuFlyoutItem { Text = L.T("Recent"), IsEnabled = false });
            foreach (var file in recent)
            {
                var item = Item(System.IO.Path.GetFileName(file), () => Fg.Open(file));
                item.Icon = MenuPicture(file, isFile: true);
                ToolTipService.SetToolTip(item, file);
                menu.Items.Add(item);
            }
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        if (entry.ExePath != null && File.Exists(entry.ExePath))
            menu.Items.Add(Item(L.T("Run as administrator"), () => AppShell.Launch(entry.Pin?.Path ?? entry.ExePath, admin: true)));
        else if (entry.Pin?.Path is { } packaged && AppShell.PackageCanElevate(packaged))   // a desktop app of the Store (Terminal, …)
            menu.Items.Add(Item(L.T("Run as administrator"), () => AppShell.Launch(packaged, admin: true)));
        if (entry.ExePath != null && File.Exists(entry.ExePath)) menu.Items.Add(Item(L.T("Open file location"), () => Fg.Run("explorer.exe", $"/select,\"{entry.ExePath}\"")));
        if (entry.Pin != null && S.DockEditMode)
        {
            // own name and picture of a pinned icon (edit mode of the dock)
            menu.Items.Add(Item(L.T("Rename…"), async () =>
            {
                if (await TextPrompt.AskAsync(L.T("Rename"), L.T("Name in the dock (empty = the app's name)"), entry.Pin.Name ?? entry.Name) is { } name)
                    EditPins(_ => entry.Pin.Name = string.IsNullOrWhiteSpace(name) ? AppShell.AppName(entry.Pin.Path ?? entry.IconPath) : name.Trim());
            }));
            menu.Items.Add(Item(L.T("Change icon…"), async () =>
            {
                _menusOpen++;
                try
                {
                    if (await OwnPicture.AskAsync(_hwnd) is { } file)
                    {
                        string path = OwnPicture.ImportIcon(file);
                        if (OwnPicture.Load(path) != null) EditPins(_ => entry.Pin.IconPath = path);
                    }
                }
                catch (Exception ex) { Log.Write("Change icon", ex); }
                _menusOpen--;
            }));
            if (entry.Pin.IconPath != null) menu.Items.Add(Item(L.T("Original icon"), () => EditPins(_ => entry.Pin.IconPath = null)));
        }
        if (entry.Pin != null) menu.Items.Add(Item(L.T("Unpin from dock"), () => EditPins(p => p.Remove(entry.Pin))));
        else if (entry.ExePath != null) menu.Items.Add(Item(L.T("Pin to dock"), () => Pin(entry.ExePath)));
        if (entry.IsRunning)
        {
            int n = entry.Windows.Count;
            menu.Items.Add(Item(n > 1 ? L.T("Close all windows ({0})", n) : L.T("Close window"), () => { foreach (var w in entry.Windows) Launcher.Close(w); }));
        }
        return menu;
    }

    /// <summary>The name of the app a window belongs to, as in the dock (null when it is not there).</summary>
    internal string AppNameOf(IntPtr hwnd) => _entries.FirstOrDefault(e => e.Windows.Any(w => w.Handle == hwnd))?.Name;

    /// <summary>The menu of the app of a window – the same as its icon (for the name of the app in the top bar).</summary>
    internal MenuFlyout MenuForWindow(IntPtr hwnd) =>
        _entries.FirstOrDefault(e => e.Windows.Any(w => w.Handle == hwnd)) is { } entry ? AppMenu(entry) : null;

    /// <summary>A menu that keeps the dock still while it is open (no hiding, no previews).</summary>
    MenuFlyout Menu()
    {
        var menu = new MenuFlyout();
        Track(menu);
        return menu;
    }

    void Track(FlyoutBase flyout)
    {
        // the dock is not the active window (the app in front is): without the foreground a menu of WinUI closes at once
        flyout.Opening += (_, _) => { _menusOpen++; flyout.AreOpenCloseAnimationsEnabled = Motion.On; Fg.Force(_hwnd); };   // no animation with the lighter drawing
        flyout.Closed += (_, _) =>
        {
            _menusOpen = Math.Max(0, _menusOpen - 1);
            if (_menusOpen == 0 && _placeLater) { _placeLater = false; if (!Over(Root)) { ResetMagnification(); _expanded = false; } Place(); }
        };
    }

    bool _placeLater;

    /// <summary>A small picture for a menu item (an app, a file), loaded in the background.</summary>
    static IconElement MenuPicture(string key, bool isFile)
    {
        var icon = new ImageIcon { Width = 16, Height = 16 };
        _ = Load();
        return icon;

        async Task Load()
        {
            try { icon.Source = await Pictures.OfAsync(key, isFile, (int)Math.Ceiling(16 * (Native.MonitorDpi(Monitors.Primary().Handle) ?? 1))); }
            catch (Exception ex) { Log.Write("Menu icon " + key, ex); }
        }
    }

    static MenuFlyoutItem Item(string text, Action click, string glyph = null)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = click != null };
        if (glyph != null) item.Icon = new FontIcon { Glyph = glyph };
        if (click != null) item.Click += (_, _) => click();
        return item;
    }

    /// <summary>The icons are drawn again (read anew after <see cref="Pictures.Forget"/>).</summary>
    public void RedrawIcons()
    {
        _shown = null;
        Later();
    }

    /// <summary>Changes the pins and saves them (the docks follow by <see cref="DockState.Changed"/>).</summary>
    void EditPins(Action<List<PinnedApp>> change)
    {
        change(_pins);
        DockState.Save(_pins);
        _shown = null;
        Later();
    }

    public void Pin(string path)
    {
        var pins = DockState.Load();
        if (pins.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        pins.Add(new PinnedApp { Path = path, Name = Directory.Exists(path) ? System.IO.Path.GetFileName(path.TrimEnd('\\')) : AppShell.AppName(path) });
        DockState.Save(pins);
        _shown = null;
        Later();
    }

    /// <summary>A program, shortcut or app of Start is pinned to the dock (by its path, or the program of a pinned shortcut).</summary>
    internal bool IsPinned(string key) => !string.IsNullOrEmpty(key) && DockState.Load().Any(p => Same(p, key));

    internal void Unpin(string key)
    {
        var pins = DockState.Load();
        if (pins.RemoveAll(p => Same(p, key)) == 0) return;
        DockState.Save(pins);
        _shown = null;
        Later();
    }

    bool Same(PinnedApp p, string key) =>
        string.Equals(p.Path, key, StringComparison.OrdinalIgnoreCase)
        || p.AppId != null && key.EndsWith(p.AppId, StringComparison.OrdinalIgnoreCase)
        || _entries.Any(e => e.Pin == p && string.Equals(e.ExePath, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The indicator shows at once while the app starts (for a few seconds, until its window comes); the icon bounces.</summary>
    public void MarkLaunching(string key)
    {
        if (string.IsNullOrEmpty(key) || !_launching.Add(key)) return;
        _shown = null;
        Rebuild();
        if (_entries.FirstOrDefault(e => string.Equals(e.ExePath ?? e.IconPath, key, StringComparison.OrdinalIgnoreCase)) is { } entry && ButtonOf(entry) is { } b) Bounce(b);
        var end = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        end.Tick += (_, _) => { end.Stop(); _launching.Remove(key); _shown = null; Later(); };
        end.Start();
    }

    Border AttentionBack()
    {
        var back = new Border
        {
            Margin = new Thickness(2), CornerRadius = new CornerRadius(6), IsHitTestVisible = false, Opacity = 0.55,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xF7, 0x8C, 0x1E))
        };
        if (S.AnimationSpeed == 3 || S.LowGraphics) return back;
        var pulse = new DoubleAnimation { From = 0.25, To = 0.7, Duration = TimeSpan.FromSeconds(0.9), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Storyboard.SetTarget(pulse, back);
        Storyboard.SetTargetProperty(pulse, "Opacity");
        var board = new Storyboard { Children = { pulse } };
        back.Loaded += (_, _) => board.Begin();
        back.Unloaded += (_, _) => board.Stop();
        return back;
    }

    /// <summary>A window asks for attention: its icon bounces once (when it starts flashing).</summary>
    void BounceFlashing()
    {
        var now = _entries.Where(e => e.Windows.Any(w => w.Flashing) && e.ExePath != null).Select(e => e.ExePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var exe in now.Where(x => !_flashing.Contains(x)))
            if (_entries.FirstOrDefault(e => string.Equals(e.ExePath, exe, StringComparison.OrdinalIgnoreCase)) is { } entry && ButtonOf(entry) is { } b) Bounce(b);
        _flashing.Clear();
        _flashing.UnionWith(now);
        BringUpConsent();
    }

    readonly HashSet<IntPtr> _consentRaised = new();

    /// <summary>
    /// A UAC prompt that came up behind the other windows (only its button blinked in the bar): brought to the front once.
    /// Windows does that when the app that asked for it was not in front – a click on the dock does not make it the foreground.
    /// </summary>
    void BringUpConsent()
    {
        if (!_main) return;
        foreach (var w in _entries.SelectMany(e => e.Windows))
        {
            bool consent = string.Equals(System.IO.Path.GetFileName(w.ExePath ?? ""), "consent.exe", StringComparison.OrdinalIgnoreCase);
            if (!consent)
            {
                var cls = new System.Text.StringBuilder(64);
                GetClassName(w.Handle, cls, cls.Capacity);                // the path of a system process cannot always be read
                consent = cls.ToString() == "Credential Dialog Xaml Host";
            }
            if (!consent || !_consentRaised.Add(w.Handle)) continue;
            if (Native.GetForegroundWindow() != w.Handle) Fg.Force(w.Handle);
            Diag.Write("UAC", "prompt brought to the front");
        }
        if (_consentRaised.Count > 20) _consentRaised.Clear();
    }

    /// <summary>The icon jumps out of the dock twice (Settings → Animations; not with animations off).</summary>
    void Bounce(Button button)
    {
        if (!S.LaunchAnimation || S.AnimationSpeed == 3 || S.LowGraphics || button.Content is not Grid || PictureOf(button) is not { } icon) return;
        // inside the place of the icon: the window does not grow for it
        double amp = Math.Max(3, (Slot - IconPx) / 2) * (Edge is 0 or 2 ? -1 : 1);
        double speed = S.AnimationSpeed switch { 0 => 1.6, 1 => 1.2, _ => 1 };
        var move = TransformOf(icon);
        var frames = new DoubleAnimationUsingKeyFrames();
        foreach (var (t, v) in new[] { (0.0, 0.0), (0.16, amp), (0.32, 0.0), (0.44, amp * 0.45), (0.56, 0.0) })
            frames.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(t * speed), Value = v, EasingFunction = new QuadraticEase { EasingMode = v == 0 ? EasingMode.EaseIn : EasingMode.EaseOut } });
        Storyboard.SetTarget(frames, move);
        Storyboard.SetTargetProperty(frames, Vertical ? "TranslateX" : "TranslateY");
        var board = new Storyboard { Children = { frames } };
        board.Completed += (_, _) => { move.TranslateX = move.TranslateY = 0; };
        board.Begin();
    }

    /// <summary>Win+1 … Win+9: the apps of the dock in their order.</summary>
    public void ActivateApp(int index)
    {
        var apps = _entries.Where(e => !e.IsGap).ToList();
        if (index >= 0 && index < apps.Count && ButtonOf(apps[index]) is { } b) Open(apps[index], b, newWindow: false);
    }

    // ───────────────────────── magnification ─────────────────────────

    /// <summary>The icons near the mouse grow (a wave over <see cref="DockSettings.MagnificationSpread"/> icons), anchored at the edge.</summary>
    void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        double max = FitMagnification;
        if (S.HighlightOnly || max <= 1.001 || _menusOpen > 0 || _dragPin != null) return;
        var p = e.GetCurrentPoint(Row).Position;
        // it starts only when the mouse is on an icon (not already near one: at the end of the dock, by the tray icons);
        // from then on the wave follows the mouse until it leaves the dock
        if (!_magnifying)
        {
            _magnifying = Magnified().Any(b =>
            {
                try { return b.TransformToVisual(Row).TransformBounds(new Windows.Foundation.Rect(0, 0, b.ActualWidth, b.ActualHeight)).Contains(p); }
                catch { return false; }
            });
            if (!_magnifying) return;
        }
        double at = Vertical ? p.Y : p.X;
        double spread = Math.Max(1.5, S.MagnificationSpread) * Slot / 2;
        foreach (var b in Magnified())
        {
            Windows.Foundation.Point c;
            try { c = b.TransformToVisual(Row).TransformPoint(new Windows.Foundation.Point(b.ActualWidth / 2, b.ActualHeight / 2)); } catch { continue; }
            double d = Math.Abs((Vertical ? c.Y : c.X) - at);
            double f = d < spread ? (Math.Cos(d / spread * Math.PI) + 1) / 2 : 0;
            if (PictureOf(b) is not { } picture) continue;
            var t = TransformOf(picture);
            t.ScaleX = t.ScaleY = 1 + (max - 1) * f;
        }
    }

    IEnumerable<Button> Magnified()
    {
        foreach (var b in Apps.Children.OfType<Button>()) if (b.Tag is DockEntry { IsGap: false }) yield return b;
        foreach (var b in new[] { StartButton, SearchButton, TaskViewButton, BinButton, DownloadsButton }) if (b.Visibility == Visibility.Visible) yield return b;
    }

    bool _magnifying;

    void ResetMagnification()
    {
        _magnifying = false;
        foreach (var b in Magnified()) if (PictureOf(b)?.RenderTransform is CompositeTransform t) t.ScaleX = t.ScaleY = 1;
    }
}
