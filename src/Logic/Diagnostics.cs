using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace HeMiDock;

/// <summary>
/// Debug mode (Settings → About, off by default): what the app does – start, monitors, pop-ups, launched apps, changed
/// settings, errors – goes with exact times into Logs\debug-log.txt in the settings folder, every few minutes with its memory
/// and handles. "Diagnostic data" packs the logs, settings and a description of the system into one zip file for finding
/// problems on another PC. Errors also go into log.txt (see <see cref="Log"/>). With debug mode off no log file is written.
/// </summary>
static class Diag
{
    /// <summary>HeMiDock.exe --measure (the older --timing too): the times of the start and the load of the dock into log.txt.</summary>
    public static readonly bool Measuring = Environment.GetCommandLineArgs().Any(a => a is "--measure" or "--timing");
    static bool Timing => Measuring;
    static readonly List<string> Marks = new();

    /// <summary>
    /// HeMiDock.exe --measure: the milliseconds from the start of the process to each step of the start, written into log.txt
    /// once the dock is drawn (to find what makes the start slow on another PC).
    /// </summary>
    public static void Mark(string step)
    {
        if (!Timing) return;
        try { Marks.Add($"{(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds,6:0} ms  {step}"); } catch { }
        if (step == "dock drawn") Log.Note("Start", "\n" + string.Join("\n", Marks));
    }

    static readonly object Gate = new();
    static Timer _beat;
    static bool _on, _applied;

    static string LogFile => Path.Combine(AppPaths.Logs, "debug-log.txt");

    public static bool On => _applied ? _on : DockSettings.Current?.DebugMode == true;

    /// <summary>What the interface adds to the description of the system (docks, apps, the open pop-up, tray icons).</summary>
    public static Func<string> DescribeUi { get; set; }

    /// <summary>At the start and after every change of the settings: debug mode switched on / off.</summary>
    public static void Apply()
    {
        bool on = DockSettings.Current.DebugMode;
        if (_applied && on == _on) return;
        bool was = _on;
        _applied = true;
        if (!on && was) Write("Debug", "debug mode off");
        _on = on;
        if (on)
        {
            Write("Debug", "debug mode on");
            // once the app has started (docks, tray icons) – a moment later
            Task.Delay(3000).ContinueWith(_ => Write("System", "\n" + SystemInfo()));
            _beat ??= new Timer(_ => Write("Heartbeat", Resources()));
            _beat.Change(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }
        else _beat?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>One line into debug-log.txt (only in debug mode). The file is started again above 4 MB, the previous one kept as .old.</summary>
    public static void Write(string where, string message)
    {
        if (!On) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                var file = new FileInfo(LogFile);
                if (file.Exists && file.Length > 4 * 1024 * 1024) File.Move(LogFile, Path.Combine(AppPaths.Logs, "debug-log.old.txt"), true);
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] [{where}] {message}\n");
            }
        }
        catch { }
    }

    /// <summary>A changed setting with its new value (long texts shortened).</summary>
    public static void SettingChanged(string name, object value)
    {
        if (!On) return;
        string text = value?.ToString() ?? "null";
        Write("Setting", $"{name} = {(text.Length > 120 ? text[..120] + "…" : text)}");
    }

    // ───────────────────────── what is written ─────────────────────────

    [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process, uint flags);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, out int value, uint ini);

    /// <summary>Memory and handles of the app – a leak shows as numbers that keep growing.</summary>
    static string Resources()
    {
        using var p = Process.GetCurrentProcess();
        return $"memory {p.WorkingSet64 / 1048576} MB (private {p.PrivateMemorySize64 / 1048576} MB, managed {GC.GetTotalMemory(false) / 1048576} MB), " +
               $"handles {p.HandleCount}, GDI {GetGuiResources(p.Handle, 0)}, USER {GetGuiResources(p.Handle, 1)}, threads {p.Threads.Count}, " +
               $"running {DateTime.Now - p.StartTime:d\\.hh\\:mm\\:ss}";
    }

    static string Reg(string key, string name) => Registry.GetValue(key, name, null)?.ToString() ?? "?";

    /// <summary>The app, Windows, the monitors and the state of the shell – the start of every debug log and of the diagnostic data.</summary>
    public static string SystemInfo()
    {
        const string nt = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        const string personalize = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        var s = new StringBuilder();
        s.AppendLine($"HeMiDock {AppInfo.Version} (built {AppInfo.BuildDate:yyyy-MM-dd HH:mm}), {typeof(Diag).Assembly.GetName().Version}");
        s.AppendLine($"  exe {Environment.ProcessPath}, args \"{string.Join(" ", Environment.GetCommandLineArgs().Skip(1))}\"");
        s.AppendLine($"  {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}");
        string build = Reg(nt, "CurrentBuild"), product = Reg(nt, "ProductName");
        if (int.TryParse(build, out int b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");   // the registry still says 10
        s.AppendLine($"Windows: {product} {Reg(nt, "DisplayVersion")}, build {build}.{Reg(nt, "UBR")} ({RuntimeInformation.OSArchitecture})");
        bool animations = !SystemParametersInfo(0x1042, 0, out int anim, 0) || anim != 0;   // SPI_GETCLIENTAREAANIMATION
        s.AppendLine($"  language {System.Globalization.CultureInfo.CurrentUICulture.Name}, format {System.Globalization.CultureInfo.CurrentCulture.Name}, " +
                     $"keyboards {InputLanguage.Layouts().Length}, running for {TimeSpan.FromMilliseconds(Environment.TickCount64):d\\.hh\\:mm}");
        s.AppendLine($"  theme {(Theme.IsLight ? "light" : "dark")}, transparency {Reg(personalize, "EnableTransparency")}, animations {(animations ? "on" : "off")}");
        s.AppendLine($"  Explorer running: {Process.GetProcessesByName("explorer").Length > 0}");
        foreach (var m in Monitors.All())
            s.AppendLine($"Monitor {m.Device}{(m.Primary ? " (primary)" : "")}: {m.Bounds.Right - m.Bounds.Left}×{m.Bounds.Bottom - m.Bounds.Top} at {m.Bounds.Left},{m.Bounds.Top}, " +
                         $"work area {m.Work.Left},{m.Work.Top}–{m.Work.Right},{m.Work.Bottom}, scaling {Native.MonitorDpi(m.Handle) * 100:0} %");
        try { if (DescribeUi?.Invoke() is { Length: > 0 } ui) s.AppendLine(ui); } catch { }
        s.AppendLine($"Graphics: {GraphicsChip.Name} (integrated: {GraphicsChip.Integrated}), lighter drawing: {DockSettings.Current.LowGraphics}, frosted glass: {DockSettings.Current.Blur}");
        s.AppendLine("Process: " + Resources());
        return s.ToString();
    }

    // ───────────────────────── diagnostic data ─────────────────────────

    /// <summary>
    /// A zip file on the desktop: the logs and settings of HeMiDock, the description of the system and the crashes of
    /// HeMiDock that Windows recorded (Event Viewer). Returns its path, null when it could not be made.
    /// </summary>
    public static string Export()
    {
        try
        {
            string zip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"HeMiDock-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip");
            Write("Debug", "diagnostic data → " + zip);
            if (File.Exists(zip)) File.Delete(zip);
            using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
            // the settings (folder of the app) and the logs (its Logs folder, kept as Logs\ in the zip)
            foreach (var (dir, prefix) in new[] { (AppPaths.Folder, ""), (AppPaths.Logs, "Logs/") })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is not (".txt" or ".json") || new FileInfo(file).Length > 16 * 1024 * 1024) continue;
                    // the log may be written right now – copied through a shared read
                    using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var dst = archive.CreateEntry(prefix + Path.GetFileName(file)).Open();
                    src.CopyTo(dst);
                }
            }
            Add(archive, "system.txt", SystemInfo());
            Add(archive, "crashes.txt", Crashes());
            return zip;
        }
        catch (Exception ex)
        {
            Log.Write("Diagnostics export", ex);
            return null;
        }
    }

    static void Add(ZipArchive archive, string name, string text)
    {
        using var w = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(true));
        w.Write(text);
    }

    /// <summary>The last crashes of HeMiDock in the Windows event log (errors the app could not write itself).</summary>
    static string Crashes()
    {
        try
        {
            var psi = new ProcessStartInfo("wevtutil.exe",
                "qe Application /q:\"*[System[(Provider[@Name='.NET Runtime'] or Provider[@Name='Application Error'] or Provider[@Name='Windows Error Reporting'])]]\" /c:200 /rd:true /f:text")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8
            };
            using var p = Process.Start(psi);
            string all = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            var ours = all.Split("Event[", StringSplitOptions.RemoveEmptyEntries).Where(e => e.Contains("HeMiDock", StringComparison.OrdinalIgnoreCase)).Take(20).ToList();
            return ours.Count == 0 ? "No crashes of HeMiDock recorded by Windows.\n" : string.Join("\n", ours.Select(e => "Event[" + e));
        }
        catch (Exception ex) { return "Event log could not be read: " + ex.Message + "\n"; }
    }
}
