using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinSize = global::Windows.Foundation.Size;
using WinRect = global::Windows.Foundation.Rect;

namespace HeMiDock;

/// <summary>
/// The root of a pop-up: measures its content without limits (its own size, not cut to the window) and says when that size
/// changed, so the window follows it – a list that grows later (the Wi-Fi networks) is never taller than its window. Only on
/// a real change, during the layout itself: measuring the content from outside after every layout kept the layout busy.
/// A full-screen pop-up (<see cref="Fill"/>) gets the size of the window instead.
/// </summary>
sealed partial class PopupHost : Microsoft.UI.Xaml.Controls.Panel
{
    WinSize _last;

    public FrameworkElement Child
    {
        get => Children.Count > 0 ? Children[0] as FrameworkElement : null;
        set { Children.Clear(); if (value != null) Children.Add(value); }
    }

    /// <summary>The content fills the window (full-screen Start).</summary>
    public bool Fill { get => _fill; set { if (_fill == value) return; _fill = value; InvalidateMeasure(); } }
    bool _fill;

    /// <summary>
    /// The content keeps to the lower edge (a pop-up above a dock at the bottom): when it gets smaller (notifications
    /// cleared), it stays at the dock until the window follows, instead of leaving an empty strip there for a moment.
    /// </summary>
    public bool AtBottom { get => _atBottom; set { if (_atBottom == value) return; _atBottom = value; InvalidateArrange(); } }
    bool _atBottom;

    /// <summary>The size of the content changed (points).</summary>
    public event Action<WinSize> ContentSizeChanged;

    /// <summary>The size the content wants, without limits.</summary>
    public WinSize ContentSize
    {
        get
        {
            if (Child is not { } c) return default;
            if (!Fill) c.Measure(new WinSize(double.PositiveInfinity, double.PositiveInfinity));   // the same as the layout: nothing is measured again
            return c.DesiredSize;
        }
    }

    protected override WinSize MeasureOverride(WinSize available)
    {
        if (Child is not { } c) return default;
        if (Fill) { c.Measure(available); return available; }
        c.Measure(new WinSize(double.PositiveInfinity, double.PositiveInfinity));
        var size = c.DesiredSize;
        if (Math.Abs(size.Width - _last.Width) > 0.5 || Math.Abs(size.Height - _last.Height) > 0.5)
        {
            _last = size;
            var changed = ContentSizeChanged;
            if (changed != null) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => changed(size));
        }
        return new WinSize(Math.Min(size.Width, available.Width), Math.Min(size.Height, available.Height));
    }

    protected override WinSize ArrangeOverride(WinSize final)
    {
        if (Child is { } c)
        {
            double h = Fill ? final.Height : c.DesiredSize.Height;
            c.Arrange(new WinRect(0, !Fill && AtBottom ? Math.Max(0, final.Height - h) : 0, Fill ? final.Width : c.DesiredSize.Width, h));
        }
        return final;
    }
}
