// Taken from ManagedShell (https://github.com/cairoshell/ManagedShell), Apache License 2.0; changed for HeMiDock to run
// without WPF and Windows Forms where needed (pictures as pixels, own dispatcher, timer and message window).
// The files of one area of ManagedShell joined into one (their names are kept as headings).

using ManagedShell.Common.Enums;
using ManagedShell.Common.Helpers;
using ManagedShell.Common.Logging;
using ManagedShell.Common.SupportingClasses;
using ManagedShell.Interop;
using static ManagedShell.Interop.NativeMethods;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;

// ----- WindowsTasks\ApplicationWindow.cs -----
namespace ManagedShell.WindowsTasks
{
    [DebuggerDisplay("Title: {Title}, Handle: {Handle}")]
    public partial class ApplicationWindow : IEquatable<ApplicationWindow>, INotifyPropertyChanged, IDisposable
    {
        const int TITLE_LENGTH = 1024;
        private readonly TasksService _tasksService;
        StringBuilder titleBuilder = new StringBuilder(TITLE_LENGTH);

        public delegate void GetButtonRectEventHandler(ref NativeMethods.ShortRect rect);

        public event GetButtonRectEventHandler GetButtonRect;

        public ApplicationWindow(TasksService tasksService, IntPtr handle)
        {
            _tasksService = tasksService;
            Handle = handle;
            State = WindowState.Inactive;
        }

        public void Dispose()
        {
            // no longer required
        }

        public IntPtr Handle
        {
            get;
            set;
        }

        private string _appUserModelId = null;

        public string AppUserModelID
        {
            get
            {
                if (string.IsNullOrEmpty(_appUserModelId))
                {
                    _appUserModelId = ShellHelper.GetAppUserModelIdPropertyForHandle(Handle);
                }

                return _appUserModelId;
            }
        }

        private bool? _isUWP = null;

        public bool IsUWP
        {
            get
            {
                if (_isUWP == null)
                {
                    _isUWP = WinFileName.ToLower().Contains("applicationframehost.exe");
                }

                return (bool)_isUWP;
            }
        }

        private string _winFileName = "";

        public string WinFileName
        {
            get
            {
                if (string.IsNullOrEmpty(_winFileName))
                {
                    _winFileName = ShellHelper.GetPathForWindowHandle(Handle);
                }

                return _winFileName;
            }
        }

        private string _winFileDescription;

        public string WinFileDescription
        {
            get
            {
                if (string.IsNullOrEmpty(_winFileDescription))
                {
                    _winFileDescription = getFileDescription();
                }

                return _winFileDescription;
            }
        }

        private uint? _procId;

        public uint? ProcId => _procId = _procId ?? ShellHelper.GetProcIdForHandle(Handle);

        private string _category;

        public string Category
        {
            get
            {
                return _category;
            }
            set
            {
                if (value != _category)
                {
                    _category = value;
                    OnPropertyChanged("Category");
                }
            }
        }

        private string _className;

        public string ClassName
        {
            get
            {
                if (_className == null)
                {
                    setClassName();
                }

                return _className;
            }
        }

        private void setClassName()
        {
            string className = "";
            try
            {
                StringBuilder cName = new StringBuilder(256);
                NativeMethods.GetClassName(Handle, cName, cName.Capacity);
                className = cName.ToString();
            }
            catch { }

            if (_className != className)
            {
                _className = className;
                OnPropertyChanged("ClassName");
            }
        }

        private string _title;

        public string Title
        {
            get
            {
                if (_title == null)
                {
                    setTitle();
                }

                return _title;
            }
        }

        private void setTitle()
        {
            string title = "";
            try
            {
                titleBuilder.Clear();
                NativeMethods.GetWindowText(Handle, titleBuilder, TITLE_LENGTH + 1);

                title = titleBuilder.ToString();
            }
            catch { }

            if (_title != title)
            {
                _title = title;
                OnPropertyChanged("Title");
            }
        }

        private bool _iconLoading;
        private ShellImage _icon;
        private IntPtr _hIcon = IntPtr.Zero;

        public ShellImage Icon
        {
            get
            {
                if (_icon == null)
                    setIcon();

                return _icon;
            }
            set
            {
                _icon = value;
                OnPropertyChanged("Icon");
            }
        }
        
        private ShellImage _overlayIcon;

        public ShellImage OverlayIcon
        {
            get
            {
                return _overlayIcon;
            }
            private set
            {
                _overlayIcon = value;
                OnPropertyChanged("OverlayIcon");
            }
        }

        private string _overlayIconDescription;

        public string OverlayIconDescription
        {
            get
            {
                return _overlayIconDescription;
            }
            private set
            {
                _overlayIconDescription = value;
                OnPropertyChanged("OverlayIconDescription");
            }
        }

        private NativeMethods.TBPFLAG _progressState;

        public NativeMethods.TBPFLAG ProgressState
        {
            get
            {
                return _progressState;
            }

            set
            {
                _progressState = value;

                if (value == NativeMethods.TBPFLAG.TBPF_NOPROGRESS)
                {
                    ProgressValue = 0;
                }

                OnPropertyChanged("ProgressState");
            }
        }

        private int _progressValue;

        public int ProgressValue
        {
            get
            {
                return _progressValue;
            }

            set
            {
                _progressValue = value;
                OnPropertyChanged("ProgressValue");
            }
        }

        private WindowState _state;

        public WindowState State
        {
            get
            {
                return _state;
            }

            set
            {
                _state = value;
                OnPropertyChanged("State");
            }
        }

        private IntPtr _hMonitor;

        public IntPtr HMonitor
        {
            get
            {
                if (_hMonitor == IntPtr.Zero || EnvironmentHelper.IsWindows8OrBetter)
                {
                    // Ignore the cache on Windows 8+, as it may be wrong.
                    SetMonitor();
                }

                return _hMonitor;
            }

            private set
            {
                if (_hMonitor != value)
                {
                    if (_hMonitor != IntPtr.Zero)
                    {
                        ShellLogger.Debug($"ApplicationWindow: Monitor changed for {Handle} ({Title})");
                    }
                    _hMonitor = value;
                    OnPropertyChanged("HMonitor");
                }
            }
        }

        public bool IsMinimized
        {
            get { return NativeMethods.IsIconic(Handle); }
        }

        public NativeMethods.WindowShowStyle ShowStyle
        {
            get { return GetWindowShowStyle(Handle); }
        }

        public int WindowStyles
        {
            get
            {
                return NativeMethods.GetWindowLong(Handle, NativeMethods.WindowLongFlags.GWL_STYLE);
            }
        }

        public int ExtendedWindowStyles
        {
            get
            {
                return NativeMethods.GetWindowLong(Handle, NativeMethods.WindowLongFlags.GWL_EXSTYLE);
            }
        }

        public bool CanAddToTaskbar
        {
            get
            {
                int extendedWindowStyles = ExtendedWindowStyles;
                bool isWindow = NativeMethods.IsWindow(Handle);
                bool isVisible = NativeMethods.IsWindowVisible(Handle);
                bool isToolWindow = (extendedWindowStyles & (int)NativeMethods.ExtendedWindowStyles.WS_EX_TOOLWINDOW) != 0;
                bool isAppWindow = (extendedWindowStyles & (int)NativeMethods.ExtendedWindowStyles.WS_EX_APPWINDOW) != 0;
                bool isNoActivate = (extendedWindowStyles & (int)NativeMethods.ExtendedWindowStyles.WS_EX_NOACTIVATE) != 0;
                bool isDeleted = NativeMethods.GetProp(Handle, "ITaskList_Deleted") != IntPtr.Zero;
                IntPtr ownerWin = NativeMethods.GetWindow(Handle, NativeMethods.GetWindow_Cmd.GW_OWNER);

                return isWindow && isVisible && (ownerWin == IntPtr.Zero || isAppWindow) && (!isNoActivate || isAppWindow) && !isToolWindow && !isDeleted;
            }
        }

        public bool CanMinimize => (WindowStyles & (int)NativeMethods.WindowStyles.WS_MINIMIZEBOX) != 0 && NativeMethods.IsWindowEnabled(Handle);

        private bool? _showInTaskbar;

        // True if this window should be shown in the taskbar
        public bool ShowInTaskbar
        {
            get
            {
                if (_showInTaskbar == null)
                {
                    SetShowInTaskbar();
                }

                return (bool)_showInTaskbar;
            }
        }

        public void SetShowInTaskbar()
        {
            bool showInTaskbar = getShowInTaskbar();

            if (_showInTaskbar != showInTaskbar)
            {
                _showInTaskbar = showInTaskbar;

                // If we are becoming visible in the taskbar, get the category if it hasn't been set yet
                if (_showInTaskbar == true && Category == null)
                {
                    Category = _tasksService.TaskCategoryProvider?.GetCategory(this);
                }

                OnPropertyChanged("ShowInTaskbar");
            }
        }

        private bool getShowInTaskbar()
        {
            // EnumWindows and ShellHook return UWP app windows that are 'cloaked', which should not be visible in the taskbar.
            if (EnvironmentHelper.IsWindows8OrBetter)
            {
                int cbSize = Marshal.SizeOf(typeof(uint));
                NativeMethods.DwmGetWindowAttribute(Handle, NativeMethods.DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, out var cloaked, cbSize);

                if (cloaked > 0)
                {
                    ShellLogger.Debug($"ApplicationWindow: Cloaked window {Handle} ({Title}) hidden from taskbar");
                    return false;
                }

                // UWP shell windows that are not cloaked should be hidden from the taskbar, too.
                if (IsImmersiveShellWindow())
                {
                    ShellLogger.Debug($"ApplicationWindow: Hiding immersive shell window {Handle} ({Title}) from taskbar");
                    return false;
                }
            }

            return CanAddToTaskbar;
        }

        public bool IsImmersiveShellWindow()
        {
            if (!EnvironmentHelper.IsWindows8OrBetter)
            {
                return false;
            }
            
            if (ClassName == "ApplicationFrameWindow" || ClassName == "Windows.UI.Core.CoreWindow" || ClassName == "StartMenuSizingFrame" || ClassName == "Shell_LightDismissOverlay")
            {
                if ((ExtendedWindowStyles & (int)NativeMethods.ExtendedWindowStyles.WS_EX_WINDOWEDGE) == 0)
                {
                    return true;
                }
            }
            else if (!EnvironmentHelper.IsWindows10OrBetter && (ClassName == "ImmersiveBackgroundWindow" || ClassName == "SearchPane" || ClassName == "NativeHWNDHost" || ClassName == "Shell_CharmWindow" || ClassName == "ImmersiveLauncher") && WinFileName.ToLower().Contains("explorer.exe"))
            {
                return true;
            }

            return false;
        }

        private string getFileDescription()
        {
            string desc;

            try
            {
                desc = FileVersionInfo.GetVersionInfo(WinFileName).FileDescription;
            }
            catch (Exception e)
            {
                ShellLogger.Warning($"ApplicationWindow: Unable to get file description for {WinFileName} ({Title}): {e.Message}");
                desc = Title;
            }

            return desc;
        }

        private void setIcon()
        {
            if (!_iconLoading && ShowInTaskbar)
            {
                _iconLoading = true;

                Task.Factory.StartNew(() =>
                {
                    if (IsUWP && !string.IsNullOrEmpty(AppUserModelID))
                    {
                        // UWP apps
                        try
                        {
                            var storeApp = UWPInterop.StoreAppHelper.AppList.GetAppByAumid(AppUserModelID);

                            if (storeApp != null)
                            {
                                Icon = storeApp.GetIcon(_tasksService.TaskIconSize);
                            }
                            else
                            {
                                Icon = IconImageConverter.GetDefaultIcon();
                            }
                        }
                        catch
                        {
                            if (_icon == null) Icon = IconImageConverter.GetDefaultIcon();
                        }
                    }
                    else
                    {
                        // non-UWP apps
                        IntPtr hIco = default;
                        uint WM_GETICON = (uint)NativeMethods.WM.GETICON;
                        uint WM_QUERYDRAGICON = (uint)NativeMethods.WM.QUERYDRAGICON;
                        int GCL_HICON = -14;
                        int GCL_HICONSM = -34;
                        IconSize sizeSetting = _tasksService.TaskIconSize;

                        if (sizeSetting == IconSize.Small)
                        {
                            NativeMethods.SendMessageTimeout(Handle, WM_GETICON, 2, 0, 2, 1000, ref hIco);
                            if (hIco == IntPtr.Zero)
                                NativeMethods.SendMessageTimeout(Handle, WM_GETICON, 0, 0, 2, 1000, ref hIco);
                        }
                        else
                        {
                            NativeMethods.SendMessageTimeout(Handle, WM_GETICON, 1, 0, 2, 1000, ref hIco);
                        }

                        if (hIco == IntPtr.Zero && sizeSetting == IconSize.Small)
                        {
                            if (!Environment.Is64BitProcess)
                                hIco = NativeMethods.GetClassLong(Handle, GCL_HICONSM);
                            else
                                hIco = NativeMethods.GetClassLongPtr(Handle, GCL_HICONSM);
                        }

                        if (hIco == IntPtr.Zero)
                        {
                            if (!Environment.Is64BitProcess)
                                hIco = NativeMethods.GetClassLong(Handle, GCL_HICON);
                            else
                                hIco = NativeMethods.GetClassLongPtr(Handle, GCL_HICON);
                        }

                        if (hIco == IntPtr.Zero)
                        {
                            NativeMethods.SendMessageTimeout(Handle, WM_QUERYDRAGICON, 0, 0, 0, 1000, ref hIco);
                        }

                        if (hIco == IntPtr.Zero && _icon == null)
                        {
                            // last resort: find icon by executable. if we already have an icon from a previous fetch, then just skip this
                            if (ShellHelper.Exists(WinFileName))
                            {
                                IconSize size = IconSize.Small;
                                if (sizeSetting != size)
                                    size = IconSize.Large;

                                hIco = IconHelper.GetIconByFilename(WinFileName, size);
                            }
                        }

                        if (hIco != IntPtr.Zero)
                        {
                            if (_hIcon != hIco)
                            {
                                _hIcon = hIco;
                                bool returnDefault = (_icon == null); // only return a default icon if we don't already have one. otherwise let's use what we have.
                                ShellImage icon = IconImageConverter.GetImageFromHIcon(hIco, returnDefault);
                                if (icon != null)
                                {
                                    Icon = icon;
                                }
                            }
                            else
                            {
                                NativeMethods.DestroyIcon(hIco);
                            }
                        }
                    }

                    _iconLoading = false;
                }, CancellationToken.None, TaskCreationOptions.None, IconHelper.IconScheduler);
            }
        }

        internal void SetMonitor()
        {
            HMonitor = NativeMethods.MonitorFromWindow(Handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        }

        internal NativeMethods.ShortRect GetButtonRectFromShell()
        {
            NativeMethods.ShortRect rect = new NativeMethods.ShortRect();
            GetButtonRect?.Invoke(ref rect);
            return rect;
        }

        public void SetOverlayIcon(IntPtr hIcon)
        {
            if (hIcon == IntPtr.Zero)
            {
                OverlayIcon = null;
                return;
            }

            ShellImage icon = IconImageConverter.GetImageFromHIcon(hIcon, false);
            if (icon != null)
            {
                OverlayIcon = icon;
            }
        }

        public void SetOverlayIconDescription(IntPtr lParam)
        {
            try
            {
                if (ProcId is uint procId)
                {
                    if (lParam == IntPtr.Zero)
                    {
                        return;
                    }

                    IntPtr hShared = NativeMethods.SHLockShared(lParam, procId);

                    if (hShared == IntPtr.Zero)
                    {
                        return;
                    }

                    string str = Marshal.PtrToStringAuto(hShared);
                    NativeMethods.SHUnlockShared(hShared);

                    OverlayIconDescription = str;
                }
            }
            catch (Exception e)
            {
                ShellLogger.Error($"ApplicationWindow: Unable to get overlay icon description from process {Title}: {e.Message}");
            }
        }

        internal void UpdateProperties()
        {
            setTitle();
            SetShowInTaskbar();
            setIcon();
        }

        public void BringToFront()
        {
            // call restore if window is minimized
            if (IsMinimized)
            {
                Restore();
            }
            else
            {
                // If the window is maximized, use ShowMaximize so that it doesn't un-maximize
                if (GetWindowShowStyle(Handle) != NativeMethods.WindowShowStyle.ShowMaximized ||
                    !NativeMethods.ShowWindow(Handle, NativeMethods.WindowShowStyle.ShowMaximized))
                {
                    NativeMethods.ShowWindow(Handle, NativeMethods.WindowShowStyle.Show);
                }
                makeForeground();

                if (State == WindowState.Flashing) State = WindowState.Active; // some stubborn windows (Outlook) start flashing while already active, this lets us stop
            }
        }

        public void Minimize()
        {
            if (!CanMinimize)
            {
                return;
            }

            NativeMethods.GetWindowThreadProcessId(Handle, out uint procId);
            NativeMethods.AllowSetForegroundWindow(procId);
            NativeMethods.PostMessage(Handle, (int)NativeMethods.WM.SYSCOMMAND, (IntPtr)NativeMethods.SC_MINIMIZE, IntPtr.Zero);
        }

        public void Restore()
        {
            IntPtr retval = IntPtr.Zero;
            NativeMethods.SendMessageTimeout(Handle, (int)NativeMethods.WM.SYSCOMMAND, NativeMethods.SC_RESTORE, 0, 2, 200, ref retval);

            makeForeground();
        }

        public void Maximize()
        {
            bool maximizeResult = NativeMethods.ShowWindow(Handle, NativeMethods.WindowShowStyle.Maximize);
            if (!maximizeResult)
            {
                // we don't have a fallback for elevated windows here since our only hope, SC_MAXIMIZE, doesn't seem to work for them. fall back to restore.
                IntPtr retval = IntPtr.Zero;
                NativeMethods.SendMessageTimeout(Handle, (int)NativeMethods.WM.SYSCOMMAND, NativeMethods.SC_RESTORE, 0, 2, 200, ref retval);
            }
            makeForeground();
        }

        private void makeForeground()
        {
            NativeMethods.SetForegroundWindow(NativeMethods.GetLastActivePopup(Handle));
        }

        internal IntPtr DoClose()
        {
            makeForeground();
            IntPtr retval = IntPtr.Zero;
            NativeMethods.SendMessageTimeout(Handle, (int)NativeMethods.WM.SYSCOMMAND, NativeMethods.SC_CLOSE, 0, 2, 200, ref retval);

            return retval;
        }

        public void Close()
        {
            _tasksService.CloseWindow(this);
        }

        public void Move()
        {
            // move window via arrow keys; must be active window to control
            BringToFront();
            IntPtr retval = IntPtr.Zero;
            NativeMethods.SendMessageTimeout(Handle, (int)NativeMethods.WM.SYSCOMMAND, NativeMethods.SC_MOVE, 0, 2, 200, ref retval);
        }

        public void Size()
        {
            // size window via arrow keys; must be active window to control
            BringToFront();
            IntPtr retval = IntPtr.Zero;
            NativeMethods.SendMessageTimeout(Handle, (int)NativeMethods.WM.SYSCOMMAND, NativeMethods.SC_SIZE, 0, 2, 200, ref retval);
        }

        /// <summary>
        /// Returns whether a window is normal (1), minimized (2), or maximized (3).
        /// </summary>
        /// <param name="hWnd">The handle of the window.</param>
        private NativeMethods.WindowShowStyle GetWindowShowStyle(IntPtr hWnd)
        {
            NativeMethods.WINDOWPLACEMENT placement = new NativeMethods.WINDOWPLACEMENT();
            NativeMethods.GetWindowPlacement(hWnd, ref placement);
            return placement.showCmd;
        }

        #region IEquatable<Window> Members

        public bool Equals(ApplicationWindow other)
        {
            return Handle.Equals(other.Handle);
        }

        #endregion

        #region INotifyPropertyChanged Members

        public event PropertyChangedEventHandler PropertyChanged;

        public void OnPropertyChanged(string PropertyName)
        {
            if (this.PropertyChanged != null)
            {
                this.PropertyChanged(this, new PropertyChangedEventArgs(PropertyName));
            }
        }

        #endregion

        public enum WindowState
        {
            Active,
            Inactive,
            Hidden,
            Flashing,
            Unknown = 999
        }
    }

}

// ----- WindowsTasks\FullScreenEventArgs.cs -----
namespace ManagedShell.WindowsTasks
{
    public class FullScreenEventArgs : EventArgs
    {
        public IntPtr Handle;
        public bool IsEntering;
    }
}

// ----- WindowsTasks\ITaskCategoryProvider.cs -----
namespace ManagedShell.WindowsTasks
{
    public interface ITaskCategoryProvider : IDisposable
    {
        /// <summary>
        /// Called by ApplicationWindow to request its category.
        /// </summary>
        /// <param name="window">ApplicationWindow to get category for</param>
        /// <returns>Category</returns>
        string GetCategory(ApplicationWindow window);

        /// <summary>
        /// Provides the ITaskCategoryProvider with delegate to call when ApplicationWindow
        /// categories need to be re-evaluated.
        /// </summary>
        /// <param name="changeDelegate">Delegate to call when categories change</param>
        void SetCategoryChangeDelegate(TaskCategoryChangeDelegate changeDelegate);
    }
}

// ----- WindowsTasks\TaskCategoryChangeDelegate.cs -----
namespace ManagedShell.WindowsTasks
{
    public delegate void TaskCategoryChangeDelegate();
}

// ----- WindowsTasks\Tasks.cs -----
// Taken from ManagedShell (https://github.com/cairoshell/ManagedShell), Apache License 2.0.
// Changed for HeMiDock: the windows as a plain collection instead of a grouped view of WPF.
namespace ManagedShell.WindowsTasks
{
    public partial class Tasks : IDisposable
    {
        private readonly TasksService _tasksService;

        public Tasks(TasksService tasksService)
        {
            _tasksService = tasksService;
        }

        /// <summary>All windows the service knows (filter by <see cref="ApplicationWindow.ShowInTaskbar"/>).</summary>
        public ObservableCollection<ApplicationWindow> Windows => _tasksService.Windows;

        public void Initialize(ITaskCategoryProvider taskCategoryProvider, bool withMultiMonTracking = false)
        {
            if (!_tasksService.IsInitialized)
            {
                SetTaskCategoryProvider(taskCategoryProvider);
                Initialize(withMultiMonTracking);
            }
        }

        public void Initialize(bool withMultiMonTracking = false)
        {
            _tasksService.Initialize(withMultiMonTracking);
        }

        public void SetTaskCategoryProvider(ITaskCategoryProvider taskCategoryProvider)
        {
            if (_tasksService.TaskCategoryProvider != null)
            {
                _tasksService.TaskCategoryProvider.Dispose();
            }

            _tasksService.SetTaskCategoryProvider(taskCategoryProvider);
        }

        public void Dispose()
        {
            _tasksService.Dispose();
        }
    }
}

// ----- WindowsTasks\TasksService.cs -----
namespace ManagedShell.WindowsTasks
{
    public partial class TasksService : IDisposable
    {
        public static readonly IconSize DEFAULT_ICON_SIZE = IconSize.Small;

        public event EventHandler<WindowEventArgs> WindowActivated;
        public event EventHandler<EventArgs> DesktopActivated;
        public event EventHandler<FullScreenEventArgs> FullScreenChanged;
        public event EventHandler<WindowEventArgs> MonitorChanged;

        private NativeWindowEx _HookWin;
        private object _windowsLock = new object();
        internal bool IsInitialized;
        private IconSize _taskIconSize;

        private static int WM_SHELLHOOKMESSAGE = -1;
        private static int WM_TASKBARCREATEDMESSAGE = -1;
        private static int TASKBARBUTTONCREATEDMESSAGE = -1;
        private static IntPtr cloakEventHook = IntPtr.Zero;
        private WinEventProc cloakEventProc;
        private static IntPtr moveEventHook = IntPtr.Zero;
        private WinEventProc moveEventProc;

        internal ITaskCategoryProvider TaskCategoryProvider;
        private TaskCategoryChangeDelegate CategoryChangeDelegate;

        public IconSize TaskIconSize
        {
            get { return _taskIconSize; }
            set
            {
                if (value == _taskIconSize)
                {
                    return;
                }

                _taskIconSize = value;

                if (!IsInitialized)
                {
                    return;
                }

                foreach (var window in Windows)
                {
                    if (!window.ShowInTaskbar)
                    {
                        return;
                    }

                    window.UpdateProperties();
                }
            }
        }

        public TasksService() : this(DEFAULT_ICON_SIZE)
        {
        }
        
        public TasksService(IconSize iconSize)
        {
            TaskIconSize = iconSize;
        }

        internal void Initialize(bool withMultiMonTracking)
        {
            if (IsInitialized)
            {
                return;
            }

            try
            {
                ShellLogger.Debug("TasksService: Starting");

                // create window to receive task events
                _HookWin = new NativeWindowEx();
                _HookWin.CreateHandle(new CreateParams());

                // prevent other shells from working properly
                SetTaskmanWindow(_HookWin.Handle);

                // register to receive task events
                RegisterShellHookWindow(_HookWin.Handle);
                WM_SHELLHOOKMESSAGE = RegisterWindowMessage("SHELLHOOK");
                WM_TASKBARCREATEDMESSAGE = RegisterWindowMessage("TaskbarCreated");
                TASKBARBUTTONCREATEDMESSAGE = RegisterWindowMessage("TaskbarButtonCreated");
                _HookWin.MessageReceived += ShellWinProc;

                if (EnvironmentHelper.IsWindows8OrBetter)
                {
                    // set event hook for cloak/uncloak events
                    cloakEventProc = CloakEventCallback;

                    if (cloakEventHook == IntPtr.Zero)
                    {
                        cloakEventHook = SetWinEventHook(
                            EVENT_OBJECT_CLOAKED,
                            EVENT_OBJECT_UNCLOAKED,
                            IntPtr.Zero,
                            cloakEventProc,
                            0,
                            0,
                            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
                    }
                }

                if (withMultiMonTracking && !EnvironmentHelper.IsWindows8OrBetter)
                {
                    // set event hook for move events
                    // In Windows 8 and newer, use HSHELL_MONITORCHANGED instead
                    moveEventProc = MoveEventCallback;

                    if (moveEventHook == IntPtr.Zero)
                    {
                        moveEventHook = SetWinEventHook(
                            EVENT_OBJECT_LOCATIONCHANGE,
                            EVENT_OBJECT_LOCATIONCHANGE,
                            IntPtr.Zero,
                            moveEventProc,
                            0,
                            0,
                            WINEVENT_OUTOFCONTEXT);
                    }
                }

                // set window for ITaskbarList
                setTaskbarListHwnd(_HookWin.Handle);

                // adjust minimize animation
                SetMinimizedMetrics();

                // enumerate windows already opened and set active window
                getInitialWindows();

                IsInitialized = true;
            }
            catch (Exception ex)
            {
                ShellLogger.Info("TasksService: Unable to start: " + ex.Message);
            }
        }

        internal void SetTaskCategoryProvider(ITaskCategoryProvider provider)
        {
            TaskCategoryProvider = provider;

            if (CategoryChangeDelegate == null)
            {
                CategoryChangeDelegate = CategoriesChanged;
            }

            TaskCategoryProvider.SetCategoryChangeDelegate(CategoryChangeDelegate);
        }

        private void getInitialWindows()
        {
            EnumWindows((hwnd, lParam) =>
            {
                ApplicationWindow win = new ApplicationWindow(this, hwnd);

                if (win.CanAddToTaskbar && win.ShowInTaskbar && !Windows.Contains(win))
                {
                    Windows.Add(win);

                    sendTaskbarButtonCreatedMessage(win.Handle);
                }

                return true;
            }, 0);

            IntPtr hWndForeground = GetForegroundWindow();
            if (Windows.Any(i => i.Handle == hWndForeground && i.ShowInTaskbar))
            {
                ApplicationWindow win = Windows.First(wnd => wnd.Handle == hWndForeground);
                win.State = ApplicationWindow.WindowState.Active;
                win.SetShowInTaskbar();
            }
        }

        public void Dispose()
        {
            if (IsInitialized)
            {
                ShellLogger.Debug("TasksService: Deregistering hooks");
                DeregisterShellHookWindow(_HookWin.Handle);
                if (cloakEventHook != IntPtr.Zero) UnhookWinEvent(cloakEventHook);
                if (moveEventHook != IntPtr.Zero) UnhookWinEvent(moveEventHook);
                _HookWin.DestroyHandle();
                setTaskbarListHwnd(IntPtr.Zero);
                IsInitialized = false;
                Windows.Clear();
            }

            TaskCategoryProvider?.Dispose();
        }

        private void CategoriesChanged()
        {
            foreach (ApplicationWindow window in Windows)
            {
                if (window.ShowInTaskbar)
                {
                    window.Category = TaskCategoryProvider?.GetCategory(window);
                }
            }
        }

        private void SetMinimizedMetrics()
        {
            MinimizedMetrics mm = new MinimizedMetrics
            {
                cbSize = (uint)Marshal.SizeOf(typeof(MinimizedMetrics))
            };

            IntPtr mmPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MinimizedMetrics)));

            try
            {
                Marshal.StructureToPtr(mm, mmPtr, true);
                SystemParametersInfo(SPI.GETMINIMIZEDMETRICS, mm.cbSize, mmPtr, SPIF.None);
                mm.iWidth = 140;
                mm.iArrange |= MinimizedMetricsArrangement.Hide;
                Marshal.StructureToPtr(mm, mmPtr, true);
                SystemParametersInfo(SPI.SETMINIMIZEDMETRICS, mm.cbSize, mmPtr, SPIF.None);
            }
            finally
            {
                Marshal.DestroyStructure(mmPtr, typeof(MinimizedMetrics));
                Marshal.FreeHGlobal(mmPtr);
            }
        }

        public void CloseWindow(ApplicationWindow window)
        {
            if (window.DoClose() != IntPtr.Zero)
            {
                ShellLogger.Debug($"TasksService: Removing window {window.Title} from collection due to no response");
                window.Dispose();
                Windows.Remove(window);
            }
        }

        private void sendTaskbarButtonCreatedMessage(IntPtr hWnd)
        {
            // Server Core doesn't support ITaskbarList, so sending this message on that OS could cause some assuming apps to crash
            if (!EnvironmentHelper.IsServerCore) SendNotifyMessage(hWnd, (uint)TASKBARBUTTONCREATEDMESSAGE, UIntPtr.Zero, IntPtr.Zero);
        }

        private ApplicationWindow addWindow(IntPtr hWnd, ApplicationWindow.WindowState initialState = ApplicationWindow.WindowState.Inactive, bool sanityCheck = false)
        {
            ApplicationWindow win = new ApplicationWindow(this, hWnd);

            // set window state if a non-default value is provided
            if (initialState != ApplicationWindow.WindowState.Inactive) win.State = initialState;

            // add window unless we need to validate it is eligible to show in taskbar
            if (!sanityCheck || win.CanAddToTaskbar)
            {
                Windows.Add(win);
                ShellLogger.Debug($"TasksService: Added window {hWnd} ({win.Title})");
            }

            // Only send TaskbarButtonCreated if we are shell, and if OS is not Server Core
            // This is because if Explorer is running, it will send the message, so we don't need to
            if (EnvironmentHelper.IsAppRunningAsShell) sendTaskbarButtonCreatedMessage(win.Handle);

            return win;
        }

        private void removeWindow(IntPtr hWnd)
        {
            if (Windows.Any(i => i.Handle == hWnd))
            {
                do
                {
                    ApplicationWindow win = Windows.First(wnd => wnd.Handle == hWnd);
                    win.Dispose();
                    Windows.Remove(win);

                    ShellLogger.Debug($"TasksService: Removed window {hWnd} ({win.Title})");
                }
                while (Windows.Any(i => i.Handle == hWnd));
            }
        }

        private void redrawWindow(ApplicationWindow win)
        {
            win.UpdateProperties();
            ShellLogger.Debug($"TasksService: Updated window {win.Handle} ({win.Title})");

            foreach (ApplicationWindow wind in Windows)
            {
                if (wind.WinFileName == win.WinFileName && wind.Handle != win.Handle)
                {
                    wind.UpdateProperties();
                }
            }
        }

        private void ShellWinProc(ref Message msg, ref bool handled)
        {
            Message msgCopy = msg;
            handled = true;
            if (msg.Msg == WM_SHELLHOOKMESSAGE)
            {
                try
                {
                    lock (_windowsLock)
                    {
                        switch ((HSHELL)msg.WParam.ToInt32())
                        {
                            case HSHELL.WINDOWCREATED:
                                if (!Windows.Any(i => i.Handle == msgCopy.LParam))
                                {
                                    addWindow(msg.LParam);
                                }
                                else
                                {
                                    ApplicationWindow win = Windows.First(wnd => wnd.Handle == msgCopy.LParam);
                                    win.UpdateProperties();
                                }
                                break;

                            case HSHELL.WINDOWDESTROYED:
                                removeWindow(msg.LParam);
                                break;

                            case HSHELL.WINDOWREPLACING:
                                if (Windows.Any(i => i.Handle == msgCopy.LParam))
                                {
                                    ApplicationWindow win = Windows.First(wnd => wnd.Handle == msgCopy.LParam);
                                    win.State = ApplicationWindow.WindowState.Inactive;
                                    win.SetShowInTaskbar();
                                }
                                else
                                {
                                    addWindow(msg.LParam);
                                }
                                break;
                            case HSHELL.WINDOWREPLACED:
                                // TODO: If a window gets replaced, we lose app-level state such as overlay icons.
                                removeWindow(msg.LParam);
                                break;

                            case HSHELL.WINDOWACTIVATED:
                            case HSHELL.RUDEAPPACTIVATED:
                                foreach (var aWin in Windows.Where(w => w.State == ApplicationWindow.WindowState.Active))
                                {
                                    aWin.State = ApplicationWindow.WindowState.Inactive;
                                }

                                if (msg.LParam != IntPtr.Zero)
                                {
                                    ApplicationWindow win = null;

                                    if (Windows.Any(i => i.Handle == msgCopy.LParam))
                                    {
                                        win = Windows.First(wnd => wnd.Handle == msgCopy.LParam);
                                        win.State = ApplicationWindow.WindowState.Active;
                                        win.SetShowInTaskbar();
                                        ShellLogger.Debug($"TasksService: Activated window {win.Handle} ({win.Title})");
                                    }
                                    else
                                    {
                                        win = addWindow(msg.LParam, ApplicationWindow.WindowState.Active);
                                    }

                                    if (win != null)
                                    {
                                        foreach (ApplicationWindow wind in Windows)
                                        {
                                            if (wind.WinFileName == win.WinFileName && wind.Handle != win.Handle)
                                                wind.SetShowInTaskbar();
                                        }

                                        WindowEventArgs args = new WindowEventArgs
                                        {
                                            Window = win
                                        };

                                        WindowActivated?.Invoke(this, args);
                                    }
                                }
                                else
                                {
                                    DesktopActivated?.Invoke(this, new EventArgs());
                                }
                                break;

                            case HSHELL.FLASH:
                                if (Windows.Any(i => i.Handle == msgCopy.LParam))
                                {
                                    ApplicationWindow win = Windows.First(wnd => wnd.Handle == msgCopy.LParam);
                                    
                                    if (win.State != ApplicationWindow.WindowState.Active)
                                    {
                                        win.State = ApplicationWindow.WindowState.Flashing;
                                    }

                                    redrawWindow(win);
                                }
                                else
                                {
                                    addWindow(msg.LParam, ApplicationWindow.WindowState.Flashing, true);
                                }
                                break;

                            case HSHELL.ACTIVATESHELLWINDOW:
                                ShellLogger.Debug("TasksService: Activate shell window called.");
                                break;

                            case HSHELL.ENDTASK:
                                removeWindow(msg.LParam);
                                break;

                            case HSHELL.REDRAW:
                                if (Windows.Any(i => i.Handle == msgCopy.LParam))
                                {
                                    ApplicationWindow win = Windows.First(wnd => wnd.Handle == msgCopy.LParam);

                                    if (win.State == ApplicationWindow.WindowState.Flashing)
                                    {
                                        win.State = ApplicationWindow.WindowState.Inactive;
                                    }

                                    redrawWindow(win);
                                }
                                else
                                {
                                    addWindow(msg.LParam, ApplicationWindow.WindowState.Inactive, true);
                                }
                                break;

                            case HSHELL.MONITORCHANGED:
                                if (Windows.Any(i => i.Handle == msgCopy.LParam))
                                {
                                    ApplicationWindow win = Windows.First(wnd => wnd.Handle == msgCopy.LParam);
                                    win.SetMonitor();

                                    WindowEventArgs args = new WindowEventArgs
                                    {
                                        Window = win
                                    };

                                    MonitorChanged?.Invoke(this, args);
                                }
                                break;

                            case HSHELL.FULLSCREENENTER:
                                {
                                    FullScreenEventArgs args = new FullScreenEventArgs
                                    {
                                        Handle = msgCopy.LParam,
                                        IsEntering = true
                                    };

                                    FullScreenChanged?.Invoke(this, args);
                                    ShellLogger.Debug($"TasksService: Full screen entered by window {msgCopy.LParam}");
                                    break;
                                }

                            case HSHELL.FULLSCREENEXIT:
                                {
                                    FullScreenEventArgs args = new FullScreenEventArgs
                                    {
                                        Handle = msgCopy.LParam,
                                        IsEntering = false
                                    };

                                    FullScreenChanged?.Invoke(this, args);
                                    ShellLogger.Debug($"TasksService: Full screen exited by window {msgCopy.LParam}");
                                    break;
                                }

                            case HSHELL.GETMINRECT:
                                SHELLHOOKINFO minRectInfo = Marshal.PtrToStructure<SHELLHOOKINFO>(msg.LParam);
                                if (Windows.Any(i => i.Handle == minRectInfo.hwnd))
                                {
                                    ApplicationWindow win = Windows.First(wnd => wnd.Handle == minRectInfo.hwnd);
                                    minRectInfo.rc = win.GetButtonRectFromShell();

                                    if (minRectInfo.rc.Width <= 0 && minRectInfo.rc.Height <= 0)
                                    {
                                        break;
                                    }
                                    Marshal.StructureToPtr(minRectInfo, msg.LParam, false);
                                    msg.Result = (IntPtr)1;
                                    ShellLogger.Debug($"TasksService: MinRect {minRectInfo.rc.Width}x{minRectInfo.rc.Height} provided for {win.Handle} ({win.Title})");
                                    return; // return here so the result isnt reset to DefWindowProc
                                }
                                break;

                            // TaskMan needs to return true if we provide our own task manager to prevent explorers.
                            // case HSHELL.TASKMAN:
                            //     SingletonLogger.Instance.Info("TaskMan Message received.");
                            //     break;

                            default:
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    ShellLogger.Error("TasksService: Error in ShellWinProc. ", ex);
                    Debugger.Break();
                }
            }
            else if (msg.Msg == WM_TASKBARCREATEDMESSAGE)
            {
                ShellLogger.Debug("TasksService: TaskbarCreated received, setting ITaskbarList window");
                setTaskbarListHwnd(_HookWin.Handle);
            }
            else if (msg.Msg >= (int)WM.USER)
            {
                // Handle ITaskbarList functions, most not implemented yet

                ApplicationWindow win = null;

                switch (msg.Msg)
                {
                    case (int)WM.USER + 50:
                        // ActivateTab
                        // Also sends WM_SHELLHOOK message
                        ShellLogger.Debug("TasksService: ITaskbarList: ActivateTab HWND:" + msg.LParam);
                        msg.Result = IntPtr.Zero;
                        return;
                    case (int)WM.USER + 60:
                        // MarkFullscreenWindow
                        // Also sends WM_SHELLHOOK message
                        ShellLogger.Debug("TasksService: ITaskbarList: MarkFullscreenWindow HWND:" + msg.LParam + " Entering? " + msg.WParam);
                        FullScreenEventArgs args = new FullScreenEventArgs
                        {
                            Handle = msgCopy.LParam,
                            IsEntering = msg.WParam != IntPtr.Zero
                        };

                        FullScreenChanged?.Invoke(this, args);
                        msg.Result = IntPtr.Zero;
                        return;
                    case (int)WM.USER + 64:
                        // SetProgressValue
                        ShellLogger.Debug("TasksService: ITaskbarList: SetProgressValue HWND:" + msg.WParam + " Progress: " + msg.LParam);

                        win = new ApplicationWindow(this, msg.WParam);
                        if (Windows.Contains(win))
                        {
                            win = Windows.First(wnd => wnd.Handle == msgCopy.WParam);
                            win.ProgressValue = (int)msg.LParam;
                        }

                        msg.Result = IntPtr.Zero;
                        return;
                    case (int)WM.USER + 65:
                        // SetProgressState
                        ShellLogger.Debug("TasksService: ITaskbarList: SetProgressState HWND:" + msg.WParam + " Flags: " + msg.LParam);

                        win = new ApplicationWindow(this, msg.WParam);
                        if (Windows.Contains(win))
                        {
                            win = Windows.First(wnd => wnd.Handle == msgCopy.WParam);
                            win.ProgressState = (TBPFLAG)msg.LParam;
                        }

                        msg.Result = IntPtr.Zero;
                        return;
                    case (int)WM.USER + 67:
                        // RegisterTab
                        ShellLogger.Debug("TasksService: ITaskbarList: RegisterTab MDI HWND:" + msg.LParam + " Tab HWND: " + msg.WParam);
                        msg.Result = IntPtr.Zero;
                        return;
                    case (int)WM.USER + 68:
                        // UnregisterTab
                        ShellLogger.Debug("TasksService: ITaskbarList: UnregisterTab Tab HWND: " + msg.WParam);
                        msg.Result = IntPtr.Zero;
                        return;
                    case (int)WM.USER + 71:
                        // SetTabOrder
                        ShellLogger.Debug("TasksService: ITaskbarList: SetTabOrder HWND:" + msg.WParam + " Before HWND: " + msg.LParam);
                        msg.Result = IntPtr.Zero;
                        return;
                    case (int)WM.USER + 72:
                        // SetTabActive
                        ShellLogger.Debug("TasksService: ITaskbarList: SetTabActive HWND:" + msg.WParam);
                        msg.Result = IntPtr.Zero;
                        return;
                    case (int)WM.USER + 75:
                        // Unknown
                        ShellLogger.Debug("TasksService: ITaskbarList: Unknown HWND:" + msg.WParam + " LParam: " + msg.LParam);
                        msg.Result = IntPtr.Zero;
                        return;
                    case (int)WM.USER + 76:
                        // ThumbBarAddButtons
                        ShellLogger.Debug("TasksService: ITaskbarList: ThumbBarAddButtons HWND:" + msg.WParam);
                        msg.Result = IntPtr.Zero;
                        return;
                    case (int)WM.USER + 77:
                        // ThumbBarUpdateButtons
                        ShellLogger.Debug("TasksService: ITaskbarList: ThumbBarUpdateButtons HWND:" + msg.WParam);
                        msg.Result = IntPtr.Zero;
                        return;
                    case (int)WM.USER + 78:
                        // ThumbBarSetImageList
                        ShellLogger.Debug("TasksService: ITaskbarList: ThumbBarSetImageList HWND:" + msg.WParam);
                        msg.Result = IntPtr.Zero;
                        return;
                    case (int)WM.USER + 79:
                        // SetOverlayIcon - Icon
                        ShellLogger.Debug("TasksService: ITaskbarList: SetOverlayIcon - Icon HWND:" + msg.WParam);

                        win = new ApplicationWindow(this, msg.WParam);
                        if (Windows.Contains(win))
                        {
                            win = Windows.First(wnd => wnd.Handle == msgCopy.WParam);
                            win.SetOverlayIcon(msg.LParam);
                        }

                        msg.Result = IntPtr.Zero;
                        return;
                    case (int)WM.USER + 80:
                        // SetThumbnailTooltip
                        ShellLogger.Debug("TasksService: ITaskbarList: SetThumbnailTooltip HWND:" + msg.WParam);
                        msg.Result = IntPtr.Zero;
                        return;
                    case (int)WM.USER + 81:
                        // SetThumbnailClip
                        ShellLogger.Debug("TasksService: ITaskbarList: SetThumbnailClip HWND:" + msg.WParam);
                        msg.Result = IntPtr.Zero;
                        return;
                    case (int)WM.USER + 85:
                        // SetOverlayIcon - Description
                        ShellLogger.Debug("TasksService: ITaskbarList: SetOverlayIcon - Description HWND:" + msg.WParam);

                        win = new ApplicationWindow(this, msg.WParam);
                        if (Windows.Contains(win))
                        {
                            win = Windows.First(wnd => wnd.Handle == msgCopy.WParam);
                            win.SetOverlayIconDescription(msg.LParam);
                        }

                        msg.Result = IntPtr.Zero;
                        return;
                    case (int)WM.USER + 87:
                        // SetTabProperties
                        ShellLogger.Debug("TasksService: ITaskbarList: SetTabProperties HWND:" + msg.WParam);
                        msg.Result = IntPtr.Zero;
                        return;
                    default:
                        ShellLogger.Debug($"TasksService: Unknown ITaskbarList Msg: {msg.Msg} LParam: {msg.LParam} WParam: {msg.WParam}");
                        break;
                }
            }

            handled = false;
        }

        private void MoveEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (hWnd != IntPtr.Zero && idObject == 0 && idChild == 0)
            {
                if (Windows.Any(i => i.Handle == hWnd))
                {
                    ApplicationWindow win = Windows.First(wnd => wnd.Handle == hWnd);
                    win.SetMonitor();
                }
            }
        }

        private void CloakEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (hWnd != IntPtr.Zero && idObject == 0 && idChild == 0)
            {
                if (Windows.Any(i => i.Handle == hWnd))
                {
                    ApplicationWindow win = Windows.First(wnd => wnd.Handle == hWnd);
                    ShellLogger.Debug($"TasksService: {(eventType == EVENT_OBJECT_CLOAKED ? "Cloak" : "Uncloak")} event received for {win.Title}");
                    win.SetShowInTaskbar();
                }
            }
        }

        // set property on hook window that should receive ITaskbarList messages
        private void setTaskbarListHwnd(IntPtr hwndHook)
        {
            bool resetProp = true;

            // get the topmost tray
            IntPtr taskbarHwnd = WindowHelper.FindWindowsTray(IntPtr.Zero);
            
            if (taskbarHwnd == IntPtr.Zero)
            {
                return;
            }

            // if our tray is running, there may also be a second tray running
            IntPtr systemTaskbarHwnd = WindowHelper.FindWindowsTray(taskbarHwnd);

            if (hwndHook == IntPtr.Zero)
            {
                // no target hwnd provided
                // Try to find and use the handle of the Explorer hook window
                resetProp = false;
                hwndHook = getChildHwndByClass(systemTaskbarHwnd == IntPtr.Zero ? taskbarHwnd : systemTaskbarHwnd, "MSTaskSwWClass");
            }

            if (hwndHook == IntPtr.Zero)
            {
                // if still no hwnd to hook, we can't do anything
                return;
            }

            ShellLogger.Debug("TasksService: Adding TaskbandHWND prop to hwnd: " + taskbarHwnd);
            SetProp(taskbarHwnd, "TaskbandHWND", hwndHook);

            // Remove the property from the Explorer taskbar, if it is not the only tray
            if (resetProp && systemTaskbarHwnd != IntPtr.Zero)
            {
                ShellLogger.Debug("TasksService: Removing TaskbandHWND prop from hwnd: " + systemTaskbarHwnd);
                RemoveProp(systemTaskbarHwnd, "TaskbandHWND");
            }
        }

        private IntPtr getChildHwndByClass(IntPtr parentHwnd, string wndClass)
        {
            IntPtr childHwnd = IntPtr.Zero;
            EnumChildWindows(parentHwnd, (hwnd, lParam) =>
            {
                StringBuilder cName = new StringBuilder(256);
                GetClassName(hwnd, cName, cName.Capacity);
                if (cName.ToString() == wndClass)
                {
                    childHwnd = hwnd;
                    return false;
                }

                return true;
            }, 0);

            return childHwnd;
        }

        // HeMiDock: a plain collection instead of a dependency property of WPF
        internal ObservableCollection<ApplicationWindow> Windows { get; } = new ObservableCollection<ApplicationWindow>();
    }
}

// ----- WindowsTasks\WindowEventArgs.cs -----
namespace ManagedShell.WindowsTasks
{
    public class WindowEventArgs : EventArgs
    {
        public ApplicationWindow Window;
    }
}
