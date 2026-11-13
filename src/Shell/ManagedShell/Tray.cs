// Taken from ManagedShell (https://github.com/cairoshell/ManagedShell), Apache License 2.0; changed for HeMiDock to run
// without WPF and Windows Forms where needed (pictures as pixels, own dispatcher, timer and message window).
// The files of one area of ManagedShell joined into one (their names are kept as headings).

using ManagedShell.Common.Enums;
using ManagedShell.Common.Helpers;
using ManagedShell.Common.Logging;
using ManagedShell.Interop;
using Microsoft.Win32;
using static ManagedShell.Interop.NativeMethods;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System;

// ----- WindowsTray\BalloonVisibility.cs -----
namespace ManagedShell.WindowsTray
{
    public enum BalloonVisibility
    {
        Visible,
        Hidden,
        TimedOut
    }
}

// ----- WindowsTray\Delegates.cs -----
namespace ManagedShell.WindowsTray
{
    /// <summary>
    /// Delegate signature for the system tray callback.
    /// </summary>
    /// <param name="msg">The system tray message number.</param>
    /// <param name="nicData">The NotifyIconData structure</param>
    /// <returns>Indication of message outcome.</returns>
    public delegate bool SystrayDelegate(uint msg, SafeNotifyIconData nicData);

    /// <summary>
    /// Delegate signature for the icon data callback.
    /// </summary>
    /// <param name="dwMessage">The message sent</param>
    /// <param name="hWnd">The handle of the icon</param>
    /// <param name="uID">The the ID ofthe icon</param>
    /// <param name="guidItem">The GUID of the icon</param>
    /// <returns>Indication of message outcome.</returns>
    public delegate IntPtr IconDataDelegate(int dwMessage, uint hWnd, uint uID, Guid guidItem);


    /// <summary>
    /// Delegate signature for SHAppBarMessage callbacks.
    /// </summary>
    /// <param name="amd">The AppBar message data received by Shell_TrayWnd.</param>
    /// <param name="handled">Whether the delegate handled the message.</param>
    /// <returns>Result if handled.</returns>
    public delegate IntPtr AppBarMessageDelegate(APPBARMSGDATAV3 amd, ref bool handled);
}

// ----- WindowsTray\ExplorerTrayService.cs -----
namespace ManagedShell.WindowsTray
{
    public  class ExplorerTrayService
    {
        private SystrayDelegate trayDelegate;

        public ExplorerTrayService()
        {
        }

        internal void SetSystrayCallback(SystrayDelegate theDelegate)
        {
            trayDelegate = theDelegate;
        }

        internal void Run()
        {
            if (!EnvironmentHelper.IsAppRunningAsShell && trayDelegate != null)
            {
                bool autoTrayEnabled = GetAutoTrayEnabled();

                if (autoTrayEnabled)
                {
                    // we can't get tray icons that are in the hidden area, so disable that temporarily if enabled
                    try
                    {
                        TrayNotify trayNotify = new TrayNotify();

                        SetAutoTrayEnabled(trayNotify, false);
                        GetTrayItems();
                        SetAutoTrayEnabled(trayNotify, true);

                        Marshal.ReleaseComObject(trayNotify);
                    }
                    catch (Exception e)
                    {
                        ShellLogger.Debug($"ExplorerTrayService: Unable to get items using ITrayNotify: {e.Message}");
                    }
                }
                else
                {
                    try
                    {
                        GetTrayItems();
                    }
                    catch (Exception e)
                    {
                        ShellLogger.Debug($"ExplorerTrayService: Unable to get items: {e.Message}");
                    }
                }
            }
        }

        private void GetTrayItems()
        {
            IntPtr toolbarHwnd = FindExplorerTrayToolbarHwnd();

            if (toolbarHwnd == IntPtr.Zero)
            {
                return;
            }

            int count = GetNumTrayIcons(toolbarHwnd);

            if (count < 1)
            {
                return;
            }

            GetWindowThreadProcessId(toolbarHwnd, out var processId);
            IntPtr hProcess = OpenProcess(ProcessAccessFlags.All, false, (int)processId);
            IntPtr hBuffer = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)Marshal.SizeOf(new TBBUTTON()), AllocationType.Commit,
                MemoryProtection.ReadWrite);

            for (int i = 0; i < count; i++)
            {
                TrayItem trayItem = GetTrayItem(i, hBuffer, hProcess, toolbarHwnd);

                if (trayItem.hWnd == IntPtr.Zero || !IsWindow(trayItem.hWnd))
                {
                    ShellLogger.Debug($"ExplorerTrayService: Ignored notify icon {trayItem.szIconText} due to invalid handle");
                    continue;
                }

                SafeNotifyIconData nid = GetTrayItemIconData(trayItem);

                if (trayDelegate != null)
                {
                    if (!trayDelegate((uint)NIM.NIM_ADD, nid))
                    {
                        ShellLogger.Debug("ExplorerTrayService: Ignored notify icon message");
                    }
                }
                else
                {
                    ShellLogger.Debug("ExplorerTrayService: trayDelegate is null");
                }
            }

            VirtualFreeEx(hProcess, hBuffer, 0, AllocationType.Release);

            CloseHandle(hProcess);
        }

        private IntPtr FindExplorerTrayToolbarHwnd()
        {
            IntPtr hwnd = FindWindow("Shell_TrayWnd", "");

            if (hwnd != IntPtr.Zero)
            {
                hwnd = FindWindowEx(hwnd, IntPtr.Zero, "TrayNotifyWnd", "");

                if (hwnd != IntPtr.Zero)
                {
                    hwnd = FindWindowEx(hwnd, IntPtr.Zero, "SysPager", "");

                    if (hwnd != IntPtr.Zero)
                    {
                        hwnd = FindWindowEx(hwnd, IntPtr.Zero, "ToolbarWindow32", IntPtr.Zero);

                        return hwnd;
                    }
                }
            }

            return IntPtr.Zero;
        }

        private int GetNumTrayIcons(IntPtr toolbarHwnd)
        {
            return (int)SendMessage(toolbarHwnd, (int)TB.BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
        }

        private TrayItem GetTrayItem(int i, IntPtr hBuffer, IntPtr hProcess, IntPtr toolbarHwnd)
        {
            TBBUTTON tbButton = new TBBUTTON();
            TrayItem trayItem = new TrayItem();
            IntPtr hTBButton = Marshal.AllocHGlobal(Marshal.SizeOf(tbButton));
            IntPtr hTrayItem = Marshal.AllocHGlobal(Marshal.SizeOf(trayItem));

            IntPtr msgSuccess = SendMessage(toolbarHwnd, (int)TB.GETBUTTON, (IntPtr)i, hBuffer);
            if (ReadProcessMemory(hProcess, hBuffer, hTBButton, Marshal.SizeOf(tbButton), out _))
            {
                tbButton = (TBBUTTON)Marshal.PtrToStructure(hTBButton, typeof(TBBUTTON));

                if (tbButton.dwData != UIntPtr.Zero)
                {
                    if (ReadProcessMemory(hProcess, tbButton.dwData, hTrayItem, Marshal.SizeOf(trayItem), out _))
                    {
                        trayItem = (TrayItem)Marshal.PtrToStructure(hTrayItem, typeof(TrayItem));

                        if ((tbButton.fsState & TBSTATE_HIDDEN) != 0)
                        {
                            trayItem.dwState = 1;
                        }
                        else
                        {
                            trayItem.dwState = 0;
                        }

                        ShellLogger.Debug(
                            $"ExplorerTrayService: Got tray item: {trayItem.szIconText}");
                    }
                }
            }

            return trayItem;
        }

        private SafeNotifyIconData GetTrayItemIconData(TrayItem trayItem)
        {
            SafeNotifyIconData nid = new SafeNotifyIconData();

            nid.hWnd = trayItem.hWnd;
            nid.uID = trayItem.uID;
            nid.uCallbackMessage = trayItem.uCallbackMessage;
            nid.szTip = trayItem.szIconText;
            nid.hIcon = trayItem.hIcon;
            nid.uVersion = trayItem.uVersion;
            nid.guidItem = trayItem.guidItem;
            nid.dwState = (int)trayItem.dwState;
            nid.uFlags = NIF.GUID | NIF.MESSAGE | NIF.TIP | NIF.STATE;

            if (nid.hIcon != IntPtr.Zero)
            {
                nid.uFlags |= NIF.ICON;
            }
            else
            {
                ShellLogger.Warning($"ExplorerTrayService: Unable to use {trayItem.szIconText} icon handle for NOTIFYICONDATA struct");
            }

            return nid;
        }

        private bool GetAutoTrayEnabled()
        {
            int enableAutoTray = 1;

            try
            {
                RegistryKey explorerKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer", false);

                if (explorerKey != null)
                {
                    var enableAutoTrayValue = explorerKey.GetValue("EnableAutoTray");

                    if (enableAutoTrayValue != null)
                    {
                        enableAutoTray = Convert.ToInt32(enableAutoTrayValue);
                    }
                }
            }
            catch (Exception e)
            {
                ShellLogger.Debug($"ExplorerTrayService: Unable to get EnableAutoTray setting: {e.Message}");
            }

            return enableAutoTray == 1;
        }

        private void SetAutoTrayEnabled(TrayNotify trayNotify, bool enabled)
        {
            try
            {
                if (EnvironmentHelper.IsWindows8OrBetter)
                {
                    var trayNotifyInstance = (ITrayNotify)trayNotify;
                    trayNotifyInstance.EnableAutoTray(enabled);
                }
                else
                {
                    var trayNotifyInstance = (ITrayNotifyLegacy)trayNotify;
                    trayNotifyInstance.EnableAutoTray(enabled);
                }
            }
            catch (Exception e)
            {
                ShellLogger.Debug($"ExplorerTrayService: Unable to set EnableAutoTray setting: {e.Message}");
            }
        }

        private const byte TBSTATE_HIDDEN = 8;

        private enum TB : uint
        {
            GETBUTTON = WM.USER + 23,
            BUTTONCOUNT = WM.USER + 24
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct TrayItem
        {
            public IntPtr hWnd;
            public uint uID;
            public uint uCallbackMessage;
            public uint dwState;
            public uint uVersion;
            public IntPtr hIcon;
            public IntPtr uIconDemoteTimerID;
            public uint dwUserPref;
            public uint dwLastSoundTime;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szIconText;
            public uint uNumSeconds;
            public Guid guidItem;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TBBUTTON
        {
            public int iBitmap;
            public int idCommand;
            [StructLayout(LayoutKind.Explicit)]
            private struct TBBUTTON_U
            {
                [FieldOffset(0)] public byte fsState;
                [FieldOffset(1)] public byte fsStyle;
                [FieldOffset(0)] private IntPtr bReserved;
            }
            private TBBUTTON_U union;
            public byte fsState { get { return union.fsState; } set { union.fsState = value; } }
            public byte fsStyle { get { return union.fsStyle; } set { union.fsStyle = value; } }
            public UIntPtr dwData;
            public IntPtr iString;
        }
    }
}

// ----- WindowsTray\NotificationArea.cs -----
namespace ManagedShell.WindowsTray
{
    public partial class NotificationArea : IDisposable
    {
        public const string HEALTH_GUID = "7820ae76-23e3-4229-82c1-e41cb67d5b9c";
        public const string MEETNOW_GUID = "7820ae83-23e3-4229-82c1-e41cb67d5b9c";
        public const string NETWORK_GUID = "7820ae74-23e3-4229-82c1-e41cb67d5b9c";
        public const string POWER_GUID = "7820ae75-23e3-4229-82c1-e41cb67d5b9c";
        public const string VOLUME_GUID = "7820ae73-23e3-4229-82c1-e41cb67d5b9c";

        public static readonly string[] DEFAULT_PINNED = {
            HEALTH_GUID,
            POWER_GUID,
            NETWORK_GUID,
            VOLUME_GUID
        };

        internal static readonly List<string> Win11ActionCenterIcons = EnvironmentHelper.IsWindows1122H2OrBetter ? new List<string>()
        {
            // In 22H2, the network and power flyouts work again
            VOLUME_GUID
        } : new List<string>()
        {
            NETWORK_GUID,
            POWER_GUID,
            VOLUME_GUID
        };

        readonly NativeMethods.Rect defaultPlacement = new NativeMethods.Rect
        {
            Top = 0,
            Left = GetSystemMetrics(0) - 200,
            Bottom = 23,
            Right = 23
        };
        
        public string[] PinnedNotifyIcons { get; internal set; }
        public IntPtr Handle { get; private set; }
        public bool IsFailed { get; private set; }

        public event EventHandler<NotificationBalloonEventArgs> NotificationBalloonShown;

        private AppBarMessageDelegate appBarMessageDelegate;
        private SystrayDelegate trayDelegate;
        private IconDataDelegate iconDataDelegate;
        private object _lockObject = new object();
        private ShellServiceObject shellServiceObject;
        private TrayHostSizeData trayHostSizeData = new TrayHostSizeData
        {
            edge = ABEdge.ABE_TOP,
            rc = new NativeMethods.Rect
            {
                Top = 0,
                Left = 0,
                Bottom = 23,
                Right = GetSystemMetrics(0)
            }
        };

        public NotificationArea(TrayService trayService, ExplorerTrayService explorerTrayService) : this(DEFAULT_PINNED, trayService, explorerTrayService)
        {
        }
        
        public NotificationArea(string[] savedPinnedIcons, TrayService trayService, ExplorerTrayService explorerTrayService)
        {
            PinnedNotifyIcons = savedPinnedIcons;
            _trayService = trayService;
            _explorerTrayService = explorerTrayService;
        }

        // HeMiDock: a plain collection instead of a dependency property of WPF and its filtered views
        public ObservableCollection<NotifyIcon> TrayIcons { get; } = new ObservableCollection<NotifyIcon>();
        private readonly TrayService _trayService;
        private readonly ExplorerTrayService _explorerTrayService;

        public void Initialize()
        {
            try
            {
                prepareCollections();
                trayDelegate = SysTrayCallback;
                iconDataDelegate = IconDataCallback;

                _explorerTrayService.SetSystrayCallback(trayDelegate);
                _explorerTrayService.Run();

                _trayService.SetSystrayCallback(trayDelegate);
                _trayService.SetIconDataCallback(iconDataDelegate);
                Handle = _trayService.Initialize();
                _trayService.Run();

                // load the shell system tray objects (network, power, etc)
                shellServiceObject = new ShellServiceObject();
                shellServiceObject.Start();
            }
            catch
            {
                IsFailed = true;
            }
        }


        internal void UpdatePinnedIcons()
        {
            foreach (NotifyIcon notifyIcon in TrayIcons)
            {
                notifyIcon.SetPinValues();
            }
        }

        #region Collections
        private void prepareCollections() { }




        #endregion

        #region Callbacks
        private IntPtr IconDataCallback(int dwMessage, uint hWnd, uint uID, Guid guidItem)
        {
            NotifyIcon icon = null;
            foreach (NotifyIcon ti in TrayIcons)
            {
                if ((guidItem != Guid.Empty && guidItem == ti.GUID) || (ti.HWnd == (IntPtr)hWnd && ti.UID == uID))
                {
                    icon = ti;
                    break;
                }
            }

            if (icon != null)
            {
                if (dwMessage == 1)
                    return MakeLParamIntPtr(icon.Placement.Left, icon.Placement.Top);
                else if (dwMessage == 2)
                    return MakeLParamIntPtr(icon.Placement.Right, icon.Placement.Bottom);
            }
            else if (guidItem == new Guid(VOLUME_GUID))
            {
                // If the shell is using sndvol32 as a volume control, it won't have a corresponding
                // tray icon in Windows 10+, but it still requests the icon location. Provide the tray.
                if (dwMessage == 1)
                    return MakeLParamIntPtr(trayHostSizeData.rc.Left, trayHostSizeData.rc.Top);
                else if (dwMessage == 2)
                    return MakeLParamIntPtr(trayHostSizeData.rc.Right, trayHostSizeData.rc.Bottom);
            }

            return IntPtr.Zero;
        }

        private bool SysTrayCallback(uint message, SafeNotifyIconData nicData)
        {
            // We always need hWnd for new icons, or for existing icons if no GUID is present
            if (nicData.hWnd == IntPtr.Zero && (nicData.guidItem == Guid.Empty || (NIM)message == NIM.NIM_ADD))
                return false;

            NotifyIcon trayIcon = new NotifyIcon(this, nicData.hWnd);
            trayIcon.UID = nicData.uID;

            lock (_lockObject)
            {
                if ((NIM)message == NIM.NIM_ADD || (NIM)message == NIM.NIM_MODIFY)
                {
                    try
                    {
                        bool exists = false;
                        bool titleChanged = false;

                        // hide icons while we are shell which require UWP support & we have a separate implementation for
                        if (nicData.guidItem == new Guid(VOLUME_GUID) && ((EnvironmentHelper.IsAppRunningAsShell && EnvironmentHelper.IsWindows10OrBetter) || GroupPolicyHelper.HideScaVolume))
                            return false;

                        // hide icons per group policy
                        if ((nicData.guidItem == new Guid(HEALTH_GUID) && GroupPolicyHelper.HideScaHealth) ||
                            (nicData.guidItem == new Guid(MEETNOW_GUID) && GroupPolicyHelper.HideScaMeetNow) ||
                            (nicData.guidItem == new Guid(NETWORK_GUID) && GroupPolicyHelper.HideScaNetwork) ||
                            (nicData.guidItem == new Guid(POWER_GUID) && GroupPolicyHelper.HideScaPower))
                            return false;

                        foreach (NotifyIcon ti in TrayIcons)
                        {
                            if (ti.Equals(nicData))
                            {
                                exists = true;
                                trayIcon = ti;
                                break;
                            }
                        }

                        if ((NIF.STATE & nicData.uFlags) != 0)
                            trayIcon.IsHidden = nicData.dwState == 1;

                        if ((NIF.TIP & nicData.uFlags) != 0 && !string.IsNullOrEmpty(nicData.szTip))
                        {
                            trayIcon.Title = nicData.szTip;
                            titleChanged = true;
                        }

                        if ((NIF.ICON & nicData.uFlags) != 0)
                        {
                            if (nicData.hIcon != IntPtr.Zero)
                            {
                                ShellImage icon = IconImageConverter.GetImageFromHIcon(nicData.hIcon, false);

                                if (icon != null)
                                {
                                    trayIcon.Icon = icon;
                                }
                                else if (icon == null && trayIcon.Icon == null)
                                {
                                    // Use default only if we don't have a valid icon already
                                    trayIcon.Icon = IconImageConverter.GetDefaultIcon();
                                }
                            }
                            else
                            {
                                trayIcon.Icon = null;
                            }
                        }

                        if (nicData.hWnd != IntPtr.Zero)
                        {
                            trayIcon.HWnd = nicData.hWnd;
                            trayIcon.UID = nicData.uID;
                        }

                        if ((NIF.GUID & nicData.uFlags) != 0)
                            trayIcon.GUID = nicData.guidItem;

                        if (nicData.uVersion > 0 && nicData.uVersion <= 4)
                            trayIcon.Version = nicData.uVersion;

                        if ((NIF.MESSAGE & nicData.uFlags) != 0)
                            trayIcon.CallbackMessage = nicData.uCallbackMessage;

                        if (!exists)
                        {
                            // we need a valid hWnd to add a new icon
                            if (nicData.hWnd == IntPtr.Zero)
                                return false;

                            // default placement to a menu bar-like rect
                            trayIcon.Placement = defaultPlacement;

                            // set properties used for pinning
                            trayIcon.Path = ShellHelper.GetPathForWindowHandle(trayIcon.HWnd);
                            trayIcon.SetPinValues();

                            if (trayIcon.Icon == null)
                                trayIcon.Icon = IconImageConverter.GetDefaultIcon();

                            TrayIcons.Add(trayIcon);

                            if ((NIF.INFO & nicData.uFlags) != 0)
                                handleBalloonData(nicData, trayIcon);

                            ShellLogger.Debug($"NotificationArea: Added: {trayIcon.Title} Path: {trayIcon.Path} Hidden: {trayIcon.IsHidden} GUID: {trayIcon.GUID} UID: {trayIcon.UID} Version: {trayIcon.Version}");

                            if ((NIM)message == NIM.NIM_MODIFY)
                            {
                                // return an error to the notifyicon as we received a modify for an icon we did not yet have
                                return false;
                            }
                        }
                        else
                        {
                            if ((NIF.INFO & nicData.uFlags) != 0)
                                handleBalloonData(nicData, trayIcon);

                            if (titleChanged && trayIcon.GUID == default && !trayIcon.IsPinned)
                            {
                                // set properties used for pinning
                                trayIcon.SetPinValues();
                            }

                            ShellLogger.Debug($"NotificationArea: Modified: {trayIcon.Title}");
                        }
                    }
                    catch (Exception ex)
                    {
                        ShellLogger.Error("NotificationArea: Unable to modify the icon in the collection.", ex);
                    }
                }
                else if ((NIM)message == NIM.NIM_DELETE)
                {
                    try
                    {
                        foreach (var icon in TrayIcons)
                        {
                            if (icon.Equals(trayIcon))
                            {
                                TrayIcons.Remove(trayIcon);

                                ShellLogger.Debug($"NotificationArea: Removed: {icon.Title}");

                                return true;
                            }
                        }

                        return false;
                    }
                    catch (Exception ex)
                    {
                        ShellLogger.Error("NotificationArea: Unable to remove the icon from the collection.", ex);
                    }
                }
                else if ((NIM)message == NIM.NIM_SETVERSION)
                {
                    if (nicData.uVersion > 4)
                    {
                        return false;
                    }

                    foreach (NotifyIcon ti in TrayIcons)
                    {
                        if (ti.Equals(nicData))
                        {
                            ti.Version = nicData.uVersion;
                            ShellLogger.Debug($"NotificationArea: Modified version to {ti.Version} on: {ti.Title}");
                            break;
                        }
                    }
                }
            }
            return true;
        }
        #endregion

        private void handleBalloonData(SafeNotifyIconData nicData, NotifyIcon notifyIcon)
        {
            if (string.IsNullOrEmpty(nicData.szInfoTitle))
            {
                return;
            }

            NotificationBalloon balloonInfo = new NotificationBalloon(nicData, notifyIcon);
            NotificationBalloonEventArgs args = new NotificationBalloonEventArgs
            {
                Balloon = balloonInfo
            };

            ShellLogger.Debug($"NotificationArea: Received notification \"{balloonInfo.Title}\" for {notifyIcon.Title}");

            NotificationBalloonShown?.Invoke(this, args);

            if (!args.Handled)
            {
                notifyIcon.TriggerNotificationBalloon(balloonInfo);
            }
        }

        // The notification area control calls this when an icon is clicked to set the placement of its host (such as for ABM_GETTASKBARPOS usage)
        public void SetTrayHostSizeData(TrayHostSizeData data)
        {
            trayHostSizeData = data;
            _trayService?.SetTrayHostSizeData(trayHostSizeData);
        }

        // The AppBarManager calls this to return information regarding the location of the notification area.
        // With Explorer, the notification area is always in an AppBar, but we do not have that assumption.
        public void FillTrayHostSizeData(ref APPBARDATAV2 abd)
        {
            TrayHostSizeData msd = trayHostSizeData;
            abd.rc = msd.rc;
            abd.uEdge = (uint)msd.edge;
        }

        // The AppBarManager calls this to provide the delegate that handles AppBar messages received by the tray
        public void SetAppBarMessageCallback(AppBarMessageDelegate theDelegate)
        {
            if (theDelegate == null)
            {
                return;
            }

            appBarMessageDelegate = theDelegate;
            _trayService?.SetAppBarMessageCallback(appBarMessageDelegate);
        }

        public void Dispose()
        {
            if (!IsFailed && trayDelegate != null)
            {
                shellServiceObject?.Dispose();
                _trayService.Dispose();
            }
        }
    }
}

// ----- WindowsTray\NotificationBalloon.cs -----
namespace ManagedShell.WindowsTray
{
    public class NotificationBalloon
    {
        public string Info { get; internal set; }

        public string Title { get; internal set; }

        public NIIF Flags { get; internal set; }

        public ShellImage Icon { get; internal set; }

        public int Timeout { get; internal set; }

        public DateTime Received { get; internal set; }

        public readonly NotifyIcon NotifyIcon;

        public NotificationBalloon() { }

        public NotificationBalloon(SafeNotifyIconData nicData, NotifyIcon notifyIcon)
        {
            NotifyIcon = notifyIcon;

            Title = nicData.szInfoTitle;
            Info = nicData.szInfo;
            Flags = nicData.dwInfoFlags;
            if ((int)nicData.uVersion >= 1000)
            {
                // Potentially valid timeout provided
                Timeout = (int)nicData.uVersion;
            }
            else
            {
                // Fall back to the system duration
                uint timeoutDuration = 5;
                SystemParametersInfo(SPI.GETMESSAGEDURATION, 0, ref timeoutDuration, SPIF.None);
                // Convert from sec to msec
                Timeout = (int)timeoutDuration * 1000;
            }
            Received = DateTime.Now;

            if (Flags.HasFlag(NIIF.ERROR))
            {
                Icon = GetSystemIcon(32513);
            }
            else if (Flags.HasFlag(NIIF.INFO))
            {
                Icon = GetSystemIcon(32516);
            }
            else if (Flags.HasFlag(NIIF.WARNING))
            {
                Icon = GetSystemIcon(32515);
            }
            else if (Flags.HasFlag(NIIF.USER))
            {
                if (nicData.hBalloonIcon != 0)
                {
                    SetIconFromHIcon((IntPtr)nicData.hBalloonIcon);
                }
                else if (nicData.hIcon != IntPtr.Zero)
                {
                    SetIconFromHIcon(nicData.hIcon);
                }
            }
        }


        public void Click()
        {
            if (NotifyIcon == null)
            {
                ShellLogger.Error("NotificationBalloon: NotifyIcon is null");
                return;
            }

            NotifyIcon.SendMessage((uint)NIN.BALLOONUSERCLICK, 0);
        }

        private void SetIconFromHIcon(IntPtr hIcon)
        {
            if (hIcon == IntPtr.Zero)
            {
                if (Icon == null)
                {
                    // Use default only if we don't have a valid icon already
                    Icon = IconImageConverter.GetDefaultIcon();
                }

                return;
            }

            ShellImage icon = IconImageConverter.GetImageFromHIcon(hIcon, false);

            if (icon != null)
            {
                Icon = icon;
            }
            else if (icon == null && Icon == null)
            {
                // Use default only if we don't have a valid icon already
                Icon = IconImageConverter.GetDefaultIcon();
            }
        }

        [DllImport("user32.dll")] static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

        // HeMiDock: the icon of Windows (IDI_ERROR / IDI_INFORMATION / IDI_WARNING) as pixels; a shared icon, not destroyed
        private ShellImage GetSystemIcon(int id) => IconImageConverter.FromIcon(LoadIcon(IntPtr.Zero, (IntPtr)id));
    }
}

// ----- WindowsTray\NotificationBalloonEventArgs.cs -----
namespace ManagedShell.WindowsTray
{
    public class NotificationBalloonEventArgs : HandledEventArgs
    {
        public NotificationBalloon Balloon;
    }
}

// ----- WindowsTray\NotifyIcon.cs -----
namespace ManagedShell.WindowsTray
{
    /// <summary>
    /// NotifyIcon class representing a notification area icon.
    /// </summary>
    public partial class NotifyIcon : IEquatable<NotifyIcon>, INotifyPropertyChanged
    {
        private readonly NotificationArea _notificationArea;

        /// <summary>
        /// Initializes a new instance of the TrayIcon class with no hwnd.
        /// </summary>
        public NotifyIcon(NotificationArea notificationArea) : this(notificationArea, IntPtr.Zero)
        {
        }

        /// <summary>
        /// Initializes a new instance of the TrayIcon class with the specified hWnd.
        /// </summary>
        /// <param name="hWnd">The window handle of the icon.</param>
        public NotifyIcon(NotificationArea notificationArea, IntPtr hWnd)
        {
            _notificationArea = notificationArea;
            HWnd = hWnd;
            MissedNotifications = new ObservableCollection<NotificationBalloon>();
        }

        private ShellImage _icon;

        /// <summary>
        /// Gets or sets the Icon's image.
        /// </summary>
        public ShellImage Icon
        {
            get
            {
                return _icon;
            }
            set
            {
                _icon = value;
                OnPropertyChanged();
            }
        }

        private string _title;

        /// <summary>
        /// Gets or sets the Icon's title (tool tip).
        /// </summary>
        public string Title
        {
            get
            {
                return _title;
            }
            set
            {
                _title = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Gets or sets the path to the application that created the icon.
        /// </summary>
        public string Path
        {
            get;
            set;
        }

        private bool _isPinned;

        /// <summary>
        /// Gets or sets whether or not the icon is pinned.
        /// </summary>
        public bool IsPinned
        {
            get
            {
                return _isPinned;
            }
            set
            {
                if (_isPinned == value)
                {
                    return;
                }

                if (value)
                {
                    Pin();
                }
                else
                {
                    Unpin();
                }

                // the backing var is updated by the above called methods
            }
        }

        private bool _isHidden;

        /// <summary>
        /// Gets or sets whether or not the icon is hidden.
        /// </summary>
        public bool IsHidden
        {
            get
            {
                return _isHidden;
            }
            set
            {
                _isHidden = value;
                OnPropertyChanged();
            }
        }

        private int _pinOrder;

        /// <summary>
        /// Gets the order index of the item in the pinned icons
        /// </summary>
        public int PinOrder
        {
            get
            {
                return _pinOrder;
            }
            private set
            {
                _pinOrder = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Gets or sets the owners handle.
        /// </summary>
        public IntPtr HWnd
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the callback message id.
        /// </summary>
        public uint CallbackMessage
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the UID of the Icon.
        /// </summary>
        public uint UID
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the GUID of the Icon.
        /// </summary>
        public Guid GUID
        {
            get;
            set;
        }

        public uint Version
        {
            get;
            set;
        }

        public Rect Placement
        {
            get;
            set;
        }

        public string Identifier
        {
            get
            {
                if (GUID != default) return GUID.ToString();
                else return Path + ":" + UID.ToString() + ":" + Title;
            }
        }

        #region Balloon Notifications

        public ObservableCollection<NotificationBalloon> MissedNotifications
        {
            get;
            set;
        }

        public event EventHandler<NotificationBalloonEventArgs> NotificationBalloonShown;

        internal void TriggerNotificationBalloon(NotificationBalloon balloonInfo)
        {
            NotificationBalloonEventArgs args = new NotificationBalloonEventArgs
            {
                Balloon = balloonInfo
            };

            NotificationBalloonShown?.Invoke(this, args);

            if (!args.Handled)
            {
                MissedNotifications.Add(balloonInfo);
            }
        }

        #endregion

        #region Pinning

        public void Pin()
        {
            Pin(_notificationArea.PinnedNotifyIcons.Length);
        }
        
        public void Pin(int position)
        {
            bool updated = false;

            if (IsPinned && position != PinOrder)
            {
                // already pinned, just moving
                List<string> icons = _notificationArea.PinnedNotifyIcons.ToList();
                int insertPos = position;

                for (int i = 0; i < icons.Count; i++)
                {
                    if (IsEqualByIdentifier(icons[i]))
                    {
                        icons.RemoveAt(i);
                    }
                }

                if (PinOrder < position && position > 0)
                {
                    insertPos -= 1;
                }

                icons.Insert(insertPos, Identifier);
                _notificationArea.PinnedNotifyIcons = icons.ToArray();
                updated = true;
            }
            else if (!IsPinned)
            {
                // newly pinned. welcome to the party!
                List<string> icons = _notificationArea.PinnedNotifyIcons.ToList();
                icons.Insert(position, Identifier);
                _notificationArea.PinnedNotifyIcons = icons.ToArray();
                updated = true;
            }

            if (updated)
            {
                OnPropertyChanged("IsPinned");
                _notificationArea.UpdatePinnedIcons();
            }
        }

        public void Unpin()
        {
            if (!IsPinned)
            {
                return;
            }

            List<string> icons = _notificationArea.PinnedNotifyIcons.ToList();

            for (int i = 0; i < icons.Count; i++)
            {
                if (IsEqualByIdentifier(icons[i]))
                {
                    icons.RemoveAt(i);
                }
            }

            _notificationArea.PinnedNotifyIcons = icons.ToArray();

            _isPinned = false;
            PinOrder = 0;

            OnPropertyChanged("IsPinned");
            _notificationArea.UpdatePinnedIcons();
        }

        public void SetPinValues()
        {
            var inPinnedList = false;

            for (int i = 0; i < _notificationArea.PinnedNotifyIcons.Length; i++)
            {
                string item = _notificationArea.PinnedNotifyIcons[i];
                if (IsEqualByIdentifier(item))
                {
                    inPinnedList = true;
                    PinOrder = i;
                    break;
                }
            }

            if (inPinnedList != _isPinned)
            {
                _isPinned = inPinnedList;
                OnPropertyChanged("IsPinned");
            }
        }

        public bool IsEqualByIdentifier(string otherIdentifier)
        {
            otherIdentifier = otherIdentifier.ToLower();

            if (otherIdentifier == GUID.ToString().ToLower())
            {
                return true;
            }

            if (GUID != default || Path == null)
            {
                // Ignore below checks
                return false;
            }

            string pathLower = Path.ToLower();

            if (otherIdentifier.StartsWith(pathLower + ":" + UID.ToString()))
            {
                return true;
            }

            string[] otherIdentifierArray = otherIdentifier.Split(new[] { ':' }, 4);

            if (otherIdentifierArray.Length < 4)
            {
                return false;
            }

            string cleanTitle = Title;
            if (cleanTitle == null)
            {
                cleanTitle = "";
            }

            if (otherIdentifierArray[0] + ":" + otherIdentifierArray[1] == pathLower && otherIdentifierArray[3] == cleanTitle.ToLower())
            {
                return true;
            }

            return false;
        }
        #endregion

        #region Mouse events

        private DateTime _lastLClick = DateTime.Now;
        private DateTime _lastMClick = DateTime.Now;
        private DateTime _lastRClick = DateTime.Now;

        public void IconMouseEnter(uint mouse)
        {
            if (RemoveIfInvalid()) return;

            SendMessage((uint)WM.MOUSEHOVER, mouse);

            if (Version > 3) SendMessage((uint)NIN.POPUPOPEN, mouse);
        }

        public void IconMouseLeave(uint mouse)
        {
            if (RemoveIfInvalid()) return;

            SendMessage((uint)WM.MOUSELEAVE, mouse);

            if (Version > 3) SendMessage((uint)NIN.POPUPCLOSE, mouse);
        }


        public void IconMouseDown(MouseButton button, uint mouse, int doubleClickTime)
        {
            // allow notify icon to focus so that menus go away after clicking outside
            GetWindowThreadProcessId(HWnd, out uint procId);
            AllowSetForegroundWindow(procId);

            if (button == MouseButton.Left)
            {
                if (handleClickOverride(false))
                {
                    return;
                }

                if (DateTime.Now.Subtract(_lastLClick).TotalMilliseconds <= doubleClickTime)
                {
                    SendMessage((uint)WM.LBUTTONDBLCLK, mouse);
                }
                else
                {
                    SendMessage((uint)WM.LBUTTONDOWN, mouse);
                }

                _lastLClick = DateTime.Now;
            }
            else if (button == MouseButton.Middle)
            {
                if (handleClickOverride(false))
                {
                    return;
                }

                if (DateTime.Now.Subtract(_lastMClick).TotalMilliseconds <= doubleClickTime)
                {
                    SendMessage((uint)WM.MBUTTONDBLCLK, mouse);
                }
                else
                {
                    SendMessage((uint)WM.MBUTTONDOWN, mouse);
                }

                _lastMClick = DateTime.Now;
            }
            else if (button == MouseButton.Right)
            {
                if (DateTime.Now.Subtract(_lastRClick).TotalMilliseconds <= doubleClickTime)
                {
                    SendMessage((uint)WM.RBUTTONDBLCLK, mouse);
                }
                else
                {
                    SendMessage((uint)WM.RBUTTONDOWN, mouse);
                }

                _lastRClick = DateTime.Now;
            }
        }

        public void IconMouseUp(MouseButton button, uint mouse, int doubleClickTime)
        {
            ShellLogger.Debug($"NotifyIcon: {button} mouse button clicked: {Title}");

            if (button == MouseButton.Left)
            {
                if (handleClickOverride(true))
                {
                    return;
                }

                SendMessage((uint)WM.LBUTTONUP, mouse);

                // This is documented as version 4, but Explorer does this for version 3 as well
                if (Version >= 3) SendMessage((uint)NIN.SELECT, mouse);

                _lastLClick = DateTime.Now;
            }
            else if (button == MouseButton.Middle)
            {
                if (handleClickOverride(true))
                {
                    return;
                }

                SendMessage((uint)WM.MBUTTONUP, mouse);

                _lastMClick = DateTime.Now;
            }
            else if (button == MouseButton.Right)
            {
                SendMessage((uint)WM.RBUTTONUP, mouse);

                // This is documented as version 4, but Explorer does this for version 3 as well
                if (Version >= 3) SendMessage((uint)WM.CONTEXTMENU, mouse);

                _lastRClick = DateTime.Now;
            }
        }

        internal bool SendMessage(uint message, uint mouse)
        {
            return SendNotifyMessage(HWnd, CallbackMessage, GetMessageWParam(mouse), message | (GetMessageHiWord() << 16));
        }

        private uint GetMessageHiWord()
        {
            if (Version > 3)
            {
                return UID;
            }

            return 0;
        }

        private uint GetMessageWParam(uint mouse)
        {
            if (Version > 3)
            {
                return mouse;
            }

            return UID;
        }

        private bool RemoveIfInvalid()
        {
            if (!IsWindow(HWnd))
            {
                _notificationArea.TrayIcons.Remove(this);
                return true;
            }

            return false;
        }

        private bool handleClickOverride(bool performAction)
        {
            if (NotificationArea.Win11ActionCenterIcons.Contains(GUID.ToString()) && EnvironmentHelper.IsWindows11OrBetter)
            {
                if (performAction)
                {
                    ShellHelper.ShowActionCenter();
                }

                if (!EnvironmentHelper.IsWindows1122H2OrBetter)
                {
                    // Some earlier Windows 11 versions have a crash when sending click events
                    return true;
                }
            }

            return false;
        }
        #endregion

        #region IEquatable<NotifyIcon> Members

        /// <summary>
        /// Checks the equality of the icon based on the hWnd and uID;
        /// </summary>
        /// <param name="other">The other NotifyIcon to compare to.</param>
        /// <returns>Indication of equality.</returns>
        public bool Equals(NotifyIcon other)
        {
            if (other == null) return false;

            return (HWnd.Equals(other.HWnd) && UID.Equals(other.UID)) || (other.GUID != Guid.Empty && GUID.Equals(other.GUID));
        }

        public bool Equals(SafeNotifyIconData other)
        {
            return (HWnd.Equals(other.hWnd) && UID.Equals(other.uID)) || (other.guidItem != Guid.Empty && GUID.Equals(other.guidItem));
        }

        #endregion

        #region INotifyPropertyChanged Members

        public event PropertyChangedEventHandler PropertyChanged;

        public void OnPropertyChanged([CallerMemberName]string PropertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(PropertyName));
        }

        #endregion
    }
}

// ----- WindowsTray\SafeNotifyIconData.cs -----
namespace ManagedShell.WindowsTray
{
    public class SafeNotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public NIF uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        public string szTip;
        public int dwState;
        public int dwStateMask;
        public string szInfo;
        public uint uVersion;  // used with NIM_SETVERSION, values 0, 3 and 4
        public string szInfoTitle;
        public NIIF dwInfoFlags;
        public Guid guidItem;
        public uint hBalloonIcon;

        public SafeNotifyIconData() { }

        public SafeNotifyIconData(NOTIFYICONDATA nid)
        {
            cbSize = nid.cbSize;
            hWnd = (IntPtr)nid.hWnd;
            uID = nid.uID;
            uFlags = nid.uFlags;
            uCallbackMessage = nid.uCallbackMessage;

            try
            {
                hIcon = (IntPtr)nid.hIcon;
            }
            catch (Exception e)
            {
                ShellLogger.Error($"SafeNotifyIconData: Unable to convert icon handle: {e.Message}");
            }
            
            szTip = nid.szTip;
            dwState = nid.dwState;
            dwStateMask = nid.dwStateMask;
            szInfo = nid.szInfo;
            uVersion = nid.uVersion;
            szInfoTitle = nid.szInfoTitle;
            dwInfoFlags = nid.dwInfoFlags;
            guidItem = nid.guidItem;
            hBalloonIcon = nid.hBalloonIcon;
        }
    }
}

// ----- WindowsTray\ShellServiceObject.cs -----
namespace ManagedShell.WindowsTray
{
    public partial class ShellServiceObject: IDisposable
    {
        const string CGID_SHELLSERVICEOBJECT = "000214D2-0000-0000-C000-000000000046";
        private IOleCommandTarget sysTrayObject;

        public void Start()
        {
            if (EnvironmentHelper.IsAppRunningAsShell)
            {
                try
                {
                    sysTrayObject = (IOleCommandTarget)new SysTrayObject();
                    Guid sso = new Guid(CGID_SHELLSERVICEOBJECT);
                    sysTrayObject.Exec(ref sso, OLECMDID_NEW, OLECMDEXECOPT_DODEFAULT, IntPtr.Zero, IntPtr.Zero);
                }
                catch
                {
                    ShellLogger.Debug("ShellServiceObject: Unable to start");
                }
            }
        }

        public void Dispose()
        {
            if (sysTrayObject != null)
            {
                try
                {
                    Guid sso = new Guid(CGID_SHELLSERVICEOBJECT);
                    sysTrayObject.Exec(ref sso, OLECMDID_SAVE, OLECMDEXECOPT_DODEFAULT, IntPtr.Zero, IntPtr.Zero);
                }
                catch
                {
                    ShellLogger.Debug("ShellServiceObject: Unable to stop");
                }
            }
        }
    }
}

// ----- WindowsTray\SysTrayObject.cs -----
namespace ManagedShell.WindowsTray
{
    [ComImport]
    [Guid("35CEC8A3-2BE6-11D2-8773-92E220524153")]
    class SysTrayObject
    {
    }
}

// ----- WindowsTray\TrayHostSizeData.cs -----
namespace ManagedShell.WindowsTray
{
    /// <summary>
    /// AppBar message data structure type
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct TrayHostSizeData
    {
        public Rect rc;
        public ABEdge edge;
    }
}

// ----- WindowsTray\TrayNotify.cs -----
namespace ManagedShell.WindowsTray
{
    [ComImport]
    [Guid("25DEAD04-1EAC-4911-9E3A-AD0A4AB560FD")]
    class TrayNotify
    {
    }

    enum NOTIFYITEM_PREFERENCE
    {
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYITEM
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pszExeName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pszIconText;
        public IntPtr hIcon;
        public IntPtr hWnd;
        public NOTIFYITEM_PREFERENCE dwUserPref;
        public uint uID;
        public Guid guidItem;
    };

    [ComImport]
    [Guid("D782CCBA-AFB0-43F1-94DB-FDA3779EACCB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface INotificationCB
    {
        void Notify([In] uint nEvent, [In] ref NOTIFYITEM notifyItem);
    }

    [ComImport]
    [Guid("FB852B2C-6BAD-4605-9551-F15F87830935")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITrayNotifyLegacy
    {
        void RegisterCallback([MarshalAs(UnmanagedType.Interface)] INotificationCB callback);
        void SetPreference([In] ref NOTIFYITEM notifyItem);
        void EnableAutoTray([In] bool enabled);
    }

    [ComImport]
    [Guid("D133CE13-3537-48BA-93A7-AFCD5D2053B4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITrayNotify
    {
        void RegisterCallback([MarshalAs(UnmanagedType.Interface)] INotificationCB callback, [Out] out ulong handle);
        void UnregisterCallback([In] ulong handle);
        void SetPreference([In] ref NOTIFYITEM notifyItem);
        void EnableAutoTray([In] bool enabled);
        void DoAction([In] bool enabled);
    }
}

// ----- WindowsTray\TrayService.cs -----
namespace ManagedShell.WindowsTray
{
    public partial class TrayService : IDisposable
    {
        private const string NotifyWndClass = "TrayNotifyWnd";
        private const string TrayWndClass = "Shell_TrayWnd";
        private readonly int[] ForwardMessagesPost = { (int)WM.USER + 372 };

        private AppBarMessageDelegate appBarMessageDelegate;
        private IconDataDelegate iconDataDelegate;
        private SystrayDelegate trayDelegate;
        private WndProcDelegate wndProcDelegate;

        private IntPtr HwndTray;
        private IntPtr HwndNotify;
        private IntPtr HwndFwd;
        private IntPtr hInstance = Marshal.GetHINSTANCE(typeof(TrayService).Module);

        private readonly ShellTimer trayMonitor = new ShellTimer();

        public TrayService()
        {
            SetupTrayMonitor();
        }

        #region Set callbacks
        internal void SetSystrayCallback(SystrayDelegate theDelegate)
        {
            trayDelegate = theDelegate;
        }

        internal void SetIconDataCallback(IconDataDelegate theDelegate)
        {
            iconDataDelegate = theDelegate;
        }

        internal void SetAppBarMessageCallback(AppBarMessageDelegate theDelegate)
        {
            appBarMessageDelegate = theDelegate;
        }
        #endregion

        internal IntPtr Initialize()
        {
            if (HwndTray != IntPtr.Zero)
            {
                return HwndTray;
            }

            DestroyWindows();

            wndProcDelegate = WndProc;

            RegisterTrayWnd();
            RegisterNotifyWnd();

            return HwndTray;
        }

        /// <summary>
        /// Starts the system tray listener (send the TaskbarCreated message).
        /// </summary>
        internal void Run()
        {
            if (HwndTray != IntPtr.Zero)
            {
                Resume();
                SendTaskbarCreated();
            }
        }


        internal void Resume()
        {
            // if we are above another tray, we will receive messages
            if (HwndTray != IntPtr.Zero)
            {
                SetWindowsTrayBottommost();
                MakeTrayTopmost();
                trayMonitor.Start();
            }
        }

        internal void SetTrayHostSizeData(TrayHostSizeData data)
        {
            if (HwndTray != IntPtr.Zero)
            {
                SetWindowPos(HwndTray, IntPtr.Zero, data.rc.Left, data.rc.Top, data.rc.Width, data.rc.Height, (int)SetWindowPosFlags.SWP_NOACTIVATE | (int)SetWindowPosFlags.SWP_NOZORDER);
            }

            if (HwndNotify != IntPtr.Zero)
            {
                SetWindowPos(HwndNotify, IntPtr.Zero, data.rc.Left, data.rc.Top, data.rc.Width, data.rc.Height, (int)SetWindowPosFlags.SWP_NOACTIVATE | (int)SetWindowPosFlags.SWP_NOZORDER);
            }
        }

        private void SendTaskbarCreated()
        {
            int msg = RegisterWindowMessage("TaskbarCreated");

            if (msg > 0)
            {
                ShellLogger.Debug("TrayService: Sending TaskbarCreated message");
                SendNotifyMessage(HWND_BROADCAST,
                    (uint)msg, UIntPtr.Zero, IntPtr.Zero);
            }
        }

        private void DestroyWindows()
        {
            if (HwndNotify != IntPtr.Zero)
            {
                DestroyWindow(HwndNotify);
                UnregisterClass(NotifyWndClass, hInstance);
                ShellLogger.Debug($"TrayService: Unregistered {NotifyWndClass}");
            }

            if (HwndTray != IntPtr.Zero)
            {
                DestroyWindow(HwndTray);
                UnregisterClass(TrayWndClass, hInstance);
                ShellLogger.Debug($"TrayService: Unregistered {TrayWndClass}");
            }
        }

        public void Dispose()
        {
            trayMonitor.Stop();
            DestroyWindows();

            if (!EnvironmentHelper.IsAppRunningAsShell)
                SendTaskbarCreated();
        }

        private IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            switch ((WM)msg)
            {
                case WM.COPYDATA:
                    if (lParam == IntPtr.Zero)
                    {
                        ShellLogger.Debug("TrayService: CopyData is null");
                        break;
                    }

                    COPYDATASTRUCT copyData =
                        (COPYDATASTRUCT)Marshal.PtrToStructure(lParam, typeof(COPYDATASTRUCT));

                    switch ((int)copyData.dwData)
                    {
                        case 0:
                            // AppBar message
                            if (Marshal.SizeOf(typeof(APPBARMSGDATAV3)) == copyData.cbData)
                            {
                                APPBARMSGDATAV3 amd = (APPBARMSGDATAV3)Marshal.PtrToStructure(copyData.lpData,
                                    typeof(APPBARMSGDATAV3));

                                if (Marshal.SizeOf(typeof(APPBARDATAV2)) != amd.abd.cbSize)
                                {
                                    ShellLogger.Debug("TrayService: Size incorrect for AppBarData");
                                    break;
                                }

                                bool handled = false;
                                IntPtr abmResult = IntPtr.Zero;
                                if (appBarMessageDelegate != null)
                                {
                                    abmResult = appBarMessageDelegate(amd, ref handled);
                                }

                                if (handled)
                                {
                                    ShellLogger.Debug($"TrayService: Handled AppBar message {(ABMsg)amd.dwMessage}");
                                    return abmResult;
                                }

                                ShellLogger.Debug($"TrayService: Forwarding AppBar message {(ABMsg)amd.dwMessage}");
                            }
                            else
                            {
                                ShellLogger.Debug("TrayService: AppBar message received, but with unknown size");
                            }
                            break;
                        case 1:
                            SHELLTRAYDATA trayData =
                                (SHELLTRAYDATA)Marshal.PtrToStructure(copyData.lpData,
                                    typeof(SHELLTRAYDATA));
                            if (trayDelegate != null)
                            {
                                if (trayDelegate(trayData.dwMessage, new SafeNotifyIconData(trayData.nid)))
                                {
                                    return (IntPtr)1;
                                }

                                ShellLogger.Debug("TrayService: Ignored notify icon message");
                            }
                            else
                            {
                                ShellLogger.Info("TrayService: TrayDelegate is null");
                            }
                            break;
                        case 3:
                            WINNOTIFYICONIDENTIFIER iconData =
                                (WINNOTIFYICONIDENTIFIER)Marshal.PtrToStructure(copyData.lpData,
                                    typeof(WINNOTIFYICONIDENTIFIER));

                            if (iconDataDelegate != null)
                            {
                                return iconDataDelegate(iconData.dwMessage, iconData.hWnd, iconData.uID,
                                    iconData.guidItem);
                            }

                            ShellLogger.Info("TrayService: IconDataDelegate is null");
                            break;
                    }

                    break;
                case WM.WINDOWPOSCHANGED:
                    WINDOWPOS wndPos = WINDOWPOS.FromMessage(lParam);

                    if ((wndPos.flags & SetWindowPosFlags.SWP_SHOWWINDOW) != 0)
                    {
                        SetWindowLong(HwndTray, WindowLongFlags.GWL_STYLE,
                            GetWindowLong(HwndTray, WindowLongFlags.GWL_STYLE) &
                            ~(int)WindowStyles.WS_VISIBLE);

                        ShellLogger.Debug($"TrayService: {TrayWndClass} became visible; hiding");
                    }
                    break;
            }

            if (msg == (int)WM.COPYDATA ||
                msg == (int)WM.ACTIVATEAPP ||
                msg == (int)WM.COMMAND ||
                msg >= (int)WM.USER)
            {
                return ForwardMsg(hWnd, msg, wParam, lParam);
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        #region Event handling
        private IntPtr ForwardMsg(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            if (HwndFwd == IntPtr.Zero || !IsWindow(HwndFwd))
            {
                HwndFwd = WindowHelper.FindWindowsTray(HwndTray);
            }

            if (HwndFwd != IntPtr.Zero)
            {
                if (msg >= (int)WM.USER && ForwardMessagesPost.Contains(msg))
                {
                    ShellLogger.Debug($"TrayService: Forwarding message via PostMessage: {msg}");
                    PostMessage(HwndFwd, (uint)msg, wParam, lParam);
                    return DefWindowProc(hWnd, msg, wParam, lParam);
                }
                return SendMessage(HwndFwd, msg, wParam, lParam);
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }
        #endregion

        #region Window helpers
        private ushort RegisterWndClass(string name)
        {
            WNDCLASS newClass = new WNDCLASS
            {
                lpszClassName = name,
                hInstance = hInstance,
                style = 0x8,
                lpfnWndProc = wndProcDelegate
            };

            return RegisterClass(ref newClass);
        }

        private void RegisterTrayWnd()
        {
            ushort trayClassReg = RegisterWndClass(TrayWndClass);
            if (trayClassReg == 0)
            {
                ShellLogger.Info($"TrayService: Error registering {TrayWndClass} class ({Marshal.GetLastWin32Error()})");
            }

            HwndTray = CreateWindowEx(
                ExtendedWindowStyles.WS_EX_TOPMOST |
                ExtendedWindowStyles.WS_EX_TOOLWINDOW, trayClassReg, "",
                WindowStyles.WS_POPUP | WindowStyles.WS_CLIPCHILDREN |
                WindowStyles.WS_CLIPSIBLINGS, 0, 0, GetSystemMetrics(0),
                Convert.ToInt32(23 * DpiHelper.DpiScale), IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (HwndTray == IntPtr.Zero)
            {
                ShellLogger.Info($"TrayService: Error creating {TrayWndClass} window ({Marshal.GetLastWin32Error()})");
            }
            else
            {
                ShellLogger.Debug($"TrayService: Created {TrayWndClass}");
            }
        }

        private void RegisterNotifyWnd()
        {
            ushort trayNotifyClassReg = RegisterWndClass(NotifyWndClass);
            if (trayNotifyClassReg == 0)
            {
                ShellLogger.Info($"TrayService: Error registering {NotifyWndClass} class ({Marshal.GetLastWin32Error()})");
            }

            HwndNotify = CreateWindowEx(0, trayNotifyClassReg, null,
                WindowStyles.WS_CHILD | WindowStyles.WS_CLIPCHILDREN |
                WindowStyles.WS_CLIPSIBLINGS, 0, 0, GetSystemMetrics(0),
                Convert.ToInt32(23 * DpiHelper.DpiScale), HwndTray, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (HwndNotify == IntPtr.Zero)
            {
                ShellLogger.Info($"TrayService: Error creating {NotifyWndClass} window ({Marshal.GetLastWin32Error()})");
            }
            else
            {
                ShellLogger.Debug($"TrayService: Created {NotifyWndClass}");
            }
        }

        private void SetupTrayMonitor()
        {
            trayMonitor.Interval = new TimeSpan(0, 0, 0, 0, 100);
            trayMonitor.Tick += TrayMonitor_Tick;
        }

        private void TrayMonitor_Tick(object sender, EventArgs e)
        {
            if (HwndTray == IntPtr.Zero) return;

            IntPtr taskbarHwnd = FindWindow(TrayWndClass, "");

            if (taskbarHwnd == HwndTray) return;

            ShellLogger.Debug("TrayService: Raising Shell_TrayWnd");
            MakeTrayTopmost();
            // HeMiDock: another tray came over ours – Explorer started after the dock (the dock starts at sign-in by a task,
            // before Explorer): the apps that added their icons to its tray meanwhile add them to ours again
            SendTaskbarCreated();
        }

        private void SetWindowsTrayBottommost()
        {
            IntPtr taskbarHwnd = WindowHelper.FindWindowsTray(HwndTray);

            if (taskbarHwnd != IntPtr.Zero)
            {
                SetWindowPos(taskbarHwnd, (IntPtr)WindowZOrder.HWND_BOTTOM, 0, 0, 0, 0,
                    (int)SetWindowPosFlags.SWP_NOMOVE | (int)SetWindowPosFlags.SWP_NOSIZE |
                    (int)SetWindowPosFlags.SWP_NOACTIVATE);
            }
        }

        private void MakeTrayTopmost()
        {
            if (HwndTray != IntPtr.Zero)
            {
                SetWindowPos(HwndTray, (IntPtr)WindowZOrder.HWND_TOPMOST, 0, 0, 0, 0,
                    (int)SetWindowPosFlags.SWP_NOMOVE | (int)SetWindowPosFlags.SWP_NOACTIVATE |
                    (int)SetWindowPosFlags.SWP_NOSIZE);
            }
        }
        #endregion
    }
}
