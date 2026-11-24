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
            StartWatching();                                              // the freeze watch and the events of Windows (they write only in debug mode)
            // once the app has started (docks, tray icons) – a moment later
            Task.Delay(3000).ContinueWith(_ => Write("System", "\n" + SystemInfo()));
            _beat ??= new Timer(_ => Write("Heartbeat", Resources() + $", CPU of the dock {DockCpu():0.0} %, of the PC {SystemCpu():0} %, window events: {WinEvents.TakeCounts()}"));
            _beat.Change(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }
        else _beat?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    // ───────────────────────── events ─────────────────────────
    // Only in debug mode (without it the app records nothing at all): every event goes into debug-log.txt and into a memory of
    // the last ones (a crash and the diagnostic data have what happened right before). The same line
    // again and again is written once with how many times it came; many lines of one kind in a second are marked as a storm
    // (the AppBar messages that made the whole PC stutter were 40 in 0.3 s – easy to miss among the other lines).

    const int RingSize = 3000;
    static readonly string[] Ring = new string[RingSize];
    static int _ringNext, _ringCount;
    static string _lastKey;
    static int _repeats;
    static DateTime _lastAt, _repeatFrom;
    static readonly Dictionary<string, (DateTime From, int Count, bool Told)> Rates = new();
    const int StormCount = 20;

    /// <summary>One event: into the memory of the last ones, and in debug mode into debug-log.txt (started again above 4 MB).</summary>
    public static void Write(string where, string message)
    {
        if (!On) return;                                                  // with debug mode off nothing at all, not even in memory
        try
        {
            var now = DateTime.Now;
            lock (Gate)
            {
                string key = where + "|" + message;
                if (key == _lastKey && (now - _lastAt).TotalSeconds < 2)
                {
                    if (_repeats++ == 0) _repeatFrom = _lastAt;
                    _lastAt = now;
                    Storm(where, message, now);
                    return;
                }
                FlushRepeats();
                _lastKey = key;
                _lastAt = now;
                Store($"{now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] [{where}] {message}");
                Storm(where, message, now);
            }
        }
        catch { }
    }

    /// <summary>"↑ the same ×N" after a line that came again and again.</summary>
    static void FlushRepeats()
    {
        if (_repeats == 0) return;
        Store($"{_lastAt:yyyy-MM-dd HH:mm:ss.fff}   ↑ the same ×{_repeats} more in {(_lastAt - _repeatFrom).TotalMilliseconds:0} ms");
        _repeats = 0;
    }

    /// <summary>Many events of one kind (numbers left out: window handles, percentages) within a second: one warning line.</summary>
    static void Storm(string where, string message, DateTime now)
    {
        var kind = new StringBuilder(where).Append('|');
        foreach (char c in message.Length > 80 ? message[..80] : message) kind.Append(char.IsDigit(c) ? '#' : c);
        string k = kind.ToString();
        if (!Rates.TryGetValue(k, out var r) || (now - r.From).TotalSeconds > 1) r = (now, 0, false);
        r.Count++;
        if (r.Count >= StormCount && !r.Told)
        {
            r.Told = true;
            Store($"{now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] [STORM] {r.Count}× \"{where}: {message}\" within {(now - r.From).TotalMilliseconds:0} ms");
        }
        Rates[k] = r;
        if (Rates.Count > 400) foreach (var old in Rates.Where(p => (now - p.Value.From).TotalSeconds > 5).Select(p => p.Key).ToList()) Rates.Remove(old);
    }

    /// <summary>A finished line: into the memory, in debug mode into the file.</summary>
    static void Store(string line)
    {
        Ring[_ringNext] = line;
        _ringNext = (_ringNext + 1) % RingSize;
        if (_ringCount < RingSize) _ringCount++;
        if (!On) return;
        Directory.CreateDirectory(AppPaths.Logs);
        var file = new FileInfo(LogFile);
        if (file.Exists && file.Length > 4 * 1024 * 1024) File.Move(LogFile, Path.Combine(AppPaths.Logs, "debug-log.old.txt"), true);
        File.AppendAllText(LogFile, line + "\n");
    }

    /// <summary>The last events (oldest first) – for a crash, a freeze and the diagnostic data.</summary>
    public static string Recent(int count = RingSize)
    {
        lock (Gate)
        {
            FlushRepeats();
            int n = Math.Min(count, _ringCount);
            var s = new StringBuilder();
            for (int i = n; i > 0; i--) s.AppendLine(Ring[((_ringNext - i) % RingSize + RingSize) % RingSize]);
            return s.ToString();
        }
    }

    /// <summary>An unhandled error: it and the events before it into Logs\crash-context.txt (also with debug mode off).</summary>
    public static void Crash(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            File.WriteAllText(Path.Combine(AppPaths.Logs, "crash-context.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {ex}\n\nThe events before it:\n{Recent(400)}");
        }
        catch { }
    }

    /// <summary>
    /// Measures a piece of work: <c>using (Diag.Time("Dock placed")) { … }</c> – a line only when it took longer than
    /// <paramref name="limitMs"/> (a slow dock or pop-up shows in the log with what was slow).
    /// </summary>
    public static Timed Time(string what, int limitMs = 50) => new(what, limitMs);

    public readonly struct Timed : IDisposable
    {
        readonly string _what;
        readonly int _limit;
        readonly long _start;
        public Timed(string what, int limit) { _what = what; _limit = limit; _start = Stopwatch.GetTimestamp(); }
        public void Dispose()
        {
            double ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            if (ms > _limit) Write("Slow", $"{_what} took {ms:0} ms");
        }
    }

    // ───────────────────────── freezes and the system ─────────────────────────

    static Thread _watch;

    /// <summary>
    /// Once: a thread that asks the thread of the interface every half second whether it answers – a freeze (the dock, Start or
    /// the control center not reacting) is written with how long it lasted. And the events of Windows that often come before
    /// problems: sleep and wake, lock and unlock, monitors and display settings, the theme.
    /// </summary>
    static void StartWatching()
    {
        if (_watch != null || Ui.Context == null) return;
        _watch = new Thread(WatchUi) { IsBackground = true, Name = "HeMiDock freeze watch", Priority = ThreadPriority.AboveNormal };
        _watch.Start();
        try
        {
            SystemEvents.PowerModeChanged += (_, e) => Write("System", "power: " + e.Mode);
            SystemEvents.SessionSwitch += (_, e) => Write("System", "session: " + e.Reason);
            SystemEvents.DisplaySettingsChanged += (_, _) => Write("System", "display settings changed: " +
                string.Join(", ", Monitors.All().Select(m => $"{m.Device} {m.Bounds.Right - m.Bounds.Left}×{m.Bounds.Bottom - m.Bounds.Top} work {m.Work.Left},{m.Work.Top}–{m.Work.Right},{m.Work.Bottom}")));
            SystemEvents.UserPreferenceChanged += (_, e) => Write("System", "user preference changed: " + e.Category);
            SystemEvents.TimeChanged += (_, _) => Write("System", "time changed");
        }
        catch (Exception ex) { Write("Debug", "system events: " + ex.Message); }
    }

    static void WatchUi()
    {
        using var answered = new ManualResetEventSlim();
        while (true)
        {
            Thread.Sleep(500);
            if (!On) continue;                                            // debug mode off: nothing is watched
            answered.Reset();
            long start = Stopwatch.GetTimestamp();
            try { Ui.Post(() => answered.Set()); } catch { continue; }
            if (answered.Wait(250)) continue;
            // no answer: wait on, telling a long freeze already while it lasts (a hang that never ends is still seen)
            bool told = false;
            while (!answered.Wait(1000))
            {
                double so = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (!told && so > 5000) { told = true; Write("FREEZE", $"the interface has not answered for {so:0} ms (still waiting) · {Resources()}"); }
            }
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Write("FREEZE", $"the interface did not answer for {ms:0} ms · CPU of the PC {SystemCpu():0} %");
        }
    }

    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    static long _sysIdle, _sysTotal;

    /// <summary>How busy the whole PC was since the last call (%): a stutter of the PC vs. of the app alone.</summary>
    static double SystemCpu()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return -1;
        long total = kernel + user;                                       // kernel time includes the idle time
        double busy = total - _sysTotal <= 0 ? 0 : 100.0 * (1 - (double)(idle - _sysIdle) / (total - _sysTotal));
        _sysIdle = idle;
        _sysTotal = total;
        return Math.Clamp(busy, 0, 100);
    }

    /// <summary>A changed setting with its new value (long texts shortened).</summary>
    public static void SettingChanged(string name, object value)
    {
        if (!On) return;
        // the address of the internet calendar often has a private key in it
        string text = name == nameof(DockSettings.CalendarIcs) ? "<hidden>" : value?.ToString() ?? "null";
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

    static TimeSpan _dockCpu;
    static long _dockAt;

    /// <summary>How much of the processor the dock took since the last heartbeat (% of the whole PC).</summary>
    static double DockCpu()
    {
        using var p = Process.GetCurrentProcess();
        var cpu = p.TotalProcessorTime;
        long now = Stopwatch.GetTimestamp();
        double result = _dockAt == 0 ? 0 : (cpu - _dockCpu).TotalMilliseconds / Stopwatch.GetElapsedTime(_dockAt, now).TotalMilliseconds / Environment.ProcessorCount * 100;
        _dockCpu = cpu;
        _dockAt = now;
        return result;
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
                    // the log may be written right now – read through a shared read
                    using var src = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                    Add(archive, prefix + Path.GetFileName(file), src.ReadToEnd());
                }
            }
            Add(archive, "system.txt", SystemInfo());
            Add(archive, "crashes.txt", Crashes());
            Add(archive, "processes.txt", Processes());
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
        w.Write(Anonymize(text, DockSettings.Current?.CalendarIcs, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    }

    /// <summary>
    /// For the diagnostic data: the address of the internet calendar hidden (it often has a private key in it), the user
    /// folder (with the user name) written as %USERPROFILE% – also in the JSON of the settings, where \ is doubled.
    /// </summary>
    internal static string Anonymize(string text, string calendar, string profile)
    {
        if (string.IsNullOrEmpty(text)) return text;
        const System.Text.RegularExpressions.RegexOptions Ci = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        text = System.Text.RegularExpressions.Regex.Replace(text, "(\"CalendarIcs\"\\s*:\\s*)\"(?:[^\"\\\\]|\\\\.)*\"", "$1\"<hidden>\"");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(CalendarIcs = )[^\r\n]*", "$1<hidden>");   // older debug logs
        if (!string.IsNullOrWhiteSpace(calendar)) text = text.Replace(calendar.Trim(), "<hidden>", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(profile))
            foreach (var form in new[] { profile.Replace("\\", "\\\\"), profile, profile.Replace('\\', '/') })
                text = System.Text.RegularExpressions.Regex.Replace(text, System.Text.RegularExpressions.Regex.Escape(form) + @"(?![\w.-])", "%USERPROFILE%", Ci);
        return text;
    }

    /// <summary>
    /// The processes that take the most memory and the most processor time (since they started) – when the PC is slow, whether
    /// it is the dock or something else; also the other shells and tools that change the taskbar (they can fight the dock).
    /// </summary>
    static string Processes()
    {
        var s = new StringBuilder();
        try
        {
            var all = Process.GetProcesses().Select(p =>
            {
                try { return (p.ProcessName, p.Id, Mem: p.WorkingSet64 / 1048576, Cpu: p.TotalProcessorTime.TotalSeconds); }
                catch { return (p.ProcessName, p.Id, Mem: p.WorkingSet64 / 1048576, Cpu: -1.0); }
                finally { p.Dispose(); }
            }).ToList();
            s.AppendLine($"Processes: {all.Count}, CPU of the PC now {SystemCpu():0} %");
            s.AppendLine("\nThe most processor time (s, since each started):");
            foreach (var p in all.OrderByDescending(p => p.Cpu).Take(25)) s.AppendLine($"  {p.Cpu,10:0.0}  {p.ProcessName} ({p.Id})");
            s.AppendLine("\nThe most memory (MB):");
            foreach (var p in all.OrderByDescending(p => p.Mem).Take(25)) s.AppendLine($"  {p.Mem,10}  {p.ProcessName} ({p.Id})");
            string[] shells = { "StartAllBack", "ExplorerPatcher", "Start11", "StartIsBack", "RetroBar", "Cairo", "TranslucentTB", "RoundedTB", "Windhawk", "SmartTaskbar", "TaskbarX", "Stardock", "Fences", "Rainmeter", "DisplayFusion", "AutoHotkey", "PowerToys" };
            var found = all.Select(p => p.ProcessName).Where(n => shells.Any(x => n.Contains(x, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
            s.AppendLine("\nTools that change the taskbar or the desktop: " + (found.Count == 0 ? "none" : string.Join(", ", found)));
        }
        catch (Exception ex) { s.AppendLine("Processes could not be read: " + ex.Message); }
        return s.ToString();
    }

    /// <summary>The last crashes of HeMiDock in the Windows event log (errors the app could not write itself).</summary>
    static string Crashes()
    {
        try
        {
            var psi = new ProcessStartInfo("wevtutil.exe",
                "qe Application /q:\"*[System[(Provider[@Name='.NET Runtime'] or Provider[@Name='Application Error'] or Provider[@Name='Windows Error Reporting'] or Provider[@Name='Application Hang'])]]\" /c:200 /rd:true /f:text")
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
