using System.Runtime.InteropServices;

namespace HeMiDock;

/// <summary>
/// Windows shown, hidden, closed, renamed or brought to the front – for the dock while ManagedShell does not run (it could
/// not start). The hook starts with the first handler of <see cref="Changed"/>.
/// </summary>
static class WindowEvents
{
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);

    static readonly WinEventProc Proc = (_, _, _, obj, _, _, _) => { if (obj == 0) _changed?.Invoke(); };
    static readonly List<IntPtr> Hooks = new();
    static Action _changed;

    public static event Action Changed
    {
        add
        {
            _changed += value;
            if (Hooks.Count > 0) return;
            foreach (var (min, max) in new (uint, uint)[] { (3, 3), (0x8001, 0x8003), (0x800C, 0x800C) })   // foreground; destroy, show, hide; name change
                Hooks.Add(SetWinEventHook(min, max, IntPtr.Zero, Proc, 0, 0, 2));                       // out of context, not the own windows
        }
        remove => _changed -= value;
    }

    // ───────────────────────── windows gone ─────────────────────────

    static readonly WinEventProc GoneProc = (_, _, hwnd, obj, child, _, _) => { if (obj == 0 && child == 0 && hwnd != IntPtr.Zero) _gone?.Invoke(hwnd); };
    static readonly List<IntPtr> GoneHooks = new();
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
            _gone += value;
            if (GoneHooks.Count > 0) return;
            foreach (uint ev in new uint[] { 0x8001, 0x8003, 0x8017 })        // EVENT_OBJECT_DESTROY, _HIDE, _CLOAKED
                GoneHooks.Add(SetWinEventHook(ev, ev, IntPtr.Zero, GoneProc, 0, 0, 2));
        }
        remove
        {
            _gone -= value;
            if (_gone != null) return;
            foreach (var h in GoneHooks) UnhookWinEvent(h);
            GoneHooks.Clear();
        }
    }

    public static void Stop()
    {
        foreach (var h in Hooks) UnhookWinEvent(h);
        Hooks.Clear();
    }
}
