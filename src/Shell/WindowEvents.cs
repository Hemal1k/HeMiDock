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

    public static void Stop()
    {
        foreach (var h in Hooks) UnhookWinEvent(h);
        Hooks.Clear();
    }
}
