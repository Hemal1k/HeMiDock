namespace HeMiDock;

/// <summary>
/// Do not disturb: the same switch as in Windows (notifications go silently to the notification center, alarms and
/// priority apps still show). Windows has no public API for it – the interface its own Settings use.
/// </summary>
static class Dnd
{
    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("6bff4732-81ec-4ffb-ae67-b6c1bc29631f"),
     System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    interface IQuietHoursSettings
    {
        [System.Runtime.InteropServices.PreserveSig]
        int GetUserSelectedProfile([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] out string id);
        [System.Runtime.InteropServices.PreserveSig]
        int SetUserSelectedProfile([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string id);
    }

    const string Off = "Microsoft.QuietHoursProfile.Unrestricted", On = "Microsoft.QuietHoursProfile.PriorityOnly";
    static IQuietHoursSettings _settings;

    static IQuietHoursSettings Settings =>
        _settings ??= (IQuietHoursSettings)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("f53321fa-34f8-4b7f-b9a3-361877cb94cf")));

    public static bool IsOn
    {
        get
        {
            try { return Settings.GetUserSelectedProfile(out var id) == 0 && id != null && id != Off; }
            catch { _settings = null; return false; }
        }
    }

    public static void Set(bool on)
    {
        try { Settings.SetUserSelectedProfile(on ? On : Off); }
        catch (Exception ex) { _settings = null; Log.Write("Dnd", ex); }
    }
}
