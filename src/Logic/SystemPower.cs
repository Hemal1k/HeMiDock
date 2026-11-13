using System.Runtime.InteropServices;

namespace HeMiDock;

/// <summary>
/// Updates of Windows waiting for a restart: the power menus then offer "Update and shut down" and "Update and restart"
/// next to the plain items (as the Start menu of Windows does), which install them while Windows goes down.
/// </summary>
static class SystemPower
{
    public const string UpdateShutDown = "Update and shut down", UpdateRestart = "Update and restart";

    static bool _pending;
    static DateTime _checked;

    /// <summary>
    /// Updates wait for a restart: Windows Update tells its own power menu so (Orchestrator\ShutdownFlyoutOptions other than 0 –
    /// the only mark of a driver "pending restart" on Windows 11, it is kept inside the update service otherwise), the
    /// documented Windows Update Agent says so, or the marks of Windows Update and of the servicing stack in the registry.
    /// Read again at most every 30 seconds. (wuaext.dll, GetAutoUpdateNotification, is gone from new Windows 11.)
    /// </summary>
    public static bool UpdatePending { get { lock (Gate) return ReadPending(); } }   // also read by the status icons in the background
    static readonly object Gate = new();

    static bool ReadPending()
    {
        {
            if (DateTime.UtcNow - _checked < TimeSpan.FromSeconds(30)) return _pending;
            _checked = DateTime.UtcNow;
            bool pending = false;
            try
            {
                using var lm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
                using (var o = lm.OpenSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\Orchestrator"))
                    pending = o?.GetValue("ShutdownFlyoutOptions") is int options && options != 0;
                foreach (var mark in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
                                             @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending" })
                    using (var k = lm.OpenSubKey(mark))
                        pending |= k != null;
            }
            catch { }
            if (!pending)
                try
                {
                    var type = Type.GetTypeFromProgID("Microsoft.Update.SystemInfo");
                    if (type != null)
                    {
                        object info = Activator.CreateInstance(type);
                        try { pending = type.InvokeMember("RebootRequired", System.Reflection.BindingFlags.GetProperty, null, info, null) is true; }
                        finally { Marshal.ReleaseComObject(info); }
                    }
                }
                catch { }
            return _pending = pending;
        }
    }

    /// <summary>
    /// The items of a group with the update items after "Shut down" / "Restart" while updates are waiting (each only when its
    /// plain item is shown).
    /// </summary>
    public static List<T> WithUpdates<T>(List<T> group, Func<T, string> id, Func<string, Action, T> make)
    {
        if (!UpdatePending || !group.Any(p => id(p) is "Shut down" or "Restart")) return group;
        var list = new List<T>();
        foreach (var p in group)
        {
            // before the plain item, as Windows orders them
            if (id(p) == "Shut down") list.Add(make(UpdateShutDown, () => ShutDown(restart: false, update: true)));
            else if (id(p) == "Restart") list.Add(make(UpdateRestart, UpdateAndRestart));
            list.Add(p);
        }
        return list;
    }

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    /// <summary>Windows is going down (SM_SHUTTINGDOWN).</summary>
    public static bool ShuttingDown => GetSystemMetrics(0x2000) != 0;

    /// <summary>
    /// "Update and restart" the way of Windows Update: "Restart now" in its Settings is done by MoNotificationUx.exe (the
    /// restart log of Windows names it) – it finishes also the updates the update service keeps "pending restart" (a driver),
    /// which a restart with the install flag left waiting (UsoClient RestartDevice did nothing). When Windows has not begun to
    /// go down within 4 s, the restart of the dock right away.
    /// </summary>
    public static async void UpdateAndRestart()
    {
        Watchdog.MarkClean();                                            // the watchdog must not start the dock again meanwhile
        try
        {
            string ux = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"uus\AMD64\MoNotificationUx.exe");
            if (File.Exists(ux))
            {
                Log.Note("Shut down", "MoNotificationUx RebootWithUX");
                Fg.Run(ux, "RebootWithUX");
                for (int i = 0; i < 16 && !ShuttingDown; i++) await Task.Delay(250);
                if (ShuttingDown) return;
                Log.Note("Shut down", "MoNotificationUx did not restart; restart with the install flag");
            }
        }
        catch (Exception ex) { Log.Write("Shut down: MoNotificationUx", ex); }
        ShutDown(restart: true, update: true);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint InitiateShutdown(string machine, string message, uint grace, uint flags, uint reason);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool LookupPrivilegeValue(string system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivilege state, int length, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential, Pack = 4)] struct TokenPrivilege { public int Count; public long Luid; public int Attributes; }

    const uint SHUTDOWN_FORCE_OTHERS = 0x1, SHUTDOWN_RESTART = 0x4, SHUTDOWN_POWEROFF = 0x8, SHUTDOWN_INSTALL_UPDATES = 0x40, SHUTDOWN_ARSO = 0x2000;
    const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000;

    // ───── the update orchestrator of Windows (not documented; the same calls as Open-Shell, MIT) ─────

    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr obj);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateUxUpdateManagerFn(IntPtr self, out IntPtr manager);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetAndModifyShutdownFlagsFn(IntPtr self, uint flags, out uint newFlags);

    static readonly Guid ClsidUpdateSessionOrchestrator = new("B91D5831-B1BD-4608-8198-D72E155020F7");
    static readonly Guid IidUpdateSessionOrchestrator = new("07F3AFAC-7C8A-4CE7-A5E0-3D24EE8A77E0");
    /// <summary>IUxUpdateManager of Windows 10 and of Windows 11 and the place of SetAndModifyShutdownFlags among their methods.</summary>
    static readonly (Guid Iid, int Slot)[] UxUpdateManagers =
    {
        (new Guid("833EE9A0-2999-432C-8EF2-87A8EC2D748D"), 30),
        (new Guid("B96BA95F-9479-4656-B7A1-6F3A69091910"), 31),
    };

    static T Method<T>(IntPtr obj, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), (3 + slot) * IntPtr.Size));   // after the 3 of IUnknown

    /// <summary>
    /// With the enhanced shutdown of Windows Update on (Orchestrator\EnhancedShutdownEnabled), Windows installs the prepared
    /// updates at a shutdown only when the shutdown was announced to its orchestrator first – this is what the shutdown of
    /// Windows itself does (shutdownux UsoCommitHelper::SetAndModifyShutdownFlags); without it the flag to install them was
    /// ignored. The orchestrator may also hand back other flags, which are used then. Without it: the flags as they are.
    /// </summary>
    static uint AnnounceToUpdates(uint flags)
    {
        try
        {
            using (var o = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\Orchestrator"))
                if (o?.GetValue("EnhancedShutdownEnabled") == null) return flags;
            var clsid = ClsidUpdateSessionOrchestrator;
            var iid = IidUpdateSessionOrchestrator;
            if (CoCreateInstance(ref clsid, IntPtr.Zero, 4 /*CLSCTX_LOCAL_SERVER*/, ref iid, out IntPtr orchestrator) != 0) return flags;
            try
            {
                if (Method<CreateUxUpdateManagerFn>(orchestrator, 3)(orchestrator, out IntPtr manager) != 0 || manager == IntPtr.Zero) return flags;
                try
                {
                    foreach (var (managerIid, slot) in UxUpdateManagers)
                    {
                        var want = managerIid;
                        if (Marshal.QueryInterface(manager, ref want, out IntPtr ux) != 0) continue;
                        try { return Method<SetAndModifyShutdownFlagsFn>(ux, slot)(ux, flags, out uint changed) == 0 ? changed : flags; }
                        finally { Marshal.Release(ux); }
                    }
                }
                finally { Marshal.Release(manager); }
            }
            finally { Marshal.Release(orchestrator); }
        }
        catch (Exception ex) { Log.Write("Shut down: update orchestrator", ex); }
        return flags;
    }

    /// <summary>
    /// Shuts down or restarts Windows; with <paramref name="update"/> the waiting updates are installed first. Without the right to
    /// shut down (not given) shutdown.exe does it (without the updates).
    /// </summary>
    public static void ShutDown(bool restart, bool update)
    {
        Watchdog.MarkClean();                                            // the watchdog must not start the dock again meanwhile
        try
        {
            if (OpenProcessToken(GetCurrentProcess(), 0x28, out var token))   // TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY
            {
                try
                {
                    if (LookupPrivilegeValue(null, "SeShutdownPrivilege", out long luid))
                    {
                        var p = new TokenPrivilege { Count = 1, Luid = luid, Attributes = 2 };   // SE_PRIVILEGE_ENABLED
                        AdjustTokenPrivileges(token, false, ref p, 0, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                finally { CloseHandle(token); }
            }
            uint flags = (restart ? SHUTDOWN_RESTART : SHUTDOWN_POWEROFF) | (update ? SHUTDOWN_INSTALL_UPDATES : 0) | SHUTDOWN_FORCE_OTHERS | SHUTDOWN_ARSO;
            flags = AnnounceToUpdates(flags);
            Log.Note("Shut down", $"flags 0x{flags:X}");
            uint result = InitiateShutdown(null, null, 0, flags, SHTDN_REASON_FLAG_PLANNED);
            if (result == 0) return;
            Log.Write("Shut down", new System.ComponentModel.Win32Exception((int)result));
        }
        catch (Exception ex) { Log.Write("Shut down", ex); }
        Fg.Run("shutdown.exe", restart ? "/r /t 0" : "/s /t 0");
    }
}
