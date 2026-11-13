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

/// <summary>Dragging: files and apps dropped on the dock, the parts moved in edit mode, pinned apps moved with the mouse.</summary>
sealed partial class DockWindow
{
    // ───────────────────────── drag and drop ─────────────────────────

    // ───────────────────────── the parts of the dock (edit mode) ─────────────────────────

    /// <summary>The parts of the dock in their default order (ids stored in <see cref="DockSettings.DockOrder"/>.</summary>
    (string Id, FrameworkElement El)[] Parts => new (string, FrameworkElement)[]
    {
        ("start", StartButton), ("search", SearchButton), ("taskview", TaskViewButton), ("bin", BinButton), ("downloads", DownloadsButton), ("line1", StartDivider), ("apps", Apps),
        // drives and the keyboard language with the status icons (as in Windows): the language right before the clock
        ("line2", Divider), ("desks", Desktops), ("monitor", MonitorText), ("keys", LocksText), ("overflow", OverflowButton), ("tray", Tray),
        ("usb", UsbButton), ("status", StatusButton), ("lang", LangButton), ("clock", ClockButton), ("desktop", DesktopButton)
    };

    /// <summary>The parts that go to the far end of a full-width dock.</summary>
    static readonly string[] RightParts = { "monitor", "usb", "lang", "keys", "tray", "overflow", "status", "clock", "desks", "desktop" };

    bool _partsHooked;

    /// <summary>The parts in the stored order (parts added in a newer version go after their default neighbour).</summary>
    void ApplyOrder()
    {
        var parts = Parts;
        if (!_partsHooked)
        {
            _partsHooked = true;
            foreach (var (id, el) in parts)
            {
                if (id == "apps") continue;                              // its apps are moved one by one
                var part = el;
                // the press is seen also when the button takes it itself
                part.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => PressPart(part, e)), true);
            }
        }
        var ids = S.DockOrder.Split('|', StringSplitOptions.RemoveEmptyEntries).Where(id => parts.Any(p => p.Id == id)).Distinct().ToList();
        for (int i = 0; i < parts.Length; i++)
            if (!ids.Contains(parts[i].Id)) ids.Insert(i == 0 ? 0 : ids.IndexOf(parts[i - 1].Id) + 1, parts[i].Id);
        bool full = S.DockFull != 0 && !Vertical;
        var left = ids.Where(id => !full || !RightParts.Contains(id)).Select(id => parts.First(p => p.Id == id).El).ToList();
        var right = full ? ids.Where(RightParts.Contains).Select(id => parts.First(p => p.Id == id).El).ToList() : new();
        if (Row.Children.Cast<UIElement>().SequenceEqual(left) && RowRight.Children.Cast<UIElement>().SequenceEqual(right)) return;
        Row.Children.Clear();
        RowRight.Children.Clear();
        foreach (var el in left) Row.Children.Add(el);
        foreach (var el in right) RowRight.Children.Add(el);
    }

    /// <summary>Where a part let go at the screen point goes: before the visible part whose middle is past it (the index in
    /// the parts without the dragged one), and that part for the line (null: at the end, after <c>last</c>).</summary>
    (int Index, FrameworkElement Before, FrameworkElement Last) PartPlace(FrameworkElement part, Native.POINT p)
    {
        var list = Row.Children.Concat(RowRight.Children).OfType<FrameworkElement>().Where(c => c != part).ToList();
        FrameworkElement last = null;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (c.Visibility != Visibility.Visible || c.ActualWidth <= 0) continue;
            var r = ScreenRect(c);
            if ((Vertical ? (r.Top + r.Bottom) / 2 : (r.Left + r.Right) / 2) > (Vertical ? p.Y : p.X)) return (i, c, last);
            last = c;
        }
        return (list.Count, null, last);
    }

    /// <summary>
    /// Edit mode: a part pressed and moved by a few pixels is dragged – it goes faint, a line shows its new place, let go it
    /// moves there (the order is stored for all docks). The drag of WinUI did not start on the buttons of the dock: the mouse
    /// is followed through Windows itself. A press without moving stays a click.
    /// </summary>
    void PressPart(FrameworkElement part, PointerRoutedEventArgs e)
    {
        if (!S.DockEditMode || !e.GetCurrentPoint(part).Properties.IsLeftButtonPressed || !Native.GetCursorPos(out var from)) return;
        if (part == Tray && e.OriginalSource != Tray) return;            // a tray icon: its own drag
        if (AtInnerEdge(e.GetCurrentPoint(Root).Position)) return;         // the size of the dock is dragged there
        var follow = DispatcherQueue.CreateTimer();
        follow.Interval = TimeSpan.FromMilliseconds(12);
        bool dragging = false;
        follow.Tick += (_, _) =>
        {
            if (!Native.GetCursorPos(out var now)) return;
            bool down = (Native.GetAsyncKeyState(0x01) & 0x8000) != 0;
            if (!dragging && Math.Abs(now.X - from.X) + Math.Abs(now.Y - from.Y) >= 8)
            {
                dragging = true;
                part.ReleasePointerCaptures();                            // no click of the button when it is let go
                part.Opacity = 0.4;
                ResetMagnification();
            }
            var place = dragging ? PartPlace(part, now) : default;
            if (dragging)
            {
                if (place.Before != null) Marker?.At(place.Before, -1, rows: Vertical);
                else if (place.Last != null) Marker?.At(place.Last, 1, rows: Vertical);
                else Marker?.Hide();
            }
            if (down) return;
            follow.Stop();
            if (!dragging) return;
            part.Opacity = 1;
            Marker?.Hide();
            var parts = Parts;
            var list = Row.Children.Concat(RowRight.Children).OfType<FrameworkElement>().Where(c => c != part).ToList();
            list.Insert(Math.Clamp(place.Index, 0, list.Count), part);
            S.DockOrder = string.Join("|", list.Select(el => parts.First(x => x.El == el).Id));
            S.Save();
        };
        follow.Start();
    }

    /// <summary>The inner edge of the dock (6 points, and the grip shown in edit mode): its size is dragged there.</summary>
    bool AtInnerEdge(Windows.Foundation.Point at) =>
        Edge switch { 0 => at.Y < 6, 3 => at.Y > Root.ActualHeight - 6, 1 => at.X > Root.ActualWidth - 6, _ => at.X < 6 };

    /// <summary>
    /// The grips of edit mode (it was not to be seen where to drag): at the inner edge the size of the dock, at its two ends
    /// its width (the spacing of the apps – the dock is as long as its content).
    /// </summary>
    readonly Border _grip = Grip(), _gripStart = Grip(), _gripEnd = Grip();

    static Border Grip() => new() { CornerRadius = new CornerRadius(2), Visibility = Visibility.Collapsed, Background = ThemeBrush.Accent };

    void ShowGrip()
    {
        foreach (var g in new[] { _grip, _gripStart, _gripEnd })
            if (g.Parent == null) { Canvas.SetZIndex(g, 900); Root.Children.Add(g); }
        bool edit = S.DockEditMode, ends = edit && (Vertical || S.DockFull == 0);   // a full-width dock has the width of the screen
        _grip.Visibility = edit ? Visibility.Visible : Visibility.Collapsed;
        _gripStart.Visibility = _gripEnd.Visibility = ends ? Visibility.Visible : Visibility.Collapsed;
        // the size: across the middle of the inner edge
        _grip.Width = Vertical ? 4 : 44;
        _grip.Height = Vertical ? 44 : 4;
        _grip.HorizontalAlignment = Edge switch { 1 => HorizontalAlignment.Right, 2 => HorizontalAlignment.Left, _ => HorizontalAlignment.Center };
        _grip.VerticalAlignment = Edge switch { 0 => VerticalAlignment.Top, 3 => VerticalAlignment.Bottom, _ => VerticalAlignment.Center };
        _grip.Margin = Edge switch { 0 => new Thickness(0, -2, 0, 0), 3 => new Thickness(0, 0, 0, -2), 1 => new Thickness(0, 0, -2, 0), _ => new Thickness(-2, 0, 0, 0) };
        // the width: at the two ends, in the middle across
        foreach (var (g, start) in new[] { (_gripStart, true), (_gripEnd, false) })
        {
            g.Width = Vertical ? 24 : 4;
            g.Height = Vertical ? 4 : 24;
            g.HorizontalAlignment = Vertical ? HorizontalAlignment.Center : start ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            g.VerticalAlignment = !Vertical ? VerticalAlignment.Center : start ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            g.Margin = Vertical ? new Thickness(0, start ? -2 : 0, 0, start ? 0 : -2) : new Thickness(start ? -2 : 0, 0, start ? 0 : -2, 0);
        }
    }

    /// <summary>
    /// The width of the dock follows the mouse while a grip at an end is held: outwards wider, inwards narrower. The spacing
    /// of the apps changes, both ends of the dock move (it stays in the middle), so the change is shared by all gaps.
    /// </summary>
    void StartWiden(bool atStart)
    {
        if (!Native.GetCursorPos(out var from)) return;
        double spacing = S.IconSpacing;
        int gaps = Math.Max(1, Apps.Children.OfType<FrameworkElement>().Count(c => c.Visibility == Visibility.Visible) - 1);
        // the length now (the dock stays in the middle, so both ends move) and the longest it can be
        double length = (Vertical ? _body.Bottom - _body.Top : _body.Right - _body.Left) / Scale;
        double longest = (Vertical ? _mon.Work.Bottom - _mon.Work.Top : _mon.Work.Right - _mon.Work.Left) / Scale;
        ResetMagnification();
        _sizing = true;
        var follow = DispatcherQueue.CreateTimer();
        follow.Interval = TimeSpan.FromMilliseconds(16);
        follow.Tick += (_, _) =>
        {
            if (!Native.GetCursorPos(out var c)) return;
            double d = (Vertical ? c.Y - from.Y : c.X - from.X) * (atStart ? -1 : 1) / Scale;   // outwards, in points
            if (S.WidenSpreadsIcons)
            {
                double now = Math.Round(Math.Clamp(spacing + 2 * d / gaps, 0, 40));
                if (now != S.IconSpacing) S.IconSpacing = now;
            }
            else
            {
                // free room after the apps (the icons keep their spacing); narrower than the apps need: back to automatic
                double now = Math.Round(Math.Min(length + 2 * d, longest));
                if (now <= length - _fill - 1) now = 0;
                if (now != S.DockMinLength) S.DockMinLength = now;
            }
            if ((Native.GetAsyncKeyState(0x01) & 0x8000) != 0) return;
            follow.Stop();
            _sizing = false;
            S.Save();
        };
        follow.Start();
    }

    bool _sizing;

    /// <summary>The size of the dock follows the mouse while it is held at the inner edge (inwards makes it bigger).</summary>
    void StartResize()
    {
        if (!Native.GetCursorPos(out var from)) return;
        double size = S.IconSize;
        ResetMagnification();
        _sizing = true;
        var follow = DispatcherQueue.CreateTimer();
        follow.Interval = TimeSpan.FromMilliseconds(16);
        follow.Tick += (_, _) =>
        {
            if (!Native.GetCursorPos(out var c)) return;
            double d = Edge switch { 0 => from.Y - c.Y, 3 => c.Y - from.Y, 1 => c.X - from.X, _ => from.X - c.X } / Scale;   // screen pixels to points
            double now = Math.Round(Math.Clamp(size + d, 24, 96));
            if (now != S.IconSize) S.IconSize = now;
            if ((Native.GetAsyncKeyState(0x01) & 0x8000) != 0) return;
            follow.Stop();
            _sizing = false;
            S.Save();
        };
        follow.Start();
    }

    Windows.Foundation.Point? _edgeDragFrom;

    /// <summary>
    /// Edit mode: at the inner edge the size of the dock is dragged (also over its buttons); by an empty place the dock is
    /// dragged to the edge of the monitor nearest to the mouse.
    /// </summary>
    void HookEdgeDrag()
    {
        _grip.PointerPressed += (_, e) => { if (S.DockEditMode && e.GetCurrentPoint(_grip).Properties.IsLeftButtonPressed) { e.Handled = true; StartResize(); } };
        _gripStart.PointerPressed += (_, e) => { if (S.DockEditMode && e.GetCurrentPoint(_gripStart).Properties.IsLeftButtonPressed) { e.Handled = true; StartWiden(atStart: true); } };
        _gripEnd.PointerPressed += (_, e) => { if (S.DockEditMode && e.GetCurrentPoint(_gripEnd).Properties.IsLeftButtonPressed) { e.Handled = true; StartWiden(atStart: false); } };
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (!S.DockEditMode || !e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed) return;
            if (e.OriginalSource == _grip || e.OriginalSource == _gripStart || e.OriginalSource == _gripEnd) return;   // the grips themselves
            var at = e.GetCurrentPoint(Root).Position;
            if (AtInnerEdge(at)) { (e.OriginalSource as UIElement)?.ReleasePointerCaptures(); e.Handled = true; StartResize(); return; }
            if (e.OriginalSource != Root && e.OriginalSource != Row && e.OriginalSource != RowRight) return;
            _edgeDragFrom = at;
            Root.CapturePointer(e.Pointer);
            e.Handled = true;
        }), true);
        Root.PointerReleased += (_, e) =>
        {
            if (_edgeDragFrom is not { } from) return;
            _edgeDragFrom = null;
            Root.ReleasePointerCaptures();
            var to = e.GetCurrentPoint(Root).Position;
            if (Math.Abs(to.X - from.X) + Math.Abs(to.Y - from.Y) < 40 || !Native.GetCursorPos(out var c)) return;
            var m = _mon.Bounds;
            var near = new[] { (Edge: 0, At: m.Bottom - c.Y), (Edge: 1, At: c.X - m.Left), (Edge: 2, At: m.Right - c.X), (Edge: 3, At: c.Y - m.Top) };
            S.DockPosition = near.Where(n => n.Edge != 3 || !S.TopBar).OrderBy(n => n.At).First().Edge;
        };
    }

    void StartDrag(DockEntry entry, DragStartingEventArgs e)
    {
        if (entry.Pin == null) { e.Cancel = true; return; }
        _dragPin = entry.Pin;
        ResetMagnification();
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.SetText("hemidock-pin");
        if (entry.Pin.Path is { } path && File.Exists(path)) e.AllowedOperations = DataPackageOperation.Move | DataPackageOperation.Copy | DataPackageOperation.Link;
    }

    void Root_DragOver(object sender, DragEventArgs e)
    {
        if (_dragPin != null) { e.AcceptedOperation = DataPackageOperation.Move; e.DragUIOverride.IsCaptionVisible = false; return; }
        if (e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(StandardDataFormats.WebLink) || e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.Link;
            e.DragUIOverride.Caption = L.T("Pin to dock");
        }
    }

    /// <summary>A pin to a new place among the pins (the index counts the pins before it).</summary>
    void MovePin(PinnedApp moved, int index)
    {
        var pins = DockState.Load();
        int from = pins.FindIndex(x => x.Path == moved.Path && x.AppId == moved.AppId && x.Spacer == moved.Spacer && x.Line == moved.Line && x.Name == moved.Name);
        if (from < 0) return;
        var item = pins[from];
        pins.RemoveAt(from);
        if (from < index) index--;
        pins.Insert(Math.Clamp(index, 0, pins.Count), item);
        DockState.Save(pins);
        _shown = null;
        Later();
    }

    // ───────────────────────── dragging a pinned app with the mouse ─────────────────────────

    bool _pinDragged;

    /// <summary>
    /// A pinned app is pressed: moved by a few pixels it is dragged – its picture goes with the mouse, a line shows its new
    /// place, let go it moves there (onto the Recycle Bin: unpinned). The drag of WinUI did not start on the buttons of the
    /// dock; the mouse is followed through Windows itself.
    /// </summary>
    void PressPin(DockEntry entry, Button button, PointerRoutedEventArgs e)
    {
        if (entry.Pin == null || !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed || !Native.GetCursorPos(out var from)) return;
        if (S.DockEditMode && AtInnerEdge(e.GetCurrentPoint(Root).Position)) return;   // the size of the dock is dragged there
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
                ResetMagnification();
                PreviewWindow.HideNow();
                button.Opacity = 0.3;
                bool isFile = entry.Pin.AppId == null;
                var picture = isFile ? AppShell.FileTypeIcon(entry.IconPath) : AppShell.Icon(entry.IconPath);
                DragGhost.Show(picture, (int)Math.Round(IconPx * Scale), now.X, now.Y);
            }
            if (dragging) { DragGhost.Move(now.X, now.Y); ShowPinPlace(now); }
            if (down) return;
            follow.Stop();
            if (!dragging) return;                                        // a click: the button opens the app itself
            DragGhost.Hide();
            Marker.Hide();
            button.Opacity = 1;
            var bin = ScreenRect(BinButton);
            if (BinButton.Visibility == Visibility.Visible && now.X >= bin.Left && now.X < bin.Right && now.Y >= bin.Top && now.Y < bin.Bottom)
                EditPins(p => p.Remove(entry.Pin));                       // onto the Recycle Bin: unpinned
            else if (PinPlace(now) is { } place) MovePin(entry.Pin, place.Index);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _pinDragged = false);   // after the click it would have been
        };
        follow.Start();
    }

    /// <summary>The new place of a dragged pin at a screen point: how many pins go before it, and where the line goes.</summary>
    (int Index, Native.RECT Mark)? PinPlace(Native.POINT p)
    {
        var dock = ScreenRect(Root);
        int room = (int)Math.Round(30 * Scale);
        if (p.X < dock.Left - room || p.X > dock.Right + room || p.Y < dock.Top - room || p.Y > dock.Bottom + room) return null;
        int index = 0;
        Native.RECT? mark = null, last = null;
        foreach (var child in Apps.Children.OfType<FrameworkElement>())
        {
            if (child.Tag is not DockEntry { Pin: not null } || child.Visibility != Visibility.Visible) continue;
            var r = ScreenRect(child);
            if ((Vertical ? (r.Top + r.Bottom) / 2 : (r.Left + r.Right) / 2) > (Vertical ? p.Y : p.X))
            {
                mark = Vertical ? new Native.RECT { Left = r.Left, Right = r.Right, Top = r.Top, Bottom = r.Top } : new Native.RECT { Left = r.Left, Right = r.Left, Top = r.Top, Bottom = r.Bottom };
                break;
            }
            index++;
            last = r;
        }
        if (mark == null && last is { } l)
            mark = Vertical ? new Native.RECT { Left = l.Left, Right = l.Right, Top = l.Bottom, Bottom = l.Bottom } : new Native.RECT { Left = l.Right, Right = l.Right, Top = l.Top, Bottom = l.Bottom };
        return mark is { } m ? (index, m) : null;
    }

    void ShowPinPlace(Native.POINT p)
    {
        if (PinPlace(p) is { } place)
            Marker.Line(this, place.Mark.Left - (Vertical ? 0 : (int)Math.Round(Apps.Spacing * Scale / 2)), place.Mark.Top - (Vertical ? (int)Math.Round(Apps.Spacing * Scale / 2) : 0),
                Vertical ? place.Mark.Right - place.Mark.Left : place.Mark.Bottom - place.Mark.Top, Vertical);
        else Marker.Hide();
    }

    async void Root_Drop(object sender, DragEventArgs e)
    {
        if (_dragPin is { } moved)
        {
            // the new place among the icons: before the first one whose middle is past the mouse
            var p = e.GetPosition(Apps);
            double at = Vertical ? p.Y : p.X;
            int index = 0;
            foreach (var child in Apps.Children.OfType<FrameworkElement>())
            {
                var c = child.TransformToVisual(Apps).TransformPoint(new Windows.Foundation.Point(child.ActualWidth / 2, child.ActualHeight / 2));
                if ((Vertical ? c.Y : c.X) > at) break;
                if (child.Tag is DockEntry { Pin: not null }) index++;
            }
            _dragPin = null;
            MovePin(moved, index);
            return;
        }
        var deferral = e.GetDeferral();
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
                foreach (var i in await e.DataView.GetStorageItemsAsync()) { if (!string.IsNullOrEmpty(i.Path)) Pin(i.Path); }
            else if (e.DataView.Contains(StandardDataFormats.WebLink)) { if (WebPins.UrlOf((await e.DataView.GetWebLinkAsync()).AbsoluteUri) is { } url) WebPins.Pin(url); }
            else if (e.DataView.Contains(StandardDataFormats.Text) && WebPins.UrlOf(await e.DataView.GetTextAsync()) is { } url) WebPins.Pin(url);
        }
        catch (Exception ex) { Log.Write("Drop", ex); }
        deferral.Complete();
    }

    void Bin_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_dragPin != null) { e.AcceptedOperation = DataPackageOperation.Move; e.DragUIOverride.Caption = L.T("Unpin from dock"); return; }
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Move : DataPackageOperation.None;
    }

    async void Bin_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_dragPin is { } pin)
        {
            _dragPin = null;
            var pins = DockState.Load();
            pins.RemoveAll(x => x.Path == pin.Path && x.AppId == pin.AppId && x.Spacer == pin.Spacer && x.Line == pin.Line && x.Name == pin.Name);
            DockState.Save(pins);
            return;
        }
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try { RecycleBin.Delete((await e.DataView.GetStorageItemsAsync()).Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList()); }
        catch (Exception ex) { Log.Write("Recycle Bin drop", ex); }
        deferral.Complete();
        _binCount = -2;
        UpdateBin();
    }
}
