using System.Diagnostics;
using System.Text.Json;

namespace HeMiDock;

/// <summary>A window the taskbar of Windows would show.</summary>
sealed record TaskWindow(IntPtr Handle, string Title, string ExePath, uint ProcessId)
{
    public string AppId { get; init; }
    /// <summary>The small picture an app puts on its taskbar icon (new mail …).</summary>
    public IconImage Overlay { get; init; }
    /// <summary>Progress the app reports on the taskbar (0–1), -1 for none.</summary>
    public double Progress { get; init; } = -1;
    public bool Flashing { get; init; }
    public bool Active { get; init; }
}

/// <summary>
/// The windows of the taskbar: visible, not owned by another window, not a tool window (unless it asks for the taskbar),
/// not hidden by Windows, with a title. Without ManagedShell they come from a few calls of Windows.
/// </summary>
static class TaskWindows
{
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;

    public static List<TaskWindow> List()
    {
        var list = new List<TaskWindow>();
        uint own = (uint)Environment.ProcessId;
        Win32.EnumWindows((h, _) =>
        {
            if (!Win32.IsWindowVisible(h) || Win32.GetWindowTextLength(h) == 0 || Win32.IsCloaked(h)) return true;
            int ex = Win32.GetWindowLong(h, GWL_EXSTYLE);
            bool owned = Win32.GetWindow(h, 4) != IntPtr.Zero;    // GW_OWNER
            if ((ex & WS_EX_APPWINDOW) == 0 && (owned || (ex & WS_EX_TOOLWINDOW) != 0)) return true;
            Win32.GetWindowThreadProcessId(h, out uint pid);
            if (pid == own) return true;
            var title = new System.Text.StringBuilder(256);
            Win32.GetWindowText(h, title, title.Capacity);
            list.Add(new TaskWindow(h, title.ToString(), Win32.ProcessPath(pid), pid));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}

/// <summary>One icon of the dock: a pinned app and / or the windows of a running one.</summary>
sealed class DockEntry
{
    public string Name { get; init; }
    /// <summary>The file whose icon is shown (the shortcut of a pin, otherwise the program).</summary>
    public string IconPath { get; init; }
    public string ExePath { get; init; }
    public PinnedApp Pin { get; init; }
    public List<TaskWindow> Windows { get; } = new();
    public bool IsRunning => Windows.Count > 0;
    /// <summary>A space or a line between the icons (edit mode), not an app.</summary>
    public bool IsGap => Pin is { Spacer: true } or { Line: true };
    /// <summary>A pinned folder: it opens as a stack of its newest files.</summary>
    public bool IsFolder => Pin?.Path is { } p && Directory.Exists(p);
    /// <summary>Outlook: the envelope of new mail goes on its icon.</summary>
    public bool IsMail => ExePath?.EndsWith("OUTLOOK.EXE", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>What the dock shows: the pins in their order with their windows, then the other running apps.</summary>
static class DockModel
{
    static readonly Dictionary<string, string> Targets = new(StringComparer.OrdinalIgnoreCase);

    public static List<DockEntry> Build(IEnumerable<PinnedApp> pins, IEnumerable<TaskWindow> windows)
    {
        var entries = new List<DockEntry>();
        foreach (var pin in pins)
        {
            if (pin.Spacer || pin.Line) { entries.Add(new DockEntry { Name = "", Pin = pin }); continue; }   // a space or a line between icons
            if (pin.Path == null && pin.AppId == null) continue;
            string key = pin.Path ?? "shell:AppsFolder\\" + pin.AppId;
            // a Store app: its key, which its windows have too; File Explorer pinned as the app of Windows (taken over from the
            // taskbar) or as its shortcut, which points to no file: its windows are of explorer.exe – they got an icon of their own
            string exe = pin.Path != null ? TargetOf(pin.Path) ?? (IsExplorerLink(pin.Path) ? ExplorerExe : null)
                : string.Equals(pin.AppId, "Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase) ? ExplorerExe : key;
            string name = pin.Name ?? System.IO.Path.GetFileNameWithoutExtension(pin.Path ?? pin.AppId);
            // File Explorer keeps the name Windows gave it (English on an English Windows) unless renamed: in the dock's language
            if (exe == ExplorerExe && (pin.Name == null || IsExplorerName(pin.Name))) name = L.T("File Explorer");
            entries.Add(new DockEntry { Name = name, IconPath = key, ExePath = exe, Pin = pin });
        }
        foreach (var w in windows.Where(w => w.ExePath != null))
        {
            // a window of a helper program goes to its app (Steam shows its window from steamwebhelper.exe in its folder): to
            // the pinned app, or to an icon of the app itself
            string exe = entries.Any(e => string.Equals(e.ExePath, w.ExePath, StringComparison.OrdinalIgnoreCase)) ? w.ExePath : HelperOf(w) ?? w.ExePath;
            var entry = entries.FirstOrDefault(e => string.Equals(e.ExePath, exe, StringComparison.OrdinalIgnoreCase));
            // a packaged app (shown by its app id) pinned earlier by its program in WindowsApps: still its pin
            if (entry == null && exe.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) && Win32.ProcessPath(w.ProcessId) is { } program)
                entry = entries.FirstOrDefault(e => string.Equals(e.ExePath, program, StringComparison.OrdinalIgnoreCase));
            if (entry == null) entries.Add(entry = new DockEntry { Name = NameOf(exe), IconPath = exe, ExePath = exe });
            entry.Windows.Add(w);
        }
        return entries;
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int size, out int length);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct ProcessBasicInformation { public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId; }

    static readonly Dictionary<uint, string> Helpers = new();
    static readonly string WindowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>
    /// The program that started the process of a window, when the window's program lies in its folder (a helper of it, as
    /// steamwebhelper.exe of Steam); null otherwise. Programs of Windows (Explorer starts most apps) never count. Remembered.
    /// </summary>
    static string HelperOf(TaskWindow w)
    {
        if (Helpers.TryGetValue(w.ProcessId, out var known)) return known;
        string main = null;
        try
        {
            IntPtr h = OpenProcess(0x1000, false, w.ProcessId);       // PROCESS_QUERY_LIMITED_INFORMATION
            if (h != IntPtr.Zero)
            {
                try
                {
                    var info = new ProcessBasicInformation();
                    if (NtQueryInformationProcess(h, 0, ref info, System.Runtime.InteropServices.Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
                        && Win32.ProcessPath((uint)info.InheritedFromUniqueProcessId) is { } parent
                        && !parent.StartsWith(WindowsFolder, StringComparison.OrdinalIgnoreCase)
                        && System.IO.Path.GetDirectoryName(parent) is { } dir
                        && w.ExePath.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(parent, w.ExePath, StringComparison.OrdinalIgnoreCase))
                        main = parent;
                }
                finally { CloseHandle(h); }
            }
        }
        catch { }
        if (Helpers.Count > 500) Helpers.Clear();
        return Helpers[w.ProcessId] = main;
    }

    public static readonly string ExplorerExe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>The shortcut of File Explorer of the taskbar (it points into the shell, not to a file).</summary>
    static bool IsExplorerLink(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return false;
        return IsExplorerName(System.IO.Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>A name Windows gives File Explorer (not one the user chose).</summary>
    static bool IsExplorerName(string name) =>
        name.Equals("File Explorer", StringComparison.OrdinalIgnoreCase) || name.Equals("Průzkumník souborů", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Explorer", StringComparison.OrdinalIgnoreCase) || name.Equals("Průzkumník", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Windows Explorer", StringComparison.OrdinalIgnoreCase);

    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The description of a program ("Visual Studio Code"), else its file name – remembered: reading it from the file took
    /// tens of milliseconds whenever the windows changed (a title, a new window).
    /// </summary>
    static string NameOf(string exe)
    {
        if (Names.TryGetValue(exe, out var known)) return known;
        if (string.Equals(exe, ExplorerExe, StringComparison.OrdinalIgnoreCase)) return Names[exe] = L.T("File Explorer");
        string name = null;
        try { name = exe.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? AppShell.AppName(exe) : FileVersionInfo.GetVersionInfo(exe).FileDescription; } catch { }   // a Store app: its name in Windows
        return Names[exe] = string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileNameWithoutExtension(exe) : name;
    }

    /// <summary>The program a shortcut opens (remembered, shortcuts do not change while the dock runs).</summary>
    static string TargetOf(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return path;
        if (Targets.TryGetValue(path, out var known)) return known;
        string target = null;
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            target = Environment.ExpandEnvironmentVariables((string)shell.CreateShortcut(path).TargetPath ?? "");
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
        catch (Exception ex) { Log.Write("Shortcut " + path, ex); }
        return Targets[path] = string.IsNullOrEmpty(target) ? null : target;
    }
}

/// <summary>Starting apps and switching to their windows.</summary>
static class Launcher
{
    public static void Start(DockEntry entry, bool newWindow = false)
    {
        try
        {
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);   // the app (or its UAC prompt) may come to the front, not only blink
            // a new window of File Explorer (middle click, Shift + click): its shortcut only brings up the open window,
            // explorer.exe itself opens a new one
            if (newWindow && string.Equals(entry.ExePath, DockModel.ExplorerExe, StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(DockModel.ExplorerExe) { UseShellExecute = true });
                return;
            }
            var pin = entry.Pin;
            if (pin?.AppId is { Length: > 0 } id) Process.Start(new ProcessStartInfo("explorer.exe", $"\"shell:AppsFolder\\{id}\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(pin?.Path ?? entry.ExePath) { UseShellExecute = true, Arguments = pin?.Arguments ?? "" });
        }
        catch (Exception ex) { Log.Write("Start " + entry.Name, ex); }
    }

    /// <summary>Like the taskbar: the window in front is minimized, any other one comes to the front.</summary>
    public static void Activate(TaskWindow w)
    {
        if (Win32.GetForegroundWindow() == w.Handle && !Win32.IsIconic(w.Handle)) { Win32.ShowWindow(w.Handle, Win32.SW_MINIMIZE); return; }
        if (Win32.IsIconic(w.Handle)) Win32.ShowWindow(w.Handle, Win32.SW_RESTORE);
        Win32.SetForegroundWindow(w.Handle);
    }

    public static void Close(TaskWindow w) => Win32.PostMessage(w.Handle, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
}
