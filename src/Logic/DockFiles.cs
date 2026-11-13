using System.Runtime.InteropServices;
using System.Text;

namespace HeMiDock;

/// <summary>
/// Recent files of an app for its right-click menu (like the jump list of the taskbar): the newest files of the Windows
/// "Recent" folder that open with this app by default.
/// </summary>
static class RecentForApp
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int AssocQueryString(int flags, int str, string assoc, string extra, StringBuilder output, ref uint size);

    static readonly Dictionary<string, string> AppOfExtension = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The program that opens files of this type (full path), null when unknown.</summary>
    static string AppFor(string extension)
    {
        lock (AppOfExtension)
        {
            if (AppOfExtension.TryGetValue(extension, out var cached)) return cached;
            string exe = null;
            try
            {
                uint size = 1024;
                var sb = new StringBuilder((int)size);
                if (AssocQueryString(0, 2 /* ASSOCSTR_EXECUTABLE */, extension, "open", sb, ref size) == 0) exe = sb.ToString();
            }
            catch { }
            return AppOfExtension[extension] = exe;
        }
    }

    // the newest recent files with the program that opens them – read in the background (resolving shortcuts is slow),
    // so the menu of an icon opens at once
    static volatile List<(string File, string App)> _recent = new();
    static long _readAt;
    static int _reading;

    /// <summary>Reads the recent files again in the background (when the last reading is older than a few seconds).</summary>
    public static void Refresh()
    {
        if (Environment.TickCount64 - _readAt < 15000 || Interlocked.Exchange(ref _reading, 1) == 1) return;
        var t = new Thread(() =>
        {
            var list = new List<(string, string)>();
            try
            {
                var dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                foreach (var lnk in new DirectoryInfo(dir).GetFiles("*.lnk").OrderByDescending(f => f.LastWriteTime).Take(80))
                {
                    string target = AppShell.ResolveShortcut(lnk.FullName).target;
                    if (target == null || !File.Exists(target)) continue;
                    string ext = Path.GetExtension(target);
                    if (ext.Length > 0 && AppFor(ext) is { } app) list.Add((target, app));
                }
            }
            catch (Exception ex) { Log.Write("RecentForApp", ex); }
            _recent = list;
            _readAt = Environment.TickCount64;
            _reading = 0;
        }) { IsBackground = true, Name = "RecentForApp" };
        t.SetApartmentState(ApartmentState.STA);                     // shortcuts are read through COM
        t.Start();
    }

    /// <summary>The newest files that open with this program (from the last reading; a new one starts in the background).</summary>
    public static List<string> Files(string appExe, int count = 8)
    {
        Refresh();
        if (string.IsNullOrEmpty(appExe)) return new();
        return _recent.Where(r => string.Equals(r.App, appExe, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.File).Distinct(StringComparer.OrdinalIgnoreCase).Take(count).ToList();
    }
}

/// <summary>A web page pinned to the dock: its own shortcut with the icon of the site when it has one, not the icon of the browser.</summary>
static class WebPins
{
    static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>A web address from dropped text, null when it is none.</summary>
    public static string UrlOf(string text)
    {
        text = text?.TrimEnd('\0').Trim();
        return Uri.TryCreate(text, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") ? u.AbsoluteUri : null;
    }

    /// <summary>Pins the page: a .url file in the settings folder, the icon of the site comes a moment later.</summary>
    public static async void Pin(string url)
    {
        try
        {
            var uri = new Uri(url);
            string host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            string folder = AppPaths.Of("WebPins");
            Directory.CreateDirectory(folder);
            string name = string.Concat(host.Split(Path.GetInvalidFileNameChars()));
            string file = Path.Combine(folder, name + ".url");
            for (int i = 2; File.Exists(file); i++) file = Path.Combine(folder, $"{name} {i}.url");
            File.WriteAllText(file, "[InternetShortcut]\r\nURL=" + url + "\r\n");
            var pins = DockState.Load();
            pins.Add(new PinnedApp { Path = file, Name = host });
            DockState.Save(pins);
            if (await SiteIcon(uri, Path.ChangeExtension(file, null)) is { } icon)
            {
                pins = DockState.Load();
                if (pins.FirstOrDefault(p => string.Equals(p.Path, file, StringComparison.OrdinalIgnoreCase)) is { } pin) { pin.IconPath = icon; DockState.Save(pins); }
            }
        }
        catch (Exception ex) { Log.Write("Web pin", ex); }
    }

    /// <summary>The icon named by the page, otherwise /favicon.ico; saved next to the shortcut.</summary>
    static async Task<string> SiteIcon(Uri page, string fileBase)
    {
        var tries = new List<Uri>();
        try
        {
            string html = await Http.GetStringAsync(page);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, "<link[^>]+rel=[\"'][^\"']*icon[^\"']*[\"'][^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var href = System.Text.RegularExpressions.Regex.Match(m.Value, "href=[\"']([^\"']+)[\"']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (href.Success && !href.Groups[1].Value.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(page, System.Net.WebUtility.HtmlDecode(href.Groups[1].Value), out var u)) tries.Add(u);
            }
        }
        catch { }
        tries.Add(new Uri(page, "/favicon.ico"));
        foreach (var u in tries)
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(u);
                if (bytes.Length < 64) continue;
                bool png = bytes[0] == 0x89 && bytes[1] == 0x50, ico = bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1;
                if (!png && !ico) continue;                             // an error page instead of a picture
                string file = fileBase + (png ? ".png" : ".ico");
                File.WriteAllBytes(file, bytes);
                return file;
            }
            catch { }
        }
        return null;
    }
}
