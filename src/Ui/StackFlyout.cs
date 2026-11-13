using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace HeMiDock;

/// <summary>
/// A folder pinned to the dock opens as a stack (like on a Mac): its newest files and folders as a grid above the icon.
/// A click opens the file, a right click shows it in its folder; the button at the top opens the whole folder.
/// </summary>
static class StackFlyout
{
    const int MaxItems = 60;
    const double Tile = 88;

    public static Flyout Create(string folder, string title)
    {
        var flyout = new Flyout();
        var open = new Button
        {
            Padding = new Thickness(8, 5, 8, 5),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { new TextBlock { Text = L.T("Open in Explorer"), FontSize = 12.5 }, new FontIcon { Glyph = "", FontSize = 12 } }
            }
        };
        open.Click += (_, _) => { flyout.Hide(); Fg.Open("explorer.exe", $"\"{folder}\""); };
        var header = new Grid { Margin = new Thickness(4, 0, 0, 8) };
        header.Children.Add(new TextBlock { Text = title ?? System.IO.Path.GetFileName(folder), FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        open.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(open);

        // the newest first (like "date added" of a Mac stack)
        List<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(folder).EnumerateFileSystemInfos()
                .Where(e => !e.Attributes.HasFlag(FileAttributes.Hidden) && !e.Attributes.HasFlag(FileAttributes.System))
                .OrderByDescending(e => e.LastWriteTime).Take(MaxItems).ToList();
        }
        catch (Exception ex) { Log.Write("StackFlyout " + folder, ex); entries = new(); }

        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 5, ItemWidth = Tile, Width = Tile * 5 };
        foreach (var e in entries) grid.Children.Add(MakeTile(flyout, e.FullName, e.Name));
        UIElement body = entries.Count > 0
            ? new ScrollViewer { Content = grid, MaxHeight = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
            : new TextBlock { Text = L.T("The folder is empty"), Margin = new Thickness(4, 12, 4, 16), Width = Tile * 5, Foreground = ThemeBrush.Get("TextFillColorSecondaryBrush") };
        flyout.Content = new StackPanel { Children = { header, body } };
        return flyout;
    }

    static FrameworkElement MakeTile(Flyout flyout, string path, string name)
    {
        var icon = new Image { Width = 40, Height = 40, Margin = new Thickness(0, 4, 0, 6) };
        Pictures.Into(icon, path, isFile: true);                      // in the background
        var text = new TextBlock { Text = name, FontSize = 11.5, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 32, LineHeight = 15.5 };
        var tile = new Button
        {
            Width = Tile - 4, Margin = new Thickness(2), Padding = new Thickness(4, 6, 4, 6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, Content = new StackPanel { Children = { icon, text } }
        };
        ToolTipService.SetToolTip(tile, name);
        tile.Click += (_, _) => { flyout.Hide(); Fg.Open(path); };
        var menu = new MenuFlyout();
        var openItem = new MenuFlyoutItem { Text = L.T("Open"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        openItem.Click += (_, _) => { flyout.Hide(); Fg.Open(path); };
        var show = new MenuFlyoutItem { Text = L.T("Show in folder") };
        show.Click += (_, _) => { flyout.Hide(); Fg.Open("explorer.exe", $"/select,\"{path}\""); };
        menu.Items.Add(openItem);
        menu.Items.Add(show);
        tile.ContextFlyout = menu;
        return tile;
    }
}
