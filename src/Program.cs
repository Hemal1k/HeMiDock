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
        // quiet installation that could not add it) Windows asked about the runtime with a window
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
        // started again by the dock itself (another light / dark theme of the app): the old one ends first (at most 10 s)
        int waitAt = Array.IndexOf(args, "--wait-pid");
        if (waitAt >= 0 && waitAt + 1 < args.Length && int.TryParse(args[waitAt + 1], out int old))
            try { System.Diagnostics.Process.GetProcessById(old).WaitForExit(10000); } catch { }
        // the start at sign-in comes twice (the task and the Run value, see Autostart): the second one, or one switched off in
        // Task Manager, ends here at once – before anything of WinUI is loaded
        if (args.Contains(Autostart.StartupArg)
            && (Autostart.DisabledByUser || Mutex.TryOpenExisting("HeMiDock_SingleInstance", out _)))
            return;
        // started by the installer after an update: when a dock runs already, nothing (not its settings)
        if (args.Contains("--after-update") && Mutex.TryOpenExisting("HeMiDock_SingleInstance", out _)) return;
#if HEMI_BOOTSTRAP
        // only now the Windows App Runtime (the installed release; the portable one carries it) – what the installer starts
        // above never asks. Missing or too old: HeMiDock offers to install it from Microsoft itself and goes on
        if (!FindRuntime(out int hr) && !(OfferRuntime() && FindRuntime(out hr))) { Environment.Exit(hr); return; }
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
            Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.InitializeOptions.None, out hr);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);

    /// <summary>The Windows App Runtime is missing or too old: asked, then its installer from Microsoft runs quietly (it needs
    /// no administrator rights – without them it is installed for this user). True when it ran.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static bool OfferRuntime()
    {
        const ushort major = Microsoft.WindowsAppSDK.Runtime.Version.Major, minor = Microsoft.WindowsAppSDK.Runtime.Version.Minor;
        string need = $"{major}.{minor}.{Microsoft.WindowsAppSDK.Runtime.Version.Build}";
        // before WinUI and the settings: the language of Windows, as L.Apply does (Slovak reads the Czech texts too)
        bool cs = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "cs" or "sk";
        string ask = cs
            ? $"HeMiDock potřebuje Windows App Runtime {need} nebo novější od Microsoftu a na tomto PC chybí (nebo je starší).\n\nStáhnout a nainstalovat ho teď? Trvá to chvilku, pak se HeMiDock spustí."
            : $"HeMiDock needs the Windows App Runtime {need} or newer from Microsoft, and this PC lacks it (or has an older one).\n\nDownload and install it now? It takes a moment, then HeMiDock starts.";
        // MB_YESNO | MB_ICONQUESTION | MB_SETFOREGROUND: Yes = 6
        if (MessageBoxW(0, ask, "HeMiDock", 0x4 | 0x20 | 0x10000) != 6) return false;
        string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        string url = $"https://aka.ms/windowsappsdk/{major}.{minor}/latest/windowsappruntimeinstall-{arch}.exe";
        // a new folder of its own in %TEMP%: no file put there before can be started instead
        string dir = Path.Combine(Path.GetTempPath(), "HeMiDock-runtime-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, $"windowsappruntimeinstall-{arch}.exe");
        try
        {
            Directory.CreateDirectory(dir);
            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var s = http.GetStreamAsync(url).GetAwaiter().GetResult())
            using (var f = File.Create(file))
                s.CopyTo(f);
            // started only with a valid signature of Microsoft (a changed download, a wrong redirect)
            if (!Authenticode.IsMicrosoft(file))
                throw new InvalidOperationException(cs ? "stažený soubor nemá platný podpis od Microsoftu." : "the downloaded file has no valid signature of Microsoft.");
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, "--quiet") { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit();
            return true;
        }
        catch (Exception e)
        {
            string fail = cs
                ? $"Windows App Runtime se nepodařilo nainstalovat: {e.Message}\n\nStáhněte ho ručně: https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads"
                : $"The Windows App Runtime could not be installed: {e.Message}\n\nGet it by hand: https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads";
            MessageBoxW(0, fail, "HeMiDock", 0x10 | 0x10000);
            return false;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception ex) { Log.Write("Runtime installer folder", ex); }
        }
    }
#endif
}
