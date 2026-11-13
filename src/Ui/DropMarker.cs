using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace HeMiDock;

/// <summary>
/// Where a dragged icon will go when it is let go: a short line in the accent colour between the icons (or a frame around a
/// place it goes into), over everything else of the window. Placed from screen pixels, as the drags follow the mouse there.
/// </summary>
sealed class DropMarker
{
    readonly Canvas _layer = new() { IsHitTestVisible = false };
    readonly Rectangle _line = new() { RadiusX = 1.5, RadiusY = 1.5, Visibility = Visibility.Collapsed };
    readonly Border _frame = new() { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6), Visibility = Visibility.Collapsed };
    readonly FrameworkElement _root;

    public DropMarker(Panel root)
    {
        _root = root;
        _layer.Children.Add(_line);
        _layer.Children.Add(_frame);
        Canvas.SetZIndex(_layer, 1000);
        if (root is Grid grid)
        {
            Grid.SetRowSpan(_layer, Math.Max(1, grid.RowDefinitions.Count));
            Grid.SetColumnSpan(_layer, Math.Max(1, grid.ColumnDefinitions.Count));
        }
        root.Children.Add(_layer);
    }

    Windows.Foundation.Point ToRoot(IPanelWindow host, int x, int y)
    {
        var origin = host.ScreenRect(_root);
        double scale = _root.XamlRoot?.RasterizationScale ?? 1;
        return new((x - origin.Left) / scale, (y - origin.Top) / scale);
    }

    /// <summary>A line between icons at a screen point; <paramref name="across"/> is its length in pixels (across the row).</summary>
    public void Line(IPanelWindow host, int x, int y, int across, bool vertical)
    {
        var p = ToRoot(host, x, y);
        double scale = _root.XamlRoot?.RasterizationScale ?? 1, len = across / scale;
        _line.Fill = ThemeBrush.Accent;
        _line.Width = vertical ? len : 3;
        _line.Height = vertical ? 3 : len;
        Canvas.SetLeft(_line, vertical ? p.X : p.X - 1.5);
        Canvas.SetTop(_line, vertical ? p.Y - 1.5 : p.Y);
        _line.Visibility = Visibility.Visible;
        _frame.Visibility = Visibility.Collapsed;
    }

    /// <summary>A frame around a place (the arrow of the hidden icons, a group in Start).</summary>
    public void Frame(IPanelWindow host, Native.RECT r)
    {
        var a = ToRoot(host, r.Left, r.Top);
        var b = ToRoot(host, r.Right, r.Bottom);
        _frame.BorderBrush = ThemeBrush.Accent;
        _frame.Width = Math.Max(4, b.X - a.X);
        _frame.Height = Math.Max(4, b.Y - a.Y);
        Canvas.SetLeft(_frame, a.X);
        Canvas.SetTop(_frame, a.Y);
        _frame.Visibility = Visibility.Visible;
        _line.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// At an element of the same window: <paramref name="where"/> −1 a line before it, 1 after it, 0 a frame around it
    /// (it goes into it); <paramref name="rows"/>: the elements are one under another.
    /// </summary>
    public void At(FrameworkElement el, int where, bool rows)
    {
        try
        {
            var p = el.TransformToVisual(_root).TransformPoint(new Windows.Foundation.Point(0, 0));
            double w = el.ActualWidth, h = el.ActualHeight;
            var accent = ThemeBrush.Accent;
            if (where == 0)
            {
                _frame.BorderBrush = accent;
                _frame.Width = w; _frame.Height = h;
                Canvas.SetLeft(_frame, p.X); Canvas.SetTop(_frame, p.Y);
                _frame.Visibility = Visibility.Visible;
                _line.Visibility = Visibility.Collapsed;
                return;
            }
            _line.Fill = accent;
            if (rows) { _line.Width = w * 0.8; _line.Height = 3; Canvas.SetLeft(_line, p.X + w * 0.1); Canvas.SetTop(_line, (where < 0 ? p.Y : p.Y + h) - 1.5); }
            else { _line.Width = 3; _line.Height = h * 0.8; Canvas.SetLeft(_line, (where < 0 ? p.X : p.X + w) - 1.5); Canvas.SetTop(_line, p.Y + h * 0.1); }
            _line.Visibility = Visibility.Visible;
            _frame.Visibility = Visibility.Collapsed;
        }
        catch { Hide(); }
    }

    public void Hide()
    {
        _line.Visibility = Visibility.Collapsed;
        _frame.Visibility = Visibility.Collapsed;
    }
}
