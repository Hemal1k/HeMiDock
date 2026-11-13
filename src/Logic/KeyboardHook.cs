using System.Runtime.InteropServices;

namespace HeMiDock;

/// <summary>
/// Global keyboard hook:
///  • pressing the Win key alone → our Start menu (Windows Start does not open),
///  • Ctrl+Esc → the same, Win+S → Start menu with the search focused, Win+X → power user menu, Win+1 … 0 → apps of the dock,
///  • the own shortcuts of <see cref="Hotkeys"/> (they come first).
/// Other Win shortcuts (Win+E, Win+D, Win+L…) are passed on to Windows unchanged.
/// </summary>
static class KeyboardHook
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    const uint LLKHF_INJECTED = 0x10, LLKHF_ALTDOWN = 0x20;
    const byte VK_MASK = 0xE8;                                    // unassigned key: Windows then does not treat Win+key as "Win alone"
    const int VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_S = 0x53, VK_X = 0x58, VK_A = 0x41, VK_ESCAPE = 0x1B, VK_CONTROL = 0x11;

    static IntPtr _hook;
    static Native.HookProc _proc;                                 // must stay referenced, otherwise the GC collects it
    static bool _winDown, _combo, _mask;
    static int _swallowed;                                        // key whose press we swallowed (its release is swallowed too)

    public static event Action StartPressed;
    public static event Action SearchPressed;
    /// <summary>Win+X – our power user menu above the Start button.</summary>
    public static event Action PowerMenuPressed;
    /// <summary>Win+A – the control center of HeMiDock (<see cref="ControlKey"/>).</summary>
    public static event Action ControlPressed;
    /// <summary>Win+A opens our control center instead of the quick settings of Windows.</summary>
    public static bool ControlKey { get; set; }
    /// <summary>Win+1 … Win+9, Win+0 – the app at that place in the dock (index 0 … 9), like on the Windows taskbar.</summary>
    public static event Action<int> AppKeyPressed;

    /// <summary>The Win key, Ctrl+Esc, Win+S and Win+X open our Start menu / menus (off = Windows handles them).</summary>
    public static bool ReplaceStart { get; set; } = true;
    /// <summary>Each of them can be switched off on its own (Settings → Keyboard shortcuts); they work only with <see cref="ReplaceStart"/>.</summary>
    public static bool StartKey { get; set; } = true;
    public static bool SearchKey { get; set; } = true;
    public static bool PowerKey { get; set; } = true;
    /// <summary>Win+1 … 9 are taken over for the dock.</summary>
    public static bool AppKeys { get; set; } = true;
    /// <summary>Own shortcuts of HeMiDock: code (modifiers &lt;&lt; 16 | key, see <see cref="Hotkeys"/>) → action.</summary>
    public static Dictionary<int, string> Shortcuts { get; set; } = new();
    public static event Action<string> ShortcutPressed;
    /// <summary>
    /// While set (the settings record a shortcut), the next key with its modifiers goes here and nothing else happens –
    /// not even Windows shortcuts like Win+E.
    /// </summary>
    public static Action<Hotkeys.Combo> Recording { get; set; }

    static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5 or VK_LWIN or VK_RWIN;

    static int Modifiers(uint flags)
    {
        int m = 0;
        if ((flags & LLKHF_ALTDOWN) != 0 || (Native.GetAsyncKeyState(0x12) & 0x8000) != 0) m |= Hotkeys.Alt;
        if ((Native.GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) m |= Hotkeys.Ctrl;
        if ((Native.GetAsyncKeyState(0x10) & 0x8000) != 0) m |= Hotkeys.Shift;
        if (WinReallyDown()) m |= Hotkeys.Win;
        return m;
    }

    /// <summary>
    /// A key pressed with its modifiers: recorded (settings), or one of the own shortcuts. It is swallowed, and so that the
    /// released Win / Alt do nothing on their own (Start, the menu bar of an app), a blank key is put in between.
    /// </summary>
    static bool HandleShortcut(int vk, uint flags)
    {
        var record = Recording;
        int mods = Modifiers(flags);
        string action = null;
        if (record == null && !Shortcuts.TryGetValue(mods << 16 | vk, out action)) return false;

        if ((mods & Hotkeys.Win) != 0) { _winDown = true; _combo = true; _mask = true; }
        else if ((mods & Hotkeys.Alt) != 0) Mask();
        _swallowed = vk;
        if (record != null) Post(() => record(new Hotkeys.Combo(mods, vk)));
        else Post(() => ShortcutPressed?.Invoke(action));
        return true;
    }

    public static void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = Callback;
        _hook = Native.SetWindowsHookEx(WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Write("KeyboardHook", new InvalidOperationException("SetWindowsHookEx selhalo: " + Marshal.GetLastWin32Error()));
        _winDown = _combo = _mask = false;
        _swallowed = 0;
    }

    public static void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>Is a Win key physically held right now?</summary>
    static bool WinReallyDown() =>
        (Native.GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (Native.GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

    /// <summary>Forgets a held Win key (the session was locked / switched – its release never comes).</summary>
    public static void Reset() => _winDown = _combo = _mask = false;

    static void Post(Action a)
    {
        if (a == null) return;
        Ui.Post(a);           // the hook must return quickly, the actual work runs on the UI thread
    }

    /// <summary>Injects a "blank" key between pressing and releasing Win – Windows then does not open Start.</summary>
    static void Mask()
    {
        Native.keybd_event(VK_MASK, 0, 0, UIntPtr.Zero);
        Native.keybd_event(VK_MASK, 0, 2, UIntPtr.Zero);
    }

    static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return Native.CallNextHookEx(_hook, code, wParam, lParam);

        try
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if ((k.flags & LLKHF_INJECTED) == 0)                  // our own (injected) keys are ignored
            {
                int msg = (int)wParam;
                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                int vk = (int)k.vkCode;

                if (vk == VK_LWIN || vk == VK_RWIN)
                {
                    if (down)
                    {
                        if (!_winDown) { _winDown = true; _combo = false; _mask = false; }
                    }
                    else
                    {
                        if (Recording != null) Mask();                // recording a shortcut: Win alone does nothing
                        else if (_winDown && !_combo && ReplaceStart && StartKey) { Mask(); Post(() => StartPressed?.Invoke()); }
                        else if (_winDown && _mask) Mask();
                        _winDown = false;
                    }
                }
                else if (_winDown && down && !WinReallyDown())
                {
                    // The release of Win was never seen (e.g. Win+L: the lock screen swallows it) – without this every
                    // following key was taken as a Win shortcut and digits opened the apps of the dock.
                    _winDown = _combo = _mask = false;
                    if (!IsModifier(vk) && HandleShortcut(vk, k.flags)) return (IntPtr)1;
                }
                else if (down && !IsModifier(vk) && HandleShortcut(vk, k.flags))
                {
                    return (IntPtr)1;                             // an own shortcut (they come before the built-in ones)
                }
                else if (_winDown && down)
                {
                    _combo = true;                                // Win+something = a shortcut, Start does not open
                    if (vk == VK_S && ReplaceStart && SearchKey)
                    {
                        _mask = true;
                        _swallowed = VK_S;
                        Post(() => SearchPressed?.Invoke());
                        return (IntPtr)1;
                    }
                    if (vk == VK_A && ControlKey)
                    {
                        _mask = true;
                        _swallowed = VK_A;
                        Post(() => ControlPressed?.Invoke());
                        return (IntPtr)1;
                    }
                    if (vk == VK_X && ReplaceStart && PowerKey)
                    {
                        _mask = true;
                        _swallowed = VK_X;
                        Post(() => PowerMenuPressed?.Invoke());
                        return (IntPtr)1;
                    }
                    if (vk is >= 0x30 and <= 0x39 && (Native.GetKeyState(0x10) & 0x8000) == 0      // Win+number (not Win+Shift+number)
                        && (Native.GetKeyState(VK_CONTROL) & 0x8000) == 0 && AppKeys && AppKeyPressed != null)
                    {
                        _mask = true;
                        _swallowed = vk;
                        int index = vk == 0x30 ? 9 : vk - 0x31;
                        Post(() => AppKeyPressed?.Invoke(index));
                        return (IntPtr)1;
                    }
                }
                else if (down && vk == VK_ESCAPE && ReplaceStart && StartKey && (Native.GetKeyState(VK_CONTROL) & 0x8000) != 0)
                {
                    _swallowed = VK_ESCAPE;                       // Ctrl+Esc = Start
                    Post(() => StartPressed?.Invoke());
                    return (IntPtr)1;
                }
                else if (!down && vk == _swallowed && vk != 0)
                {
                    _swallowed = 0;                               // release of a key whose press we swallowed
                    return (IntPtr)1;
                }
            }
        }
        catch (Exception ex) { Log.Write("KeyboardHook.Callback", ex); }

        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }
}
