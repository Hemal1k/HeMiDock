// Taken from ManagedShell (https://github.com/cairoshell/ManagedShell), Apache License 2.0; changed for HeMiDock to run
// without WPF and Windows Forms where needed (pictures as pixels, own dispatcher, timer and message window).
// The files of one area of ManagedShell joined into one (their names are kept as headings).

using ManagedShell.Common.Enums;
using ManagedShell.Common.Extensions;
using ManagedShell.Common.Interfaces;
using ManagedShell.Common.Logging;
using ManagedShell.Common.Structs;
using ManagedShell.Common.SupportingClasses;
using ManagedShell.Interop;
using Microsoft.Win32;
using static ManagedShell.Interop.NativeMethods;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System;

// ----- Common\Helpers\AppVisibilityHelper.cs -----
namespace ManagedShell.Common.Helpers
{
    public partial class AppVisibilityHelper : IDisposable
    {
        public event EventHandler<AppVisibilityEventArgs> AppVisibilityChanged;
        public event EventHandler<LauncherVisibilityEventArgs> LauncherVisibilityChanged;

        private IAppVisibility _appVis;
        private int _eventCookie;
        private bool _useEvents;

        // Note: when using events, this object must be held in memory by the consumer
        public AppVisibilityHelper(bool useEvents)
        {
            if (!EnvironmentHelper.IsWindows8OrBetter)
            {
                ShellLogger.Debug("AppVisibilityHelper: Requires Windows 8 or higher");
                return;
            }

            // get IAppVisibility instance
            _appVis = (IAppVisibility)new AppVisibility();
            _useEvents = useEvents;

            if (_appVis == null)
            {
                ShellLogger.Debug("AppVisibilityHelper: Unable create IAppVisibility");
                return;
            }

            if (!_useEvents)
            {
                return;
            }

            if (!EnvironmentHelper.IsWindows10OrBetter)
            {
                ShellLogger.Debug("AppVisibilityHelper: Events require Windows 10 or higher");
                return;
            }

            // register for events
            // only works properly on Windows 10 or later
            AppVisibilityEvents events = new AppVisibilityEvents();
            events.AppVisibilityChanged += Events_AppVisibilityChanged;
            events.LauncherVisibilityChanged += Events_LauncherVisibilityChanged;

            if (_appVis.Advise(events, out _eventCookie) == 0)
            {
                // subscribed to events successfully
                ShellLogger.Debug("AppVisibilityHelper: Subscribed to change events");
            }
            else
            {
                ShellLogger.Debug("AppVisibilityHelper: Unable to subscribe to change events");
            }
        }

        private void Events_AppVisibilityChanged(object sender, AppVisibilityEventArgs e)
        {
            AppVisibilityChanged?.Invoke(sender, e);
        }

        private void Events_LauncherVisibilityChanged(object sender, LauncherVisibilityEventArgs e)
        {
            LauncherVisibilityChanged?.Invoke(sender, e);
        }

        public bool IsLauncherVisible()
        {
            if (!EnvironmentHelper.IsWindows8OrBetter)
            {
                return false;
            }

            if (_appVis == null)
            {
                return false;
            }

            _appVis.IsLauncherVisible(out bool pfVisible);

            return pfVisible;
        }

        public MONITOR_APP_VISIBILITY GetAppVisibilityOnMonitor(IntPtr hMonitor)
        {
            if (!EnvironmentHelper.IsWindows8OrBetter)
            {
                return MONITOR_APP_VISIBILITY.MAV_UNKNOWN;
            }

            if (_appVis == null)
            {
                return MONITOR_APP_VISIBILITY.MAV_UNKNOWN;
            }

            _appVis.GetAppVisibilityOnMonitor(hMonitor, out MONITOR_APP_VISIBILITY pMode);

            return pMode;
        }

        public void Dispose()
        {
            if (!EnvironmentHelper.IsWindows8OrBetter)
            {
                return;
            }

            if (_appVis == null)
            {
                return;
            }

            if (_useEvents && _eventCookie > 0)
            {
                // unregister from events
                if (_appVis.Unadvise(_eventCookie) == 0)
                {
                    ShellLogger.Debug("AppVisibilityHelper: Unsubscribed from change events");
                }
            }
        }
    }
}

// ----- Common\Helpers\DpiHelper.cs -----
namespace ManagedShell.Common.Helpers
{
    public static class DpiHelper
    {
        // DPI at user logon to the system
        private static double? _oldDpiScale;
        public static double OldDpiScale
        {
            get
            {
                if (_oldDpiScale == null)
                {
                    _oldDpiScale = GetDpiScale();
                }

                return (double)_oldDpiScale;
            }
            set
            {
                _oldDpiScale = value;
            }
        }

        // Current system DPI; typically set on AppBar startup and on WM_DPICHANGED
        private static double? _dpiScale;
        public static double DpiScale
        {
            get
            {
                if (_dpiScale == null)
                {
                    _dpiScale = GetDpiScale();
                }

                return (double)_dpiScale;
            }
            set
            {
                _dpiScale = value;
            }
        }

        // SystemParameters class returns values based on logon DPI only; this calculates how we should scale that number if logon DPI != current DPI.
        public static double DpiScaleAdjustment
        {
            get { return DpiScale / OldDpiScale; }
        }



        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForSystem();

        private static double GetDpiScale()
        {
            // HeMiDock: the scaling of Windows itself instead of System.Drawing
            return GetDpiForSystem() / 96.0;
        }

    }
}

// ----- Common\Helpers\EnvironmentHelper.cs -----
namespace ManagedShell.Common.Helpers
{
    public static class EnvironmentHelper
    {
        private static int osVersionMajor = 0;
        private static int osVersionMinor = 0;
        private static int osVersionBuild = 0;

        public static bool EnableWin11DpiWorkaround;

        private static void getOSVersion()
        {
            osVersionMajor = Environment.OSVersion.Version.Major;
            osVersionMinor = Environment.OSVersion.Version.Minor;
            osVersionBuild = Environment.OSVersion.Version.Build;
        }

        public static bool IsWindows2kOrBetter
        {
            get
            {
                if (osVersionMajor == 0)
                {
                    getOSVersion();
                }

                return (osVersionMajor >= 5);
            }
        }

        public static bool IsWindowsVistaOrBetter
        {
            get
            {
                if (osVersionMajor == 0)
                {
                    getOSVersion();
                }

                return (osVersionMajor >= 6);
            }
        }

        public static bool IsWindows8OrBetter
        {
            get
            {
                if (osVersionMajor == 0)
                {
                    getOSVersion();
                }

                return (osVersionMajor > 6 || (osVersionMajor == 6 && osVersionMinor >= 2));
            }
        }

        public static bool IsWindows81OrBetter
        {
            get
            {
                if (osVersionMajor == 0)
                {
                    getOSVersion();
                }

                return (osVersionMajor > 6 || (osVersionMajor == 6 && osVersionMinor >= 2 && osVersionBuild >= 9600));
            }
        }

        public static bool IsWindows10OrBetter
        {
            get
            {
                if (osVersionMajor == 0)
                {
                    getOSVersion();
                }

                return (osVersionMajor >= 10);
            }
        }

        public static bool IsWindows10RS1OrBetter
        {
            get
            {
                if (osVersionMajor == 0)
                {
                    getOSVersion();
                }

                return (osVersionMajor >= 10 && osVersionBuild >= 14393);
            }
        }

        public static bool IsWindows10RS4OrBetter
        {
            get
            {
                if (osVersionMajor == 0)
                {
                    getOSVersion();
                }

                return (osVersionMajor >= 10 && osVersionBuild >= 16353);
            }
        }

        public static bool IsWindows1020H1OrBetter
        {
            get
            {
                if (osVersionMajor == 0)
                {
                    getOSVersion();
                }

                return (osVersionMajor >= 10 && osVersionBuild >= 19041);
            }
        }

        public static bool IsWindows11OrBetter
        {
            get
            {
                if (osVersionMajor == 0)
                {
                    getOSVersion();
                }

                return (osVersionMajor >= 10 && osVersionBuild >= 22000);
            }
        }

        public static bool IsWindows1122H2OrBetter
        {
            get
            {
                if (osVersionMajor == 0)
                {
                    getOSVersion();
                }

                return (osVersionMajor >= 10 && osVersionBuild >= 22621);
            }
        }

        public static bool IsWindows1124H2OrBetter
        {
            get
            {
                if (osVersionMajor == 0)
                {
                    getOSVersion();
                }

                return (osVersionMajor >= 10 && osVersionBuild >= 26100);
            }
        }

        public static bool IsWindows10DarkModeSupported
        {
            get
            {
                if (IsServerCore)
                {
                    return false;
                }
                
                if (osVersionMajor == 0)
                {
                    getOSVersion();
                }

                return (osVersionMajor >= 10 && osVersionBuild >= 18362);
            }
        }

        private static bool? isAppConfiguredAsShell;

        /// <summary>
        /// Checks the currently configured shell, NOT the currently running shell! Use IsAppRunningAsShell for that.
        /// </summary>
        public static bool IsAppConfiguredAsShell
        {
            get
            {
                if (isAppConfiguredAsShell == null)
                {
                    // first check if we are the current user's shell
                    RegistryKey userShellKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\WinLogon", false);
                    string userShell = userShellKey?.GetValue("Shell") as string;
                    if (userShell != null)
                    {
                        isAppConfiguredAsShell = userShell.ToLower().Contains(AppDomain.CurrentDomain.FriendlyName.ToLower());
                    }
                    else
                    {
                        // check if we are the current system's shell
                        RegistryKey systemShellKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\WinLogon", false);
                        string systemShell = systemShellKey?.GetValue("Shell") as string;
                        if (systemShell != null)
                        {
                            isAppConfiguredAsShell = systemShell.ToLower().Contains(AppDomain.CurrentDomain.FriendlyName.ToLower());
                        }
                        else
                        {
                            isAppConfiguredAsShell = false;
                        }
                    }
                }

                return (bool)isAppConfiguredAsShell;
            }
            set
            {
                if (value != IsAppConfiguredAsShell)
                {
                    RegistryKey regKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\WinLogon", true);

                    if (value)
                    {
                        // set as the user's shell
                        regKey.SetValue("Shell", AppDomain.CurrentDomain.BaseDirectory + AppDomain.CurrentDomain.FriendlyName);
                    }
                    else
                    {
                        // reset user's shell to system default
                        object userShell = regKey.GetValue("Shell");

                        if (userShell != null)
                        {
                            regKey.DeleteValue("Shell");
                        }
                    }

                    isAppConfiguredAsShell = value;
                }
            }
        }

        public static bool IsAppRunningAsShell;

        private static bool? isServerCore;

        public static bool IsServerCore
        {
            get
            {
                if (isServerCore == null)
                {
                    RegistryKey installationTypeKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion", false);
                    string installationType = installationTypeKey?.GetValue("InstallationType") as string;

                    isServerCore = installationType == "Server Core";
                }

                return (bool)isServerCore;
            }
        }

        private static string windowsProductName;

        public static string WindowsProductName
        {
            get
            {
                if (string.IsNullOrEmpty(windowsProductName))
                {
                    RegistryKey versionKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion", false);
                    windowsProductName = versionKey?.GetValue("ProductName") as string;
                }

                return windowsProductName;
            }
        }

        private static bool? isWow64;

        public static bool IsWow64
        {
            get
            {
                if (isWow64 == null)
                {
                    if ((Environment.OSVersion.Version.Major == 5 && Environment.OSVersion.Version.Minor >= 1) || Environment.OSVersion.Version.Major >= 6)
                    {
                        using (Process p = Process.GetCurrentProcess())
                        {
                            try
                            {
                                bool retVal;
                                
                                if (!NativeMethods.IsWow64Process(p.Handle, out retVal))
                                {
                                    isWow64 = false;
                                }
                                else
                                {
                                    isWow64 = retVal;
                                }
                            }
                            catch (Exception)
                            {
                                isWow64 = false;
                            }
                        }
                    }
                    else
                    {
                        isWow64 = false;
                    }
                }

                return (bool)isWow64;
            }
        }
    }
}

// ----- Common\Helpers\GroupPolicyHelper.cs -----
namespace ManagedShell.Common.Helpers
{
    public static class GroupPolicyHelper
    {
        const string ExplorerPolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";


        /// <summary>
        /// Removes icons, shortcuts, and other default and user-defined items from the desktop, including 
        /// Briefcase, Recycle Bin, Computer, and Network Locations.
        ///
        /// Removing icons and shortcuts does not prevent the user from using another method to start the 
        /// programs or opening the items they represent.
        ///
        /// Also, see "Items displayed in Places Bar" in User Configuration\Administrative Templates\Windows 
        /// Components\Common Open File Dialog to remove the Desktop icon from the Places Bar.This will help 
        /// prevent users from saving data to the Desktop.
        /// </summary>
        /// <remarks>
        /// Policy: Hide and disable all items on the desktop
        /// Category Path: User Configuration\Administrative Templates\Desktop\
        /// Supported On: At least Microsoft Windows 2000
        /// Registry Key: HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer
        /// Value: NoDesktop
        /// Admx: Desktop.admx
        /// Documentation: https://gpsearch.azurewebsites.net/#146
        /// </remarks>
        public static bool NoDesktop => Registry.CurrentUser.GetSubKeyValue<int>(ExplorerPolicyKey, "NoDesktop") == 1;

        /// <summary>
        /// This policy setting allows you to remove Security and Maintenance from the system control area.
        ///
        /// If you enable this policy setting, the Security and Maintenance icon is not displayed in the system notification area.
        ///
        /// If you disable or do not configure this policy setting, the Security and Maintenance icon is displayed in the system notification area. 
        /// </summary>
        /// <remarks>
        /// Policy: Remove the Security and Maintenance icon
        /// Category Path: User Configuration\Administrative Templates\Start Menu and Taskbar\
        /// Supported On: At least Microsoft Windows Vista
        /// Registry Key: HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer
        /// Value: HideSCAHealth
        /// Admx: Taskbar.admx
        /// Documentation: https://gpsearch.azurewebsites.net/#4678
        /// </remarks>
        public static bool HideScaHealth => Registry.CurrentUser.GetSubKeyValue<int>(ExplorerPolicyKey, "HideSCAHealth") == 1;

        /// <summary>
        /// This policy setting allows you to remove the Meet Now icon from the system control area.
        ///
        /// If you enable this policy setting, the Meet Now icon is not displayed in the system notification area.
        /// 
        /// If you disable or do not configure this policy setting, the Meet Now icon is displayed in the system notification area. 
        /// </summary>
        /// <remarks>
        /// Policy: Remove the volume control icon
        /// Category Path: User Configuration\Administrative Templates\Start Menu and Taskbar\
        /// Supported On: At least Microsoft Windows Vista
        /// Registry Key: HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer
        /// Value: HideSCAMeetNow
        /// Admx: Taskbar.admx
        /// Documentation: https://gpsearch.azurewebsites.net/#15675
        /// </remarks>
        public static bool HideScaMeetNow => Registry.CurrentUser.GetSubKeyValue<int>(ExplorerPolicyKey, "HideSCAMeetNow") == 1;

        /// <summary>
        /// This policy setting allows you to remove the networking icon from the system control area.
        ///
        /// If you enable this policy setting, the networking icon is not displayed in the system notification area.
        ///
        /// If you disable or do not configure this policy setting, the networking icon is displayed in the system notification area. 
        /// </summary>
        /// <remarks>
        /// Policy: Remove the networking icon
        /// Category Path: User Configuration\Administrative Templates\Start Menu and Taskbar\
        /// Supported On: At least Microsoft Windows Vista
        /// Registry Key: HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer
        /// Value: HideSCANetwork
        /// Admx: Taskbar.admx
        /// Documentation: https://gpsearch.azurewebsites.net/#4676
        /// </remarks>
        public static bool HideScaNetwork => Registry.CurrentUser.GetSubKeyValue<int>(ExplorerPolicyKey, "HideSCANetwork") == 1;

        /// <summary>
        /// This policy setting allows you to remove the battery meter from the system control area.
        ///
        /// If you enable this policy setting, the battery meter is not displayed in the system notification area.
        ///
        /// If you disable or do not configure this policy setting, the battery meter is displayed in the system notification area. 
        /// </summary>
        /// <remarks>
        /// Policy: Remove the battery meter
        /// Category Path: User Configuration\Administrative Templates\Start Menu and Taskbar\
        /// Supported On: At least Microsoft Windows Vista
        /// Registry Key: HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer
        /// Value: HideSCAPower
        /// Admx: Taskbar.admx
        /// Documentation: https://gpsearch.azurewebsites.net/#4675
        /// </remarks>
        public static bool HideScaPower => Registry.CurrentUser.GetSubKeyValue<int>(ExplorerPolicyKey, "HideSCAPower") == 1;

        /// <summary>
        /// This policy setting allows you to remove the volume control icon from the system control area.
        /// 
        /// If you enable this policy setting, the volume control icon is not displayed in the system notification area.
        ///
        /// If you disable or do not configure this policy setting, the volume control icon is displayed in the system notification area.
        /// </summary>
        /// <remarks>
        /// Policy: Remove the volume control icon
        /// Category Path: User Configuration\Administrative Templates\Start Menu and Taskbar\
        /// Supported On: At least Microsoft Windows Vista
        /// Registry Key: HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer
        /// Value: HideSCAVolume
        /// Admx: Taskbar.admx
        /// Documentation: https://gpsearch.azurewebsites.net/#4677
        /// </remarks>
        public static bool HideScaVolume => Registry.CurrentUser.GetSubKeyValue<int>(ExplorerPolicyKey, "HideSCAVolume") == 1;
    }
}

// ----- Common\Helpers\IconHelper.cs -----
namespace ManagedShell.Common.Helpers
{
    public static class IconHelper
    {
        public static ComTaskScheduler IconScheduler = new ComTaskScheduler();
        public static object ComLock = new object();
        
        // IImageList references
        private static Guid iidImageList = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
        private static IImageList imlLarge; // 32pt
        private static IImageList imlSmall; // 16pt
        private static IImageList imlExtraLarge; // 48pt
        private static IImageList imlJumbo; // 256pt

        private static void initIml(IconSize size)
        {
            // Initialize the appropriate IImageList for the desired icon size if it hasn't been already

            if (size == IconSize.Large && imlLarge == null)
            {
                SHGetImageList((int)size, ref iidImageList, out imlLarge);
            }
            else if (size == IconSize.Small && imlSmall == null)
            {
                SHGetImageList((int)size, ref iidImageList, out imlSmall);
            }
            else if (size == IconSize.ExtraLarge && imlExtraLarge == null)
            {
                SHGetImageList((int)size, ref iidImageList, out imlExtraLarge);
            }
            else if (size == IconSize.Jumbo && imlJumbo == null)
            {
                SHGetImageList((int)size, ref iidImageList, out imlJumbo);
            }
            else if (size != IconSize.Small && size != IconSize.Large && size != IconSize.ExtraLarge && size != IconSize.Jumbo)
            {
                ShellLogger.Error($"IconHelper: Unsupported icon size {size}");
            }
        }

        public static void DisposeIml()
        {
            // Dispose any IImageList objects we instantiated.
            // Called by the main shutdown method.

            lock (ComLock)
            {
                if (imlLarge != null)
                {
                    Marshal.ReleaseComObject(imlLarge);
                    imlLarge = null;
                }
                if (imlSmall != null)
                {
                    Marshal.ReleaseComObject(imlSmall);
                    imlSmall = null;
                }
                if (imlExtraLarge != null)
                {
                    Marshal.ReleaseComObject(imlExtraLarge);
                    imlExtraLarge = null;
                }
                if (imlJumbo != null)
                {
                    Marshal.ReleaseComObject(imlJumbo);
                    imlJumbo = null;
                }
            }
        }

        public static IntPtr GetIconByFilename(string fileName, IconSize size)
        {
            return GetIcon(fileName, size);
        }

        public static IntPtr GetIconByPidl(IntPtr pidl, IconSize size)
        {
            return GetIcon(pidl, size);
        }

        private static IntPtr GetIcon(string filename, IconSize size)
        {
            lock (ComLock)
            {
                try
                {
                    filename = translateIconExceptions(filename);

                    SHFILEINFO shinfo = new SHFILEINFO();
                    shinfo.szDisplayName = string.Empty;
                    shinfo.szTypeName = string.Empty;

                    if (!filename.StartsWith("\\") && (File.GetAttributes(filename) & FileAttributes.Directory) == FileAttributes.Directory)
                    {
                        SHGetFileInfo(filename, FILE_ATTRIBUTE_NORMAL | FILE_ATTRIBUTE_DIRECTORY, ref shinfo, (uint)Marshal.SizeOf(shinfo), (uint)(SHGFI.SysIconIndex));
                    }
                    else
                    {
                        SHGetFileInfo(filename, FILE_ATTRIBUTE_NORMAL, ref shinfo, (uint)Marshal.SizeOf(shinfo), (uint)(SHGFI.UseFileAttributes | SHGFI.SysIconIndex));
                    }

                    return getFromImageList(shinfo.iIcon, size);
                }
                catch
                {
                    return IntPtr.Zero;
                }
            }
        }

        private static IntPtr GetIcon(IntPtr pidl, IconSize size)
        {
            lock (ComLock)
            {
                try
                {
                    SHFILEINFO shinfo = new SHFILEINFO();
                    shinfo.szDisplayName = string.Empty;
                    shinfo.szTypeName = string.Empty;

                    SHGetFileInfo(pidl, FILE_ATTRIBUTE_NORMAL, ref shinfo, (uint)Marshal.SizeOf(shinfo), (uint)(SHGFI.PIDL | SHGFI.SysIconIndex));

                    return getFromImageList(shinfo.iIcon, size);
                }
                catch
                {
                    return IntPtr.Zero;
                }
            }
        }

        private static IntPtr getFromImageList(int iconIndex, IconSize size)
        {
            // Initialize the IImageList object
            initIml(size);

            IntPtr hIcon = IntPtr.Zero;
            int ILD_TRANSPARENT = 1;

            switch (size)
            {
                case IconSize.Large:
                    imlLarge.GetIcon(iconIndex, ILD_TRANSPARENT, ref hIcon);
                    break;
                case IconSize.Small:
                    imlSmall.GetIcon(iconIndex, ILD_TRANSPARENT, ref hIcon);
                    break;
                case IconSize.ExtraLarge:
                    imlExtraLarge.GetIcon(iconIndex, ILD_TRANSPARENT, ref hIcon);
                    break;
                case IconSize.Jumbo:
                    imlJumbo.GetIcon(iconIndex, ILD_TRANSPARENT, ref hIcon);
                    break;
            }

            return hIcon;
        }

        private static string translateIconExceptions(string filename)
        {
            if (filename.EndsWith(".settingcontent-ms"))
            {
                return Environment.GetEnvironmentVariable("WINDIR") + "\\ImmersiveControlPanel\\SystemSettings.exe";
            }

            return filename;
        }

        public static IconSize ParseSize(int size)
        {
            if (Enum.IsDefined(typeof(IconSize), size))
            {
                return (IconSize)size;
            }
            
            return IconSize.Small;
        }

        public static int GetSize(int size)
        {
            return GetSize(ParseSize(size));
        }

        public static int GetSize(IconSize size)
        {
            switch (size)
            {
                case IconSize.Large:
                    return 32;
                case IconSize.Small:
                    return 16;
                case IconSize.Medium:
                    return 24;
                case IconSize.ExtraLarge:
                    return 48;
                case IconSize.Jumbo:
                    return 96;
                default:
                    return 16;
            }
        }
    }
}

// ----- Common\Helpers\IconImageConverter.cs -----
// Taken from ManagedShell (https://github.com/cairoshell/ManagedShell), Apache License 2.0.
// Changed for HeMiDock: the icons are read into pixels (ShellImage) by calls of Windows instead of WPF bitmaps.
namespace ManagedShell.Common.Helpers
{
    /// <summary>
    /// Provides static conversion methods to change Win32 icons and bitmaps into pictures (premultiplied BGRA pixels).
    /// </summary>
    public class IconImageConverter
    {
        [StructLayout(LayoutKind.Sequential)]
        struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

        [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bmp);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        /// <summary>Retrieves the icon for the file name.</summary>
        /// <param name="size">0 = 32px, 1 = 16px, 2 = 48px</param>
        public static ShellImage GetImageFromAssociatedIcon(string filename, IconSize size) =>
            GetImageFromHIcon(IconHelper.GetIconByFilename(filename, size));

        /// <summary>Retrieves the icon for the absolute PIDL.</summary>
        public static ShellImage GetImageFromAssociatedIcon(IntPtr pidl, IconSize size) =>
            GetImageFromHIcon(IconHelper.GetIconByPidl(pidl, size));


        /// <summary>The picture of an icon (HICON, destroyed afterwards); null (or an empty picture) when there is none.</summary>
        public static ShellImage GetImageFromHIcon(IntPtr hIcon, bool returnDefault = true)
        {
            ShellImage image = null;
            if (hIcon != IntPtr.Zero)
            {
                try { image = FromIcon(hIcon); }
                catch { }
                finally { NativeMethods.DestroyIcon(hIcon); }
            }
            return image ?? (returnDefault ? GetDefaultIcon() : null);
        }

        /// <summary>The picture of an icon without destroying it.</summary>
        public static ShellImage FromIcon(IntPtr hIcon)
        {
            if (!GetIconInfo(hIcon, out var info)) return null;
            try
            {
                if (info.hbmColor != IntPtr.Zero) return Read(info.hbmColor, info.hbmMask, premultiplied: false);
                // a monochrome icon: the mask holds the AND part above the XOR part
                return ReadMonochrome(info.hbmMask);
            }
            finally
            {
                if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
                if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            }
        }

        static byte[] Bits(IntPtr bitmap, int w, int h)
        {
            var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var bits = new byte[w * h * 4];
            var dc = GetDC(IntPtr.Zero);
            try { return GetDIBits(dc, bitmap, 0, (uint)h, bits, ref bmi, 0) == 0 ? null : bits; }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }

        /// <summary>A colour bitmap as premultiplied BGRA; without an alpha channel the mask makes it (white = transparent).</summary>
        static ShellImage Read(IntPtr color, IntPtr mask, bool premultiplied)
        {
            if (GetObject(color, Marshal.SizeOf<BITMAP>(), out var bmp) == 0) return null;
            int w = bmp.bmWidth, h = Math.Abs(bmp.bmHeight);
            if (w <= 0 || h <= 0) return null;
            var px = Bits(color, w, h);
            if (px == null) return null;
            bool alpha = false;
            for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { alpha = true; break; }
            if (!alpha)
            {
                var m = mask != IntPtr.Zero ? Bits(mask, w, h) : null;
                for (int i = 0; i < px.Length; i += 4) px[i + 3] = m == null || m[i] == 0 ? (byte)255 : (byte)0;
                premultiplied = false;
            }
            if (!premultiplied)
                for (int i = 0; i < px.Length; i += 4)
                {
                    int a = px[i + 3];
                    if (a == 255) continue;
                    px[i] = (byte)(px[i] * a / 255);
                    px[i + 1] = (byte)(px[i + 1] * a / 255);
                    px[i + 2] = (byte)(px[i + 2] * a / 255);
                }
            return new ShellImage(px, w, h);
        }

        static ShellImage ReadMonochrome(IntPtr mask)
        {
            if (mask == IntPtr.Zero || GetObject(mask, Marshal.SizeOf<BITMAP>(), out var bmp) == 0) return null;
            int w = bmp.bmWidth, h = Math.Abs(bmp.bmHeight) / 2;
            var all = Bits(mask, w, h * 2);
            if (all == null || h <= 0) return null;
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int andAt = (y * w + x) * 4, xorAt = ((y + h) * w + x) * 4, to = (y * w + x) * 4;
                    bool transparent = all[andAt] != 0;
                    byte v = all[xorAt];
                    px[to] = px[to + 1] = px[to + 2] = transparent ? (byte)0 : v;
                    px[to + 3] = transparent ? (byte)0 : (byte)255;
                }
            return new ShellImage(px, w, h);
        }

        /// <summary>An empty picture in the size of a small icon (the default when an icon is missing).</summary>
        public static ShellImage GetDefaultIcon() => new ShellImage(new byte[16 * 16 * 4], 16, 16);
    }
}

// ----- Common\Helpers\KeyboardLayoutHelper.cs -----
namespace ManagedShell.Common.Helpers
{
    public static class KeyboardLayoutHelper
    {
        private static uint GetFocusedThreadId()
        {
            var gti = new GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));

            // Some apps (e.g. Electron/WebView2 apps such as Teams, and WinUI3 apps such as the
            // modern Notepad) keep their real keyboard focus on a child window that belongs to a
            // different thread than the top-level foreground window. GetGUIThreadInfo(0, ...)
            // resolves the true focused window first.
            // idThread 0 asks for the foreground thread's queue; hwndFocus within it is the
            // window that truly owns keyboard focus, which may belong to a different thread
            // than the top-level foreground window itself.
            if (GetGUIThreadInfo(0, ref gti))
            {
                IntPtr focusedWindow = gti.hwndFocus != IntPtr.Zero ? gti.hwndFocus : gti.hwndActive;

                if (focusedWindow != IntPtr.Zero)
                {
                    uint focusedThreadId = GetWindowThreadProcessId(focusedWindow, out _);

                    if (focusedThreadId != 0)
                    {
                        return focusedThreadId;
                    }
                }
            }

            return GetWindowThreadProcessId(GetForegroundWindow(), out _);
        }

        public static KeyboardLayout GetKeyboardLayout(bool currentThread = false)
        {
            uint threadId = 0;
            if (!currentThread)
                threadId = GetFocusedThreadId();
            var layout = NativeMethods.GetKeyboardLayout(threadId);

            return new KeyboardLayout()
            {
                HKL = layout,
                NativeName = CultureInfo.GetCultureInfo((short)layout).NativeName,
                ThreeLetterName = CultureInfo.GetCultureInfo((short)layout).ThreeLetterISOLanguageName.ToUpper()
            };
        }

        public static List<KeyboardLayout> GetKeyboardLayoutList()
        {
            var size = NativeMethods.GetKeyboardLayoutList(0, null);
            var result = new long[size];
            NativeMethods.GetKeyboardLayoutList(size, result);

            return result.Select(x => new KeyboardLayout()
            {
                HKL = (int)x,
                NativeName = CultureInfo.GetCultureInfo((short)x).NativeName,
                ThreeLetterName = CultureInfo.GetCultureInfo((short)x).ThreeLetterISOLanguageName.ToUpper()
            }).ToList();
        }

    }
}

// ----- Common\Helpers\MouseHelper.cs -----
// Taken from ManagedShell (https://github.com/cairoshell/ManagedShell), Apache License 2.0.
// Changed for HeMiDock: the place of the mouse from Windows itself instead of Windows Forms.
namespace ManagedShell.Common.Helpers
{
    public static class MouseHelper
    {
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);

        public static uint GetCursorPositionParam()
        {
            GetCursorPos(out var p);
            return (uint)NativeMethods.MakeLParam(p.X, p.Y);
        }
    }
}

// ----- Common\Helpers\ScreenHelper.cs -----
namespace ManagedShell.Common.Helpers
{
    public static class ScreenHelper
    {
        public static Size PrimaryMonitorDeviceSize => new Size(NativeMethods.GetSystemMetrics(0), NativeMethods.GetSystemMetrics(1));
    }
}

// ----- Common\Helpers\ShellHelper.cs -----
namespace ManagedShell.Common.Helpers
{
    public static class ShellHelper
    {
        private const int MAX_PATH = 260;

        public static string GetDisplayName(string filename)
        {
            SHFILEINFO shinfo = new SHFILEINFO();
            shinfo.szDisplayName = string.Empty;
            shinfo.szTypeName = string.Empty;
            SHGetFileInfo(filename, FILE_ATTRIBUTE_NORMAL, ref shinfo, (uint)Marshal.SizeOf(shinfo), (uint)(SHGFI.DisplayName));

            return shinfo.szDisplayName;
        }

        public static string UsersStartMenuPath => GetSpecialFolderPath((int)CSIDL.CSIDL_STARTMENU);

        public static string AllUsersStartMenuPath => GetSpecialFolderPath((int)CSIDL.CSIDL_COMMON_STARTMENU);

        public static string GetSpecialFolderPath(int FOLDER)
        {
            StringBuilder sbPath = new StringBuilder(MAX_PATH);
            SHGetFolderPath(IntPtr.Zero, FOLDER, IntPtr.Zero, 0, sbPath);
            return sbPath.ToString();
        }


        public static bool ActivateApplication(string appUserModelId, string args = "")
        {
            var aam = new ApplicationActivationManager() as IApplicationActivationManager;
            try
            {
                aam.ActivateApplication(appUserModelId, args, ActivateOptions.None);
                return true;
            }
            catch (Exception ex)
            {
                ShellLogger.Error($"Error activating application {appUserModelId} with args \"{args}\". ({ex.Message})");
                return false;
            }
        }


        public static bool Exists(string filename)
        {
            foreach (char invalid in Path.GetInvalidPathChars())
            {
                if (filename.Contains(invalid.ToString()))
                {
                    return false;
                }
            }

            return !filename.StartsWith("\\\\") && (File.Exists(filename) || Directory.Exists(filename));
        }









        public static void ShowActionCenter()
        {
            ShellKeyCombo(VK.LWIN, VK.KEY_A);
        }


		


        /// <summary>
        /// Send file to recycle bin
        /// </summary>
        /// <param name="path">Location of directory or file to recycle</param>
        /// <param name="flags">FileOperationFlags to add in addition to FOF_ALLOWUNDO</param>
        public static bool SendToRecycleBin(string path, FileOperationFlags flags)
        {
            try
            {
                var fs = new SHFILEOPSTRUCT
                {
                    wFunc = FileOperationType.FO_DELETE,
                    pFrom = path + '\0' + '\0',
                    fFlags = FileOperationFlags.FOF_ALLOWUNDO | flags
                };
                SHFileOperation(ref fs);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Send file to recycle bin.  Display dialog, display warning if files are too big to fit (FOF_WANTNUKEWARNING)
        /// </summary>
        /// <param name="path">Location of directory or file to recycle</param>
        public static bool SendToRecycleBin(string path)
        {
            return SendToRecycleBin(path, FileOperationFlags.FOF_NOCONFIRMATION | FileOperationFlags.FOF_WANTNUKEWARNING);
        }

        

        public static string GetPathForWindowHandle(IntPtr hWnd)
        {
            StringBuilder outFileName = new StringBuilder(1024);

            // get process id
            var procId = GetProcIdForHandle(hWnd);

            if (procId != 0)
            {
                // open process
                // QueryLimitedInformation flag allows us to access elevated applications as well
                IntPtr hProc = OpenProcess(ProcessAccessFlags.QueryLimitedInformation, false, (int) procId);

                // get path
                int len = outFileName.Capacity;
                QueryFullProcessImageName(hProc, 0, outFileName, ref len);
                CloseHandle(hProc);
            }

            return outFileName.ToString();
        }

        public static uint GetProcIdForHandle(IntPtr hWnd)
        {
            uint procId;
            GetWindowThreadProcessId(hWnd, out procId);
            return procId;
        }

        public static string GetAppUserModelIdPropertyForHandle(IntPtr hWnd)
        {
            string aumid = string.Empty;

            IPropertyStore propStore;
            PropVariant prop;

            var g = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
            SHGetPropertyStoreForWindow(hWnd, ref g, out propStore);

            PROPERTYKEY PKEY_AppUserModel_ID = new PROPERTYKEY
            {
                fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
                pid = 5
            };

            if (propStore != null)
            {
                propStore.GetValue(PKEY_AppUserModel_ID, out prop);

                try
                {
                    aumid = prop.Value.ToString();
                }
                catch
                { }

                prop.Clear();
            }

            return aumid;
        }


        /// <summary>
        /// Calls the LockWorkStation method on the User32 API.
        /// </summary>
        public static void Lock()
        {
            LockWorkStation();
        }

        /// <summary>
        /// Calls the logoff method on the Win32 API.
        /// </summary>
        public static void Logoff()
        {
            ExitWindowsEx((uint)ExitWindows.Logoff, 0x0);
        }


        private static void ShellKeyCombo(VK wVk_1, VK wVk_2)
        {
            INPUT[] inputs = new INPUT[4];

            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].mkhi.ki.time = 0;
            inputs[0].mkhi.ki.wScan = 0;
            inputs[0].mkhi.ki.dwExtraInfo = GetMessageExtraInfo();
            inputs[0].mkhi.ki.wVk = (ushort)wVk_1;
            inputs[0].mkhi.ki.dwFlags = 0;

            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].mkhi.ki.wScan = 0;
            inputs[1].mkhi.ki.dwExtraInfo = GetMessageExtraInfo();
            inputs[1].mkhi.ki.wVk = (ushort)wVk_2;
            inputs[1].mkhi.ki.dwFlags = 0;

            inputs[2].type = INPUT_KEYBOARD;
            inputs[2].mkhi.ki.wScan = 0;
            inputs[2].mkhi.ki.dwExtraInfo = GetMessageExtraInfo();
            inputs[2].mkhi.ki.wVk = (ushort)wVk_2;
            inputs[2].mkhi.ki.dwFlags = KEYEVENTF_KEYUP;

            inputs[3].type = INPUT_KEYBOARD;
            inputs[3].mkhi.ki.wScan = 0;
            inputs[3].mkhi.ki.dwExtraInfo = GetMessageExtraInfo();
            inputs[3].mkhi.ki.wVk = (ushort)wVk_1;
            inputs[3].mkhi.ki.dwFlags = KEYEVENTF_KEYUP;

            SendInput(4, inputs, Marshal.SizeOf(typeof(INPUT)));
        }


    }
}

// ----- Common\Helpers\SoundHelper.cs -----
namespace ManagedShell.Common.Helpers
{
    public class SoundHelper
    {
        private const string SYSTEM_SOUND_ROOT_KEY = @"AppEvents\Schemes\Apps";

        /// <summary>
        /// Flag values for playing the sound.
        /// </summary>
        [Flags]
        public enum SND : uint
        {
            /// <summary>
            /// The sound is played synchronously; PlaySound returns after the sound event completes (default behavior).
            /// </summary>
            SYNC = 0x0000,

            /// <summary>
            /// The sound is played asynchronously; PlaySound returns immediately after initiating the sound.
            /// To stop an asynchronously played sound, call PlaySound with pszSound set to NULL.
            /// </summary>
            ASYNC = 0x00000001,

            /// <summary>
            /// No default sound event is used. If the sound is not found, PlaySound returns without playing a sound.
            /// </summary>
            NODEFAULT = 0x00000002,

            /// <summary>
            /// The pszSound parameter points to a sound loaded in memory.
            /// </summary>
            MEMORY = 0x00000004,

            /// <summary>
            /// The sound plays repeatedly until PlaySound is called with pszSound set to NULL.
            /// Use the ASYNC flag with LOOP.
            /// </summary>
            LOOP = 0x00000008,


            /// <summary>
            /// The pszSound parameter is a system-event alias from the registry or WIN.INI file.
            /// Do not use with FILENAME or RESOURCE.
            /// </summary>
            ALIAS = 0x00010000,

            /// <summary>
            /// The pszSound parameter is a predefined identifier for a system-event alias.
            /// </summary>
            ALIAS_ID = 0x00110000,

            /// <summary>
            /// The pszSound parameter is a file name. If the file is not found, the default sound is played unless NODEFAULT is set.
            /// </summary>
            FILENAME = 0x00020000,

            /// <summary>
            /// The pszSound parameter is a resource identifier; hmod must identify the instance that contains the resource.
            /// </summary>
            RESOURCE = 0x00040004,

            /// <summary>
            /// The pszSound parameter is an application-specific alias in the registry.
            /// Can be combined with ALIAS or ALIAS_ID to specify an application-defined sound alias.
            /// </summary>
            APPLICATION = 0x00000080,


            /// <summary>
            /// Treats the sound as a ring from a communications app.
            /// </summary>
            RING = 0x00100000,

            /// <summary>
            /// Requires Windows Vista or later. If set, the sound is assigned to the audio session for system notification sounds,
            /// allowing control via the system volume slider. Otherwise, it is assigned to the application's default audio session.
            /// </summary>
            SYSTEM = 0x00200000,
        }

        private const SND DEFAULT_SYSTEM_SOUND_FLAGS = SND.ASYNC | SND.NODEFAULT | SND.SYSTEM;

        [DllImport("winmm.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool PlaySound(string pszSound, IntPtr hmod, SND soundFlags);

        /// <summary>
        /// Plays the specified system sound using the audio session for system notification sounds.
        /// </summary>
        /// <param name="app">The name of the app that the sound belongs to. For example, ".Default" contains system sounds, "Explorer" contains Explorer sounds.</param>
        /// <param name="name">The name of the system sound to play.</param>
        public static bool PlaySystemSound(string app, string name)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"{SYSTEM_SOUND_ROOT_KEY}\{app}\{name}\.Current");
                if (key == null)
                {
                    ShellLogger.Debug($"SoundHelper: Unable to find sound {name} for app {app}");
                    return false;
                }

                var soundFileName = key.GetValue(null) as string;
                if (string.IsNullOrEmpty(soundFileName))
                {
                    ShellLogger.Debug($"SoundHelper: Missing file for sound {name} for app {app}");
                    return false;
                }

                return PlaySound(soundFileName, IntPtr.Zero, DEFAULT_SYSTEM_SOUND_FLAGS | SND.FILENAME);
            }
            catch (Exception e)
            {
                ShellLogger.Debug($"SoundHelper: Unable to play sound {name} for app {app}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Plays the specified system sound using the audio session for system notification sounds.
        /// </summary>
        /// <param name="alias">The name of the system sound for ".Default" to play.</param>
        public static bool PlaySystemSound(string alias)
        {
            try
            {
                return PlaySound(alias, IntPtr.Zero, DEFAULT_SYSTEM_SOUND_FLAGS | SND.ALIAS);
            }
            catch (Exception e)
            {
                ShellLogger.Debug($"SoundHelper: Unable to play sound {alias}: {e.Message}");
                return false;
            }
        }


    }
}

// ----- Common\Helpers\VolumeHelper.cs -----
namespace ManagedShell.Common.Helpers
{
    public static class VolumeHelper
    {
        // From Simon Mourier https://stackoverflow.com/a/31042902



        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator
        {
        }



    }
}

// ----- Common\Helpers\WindowHelper.cs -----
namespace ManagedShell.Common.Helpers
{
    public static class WindowHelper
    {
        public const string TrayWndClass = "Shell_TrayWnd";





        





        public static IntPtr FindWindowsTray(IntPtr hwndIgnore)
        {
            IntPtr taskbarHwnd = FindWindow(TrayWndClass, "");

            if (hwndIgnore != IntPtr.Zero)
            {
                while (taskbarHwnd == hwndIgnore)
                {
                    taskbarHwnd = FindWindowEx(IntPtr.Zero, taskbarHwnd, TrayWndClass, "");
                }
            }

            return taskbarHwnd;
        }
    }
}
