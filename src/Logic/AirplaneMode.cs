using System.Runtime.InteropServices;

namespace HeMiDock;

/// <summary>Airplane mode of Windows, through the radio manager Windows uses for its own switch (it has no public interface).</summary>
static class AirplaneMode
{
    [ComImport, Guid("DB3AFBFB-08E6-46C6-AA70-BF9A34C30AB7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IRadioManager
    {
        [PreserveSig] int IsRMSupported(out uint state);
        [PreserveSig] int GetUIRadioInstances([MarshalAs(UnmanagedType.IUnknown)] out object instances);
        [PreserveSig] int GetSystemRadioState(out int radiosOn, out int policy, out int reason);
        [PreserveSig] int SetSystemRadioState(int radiosOn);
        [PreserveSig] int Refresh();
        [PreserveSig] int OnHardwareSliderChange(int a, int b);
    }

    static IRadioManager _manager;

    static IRadioManager Manager => _manager ??= (IRadioManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("581333F6-28DB-41BE-BC7A-FF201F12F3F6")));

    /// <summary>On, off, or null when Windows does not say.</summary>
    public static bool? IsOn
    {
        get
        {
            try { return Manager.GetSystemRadioState(out int radiosOn, out _, out _) == 0 ? radiosOn == 0 : null; }
            catch { return null; }
        }
    }

    public static void Set(bool on)
    {
        try { Manager.SetSystemRadioState(on ? 0 : 1); }
        catch (Exception ex) { Log.Write("Airplane mode", ex); }
    }
}
