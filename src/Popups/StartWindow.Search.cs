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
    // ───────────────────────── search ─────────────────────────

    async void Search()
    {
        string q = _search.Text;
        _files?.Cancel();
        bool active = q.Trim().Length > 0;
        _resultsView.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (_main != null) _main.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        if (FullScreen && _home != null) _home.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        foreach (var c in _root.Children.OfType<Border>().Concat(_root.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Border>())))
            if (c.Tag as string == "switch") c.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        if (!active) { _local = new(); _found = new(); _results.Children.Clear(); return; }
        _local = SearchEngine.Local(q);
        _found = new();
        ShowResults();
        var cts = _files = new CancellationTokenSource();
        try
        {
            await Task.Delay(220, cts.Token);                         // files after a short pause in typing
            if (q.Trim().Length < 2) return;
            var files = await SearchEngine.FilesAsync(q.Trim(), cts.Token);
            if (cts.IsCancellationRequested || q != _search.Text) return;
            _found = files;
            ShowResults();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write("Search files", ex); }
    }

    List<StartEntry> ShownResults => _local.Concat(_found).ToList();

    void ShowResults()
    {
        var results = ShownResults;
        _results.Children.Clear();
        string group = null;
        foreach (var r in results)
        {
            if (r.Group != group) { group = r.Group; if (group != null) _results.Children.Add(Heading(group)); }
            _results.Children.Add(Row(r));
        }
        if (results.Count == 0) _results.Children.Add(new TextBlock { Text = L.T("No results"), Margin = new Thickness(8), Foreground = Res("TextFillColorSecondaryBrush") });
        _selected = Math.Clamp(_selected, 0, Math.Max(0, results.Count - 1));
        Highlight();
    }

    void Highlight()
    {
        var rows = _results.Children.OfType<Button>().ToList();
        for (int i = 0; i < rows.Count; i++)
            rows[i].Background = i == _selected ? Res("SubtleFillColorSecondaryBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        if (_selected < rows.Count) rows[_selected].StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    void Move(int delta)
    {
        int count = _results.Children.OfType<Button>().Count();
        if (count == 0 || _resultsView.Visibility != Visibility.Visible) return;
        _selected = Math.Clamp(_selected + delta, 0, count - 1);
        Highlight();
    }

    void Search_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Down: Move(1); e.Handled = true; break;
            case Windows.System.VirtualKey.Up: Move(-1); e.Handled = true; break;
            case Windows.System.VirtualKey.PageDown: Move(6); e.Handled = true; break;
            case Windows.System.VirtualKey.PageUp: Move(-6); e.Handled = true; break;
            case Windows.System.VirtualKey.Tab when _resultsView.Visibility == Visibility.Visible: Move(shift ? -1 : 1); e.Handled = true; break;
            case Windows.System.VirtualKey.Enter:
                if (_results.Children.OfType<Button>().ElementAtOrDefault(_selected)?.Tag is StartEntry en) { e.Handled = true; Launch(en, AdminKeys); }
                break;
        }
    }
}
