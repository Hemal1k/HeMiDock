using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace HeMiDock;

/// <summary>
/// New versions from the releases of the project on GitHub (its servers are the update server): checked a moment after the
/// start and then once a day (Settings → About → Check for updates automatically) or by the button there. A newer one shows
/// a sign among the status icons; an installed dock downloads its installer and installs it (the dock ends and starts again),
/// a portable one opens the page of the release.
/// </summary>
static class Updater
{
    public const string Repo = "Hemal1k/HeMiDock";
    public static string RepoUrl => "https://github.com/" + Repo;
    public static string IssuesUrl => RepoUrl + "/issues";
    const string SetupAsset = "HeMiDock-Setup.exe";

    public sealed record Release(Version Version, string Name, string Page, string SetupUrl);

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

    /// <summary>The version of this dock (1.5.0 of "Beta 1.5.0").</summary>
    public static Version Current
    {
        get
        {
            var v = (System.Reflection.Assembly.GetEntryAssembly() ?? typeof(Updater).Assembly).GetName().Version ?? new Version(0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>Installed by its installer (in the programs of the user, with its uninstall entry): it can update itself.</summary>
    public static bool Installed
    {
        get
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\HeMiDock_is1");
                return k?.GetValue("InstallLocation") is string place
                    && Path.GetFullPath(place).TrimEnd('\\').Equals(Path.GetDirectoryName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    /// <summary>On the thread of the interface: the first check 30 s after the start, then every day (while switched on).</summary>
    public static void Start()
    {
        if (_daily != null) return;
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var first = queue.CreateTimer();
        first.Interval = TimeSpan.FromSeconds(30);
        first.IsRepeating = false;
        first.Tick += (_, _) => { if (DockSettings.Current.CheckUpdates) _ = CheckAsync(quiet: true); };
        first.Start();
        _daily = queue.CreateTimer();
        _daily.Interval = TimeSpan.FromHours(24);
        _daily.Tick += (_, _) => { if (DockSettings.Current.CheckUpdates) _ = CheckAsync(quiet: true); };
        _daily.Start();
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
            string setup = null;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                    if (string.Equals(a.GetProperty("name").GetString(), SetupAsset, StringComparison.OrdinalIgnoreCase))
                        setup = a.GetProperty("browser_download_url").GetString();
            var release = new Release(version, root.TryGetProperty("name", out var n) ? n.GetString() : tag, root.GetProperty("html_url").GetString(), setup);
            bool newer = version > Current && !(quiet && DockSettings.Current.SkippedUpdate == version.ToString());
            Ui.Post(() => { Available = newer ? release : null; Changed?.Invoke(); });
            return release;
        }
        catch (Exception ex) { Log.Write("Update check", ex); return null; }
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
        if (!Installed || release.SetupUrl == null) { Fg.Open(release.Page); return; }
        try
        {
            Host.ShowInfo?.Invoke(L.T("Downloading HeMiDock {0}…", release.Version.ToString(3)));
            string file = Path.Combine(Path.GetTempPath(), $"HeMiDock-Setup-{release.Version.ToString(3)}.exe");
            using (var response = await Http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var to = File.Create(file);
                await response.Content.CopyToAsync(to);
            }
            Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE") { UseShellExecute = true });
            App.Quit();                                                  // the installer replaces the files and starts the dock again
        }
        catch (Exception ex)
        {
            Log.Write("Update install", ex);
            Fg.Open(release.Page);                                      // the download failed: the page instead
        }
    }
}
