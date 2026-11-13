using System.ComponentModel;
using System.Diagnostics;

namespace HeMiDock;

/// <summary>
/// The order of a menu set in the settings, stored as "a|b;c|d" (the groups by ';'). Every item stays in its own group; an
/// item not in the stored order (a new one) comes at the end of its group.
/// </summary>
static class ItemOrder
{
    public static List<List<T>> Apply<T>(T[][] groups, Func<T, string> id, string stored) where T : class
    {
        var order = (stored ?? "").Split(';').Select(g => g.Split('|', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var result = new List<List<T>>();
        for (int i = 0; i < groups.Length; i++)
        {
            var want = i < order.Count ? order[i] : Array.Empty<string>();
            var sorted = want.Select(w => groups[i].FirstOrDefault(x => id(x) == w)).Where(x => x != null).Distinct().ToList();
            sorted.AddRange(groups[i].Where(x => !sorted.Contains(x)));
            result.Add(sorted);
        }
        return result;
    }

    /// <summary>The stored order with one item moved by <paramref name="delta"/> places inside its group.</summary>
    public static string Move<T>(T[][] groups, Func<T, string> id, string stored, string itemId, int delta) where T : class
    {
        var ordered = Apply(groups, id, stored);
        foreach (var g in ordered)
        {
            int i = g.FindIndex(x => id(x) == itemId);
            if (i < 0) continue;
            int j = i + delta;
            if (j >= 0 && j < g.Count) (g[i], g[j]) = (g[j], g[i]);
            break;
        }
        return string.Join(";", ordered.Select(g => string.Join("|", g.Select(id))));
    }
}

/// <summary>
/// Power user menu of the Start button (right click, Win+X) – like the Windows one. Every item can be hidden in
/// Settings → Start menu → Start button menu; separators between empty groups disappear.
/// </summary>
static class PowerMenu
{
    public sealed record Entry(string Id, Action Run);

    /// <summary>Groups of items (the English text is the id, translated with <see cref="L.T(string)"/>).</summary>
    public static readonly Entry[][] Groups =
    {
        new Entry[]
        {
            new("Installed apps", () => Fg.Open("ms-settings:appsfeatures")),
            new("Power options", () => Fg.Open("ms-settings:powersleep")),
            new("Event Viewer", () => Fg.Open("eventvwr.msc")),
            new("System", () => Fg.Open("ms-settings:about")),
            new("Device Manager", () => Fg.Open("devmgmt.msc")),
            new("Network connections", () => Fg.Open("ms-settings:network")),
            new("Disk Management", () => Fg.Open("diskmgmt.msc")),
            new("Computer Management", () => Fg.Open("compmgmt.msc")),
            new("Terminal", () => RunTerminal(false)),
            new("Terminal (Admin)", () => RunTerminal(true)),
        },
        new Entry[]
        {
            new("Task Manager", () => Fg.Open("taskmgr.exe")),
            new("Settings", () => Fg.Open("ms-settings:")),
            new("File Explorer", () => Fg.Open("explorer.exe")),
            new("Search", () => Host.Command?.Invoke("startsearch")),
            new("Run", () => AppShell.Tap(0x5B, 0x52)),                                 // Win+R
        },
        new Entry[]
        {
            new("Desktop", Hotkeys.ToggleDesktop),                                      // like "Desktop" of Windows (Win+D)
        },
        // the last group – lock, sign out, sleep, shut down, restart – is the one list of the power button of Start
        // (StartSide.Power): one choice of these items for both menus
    };

    static HashSet<string> Hidden => new((DockSettings.Current.PowerMenuHidden ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries));

    public static bool IsShown(string id) => !Hidden.Contains(id);

    public static void SetShown(string id, bool shown)
    {
        var hidden = Hidden;
        if (shown) hidden.Remove(id); else hidden.Add(id);
        DockSettings.Current.PowerMenuHidden = string.Join("|", hidden);
    }

    /// <summary>The groups in the order set in the settings.</summary>
    public static List<List<Entry>> Ordered() => ItemOrder.Apply(Groups, e => e.Id, DockSettings.Current.PowerMenuOrder);

    /// <summary>An item one place up (-1) or down (+1) in its group.</summary>
    public static void Move(string id, int delta) =>
        DockSettings.Current.PowerMenuOrder = ItemOrder.Move(Groups, e => e.Id, DockSettings.Current.PowerMenuOrder, id, delta);

    /// <summary>The groups with the items that are switched on, in their order (empty groups left out).</summary>
    public static List<List<Entry>> Shown()
    {
        var hidden = Hidden;
        return Ordered().Select(g => g.Where(e => !hidden.Contains(e.Id)).ToList()).Where(g => g.Count > 0).ToList();
    }

    /// <summary>
    /// The power items of Start (with the update items while updates of Windows wait): in the menu under "Shut down or sign
    /// out", as in Windows.
    /// </summary>
    public static List<Entry> Power() => StartSide.ShownPower().SelectMany(g => g).Select(p => new Entry(p.Id, p.Run)).ToList();

    /// <summary>Windows Terminal if installed, otherwise PowerShell.</summary>
    static void RunTerminal(bool admin)
    {
        string wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
        try { Process.Start(new ProcessStartInfo(File.Exists(wt) ? wt : "powershell.exe") { UseShellExecute = true, Verb = admin ? "runas" : "" }); }
        catch (Win32Exception) { /* UAC prompt cancelled */ }
    }
}
