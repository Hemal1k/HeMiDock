// Taken from ManagedShell (https://github.com/cairoshell/ManagedShell), Apache License 2.0; changed for HeMiDock to run
// without WPF and Windows Forms where needed (pictures as pixels, own dispatcher, timer and message window).
// The files of one area of ManagedShell joined into one (their names are kept as headings).

using ManagedShell.Common.Enums;
using ManagedShell.Common.Helpers;
using ManagedShell.Common.Interfaces;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System;

// ----- Common\Common\DisposableObject.cs -----
namespace ManagedShell.Common
{
    /// <summary>
    /// Defines a class that is disposable.
    /// </summary>
    [DebuggerStepThrough]
    [Serializable]
    public abstract class DisposableObject : IDisposable
    {
        private readonly object _syncRoot = new object();
        private bool _disposed;

        /// <summary>
        /// Flag indicating whether the object has been disposed
        /// </summary>
        [Browsable(false)]
        public bool Disposed
        {
            get { return _disposed; }
        }

        /// <summary>
        /// Object that can be used to synchronize access
        /// </summary>
        [Browsable(false)]
        public object SyncRoot
        {
            get { return _syncRoot; }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Override to dispose of managed resources
        /// </summary>
        protected virtual void DisposeOfManagedResources()
        {
        }

        /// <summary>
        /// Override to dispose of unmanaged resources
        /// </summary>
        protected virtual void DisposeOfUnManagedResources()
        {
        }


        /// <summary>
        /// Internal disposal function to manage this object's disposed state
        /// </summary>
        /// <param name="disposing"></param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1063:Implement IDisposable Correctly", Justification = "This is a base class for exposing the IDisposable pattern in a reusable fashion.")]
        private void Dispose(bool disposing)
        {
            lock (SyncRoot)
            {
                if (!_disposed)
                {
                    if (disposing)
                    {
                        // dispose of managed resources here
                        DisposeOfManagedResources();
                    }

                    // dispose of unmanaged resources
                    DisposeOfUnManagedResources();

                    _disposed = true;
                }
            }
        }
    }
}

// ----- Common\Common\ThreadSafeObservableCollection.cs -----
namespace ManagedShell.Common.Common
{
    public class ThreadSafeObservableCollection<T> : ObservableCollection<T>
    {
        ReaderWriterLockSlim _lock;
        public ThreadSafeObservableCollection()
        {
            _lock = new ReaderWriterLockSlim();
        }

        protected override void ClearItems()
        {
            if (ShellDispatcher.CheckAccess())
            {
                try
                {
                    _lock.EnterWriteLock();
                    base.ClearItems();
                }
                finally { _lock.ExitWriteLock(); }
            }
            else
            {
                ShellDispatcher.Invoke(() => { Clear(); });
            }
        }
        protected override void InsertItem(int index, T item)
        {
            if (ShellDispatcher.CheckAccess())
            {
                try
                {
                    _lock.EnterWriteLock();
                    if (index >= 0 && index <= Count)
                        base.InsertItem(index, item);
                }
                finally { _lock.ExitWriteLock(); }
            }
            else
            {
                object[] e = new object[] { index, item };
                ShellDispatcher.Invoke(() => { InsertItemImpl(e); });
            }
        }
        void InsertItemImpl(object[] e)
        {
            if (ShellDispatcher.CheckAccess())
            {
                InsertItem((int)e[0], (T)e[1]);
            }
            else
            {
                ShellDispatcher.Invoke(() => { InsertItemImpl(e); });
            }
        }
        protected override void MoveItem(int oldIndex, int newIndex)
        {
            if (ShellDispatcher.CheckAccess())
            {
                try
                {
                    _lock.EnterWriteLock();
                    if (oldIndex < Count && newIndex < Count && oldIndex != newIndex)
                        base.MoveItem(oldIndex, newIndex);
                }
                finally { _lock.ExitWriteLock(); }
            }
            else
            {
                object[] e = new object[] { oldIndex, newIndex };
                ShellDispatcher.Invoke(() => { MoveItemImpl(e); });
            }
        }
        void MoveItemImpl(object[] e)
        {
            if (ShellDispatcher.CheckAccess())
            {
                MoveItem((int)e[0], (int)e[1]);
            }
            else
            {
                ShellDispatcher.Invoke(() => { MoveItemImpl(e); });
            }
        }
        protected override void RemoveItem(int index)
        {
            if (ShellDispatcher.CheckAccess())
            {
                try
                {
                    _lock.EnterWriteLock();
                    if (index < Count)
                        base.RemoveItem(index);
                }
                finally { _lock.ExitWriteLock(); }
            }
            else
            {
                ShellDispatcher.Invoke(() => { RemoveItem(index); });
            }
        }
        protected override void SetItem(int index, T item)
        {
            if (ShellDispatcher.CheckAccess())
            {
                try
                {
                    _lock.EnterWriteLock();
                    base.SetItem(index, item);
                }
                finally { _lock.ExitWriteLock(); }
            }
            else
            {
                object[] e = new object[] { index, item };
                ShellDispatcher.Invoke(() => { SetItemImpl(e); });
            }
        }
        void SetItemImpl(object[] e)
        {
            if (ShellDispatcher.CheckAccess())
            {
                SetItem((int)e[0], (T)e[1]);
            }
            else
            {
                ShellDispatcher.Invoke(() => { SetItemImpl(e); });
            }
        }
    }
}

// ----- Common\Enums\IconSize.cs -----
namespace ManagedShell.Common.Enums
{
    public enum IconSize : int
    {
        ///<summary>32 pixels</summary>
        Large = 0,
        ///<summary>16 pixels</summary>
        Small = 1,
        ///<summary>24 pixels</summary>
        Medium = 10,
        ///<summary>48 pixels</summary>
        ExtraLarge = 2,
        ///<summary>96 pixels</summary>
        Jumbo = 4
    }
}

// ----- Common\Enums\MONITOR_APP_VISIBILITY.cs -----
namespace ManagedShell.Common.Enums
{
    public enum MONITOR_APP_VISIBILITY
    {
        MAV_UNKNOWN = 0,
    }
}

// ----- Common\Enums\ShellFolderPath.cs -----
namespace ManagedShell.Common.Enums
{
    public class ShellFolderPath
    {
        private ShellFolderPath(string value)
        {
            Value = value;
        }

        public string Value { get; set; }

        public static ShellFolderPath ComputerFolder => new ShellFolderPath("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
        public static ShellFolderPath ControlPanelFolder => new ShellFolderPath("::{21EC2020-3AEA-1069-A2DD-08002B30309D}");
        public static ShellFolderPath RecycleBinFolder => new ShellFolderPath("::{645FF040-5081-101B-9F08-00AA002F954E}");
    }
}

// ----- Common\Extensions\RegistryKeyExtensions.cs -----
namespace ManagedShell.Common.Extensions
{
    public static class RegistryKeyExtensions
    {
        public static T GetValue<T>(this RegistryKey key, string valueName, T defaultValue = default)
        {
            if (key == null)
                return defaultValue;

            if (string.IsNullOrWhiteSpace(valueName))
                return defaultValue;

            object val = key.GetValue(valueName, defaultValue);
            if (val is T value)
                return value;

            return defaultValue;
        }

        public static T GetSubKeyValue<T>(this RegistryKey key, string subKey, string valueName, T defaultValue = default)
        {
            if (key == null)
                return defaultValue;

            if (string.IsNullOrWhiteSpace(subKey))
                return defaultValue;

            if (string.IsNullOrWhiteSpace(valueName))
                return defaultValue;

            var reg = key.OpenSubKey(subKey);

            if (reg == null)
                return defaultValue;

            return reg.GetValue<T>(valueName, defaultValue);
        }
    }
}

// ----- Common\Interfaces\IApplicationActivationManager.cs -----
// Copyright (c) 2019, Microsoft Corporation
//
// Permission to use, copy, modify, and/or distribute this software for any
// purpose with or without fee is hereby granted, provided that the above
// copyright notice and this permission notice appear in all copies.
//
// THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
// WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
// MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY
// SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
// WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
// ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR
// IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
//
// refer to https://docs.microsoft.com/en-us/windows/desktop/api/shobjidl_core/nn-shobjidl_core-iapplicationactivationmanager for more details regarding IApplicationActivationManager
namespace ManagedShell.Common.Interfaces
{
    public enum ActivateOptions
    {
        None = 0x0,
    };

    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IApplicationActivationManager
    {
        int ActivateApplication(
            [In][MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [In][MarshalAs(UnmanagedType.LPWStr)] string arguments,
            [In][MarshalAs(UnmanagedType.U4)] ActivateOptions options);
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    public class ApplicationActivationManager
    {
    }
}

// ----- Common\Interfaces\IAppVisibility.cs -----
namespace ManagedShell.Common.Interfaces
{
    [ComImport, Guid("2246EA2D-CAEA-4444-A3C4-6DE827E44313"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAppVisibility
    {
        long GetAppVisibilityOnMonitor([In] IntPtr hMonitor, [Out] out MONITOR_APP_VISIBILITY pMode);
        long IsLauncherVisible([Out] out bool pfVisible);
        long Advise([In] IAppVisibilityEvents pCallback, [Out] out int pdwCookie);
        long Unadvise([In] int dwCookie);
    }
}

// ----- Common\Interfaces\IAppVisibilityEvents.cs -----
namespace ManagedShell.Common.Interfaces
{
    [ComImport, Guid("6584CE6B-7D82-49C2-89C9-C6BC02BA8C38"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAppVisibilityEvents
    {
        long AppVisibilityOnMonitorChanged(
            [In] IntPtr hMonitor,
            [In] MONITOR_APP_VISIBILITY previousMode,
            [In] MONITOR_APP_VISIBILITY currentMode);

        long LauncherVisibilityChange([In] bool currentVisibleState);
    }
}

// ----- Common\Structs\KeyboardLayout.cs -----
namespace ManagedShell.Common.Structs
{
    public struct KeyboardLayout
    {
        public int HKL { get; set; }
        public string NativeName { get; set; }
        public string ThreeLetterName { get; set; }
    }
}

// ----- Common\SupportingClasses\AppVisibility.cs -----
namespace ManagedShell.Common.SupportingClasses
{
    [ComImport, Guid("7E5FE3D9-985F-4908-91F9-EE19F9FD1514")]
    class AppVisibility
    {
    }
}

// ----- Common\SupportingClasses\AppVisibilityEventArgs.cs -----
namespace ManagedShell.Common.SupportingClasses
{
    public class AppVisibilityEventArgs : EventArgs
    {
        public IntPtr MonitorHandle;
        public MONITOR_APP_VISIBILITY PreviousMode;
        public MONITOR_APP_VISIBILITY CurrentMode;
    }
}

// ----- Common\SupportingClasses\AppVisibilityEvents.cs -----
namespace ManagedShell.Common.SupportingClasses
{
    class AppVisibilityEvents : IAppVisibilityEvents
    {
        internal event EventHandler<AppVisibilityEventArgs> AppVisibilityChanged;
        internal event EventHandler<LauncherVisibilityEventArgs> LauncherVisibilityChanged;

        public AppVisibilityEvents() { }

        public long AppVisibilityOnMonitorChanged([In] IntPtr hMonitor, [In] MONITOR_APP_VISIBILITY previousMode, [In] MONITOR_APP_VISIBILITY currentMode)
        {
            AppVisibilityEventArgs args = new AppVisibilityEventArgs
            {
                MonitorHandle = hMonitor,
                PreviousMode = previousMode,
                CurrentMode = currentMode
            };

            AppVisibilityChanged?.Invoke(this, args);
            return 0;
        }

        public long LauncherVisibilityChange([In] bool currentVisibleState)
        {
            LauncherVisibilityEventArgs args = new LauncherVisibilityEventArgs
            {
                Visible = currentVisibleState
            };

            LauncherVisibilityChanged?.Invoke(this, args);
            return 0;
        }
    }
}

// ----- Common\SupportingClasses\LauncherVisibilityEventArgs.cs -----
namespace ManagedShell.Common.SupportingClasses
{
    public class LauncherVisibilityEventArgs : EventArgs
    {
        public bool Visible;
    }
}
