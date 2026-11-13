using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace HeMiDock;

/// <summary>
/// Own zones: the picture of the screen (in its proportions). A click divides a zone at the mouse – into columns or rows
/// as chosen above it (the right button the other way); a line under the mouse shows where. The lines between zones are
/// dragged to set their sizes exactly (by 1 %, the share is shown while dragging).
/// </summary>
static class ZoneEditor
{
    const double W = 560;
    const double Snap = 0.01, Smallest = 0.05;                         // 1 % steps, no zone smaller than 5 %
    const double Grab = 6;                                             // points from a line where it is taken

    /// <summary>
    /// Asks for an own layout over a window of the app: <paramref name="index"/> of an own layout to change, -1 for a new one.
    /// When confirmed it is stored (with its name) and chosen.
    /// </summary>
    public static async Task<bool> AskAsync(XamlRoot root, int index)
    {
        var s = DockSettings.Current;
        var layouts = s.OwnZoneLayouts;
        bool isNew = index < 0 || index >= layouts.Count;
        var mon = Monitors.Primary();
        double ratio = Math.Clamp((mon.Work.Bottom - mon.Work.Top) / (double)Math.Max(1, mon.Work.Right - mon.Work.Left), 0.3, 1.2);
        double H = Math.Round(W * ratio);
        var zones = isNew ? new List<Zone>() : Parse(layouts[index].Zones);
        string oldName = isNew ? "" : layouts[index].Name.Length > 0 ? layouts[index].Name : L.T("Custom");
        var name = new TextBox
        {
            Header = L.T("Name"), Width = 300, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10),
            Text = isNew ? L.T("My layout {0}", layouts.Count + 1) : oldName
        };
        if (zones.Count == 0) zones = new List<Zone> { new(0, 0, 1, 1) };
        var undo = new Stack<List<Zone>>();
        bool columns = true;

        var canvas = new Canvas { Width = W, Height = H, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        canvas.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, W, H) };
        var guide = new Rectangle { Fill = ThemeBrush.Accent, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        var share = new TextBlock { FontSize = 12, IsHitTestVisible = false, Visibility = Visibility.Collapsed, Foreground = ThemeBrush.Get("TextFillColorPrimaryBrush") };

        void Draw()
        {
            canvas.Children.Clear();
            for (int i = 0; i < zones.Count; i++)
            {
                var z = zones[i];
                var box = new Border
                {
                    Width = Math.Max(0, z.W * W - 4), Height = Math.Max(0, z.H * H - 4), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(2),
                    BorderBrush = ThemeBrush.Accent, Background = ThemeBrush.Get("ControlFillColorDefaultBrush"), IsHitTestVisible = false,
                    Child = new TextBlock { Text = $"{i + 1}\n{z.W * 100:0} × {z.H * 100:0} %", TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 }
                };
                Canvas.SetLeft(box, z.X * W + 2);
                Canvas.SetTop(box, z.Y * H + 2);
                canvas.Children.Add(box);
            }
            canvas.Children.Add(guide);
            canvas.Children.Add(share);
        }

        int ZoneAt(double x, double y) => zones.FindIndex(z => x >= z.X && x < z.X + z.W && y >= z.Y && y < z.Y + z.H);

        // a line between zones near the point: its place (0..1) and whether it is upright (between columns)
        (double At, bool Upright)? LineAt(Windows.Foundation.Point p)
        {
            double x = p.X / W, y = p.Y / H;
            foreach (var z in zones)
            {
                bool inRows = y >= z.Y && y <= z.Y + z.H, inCols = x >= z.X && x <= z.X + z.W;
                double right = z.X + z.W, bottom = z.Y + z.H;
                if (inRows && right < 0.999 && Math.Abs(p.X - right * W) <= Grab) return (right, true);
                if (inCols && bottom < 0.999 && Math.Abs(p.Y - bottom * H) <= Grab) return (bottom, false);
            }
            return null;
        }

        void ShowGuide(Windows.Foundation.Point p, bool upright, int zone)
        {
            if (zone < 0) { guide.Visibility = Visibility.Collapsed; return; }
            var z = zones[zone];
            double at = upright ? Math.Round(p.X / W / Snap) * Snap : Math.Round(p.Y / H / Snap) * Snap;
            guide.Width = upright ? 2 : z.W * W;
            guide.Height = upright ? z.H * H : 2;
            Canvas.SetLeft(guide, upright ? at * W - 1 : z.X * W);
            Canvas.SetTop(guide, upright ? z.Y * H : at * H - 1);
            guide.Visibility = Visibility.Visible;
        }

        void Split(Windows.Foundation.Point p, bool upright)
        {
            double x = p.X / W, y = p.Y / H;
            int i = ZoneAt(x, y);
            if (i < 0) return;
            var z = zones[i];
            double at = Math.Round((upright ? x : y) / Snap) * Snap, first = upright ? at - z.X : at - z.Y, size = upright ? z.W : z.H;
            if (first < Smallest || size - first < Smallest) return;
            undo.Push(new List<Zone>(zones));
            zones[i] = upright ? z with { W = first } : z with { H = first };
            zones.Add(upright ? new Zone(at, z.Y, size - first, z.H) : new Zone(z.X, at, z.W, size - first));
            Draw();
        }

        // dragging a line: every zone that ends or starts at it follows (the line is moved as a whole)
        (double At, bool Upright)? dragging = null;
        List<Zone> before = null;
        void Move(Windows.Foundation.Point p)
        {
            if (dragging is not { } d) return;
            double to = Math.Round((d.Upright ? p.X / W : p.Y / H) / Snap) * Snap;
            bool Starts(Zone z) => Math.Abs((d.Upright ? z.X : z.Y) - d.At) < 0.001;
            bool Ends(Zone z) => Math.Abs((d.Upright ? z.X + z.W : z.Y + z.H) - d.At) < 0.001;
            // no zone on either side smaller than the smallest
            foreach (var z in before)
            {
                if (Ends(z)) to = Math.Max(to, (d.Upright ? z.X : z.Y) + Smallest);
                if (Starts(z)) to = Math.Min(to, (d.Upright ? z.X + z.W : z.Y + z.H) - Smallest);
            }
            zones = before.Select(z => d.Upright
                ? Starts(z) ? z with { X = to, W = z.X + z.W - to } : Ends(z) ? z with { W = to - z.X } : z
                : Starts(z) ? z with { Y = to, H = z.Y + z.H - to } : Ends(z) ? z with { H = to - z.Y } : z).ToList();
            Draw();
            share.Text = $"{to * 100:0} %";
            Canvas.SetLeft(share, d.Upright ? Math.Min(to * W + 6, W - 44) : 6);
            Canvas.SetTop(share, d.Upright ? 6 : Math.Min(to * H + 4, H - 20));
            share.Visibility = Visibility.Visible;
        }

        canvas.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(canvas);
            e.Handled = true;
            if (point.Properties.IsLeftButtonPressed && LineAt(point.Position) is { } line)
            {
                undo.Push(new List<Zone>(zones));
                dragging = line;
                before = new List<Zone>(zones);
                guide.Visibility = Visibility.Collapsed;
                canvas.CapturePointer(e.Pointer);
                return;
            }
            if (point.Properties.IsLeftButtonPressed) Split(point.Position, columns);
            else if (point.Properties.IsRightButtonPressed) Split(point.Position, !columns);
        };
        canvas.PointerMoved += (_, e) =>
        {
            var p = e.GetCurrentPoint(canvas).Position;
            if (dragging != null) { Move(p); return; }
            if (LineAt(p) is { } line)                                   // over a line: it can be dragged
            {
                guide.Visibility = Visibility.Collapsed;
                ProtectedCursorHost.Set(canvas, line.Upright ? Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast : Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth);
                return;
            }
            ProtectedCursorHost.Set(canvas, Microsoft.UI.Input.InputSystemCursorShape.Cross);
            ShowGuide(p, columns, ZoneAt(p.X / W, p.Y / H));
        };
        canvas.PointerReleased += (_, e) =>
        {
            if (dragging == null) return;
            dragging = null;
            share.Visibility = Visibility.Collapsed;
            canvas.ReleasePointerCaptures();
        };
        canvas.PointerExited += (_, _) => { if (dragging == null) guide.Visibility = Visibility.Collapsed; };
        canvas.RightTapped += (_, e) => e.Handled = true;              // no menu of the settings over it

        // the direction of a click: columns or rows
        var byColumns = new RadioButton { Content = L.T("Columns"), IsChecked = true, GroupName = "zones", MinWidth = 0, Margin = new Thickness(0, 0, 12, 0) };
        var byRows = new RadioButton { Content = L.T("Rows"), GroupName = "zones", MinWidth = 0 };
        byColumns.Checked += (_, _) => columns = true;
        byRows.Checked += (_, _) => columns = false;

        var back = new Button { Content = L.T("Back"), Margin = new Thickness(0, 10, 6, 0) };
        back.Click += (_, _) => { if (undo.Count > 0) { zones = undo.Pop(); Draw(); } };
        var again = new Button { Content = L.T("Start again"), Margin = new Thickness(0, 10, 0, 0) };
        again.Click += (_, _) => { undo.Push(zones); zones = new List<Zone> { new(0, 0, 1, 1) }; Draw(); };
        Draw();

        var dialog = new ContentDialog
        {
            Title = L.T(isNew ? "New zone layout" : "Edit zone layout"), XamlRoot = root, PrimaryButtonText = L.T("OK"), CloseButtonText = L.T("Cancel"), DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Children =
                {
                    name,
                    new TextBlock { Text = L.T("A click divides a zone (the right button the other way). Drag the lines between zones to set their size."), FontSize = 12.5, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap, MaxWidth = W },
                    new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8), Children = { new TextBlock { Text = L.T("Divide into:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) }, byColumns, byRows } },
                    canvas,
                    new StackPanel { Orientation = Orientation.Horizontal, Children = { back, again } }
                }
            }
        };
        dialog.Resources["ContentDialogMaxWidth"] = W + 80;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        var c = System.Globalization.CultureInfo.InvariantCulture;
        string stored = string.Join(";", zones.Select(z => string.Create(c, $"{z.X * 100:0.##},{z.Y * 100:0.##},{z.W * 100:0.##},{z.H * 100:0.##}")));
        string title = name.Text.Trim();
        if (title.Length == 0) title = isNew ? L.T("My layout {0}", layouts.Count + 1) : oldName;
        if (isNew) { layouts.Add((title, stored)); index = layouts.Count - 1; }
        else layouts[index] = (title, stored);
        s.OwnZoneLayouts = layouts;
        s.ZoneLayout = 10 + index;
        return true;
    }

    /// <summary>Removes an own layout; the choice stays on the same layout (or goes to the first one when that one was removed).</summary>
    public static void Remove(int index)
    {
        var s = DockSettings.Current;
        var layouts = s.OwnZoneLayouts;
        if (index < 0 || index >= layouts.Count) return;
        int chosen = s.OwnZoneLayout;
        layouts.RemoveAt(index);
        s.OwnZoneLayouts = layouts;
        if (chosen == index) s.ZoneLayout = 0;
        else if (chosen > index) s.ZoneLayout = 10 + chosen - 1;
        else if (chosen >= 0) s.ZoneLayout = 10 + chosen;               // 4 of earlier versions becomes 10
    }

    record struct Zone(double X, double Y, double W, double H);

    static List<Zone> Parse(string stored)
    {
        var list = new List<Zone>();
        foreach (var part in (stored ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var v = part.Split(',').Select(x => double.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d / 100 : -1).ToArray();
            if (v.Length == 4 && v.All(x => x >= 0) && v[2] > 0 && v[3] > 0) list.Add(new Zone(v[0], v[1], v[2], v[3]));
        }
        return list;
    }
}

/// <summary>The mouse cursor over an element (its ProtectedCursor can be set only from inside the element: by reflection).</summary>
static class ProtectedCursorHost
{
    static readonly System.Reflection.PropertyInfo Cursor = typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    public static void Set(UIElement element, Microsoft.UI.Input.InputSystemCursorShape shape)
    {
        try { Cursor?.SetValue(element, Microsoft.UI.Input.InputSystemCursor.Create(shape)); } catch { }
    }
}
