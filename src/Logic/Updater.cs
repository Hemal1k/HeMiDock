using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace HeMiDock;

/// <summary>
/// New versions from the releases of the project on GitHub (its servers are the update server): checked a moment after the
/// start and then regularly, as often as chosen (Settings → About → Check for updates automatically) or by the button there. A newer one shows
/// a sign among the status icons; an installed dock downloads its installer and installs it (the dock ends and starts again),
/// a portable one opens the page of the release.
/// </summary>
static class Updater
{
    public const string Repo = "Hemal1k/HeMiDock";
    public static string RepoUrl => "https://github.com/" + Repo;
    public static string IssuesUrl => RepoUrl + "/issues";
    const string SetupAsset = "HeMiDock-Setup.exe";

    /// <summary>A release: its installer and the checksums of its files (SHA256SUMS.txt, checked before the installer runs).</summary>
    public sealed record Release(Version Version, string Name, string Page, string SetupUrl, string SumsUrl);

    /// <summary>A newer release than this dock (and not skipped); null when there is none.</summary>
    public static Release Available { get; private set; }
    public static event Action Changed;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static Microsoft.UI.Dispatching.DispatcherQueueTimer _daily;

    static Updater()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("HeMiDock-Updater");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>The version of this dock (1.5.0).</summary>
    public static Version Current
    {
        get
        {
            var v = (System.Reflection.Assembly.GetEntryAssembly() ?? typeof(Updater).Assembly).GetName().Version ?? new Version(0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>Installed by its installer (with its uninstall entry): it can update itself.</summary>
    public static bool Installed => InstalledFor != null;

    /// <summary>
    /// "/CURRENTUSER" when the installer put this dock into the programs of the user (HKCU uninstall entry), "/ALLUSERS" when
    /// into Program Files for all users (HKLM – its update asks for administrator rights); null for the zip / portable one.
    /// </summary>
    static string InstalledFor
    {
        get
        {
            const string key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HeMiDock_is1";
            string here = Path.GetDirectoryName(Environment.ProcessPath);
            bool Same(Microsoft.Win32.RegistryKey root)
            {
                try
                {
                    using var k = root.OpenSubKey(key);
                    return k?.GetValue("InstallLocation") is string place
                        && Path.GetFullPath(place).TrimEnd('\\').Equals(here, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
            if (Same(Microsoft.Win32.Registry.CurrentUser)) return "/CURRENTUSER";
            using var machine = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            return Same(machine) ? "/ALLUSERS" : null;
        }
    }

    /// <summary>
    /// On the thread of the interface: the first check 30 s after the start, then as often as chosen in the settings (every
    /// 6 hours by default, while switched on). With
    /// "Install updates automatically" a new version is installed in the background: quietly (/VERYSILENT, no window of the
    /// installer), the dock starts again by itself.
    /// </summary>
    public static void Start()
    {
        if (_daily != null) return;
        TakeSetupChoice();
        _ = Task.Run(DeleteOldDownloads);
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var first = queue.CreateTimer();
        first.Interval = TimeSpan.FromSeconds(30);
        first.IsRepeating = false;
        // with HeMi Launcher on this PC the updates are its job: the dock only shows what it found
        first.Tick += (_, _) => { if (DockSettings.Current.CheckUpdates && !HeMiLauncher.Present) _ = CheckAsync(quiet: true); };
        first.Start();
        _daily = queue.CreateTimer();
        _daily.Interval = DockSettings.Current.UpdateEvery;
        _daily.Tick += (_, _) => { if (DockSettings.Current.CheckUpdates && !HeMiLauncher.Present) _ = CheckAsync(quiet: true); };
        _daily.Start();
        // another choice of "How often to check": the timer starts again with the new time (on the thread of the interface)
        DockSettings.Current.PropertyChanged += (_, _) =>
        {
            if (SynchronizationContext.Current == Ui.Context) ApplyInterval();
            else Ui.Post(ApplyInterval);
        };
        _launcher = queue.CreateTimer();
        _launcher.Interval = TimeSpan.FromSeconds(30);
        _launcher.Tick += (_, _) => WatchLauncher();
        _launcher.Start();
        WatchLauncher();
    }

    /// <summary>Restarts the regular check when the time between the checks changed in the settings.</summary>
    static void ApplyInterval()
    {
        var every = DockSettings.Current.UpdateEvery;
        if (_daily == null || _daily.Interval == every) return;
        _daily.Stop();
        _daily.Interval = every;
        _daily.Start();
    }

    /// <summary>
    /// What was chosen in the installer (Updates: look for them, install them): it writes it to HKCU\Software\HeMi\HeMiDock
    /// instead of starting the app; taken into the settings once and removed.
    /// </summary>
    static void TakeSetupChoice()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\HeMi\HeMiDock", writable: true);
            if (key == null) return;
            if (key.GetValue("SetupCheckUpdates") is int check) DockSettings.Current.CheckUpdates = check != 0;
            if (key.GetValue("SetupInstallUpdates") is int install) DockSettings.Current.InstallUpdates = install != 0;
            key.DeleteValue("SetupCheckUpdates", false);
            key.DeleteValue("SetupInstallUpdates", false);
        }
        catch (Exception ex) { Log.Write("Setup choice", ex); }
    }

    static Microsoft.UI.Dispatching.DispatcherQueueTimer _launcher;

    /// <summary>The updates HeMi Launcher found (read every 30 s); a version the dock found itself goes when the launcher came.</summary>
    static void WatchLauncher()
    {
        bool changed = HeMiLauncher.Refresh();
        if (HeMiLauncher.Present && Available != null) { Available = null; changed = true; }
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// Asks GitHub for the latest release; null on an error (no internet …). <paramref name="quiet"/>: a version skipped by the
    /// user does not count (the button of the settings shows it anyway).
    /// </summary>
    public static async Task<Release> CheckAsync(bool quiet)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
            string setup = null, sums = null;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                    if (string.Equals(a.GetProperty("name").GetString(), SetupAsset, StringComparison.OrdinalIgnoreCase))
                        setup = OwnDownload(a.GetProperty("browser_download_url").GetString());
                    else if (string.Equals(a.GetProperty("name").GetString(), "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                        sums = OwnDownload(a.GetProperty("browser_download_url").GetString());
            var release = new Release(version, root.TryGetProperty("name", out var n) ? n.GetString() : tag, root.GetProperty("html_url").GetString(), setup, sums);
            bool newer = version > Current && !(quiet && DockSettings.Current.SkippedUpdate == version.ToString());
            Ui.Post(() =>
            {
                Available = newer ? release : null;
                Changed?.Invoke();
                // the automatic check with "Install updates automatically": installed right away (the dock starts again)
                if (newer && quiet && DockSettings.Current.InstallUpdates && Installed) _ = InstallAsync(release);
            });
            return release;
        }
        catch (Exception ex) { Log.Write("Update check", ex); return null; }
    }

    /// <summary>
    /// A download of the releases of this project only (https://github.com/Hemal1k/HeMiDock/releases/download/…); anything
    /// else is null – the update then opens the page of the release instead of downloading.
    /// </summary>
    internal static string OwnDownload(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && u.IsDefaultPort
        && u.AbsolutePath.StartsWith($"/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase) ? url : null;

    /// <summary>The folders of earlier updates in %TEMP% (the installer cannot delete itself): removed at the next start.</summary>
    static void DeleteOldDownloads()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), "HeMiDock-update-*"))
                try { Directory.Delete(dir, true); }
                catch (Exception ex) { Log.Write("Old update", ex); }   // the installer still runs from it
        }
        catch (Exception ex) { Log.Write("Old updates", ex); }
    }

    /// <summary>The found version is not offered again (a newer one is).</summary>
    public static void Skip()
    {
        if (Available == null) return;
        DockSettings.Current.SkippedUpdate = Available.Version.ToString();
        Available = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Installed: downloads the installer and runs it quietly – the dock ends, the installer replaces it and starts it again.
    /// Otherwise (the zip or the portable one): the page of the release.
    /// </summary>
    public static async Task InstallAsync(Release release)
    {
        string mode = InstalledFor;
        if (mode == null || release.SetupUrl == null || release.SumsUrl == null) { Fg.Open(release.Page); return; }
        if (Busy != null) return;                                        // already downloading
        string v = release.Version.ToString(3);
        try
        {
            Host.ShowInfo?.Invoke(L.T("Downloading HeMiDock {0}…", v));
            SetBusy(L.T("Downloading HeMiDock {0}…", v));
            // a new folder of its own (not a name in %TEMP% known beforehand), deleted at the next start
            string file = Path.Combine(Directory.CreateTempSubdirectory("HeMiDock-update-").FullName, SetupAsset);
            using (var response = await Http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? 0, done = 0;
                int shown = -1;
                await using var from = await response.Content.ReadAsStreamAsync();
                await using var to = File.Create(file);
                var buffer = new byte[81920];
                for (int n; (n = await from.ReadAsync(buffer)) > 0;)
                {
                    await to.WriteAsync(buffer.AsMemory(0, n));
                    done += n;
                    // the progress on the sign of the new version and in the settings (each whole percent)
                    int percent = total > 0 ? (int)(done * 100 / total) : -1;
                    if (percent != shown) { shown = percent; SetBusy(percent >= 0 ? L.T("Downloading HeMiDock {0} – {1}%", v, percent) : L.T("Downloading HeMiDock {0}…", v)); }
                }
            }
            SetBusy(L.T("Installing HeMiDock {0}…", v));
            // the installer runs only when it is exactly the file of the release (SHA256SUMS.txt of the same release)
            string sums = await Http.GetStringAsync(release.SumsUrl);
            string want = sums.Split('\n').Select(l => l.Trim().Split((char[])null, 2, StringSplitOptions.RemoveEmptyEntries))
                .Where(p => p.Length == 2 && p[1].TrimStart('*').Equals(SetupAsset, StringComparison.OrdinalIgnoreCase)).Select(p => p[0].ToLowerInvariant()).FirstOrDefault();
            // held open (others may only read it) from the check until it runs: nothing can change it in between
            using (var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                string have = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(hold)).ToLowerInvariant();
                if (want == null || want != have)
                {
                    hold.Dispose();
                    File.Delete(file);
                    throw new InvalidDataException($"{SetupAsset}: the checksum does not match");
                }
                // quietly, without its window (the user, 2026-11-23); for all users the installer asks for administrator rights
                // itself (UAC), for the user it runs without them
                Process.Start(new ProcessStartInfo(file, $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE {mode}") { UseShellExecute = true });
            }
            App.Quit();                                                  // the installer replaces the files and starts the dock again
        }
        catch (Exception ex)
        {
            Log.Write("Update install", ex);
            SetBusy(null);
            Fg.Open(release.Page);                                      // the download failed: the page instead
        }
    }

    /// <summary>What the update does now ("Downloading HeMiDock 1.9.7 – 45%"), null when nothing.</summary>
    public static string Busy { get; private set; }

    static void SetBusy(string text) => Ui.Post(() => { Busy = text; Changed?.Invoke(); });
}

/// <summary>
/// HeMi Launcher (installs and updates the HeMi apps): when it is on this PC, HeMiDock leaves its updates to it – no own
/// checks, the Updates of the settings open the launcher, and the status icon of new versions shows what the launcher found
/// for all HeMi apps (%LocalAppData%\HeMiLauncher\updates.txt, one line "&lt;id&gt; &lt;version&gt;" each).
/// </summary>
static class HeMiLauncher
{
    static string UpdatesFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeMi", "HeMiLauncher", "updates.txt");

    /// <summary>
    /// The exe of the launcher (it writes where it is at each start; null when it is not there any more, or when the path
    /// there is not a HeMiLauncher.exe).
    /// </summary>
    public static string Exe
    {
        get
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\HeMi\HeMiLauncher");
                return k?.GetValue("Path") is string p && Path.IsPathFullyQualified(p)
                    && Path.GetFileName(p).Equals("HeMiLauncher.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(p) ? p : null;
            }
            catch { return null; }   // no launcher: the dock looks for its updates itself
        }
    }

    public static bool Present => Exe != null;

    /// <summary>The apps with a new version, as the launcher found them ("HeMiPix 1.1.0").</summary>
    public static IReadOnlyList<string> Updates { get; private set; } = [];

    /// <summary>Reads the updates again; true when they changed.</summary>
    public static bool Refresh()
    {
        List<string> now = [];
        try
        {
            if (Present && File.Exists(UpdatesFile))
                foreach (var line in File.ReadAllLines(UpdatesFile))
                    if (line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries) is [var id, var v])
                        now.Add($"{(id == "HeMiDark" ? "HeMi Dark" : id == "HeMiLauncher" ? "HeMi Launcher" : id)} {v.Trim()}");
        }
        catch (Exception ex) { Log.Write("Launcher updates", ex); }
        if (now.SequenceEqual(Updates)) return false;
        Updates = now;
        return true;
    }

    /// <summary>Opens the launcher (the running one comes to the front) and lets it look for updates.</summary>
    public static void Open()
    {
        if (Exe is not string exe) return;
        try { Process.Start(new ProcessStartInfo(exe, "--check") { UseShellExecute = false }); }
        catch (Exception ex) { Log.Write("Open HeMi Launcher", ex); }
    }
}
