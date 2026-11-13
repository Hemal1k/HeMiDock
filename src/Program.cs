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
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }
}
