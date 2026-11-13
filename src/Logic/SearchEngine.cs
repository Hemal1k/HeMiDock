using System.ComponentModel;
using System.Data.OleDb;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HeMiDock;

enum EntryKind { App, Setting, Tool, File, Folder, Recent, Calc, Open, AppFolder, DockSetting }

/// <summary>Entry in the Start menu: app, setting, tool, file, calculator result…</summary>
/// <summary>How often apps were opened from Start and the search (only on this PC) – the search puts them higher.</summary>
static class SearchUsage
{
    static Dictionary<string, int> _counts;
    static string FilePath => AppPaths.Of("app-usage.json");

    static Dictionary<string, int> Counts
    {
        get
        {
            if (_counts != null) return _counts;
            try { _counts = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(FilePath)); } catch { }
            return _counts ??= new();
        }
    }

    public static int Bonus(string key) => Counts.TryGetValue(key, out int n) ? Math.Min(n, 15) * 15 : 0;

    public static void Opened(string key)
    {
        Counts[key] = Counts.GetValueOrDefault(key) + 1;
        try
        {
            Directory.CreateDirectory(AppPaths.Folder);
            File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(Counts));
        }
        catch (Exception ex) { Log.Write("App usage", ex); }
    }
}

/// <summary>Start menu search on this PC: apps, settings and tools, calculator, paths and files (Windows Search index).</summary>
static class SearchEngine
{
    sealed record Def(string Name, string Keywords, string Target, bool Tool = false);

    static readonly Def[] SettingDefs =
    {
        new("Display", "monitor resolution brightness night light scale", "ms-settings:display"),
        new("Sound", "volume speakers microphone audio output input", "ms-settings:sound"),
        new("Notifications", "alerts toast", "ms-settings:notifications"),
        new("Focus", "do not disturb quiet hours", "ms-settings:quiethours"),
        new("Power & battery", "sleep battery saver power mode", "ms-settings:powersleep"),
        new("Storage", "disk space cleanup storage sense", "ms-settings:storagesense"),
        new("Multitasking", "snap windows desktops", "ms-settings:multitasking"),
        new("Clipboard", "history paste", "ms-settings:clipboard"),
        new("Remote Desktop", "rdp remote", "ms-settings:remotedesktop"),
        new("About", "device name specs windows version pc", "ms-settings:about"),
        new("Troubleshoot", "fix problems", "ms-settings:troubleshoot"),
        new("Recovery", "reset this pc", "ms-settings:recovery"),
        new("Activation", "license product key", "ms-settings:activation"),
        new("Bluetooth & devices", "bluetooth pair devices", "ms-settings:bluetooth"),
        new("Printers & scanners", "printer scanner", "ms-settings:printers"),
        new("Mouse", "pointer cursor buttons scroll", "ms-settings:mousetouchpad"),
        new("Touchpad", "gestures", "ms-settings:devices-touchpad"),
        new("Typing", "keyboard autocorrect", "ms-settings:typing"),
        new("AutoPlay", "removable drives", "ms-settings:autoplay"),
        new("USB", "usb devices", "ms-settings:usb"),
        new("Network & internet", "network status connection", "ms-settings:network"),
        new("Wi-Fi", "wireless networks", "ms-settings:network-wifi"),
        new("Ethernet", "wired network lan", "ms-settings:network-ethernet"),
        new("VPN", "virtual private network", "ms-settings:network-vpn"),
        new("Airplane mode", "flight wireless", "ms-settings:network-airplanemode"),
        new("Mobile hotspot", "tethering share internet", "ms-settings:network-mobilehotspot"),
        new("Proxy", "proxy server", "ms-settings:network-proxy"),
        new("Personalization", "theme appearance", "ms-settings:personalization"),
        new("Background", "wallpaper desktop picture", "ms-settings:personalization-background"),
        new("Colors", "accent dark mode light mode transparency", "ms-settings:personalization-colors"),
        new("Themes", "theme", "ms-settings:themes"),
        new("Lock screen", "lock screen picture", "ms-settings:lockscreen"),
        new("Taskbar", "taskbar settings", "ms-settings:taskbar"),
        new("Start settings", "start menu layout", "ms-settings:personalization-start"),
        new("Fonts", "typeface install fonts", "ms-settings:fonts"),
        new("Installed apps", "uninstall programs apps features", "ms-settings:appsfeatures"),
        new("Default apps", "default browser file types", "ms-settings:defaultapps"),
        new("Startup apps", "autostart run at login", "ms-settings:startupapps"),
        new("Optional features", "windows features", "ms-settings:optionalfeatures"),
        new("Accounts", "your info profile user", "ms-settings:yourinfo"),
        new("Sign-in options", "password pin windows hello fingerprint", "ms-settings:signinoptions"),
        new("Email & accounts", "mail accounts", "ms-settings:emailandaccounts"),
        new("Other users", "family users add account", "ms-settings:otherusers"),
        new("Date & time", "clock time zone", "ms-settings:dateandtime"),
        new("Language & region", "language keyboard region format", "ms-settings:regionlanguage"),
        new("Speech", "voice speech recognition", "ms-settings:speech"),
        new("Game Mode", "gaming performance", "ms-settings:gaming-gamemode"),
        new("Accessibility", "ease of access", "ms-settings:easeofaccess"),
        new("Narrator", "screen reader", "ms-settings:easeofaccess-narrator"),
        new("Magnifier", "zoom screen", "ms-settings:easeofaccess-magnifier"),
        new("Text size", "bigger text", "ms-settings:easeofaccess-display"),
        new("Privacy & security", "privacy permissions", "ms-settings:privacy"),
        new("Location", "location privacy gps", "ms-settings:privacy-location"),
        new("Camera privacy", "webcam permissions", "ms-settings:privacy-webcam"),
        new("Microphone privacy", "microphone permissions", "ms-settings:privacy-microphone"),
        new("Windows Update", "updates upgrade check for updates", "ms-settings:windowsupdate"),
        new("Windows Security", "antivirus defender firewall", "windowsdefender:"),

        new("Control Panel", "classic settings", "control.exe", true),
        new("Device Manager", "drivers hardware devmgmt", "devmgmt.msc", true),
        new("Disk Management", "partitions volumes diskmgmt", "diskmgmt.msc", true),
        new("Computer Management", "compmgmt", "compmgmt.msc", true),
        new("Services", "services.msc background services", "services.msc", true),
        new("Event Viewer", "logs eventvwr", "eventvwr.msc", true),
        new("Task Manager", "processes performance taskmgr", "taskmgr.exe", true),
        new("Registry Editor", "regedit registry", "regedit.exe", true),
        new("System Information", "msinfo32 hardware summary", "msinfo32.exe", true),
        new("Resource Monitor", "resmon cpu memory disk network", "resmon.exe", true),
        new("Network Connections", "ncpa adapters", "ncpa.cpl", true),
        new("Programs and Features", "appwiz uninstall", "appwiz.cpl", true),
        new("Power Options", "powercfg power plan", "powercfg.cpl", true),
        new("Environment Variables", "path variables system properties", "SystemPropertiesAdvanced.exe", true),
    };

    static readonly string System32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
    static readonly string ControlPanelExe = System.IO.Path.Combine(System32, "control.exe");

    /// <summary>A part of the Control Panel: its applets (.cpl), the panel itself and the system properties.</summary>
    static bool IsControlPanel(Def d) =>
        d.Target.EndsWith(".cpl", StringComparison.OrdinalIgnoreCase) || d.Target.Equals("control.exe", StringComparison.OrdinalIgnoreCase)
        || d.Target.StartsWith("SystemProperties", StringComparison.OrdinalIgnoreCase);

    /// <summary>The file of a tool of Windows for its icon (a console .msc, a program).</summary>
    static string ToolFile(string target)
    {
        string path = System.IO.Path.Combine(System32, target);
        return File.Exists(path) ? path : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), target);
    }

    static readonly Dictionary<string, string> NormCache = new();

    /// <summary>Lower case without diacritics – "cafe" finds "Café".</summary>
    static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (NormCache.TryGetValue(s, out var c)) return c;
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
        return NormCache[s] = sb.ToString();
    }

    static readonly char[] WordSeparators = { ' ', '-', '_', '.', '(', ')', ':', '&', '/', '\\' };

    static int Score(string nameN, string qN)
    {
        if (nameN == qN) return 1000;
        if (nameN.StartsWith(qN, StringComparison.Ordinal)) return 800;
        var words = nameN.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(qN, StringComparison.Ordinal))) return 600;
        if (nameN.Contains(qN, StringComparison.Ordinal)) return 400;
        if (qN.Length >= 2 && new string(words.Select(w => w[0]).ToArray()).StartsWith(qN, StringComparison.Ordinal)) return 300;   // „vs" → Visual Studio
        if (qN.Length >= 3 && IsSubsequence(nameN, qN)) return 100;
        return qN.Length >= 4 && words.Any(w => Typo(w, qN)) ? 80 : 0;
    }

    /// <summary>A typo in the word or its beginning: one wrong, missing, extra or swapped letter (two in longer words).</summary>
    static bool Typo(string word, string q)
    {
        int allowed = q.Length >= 7 ? 2 : 1;
        if (word.Length < q.Length - allowed) return false;
        string part = word.Length > q.Length ? word[..q.Length] : word;
        var d = new int[part.Length + 1, q.Length + 1];
        for (int i = 0; i <= part.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= q.Length; j++) d[0, j] = j;
        for (int i = 1; i <= part.Length; i++)
            for (int j = 1; j <= q.Length; j++)
            {
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (part[i - 1] == q[j - 1] ? 0 : 1));
                if (i > 1 && j > 1 && part[i - 1] == q[j - 2] && part[i - 2] == q[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[part.Length, q.Length] <= allowed;
    }

    static bool IsSubsequence(string text, string q)
    {
        int j = 0;
        foreach (var ch in text)
            if (j < q.Length && ch == q[j]) j++;
        return j == q.Length;
    }

    /// <summary>Instant results (no waiting): calculator, apps, settings, paths.</summary>
    public static List<StartEntry> Local(string query)
    {
        var res = new List<StartEntry>();
        string t = query.Trim();
        if (t.Length == 0) return res;
        string qN = Norm(t);

        var s = DockSettings.Current;
        // a path, also with variables (%appdata%): first, so Enter opens it in Explorer
        string path = t.Length > 2 && (t.Contains('%') || t.Contains('\\') || t.Contains(':')) ? Environment.ExpandEnvironmentVariables(t.Trim('"')) : null;
        if (path != null && (Directory.Exists(path) || File.Exists(path)))
            res.Add(new StartEntry { Kind = EntryKind.Open, Name = L.T("Open {0}", path), Subtitle = Directory.Exists(path) ? L.T("Folder") : L.T("File"), Key = path, IconKey = path, IconIsFile = true, Group = L.T("Open") });

        if (TryCalc(t, out string calc))
            res.Add(new StartEntry { Kind = EntryKind.Calc, Name = calc, Subtitle = $"{t} = {calc}  ·  {L.T("Enter copies the result")}", Glyph = "\uE8EF", Group = L.T("Calculator") });
        else if (s.SearchConversions && Converter.TryConvert(t, out string converted, out string number, out string expression))
            res.Add(new StartEntry
            {
                Kind = EntryKind.Calc, Name = converted, Key = number, Subtitle = expression + "  ·  " + L.T("Enter copies the result"),
                Glyph = "", Group = L.T("Conversion")
            });

        if (s.SearchApps)
        {
            var apps = AppCatalog.Apps
                // tools of Windows often have an English name even in a translated Windows: the translated one finds them too;
                // the English name of the shortcut and the name of the program ("cleanmgr") find the app as well
                .Select(a => (App: a, Score: Math.Max(Math.Max(Score(Norm(a.Name), qN), Score(Norm(L.T(a.Name)), qN)),
                    OtherNames(a).Select(n => Math.Max(0, Score(Norm(n), qN) - 50)).DefaultIfEmpty(0).Max())))
                .Where(x => x.Score > 0)
                // tools for developers, uninstallers, help and web links go to the end (found, but after the real apps)
                .Select(x => (x.App, Score: IsMinor(x.App) ? Math.Max(1, x.Score - 700) : x.Score))
                .Select(x => (x.App, Score: x.Score + SearchUsage.Bonus(x.App.Key)))   // what is opened often comes first
                .OrderByDescending(x => x.Score).ThenBy(x => x.App.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(8)
                .Select(x => { var e = StartEntry.From(x.App); e.Name = L.T(x.App.Name); e.Group = L.T("Apps"); return e; })
                .ToList();
            res.AddRange(apps);
            // a program of Windows without a shortcut, typed by its name (msconfig, cleanmgr …): it runs like from Win + R
            if (apps.Count == 0 && WindowsProgram(t) is { } program)
                res.Add(new StartEntry
                {
                    Kind = EntryKind.Tool, Name = System.IO.Path.GetFileName(program), Subtitle = L.T("Tool"), Key = program,
                    Group = L.T("Tools"), IconKey = program, IconIsFile = true, Glyph = ""
                });
        }

        if (s.SearchClipboard) res.AddRange(ClipboardHistory.Find(text => Norm(text).Contains(qN, StringComparison.Ordinal)).Select(text => new StartEntry
        {
            Kind = EntryKind.Calc, Name = text.ReplaceLineEndings(" ").Trim(), Key = text, Subtitle = L.T("Enter copies the text"), Glyph = "\uE8C8", Group = L.T("Clipboard history")
        }));

        if (s.SearchWindowsSettings) res.AddRange(SettingDefs
            // matches the English and the translated name (e.g. "Zvuk" and "Sound") and the keywords
            .Select(d => (Def: d, Score: Math.Max(Math.Max(Score(Norm(d.Name), qN), Score(Norm(L.T(d.Name)), qN)),
                                                  Norm(d.Keywords).Contains(qN, StringComparison.Ordinal) ? 200 : 0)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(5)
            // three groups: the settings of Windows, the Control Panel (its applets, with its icon) and the tools of Windows
            // (with their own icons) \u2013 they were all under "Settings" with a console symbol
            .Select(x => x.Def).OrderBy(d => !d.Tool ? 0 : IsControlPanel(d) ? 1 : 2)
            .Select(d =>
            {
                if (!d.Tool) return new StartEntry { Kind = EntryKind.Setting, Name = L.T(d.Name), Subtitle = L.T("Windows settings"), Key = d.Target, Glyph = "\uE713", Group = L.T("Windows settings") };
                bool panel = IsControlPanel(d);
                return new StartEntry
                {
                    Kind = EntryKind.Tool, Name = L.T(d.Name), Subtitle = L.T(panel ? "Control Panel" : "Tool"), Key = d.Target,
                    Group = L.T(panel ? "Control Panel" : "Tools"), IconKey = panel ? ControlPanelExe : ToolFile(d.Target), IconIsFile = true, Glyph = "\uE756"
                };
            }));

        if (s.SearchDockSettings && Host.SearchSettings?.Invoke(t) is { } found) res.AddRange(found);

        // Search stays on this PC: no web results.
        return res;
    }

    static readonly Dictionary<string, bool> MinorCache = new();

    /// <summary>Words of apps hardly anyone looks for: SDK and developer tools, uninstallers, help, readme and web links.</summary>
    static readonly string[] MinorWords =
    {
        "Application Verifier", "Windows Kits", "Windows SDK", "WinDbg", "Debuggable Package", "Developer Command Prompt", "Developer PowerShell",
        "Native Tools Command Prompt", "Cross Tools Command Prompt", "Debugging Tools", "Windows Performance", "Windows App Cert", "App Certification Kit",
        "Uninstall", "Odinstal", "Readme", "Read me", "Release Notes", "Documentation", "Dokumentace", "Help", "Nápověda", "Manual", "Website", "Web site",
        "License", "Licence", "Changelog", "What's New", "Support", "Podpora", "Tools for Unity", "Recovery Media", "Character Map Help",
    };

    static bool IsMinor(AppEntry a)
    {
        if (MinorCache.TryGetValue(a.Key, out bool known)) return known;
        bool minor = MinorWords.Any(w => a.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                || a.Key.Contains(w, StringComparison.OrdinalIgnoreCase) || a.ExePath?.Contains(w, StringComparison.OrdinalIgnoreCase) == true)
            || a.Key.EndsWith(".url", StringComparison.OrdinalIgnoreCase) || a.Key.EndsWith(".chm", StringComparison.OrdinalIgnoreCase)
            || a.Key.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || a.Key.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            || a.Key.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) || a.Key.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
        return MinorCache[a.Key] = minor;
    }

    static readonly Dictionary<string, string[]> OtherNamesCache = new();
    const string AppsFolder = @"shell:AppsFolder\";

    /// <summary>
    /// Other names of an app than the one shown: the name of its shortcut file (English in a translated Windows: "Disk
    /// Cleanup"), the name of its program ("cleanmgr") and the short name of a Store app ("Photos").
    /// </summary>
    static string[] OtherNames(AppEntry a)
    {
        if (OtherNamesCache.TryGetValue(a.Key, out var known)) return known;
        var names = new List<string>();
        if (a.Key.StartsWith(AppsFolder, StringComparison.OrdinalIgnoreCase))
        {
            string rel = a.Key[AppsFolder.Length..];
            if (rel.Contains('\\')) names.Add(System.IO.Path.GetFileNameWithoutExtension(rel));
            else if (rel.Contains('!')) names.Add(rel.Split('!')[0].Split('_')[0].Split('.')[^1]);   // Microsoft.Windows.Photos_8wekyb3d8bbwe!App
        }
        if (a.ExePath != null) names.Add(System.IO.Path.GetFileNameWithoutExtension(a.ExePath));
        return OtherNamesCache[a.Key] = names.Where(n => n.Length > 1 && !n.Equals(a.Name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>A program or console of Windows by its exact name, with or without its extension (cleanmgr, msconfig.exe, gpedit.msc).</summary>
    static string WindowsProgram(string t)
    {
        if (t.Length < 3 || t.Any(ch => !char.IsLetterOrDigit(ch) && ch != '.' && ch != '_' && ch != '-')) return null;
        var names = System.IO.Path.HasExtension(t) ? new[] { t } : new[] { t + ".exe", t + ".msc", t + ".cpl" };
        foreach (var name in names)
        {
            if (!(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".msc", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".cpl", StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var dir in new[] { System32, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
            {
                string path = System.IO.Path.Combine(dir, name);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    static bool TryCalc(string t, out string result)
    {
        result = null;
        if (t.Length < 3 || !Regex.IsMatch(t, @"^[\d\s\.,\+\-\*/\(\)%x×÷]+$")) return false;
        // needs an operator between numbers; a plain "2024-10" or a phone number is not an expression
        bool hasOp = Regex.IsMatch(t, @"\d\s*[\+\*/%x×÷]\s*[\d\(]") || Regex.IsMatch(t, @"\d\s+-\s+\d") || Regex.IsMatch(t, @"\(.*\)");
        if (!hasOp) return false;
        string expr = t.Replace(',', '.').Replace('×', '*').Replace('x', '*').Replace('÷', '/');
        try
        {
            var v = new System.Data.DataTable().Compute(expr, null);
            double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
            if (double.IsNaN(d) || double.IsInfinity(d)) return false;
            result = d.ToString("0.##########", CultureInfo.CurrentCulture);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Files and folders from the Windows Search index (runs in the background; returns an empty list on errors or when indexing is off).</summary>
    public static Task<List<StartEntry>> FilesAsync(string query, CancellationToken ct) =>
        DockSettings.Current.SearchFiles ? Task.Run(() => Files(query.Trim(), ct), ct) : Task.FromResult(new List<StartEntry>());

    static bool _filesFailedLogged;

    static List<StartEntry> Files(string q, CancellationToken ct)
    {
        var list = new List<StartEntry>();
        string safe = q.Replace("'", "''").Replace("%", "").Replace("[", "").Replace("]", "").Replace("_", "");
        if (safe.Length < 2) return list;

        try
        {
            string sql = "SELECT TOP 40 System.ItemNameDisplay, System.ItemPathDisplay FROM SystemIndex " +
                         $"WHERE SCOPE='file:' AND System.ItemNameDisplay LIKE '%{safe}%' ORDER BY System.DateModified DESC";
            using var conn = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            conn.Open();
            using var cmd = new OleDbCommand(sql, conn);
            using var r = cmd.ExecuteReader();
            while (r.Read() && list.Count < 8)
            {
                if (ct.IsCancellationRequested) return list;
                string name = r.IsDBNull(0) ? null : r.GetString(0);
                string path = r.IsDBNull(1) ? null : r.GetString(1);
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) continue;
                if (path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\Start Menu\", StringComparison.OrdinalIgnoreCase)
                    || path.Contains(@"\$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool dir = Directory.Exists(path);
                list.Add(new StartEntry
                {
                    Kind = dir ? EntryKind.Folder : EntryKind.File,
                    Name = name,
                    Subtitle = System.IO.Path.GetDirectoryName(path) ?? path,
                    Key = path,
                    IconKey = path,
                    IconIsFile = true,
                    Group = dir ? L.T("Folders") : L.T("Files")
                });
            }
        }
        catch (Exception ex)
        {
            if (!_filesFailedLogged) { _filesFailedLogged = true; Log.Write("SearchEngine.Files (Windows Search)", ex); }
        }
        return list;
    }
}

