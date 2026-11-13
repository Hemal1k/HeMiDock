using System.Runtime.InteropServices;

namespace HeMiDock;

/// <summary>
/// Own keyboard shortcuts of HeMiDock (Settings → Keyboard shortcuts): every action can get any combination of Ctrl / Alt /
/// Shift / Win with a key, recorded by pressing it. They are caught by <see cref="KeyboardHook"/>, so Win combinations work
/// too. Stored as "id=modifiers:key|…" in <see cref="DockSettings.Hotkeys"/>; an action that is not stored has its default.
/// </summary>
static class Hotkeys
{
    public const int Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

    /// <summary>A shortcut: modifiers (Alt / Ctrl / Shift / Win bits) and a virtual key; 0 = none.</summary>
    public readonly record struct Combo(int Mods, int Key)
    {
        public bool IsSet => Key != 0;
        public int Code => Mods << 16 | Key;
        public override string ToString() => Hotkeys.Name(this);
    }

    /// <summary>
    /// The actions (id, name, description, group) in the order of the settings. The group is the settings page the shortcut
    /// belongs to: it is set there too, and the page Keyboard shortcuts lists them all by these groups.
    /// </summary>
    public static readonly (string Id, string Name, string Description, string Group)[] Actions =
    {
        ("start", "Open Start", null, "start"),
        ("spotlight", "Quick search", "A search box in the middle of the screen", "start"),
        ("powermenu", "Start button menu", null, "start"),
        ("autohide", "Automatically hide the dock", "On / off", "dock"),
        ("desktop", "Show desktop", "Hides all windows; again brings them back", "dock"),
        ("control", "Control center", null, "quick"),
        ("mute", "Mute sound", null, "quick"),
        ("calendar", "Notifications and calendar", null, "clock"),
        ("dnd", "Do not disturb", "On / off", "clock"),
        ("zones", "Window zones", "On / off", "windows"),
        ("zonelayout", "Switch the zone layout", "To the next one; its name and zones are shown for a moment", "windows"),
        ("ontop", "Always on top", "The active window stays above the others", "windows"),
        ("wsave", "Save a workspace", null, "windows"),
        ("wopen", "Open a workspace", null, "windows"),
        ("settings", "App settings", null, "general"),
    };

    /// <summary>
    /// Keys that may be a shortcut on their own (no Ctrl / Alt / Shift / Win): they type nothing – F1 … F24, Pause, Scroll Lock,
    /// Print Screen, the menu key, and the media, browser and launch keys of a keyboard.
    /// </summary>
    public static bool AllowedAlone(int vk) => vk is >= 0x70 and <= 0x87 or 0x13 or 0x91 or 0x2C or 0x5D or >= 0xA6 and <= 0xB7;

    static Dictionary<string, Combo> Parse(string stored)
    {
        var map = new Dictionary<string, Combo>();
        foreach (var part in (stored ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var value = part[(eq + 1)..].Split(':');
            map[part[..eq]] = value.Length == 2 && int.TryParse(value[0], out int m) && int.TryParse(value[1], out int k) ? new Combo(m, k) : default;
        }
        return map;
    }

    /// <summary>The default of an action: only the quick search has one (Alt+Space).</summary>
    static Combo Default(string id) => id == "spotlight" ? new Combo(Alt, 0x20) : default;

    public static Combo Get(string id) => Parse(DockSettings.Current.Hotkeys).TryGetValue(id, out var c) ? c : Default(id);

    /// <summary>Sets the shortcut of an action (none = default(Combo)); another action with the same keys loses it. Returns that action.</summary>
    public static string Set(string id, Combo combo)
    {
        var map = Actions.ToDictionary(a => a.Id, a => Get(a.Id));
        string taken = combo.IsSet ? map.FirstOrDefault(p => p.Key != id && p.Value == combo).Key : null;
        if (taken != null) map[taken] = default;
        map[id] = combo;
        DockSettings.Current.Hotkeys = string.Join("|", map.Select(p => $"{p.Key}={p.Value.Mods}:{p.Value.Key}"));
        return taken;
    }

    /// <summary>All set shortcuts by their code (for the keyboard hook).</summary>
    public static Dictionary<int, string> Map() =>
        Actions.Select(a => (a.Id, Combo: Get(a.Id))).Where(x => x.Combo.IsSet).GroupBy(x => x.Combo.Code).ToDictionary(g => g.Key, g => g.First().Id);

    /// <summary>Does the action of a shortcut (on the UI thread).</summary>
    public static void Run(string id)
    {
        try
        {
            switch (id)
            {
                case "autohide": DockSettings.Current.AutoHide = !DockSettings.Current.AutoHide; break;
                case "dnd": Dnd.Set(!Dnd.IsOn); break;
                case "ontop": WindowManager.ToggleTopmost(); break;
                case "zones": DockSettings.Current.ZonesOn = !DockSettings.Current.ZonesOn; break;
                case "zonelayout": WindowManager.NextLayout(); break;
                case "wsave": WindowManager.AskSave(); break;
                case "wopen": WindowManager.ShowOpenMenu(); break;
                case "desktop": ToggleDesktop(); break;
                default: Host.Command?.Invoke(id); break;        // quick search, menus, control center, calendar, mute, settings
            }
        }
        catch (Exception ex) { Log.Write("Hotkey " + id, ex); }
    }

    /// <summary>
    /// Show desktop through the shell (not by pressing Win+D: the keys of the shortcut may still be held, and Ctrl+Win+D makes a
    /// new virtual desktop).
    /// </summary>
    public static void ToggleDesktop()
    {
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type == null) return;
        object shell = Activator.CreateInstance(type);
        try { type.InvokeMember("ToggleDesktop", System.Reflection.BindingFlags.InvokeMethod, null, shell, null); }
        finally { Marshal.ReleaseComObject(shell); }
    }

    // ───────────────────────── names of keys ─────────────────────────

    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetKeyNameText(int lParam, System.Text.StringBuilder name, int size);

    /// <summary>"Ctrl + Alt + H" (key names in the language of the keyboard, like Windows shows them).</summary>
    public static string Name(Combo c)
    {
        if (!c.IsSet) return L.T("Not set");
        var parts = new List<string>();
        if ((c.Mods & Win) != 0) parts.Add("Win");
        if ((c.Mods & Ctrl) != 0) parts.Add("Ctrl");
        if ((c.Mods & Alt) != 0) parts.Add("Alt");
        if ((c.Mods & Shift) != 0) parts.Add("Shift");
        parts.Add(KeyName(c.Key));
        return string.Join(" + ", parts);
    }

    static string KeyName(int vk)
    {
        if (vk == 0x20) return L.T("Space");
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        try
        {
            uint scan = MapVirtualKey((uint)vk, 0);
            // arrows, Insert / Delete, Home / End, Page Up / Down are "extended" keys – without the flag they get numpad names
            bool extended = vk is >= 0x21 and <= 0x2E;
            var sb = new System.Text.StringBuilder(64);
            if (scan != 0 && GetKeyNameText((int)(scan << 16 | (extended ? 1u << 24 : 0)), sb, sb.Capacity) > 0) return sb.ToString();
        }
        catch { }
        return ((ConsoleKey)vk).ToString();
    }
}
