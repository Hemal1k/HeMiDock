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
    // ───────────────────────── pages ─────────────────────────

    void ShowHomePage()
    {
        if (_layout == 1) { ShowAllApps(false); return; }
        if (_main != null) _main.Content = _home;
    }

    /// <summary>Two columns: all apps instead of the pinned ones ("All apps ›" / "‹ Back").</summary>
    void ShowAllApps(bool all)
    {
        if (_layout != 1) return;
        _showAll = all;
        _all ??= new ScrollViewer { Content = _allList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _switchText.Text = L.T(all ? "Back" : "All apps");
        _switchGlyph.Glyph = all ? "" : "";
        _switchGlyph.HorizontalAlignment = all ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        _switchText.Margin = all ? new Thickness(22, 0, 0, 0) : new Thickness(0);
        _main.Content = all ? _all : _home;
        if (all) ((ScrollViewer)_all).ChangeView(null, 0, null, true);
    }

    /// <summary>The letter index: the letters with apps are bright, a click goes back to all apps at that letter.</summary>
    void ShowLetters()
    {
        var present = _groups.Select(g => g.Letter).ToHashSet();
        var letters = new[] { "#" }.Concat(Enumerable.Range('A', 26).Select(c => ((char)c).ToString()))
            .Concat(present.Where(l => l != "#" && (l.Length != 1 || l[0] < 'A' || l[0] > 'Z')).OrderBy(l => l, StringComparer.Ordinal)).ToList();   // other alphabets
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = FullScreen ? 9 : 5, ItemWidth = 58, ItemHeight = 50, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var l in letters)
        {
            bool on = present.Contains(l);
            var b = new Button { Style = DockButton, Width = 52, Height = 44, IsEnabled = on, Opacity = on ? 1 : 0.3, Content = new TextBlock { Text = l, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold } };
            b.Click += (_, _) => JumpTo(l);
            grid.Children.Add(b);
        }
        var page = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Children = { grid } };
        page.Tapped += (_, e) => { if (ReferenceEquals(e.OriginalSource, page) && !_tapEndedNaming) ShowHomePage(); };
        if (FullScreen) { ShowCard(page, null); return; }
        _main.Content = page;
    }

    void JumpTo(string letter)
    {
        CloseFolder();
        if (_layout == 1) ShowAllApps(true); else if (_main != null) _main.Content = _home;
        if (_letterHeads.TryGetValue(letter, out var head))
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => head.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false }));
    }

    /// <summary>A folder (or a group of pins) – each look its own way: a page, in place, a card in the middle.</summary>
    void OpenFolder(StartEntry folder)
    {
        if (_layout == 1)
        {
            if (StartPins.IsGroup(folder.Key)) { if (!_openGroups.Remove(folder.Key)) _openGroups.Add(folder.Key); BuildPinned(); return; }
            var tree = _treeFolders.FirstOrDefault(f => f.Key == folder.Key) ?? folder;
            tree.Expanded = !tree.Expanded;
            RefreshAll();
            return;
        }
        var list = FullScreen ? (Panel)TileGridFor(folder.Children.Count) : new StackPanel { Spacing = 1 };
        foreach (var c in folder.Children) list.Children.Add(FullScreen ? Tile(c, pinned: StartPins.IsGroup(folder.Key)) : Row(c, pinned: StartPins.IsGroup(folder.Key)));
        if (FullScreen) { ShowCard(list, folder.Name); return; }
        var back = new Button { Style = DockButton, Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = "", FontSize = 13 }, new TextBlock { Text = folder.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold } } } };
        back.Click += (_, _) => ShowHomePage();
        _main.Content = new ScrollViewer { Content = new StackPanel { Spacing = 8, Children = { back, list } }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    VariableSizedWrapGrid TileGridFor(int count)
    {
        var g = TileGrid();
        g.MaximumRowsOrColumns = Math.Clamp(count, 1, 5);
        if (FullScreen) g.Width = g.MaximumRowsOrColumns * FullTile;
        return g;
    }

    /// <summary>Full screen: a card over the apps (a folder, the letter index).</summary>
    void ShowCard(FrameworkElement content, string title)
    {
        var stack = new StackPanel { Spacing = 12 };
        if (title != null) stack.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(content);
        var card = new Border
        {
            Child = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 640 },
            Padding = new Thickness(24), CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0xE6, 0x2A, 0x2A, 0x30)), BorderBrush = Res("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1)
        };
        card.Tapped += (_, e) => { if (!ReferenceEquals(e.OriginalSource, card)) return; e.Handled = true; };
        _folderLayer.Children.Clear();
        _folderLayer.Children.Add(card);
        _folderLayer.Visibility = Visibility.Visible;
    }

    void CloseFolder()
    {
        if (_folderLayer == null) return;
        _folderLayer.Visibility = Visibility.Collapsed;
        _folderLayer.Children.Clear();
    }
}
