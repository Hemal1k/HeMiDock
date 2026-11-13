using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HeMiDock;

/// <summary>A small window asking for one line of text (the name of a workspace …); Enter confirms, Esc cancels.</summary>
sealed class TextPrompt : Window
{
    readonly TaskCompletionSource<string> _answer = new();
    readonly TextBox _box;

    TextPrompt(string title, string question, string text)
    {
        Title = title;
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        _box = new TextBox { Text = text ?? "", Header = question, MinWidth = 320 };
        var ok = new Button { Content = L.T("OK"), Style = (Style)Application.Current.Resources["AccentButtonStyle"], MinWidth = 90 };
        var cancel = new Button { Content = L.T("Cancel"), MinWidth = 90 };
        ok.Click += (_, _) => Done(_box.Text);
        cancel.Click += (_, _) => Done(null);
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) Done(_box.Text);
            else if (e.Key == Windows.System.VirtualKey.Escape) Done(null);
        };
        Content = new StackPanel
        {
            Padding = new Thickness(24), Spacing = 18,
            Children = { new TextBlock { Text = title, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, _box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } } }
        };
        if (AppWindow.Presenter is OverlappedPresenter p) { p.IsResizable = p.IsMaximizable = p.IsMinimizable = false; p.IsAlwaysOnTop = true; }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(440, 240));
        Closed += (_, _) => _answer.TrySetResult(null);
    }

    void Done(string text)
    {
        _answer.TrySetResult(text);
        Close();
    }

    public static Task<string> AskAsync(string title, string question, string text)
    {
        var w = new TextPrompt(title, question, text);
        w.Activate();
        Fg.Force(WinRT.Interop.WindowNative.GetWindowHandle(w));
        w._box.Focus(FocusState.Programmatic);
        w._box.SelectAll();
        return w._answer.Task;
    }
}
