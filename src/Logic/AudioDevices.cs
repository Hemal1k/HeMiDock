using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace HeMiDock;

/// <summary>
/// Sound devices for the control center: switching the default output / input (the same as choosing it in the Windows
/// volume flyout) and the apps playing sound right now with their own volume (volume mixer).
/// </summary>
static class AudioDevices
{
    // Windows has no public API for the default device – every sound switcher uses this interface of Windows itself.
    // Only SetDefaultEndpoint is called; the methods before it just keep its place in the interface.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }

    /// <summary>Makes the device the default one for everything (apps, communication).</summary>
    public static void SetDefault(string deviceId)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
                policy.SetDefaultEndpoint(deviceId, role);
            Marshal.ReleaseComObject(policy);
        }
        catch (Exception ex) { Log.Write("AudioDevices.SetDefault", ex); }
    }

    public static List<MMDevice> Active(MMDeviceEnumerator e, DataFlow flow)
    {
        try { return e.EnumerateAudioEndPoints(flow, DeviceState.Active).ToList(); }
        catch (Exception ex) { Log.Write("AudioDevices.Active", ex); return new(); }
    }

    /// <summary>One app in the volume mixer: all its sound sessions (a browser has several) under one name.</summary>
    public sealed class AppSound
    {
        public string Name { get; init; }
        public string IconPath { get; init; }
        public bool System { get; init; }
        public List<AudioSessionControl> Sessions { get; } = new();

        public float Volume
        {
            get => Sessions.Count > 0 ? Sessions.Max(s => s.SimpleAudioVolume.Volume) : 0;
            set { foreach (var s in Sessions) try { s.SimpleAudioVolume.Volume = value; } catch { } }
        }

        public bool Mute
        {
            get => Sessions.Count > 0 && Sessions.All(s => s.SimpleAudioVolume.Mute);
            set { foreach (var s in Sessions) try { s.SimpleAudioVolume.Mute = value; } catch { } }
        }
    }

    /// <summary>Apps with sound on the device (system sounds first), each once.</summary>
    public static List<AppSound> Apps(MMDevice device)
    {
        var apps = new Dictionary<string, AppSound>(StringComparer.CurrentCultureIgnoreCase);
        try
        {
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                bool system = s.IsSystemSoundsSession;
                string path = null, name = null;
                if (!system)
                {
                    try
                    {
                        using var p = Process.GetProcessById((int)s.GetProcessID);
                        path = p.MainModule?.FileName;
                        name = p.MainModule?.FileVersionInfo.FileDescription;
                        if (string.IsNullOrWhiteSpace(name)) name = p.ProcessName;
                    }
                    catch { }
                    if (!string.IsNullOrWhiteSpace(s.DisplayName) && !s.DisplayName.StartsWith('@')) name = s.DisplayName;
                    if (string.IsNullOrWhiteSpace(name)) continue;                 // the app is gone already
                }
                else name = L.T("System sounds");

                if (!apps.TryGetValue(name, out var app))
                    apps[name] = app = new AppSound { Name = name, IconPath = path, System = system };
                app.Sessions.Add(s);
            }
        }
        catch (Exception ex) { Log.Write("AudioDevices.Apps", ex); }
        return apps.Values.OrderByDescending(a => a.System).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
