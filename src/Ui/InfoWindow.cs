using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HeMiDock;

/// <summary>
/// A short text in a small window in the middle above the dock for a moment, without taking the focus (the zone layout
/// switched by a shortcut). One window, shown again for every text.
/// </summary>
sealed class InfoWindow : Window
{
    static InfoWindow _one;

    readonly IntPtr _hwnd;
    readonly TextBlock _text = new() { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _hide;

    InfoWindow()
    {
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SystemBackdrop = new AlwaysActiveAcrylic();
        ExtendsContentIntoTitleBar = false;
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        NoFrame.HideFromSwitchers(AppWindow, _hwnd);
        NoFrame.Apply(_hwnd);
        int round = 2;
        Win32.DwmSetWindowAttribute(_hwnd, 33, ref round, 4);
        Content = new Grid { Padding = new Thickness(22, 12, 22, 12), Children = { _text } };
        ThemeWatch.Track(this);
        _hide = DispatcherQueue.CreateTimer();
        _hide.Interval = TimeSpan.FromMilliseconds(1500);
        _hide.IsRepeating = false;
        _hide.Tick += (_, _) => AppWindow.Hide();
    }

    public static void Show(string text)
    {
        _one ??= new InfoWindow();
        var w = _one;
        w._text.Text = text;
        // its size from the text, in the middle of the monitor of the mouse, above the dock
        var mon = Native.GetCursorPos(out var p) ? Monitors.All().FirstOrDefault(m => p.X >= m.Bounds.Left && p.X < m.Bounds.Right && p.Y >= m.Bounds.Top && p.Y < m.Bounds.Bottom) ?? Monitors.Primary() : Monitors.Primary();
        double scale = Native.MonitorDpi(mon.Handle) ?? 1;
        w._text.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        int width = (int)Math.Ceiling((w._text.DesiredSize.Width + 44) * scale), height = (int)Math.Ceiling((w._text.DesiredSize.Height + 24) * scale);
        var work = mon.Work;
        w.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32((work.Left + work.Right - width) / 2, work.Bottom - height - (int)(24 * scale), width, height));
        w.AppWindow.Show(false);                                        // without taking the focus from the app in front
        w._hide.Stop();
        w._hide.Start();
    }
}
