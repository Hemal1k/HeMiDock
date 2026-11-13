namespace HeMiDock;

/// <summary>
/// One item of Start and of the search: an app, a folder of apps, a setting, a file … – the data and what a click does.
/// The look (icon, visibility of its parts) belongs to the app; it takes the icon from <see cref="AppShell"/> by <see cref="IconKey"/>.
/// </summary>
sealed class StartEntry
{
    public EntryKind Kind { get; init; }
    public string Name { get; set; }
    public string Subtitle { get; init; }
    public string Key { get; init; }                  // what gets launched / opened
    public string ExePath { get; init; }              // path to the .exe for classic programs
    public string IconKey { get; init; }              // null = font icon (Glyph)
    public bool IconIsFile { get; init; }
    public string Glyph { get; init; }
    public string OwnIcon { get; init; }              // own picture of a pinned app

    public string Group { get; set; }

    public string PinKey => ExePath ?? Key;
    /// <summary>Classic programs can run as administrator, also those known only by their place in the Start menu (consoles .msc); Store apps cannot.</summary>
    public bool CanElevate => Kind == EntryKind.App && (ExePath != null || !Key.Contains('!') || AppShell.PackageCanElevate(Key));
    public bool IsApp => Kind == EntryKind.App;

    /// <summary>Apps of a Start menu folder (only for <see cref="EntryKind.AppFolder"/>).</summary>
    public List<StartEntry> Children { get; init; }

    /// <summary>Level of the row in the tree of all apps (classic Start menu: apps of an opened folder).</summary>
    public int Level { get; set; }

    /// <summary>Folder rows of the classic tree: opened or closed.</summary>
    public bool Expanded { get; set; }

    public static StartEntry FromKey(string key)
    {
        StartPins.Own.TryGetValue(key, out var own);
        if (StartPins.IsGroup(key)) return new()                     // a group of pins opens like a folder of the Start menu
        {
            Kind = EntryKind.AppFolder,
            Name = own?.Name ?? L.T("Group"),
            Key = key,
            IconKey = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            IconIsFile = true,
            Children = (own?.Items ?? new()).Select(FromKey).ToList()
        };
        return new()
        {
            Kind = EntryKind.App,
            Name = own?.Name ?? AppCatalog.Apps?.FirstOrDefault(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Name ?? AppShell.AppName(key),
            Key = key,
            ExePath = AppShell.IsShellKey(key) ? null : key,
            IconKey = key,
            OwnIcon = own?.IconPath
        };
    }

    public static StartEntry From(AppEntry a) => new()
    {
        Kind = EntryKind.App,
        Name = a.Name,
        Key = a.Key,
        ExePath = a.ExePath,
        IconKey = a.Key
    };

    public static StartEntry From(AppFolder f) => new()
    {
        Kind = EntryKind.AppFolder,
        Name = f.Name,
        Key = f.Path,
        IconKey = f.Path,
        IconIsFile = true,
        Children = f.Apps.Select(From).ToList()
    };

    public void Run(bool admin = false)
    {
        switch (Kind)
        {
            case EntryKind.App:
                SearchUsage.Opened(Key);
                Host.Launching?.Invoke(PinKey);
                AppShell.Launch(admin && ExePath != null ? ExePath : Key, admin);
                break;
            case EntryKind.Calc:
                // a conversion copies only its number
                try { ClipboardText.Set(Key ?? Name); } catch (Exception ex) { Log.Write("Clipboard", ex); }
                break;
            case EntryKind.DockSetting:                                       // "title\nplace" of a HeMiDock setting
                Host.Command?.Invoke("setting:" + Key);
                break;
            default:
                Fg.Open(Key);
                break;
        }
    }

    /// <summary>
    /// What "Open file location" shows: the program (or its Start menu shortcut / Store app folder), the file or folder,
    /// for a recent file the file itself (not its shortcut in Recent).
    /// </summary>
    public string LocationPath => Kind switch
    {
        EntryKind.App => AppShell.FileLocation(Key, ExePath, Name),
        EntryKind.File or EntryKind.Folder or EntryKind.AppFolder => Key,
        EntryKind.Recent => AppShell.ResolveShortcut(Key).target is { } t && (File.Exists(t) || Directory.Exists(t)) ? t : Key,
        _ => null
    };
}
