namespace HeMiDock;

/// <summary>
/// The windows of the dock: shown, hidden, closed, renamed or brought to the front – for the dock while ManagedShell does not
/// run (it could not start) – and closed or hidden at once also while it runs. Both through the one watch of <see cref="WinEvents"/>.
/// </summary>
static class WindowEvents
{
    static readonly uint[] ChangeEvents = { WinEvents.Foreground, WinEvents.Destroy, WinEvents.Show, WinEvents.Hide, WinEvents.NameChange };
    static readonly WinEvents.Handler ChangeProc = (_, _, obj, _) => { if (obj == 0) _changed?.Invoke(); };
    static Action _changed;

    /// <summary>Any window shown, hidden, closed, renamed or brought to the front (without ManagedShell).</summary>
    public static event Action Changed
    {
        add
        {
            bool first = _changed == null;
            _changed += value;
            if (first) foreach (var ev in ChangeEvents) WinEvents.On(ev, ChangeProc);
        }
        remove
        {
            _changed -= value;
            if (_changed == null) Stop();
        }
    }

    public static void Stop()
    {
        foreach (var ev in ChangeEvents) WinEvents.Off(ev, ChangeProc);
    }

    // ───────────────────────── windows gone ─────────────────────────

    static readonly uint[] GoneEvents = { WinEvents.Destroy, WinEvents.Hide, WinEvents.Cloaked };
    static readonly WinEvents.Handler GoneProc = (_, hwnd, obj, child) => { if (obj == 0 && child == 0 && hwnd != IntPtr.Zero) _gone?.Invoke(hwnd); };
    static Action<IntPtr> _gone;

    /// <summary>
    /// A window closed, hidden or hidden through DWM (cloaked) – also while ManagedShell runs: many apps hide their window first
    /// when they are closed, and Windows says nothing to the taskbar about that, so the dock saw it only at its check once a
    /// second (the running mark stayed for a while). Raised on the thread of the interface for every such window of the system;
    /// the handler checks whether it is one of its own.
    /// </summary>
    public static event Action<IntPtr> Gone
    {
        add
        {
            bool first = _gone == null;
            _gone += value;
            if (first) foreach (var ev in GoneEvents) WinEvents.On(ev, GoneProc);
        }
        remove
        {
            _gone -= value;
            if (_gone == null) foreach (var ev in GoneEvents) WinEvents.Off(ev, GoneProc);
        }
    }
}
