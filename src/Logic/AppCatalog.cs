using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HeMiDock;

/// <summary>App from the Applications folder (shell:AppsFolder): Win32 and Store. Key = shell:AppsFolder\…, ExePath only for classic programs.</summary>
sealed record AppEntry(string Name, string Key, string ExePath)
{
    /// <summary>Key used to pin the app to the dock (the .exe path matches the key of a running window).</summary>
    public string PinKey => ExePath ?? Key;
}

/// <summary>Folder of the Start menu (Programs\…) with the apps in it – shown as one folder tile under all apps.</summary>
sealed record AppFolder(string Name, string Path, List<AppEntry> Apps);

/// <summary>List of all installed apps; loaded once in the background (own STA thread, shell COM objects).</summary>
static class AppCatalog
{
    static readonly Guid BhidEnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    static List<AppEntry> _apps = new();
    static List<AppFolder> _folders = new();
    static Thread _thread;

    public static bool IsLoaded { get; private set; }
    public static IReadOnlyList<AppEntry> Apps => _apps;

    /// <summary>Start menu folders with at least two apps (a folder with a single app shows the app itself, like Windows).</summary>
    public static IReadOnlyList<AppFolder> Folders => _folders;
    public static event Action Loaded;

    public static void Preload()
    {
        if (_thread != null) return;
        _thread = new Thread(Load) { IsBackground = true, Name = "AppCatalog" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        WatchInstalls();
    }

    /// <summary>Reloads the list (e.g. after an app was installed).</summary>
    public static void Reload()
    {
        if (_thread != null && _thread.IsAlive) { _again = true; return; }
        _thread = null;
        Preload();
    }

    static bool _again;
    static Timer _reloadTimer;
    static readonly List<FileSystemWatcher> _watchers = new();
    static Windows.ApplicationModel.PackageCatalog _packages;

    /// <summary>
    /// An app installed or removed while the dock runs shows up in Start: shortcuts in both Programs folders (classic
    /// programs) and Store packages are watched; the list is read again a few seconds after the last change.
    /// </summary>
    static void WatchInstalls()
    {
        if (_reloadTimer != null) return;
        _reloadTimer = new Timer(_ => Reload());
        void Changed() => _reloadTimer.Change(4000, Timeout.Infinite);
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var w = new FileSystemWatcher(root, "*.lnk") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
                w.Created += (_, _) => Changed();
                w.Deleted += (_, _) => Changed();
                w.Renamed += (_, _) => Changed();
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) { Log.Write("AppCatalog.Watch", ex); }
        }
        try
        {
            _packages = Windows.ApplicationModel.PackageCatalog.OpenForCurrentUser();
            _packages.PackageInstalling += (_, a) => { if (a.IsComplete) Changed(); };
            _packages.PackageUninstalling += (_, a) => { if (a.IsComplete) Changed(); };
            _packages.PackageUpdating += (_, a) => { if (a.IsComplete) Changed(); };
        }
        catch (Exception ex) { Log.Write("AppCatalog.Packages", ex); }
    }

    static void Load()
    {
        var list = new List<AppEntry>();
        try
        {
            var iid = typeof(IShellItem).GUID;
            object obj;
            try { Native.SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref iid, out obj); }
            catch { Native.SHCreateItemFromParsingName("shell:::{4234d49b-0245-4df3-b780-3893943456e1}", IntPtr.Zero, ref iid, out obj); }

            var folder = (IShellItem)obj;
            var bhid = BhidEnumItems;
            var iidEnum = typeof(IEnumShellItems).GUID;
            folder.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out var pEnum);
            var en = (IEnumShellItems)Marshal.GetObjectForIUnknown(pEnum);
            Marshal.Release(pEnum);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (en.Next(1, out var item, out uint got) == 0 && got == 1)
            {
                try
                {
                    var e = Read(item);
                    if (e != null && seen.Add(e.Name + "|" + (e.ExePath ?? e.Key))) list.Add(e);
                }
                catch { }
                finally { Marshal.ReleaseComObject(item); }
            }
        }
        catch (Exception ex) { Log.Write("AppCatalog.Load", ex); }
        try { AddInstalledPrograms(list); }
        catch (Exception ex) { Log.Write("AppCatalog.Installed", ex); }

        _apps = list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        try { _folders = ReadFolders(_apps); }
        catch (Exception ex) { Log.Write("AppCatalog.Folders", ex); }
        IsLoaded = true;
        Ui.Post(() => { StartPins.RepairStale(_apps); Loaded?.Invoke(); });

        // icons of all apps are loaded now (this thread is idle anyway), so the Start menu opens with them at once
        foreach (var key in StartPins.Keys.Where(k => !StartPins.IsGroup(k)).ToList()) TryIcon(key);
        foreach (var a in _apps) TryIcon(a.Key);
        foreach (var f in _folders) { try { AppShell.FileTypeIcon(f.Path); } catch { } }

        if (_again) { _again = false; _reloadTimer?.Change(1000, Timeout.Infinite); }   // something changed while reading
    }

    static void TryIcon(string key)
    {
        try { AppShell.Icon(key); } catch { }
    }

    static readonly string[] SkipFolders = { "Startup", "Maintenance" };

    /// <summary>
    /// Sub-folders of both Start menu Programs folders (all users + current user, same names merged). The apps of the
    /// Applications folder are flat, so they are matched to the shortcuts by their name.
    /// </summary>
    static List<AppFolder> ReadFolders(List<AppEntry> apps)
    {
        var byName = apps.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, (string Path, List<AppEntry> Apps)>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<AppEntry>();

        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                string folder = System.IO.Path.GetFileName(dir);
                if (SkipFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) continue;
                if (!folders.TryGetValue(folder, out var f)) folders[folder] = f = (dir, new List<AppEntry>());
                foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
                    if (byName.TryGetValue(System.IO.Path.GetFileNameWithoutExtension(lnk), out var app) && taken.Add(app))
                        f.Apps.Add(app);
            }
        }

        return folders
            .Where(f => f.Value.Apps.Count >= 2)
            .Select(f => new AppFolder(AppShell.DisplayName(f.Value.Path) ?? f.Key, f.Value.Path,
                f.Value.Apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList()))
            .ToList();
    }

    static readonly string[] SkipExtensions = { ".url", ".chm", ".pdf", ".htm", ".html", ".txt", ".rtf", ".hlp", ".xml", ".md", ".ini", ".doc", ".docx" };

    /// <summary>Apps of Windows that are no apps to open (Click to Do opens over the screen by its key): not in Start and the search.</summary>
    static readonly string[] HiddenApps = { "!ClickToDoApp" };

    /// <summary>
    /// Programs installed without a shortcut in Start (VLC can be installed so): their entry in the installed programs of
    /// Windows with the program as its icon. Only programs that exist, not their uninstallers, updates or parts of Windows,
    /// and not twice (same program or same name as an app already there).
    /// </summary>
    static void AddInstalledPrograms(List<AppEntry> list)
    {
        var exes = new HashSet<string>(list.Where(a => a.ExePath != null).Select(a => a.ExePath), StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(list.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall")
        };
        foreach (var (hive, path) in roots)
        {
            using var root = hive.OpenSubKey(path);
            if (root == null) continue;
            foreach (var id in root.GetSubKeyNames())
            {
                try
                {
                    using var k = root.OpenSubKey(id);
                    if (k?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                    if (k.GetValue("SystemComponent") is int sys && sys == 1 || k.GetValue("ParentKeyName") != null || k.GetValue("ReleaseType") is string) continue;
                    if (k.GetValue("DisplayIcon") is not string icon) continue;
                    string exe = Environment.ExpandEnvironmentVariables(icon.Split(',')[0].Trim().Trim('"'));
                    string file = System.IO.Path.GetFileName(exe);
                    if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe)) continue;
                    // installers kept by Windows and by setups (redistributables, cached packages) are no apps
                    if (exe.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase) || exe.Contains(@"\Installer\", StringComparison.OrdinalIgnoreCase)
                        || exe.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) continue;
                    if (file.Contains("unins", StringComparison.OrdinalIgnoreCase) || file.Contains("setup", StringComparison.OrdinalIgnoreCase)
                        || file.Contains("update", StringComparison.OrdinalIgnoreCase) || file.Contains("install", StringComparison.OrdinalIgnoreCase)) continue;
                    name = name.Trim();
                    if (!exes.Add(exe) || !names.Add(name)) continue;
                    list.Add(new AppEntry(name, exe, exe));
                }
                catch { }
            }
        }
    }

    static AppEntry Read(IShellItem item)
    {
        item.GetDisplayName(0, out string name);                          // SIGDN_NORMALDISPLAY
        item.GetDisplayName(0x80018001, out string rel);                  // SIGDN_PARENTRELATIVEPARSING
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(rel)) return null;
        if (HiddenApps.Any(h => rel.EndsWith(h, StringComparison.OrdinalIgnoreCase))) return null;

        string ext = rel.Contains('\\') ? System.IO.Path.GetExtension(rel) : "";
        if (SkipExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return null;
        if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("odinstal", StringComparison.OrdinalIgnoreCase)) return null;

        string exe = null;
        if (item is IShellItem2 i2)
        {
            var key = new PROPERTYKEY(new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2);   // System.Link.TargetParsingPath
            if (i2.GetString(ref key, out string target) == 0 && !string.IsNullOrEmpty(target)
                && (target = Environment.ExpandEnvironmentVariables(target)).EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target)
                && !target.EndsWith(@"\mstsc.exe", StringComparison.OrdinalIgnoreCase))   // a remote app (RDP) is not mstsc itself
                exe = target;
        }
        return new AppEntry(name, @"shell:AppsFolder\" + rel, exe);
    }
}

/// <summary>Apps pinned to the Start menu (startpins.json).</summary>
static class StartPins
{
    static List<string> _keys;
    public static event Action Changed;

    static string FilePath => AppPaths.Of("startpins.json");

    public static IReadOnlyList<string> Keys
    {
        get { Ensure(); return _keys; }
    }

    static void Ensure()
    {
        if (_keys != null) return;
        try { _keys = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? Defaults(); }
        catch { _keys = Defaults(); }
    }

    /// <summary>
    /// A pin whose app Windows no longer knows under that name (it was installed again in another way) goes over to the app
    /// with the same program or the same name, with its current name and icon.
    /// </summary>
    public static void RepairStale(IReadOnlyList<AppEntry> apps)
    {
        Ensure();
        const string Prefix = @"shell:AppsFolder\";
        bool changed = false;
        for (int i = 0; i < _keys.Count; i++)
        {
            string key = _keys[i];
            if (!key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || apps.Any(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) || AppShell.DisplayName(key) != null) continue;
            string file = AppShell.StartMenuFile(key);
            string name = System.IO.Path.GetFileNameWithoutExtension(file);
            var app = apps.FirstOrDefault(a => file.Equals(a.ExePath, StringComparison.OrdinalIgnoreCase)) ?? apps.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (app == null || _keys.Contains(app.Key, StringComparer.OrdinalIgnoreCase)) continue;
            Diag.Write("Start", $"pin {key} now points to {app.Key}");
            _keys[i] = app.Key;
            changed = true;
        }
        if (changed) Save();
    }

    public static bool IsPinned(string key)
    {
        Ensure();
        return _keys.Contains(key, StringComparer.OrdinalIgnoreCase) || GroupOf(key) != null;
    }

    public static void Add(string key)
    {
        Ensure();
        if (IsPinned(key)) return;
        _keys.Add(key);
        Save();
    }

    public static void Remove(string key)
    {
        Ensure();
        _keys.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (GroupOf(key) is { } group)
        {
            Own[group].Items.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            DissolveIfSmall(group);
        }
        SetIconFile(key, null);
        Own.Remove(key);
        SaveOwn();
        Save();
    }

    // ───── own names and pictures of pins (edit mode) ─────

    public sealed class OwnLook
    {
        public string Name { get; set; }
        public string IconPath { get; set; }
        /// <summary>Apps of a group of pins (its key in the pins is "group:…").</summary>
        public List<string> Items { get; set; }
    }

    const string GroupPrefix = "group:";

    public static bool IsGroup(string key) => key != null && key.StartsWith(GroupPrefix, StringComparison.Ordinal);

    /// <summary>The group a pin is in, null when it is not in any.</summary>
    public static string GroupOf(string key) =>
        Own.FirstOrDefault(x => IsGroup(x.Key) && x.Value.Items?.Contains(key, StringComparer.OrdinalIgnoreCase) == true).Key;

    /// <summary>A pin dropped on another one: they make a group, or it joins the group. Returns a newly made group.</summary>
    public static string Group(string key, string target)
    {
        Ensure();
        if (IsGroup(key) || key.Equals(target, StringComparison.OrdinalIgnoreCase)) return null;
        target = GroupOf(target) ?? target;                           // on an app of an opened group: into that group
        string old = GroupOf(key), made = null;
        if (old == target || !_keys.Contains(target, StringComparer.OrdinalIgnoreCase)) return null;
        _keys.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (old != null)
        {
            Own[old].Items.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            DissolveIfSmall(old);
        }
        if (IsGroup(target)) Own[target].Items.Add(key);
        else
        {
            made = GroupPrefix + Guid.NewGuid().ToString("N");
            Own[made] = new OwnLook { Name = L.T("Group"), Items = new List<string> { target, key } };
            _keys[_keys.FindIndex(k => k.Equals(target, StringComparison.OrdinalIgnoreCase))] = made;
        }
        SaveOwn();
        Save();
        return made;
    }

    /// <summary>A pin goes out of its group, right after it (a group of one app is no group any more).</summary>
    public static void RemoveFromGroup(string key)
    {
        Ensure();
        if (GroupOf(key) is not { } id) return;
        var items = Own[id].Items;
        items.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        int at = _keys.IndexOf(id);
        _keys.Insert(at + 1, key);
        DissolveIfSmall(id);
        SaveOwn();
        Save();
    }

    /// <summary>All apps of a group come back to the pins in its place.</summary>
    public static void Ungroup(string id)
    {
        Ensure();
        int at = _keys.IndexOf(id);
        if (at < 0 || !Own.TryGetValue(id, out var look)) return;
        _keys.RemoveAt(at);
        _keys.InsertRange(at, look.Items ?? new());
        Own.Remove(id);
        SaveOwn();
        Save();
    }

    static void DissolveIfSmall(string id)
    {
        var look = Own[id];
        if (look.Items.Count > 1) return;
        int at = _keys.IndexOf(id);
        if (at >= 0)
        {
            _keys.RemoveAt(at);
            _keys.InsertRange(at, look.Items);
        }
        Own.Remove(id);
    }

    static Dictionary<string, OwnLook> _own;
    static string OwnFile => AppPaths.Of("startpins-own.json");

    /// <summary>Key of a pin → its own name / picture.</summary>
    public static Dictionary<string, OwnLook> Own
    {
        get
        {
            if (_own != null) return _own;
            _own = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(OwnFile) && JsonSerializer.Deserialize<Dictionary<string, OwnLook>>(File.ReadAllText(OwnFile)) is { } read)
                    foreach (var (k, v) in read) _own[k] = v;
            }
            catch (Exception ex) { Log.Write("StartPins.Own", ex); }
            return _own;
        }
    }

    static void SaveOwn()
    {
        try
        {
            if (Own.Count == 0) { File.Delete(OwnFile); return; }        // nothing own – no file
            Directory.CreateDirectory(AppPaths.Folder);
            File.WriteAllText(OwnFile, JsonSerializer.Serialize(Own));
        }
        catch (Exception ex) { Log.Write("StartPins.SaveOwn", ex); }
    }

    /// <summary>Own name of a pin (empty = the name of the app again).</summary>
    public static void Rename(string key, string name)
    {
        if (!Own.TryGetValue(key, out var look)) Own[key] = look = new OwnLook();
        look.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        ChangedOwn(key, look);
    }

    /// <summary>Own picture of a pin (null = the icon of the app again).</summary>
    public static void SetIcon(string key, string iconPath)
    {
        SetIconFile(key, iconPath);
        if (!Own.TryGetValue(key, out var look)) Own[key] = look = new OwnLook();
        look.IconPath = iconPath;
        ChangedOwn(key, look);
    }

    /// <summary>A copy made for the old picture of the pin is not needed any more.</summary>
    static void SetIconFile(string key, string newPath)
    {
        if (Own.TryGetValue(key, out var look) && look.IconPath is { } old && old != newPath
            && old.StartsWith(AppPaths.Of("Icons"), StringComparison.OrdinalIgnoreCase))
            try { File.Delete(old); } catch { }
    }

    static void ChangedOwn(string key, OwnLook look)
    {
        if (look.Name == null && look.IconPath == null && look.Items == null) Own.Remove(key);
        SaveOwn();
        Changed?.Invoke();
    }

    /// <summary>Other own names and pictures instead of the current ones (imported settings).</summary>
    public static void ReplaceOwn(Dictionary<string, OwnLook> own)
    {
        _own = new(own, StringComparer.OrdinalIgnoreCase);
        SaveOwn();
    }

    /// <summary>Moves a pin before / after another one; an app of an opened group can be moved inside it or out of it.</summary>
    public static void Move(string key, string target, bool after)
    {
        Ensure();
        string group = GroupOf(key), targetGroup = GroupOf(target);
        if (group != null)
        {
            var items = Own[group].Items;
            items.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (group == targetGroup) items.Insert(items.FindIndex(k => k.Equals(target, StringComparison.OrdinalIgnoreCase)) + (after ? 1 : 0), key);
            else
            {
                int to2 = _keys.FindIndex(k => k.Equals(targetGroup ?? target, StringComparison.OrdinalIgnoreCase));
                _keys.Insert(to2 < 0 ? _keys.Count : after ? to2 + 1 : to2, key);
                DissolveIfSmall(group);
            }
            SaveOwn();
            Save();
            return;
        }
        target = targetGroup ?? target;
        int from = _keys.FindIndex(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (from < 0) return;
        var k0 = _keys[from];
        _keys.RemoveAt(from);
        int to = _keys.FindIndex(k => k.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (to < 0) { _keys.Insert(from, k0); return; }
        _keys.Insert(after ? to + 1 : to, k0);
        Save();
    }

    /// <summary>Reads the pins again (settings restored from a backup).</summary>
    public static void Reload()
    {
        _keys = null;
        _own = null;
        Changed?.Invoke();
    }

    /// <summary>Other pins instead of the current ones (imported settings).</summary>
    public static void Replace(IEnumerable<string> keys)
    {
        _keys = keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Save();
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_keys));
        }
        catch (Exception ex) { Log.Write("StartPins.Save", ex); }
        Changed?.Invoke();
    }

    /// <summary>Default pins: common Windows 11 apps (anything not installed is skipped).</summary>
    static List<string> Defaults()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        const string A = @"shell:AppsFolder\";
        return new[]
        {
            $@"{pf86}\Microsoft\Edge\Application\msedge.exe",
            $@"{win}\explorer.exe",
            A + "Microsoft.WindowsStore_8wekyb3d8bbwe!App",
            A + "Microsoft.Windows.Photos_8wekyb3d8bbwe!App",
            A + "microsoft.windowscommunicationsapps_8wekyb3d8bbwe!microsoft.windowslive.mail",
            A + "microsoft.windowscommunicationsapps_8wekyb3d8bbwe!microsoft.windowslive.calendar",
            A + "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App",
            A + "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
            A + "Microsoft.Paint_8wekyb3d8bbwe!App",
            A + "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            A + "Microsoft.ScreenSketch_8wekyb3d8bbwe!App",
            A + "Microsoft.WindowsAlarms_8wekyb3d8bbwe!App",
            A + "Microsoft.WindowsCamera_8wekyb3d8bbwe!App",
            A + "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
            $@"{win}\System32\Taskmgr.exe"
        }.Where(AppShell.Exists).ToList();
    }
}

/// <summary>Recently opened files (Recent folder).</summary>
static class RecentFiles
{
    public static List<StartEntry> Load(int count)
    {
        var list = new List<StartEntry>();
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (!Directory.Exists(dir)) return list;
            var files = new DirectoryInfo(dir).GetFiles("*.lnk").OrderByDescending(f => f.LastWriteTime).Take(count);
            foreach (var f in files)
            {
                list.Add(new StartEntry
                {
                    Kind = EntryKind.Recent,
                    Name = System.IO.Path.GetFileNameWithoutExtension(f.Name),
                    Subtitle = f.LastWriteTime.ToString("g"),
                    Key = f.FullName,
                    IconKey = f.FullName,
                    IconIsFile = true
                });
            }
        }
        catch (Exception ex) { Log.Write("RecentFiles", ex); }
        return list;
    }
}
