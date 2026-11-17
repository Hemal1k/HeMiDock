using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HeMiDock;

/// <summary>
/// One tick a second for every dock and the top bar, at the start of each second: one timer instead of one in every bar, and
/// the clocks of all bars and monitors turn to the next minute at the same moment (they were up to a second apart).
/// </summary>
static class SecondTick
{
    static Action _tick;
    static DispatcherTimer _timer, _align;

    public static event Action Tick
    {
        add { _tick += value; Start(); }
        remove { _tick -= value; if (_tick == null) Stop(); }
    }

    static void Start()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => _tick?.Invoke();
        // the first tick just after the next full second, then every second
        _align = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000 - DateTime.Now.Millisecond + 15) };
        _align.Tick += (_, _) => { _align.Stop(); _align = null; _tick?.Invoke(); _timer?.Start(); };
        _align.Start();
    }

    static void Stop()
    {
        _align?.Stop(); _align = null;
        _timer?.Stop(); _timer = null;
    }
}

/// <summary>The parts the dock and the top bar both show, built the same way for both.</summary>
static class BarParts
{
    /// <summary>
    /// The virtual desktops as numbered buttons (the current one bold and highlighted): a click switches to it, the middle
    /// button closes it. <paramref name="newButton"/> makes a button in the look of the bar (with its tooltip).
    /// </summary>
    public static void FillDesktops(Panel panel, Func<string, Button> newButton)
    {
        panel.Children.Clear();
        var all = VirtualDesktops.All();
        int current = VirtualDesktops.CurrentIndex(all);
        for (int i = 0; i < all.Count; i++)
        {
            int index = i;
            var b = newButton(VirtualDesktops.Name(all, i));
            b.Content = new TextBlock { Text = (i + 1).ToString(), FontSize = 12, FontWeight = i == current ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal };
            if (i == current) b.Background = ThemeBrush.Get("SubtleFillColorSecondaryBrush");
            b.Click += (_, _) => VirtualDesktops.SwitchTo(index);
            b.PointerReleased += (_, e) => { if (e.GetCurrentPoint(b).Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased) VirtualDesktops.Close(index); };
            panel.Children.Add(b);
        }
    }

    /// <summary>
    /// The number of notifications: its place stays while it is empty (invisible) – the bar changed its length or the places of
    /// its parts whenever the last notification went.
    /// </summary>
    public static void ShowBadge(UIElement badge, TextBlock text)
    {
        int n = NotificationBadge.Count;
        badge.Visibility = DockSettings.Current.ShowNotificationCount ? Visibility.Visible : Visibility.Collapsed;
        badge.Opacity = n > 0 ? 1 : 0;
        text.Text = n > 99 ? "99+" : n > 0 ? n.ToString() : "0";
    }
}
