// Taken from ManagedShell (https://github.com/cairoshell/ManagedShell), Apache License 2.0; changed for HeMiDock to run
// without WPF and Windows Forms where needed (pictures as pixels, own dispatcher, timer and message window).
// The files of one area of ManagedShell joined into one (their names are kept as headings).

using ManagedShell.Common.Helpers;
using ManagedShell.Common.Logging;
using ManagedShell.WindowsTray;
using static ManagedShell.Interop.NativeMethods;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System;

// ----- AppBar\AppBarEdge.cs -----
namespace ManagedShell.AppBar
{
    public enum AppBarEdge : int
    {
        Left = 0,
        Top,
        Right,
        Bottom
    }
}

// ----- AppBar\AppBarManager.cs -----
namespace ManagedShell.AppBar
{
    public partial class AppBarManager : IDisposable
    {
        private readonly ExplorerHelper _explorerHelper;
        private AppBarMessageDelegate _appBarMessageDelegate;

        // HeMiDock: no AppBar windows of its own here (its docks reserve their room themselves); only whether one of them
        // hides by itself, for the apps that ask
        public Func<bool> AutoHideActive { get; set; }

        public AppBarManager(ExplorerHelper explorerHelper)
        {
            _appBarMessageDelegate = appBarMessageDelegate;
            _explorerHelper = explorerHelper;

            _explorerHelper._notificationArea?.SetAppBarMessageCallback(_appBarMessageDelegate);
        }


        private IntPtr appBarMessageDelegate(APPBARMSGDATAV3 amd, ref bool handled)
        {
            // only handle certain messages, send other AppBar messages to default handler
            switch ((ABMsg)amd.dwMessage)
            {
                case ABMsg.ABM_GETTASKBARPOS:
                    return appBarMessage_GetTaskbarPos(amd, ref handled);
                case ABMsg.ABM_QUERYPOS:
                case ABMsg.ABM_SETPOS:
                    return appBarMessage_QuerySetPos(amd, ref handled);
                case ABMsg.ABM_GETSTATE:
                    return appBarMessage_GetState(amd, ref handled);
                case ABMsg.ABM_GETAUTOHIDEBAR:
                case ABMsg.ABM_GETAUTOHIDEBAREX:
                    return appBarMessage_GetAutoHideBar(amd, ref handled);
                case ABMsg.ABM_ACTIVATE:
                case ABMsg.ABM_WINDOWPOSCHANGED:
                    handled = true;
                    return (IntPtr)1;
            }
            return IntPtr.Zero;
        }

        #region AppBar message handlers
        private IntPtr appBarMessage_GetTaskbarPos(APPBARMSGDATAV3 amd, ref bool handled)
        {
            IntPtr hShared = SHLockShared((IntPtr)amd.hSharedMemory, (uint)amd.dwSourceProcessId);
            APPBARDATAV2 abd = (APPBARDATAV2)Marshal.PtrToStructure(hShared, typeof(APPBARDATAV2));

            if (_explorerHelper._notificationArea != null)
            {
                _explorerHelper._notificationArea.FillTrayHostSizeData(ref abd);
            }

            Marshal.StructureToPtr(abd, hShared, false);
            SHUnlockShared(hShared);
            handled = true;
            return (IntPtr)1;
        }

        private IntPtr appBarMessage_QuerySetPos(APPBARMSGDATAV3 amd, ref bool handled)
        {
            // These two messages use shared memory, and forwarding over the message as-is doesn't
            // seem to allow Explorer to access the shared memory. Here we grab the existing (old)
            // shared memory and allocate it into new shared memory, then update AppBarMessageData
            // and forward it on to Explorer.

            if (EnvironmentHelper.IsAppRunningAsShell)
            {
                // some day we will manage AppBars if we are shell, but today is not that day.
                return IntPtr.Zero;
            }

            // Get Explorer tray handle and PID
            IntPtr ignoreHwnd = IntPtr.Zero;
            IntPtr explorerTray;

            if (_explorerHelper._notificationArea != null)
            {
                ignoreHwnd = _explorerHelper._notificationArea.Handle;
            }
            explorerTray = WindowHelper.FindWindowsTray(ignoreHwnd);

            GetWindowThreadProcessId(explorerTray, out uint explorerPid);

            // recreate shared memory so that Explorer gets access to it
            IntPtr hSharedOld = SHLockShared((IntPtr)amd.hSharedMemory, (uint)amd.dwSourceProcessId);
            IntPtr hSharedNew = SHAllocShared(IntPtr.Zero, (uint)Marshal.SizeOf(typeof(APPBARDATAV2)), explorerPid);

            // Copy the data from the old shared memory into the new
            IntPtr hSharedData = SHLockShared(hSharedNew, explorerPid);
            if (hSharedData == IntPtr.Zero)
            {
                // Failed, bail out bail out!
                SHFreeShared(hSharedNew, explorerPid);
                return IntPtr.Zero;
            }

            APPBARDATAV2 abdOld = (APPBARDATAV2)Marshal.PtrToStructure(hSharedOld, typeof(APPBARDATAV2));
            Marshal.StructureToPtr(abdOld, hSharedData, false);
            SHUnlockShared(hSharedData);

            // Update AppBarMessageData with the new shared memory handle and PID
            amd.hSharedMemory = (long)hSharedNew;
            amd.dwSourceProcessId = (int)explorerPid;

            // Prepare structs to send onward
            IntPtr hAmd = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(APPBARMSGDATAV3)));
            Marshal.StructureToPtr(amd, hAmd, false);

            COPYDATASTRUCT copyData = new COPYDATASTRUCT
            {
                cbData = Marshal.SizeOf(typeof(APPBARMSGDATAV3)),
                dwData = (IntPtr)0,
                lpData = hAmd
            };
            IntPtr hCopyData = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(COPYDATASTRUCT)));
            Marshal.StructureToPtr(copyData, hCopyData, false);

            IntPtr result = SendMessage(explorerTray, (int)WM.COPYDATA, (IntPtr)amd.abd.hWnd, hCopyData);
            handled = true;

            // It's possible that Explorer modified the data we sent, so read the data back out.
            IntPtr hSharedFromExplorer = SHLockShared(hSharedNew, explorerPid);
            if (hSharedFromExplorer != IntPtr.Zero)
            {
                APPBARDATAV2 abdNew = (APPBARDATAV2)Marshal.PtrToStructure(hSharedFromExplorer, typeof(APPBARDATAV2));
                SHUnlockShared(hSharedFromExplorer);

                Marshal.StructureToPtr(abdNew, hSharedOld, false);
                SHUnlockShared(hSharedOld);
            }

            SHFreeShared(hSharedNew, explorerPid);

            return result;
        }

        private IntPtr appBarMessage_GetState(APPBARMSGDATAV3 amd, ref bool handled)
        {
            if (_explorerHelper._notificationArea != null && amd.abd.hWnd == (uint)WindowHelper.FindWindowsTray(_explorerHelper._notificationArea.Handle))
            {
                // If the Explorer AppBar is being queried specifically, forward the request on to it.
                // ExplorerHelper queries this to get the pre-existing taskbar state before hiding.
                return IntPtr.Zero;
            }

            handled = true;

            if (AutoHideActive?.Invoke() == true)
            {
                return (IntPtr)ABState.AutoHide;
            }

            return (IntPtr)ABState.Default;
        }

        private IntPtr appBarMessage_GetAutoHideBar(APPBARMSGDATAV3 amd, ref bool handled)
        {
            handled = true;

            if (AutoHideActive?.Invoke() == true)
            {
                // Return the notification area hwnd instead of the AppBar's. Why?
                // Some apps (Firefox) check the class of the AppBar matches Shell_TrayWnd.
                // However, an AppBarWindow should be providing its coordinates to a
                // NotificationArea to position it appropriately anyway.
                return _explorerHelper?._notificationArea?.Handle ?? IntPtr.Zero;
            }

            return IntPtr.Zero;
        }
        #endregion

        public void Dispose()
        {
        }
    }
}

// ----- AppBar\AppBarMode.cs -----
namespace ManagedShell.AppBar
{
    public enum AppBarMode : int
    {
        Normal = 0,
        AutoHide,
        None
    }
}

// ----- AppBar\ExplorerHelper.cs -----
namespace ManagedShell.AppBar
{
    public class ExplorerHelper
    {
        private static ABState? startupTaskbarState;
        internal NotificationArea _notificationArea;


        private readonly ShellTimer taskbarMonitor = new ShellTimer();

        private bool _hideExplorerTaskbar;

        public bool HideExplorerTaskbar
        {
            get => _hideExplorerTaskbar;

            set
            {
                if (value != _hideExplorerTaskbar && !EnvironmentHelper.IsAppRunningAsShell)
                {
                    _hideExplorerTaskbar = value;

                    if (_hideExplorerTaskbar)
                    {
                        HideTaskbar();
                    }
                    else
                    {
                        ShowTaskbar();
                    }
                }
            }
        }

        public ExplorerHelper() : this(null)
        {
        }

        public ExplorerHelper(NotificationArea notificationArea)
        {
            _notificationArea = notificationArea;

            SetupTaskbarMonitor();
        }

        public void SetTaskbarVisibility(int swp)
        {
            // only run this if our TaskBar is enabled, or if we are showing the Windows TaskBar
            if (swp != (int)SetWindowPosFlags.SWP_HIDEWINDOW || HideExplorerTaskbar)
            {
                IntPtr taskbarHwnd = WindowHelper.FindWindowsTray(getNotifyAreaHandle());
                IntPtr startButtonHwnd = FindWindowEx(IntPtr.Zero, IntPtr.Zero, (IntPtr)0xC017, null);

                if (taskbarHwnd != IntPtr.Zero
                    && swp == (int)SetWindowPosFlags.SWP_HIDEWINDOW == IsWindowVisible(taskbarHwnd))
                {
                    SetWindowPos(taskbarHwnd, (IntPtr)WindowZOrder.HWND_BOTTOM, 0, 0, 0, 0, swp | (int)SetWindowPosFlags.SWP_NOMOVE | (int)SetWindowPosFlags.SWP_NOSIZE | (int)SetWindowPosFlags.SWP_NOACTIVATE);
                    if (startButtonHwnd != IntPtr.Zero)
                    {
                        SetWindowPos(startButtonHwnd, (IntPtr)WindowZOrder.HWND_BOTTOM, 0, 0, 0, 0, swp | (int)SetWindowPosFlags.SWP_NOMOVE | (int)SetWindowPosFlags.SWP_NOSIZE | (int)SetWindowPosFlags.SWP_NOACTIVATE);
                    }
                }

                // adjust secondary TaskBars for multi-monitor
                SetSecondaryTaskbarVisibility(swp);
            }
        }

        public void SetSecondaryTaskbarVisibility(int swp)
        {
            IntPtr secTaskbarHwnd = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null);

            // if we have 3+ monitors there may be multiple secondary TaskBars
            while (secTaskbarHwnd != IntPtr.Zero)
            {
                if (swp == (int)SetWindowPosFlags.SWP_HIDEWINDOW == IsWindowVisible(secTaskbarHwnd))
                {
                    SetWindowPos(secTaskbarHwnd, (IntPtr)WindowZOrder.HWND_BOTTOM, 0, 0, 0, 0, swp | (int)SetWindowPosFlags.SWP_NOMOVE | (int)SetWindowPosFlags.SWP_NOSIZE | (int)SetWindowPosFlags.SWP_NOACTIVATE);
                }

                secTaskbarHwnd = FindWindowEx(IntPtr.Zero, secTaskbarHwnd, "Shell_SecondaryTrayWnd", null);
            }
        }

        public void SetTaskbarState(ABState state)
        {
            APPBARDATA abd = new APPBARDATA
            {
                cbSize = Marshal.SizeOf(typeof(APPBARDATA)),
                hWnd = WindowHelper.FindWindowsTray(getNotifyAreaHandle()),
                lParam = (IntPtr)state
            };

            SHAppBarMessage((int)ABMsg.ABM_SETSTATE, ref abd);
        }

        public ABState GetTaskbarState()
        {
            APPBARDATA abd = new APPBARDATA
            {
                cbSize = Marshal.SizeOf(typeof(APPBARDATA)),
                hWnd = WindowHelper.FindWindowsTray(getNotifyAreaHandle())
            };

            uint uState = SHAppBarMessage((int)ABMsg.ABM_GETSTATE, ref abd);

            return (ABState)uState;
        }

        private IntPtr getNotifyAreaHandle()
        {
            if (_notificationArea == null) return IntPtr.Zero;

            return _notificationArea.Handle;
        }

        private void HideTaskbar()
        {
            if (startupTaskbarState == null)
            {
                startupTaskbarState = GetTaskbarState();
            }

            if (HideExplorerTaskbar)
            {
                DoHideTaskbar();
                taskbarMonitor.Start();
            }
        }

        private void DoHideTaskbar()
        {
            SetTaskbarState(ABState.AutoHide);
            SetTaskbarVisibility((int)SetWindowPosFlags.SWP_HIDEWINDOW);
        }

        private void ShowTaskbar()
        {
            SetTaskbarState(startupTaskbarState ?? ABState.Default);
            SetTaskbarVisibility((int)SetWindowPosFlags.SWP_SHOWWINDOW);
            taskbarMonitor.Stop();
        }

        private void SetupTaskbarMonitor()
        {
            taskbarMonitor.Interval = new TimeSpan(0, 0, 0, 0, 100);
            taskbarMonitor.Tick += TaskbarMonitor_Tick;
        }

        private void TaskbarMonitor_Tick(object sender, EventArgs e)
        {
            IntPtr taskbarHwnd = WindowHelper.FindWindowsTray(getNotifyAreaHandle());

            if (IsWindowVisible(taskbarHwnd))
            {
                ShellLogger.Debug("ExplorerHelper: Hiding unwanted Windows taskbar");
                DoHideTaskbar();
                return;
            }

            IntPtr secTaskbarHwnd = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null);

            // if we have 3+ monitors there may be multiple secondary TaskBars
            while (secTaskbarHwnd != IntPtr.Zero)
            {
                if (IsWindowVisible(secTaskbarHwnd))
                {
                    ShellLogger.Debug("ExplorerHelper: Hiding unwanted Windows taskbar");
                    DoHideTaskbar();
                    return;
                }

                secTaskbarHwnd = FindWindowEx(IntPtr.Zero, secTaskbarHwnd, "Shell_SecondaryTrayWnd", null);
            }
        }
    }
}

// ----- AppBar\ScreenSetupReason.cs -----
namespace ManagedShell.AppBar
{
    public enum ScreenSetupReason
    {
        DeviceChange,
        DisplayChange,
        DpiChange
    }
}
