using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace HeMiDock;

/// <summary>
/// The own start of the app: the watchdog (--watchdog) runs without loading WinUI at all, it only waits for the dock;
/// everything else starts the app of WinUI.
/// </summary>
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // started from a terminal of VS Code (or another Electron app) the app inherits its variables, and every app it
        // starts gets them: with ELECTRON_RUN_AS_NODE, VS Code started from Start ran as Node.js without a window
        foreach (var name in Environment.GetEnvironmentVariables().Keys.OfType<string>().ToList())
            if (name.StartsWith("VSCODE_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable(name, null);
        // --check [file]: the checks of the logic without a window (the build server runs them), nothing of WinUI loaded
        // (--selftest is the other one, of the debug build: the export and import of the settings in the running app)
        if (args.Length >= 1 && args[0] == "--check")
        {
            Environment.ExitCode = LogicCheck.Run(args.Length >= 2 ? args[1] : null);
            return;
        }
        // what the installer and uninstaller start runs here too, before WinUI: on a PC without the Windows App Runtime (a
        // quiet installation that could not add it – the test PCs of winget) Windows asked about the runtime with a window
        // --repair: the start at sign-in and "Pin to Start" of Explorer point to this HeMiDock.exe
        if (args.Contains("--repair"))
        {
            try
            {
                L.Apply(DockSettings.Current.Language);
                if (Autostart.IsOn) Autostart.IsOn = true;
                ExplorerVerb.Apply(DockSettings.Current.ReplaceStartMenu);
            }
            catch (Exception ex) { Log.Write("Repair", ex); }
            return;
        }
        // --exit ends the running dock properly; --uninstall also takes away what it put into Windows (the settings stay)
        bool uninstall = args.Contains("--uninstall");
        if (uninstall || args.Contains("--exit"))
        {
            if (EventWaitHandle.TryOpenExisting("HeMiDock_Exit", out var running)) running.Set();
            if (uninstall)
            {
                try { Autostart.IsOn = false; } catch (Exception ex) { Log.Write("Uninstall", ex); }
                try { ExplorerVerb.Apply(false); } catch (Exception ex) { Log.Write("Uninstall", ex); }
                // the dock gives the taskbar back before the uninstaller removes its files (at most 5 s)
                for (int i = 0; i < 50 && System.Diagnostics.Process.GetProcessesByName("HeMiDock").Any(p => p.Id != Environment.ProcessId); i++) Thread.Sleep(100);
            }
            return;
        }
        if (args.Length >= 2 && args[0] == "--watchdog" && int.TryParse(args[1], out int pid))
        {
            Watchdog.Run(pid);
            return;
        }
        // the start at sign-in comes twice (the task and the Run value, see Autostart): the second one, or one switched off in
        // Task Manager, ends here at once – before anything of WinUI is loaded
        if (args.Contains(Autostart.StartupArg)
            && (Autostart.DisabledByUser || Mutex.TryOpenExisting("HeMiDock_SingleInstance", out _)))
            return;
        // started by the installer after an update: when a dock runs already, nothing (not its settings)
        if (args.Contains("--after-update") && Mutex.TryOpenExisting("HeMiDock_SingleInstance", out _)) return;
#if HEMI_BOOTSTRAP
        // only now the Windows App Runtime (the installed release; the portable one carries it): missing, Windows offers it
        // with a window and the app ends – what the installer starts above never asks
        if (!FindRuntime(out int hr)) { Environment.Exit(hr); return; }
#endif
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }

#if HEMI_BOOTSTRAP
    /// <summary>In its own method: its library is loaded only here, never by what the installer starts.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static bool FindRuntime(out int hr) =>
        Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.TryInitialize(
            Microsoft.WindowsAppSDK.Release.MajorMinor, Microsoft.WindowsAppSDK.Release.VersionTag,
            new Microsoft.Windows.ApplicationModel.DynamicDependency.PackageVersion(Microsoft.WindowsAppSDK.Runtime.Version.UInt64),
            Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.InitializeOptions.OnNoMatch_ShowUI, out hr);
#endif
}
