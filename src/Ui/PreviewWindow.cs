using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace HeMiDock;

/// <summary>
/// Live thumbnails of the windows of an app above its icon (like the previews of the taskbar). The pictures are drawn by
/// Windows (DWM thumbnails) into empty places of the window. Resting on a card for a moment shows that window alone
/// (Aero Peek). One window that is only hidden and shown again (see <see cref="PopupWindow"/> on the memory).
/// </summary>
sealed class PreviewWindow : Window
{
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("dwmapi.dll", EntryPoint = "#113")] static extern int DwmpActivateLivePreview(uint peekOn, IntPtr peekWindow, IntPtr topmostWindow, uint peekType, IntPtr unknown);

    const double ThumbHeight = 128;
    const int MaxThumbnails = 6;

    static PreviewWindow _one;

    readonly IntPtr _hwnd;
    readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
    readonly List<(FrameworkElement Host, IntPtr Thumb)> _thumbs = new();
    readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    TaskWindow _peekCandidate;
    bool _peeking, _visible;
    Native.RECT _anchor;
    MonInfo _mon;
    int _edge;

    /// <summary>The app whose windows are shown now (null while hidden).</summary>
    public static DockEntry Shown => _one is { _visible: true } ? _one._entry : null;
    /// <summary>The mouse is over the preview.</summary>
    public static bool Hovered { get; private set; }
    DockEntry _entry;

    PreviewWindow()
    {
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SystemBackdrop = new AlwaysActiveAcrylic();
        ExtendsContentIntoTitleBar = false;                         // true draws a dark line along the top edge
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        NoFrame.HideFromSwitchers(AppWindow, WinRT.Interop.WindowNative.GetWindowHandle(this));
        NoFrame.Apply(_hwnd);
        int round = 2;
        Win32.DwmSetWindowAttribute(_hwnd, 33, ref round, 4);
        int exclude = 1;
        Win32.DwmSetWindowAttribute(_hwnd, 12, ref exclude, 4);                // DWMWA_EXCLUDED_FROM_PEEK
        var root = new Grid { Children = { _panel } };
        root.PointerEntered += (_, _) => Hovered = true;
        root.PointerExited += (_, _) => Hovered = false;
        root.PointerWheelChanged += (_, e) =>
        {
            e.Handled = true;
            var list = _entry?.Windows;
            if (list is not { Count: > 1 }) return;
            int at = list.FindIndex(w => w.Active);
            Bring(list[((at < 0 ? 0 : at) + (e.GetCurrentPoint(root).Properties.MouseWheelDelta < 0 ? 1 : -1) + list.Count) % list.Count]);
        };
        root.LayoutUpdated += (_, _) => UpdateThumbnails();
        Content = root;
        ThemeWatch.Track(this);
        _peekTimer.Tick += (_, _) => { _peekTimer.Stop(); StartPeek(); };
    }

    /// <summary>Shows the windows of an app next to its icon (pixels of the screen; edge of the dock 0 bottom, 1 left, 2 right, 3 top).</summary>
    public static void Show(DockEntry entry, Native.RECT icon, int edge, MonInfo mon)
    {
        if (entry.Windows.Count == 0) { HideNow(); return; }
        _one ??= new PreviewWindow();
        _one._entry = entry;
        _one._anchor = icon;
        _one._edge = edge;
        _one._mon = mon;
        _one.Build();
        _one._visible = true;
        _one.Place();
        _one.AppWindow.Show(false);                                          // without taking the focus from the app in front
    }

    public static void HideNow()
    {
        if (_one is not { _visible: true } w) return;
        w._visible = false;
        Hovered = false;
        w._peekTimer.Stop();
        w.StopPeek();
        w.Clear();
        w.AppWindow.Hide();
    }

    double Scale => GetDpiForWindow(_hwnd) / 96.0;

    void Clear()
    {
        foreach (var t in _thumbs) if (t.Thumb != IntPtr.Zero) Native.DwmUnregisterThumbnail(t.Thumb);
        _thumbs.Clear();
        _panel.Children.Clear();
    }

    void Build()
    {
        Clear();
        bool withThumbs = _entry.Windows.Count <= MaxThumbnails;
        _panel.Orientation = withThumbs ? (_edge is 1 or 2 ? Orientation.Vertical : Orientation.Horizontal) : Orientation.Vertical;
        var fg = ThemeBrush.Get("TextFillColorPrimaryBrush");

        var windows = new List<(TaskWindow W, IntPtr Thumb, double Width)>();
        foreach (var w in _entry.Windows)
        {
            IntPtr thumb = IntPtr.Zero;
            double width = 200;
            if (withThumbs && !Win32.IsIconic(w.Handle) && Native.DwmRegisterThumbnail(_hwnd, w.Handle, out thumb) == 0)
            {
                Native.DwmQueryThumbnailSourceSize(thumb, out var size);
                if (size.cy > 0) width = Math.Clamp(ThumbHeight * size.cx / size.cy, 110, 260);
            }
            windows.Add((w, thumb, width));
        }
        // smaller when the row would be wider than the screen
        double s = Scale;
        double row = windows.Sum(x => x.Width + 16) + 12;
        double fit = withThumbs && _panel.Orientation == Orientation.Horizontal ? Math.Clamp(((_mon.Bounds.Right - _mon.Bounds.Left) / s - 40) / row, 0.5, 1) : 1;

        foreach (var (w, thumb, rowWidth) in windows)
        {
            double width = rowWidth * fit;
            var close = new Button
            {
                Width = 24, Height = 24, Padding = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
                Content = new FontIcon { Glyph = "", FontSize = 10 }
            };
            ToolTipService.SetToolTip(close, L.T("Close window"));
            close.PointerEntered += (_, _) => close.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0xC4, 0x2B, 0x1C));
            close.PointerExited += (_, _) => close.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            close.Click += (_, _) => Launcher.Close(w);

            var icon = new Image { Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) };
            Pictures.Into(icon, _entry.Pin?.IconPath ?? _entry.IconPath, isFile: _entry.Pin?.IconPath != null);
            var header = new Grid { Margin = new Thickness(2, 0, 0, 6), Width = withThumbs ? width : 320 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock { Text = string.IsNullOrWhiteSpace(w.Title) ? _entry.Name : w.Title, Foreground = fg, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 1);
            Grid.SetColumn(close, 2);
            header.Children.Add(icon);
            header.Children.Add(title);
            header.Children.Add(close);

            var stack = new StackPanel { Children = { header } };
            FrameworkElement host = null;
            if (withThumbs)
            {
                var big = new Image { Width = 48, Height = 48, Opacity = 0.8 };
                Pictures.Into(big, _entry.Pin?.IconPath ?? _entry.IconPath, isFile: _entry.Pin?.IconPath != null);
                host = new Grid { Width = width, Height = ThumbHeight * fit, Children = { big } };
                stack.Children.Add(host);
            }
            var card = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Child = stack };
            card.PointerEntered += (_, _) => { card.Background = ThemeBrush.Get("SubtleFillColorSecondaryBrush"); _peekCandidate = w; _peekTimer.Start(); };
            card.PointerExited += (_, _) => { card.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); _peekTimer.Stop(); StopPeek(); };
            card.PointerReleased += (_, e) =>
            {
                var kind = e.GetCurrentPoint(card).Properties.PointerUpdateKind;
                if (kind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased) { Launcher.Close(w); return; }
                if (kind != Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased) return;
                Bring(w);
                HideNow();
            };
            _panel.Children.Add(card);
            _thumbs.Add((host, thumb));
        }
    }

    static void Bring(TaskWindow w)
    {
        if (Win32.IsIconic(w.Handle)) Win32.ShowWindow(w.Handle, Win32.SW_RESTORE);
        Fg.Force(w.Handle);
    }

    void UpdateThumbnails()
    {
        if (!_visible) return;
        double dpi = Scale;
        var root = (FrameworkElement)Content;
        foreach (var (host, thumb) in _thumbs)
        {
            if (thumb == IntPtr.Zero || host == null || host.ActualWidth <= 0) continue;
            Windows.Foundation.Point p;
            try { p = host.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0)); } catch { continue; }
            Native.DwmQueryThumbnailSourceSize(thumb, out var src);
            double w = host.ActualWidth, h = host.ActualHeight, x = p.X, y = p.Y;
            if (src.cx > 0 && src.cy > 0)
            {
                double scale = Math.Min(w / src.cx, h / src.cy);
                double tw = src.cx * scale, th = src.cy * scale;
                x += (w - tw) / 2; y += (h - th) / 2; w = tw; h = th;
            }
            var props = new Native.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new Native.RECT { Left = (int)(x * dpi), Top = (int)(y * dpi), Right = (int)((x + w) * dpi), Bottom = (int)((y + h) * dpi) },
                opacity = 255, fVisible = true, fSourceClientAreaOnly = false
            };
            Native.DwmUpdateThumbnailProperties(thumb, ref props);
        }
    }

    /// <summary>Next to the dock on the side away from its edge, in the middle of the icon, kept on the monitor.</summary>
    void Place()
    {
        var root = (FrameworkElement)Content;
        root.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double s = Scale;
        int w = (int)Math.Ceiling(root.DesiredSize.Width * s), h = (int)Math.Ceiling(root.DesiredSize.Height * s);
        int gap = (int)(10 * s), edge = (int)(8 * s);
        int cx = (_anchor.Left + _anchor.Right) / 2, cy = (_anchor.Top + _anchor.Bottom) / 2;
        int x, y;
        switch (_edge)
        {
            case 1: x = _anchor.Right + gap; y = cy - h / 2; break;
            case 2: x = _anchor.Left - w - gap; y = cy - h / 2; break;
            case 3: x = cx - w / 2; y = _anchor.Bottom + gap; break;
            default: x = cx - w / 2; y = _anchor.Top - h - gap; break;
        }
        var mon = _mon.Bounds;
        x = Math.Clamp(x, mon.Left + edge, Math.Max(mon.Left + edge, mon.Right - w - edge));
        y = Math.Clamp(y, mon.Top + edge, Math.Max(mon.Top + edge, mon.Bottom - h - edge));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
    }

    void StartPeek()
    {
        if (_peekCandidate == null || Win32.IsIconic(_peekCandidate.Handle)) return;
        try { DwmpActivateLivePreview(1, _peekCandidate.Handle, _hwnd, 1, IntPtr.Zero); _peeking = true; } catch { }
    }

    void StopPeek()
    {
        if (!_peeking) return;
        try { DwmpActivateLivePreview(0, IntPtr.Zero, _hwnd, 1, IntPtr.Zero); } catch { }
        _peeking = false;
    }
}
