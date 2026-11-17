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
    // ───────────────────────── pinned and all apps ─────────────────────────

    void BuildPinned()
    {
        if (_pinned == null) return;
        _pinButtons.Clear();
        _pinned.Children.Clear();
        var pins = StartPins.Keys.Where(k => StartPins.IsGroup(k) || AppShell.IsShellKey(k) || File.Exists(k) || Directory.Exists(k)).Select(StartEntry.FromKey).ToList();
        if (_layout == 1)
        {
            // two columns: rows; groups of pins open in place, like the folders of all apps
            foreach (var e in pins)
            {
                _pinned.Children.Add(Row(e, pinned: true));
                if (!StartPins.IsGroup(e.Key) || !_openGroups.Contains(e.Key)) continue;
                e.Expanded = true;
                foreach (var child in e.Children) _pinned.Children.Add(Row(child, indent: 22, pinned: true));
            }
            return;
        }
        var grid = TileGrid();
        foreach (var e in pins) grid.Children.Add(Tile(e, pinned: true));
        _pinned.Children.Add(grid);
    }

    static readonly global::Windows.UI.ViewManagement.UISettings TextSettings = new();
    static double TextScale { get { try { return Math.Max(1, TextSettings.TextScaleFactor); } catch { return 1; } } }

    VariableSizedWrapGrid TileGrid()
    {
        // full screen: whole tiles per row, every row as wide as a full one (the grid stays centred with even margins)
        if (FullScreen) return new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = FullTile, ItemHeight = FullTile * 0.85, MaximumRowsOrColumns = FullColumns, Width = FullColumns * FullTile, HorizontalAlignment = HorizontalAlignment.Center };
        double available = Math.Clamp(S.StartWidth, 460, 960) - 40 - 8;
        int columns = Math.Max(3, (int)(available / (S.StartIconSize * 2.6)));
        double w = Math.Floor(available / columns);
        // room for two lines of the name in the text size of Windows (Accessibility → Text size): with a fixed height the
        // names of one row reached into the next one
        double lines = 2 * Math.Ceiling(12 * 1.34 * TextScale);
        return new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = w, ItemHeight = Math.Ceiling(S.StartIconSize + 6 + lines + 14), MaximumRowsOrColumns = columns };
    }

    readonly List<StartEntry> _treeFolders = new();
    List<(string Letter, List<StartEntry> Items)> _groups = new();

    /// <summary>All apps alphabetically under their letters; apps of a Start menu folder are in one folder entry (like Windows 11).</summary>
    void BuildAll()
    {
        if (_allList == null) return;
        var inFolder = AppCatalog.Folders.SelectMany(f => f.Apps).ToHashSet();
        var all = AppCatalog.Apps.Where(a => !inFolder.Contains(a)).Select(StartEntry.From).Concat(AppCatalog.Folders.Select(StartEntry.From))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _groups = all.GroupBy(e => LetterOf(e.Name)).OrderBy(g => g.Key == "#" ? "" : g.Key, StringComparer.Ordinal).Select(g => (g.Key, g.ToList())).ToList();
        _treeFolders.Clear();
        _treeFolders.AddRange(all.Where(e => e.Kind == EntryKind.AppFolder));
        RefreshAll();
    }

    void RefreshAll()
    {
        _allList.Children.Clear();
        _letterHeads.Clear();
        if (!AppCatalog.IsLoaded) _allList.Children.Add(new TextBlock { Text = L.T("Loading apps…"), Margin = new Thickness(12, 8, 0, 0), Foreground = Res("TextFillColorTertiaryBrush") });
        if (FullScreen)                                              // an app overview: one grid, no letters
        {
            var overview = TileGrid();
            foreach (var e in _groups.SelectMany(g => g.Items)) overview.Children.Add(Tile(e));
            _allList.Children.Add(overview);
            return;
        }
        bool grid = _layout == 0 && S.StartAppsGrid;
        foreach (var (letter, items) in _groups)
        {
            // the letter opens the letter index
            var head = new Button { Style = DockButton, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(4, 10, 0, 2), Content = new TextBlock { Text = letter, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = FullScreen ? 16 : 14 } };
            if (FullScreen) head.HorizontalAlignment = HorizontalAlignment.Left;
            head.Click += (_, _) => ShowLetters();
            _letterHeads[letter] = head;
            _allList.Children.Add(head);
            if (grid)
            {
                var tiles = TileGrid();
                foreach (var e in items) tiles.Children.Add(Tile(e));
                _allList.Children.Add(tiles);
                continue;
            }
            foreach (var e in items)
            {
                _allList.Children.Add(Row(e));
                if (_layout == 1 && e.Kind == EntryKind.AppFolder && e.Expanded)   // two columns: a tree, folders open in place
                    foreach (var child in e.Children) _allList.Children.Add(Row(child, indent: 22));
            }
        }
    }

    public static string LetterOf(string name)
    {
        foreach (var ch in (name ?? "").Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetter(ch)) return char.ToUpperInvariant(ch).ToString();
            if (char.IsDigit(ch)) return "#";
        }
        return "#";
    }
}
