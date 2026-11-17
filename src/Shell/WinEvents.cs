using System.Runtime.InteropServices;

namespace HeMiDock;

/// <summary>
/// The one watch on the events of the windows of all apps (SetWinEventHook, out of context, not the own windows). Every event
/// of Windows wakes the thread of the interface of the dock: before, seven parts of the app had about twelve hooks of their own –
/// some on whole ranges (the places of the windows got also every change of selection, state and value in every app). Now each
/// event is hooked once, only while someone needs it, and handed to everyone who asked for it. Also counted, for the heartbeat
/// of the debug log (how busy the system keeps the dock).
/// </summary>
static class WinEvents
{
    public const uint Foreground = 0x3, MoveSizeStart = 0xA, MoveSizeEnd = 0xB;
    public const uint Destroy = 0x8001, Show = 0x8002, Hide = 0x8003, LocationChange = 0x800B, NameChange = 0x800C, Cloaked = 0x8017, Uncloaked = 0x8018;

    /// <summary>An event: its kind, the window, the part of it (0 = the window itself), the child.</summary>
    public delegate void Handler(uint ev, IntPtr hwnd, int obj, int child);

    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;

    static readonly WinEventProc Proc = OnEvent;                      // kept alive while Windows calls it
    static readonly Dictionary<uint, List<Handler>> Subscribers = new();
    static readonly Dictionary<uint, IntPtr> Hooks = new();
    static readonly Dictionary<uint, long> Counts = new();

    /// <summary>Starts handing <paramref name="ev"/> to <paramref name="handler"/> (on the thread of the interface, which has to call this).</summary>
    public static void On(uint ev, Handler handler)
    {
        if (!Subscribers.TryGetValue(ev, out var list)) Subscribers[ev] = list = new List<Handler>();
        if (list.Contains(handler)) return;
        list.Add(handler);
        if (Hooks.ContainsKey(ev)) return;
        var hook = SetWinEventHook(ev, ev, IntPtr.Zero, Proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (hook != IntPtr.Zero) Hooks[ev] = hook;
        else Diag.Write("Window events", $"the hook of 0x{ev:X} failed");
    }

    /// <summary>Stops it; the hook goes with its last handler.</summary>
    public static void Off(uint ev, Handler handler)
    {
        if (!Subscribers.TryGetValue(ev, out var list) || !list.Remove(handler) || list.Count > 0) return;
        Subscribers.Remove(ev);
        if (Hooks.Remove(ev, out var hook)) UnhookWinEvent(hook);
    }

    static void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
    {
        lock (Counts) Counts[ev] = Counts.TryGetValue(ev, out long n) ? n + 1 : 1;
        if (!Subscribers.TryGetValue(ev, out var list)) return;
        // a copy: a handler may switch itself off
        foreach (var h in list.ToArray())
            try { h(ev, hwnd, obj, child); }
            catch (Exception ex) { Log.Write("Window event 0x" + ev.ToString("X"), ex); }
    }

    /// <summary>How many events came since the last call, by kind ("show 12, location 3400…") – for the heartbeat.</summary>
    public static string TakeCounts()
    {
        lock (Counts) return Take();
    }

    static string Take()
    {
        if (Counts.Count == 0) return "none";
        string Name(uint ev) => ev switch
        {
            Foreground => "foreground", MoveSizeStart => "move start", MoveSizeEnd => "move end", Destroy => "destroy", Show => "show", Hide => "hide",
            LocationChange => "location", NameChange => "name", Cloaked => "cloaked", Uncloaked => "uncloaked", _ => "0x" + ev.ToString("X")
        };
        string text = string.Join(", ", Counts.OrderByDescending(c => c.Value).Select(c => $"{Name(c.Key)} {c.Value}"));
        Counts.Clear();
        return $"{text} (hooks: {Hooks.Count})";
    }
}
