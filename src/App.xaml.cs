using Microsoft.UI.Xaml;

namespace HeMiDock;

/// <summary>
/// HeMiDock: one instance, the taskbar of Windows replaced by the docks and the top bar. Switches:
///  --open start|search|control|calendar|settings[:page]  opens it (in the running dock too; a start without switches = settings),
///  --exit  ends the running dock, --workspace "name" / --pin-start "file"  (shortcuts of workspaces, "Pin to Start" of Explorer),
///  --measure  the times of the start and the load every 10 s into log.txt.
/// Debug only: --selftest, --cycle, --simulate …; --watchdog is the guard process the app starts itself.
/// </summary>
public partial class App : Application
{
    const string ExitSignal = "HeMiDock_Exit";
    static Mutex _single;
    bool _cleanedUp;

    /// <summary>The dock on the main monitor (keys, notifications and the logic use it).</summary>
    internal static DockWindow Dock { get; private set; }

    /// <summary>The PC has a Bluetooth radio (the settings show the Bluetooth options only then).</summary>
    internal static bool HasBluetooth { get; private set; }
    /// <summary>The PC has a mobile modem (SIM / eSIM): the control center offers its mobile data only then.</summary>
    internal static bool HasCellular { get; private set; }

    public App()
    {
        InitializeComponent();
        // menus, tooltips and dialogs take the theme of the app, which WinUI takes from Windows: a dark app on a light Windows
        // had light menus with dark text in dark windows; the chosen theme of the app is set before any window (it can be
        // set only now – a change later applies to them after a restart, the windows themselves follow at once)
        try
        {
            var theme = DockSettings.Current.ThemeMode;
            if (theme == 1) RequestedTheme = ApplicationTheme.Light;
            else if (theme == 2) RequestedTheme = ApplicationTheme.Dark;
        }
        catch (Exception ex) { Log.Write("App theme", ex); }
        UnhandledException += (_, e) => { Log.Note("Unhandled", e.Exception?.ToString()); e.Handled = true; };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Diag.Mark("launched");
        // HeMiDock.exe --exit ends the running one properly (the taskbar of Windows and the tray icons come back)
        // --uninstall (the uninstaller): the dock ends, and what it put into Windows goes – the start at sign-in (task and Run
        // value) and "Pin to Start" of Explorer; the settings in %AppData% stay
        bool uninstall = Environment.GetCommandLineArgs().Contains("--uninstall");
        if (uninstall || Environment.GetCommandLineArgs().Contains("--exit"))
        {
            if (EventWaitHandle.TryOpenExisting(ExitSignal, out var running)) running.Set();
            if (uninstall)
            {
                try { Autostart.IsOn = false; } catch (Exception ex) { Log.Write("Uninstall", ex); }
                try { ExplorerVerb.Apply(false); } catch (Exception ex) { Log.Write("Uninstall", ex); }
                // the dock gives the taskbar back before the uninstaller removes its files (at most 5 s)
                for (int i = 0; i < 50 && System.Diagnostics.Process.GetProcessesByName("HeMiDock").Any(p => p.Id != Environment.ProcessId); i++) Thread.Sleep(100);
            }
            Exit();
            return;
        }
        var cmd = Environment.GetCommandLineArgs();
        // HeMiDock.exe --workspace "name" (a workspace pinned to the dock), --pin-start "file" (Explorer: "Pin to Start")
        int workspace = Array.IndexOf(cmd, "--workspace"), pinAt = Array.IndexOf(cmd, "--pin-start");
        if (workspace >= 0 && workspace + 1 < cmd.Length) WindowManager.Request(cmd[workspace + 1]);
        string pinPath = pinAt >= 0 && pinAt + 1 < cmd.Length ? cmd[pinAt + 1] : null;
        if (pinPath != null) ExplorerVerb.Request(pinPath);
        string open = OpenArg(cmd);
        _single = new Mutex(true, "HeMiDock_SingleInstance", out bool first);
        if (!first && cmd.Contains(Autostart.StartupArg)) { Exit(); return; }   // the other start at sign-in came at the same moment
        if (!first)
        {
            // the running one does it: the workspace, the pin, otherwise what --open asks for (nothing: its settings)
            string signal = workspace >= 0 ? WorkspaceSignal : pinPath != null ? PinSignal : OpenSignal;
            if (signal == OpenSignal)
                try { Directory.CreateDirectory(AppPaths.Folder); File.WriteAllText(OpenRequestFile, open ?? "settings"); } catch (Exception ex) { Log.Write("Open request", ex); }
            if (EventWaitHandle.TryOpenExisting(signal, out var running)) running.Set();
            Exit();
            return;
        }
        var exit = new EventWaitHandle(false, EventResetMode.AutoReset, ExitSignal);
        new Thread(() => { exit.WaitOne(); Ui.Post(Quit); }) { IsBackground = true }.Start();
        Ui.Context = SynchronizationContext.Current;                // the logic hands its work back to this thread
        Listen(OpenSignal, TakeOpenRequest);
        Listen(PinSignal, ExplorerVerb.TakeRequests);
        Listen(WorkspaceSignal, WindowManager.TakeRequest);
        L.Apply(DockSettings.Current.Language);
        Theme.Refresh();
#if DEBUG
        if (cmd.Contains("--selftest")) { SelfTest.Run(); _single.Dispose(); Exit(); return; }   // see SelfTest (Debug only)
#endif
        Diag.DescribeUi = () => $"Docks: {Docks.Count}, top bar: {TopBar != null}, open pop-up: {PopupWindow.Current?.GetType().Name ?? "none"}, shell: {ShellHost.Running}"
            + (ShellHost.Running ? "\nTray icons: " + string.Join(", ", ShellHost.All().Select(i => $"{i.Title?.Split('\n')[0]} [{i.Id}]")) : "");
        Diag.Apply();                                                 // debug mode: the log starts with a description of the system
        Diag.Write("App", "start " + string.Join(" ", Environment.GetCommandLineArgs().Skip(1)));

        Diag.Mark("language, theme, debug log");
        // started at sign-in by its task, the dock can be here before Explorer. Explorer that finds a taskbar window already
        // there (the one of the dock for the tray icons) takes another shell for running and does not start as the shell:
        // no Start of Windows, no Settings, not even after the dock was closed. So the shell part waits for the taskbar of
        // Explorer (at most 90 s – no Explorer at all: the dock goes on).
        bool beforeExplorer = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null) == IntPtr.Zero;
        if (beforeExplorer)
        {
            var until = DateTime.UtcNow.AddSeconds(90);
            while (Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null) == IntPtr.Zero && DateTime.UtcNow < until) Thread.Sleep(200);
            Thread.Sleep(500);                                            // Explorer registers itself as the shell right after
            Diag.Mark("waited for Explorer");
        }
        try { ShellHost.Start(hideTaskbar: true); }
        catch (Exception ex) { Log.Write("Shell", ex); }
        Diag.Mark("shell (tray, windows)");

        HostCommands.Connect();
        PinTransfer.Connect();
        Watchdog.Start();
        // small upkeep after the dock is drawn – the dock comes up sooner after sign-in
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            Autostart.TakeOver();                                     // the start at sign-in of the betas before 1.0
            SettingsBackup.AfterUpdate();
            ExplorerVerb.Apply(DockSettings.Current.ReplaceStartMenu); // "Pin to Start" in Explorer points to this HeMiDock.exe
            // the apps of Start load in the background once the dock is drawn: right after switching on the PC, reading all
            // apps and their icons took the disk while the dock was starting
            AppCatalog.Preload();
        });
        // right after sign-in Windows may give a wrong icon for a while (File Explorer came as a plain window picture) and
        // the dock kept it until restarted: the icons are read again once, a minute later
        // started at sign-in before Explorer (the task of the start at sign-in): the acrylic of Windows was not ready yet and the
        // dock stayed without transparency – made again 3 s after Explorer has drawn its desktop, and once more with the icons
        if (beforeExplorer)
        {
            var wait = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            int waited = 0, seen = -1;
            wait.Tick += (_, _) =>
            {
                waited++;
                if (seen < 0 && Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null) != IntPtr.Zero) seen = waited;
                if (seen >= 0 && waited - seen >= 3) { wait.Stop(); ThemeWatch.RenewGlass(); }
                else if (waited > 120) wait.Stop();
            };
            wait.Start();
        }
        var refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        refresh.Tick += (_, _) =>
        {
            refresh.Stop();
            AppShell.ForgetIcons(); Pictures.Forget(); foreach (var d in Docks) d.RedrawIcons();
            if (beforeExplorer) ThemeWatch.RenewGlass();
        };
        refresh.Start();
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => { Watchdog.MarkClean(); Ui.Post(Cleanup); };
        ThemeWatch.Start();
        Host.SearchSettings = SettingsWindow.Find;
        _ = FindBluetoothAsync();
        KeepAwake.Restore();
        Diag.Mark("watchdog, catalog, theme watch");
        StartServices();
        Diag.Mark("services");
        // on the thread of the interface: the hooks of the zones and the notifications get their events only on a thread
        // with a message loop – a setting changed in the background (a schedule, a reading) started them where none came
        DockSettings.Current.PropertyChanged += (_, _) =>
        {
            if (SynchronizationContext.Current == Ui.Context) { StartServices(); Diag.Apply(); }
            else Ui.Post(() => { StartServices(); Diag.Apply(); });
        };

        _measureArg = Diag.Measuring;
        WindowManager.TakeRequest();
        ExplorerVerb.TakeRequests();
        if (!DockSettings.Current.SetupDone)
        {
            // the first start: the guide comes before the dock
            DockSettings.Current.StartAtSignIn = true;                 // on unless the user turns it off in the guide
            SetupWindow.Open(() => StartDocks(cmd, open));
            return;
        }
        StartDocks(cmd, open);
    }

    /// <summary>
    /// A moment after the start, one after another: the pop-ups are built ahead (hidden), so the first opening of the control
    /// center, the calendar, Start and the quick search is quick.
    /// </summary>
    static async void WarmUp()
    {
        await Task.Delay(4000);
        foreach (var prepare in new Action[]
        {
            () => PopupWindow.Prepare("control", () => new ControlCenterWindow()),
            () => PopupWindow.Prepare("calendar", () => new CalendarWindow()),
            () => PopupWindow.Prepare("start", () => new StartWindow()),
            SpotlightWindow.Prepare,
            RecentForApp.Refresh                                         // recent files of the apps for their menus
        })
        {
            if (_quitting) return;
            prepare();
            await Task.Delay(300);                                       // the dock stays smooth meanwhile
        }
    }

    const string OpenSignal = "HeMiDock_Open", PinSignal = "HeMiDock_Pin", WorkspaceSignal = "HeMiDock_Workspace";
    static string OpenRequestFile => AppPaths.Of("open-request.txt");

    /// <summary>What --open asks for; the older --search and --settings[=page] are still understood.</summary>
    static string OpenArg(string[] cmd)
    {
        int at = Array.IndexOf(cmd, "--open");
        if (at >= 0) return at + 1 < cmd.Length ? cmd[at + 1] : "settings";
        if (cmd.Contains("--search")) return "search";
        if (cmd.FirstOrDefault(a => a.StartsWith("--settings")) is { } s) return s.StartsWith("--settings=") ? "settings:" + s["--settings=".Length..] : "settings";
        return null;
    }

    /// <summary>A second HeMiDock.exe asked for something (its file says what).</summary>
    static void TakeOpenRequest()
    {
        string what = "settings";
        try { if (File.Exists(OpenRequestFile)) { what = File.ReadAllText(OpenRequestFile).Trim(); File.Delete(OpenRequestFile); } }
        catch (Exception ex) { Log.Write("Open request", ex); }
        Open(what);
    }

    /// <summary>Opens start, search (the quick search), control, calendar or settings (settings:keys = that page).</summary>
    static void Open(string what)
    {
        string[] parts = (what ?? "").Split(':', 2);
        switch (parts[0].ToLowerInvariant())
        {
            case "start": Dock?.ToggleStart(false); break;
            case "search": case "spotlight": SpotlightWindow.Toggle(); break;
            case "control": Dock?.ToggleControlCenter(); break;
            case "calendar": Dock?.ToggleCalendar(); break;
            default: SettingsWindow.Open(parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null); break;
        }
    }

    /// <summary>A signal of a second HeMiDock.exe, done on the thread of the interface each time it comes.</summary>
    static void Listen(string name, Action action)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        new Thread(() => { while (signal.WaitOne()) Ui.Post(action); }) { IsBackground = true }.Start();
    }

    void StartDocks(string[] cmd, string open)
    {
        ArrangeDocks();
        Diag.Mark("docks created");
        ArrangeTopBar();
        Diag.Mark("top bar");
        if (open != null) OpenLater(open);
        WarmUp();
        // monitors connected / disconnected, the monitor or the "all monitors" setting changed: the docks follow
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Ui.Post(ArrangeDocks);
        DockSettings.Current.PropertyChanged += (_, _) =>
        {
            var s = DockSettings.Current;
            if ((s.DockAllMonitors, s.DockMonitor, s.StatusMonitor) != _docksFor) ArrangeDocks();
            ArrangeTopBar();
        };
#if DEBUG
        if (cmd.Contains("--cycle")) MemoryCycle();
#endif
    }

    /// <summary>--open at the start: once the dock is drawn (a pop-up needs its place on the screen).</summary>
    static async void OpenLater(string what)
    {
        await Task.Delay(what.StartsWith("settings", StringComparison.OrdinalIgnoreCase) ? 0 : 1500);   // the settings wait by themselves (WhenReady)
        Open(what);
    }

    // ───── ready: the windows of the dock are drawn and joined to their acrylic ─────
    static bool _ready;
    static readonly List<Action> _whenReady = new();
    internal static bool IsReady => _ready;

    /// <summary>
    /// The main dock has drawn its first frame: half a second later other windows may open. The settings opened in the first
    /// moments after the start (sooner than about one second, on a slower notebook for longer) crashed the app inside WinUI –
    /// the windows of the dock were still being joined to their acrylic then.
    /// </summary>
    internal static void DockDrawn()
    {
        if (_ready) return;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) => { t.Stop(); BecomeReady(); };
        t.Start();
    }

    static void BecomeReady()
    {
        if (_ready) return;
        _ready = true;
        foreach (var a in _whenReady.ToList()) try { a(); } catch (Exception ex) { Log.Write("When ready", ex); }
        _whenReady.Clear();
    }

    /// <summary>Does it now when the dock is ready, otherwise as soon as it is (at most 10 s after this call: no dock at all).</summary>
    internal static void WhenReady(Action action)
    {
        if (_ready) { action(); return; }
        _whenReady.Add(action);
        if (_whenReady.Count == 1)
        {
            var fallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            fallback.Tick += (_, _) => { fallback.Stop(); BecomeReady(); };
            fallback.Start();
        }
    }

    static bool _measureArg, _quitting;
    static (bool, int, int) _docksFor;
    static readonly List<DockWindow> Docks = new();

    /// <summary>The dock or bar whose button opened the pop-up last (Start, control center, calendar open by it).</summary>
    internal static IPanelWindow Anchor { get; set; }

    /// <summary>The top bar while it is switched on.</summary>
    internal static TopBarWindow TopBar { get; private set; }

    /// <summary>The top bar follows its setting (and the monitor of the dock).</summary>
    static void ArrangeTopBar()
    {
        if (_quitting) return;
        bool on = DockSettings.Current.TopBarOn;
        if (on == (TopBar != null)) return;
        if (on)
        {
            TopBar = new TopBarWindow();
            TopBar.Activate();
            return;
        }
        var bar = TopBar;
        TopBar = null;
        if (Anchor == bar) Anchor = null;
        bar.Close();
    }

    /// <summary>
    /// One dock on the chosen monitor, or one on each monitor (the main one has the notification area and the status icons).
    /// Docks of monitors that are gone are closed, new monitors get one.
    /// </summary>
    static void ArrangeDocks()
    {
        if (_quitting) return;
        var s = DockSettings.Current;
        _docksFor = (s.DockAllMonitors, s.DockMonitor, s.StatusMonitor);
        var all = Monitors.All();
        var main = Monitors.Dock();
        var wanted = s.DockAllMonitors || s.StatusMonitor == 1 ? all : new List<MonInfo> { main };
        // the status icons on another monitor than the dock: that monitor gets a dock for them
        if (s.StatusMonitor >= 2 && s.StatusMonitor - 2 < all.Count && wanted.All(m => m.Device != all[s.StatusMonitor - 2].Device)) wanted.Add(all[s.StatusMonitor - 2]);
        var gone = Docks.Where(d => !wanted.Any(m => m.Device == d.Monitor.Device) || (d == Dock) != (d.Monitor.Device == main.Device)).ToList();
        Docks.RemoveAll(gone.Contains);
        // the new ones first: the app ends when its last window closes
        foreach (var m in wanted.Where(m => Docks.All(d => d.Monitor.Device != m.Device)))
        {
            bool isMain = m.Device == main.Device;
            var dock = new DockWindow(m, isMain, measure: _measureArg && isMain);
            Docks.Add(dock);
            if (isMain) Dock = dock;
            dock.Activate();
        }
        foreach (var d in gone)
        {
            if (Anchor == d) Anchor = null;
            if (Dock == d) Dock = null;
            d.Close();
        }
        Dock ??= Docks.FirstOrDefault();
    }

    /// <summary>What follows the settings: keys, notifications, window manager, rules, weather, clipboard, mail.</summary>
    static Microsoft.UI.Xaml.DispatcherTimer _saveLater;

    /// <summary>
    /// The settings are written a moment after a change (they were written only when the settings window closed or the
    /// app ended, so a change could be lost).
    /// </summary>
    static void ScheduleSave()
    {
        if (_saveLater == null)
        {
            _saveLater = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _saveLater.Tick += (_, _) => { _saveLater.Stop(); DockSettings.Current.Save(); };
        }
        _saveLater.Stop();
        _saveLater.Start();
    }

    static void StartServices()
    {
        var s = DockSettings.Current;
        // each on its own: one that fails must not leave the others off (the zones stayed off after another one failed)
        static void Run(string name, Action start)
        {
            try { start(); }
            catch (Exception ex) { Log.Write("Services " + name, ex); }
        }
        Run("keys", () =>
        {
            KeyboardHook.ReplaceStart = s.ReplaceStartMenu;
            KeyboardHook.AppKeys = s.AppShortcuts;
            KeyboardHook.StartKey = s.KeyStart;
            KeyboardHook.SearchKey = s.KeySearch;
            KeyboardHook.PowerKey = s.KeyPowerMenu;
            KeyboardHook.ControlKey = s.KeyControl;
            KeyboardHook.Shortcuts = Hotkeys.Map();
            if (s.ReplaceStartMenu || s.AppShortcuts || s.KeyControl || KeyboardHook.Shortcuts.Count > 0) KeyboardHook.Start(); else KeyboardHook.Stop();
        });
        Run("notifications", () => ToastMover.Apply(s.ToastsMoved));
        Run("outlines", () => WindowOutlines.Apply(!s.DockOutline));
        Run("windows", WindowManager.Apply);
        Run("window places", WindowPlaces.Start);                         // windows Windows moved only for the dock go back
        Run("updates", Updater.Start);                                    // new versions on GitHub (once; the switch is asked each time)
        Run("rules", AppRules.Apply);
        Run("weather", Weather.Apply);
        Run("clipboard", ClipboardHistory.Apply);
        Run("mail", UnreadMail.Start);
        Run("read mail", ReadMailNotifications.Start);
        Run("theme", ThemeSchedule.Apply);
        Run("media", () => BarMedia.Watch(true));                         // what is playing, for the control center
        Run("save", ScheduleSave);
    }

#if DEBUG
    /// <summary>Debug build only: opens and closes the pop-ups again and again and logs the memory, to find leaks without clicking.</summary>
    static async void MemoryCycle()
    {
        await Task.Delay(4000);
        for (int round = 1; round <= 24; round++)
        {
            foreach (var open in new Action[] { () => Dock.ToggleStart(false), Dock.ToggleControlCenter, Dock.ToggleCalendar })
            {
                open();
                await Task.Delay(1200);
                PopupWindow.Current?.CloseNow();
                await Task.Delay(400);
            }
            if (round is 1 or 8 or 16 or 24)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                Log.Note("Cycle", $"round {round}: {p.PrivateMemorySize64 / 1048576} MB private, {p.WorkingSet64 / 1048576} MB working set, managed {GC.GetTotalMemory(false) / 1048576} MB, CPU {p.TotalProcessorTime.TotalSeconds:0.0} s");
            }
        }
    }
#endif

    static async Task FindBluetoothAsync()
    {
        try { HasBluetooth = await RadioSwitch.FindAsync(Windows.Devices.Radios.RadioKind.Bluetooth) != null; } catch { }
        try { HasCellular = await RadioSwitch.FindAsync(Windows.Devices.Radios.RadioKind.MobileBroadband) != null; } catch { }
    }

    internal void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        Watchdog.MarkClean();
        try { KeyboardHook.Stop(); } catch { }
        try { WindowManager.RefreshMarks(end: true); WindowOutlines.Apply(false); } catch { }
        try { ToastMover.Apply(false); } catch { }
        try { DockSettings.Current.Save(); } catch { }
        ShellHost.Stop();                                              // the taskbar of Windows comes back, the apps give their icons to Explorer
    }

    internal static void Quit()
    {
        _quitting = true;
        foreach (var d in Docks.ToList()) try { d.Close(); } catch { }   // their strips of the screen are given back
        Docks.Clear();
        try { TopBar?.Close(); } catch { }
        TopBar = null;
        ((App)Current).Cleanup();
        // the taskbar of Windows is back by now: should something still hold the process (it once stayed without any window),
        // it ends a moment later anyway
        new Thread(() => { Thread.Sleep(3000); Environment.Exit(0); }) { IsBackground = true }.Start();
        Current.Exit();
    }
}
