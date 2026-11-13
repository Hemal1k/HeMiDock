global using System.IO;

namespace HeMiDock;

/// <summary>
/// The data folder %AppData%\HeMiDock. The test builds before 1.0 kept theirs in %AppData%\HeMiDock.WinUI: on the first
/// start it becomes %AppData%\HeMiDock (an older folder of that name is kept aside as HeMiDock.old). When the move cannot be
/// done (a file in use), the old place is used until the next start.
/// </summary>
static class DataFolder
{
    static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string Resolve()
    {
        string folder = Path.Combine(Roaming, "HeMiDock"), beta = Path.Combine(Roaming, "HeMiDock.WinUI"), aside = Path.Combine(Roaming, "HeMiDock.old");
        if (!Directory.Exists(beta)) return folder;
        try
        {
            if (Directory.Exists(folder))
            {
                if (Directory.Exists(aside)) aside += " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm");
                Directory.Move(folder, aside);
            }
            Directory.Move(beta, folder);
            return folder;
        }
        catch { return Directory.Exists(beta) ? beta : folder; }
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
