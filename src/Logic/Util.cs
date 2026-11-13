using System.Diagnostics;

namespace HeMiDock;

/// <summary>Simple error log in %AppData%\HeMiDock\log.txt (errors only, to find out why something does not work).</summary>
static class Log
{
    static readonly object Gate = new();

    /// <summary>A line into log.txt whatever the debug mode (measurements).</summary>
    public static void Note(string where, string text)
    {
        try
        {
            lock (Gate)
            {
                var file = Path.Combine(AppPaths.Logs, "log.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] {text}\n");
            }
        }
        catch { }
    }

    /// <summary>An error into log.txt – only in debug mode (Settings → About), otherwise the app writes no logs at all.</summary>
    public static void Write(string where, Exception ex)
    {
        if (!Diag.On) return;
        try
        {
            lock (Gate)
            {
                var file = Path.Combine(AppPaths.Logs, "log.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                if (File.Exists(file) && new FileInfo(file).Length > 256 * 1024) File.Delete(file);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] {ex}\n");
            }
        }
        catch { }
        Diag.Write("ERROR " + where, ex?.ToString());                // in debug mode also among the other events, in order
    }
}

/// <summary>
/// Watches for clicks outside a window / menu. The dock never activates (NOACTIVATE), so the framework cannot tell
/// that the user clicked elsewhere – the mouse buttons are therefore read globally.
/// </summary>
sealed class ClickAway
{
    readonly Func<int, int, bool> _inside;
    readonly Action _dismiss;
    readonly DispatcherTimer _timer;
    bool _wasDown;

    public ClickAway(Func<int, int, bool> inside, Action dismiss)
    {
        _inside = inside;
        _dismiss = dismiss;
        _wasDown = AnyDown();                                      // the press that opened the window does not count
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    // 0x8000 = held now, 0x0001 = pressed since the last check – a quick tap (touchpad) between two ticks counts too
    static bool IsDown(int vk) => (Native.GetAsyncKeyState(vk) & 0x8001) != 0;

    public static bool AnyDown() => IsDown(0x01) || IsDown(0x02) || IsDown(0x04);

    void Tick()
    {
        bool down = AnyDown();
        if (down && !_wasDown && Native.GetCursorPos(out var p) && !_inside(p.X, p.Y))
        {
            Stop();
            _dismiss();
            return;
        }
        _wasDown = down;
    }

    public void Stop() => _timer.Stop();
}

/// <summary>Helpers for the foreground window and on-screen positions of elements.</summary>
static class Fg
{
    /// <summary>Brings a window to the foreground even when the process did not get the last input (the dock is NOACTIVATE).</summary>
    public static void Force(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        try
        {
            var fg = Native.GetForegroundWindow();
            uint fgThread = fg == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(fg, out _);
            uint me = Native.GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
            try
            {
                Native.BringWindowToTop(h);
                Native.SetForegroundWindow(h);
            }
            finally { if (attached) Native.AttachThreadInput(me, fgThread, false); }

            if (Native.GetForegroundWindow() != h)                // fallback: a short Alt press unlocks switching the foreground
            {
                Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
                Native.SetForegroundWindow(h);
                Native.keybd_event(0x12, 0, 2, UIntPtr.Zero);
            }
        }
        catch (Exception ex) { Log.Write("Fg.Force", ex); }
    }

    public static bool Contains(Native.RECT r, int x, int y) => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;


    /// <summary>Runs a program without a console window (shutdown, rundll32…).</summary>
    public static void Run(string exe, string args)
    {
        Diag.Write("Run", $"{exe} {args}");
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true }); }
        catch (Exception ex) { Log.Write("Run " + exe, ex); }
    }

    /// <summary>Opens anything through the shell: file, folder, address, ms-settings: …</summary>
    public static void Open(string target, string args = null)
    {
        Diag.Write("Open", $"{target} {args}");
        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (args != null) psi.Arguments = args;
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);   // what opens (or its UAC prompt) may come to the front, not only blink
            Process.Start(psi);
        }
        catch (Exception ex) { Log.Write("Open " + target, ex); }
    }
}

