global using System.IO;

namespace HeMiDock;

/// <summary>
/// The data folder %AppData%\HeMi\HeMiDock – all HeMi apps keep their settings under %AppData%\HeMi (since 1.7.0). The
/// folder of the older versions (%AppData%\HeMiDock, before 1.0 %AppData%\HeMiDock.WinUI) is moved there at the first
/// start. When the move cannot be done (a file in use), the old place is used until the next start.
/// </summary>
static class DataFolder
{
    static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string Resolve()
    {
        string folder = Path.Combine(Roaming, "HeMi", "HeMiDock");
        var olds = new[] { Path.Combine(Roaming, "HeMiDock"), Path.Combine(Roaming, "HeMiDock.WinUI") };
        if (!Directory.Exists(folder))
            foreach (var old in olds)
            {
                if (!Directory.Exists(old)) continue;
                try
                {
                    Directory.CreateDirectory(Path.Combine(Roaming, "HeMi"));
                    Directory.Move(old, folder);
                    break;
                }
                catch { return old; }
            }
        FixPaths(folder, olds);
        return folder;
    }

    /// <summary>
    /// The settings hold full paths into the data folder (the shortcuts of the dock in Pinned, own icons …): after the move
    /// they point to the new place (checked at every start – a few small files).
    /// </summary>
    static void FixPaths(string folder, string[] olds)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.GetFiles(folder, "*.json"))
            {
                string text = File.ReadAllText(file), fixedText = text;
                foreach (var old in olds)
                {
                    // as JSON writes it (\\) and as it is
                    fixedText = fixedText.Replace(old.Replace(@"\", @"\\") + @"\\", folder.Replace(@"\", @"\\") + @"\\", StringComparison.OrdinalIgnoreCase)
                                         .Replace(old + @"\", folder + @"\", StringComparison.OrdinalIgnoreCase);
                }
                if (fixedText != text) File.WriteAllText(file, fixedText);
            }
        }
        catch { }
    }
}

/// <summary>
/// The thread of the user interface for the logic: set once by the app (its synchronization context), so the logic can
/// hand work back to it without knowing the UI framework.
/// </summary>
static class Ui
{
    public static SynchronizationContext Context { get; set; }

    public static void Post(Action action)
    {
        if (Context == null) action();
        else Context.Post(_ => action(), null);
    }
}

/// <summary>A timer whose ticks come on the thread of the user interface.</summary>
sealed class DispatcherTimer
{
    Timer _timer;

    public DispatcherTimer() { }
    public DispatcherTimer(object priority) { }

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);
    public event EventHandler Tick;

    public bool IsEnabled
    {
        get => _timer != null;
        set { if (value) Start(); else Stop(); }
    }

    public void Start()
    {
        Stop();
        _timer = new Timer(_ => Ui.Post(() => { if (_timer != null) Tick?.Invoke(this, EventArgs.Empty); }), null, Interval, Interval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }
}

/// <summary>A rectangle (device-independent units or pixels, as the caller uses it).</summary>
readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Contains(Point p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;
}

readonly record struct Point(double X, double Y);

readonly record struct Size(double Width, double Height);

/// <summary>A picture as BGRA pixels (premultiplied, top row first); the app turns it into an image of its UI framework.</summary>
sealed record IconImage(byte[] Pixels, int Width, int Height);
