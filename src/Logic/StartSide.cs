
namespace HeMiDock;

/// <summary>
/// Items of the side column of the two-column Start menu (folders, settings …) and of the power menu of all Start menus.
/// Every item can be hidden in Settings → Start menu (the English text is the id, translated with <see cref="L.T(string)"/>).
/// </summary>
static class StartSide
{
    public sealed record Place(string Id, string Glyph, Action Open);
    public sealed record PowerItem(string Id, Action Run);

    static Action Shell(string target) => () => Fg.Open("explorer.exe", target);

    /// <summary>Groups of the side column (a line between them).</summary>
    public static readonly Place[][] Places =
    {
        new Place[]
        {
            new("Documents", "", Shell("shell:Personal")),
            new("Pictures", "", Shell("shell:My Pictures")),
            new("Music", "", Shell("shell:My Music")),
            new("Videos", "", Shell("shell:My Video")),
            new("Downloads", "", Shell("shell:Downloads")),
            new("User folder", "", Shell("shell:Profile")),
            new("This PC", "", Shell("shell:MyComputerFolder")),
            new("Network", "", Shell("shell:NetworkPlacesFolder")),
            new("Recycle Bin", "", Shell("shell:RecycleBinFolder")),
        },
        new Place[]
        {
            new("Settings", "", () => Fg.Open("ms-settings:")),
            new("Control Panel", "", () => Fg.Open("control.exe")),                // sliders, like the controls of the classic icon
            new("Devices and printers", "", () => Fg.Open("ms-settings:bluetooth")),
            new("Task Manager", "", () => Fg.Open("taskmgr.exe")),
            new("Run", "", () => AppShell.Tap(0x5B, 0x52)),                       // Win+R
        },
    };

    /// <summary>Groups of the power menu (a line between them).</summary>
    public static readonly PowerItem[][] Power =
    {
        new PowerItem[]
        {
            new("Lock", () => Fg.Run("rundll32.exe", "user32.dll,LockWorkStation")),
            new("Sign out", () => Fg.Run("shutdown.exe", "/l")),
        },
        new PowerItem[]
        {
            new("Sleep", () => Fg.Run("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0")),
            new("Hibernate", () => Fg.Run("shutdown.exe", "/h")),
            new("Shut down", () => Fg.Run("shutdown.exe", "/s /t 0")),
            new("Restart", () => Fg.Run("shutdown.exe", "/r /t 0")),
        },
    };

    /// <summary>Shown by default: the folders and settings of the original column; the extra items are off.</summary>
    public const string DefaultPlacesHidden = "Videos|User folder|Network|Recycle Bin|Task Manager|Run";
    public const string DefaultPowerHidden = "Hibernate";

    static HashSet<string> Split(string list) => new((list ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries));

    public static bool IsPlaceShown(string id) => !Split(DockSettings.Current.ClassicPlacesHidden).Contains(id);
    public static bool IsPowerShown(string id) => !Split(DockSettings.Current.StartPowerHidden).Contains(id);

    public static void SetPlaceShown(string id, bool shown) =>
        DockSettings.Current.ClassicPlacesHidden = With(DockSettings.Current.ClassicPlacesHidden, id, shown);

    public static void SetPowerShown(string id, bool shown) =>
        DockSettings.Current.StartPowerHidden = With(DockSettings.Current.StartPowerHidden, id, shown);

    static string With(string list, string id, bool shown)
    {
        var hidden = Split(list);
        if (shown) hidden.Remove(id); else hidden.Add(id);
        return string.Join("|", hidden);
    }

    /// <summary>
    /// The groups in the order set with the mouse (edit mode; <see cref="DockSettings.ClassicPlacesOrder"/>): items may move
    /// between the groups; items not in the stored order (new ones) stay in their default group.
    /// </summary>
    public static List<List<Place>> OrderedPlaces()
    {
        var all = Places.SelectMany(g => g).ToDictionary(p => p.Id);
        var groups = DockSettings.Current.ClassicPlacesOrder.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(g => g.Split('|').Where(all.ContainsKey).Select(id => all[id]).ToList()).ToList();
        while (groups.Count < Places.Length) groups.Add(new List<Place>());
        var placed = new HashSet<string>(groups.SelectMany(g => g).Select(p => p.Id));
        for (int i = 0; i < Places.Length; i++)
            foreach (var p in Places[i]) if (placed.Add(p.Id)) groups[i].Add(p);
        return groups;
    }

    /// <summary>The groups with only the items that are switched on (empty groups left out).</summary>
    public static List<List<Place>> ShownPlaces() =>
        OrderedPlaces().Select(g => g.Where(p => IsPlaceShown(p.Id)).ToList()).Where(g => g.Count > 0).ToList();

    /// <summary>The power items in the order set in the settings (the same for Start and "Shut down or sign out").</summary>
    public static List<List<PowerItem>> OrderedPower() => ItemOrder.Apply(Power, p => p.Id, DockSettings.Current.StartPowerOrder);

    public static void MovePower(string id, int delta) =>
        DockSettings.Current.StartPowerOrder = ItemOrder.Move(Power, p => p.Id, DockSettings.Current.StartPowerOrder, id, delta);

    public static List<List<PowerItem>> ShownPower() =>
        OrderedPower().Select(g => SystemPower.WithUpdates(g.Where(p => IsPowerShown(p.Id)).ToList(), p => p.Id, (id, run) => new PowerItem(id, run)))
            .Where(g => g.Count > 0).ToList();
}
