using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HeMiDock;

/// <summary>
/// Protection against a crash of the dock: a small second process of the app waits for the dock. When the dock ends without
/// saying it ends on purpose, the Windows taskbar comes back at once and the dock is started again – at most three times
/// in five minutes, then the taskbar stays and the user is told. The second process starts without any user interface.
/// </summary>
static class Watchdog
{
    const string CleanEvent = "HeMiDock_CleanExit";
    const int MaxRestarts = 3;
    static EventWaitHandle _clean;
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBox(IntPtr owner, string text, string caption, uint type);

    /// <summary>The dock: starts its watchdog.</summary>
    public static void Start()
    {
        try
        {
            _clean = new EventWaitHandle(false, EventResetMode.ManualReset, CleanEvent);
            _clean.Reset();
            Process.Start(new ProcessStartInfo(Environment.ProcessPath, $"--watchdog {Environment.ProcessId}") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        }
        catch (Exception ex) { Log.Write("Watchdog start", ex); }
    }

    /// <summary>The dock ends on purpose (quit, sign out, shut down) – nothing to repair.</summary>
    public static void MarkClean()
    {
        try { _clean?.Set(); } catch { }
    }

    /// <summary>The watchdog process: waits for the dock, then repairs what is needed.</summary>
    public static void Run(int pid)
    {
        try
        {
            using var clean = new EventWaitHandle(false, EventResetMode.ManualReset, CleanEvent);
            using (var dock = Process.GetProcessById(pid))
            {
                using (var me = Process.GetCurrentProcess()) SetProcessWorkingSetSize(me.Handle, -1, -1);   // it only waits: gives back its memory
                dock.WaitForExit();
            }
            // ended on purpose, or ended by Windows going down (a forced restart ends the apps without telling them first): a
            // dock started again then came up in the middle of the shutdown and flashed an error
            if (clean.WaitOne(0) || SystemPower.ShuttingDown) return;

            Diag.Write("Watchdog", "the dock ended unexpectedly – taskbar restored");
            TaskbarController.Restore();
            if (RecordCrash() <= MaxRestarts)
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false })?.Dispose();
                return;
            }
            L.Apply(DockSettings.Current.Language);
            MessageBox(IntPtr.Zero, L.T("HeMiDock ended unexpectedly several times in a row, so it was not started again. The Windows taskbar is back."), "HeMiDock", 0x40);
        }
        catch (ArgumentException) { /* the dock is already gone */ }
        catch (Exception ex) { Log.Write("Watchdog", ex); }
    }

    /// <summary>Remembers this crash; how many there were in the last five minutes.</summary>
    static int RecordCrash()
    {
        string file = AppPaths.Of("crash-times.json");
        var times = new List<DateTime>();
        try { times = JsonSerializer.Deserialize<List<DateTime>>(File.ReadAllText(file)) ?? times; } catch { }
        times.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromMinutes(5));
        times.Add(DateTime.UtcNow);
        try { Directory.CreateDirectory(AppPaths.Folder); File.WriteAllText(file, JsonSerializer.Serialize(times)); } catch { }
        return times.Count;
    }
}

/// <summary>Copies of the settings and pinned apps (Backups in the settings folder), the five newest are kept.</summary>
static class SettingsBackup
{
    static readonly string[] Files = { "settings.json", "pinned.json", "startpins.json", "startpins-own.json", "tray.json" };
    static string Root => AppPaths.Of("Backups");

    /// <summary>Saves the current state (before an import, a reset, a preset or after an update).</summary>
    public static void Save(string reason)
    {
        try
        {
            string dir = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(dir);
            foreach (var name in Files)
                if (File.Exists(AppPaths.Of(name))) File.Copy(AppPaths.Of(name), Path.Combine(dir, name), true);
            foreach (var old in Directory.GetDirectories(Root).OrderByDescending(d => d).Skip(5))
                Directory.Delete(old, true);
            Diag.Write("Backup", reason);
        }
        catch (Exception ex) { Log.Write("Settings backup", ex); }
    }

    /// <summary>When the newest backup was made, null without any.</summary>
    public static DateTime? Newest => Directory.Exists(Root) && Directory.GetDirectories(Root).OrderByDescending(d => d).FirstOrDefault() is { } d
        ? Directory.GetCreationTime(d) : null;

    /// <summary>Once after an update: the settings of the previous version are kept.</summary>
    public static void AfterUpdate()
    {
        string mark = AppPaths.Of("last-version.txt");
        try
        {
            string now = AppInfo.Version, last = File.Exists(mark) ? File.ReadAllText(mark) : null;
            if (last == now) return;
            if (last != null) Save("update from " + last);
            Directory.CreateDirectory(AppPaths.Folder);
            File.WriteAllText(mark, now);
        }
        catch (Exception ex) { Log.Write("Settings backup", ex); }
    }

    /// <summary>Back to the newest backup; the state until now becomes a backup itself, so this can be undone the same way.</summary>
    public static bool RestoreNewest()
    {
        try
        {
            if (!Directory.Exists(Root) || Directory.GetDirectories(Root).OrderByDescending(d => d).FirstOrDefault() is not { } dir) return false;
            var read = Files.ToDictionary(n => n, n => File.Exists(Path.Combine(dir, n)) ? File.ReadAllBytes(Path.Combine(dir, n)) : null);
            Directory.Delete(dir, true);
            Save("before restoring");
            foreach (var (name, bytes) in read)
            {
                if (bytes != null) File.WriteAllBytes(AppPaths.Of(name), bytes);
                else if (File.Exists(AppPaths.Of(name))) File.Delete(AppPaths.Of(name));
            }
            DockSettings.Current.Import(AppPaths.Of("settings.json"), false, backup: false);
            DockState.Save(DockState.Load());                                // the dock shows the restored pins
            StartPins.Reload();
            return true;
        }
        catch (Exception ex) { Log.Write("Settings restore", ex); return false; }
    }
}

/// <summary>
/// The pinned apps of the dock and Start in an exported settings file (Settings → Settings file → Export with the pinned
/// apps), so they can go to another PC. Files of the app they use (shortcuts taken over from the taskbar, own icons) are
/// put into the file itself; paths in the app's folder and in the user folder are written relative to them, because the
/// user name may differ on the other PC.
/// </summary>
static class PinTransfer
{
    const string DockKey = "PinnedDock", StartKey = "PinnedStart", StartOwnKey = "PinnedStartOwn", FilesKey = "PinnedFiles";
    const string Own = "%HEMIDOCK%", Home = "%USERPROFILE%";
    const long MaxFile = 4 * 1024 * 1024;

    static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static bool Under(string path, string folder) => path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Connects the export and import of the settings with the pins.</summary>
    public static void Connect()
    {
        Host.Backup = SettingsBackup.Save;
        Host.AddPins = AddTo;
        Host.HasPins = In;
        Host.ApplyPins = Apply;
    }

    /// <summary>Adds the pins (and the files they need) to the values of an export.</summary>
    public static void AddTo(Dictionary<string, object> values)
    {
        var files = new Dictionary<string, string>();
        string Portable(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (Under(path, AppPaths.Folder))
            {
                string name = Own + path[AppPaths.Folder.Length..];
                try
                {
                    var file = new FileInfo(path);
                    if (file.Exists && file.Length <= MaxFile) files[name] = Convert.ToBase64String(File.ReadAllBytes(path));
                }
                catch (Exception ex) { Log.Write("PinTransfer " + path, ex); }
                return name;
            }
            return Under(path, Profile) ? Home + path[Profile.Length..] : path;
        }

        values[DockKey] = DockState.Load().Select(p => new PinnedApp
        {
            Path = Portable(p.Path), Arguments = p.Arguments, AppId = p.AppId, Name = p.Name,
            IconPath = Portable(p.IconPath), Spacer = p.Spacer, Line = p.Line
        }).ToList();
        values[StartKey] = StartPins.Keys.Select(Portable).ToList();
        values[StartOwnKey] = StartPins.Own.ToDictionary(x => Portable(x.Key), x => new StartPins.OwnLook { Name = x.Value.Name, IconPath = Portable(x.Value.IconPath), Items = x.Value.Items?.Select(Portable).ToList() });
        values[FilesKey] = files;
    }

    /// <summary>The exported file contains pinned apps (an export with them).</summary>
    public static bool In(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && (root.TryGetProperty(DockKey, out _) || root.TryGetProperty(StartKey, out _));

    /// <summary>Takes over the pins of an export instead of the current ones (their files go into the app's folder).</summary>
    public static void Apply(JsonElement root)
    {
        string Local(string path) =>
            path == null ? null
            : path.StartsWith(Own, StringComparison.OrdinalIgnoreCase) ? AppPaths.Folder + path[Own.Length..]
            : path.StartsWith(Home, StringComparison.OrdinalIgnoreCase) ? Profile + path[Home.Length..]
            : path;

        if (root.TryGetProperty(FilesKey, out var files) && files.ValueKind == JsonValueKind.Object)
            foreach (var f in files.EnumerateObject())
            {
                try
                {
                    // only into the app's own folder, whatever the name in the file says
                    string path = Path.GetFullPath(Local(f.Name));
                    if (!f.Name.StartsWith(Own, StringComparison.OrdinalIgnoreCase) || !Under(path, AppPaths.Folder)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, Convert.FromBase64String(f.Value.GetString() ?? ""));
                }
                catch (Exception ex) { Log.Write("PinTransfer " + f.Name, ex); }
            }

        if (root.TryGetProperty(DockKey, out var dock) && dock.ValueKind == JsonValueKind.Array)
        {
            var pinned = dock.Deserialize<List<PinnedApp>>() ?? new();
            foreach (var p in pinned)
            {
                p.Path = Local(p.Path);
                p.IconPath = Local(p.IconPath);
            }
            DockState.Save(pinned);
        }

        if (root.TryGetProperty(StartKey, out _))                        // own names and pictures of the pins (none in an older export)
        {
            var own = root.TryGetProperty(StartOwnKey, out var ownEl) && ownEl.ValueKind == JsonValueKind.Object
                ? ownEl.Deserialize<Dictionary<string, StartPins.OwnLook>>() ?? new() : new();
            StartPins.ReplaceOwn(own.ToDictionary(x => Local(x.Key), x => new StartPins.OwnLook { Name = x.Value.Name, IconPath = Local(x.Value.IconPath), Items = x.Value.Items?.Select(Local).ToList() }, StringComparer.OrdinalIgnoreCase));
        }

        if (root.TryGetProperty(StartKey, out var start) && start.ValueKind == JsonValueKind.Array)
            StartPins.Replace((start.Deserialize<List<string>>() ?? new()).Select(Local));
    }
}
