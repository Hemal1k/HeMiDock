// Taken from ManagedShell (https://github.com/cairoshell/ManagedShell), Apache License 2.0; changed for HeMiDock to run
// without WPF and Windows Forms where needed (pictures as pixels, own dispatcher, timer and message window).
// The files of one area of ManagedShell joined into one (their names are kept as headings).

using Microsoft.Win32.SafeHandles;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

// ----- Interop\ComTaskScheduler.cs -----
namespace ManagedShell.Interop
{
    /* The MIT License (MIT)

    Copyright (c) 2014 Matthias

    Permission is hereby granted, free of charge, to any person obtaining a copy
    of this software and associated documentation files (the "Software"), to deal
    in the Software without restriction, including without limitation the rights
    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is
    furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in all
    copies or substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
    SOFTWARE.

    https://github.com/matthiaswelz/journeyofcode/blob/master/SingleThreadScheduler/SingleThreadScheduler/SingleThreadTaskScheduler.cs
    */
    /// <summary>
    ///     Represents a <see cref="TaskScheduler"/> which executes code on a dedicated, single thread whose <see cref="ApartmentState"/> can be configured.
    /// </summary>
    /// <remarks>
    ///     You can use this class if you want to perform operations on a non thread-safe library from a multi-threaded environment.
    /// </remarks>
    public sealed partial class ComTaskScheduler : TaskScheduler, IDisposable
    {
        private readonly Thread _thread;
        private readonly CancellationTokenSource _cancellationToken;
        private readonly BlockingCollection<Task> _tasks;
        private readonly Action _initAction;

        /// <summary>
        ///     The <see cref="System.Threading.ApartmentState"/> of the <see cref="Thread"/> this <see cref="ComTaskScheduler"/> uses to execute its work.
        /// </summary>
        public ApartmentState ApartmentState { get; private set; }

        /// <summary>
        ///     Indicates the maximum concurrency level this <see cref="T:System.Threading.Tasks.TaskScheduler"/> is able to support.
        /// </summary>
        /// 
        /// <returns>
        ///     Returns <c>1</c>.
        /// </returns>
        public override int MaximumConcurrencyLevel
        {
            get { return 1; }
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="ComTaskScheduler"/>, optionally setting an <see cref="System.Threading.ApartmentState"/>.
        /// </summary>
        /// <param name="apartmentState">
        ///     The <see cref="ApartmentState"/> to use. Defaults to <see cref="System.Threading.ApartmentState.STA"/>
        /// </param>
        public ComTaskScheduler(ApartmentState apartmentState = ApartmentState.STA)
            : this(null, apartmentState)
        {

        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="ComTaskScheduler"/> passsing an initialization action, optionally setting an <see cref="System.Threading.ApartmentState"/>.
        /// </summary>
        /// <param name="initAction">
        ///     An <see cref="Action"/> to perform in the context of the <see cref="Thread"/> this <see cref="ComTaskScheduler"/> uses to execute its work after it has been started.
        /// </param>
        /// <param name="apartmentState">
        ///     The <see cref="ApartmentState"/> to use. Defaults to <see cref="System.Threading.ApartmentState.STA"/>
        /// </param>
        public ComTaskScheduler(Action initAction, ApartmentState apartmentState = ApartmentState.STA)
        {
            if (apartmentState != ApartmentState.MTA && apartmentState != ApartmentState.STA)
                throw new ArgumentException("apartementState");

            this.ApartmentState = apartmentState;
            this._cancellationToken = new CancellationTokenSource();
            this._tasks = new BlockingCollection<Task>();
            this._initAction = initAction ?? (() => { });

            this._thread = new Thread(this.ThreadStart);
            this._thread.IsBackground = true;
            this._thread.TrySetApartmentState(apartmentState);
            this._thread.Start();
        }


        /// <summary>
        ///     Waits until all scheduled <see cref="Task"/>s on this <see cref="ComTaskScheduler"/> have executed and then disposes this <see cref="ComTaskScheduler"/>.
        /// </summary>
        /// <remarks>
        ///     Calling this method will block execution. It should only be called once.
        /// </remarks>
        /// <exception cref="TaskSchedulerException">
        ///     Thrown when this <see cref="ComTaskScheduler"/> already has been disposed by calling either <see cref="Wait"/> or <see cref="Dispose"/>.
        /// </exception>
        public void Wait()
        {
            if (this._cancellationToken.IsCancellationRequested)
                throw new TaskSchedulerException("Cannot wait after disposal.");

            this._tasks.CompleteAdding();
            this._thread.Join();

            this._cancellationToken.Cancel();
        }

        /// <summary>
        ///     Disposes this <see cref="ComTaskScheduler"/> by not accepting any further work and not executing previously scheduled tasks.
        /// </summary>
        /// <remarks>
        ///     Call <see cref="Wait"/> instead to finish all queued work. You do not need to call <see cref="Dispose"/> after calling <see cref="Wait"/>.
        /// </remarks>
        public void Dispose()
        {
            if (this._cancellationToken.IsCancellationRequested)
                return;

            this._tasks.CompleteAdding();
            this._cancellationToken.Cancel();
        }

        protected override void QueueTask(Task task)
        {
            this.VerifyNotDisposed();

            this._tasks.Add(task, this._cancellationToken.Token);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            this.VerifyNotDisposed();

            if (this._thread != Thread.CurrentThread)
                return false;
            if (this._cancellationToken.Token.IsCancellationRequested)
                return false;

            this.TryExecuteTask(task);
            return true;
        }

        protected override IEnumerable<Task> GetScheduledTasks()
        {
            this.VerifyNotDisposed();

            return this._tasks.ToArray();
        }

        private void ThreadStart()
        {
            try
            {
                var token = this._cancellationToken.Token;

                this._initAction();

                foreach (var task in this._tasks.GetConsumingEnumerable(token))
                    this.TryExecuteTask(task);
            }
            finally
            {
                this._tasks.Dispose();
            }
        }

        private void VerifyNotDisposed()
        {
            if (this._cancellationToken.IsCancellationRequested)
                throw new ObjectDisposedException(typeof(ComTaskScheduler).Name);
        }
    }
}

// ----- Interop\KnownFolders.cs -----
namespace ManagedShell.Interop
{
    /// <summary>
    /// Standard folders registered with the system that aren't provided by Environment.SpecialFolder.
    /// </summary>
    public enum KnownFolder
    {
        Contacts,
        Downloads,
        Links,
        SavedGames,
        SavedSearches
    }
    
    /// <summary>
    /// Class containing methods to retrieve specific file system paths that aren't provided by Environment.SpecialFolder.
    /// </summary>
    public static class KnownFolders
    {
        private static readonly string[] _knownFolderGuids = {
            "{56784854-C6CB-462B-8169-88E350ACB882}", // Contacts
            "{374DE290-123F-4565-9164-39C4925E467B}", // Downloads
            "{BFB9D5E0-C6A9-404C-B2B2-AE6DB6AF4968}", // Links
            "{4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4}", // SavedGames
            "{7D1D3A04-DEBB-4115-95CF-2F29DA2920DA}" // SavedSearches
        };

        /// <summary>
        /// Gets the current path to the specified known folder as currently configured. This does
        /// not require the folder to exist.
        /// </summary>
        /// <param name="knownFolder">The known folder which current path will be returned.</param>
        /// <returns>The default path of the known folder, or an empty string if the path couldn't be retrieved.</returns>
        public static string GetPath(KnownFolder knownFolder)
        {
            return GetPath(knownFolder, NativeMethods.KnownFolderFlags.DontVerify);
        }

        /// <summary>
        /// Gets the current path to the specified known folder as currently configured.
        /// </summary>
        /// <param name="knownFolder">The known folder which current path will be returned.</param>
        /// <param name="flags">The known folder flags to use.</param>
        /// <returns>The default path of the known folder, or an empty string if the path couldn't be retrieved.</returns>
        public static string GetPath(KnownFolder knownFolder, NativeMethods.KnownFolderFlags flags)
        {
            return GetPath(knownFolder, flags, false);
        }

        /// <summary>
        /// Gets the current path to the specified known folder as currently configured.
        /// </summary>
        /// <param name="knownFolder">The known folder which current path will be returned.</param>
        /// <param name="flags">The known folder flags to use.</param>
        /// <param name="defaultUser">Specifies if the paths of the default user (user profile
        ///     template) will be used. This requires administrative rights.</param>
        /// <returns>The default path of the known folder, or an empty string if the path couldn't be retrieved.</returns>
        public static string GetPath(KnownFolder knownFolder, NativeMethods.KnownFolderFlags flags,
            bool defaultUser)
        {
            int result = NativeMethods.SHGetKnownFolderPath(new Guid(_knownFolderGuids[(int)knownFolder]),
                (uint)flags, new IntPtr(defaultUser ? -1 : 0), out IntPtr outPath);
            
            if (result >= 0)
            {
                return Marshal.PtrToStringUni(outPath);
            }

            return string.Empty;
        }
    }
}

// ----- Interop\NativeMethods.cs -----
namespace ManagedShell.Interop
{
    /// <summary>
    /// Container class for Win32 Native methods used within the desktop application (e.g. shutdown, sleep, et al).
    /// </summary>
    public partial class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public Rect(int left, int top, int right, int bottom)
            {
                Left = left;
                Top = top;
                Right = right;
                Bottom = bottom;
            }

            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width => Right - Left;

            public int Height => Bottom - Top;
        }

        /// <summary>
        /// Used by HSHELL_GETMINRECT
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ShortRect
        {
            public ShortRect(short left, short top, short right, short bottom)
            {
                Left = left;
                Top = top;
                Right = right;
                Bottom = bottom;
            }

            public short Left;
            public short Top;
            public short Right;
            public short Bottom;

            public int Width => Right - Left;

            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;

            public POINT(int x, int y)
            {
                this.x = x;
                this.y = y;
            }

            public POINT(long x, long y)
            {
                this.x = (int)x;
                this.y = (int)y;
            }
        }

        // lo = x; hi = y
        public static int MakeLParam(int loWord, int hiWord)
        {
            return ((short)hiWord << 16) | ((short)loWord & 0xffff);
        }

        public static IntPtr MakeLParamIntPtr(int loWord, int hiWord)
        {
            return new IntPtr(MakeLParam(loWord, hiWord));
        }
    }
}

// ----- Interop\NativeMethods.DocObj.cs -----
namespace ManagedShell.Interop
{
    public partial class NativeMethods
    {
        [ComImport, Guid("b722bccb-4e68-101b-a2bc-00aa00404770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IOleCommandTarget
        {
            [PreserveSig()]
            int QueryStatus(ref Guid pguidCmdGroup, int cCmds, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] prgCmds, ref IntPtr pCmdText);

            [PreserveSig()]
            int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdExecOpt, ref object pvaIn, ref object pvaOut);
        }

        public const int OLECMDID_NEW = 2;
        public const int OLECMDID_SAVE = 3;
        public const int OLECMDEXECOPT_DODEFAULT = 0;
    }
}

// ----- Interop\NativeMethods.DwmApi.cs -----
namespace ManagedShell.Interop
{
    public partial class NativeMethods
    {
        public const int DWM_TNP_VISIBLE = 0x8,
            DWM_TNP_OPACITY = 0x4,
            DWM_TNP_RECTDESTINATION = 0x1,
            DWM_TNP_RECTSOURCE = 0x2,
            DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

        const string DwmApi_DllName = "dwmapi";

        [DllImport(DwmApi_DllName)]
        public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);

        [DllImport(DwmApi_DllName)]
        public static extern int DwmUnregisterThumbnail(IntPtr thumb);

        [DllImport(DwmApi_DllName)]
        public static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out PSIZE size);

        [DllImport(DwmApi_DllName)]
        public static extern int DwmUpdateThumbnailProperties(IntPtr hThumb, ref DWM_THUMBNAIL_PROPERTIES props);

        [DllImport(DwmApi_DllName, EntryPoint = "#113", SetLastError = true)]
        public static extern uint DwmActivateLivePreview(uint enable, IntPtr targetHwnd, IntPtr callingHwnd, AeroPeekType type);

        // Windows 8.1+ version has an extra unknown parameter
        [DllImport(DwmApi_DllName, EntryPoint = "#113", SetLastError = true)]
        public static extern uint DwmActivateLivePreview(uint enable, IntPtr targetHwnd, IntPtr callingHwnd, AeroPeekType type, IntPtr unknown);

        [DllImport(DwmApi_DllName)]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, DWMWINDOWATTRIBUTE dwAttribute, out uint pvAttribute, int cbAttribute);

        [DllImport(DwmApi_DllName)]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, DWMWINDOWATTRIBUTE dwAttribute, ref int pvAttribute, int cbAttribute);


        [StructLayout(LayoutKind.Sequential)]
        public struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public Rect rcDestination;
            public Rect rcSource;
            public byte opacity;
            public bool fVisible;
            public bool fSourceClientAreaOnly;
        }

        public enum DWMWINDOWATTRIBUTE
        {
            DWMWA_NCRENDERING_ENABLED = 1,
            DWMWA_NCRENDERING_POLICY,
            DWMWA_TRANSITIONS_FORCEDISABLED,
            DWMWA_ALLOW_NCPAINT,
            DWMWA_CAPTION_BUTTON_BOUNDS,
            DWMWA_NONCLIENT_RTL_LAYOUT,
            DWMWA_FORCE_ICONIC_REPRESENTATION,
            DWMWA_FLIP3D_POLICY,
            DWMWA_EXTENDED_FRAME_BOUNDS,
            DWMWA_HAS_ICONIC_BITMAP,
            DWMWA_DISALLOW_PEEK,
            DWMWA_EXCLUDED_FROM_PEEK,
            DWMWA_CLOAK,
            DWMWA_CLOAKED,
            DWMWA_FREEZE_REPRESENTATION,
            DWMWA_LAST
        }


        public enum AeroPeekType : uint
        {
            Default = 0,
            Desktop = 1,
            Window = 3
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PSIZE
        {
            public int x;
            public int y;
        }
    }
}

// ----- Interop\NativeMethods.Gdi32.cs -----
namespace ManagedShell.Interop
{
    public partial class NativeMethods
    {
        const string Gdi32_DllName = "gdi32.dll";

        [DllImport(Gdi32_DllName)]
        public static extern bool DeleteObject(IntPtr hObject);

    }
}

// ----- Interop\NativeMethods.Kernel32.cs -----
namespace ManagedShell.Interop
{
    public partial class NativeMethods
    {
        const string Kernel32_DllName = "kernel32.dll";



        [DllImport(Kernel32_DllName, CharSet = CharSet.Auto, SetLastError = true)]
        public static extern SafeFileHandle CreateFile([MarshalAs(UnmanagedType.LPTStr)] string filename,
            [MarshalAs(UnmanagedType.U4)] FileAccess access,
            [MarshalAs(UnmanagedType.U4)] FileShare share,
            IntPtr securityAttributes,
            [MarshalAs(UnmanagedType.U4)] FileMode creationDisposition,
            [MarshalAs(UnmanagedType.U4)] FileAttributes flagsAndAttributes,
            IntPtr templateFile);

        // Handling the close splash screen event
        [DllImport(Kernel32_DllName)]
        public static extern Int32 OpenEvent(Int32 DesiredAccess, bool InheritHandle, string Name);

        // OpenEvent DesiredAccess defines


        [DllImport(Kernel32_DllName)]
        public static extern Int32 CloseHandle(Int32 Handle);

        [DllImport(Kernel32_DllName)]
        public static extern bool CloseHandle(IntPtr Handle);

        [DllImport(Kernel32_DllName, SetLastError = true, CallingConvention = CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWow64Process(
            [In] IntPtr hProcess,
            [Out] out bool wow64Process
        );


        [DllImport(Kernel32_DllName, SetLastError = true)]
        public static extern IntPtr OpenProcess(ProcessAccessFlags processAccess, bool bInheritHandle, int processId);

        [Flags]
        public enum ProcessAccessFlags : uint
        {
            All = 0x001F0FFF,
            Terminate = 0x00000001,
            QueryLimitedInformation = 0x00001000,
            Synchronize = 0x00100000
        }

        [DllImport(Kernel32_DllName, SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, [Out, MarshalAs(UnmanagedType.LPTStr)] StringBuilder lpExeName, ref int lpdwSize);


        [DllImport(Kernel32_DllName, SetLastError = true, ExactSpelling = true)]
        public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress,
            uint dwSize, AllocationType flAllocationType, MemoryProtection flProtect);

        [Flags]
        public enum AllocationType
        {
            Commit = 0x1000,
            Reserve = 0x2000,
            Release = 0x8000,
            Reset = 0x80000,
            Physical = 0x400000,
        }

        [Flags]
        public enum MemoryProtection
        {
            Execute = 0x10,
            ReadOnly = 0x02,
            ReadWrite = 0x04,
        }

        [DllImport(Kernel32_DllName, SetLastError = true)]
        public static extern bool ReadProcessMemory(
            IntPtr hProcess,
            IntPtr lpBaseAddress,
            IntPtr lpBuffer,
            Int32 nSize,
            out IntPtr lpNumberOfBytesRead);

        [DllImport(Kernel32_DllName, SetLastError = true)]
        public static extern bool ReadProcessMemory(
            IntPtr hProcess,
            UIntPtr lpBaseAddress,
            IntPtr lpBuffer,
            Int32 nSize,
            out IntPtr lpNumberOfBytesRead);

        [DllImport(Kernel32_DllName, SetLastError = true, ExactSpelling = true)]
        public static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress,
            int dwSize, AllocationType dwFreeType);

    }
}

// ----- Interop\NativeMethods.Msi.cs -----
namespace ManagedShell.Interop
{
    public partial class NativeMethods
    {


    }
}

// ----- Interop\NativeMethods.Ole32.cs -----
namespace ManagedShell.Interop
{
    public partial class NativeMethods
    {
        const string Ole32_DllName = "ole32.dll";

        /// <summary>
        /// Represents the OLE struct PROPVARIANT.
        /// </summary>
        /// <remarks>
        /// Must call Clear when finished to avoid memory leaks. If you get the value of
        /// a VT_UNKNOWN prop, an implicit AddRef is called, thus your reference will
        /// be active even after the PropVariant struct is cleared.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential)]
        public struct PropVariant
        {
            #region struct fields

            // The layout of these elements needs to be maintained.
            //
            // NOTE: We could use LayoutKind.Explicit, but we want
            //       to maintain that the IntPtr may be 8 bytes on
            //       64-bit architectures, so we’ll let the CLR keep
            //       us aligned.
            //
            // NOTE: In order to allow x64 compat, we need to allow for
            //       expansion of the IntPtr. However, the BLOB struct
            //       uses a 4-byte int, followed by an IntPtr, so
            //       although the p field catches most pointer values,
            //       we need an additional 4-bytes to get the BLOB
            //       pointer. The p2 field provides this, as well as
            //       the last 4-bytes of an 8-byte value on 32-bit
            //       architectures.

            // This is actually a VarEnum value, but the VarEnum type
            // shifts the layout of the struct by 4 bytes instead of the
            // expected 2.
            ushort vt;
            ushort wReserved1;
            ushort wReserved2;
            ushort wReserved3;
            IntPtr p;
            int p2;

            #endregion // struct fields

            #region union members

            sbyte cVal // CHAR cVal;
            {
                get { return (sbyte)GetDataBytes()[0]; }
            }

            byte bVal // UCHAR bVal;
            {
                get { return GetDataBytes()[0]; }
            }

            short iVal // SHORT iVal;
            {
                get { return BitConverter.ToInt16(GetDataBytes(), 0); }
            }

            ushort uiVal // USHORT uiVal;
            {
                get { return BitConverter.ToUInt16(GetDataBytes(), 0); }
            }

            int lVal // LONG lVal;
            {
                get { return BitConverter.ToInt32(GetDataBytes(), 0); }
            }

            uint ulVal // ULONG ulVal;
            {
                get { return BitConverter.ToUInt32(GetDataBytes(), 0); }
            }

            long hVal // LARGE_INTEGER hVal;
            {
                get { return BitConverter.ToInt64(GetDataBytes(), 0); }
            }

            ulong uhVal // ULARGE_INTEGER uhVal;
            {
                get { return BitConverter.ToUInt64(GetDataBytes(), 0); }
            }

            float fltVal // FLOAT fltVal;
            {
                get { return BitConverter.ToSingle(GetDataBytes(), 0); }
            }

            double dblVal // DOUBLE dblVal;
            {
                get { return BitConverter.ToDouble(GetDataBytes(), 0); }
            }

            bool boolVal // VARIANT_BOOL boolVal;
            {
                get { return (iVal == 0 ? false : true); }
            }

            int scode // SCODE scode;
            {
                get { return lVal; }
            }

            decimal cyVal // CY cyVal;
            {
                get { return decimal.FromOACurrency(hVal); }
            }

            DateTime date // DATE date;
            {
                get { return DateTime.FromOADate(dblVal); }
            }

            #endregion // union members

            /// <summary>
            /// Gets a byte array containing the data bits of the struct.
            /// </summary>
            /// <returns>A byte array that is the combined size of the data bits.</returns>
            private byte[] GetDataBytes()
            {
                byte[] ret = new byte[IntPtr.Size + sizeof(int)];
                if (IntPtr.Size == 4)
                    BitConverter.GetBytes(p.ToInt32()).CopyTo(ret, 0);
                else if (IntPtr.Size == 8)
                    BitConverter.GetBytes(p.ToInt64()).CopyTo(ret, 0);
                BitConverter.GetBytes(p2).CopyTo(ret, IntPtr.Size);
                return ret;
            }

            /// <summary>
            /// Called to properly clean up the memory referenced by a PropVariant instance.
            /// </summary>
            [DllImport(Ole32_DllName)]
            private extern static int PropVariantClear(ref PropVariant pvar);

            /// <summary>
            /// Called to clear the PropVariant’s referenced and local memory.
            /// </summary>
            /// <remarks>
            /// You must call Clear to avoid memory leaks.
            /// </remarks>
            public void Clear()
            {
                // Can’t pass “this” by ref, so make a copy to call PropVariantClear with
                PropVariant var = this;
                PropVariantClear(ref var);

                // Since we couldn’t pass “this” by ref, we need to clear the member fields manually
                // NOTE: PropVariantClear already freed heap data for us, so we are just setting
                //       our references to null.
                vt = (ushort)VarEnum.VT_EMPTY;
                wReserved1 = wReserved2 = wReserved3 = 0;
                p = IntPtr.Zero;
                p2 = 0;
            }

            /// <summary>
            /// Gets the variant type.
            /// </summary>
            public VarEnum Type
            {
                get { return (VarEnum)vt; }
            }

            /// <summary>
            /// Gets the variant value.
            /// </summary>
            public object Value
            {
                get
                {
                    // TODO: Add support for reference types (ie. VT_REF | VT_I1)
                    // TODO: Add support for safe arrays

                    switch ((VarEnum)vt)
                    {
                        case VarEnum.VT_I1:
                            return cVal;
                        case VarEnum.VT_UI1:
                            return bVal;
                        case VarEnum.VT_I2:
                            return iVal;
                        case VarEnum.VT_UI2:
                            return uiVal;
                        case VarEnum.VT_I4:
                        case VarEnum.VT_INT:
                            return lVal;
                        case VarEnum.VT_UI4:
                        case VarEnum.VT_UINT:
                            return ulVal;
                        case VarEnum.VT_I8:
                            return hVal;
                        case VarEnum.VT_UI8:
                            return uhVal;
                        case VarEnum.VT_R4:
                            return fltVal;
                        case VarEnum.VT_R8:
                            return dblVal;
                        case VarEnum.VT_BOOL:
                            return boolVal;
                        case VarEnum.VT_ERROR:
                            return scode;
                        case VarEnum.VT_CY:
                            return cyVal;
                        case VarEnum.VT_DATE:
                            return date;
                        case VarEnum.VT_FILETIME:
                            return DateTime.FromFileTime(hVal);
                        case VarEnum.VT_BSTR:
                            return Marshal.PtrToStringBSTR(p);
                        case VarEnum.VT_BLOB:
                            byte[] blobData = new byte[lVal];
                            IntPtr pBlobData;
                            if (IntPtr.Size == 4)
                            {
                                pBlobData = new IntPtr(p2);
                            }
                            else if (IntPtr.Size == 8)
                            {
                                // In this case, we need to derive a pointer at offset 12,
                                // because the size of the blob is represented as a 4-byte int
                                // but the pointer is immediately after that.
                                pBlobData = new IntPtr(BitConverter.ToInt64(GetDataBytes(), sizeof(int)));
                            }
                            else
                                throw new NotSupportedException();
                            Marshal.Copy(pBlobData, blobData, 0, lVal);
                            return blobData;
                        case VarEnum.VT_LPSTR:
                            return Marshal.PtrToStringAnsi(p);
                        case VarEnum.VT_LPWSTR:
                            return Marshal.PtrToStringUni(p);
                        case VarEnum.VT_UNKNOWN:
                            return Marshal.GetObjectForIUnknown(p);
                        case VarEnum.VT_DISPATCH:
                            return p;
                        default:
                            return "";
                    }
                }
            }
        }
    }
}

// ----- Interop\NativeMethods.Shell32.cs -----
namespace ManagedShell.Interop
{
    public partial class NativeMethods
    {
        const string Shell32_DllName = "shell32.dll";

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uCallbackMessage;
            public int uEdge;
            public Rect rc;
            public IntPtr lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATAV2
        {
            public int cbSize;
            public uint hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public Rect rc;
            public long lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARMSGDATAV3
        {
            public APPBARDATAV2 abd;
            public int dwMessage;
            public int dwPadding1;
            public long hSharedMemory;
            public int dwSourceProcessId;
            public int dwPadding3;
        }

        public enum ABMsg : int
        {
            ABM_NEW = 0,
            ABM_REMOVE,
            ABM_QUERYPOS,
            ABM_SETPOS,
            ABM_GETSTATE,
            ABM_GETTASKBARPOS,
            ABM_ACTIVATE,
            ABM_GETAUTOHIDEBAR,
            ABM_SETAUTOHIDEBAR,
            ABM_WINDOWPOSCHANGED,
            ABM_SETSTATE,
            ABM_GETAUTOHIDEBAREX
        }

        public enum ABEdge : int
        {
            ABE_LEFT = 0,
            ABE_TOP,
            ABE_RIGHT,
            ABE_BOTTOM
        }

        public enum ABState : int
        {
            Default = 0,
            AutoHide,
            OnTop
        }


        [DllImport(Shell32_DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern uint SHAppBarMessage(int dwMessage, ref APPBARDATA pData);

        public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;


        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [Flags]
        public enum SHGFI : int
        {
            /// <summary>get icon</summary>
            Icon = 0x000000100,
            /// <summary>get display name</summary>
            DisplayName = 0x000000200,
            /// <summary>get attributes</summary>
            Attributes = 0x000000800,
            /// <summary>get system icon index</summary>
            SysIconIndex = 0x000004000,
            /// <summary>show icon in selected state</summary>
            Selected = 0x000010000,
            /// <summary>pszPath is a pidl</summary>
            PIDL = 0x000000008,
            /// <summary>use passed dwFileAttribute</summary>
            UseFileAttributes = 0x000000010,
        }

        [DllImport(Shell32_DllName, CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport(Shell32_DllName, CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(IntPtr pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);




        /// <summary>
        /// Possible flags for the SHFileOperation method.
        /// </summary>
        [Flags]
        public enum FileOperationFlags : ushort
        {
            /// <summary>
            /// Do not ask the user to confirm selection
            /// </summary>
            FOF_NOCONFIRMATION = 0x0010,
            /// <summary>
            /// Delete the file to the recycle bin.  (Required flag to send a file to the bin
            /// </summary>
            FOF_ALLOWUNDO = 0x0040,
            /// <summary>
            /// Warn if files are too big to fit in the recycle bin and will need
            /// to be deleted completely.
            /// </summary>
            FOF_WANTNUKEWARNING = 0x4000,
        }

        /// <summary>
        /// File Operation Function Type for SHFileOperation
        /// </summary>
        public enum FileOperationType : uint
        {
            /// <summary>
            /// Delete (or recycle) the objects
            /// </summary>
            FO_DELETE = 0x0003,
        }

        /// <summary>
        /// SHFILEOPSTRUCT for SHFileOperation from COM
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct SHFILEOPSTRUCT
        {

            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.U4)]
            public FileOperationType wFunc;
            public string pFrom;
            public string pTo;
            public FileOperationFlags fFlags;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport(Shell32_DllName, CharSet = CharSet.Auto)]
        public static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);

        public enum NIN : uint
        {
            SELECT = 0x400,
            BALLOONUSERCLICK = 0x405,
            POPUPOPEN = 0x406,
            POPUPCLOSE = 0x407,
        }

        /// <summary>
        /// Numerical values of the NIM_* messages represented as an enumeration.
        /// </summary>
        public enum NIM : uint
        {
            /// <summary>
            /// Add a new icon.
            /// </summary>
            NIM_ADD = 0,

            /// <summary>
            /// Modify an existing icon.
            /// </summary>
            NIM_MODIFY = 1,

            /// <summary>
            /// Delete an icon.
            /// </summary>
            NIM_DELETE = 2,


            /// <summary>
            /// Shell v4 and above - Instructs the taskbar to behave accordingly based on the version (uVersion) set in the notifiyicondata struct.
            /// </summary>
            NIM_SETVERSION = 4
        }

        /// <summary>
        /// Shell_NotifyIcon flags.  NIF_*
        /// </summary>
        [Flags]
        public enum NIF : uint
        {
            MESSAGE = 0x0001,
            ICON = 0x0002,
            TIP = 0x0004,
            STATE = 0x0008,
            INFO = 0x0010,
            GUID = 0x0020,


        }

        /// <summary>
        /// Notify icon info balloon flags
        /// </summary>
        [Flags]
        public enum NIIF : uint
        {
            NONE = 0x00000000,
            INFO = 0x00000001,
            WARNING = 0x00000002,
            ERROR = 0x00000003,
            /// <summary>Use app-provided icon in the balloon. XP SP2 and later.</summary>
            USER = 0x00000004,
        }

        /// <summary>
        /// Notify icon data structure type
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATA
        {
            public int cbSize;
            public uint hWnd;
            public uint uID;
            public NIF uFlags;
            public uint uCallbackMessage;
            public uint hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;  // used with NIM_SETVERSION, values 0, 3 and 4
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public NIIF dwInfoFlags;
            public Guid guidItem;
            public uint hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SHELLTRAYDATA
        {
            public int dwUnknown;
            public uint dwMessage;
            public NOTIFYICONDATA nid;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINNOTIFYICONIDENTIFIER
        {
            public int dwMagic;
            public int dwMessage;
            public int cbSize;
            public int dwPadding;
            public uint hWnd;
            public uint uID;
            public Guid guidItem;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPropertyStore
        {
            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetCount([Out] out uint cProps);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetAt([In] uint iProp, out PROPERTYKEY pkey);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void GetValue([In] ref PROPERTYKEY key, out PropVariant pv);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void SetValue([In] ref PROPERTYKEY key, [In] ref PropVariant pv);

            [MethodImpl(MethodImplOptions.InternalCall, MethodCodeType = MethodCodeType.Runtime)]
            void Commit();
        }

        [DllImport(Shell32_DllName, SetLastError = true)]
        public static extern int SHGetPropertyStoreForWindow(IntPtr handle, ref Guid riid, out IPropertyStore propertyStore);



        public struct IMAGELISTDRAWPARAMS
        {
            public int cbSize;
            public IntPtr himl;
            public int i;
            public IntPtr hdcDst;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public int xBitmap;        // x offest from the upperleft of bitmap
            public int yBitmap;        // y offset from the upperleft of bitmap
            public int rgbBk;
            public int rgbFg;
            public int fStyle;
            public int dwRop;
            public int fState;
            public int Frame;
            public int crEffect;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IMAGEINFO
        {
            public IntPtr hbmImage;
            public IntPtr hbmMask;
            public int Unused1;
            public int Unused2;
            public Rect rcImage;
        }

        [ComImportAttribute()]
        [GuidAttribute("46EB5926-582E-4017-9FDF-E8998DAA0950")]
        [InterfaceTypeAttribute(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IImageList
        {
            [PreserveSig]
            int Add(
                IntPtr hbmImage,
                IntPtr hbmMask,
                ref int pi);

            [PreserveSig]
            int ReplaceIcon(
                int i,
                IntPtr hicon,
                ref int pi);

            [PreserveSig]
            int SetOverlayImage(
                int iImage,
                int iOverlay);

            [PreserveSig]
            int Replace(
                int i,
                IntPtr hbmImage,
                IntPtr hbmMask);

            [PreserveSig]
            int AddMasked(
                IntPtr hbmImage,
                int crMask,
                ref int pi);

            [PreserveSig]
            int Draw(
                ref IMAGELISTDRAWPARAMS pimldp);

            [PreserveSig]
            int Remove(
            int i);

            [PreserveSig]
            int GetIcon(
                int i,
                int flags,
                ref IntPtr picon);

            [PreserveSig]
            int GetImageInfo(
                int i,
                ref IMAGEINFO pImageInfo);

            [PreserveSig]
            int Copy(
                int iDst,
                IImageList punkSrc,
                int iSrc,
                int uFlags);

            [PreserveSig]
            int Merge(
                int i1,
                IImageList punk2,
                int i2,
                int dx,
                int dy,
                ref Guid riid,
                ref IntPtr ppv);

            [PreserveSig]
            int Clone(
                ref Guid riid,
                ref IntPtr ppv);

            [PreserveSig]
            int GetImageRect(
                int i,
                ref Rect prc);

            [PreserveSig]
            int GetIconSize(
                ref int cx,
                ref int cy);

            [PreserveSig]
            int SetIconSize(
                int cx,
                int cy);

            [PreserveSig]
            int GetImageCount(
            ref int pi);

            [PreserveSig]
            int SetImageCount(
                int uNewCount);

            [PreserveSig]
            int SetBkColor(
                int clrBk,
                ref int pclr);

            [PreserveSig]
            int GetBkColor(
                ref int pclr);

            [PreserveSig]
            int BeginDrag(
                int iTrack,
                int dxHotspot,
                int dyHotspot);

            [PreserveSig]
            int EndDrag();

            [PreserveSig]
            int DragEnter(
                IntPtr hwndLock,
                int x,
                int y);

            [PreserveSig]
            int DragLeave(
                IntPtr hwndLock);

            [PreserveSig]
            int DragMove(
                int x,
                int y);

            [PreserveSig]
            int SetDragCursorImage(
                ref IImageList punk,
                int iDrag,
                int dxHotspot,
                int dyHotspot);

            [PreserveSig]
            int DragShowNolock(
                int fShow);

            [PreserveSig]
            int GetDragImage(
                ref POINT ppt,
                ref POINT pptHotspot,
                ref Guid riid,
                ref IntPtr ppv);

            [PreserveSig]
            int GetItemFlags(
                int i,
                ref int dwFlags);

            [PreserveSig]
            int GetOverlayImage(
                int iOverlay,
                ref int piIndex);
        };

        [DllImport(Shell32_DllName, EntryPoint = "#727")]
        public static extern int SHGetImageList(
            int iImageList,
            ref Guid riid,
            out IImageList ppv
            );

        [DllImport(Shell32_DllName)]
        public static extern int SHGetKnownFolderPath(
            [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken,
            out IntPtr ppszPath);

        [Flags]
        public enum KnownFolderFlags : uint
        {
            None = 0,
            Init = 0x00000800,
            DontVerify = 0x00004000,
            Create = 0x00008000,
        }
    }
}

// ----- Interop\NativeMethods.ShFolder.cs -----
namespace ManagedShell.Interop
{
    public partial class NativeMethods
    {
        const string ShFolder_DllName = "shfolder.dll";

        public enum CSIDL
        {
            CSIDL_BITBUCKET = 0x000A, /*RecycleBin*/
            CSIDL_STARTMENU = 0x000B,
            CSIDL_DRIVES = 0x0011, /*MyComputer*/
            CSIDL_COMMON_STARTMENU = 0x0016,
        }//CSIDL

        [DllImport(ShFolder_DllName, CharSet = CharSet.Auto)]
        public static extern int SHGetFolderPath(IntPtr hwndOwner, int nFolder, IntPtr hToken, int dwFlags, StringBuilder lpszPath);
    }
}

// ----- Interop\NativeMethods.ShlwApi.cs -----
namespace ManagedShell.Interop
{
    public partial class NativeMethods
    {
        const string ShlwApi_DllName = "shlwapi.dll";

        [DllImport(ShlwApi_DllName)]
        public static extern IntPtr SHAllocShared(IntPtr hData, uint dwSize, uint dwProcessId);

        [DllImport(ShlwApi_DllName)]
        public static extern IntPtr SHFreeShared(IntPtr hData, uint dwProcessId);

        [DllImport(ShlwApi_DllName)]
        public static extern IntPtr SHLockShared(IntPtr hData, uint dwProcessId);

        [DllImport(ShlwApi_DllName, SetLastError = true)]
        public static extern bool SHUnlockShared(IntPtr pvData);

        [DllImport(ShlwApi_DllName, BestFitMapping = false, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = false, ThrowOnUnmappableChar = true)]
        public static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);
    }
}

// ----- Interop\NativeMethods.User32.cs -----
namespace ManagedShell.Interop
{
    public partial class NativeMethods
    {
        const string User32_DllName = "user32";

        public delegate bool CallBackPtr(IntPtr hwnd, int lParam);

        [DllImport(User32_DllName)]
        public static extern int EnumWindows(CallBackPtr callPtr, int lPar);

        [DllImport(User32_DllName)]
        public static extern int EnumChildWindows(IntPtr hParent, CallBackPtr callPtr, int lPar);

        [DllImport(User32_DllName, CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int Length);

        [DllImport(User32_DllName)]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport(User32_DllName, SetLastError = true)]
        public static extern bool LockWorkStation();



        [DllImport(User32_DllName)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, int uFlags);


        public enum WindowZOrder
        {
            HWND_BOTTOM = 1,
            HWND_TOPMOST = -1,
        }

        [Flags()]
        public enum SetWindowPosFlags
        {
            SWP_NOSIZE = 0x1,
            SWP_NOMOVE = 0x2,
            SWP_NOZORDER = 0x4,
            SWP_NOACTIVATE = 0x10,
            SWP_SHOWWINDOW = 0x40,
            SWP_HIDEWINDOW = 0x80,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPOS
        {
            public IntPtr hWnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public SetWindowPosFlags flags;

            // Returns the WINDOWPOS structure pointed to by the lParam parameter
            // of a WM_WINDOWPOSCHANGING or WM_WINDOWPOSCHANGED message.
            public static WINDOWPOS FromMessage(IntPtr lParam)
            {
                // Marshal the lParam parameter to an WINDOWPOS structure,
                // and return the new structure
                return (WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(WINDOWPOS));
            }

            // Replaces the original WINDOWPOS structure pointed to by the lParam
            // parameter of a WM_WINDOWPOSCHANGING or WM_WINDOWPSCHANGING message
            // with this one, so that the native window will be able to see any
            // changes that we have made to its values.
        }

        public enum ExitWindows : uint
        {
            /// <summary>
            /// Log the user off.
            /// </summary>
            Logoff = 0x00,

            /// <summary>
            /// Shutdown the machine.
            /// </summary>
            Shutdown = 0x08,

            /// <summary>
            /// Reboots the machine.
            /// </summary>
            Reboot = 0x02,

            /// <summary>
            /// Forces the machine to perform the operation if the apps are hung.
            /// Use this in conjunction with one of the lower flags.
            /// </summary>
            ForceIfHung = 0x10
        }

        [DllImport(User32_DllName)]
        public static extern bool ExitWindowsEx(uint flags, uint reason);

        [DllImport(User32_DllName, SetLastError = true)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport(User32_DllName, CharSet = CharSet.Auto)]
        public static extern int RegisterWindowMessage(string msg);

        [DllImport(User32_DllName)]
        public static extern int GetSystemMetrics(int Index);

        [DllImport(User32_DllName)]
        public static extern int DestroyIcon(IntPtr hIcon);

        [DllImport(User32_DllName, SetLastError = true)]
        public static extern IntPtr GetWindow(IntPtr hWnd, GetWindow_Cmd uCmd);

        public enum GetWindow_Cmd : uint
        {
            GW_OWNER = 4,
        }

        [Flags]
        public enum ExtendedWindowStyles : uint
        {
            WS_EX_TOPMOST = unchecked(0x8),
            WS_EX_TRANSPARENT = 0x20,
            WS_EX_TOOLWINDOW = 0x80,
            WS_EX_WINDOWEDGE = 0x100,
            WS_EX_APPWINDOW = 0x40000,
            WS_EX_LAYERED = 0x80000,
            WS_EX_NOACTIVATE = 0x8000000
        }

        [Flags]
        public enum WindowStyles : uint
        {
            WS_POPUP = 0x80000000,
            WS_CHILD = 0x40000000,
            WS_VISIBLE = 0x10000000,
            WS_DISABLED = 0x8000000,
            WS_CLIPSIBLINGS = 0x4000000,
            WS_CLIPCHILDREN = 0x2000000,
            WS_CAPTION = (WS_BORDER | WS_DLGFRAME),
            WS_BORDER = 0x800000,
            WS_DLGFRAME = 0x400000,
            WS_THICKFRAME = 0x40000,
            WS_MINIMIZEBOX = 0x20000,
        }


        public enum WindowLongFlags : int
        {
            GWL_EXSTYLE = -20,
            GWL_STYLE = -16,
        }

        public const int HSHELL_HIGHBIT = 0x8000;

        public const int SC_MINIMIZE = 0xF020;
        public const int SC_MAXIMIZE = 0xF030;
        public const int SC_MOVE = 0xF010;
        public const int SC_RESTORE = 0xF120;
        public const int SC_SIZE = 0xF000;
        public const int SC_CLOSE = 0xF060;


        // Specifies how TrackPopupMenuEx positions the shortcut menu horizontally
        [Flags]
        public enum TPM : uint
        {
            LEFTBUTTON = 0x0000,
            RIGHTBUTTON = 0x0002,
            HORIZONTAL = 0x0000,
            VERTICAL = 0x0040,
            WORKAREA = 0x10000
        }

        [DllImport(User32_DllName, SetLastError = true)]
        public static extern bool GetCursorPos(out POINT lpPoint);



        // Displays a shortcut menu at the specified location and 
        // tracks the selection of items on the shortcut menu
        [DllImport(User32_DllName, ExactSpelling = true, SetLastError = true)]
        public static extern uint TrackPopupMenuEx(IntPtr hMenu, TPM uFlags, int x, int y, IntPtr hWnd, IntPtr lpTPMParams);

        [StructLayout(LayoutKind.Sequential)]
        public struct COPYDATASTRUCT
        {
            public IntPtr dwData;    // Any value the sender chooses.  Perhaps its main window handle?
            public int cbData;       // The count of bytes in the message.
            public IntPtr lpData;    // The address of the message.
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SHELLHOOKINFO
        {
            public IntPtr hwnd;
            public ShortRect rc;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MinimizedMetrics
        {
            public uint cbSize;
            public int iWidth;
            public int iHorzGap;
            public int iVertGap;
            public MinimizedMetricsArrangement iArrange;
        }

        [Flags]
        public enum MinimizedMetricsArrangement
        {
            Left = 0,
            Right = 0,
            Up = 4,
            Down = 4,
            Hide = 8
        }

        public delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport(User32_DllName, SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport(User32_DllName, EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLong64(IntPtr hWnd, int nIndex);

        public static int GetWindowLong(IntPtr hWnd, WindowLongFlags nIndex)
        {
            return GetWindowLong(hWnd, (int)nIndex);
        }

        public static IntPtr GetWindowLongPtr(IntPtr hWnd, WindowLongFlags nIndex)
        {
            return GetWindowLongPtr(hWnd, (int)nIndex);
        }

        // This static method is required because Win32 does not support
        // GetWindowLongPtr directly
        public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8)
                return GetWindowLong64(hWnd, nIndex);
            return (IntPtr)GetWindowLong(hWnd, nIndex);
        }


        [DllImport(User32_DllName)]
        public static extern uint SendMessageTimeout(IntPtr hWnd, uint messageId, IntPtr wparam, IntPtr lparam, uint timeoutFlags, uint timeout, ref IntPtr retval);

        [DllImport(User32_DllName)]
        public static extern uint SendMessageTimeout(IntPtr hWnd, uint messageId, uint wparam, uint lparam, uint timeoutFlags, uint timeout, ref IntPtr retval);

        [DllImport(User32_DllName)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int messageId, IntPtr wparam, IntPtr lparam);


        [DllImport(User32_DllName)]
        public static extern IntPtr GetClassLong(IntPtr handle, int longClass);

        [DllImport(User32_DllName)]
        public static extern IntPtr GetClassLongPtr(IntPtr handle, int longClass);

        [DllImport(User32_DllName)]
        public static extern bool AllowSetForegroundWindow(uint procId);

        [DllImport(User32_DllName)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport(User32_DllName)]
        public static extern IntPtr GetLastActivePopup(IntPtr hWnd);

        [DllImport(User32_DllName)]
        public static extern IntPtr GetParent(IntPtr handle);

        public struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public WindowShowStyle showCmd;
            public Point ptMinPosition;
            public Point ptMaxPosition;
            public Rectangle rcNormalPosition;
        }

        [DllImport(User32_DllName)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport(User32_DllName)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport(User32_DllName, SetLastError = true)]
        public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref Rect pvParam, uint fWinIni);

        [DllImport(User32_DllName, SetLastError = true)]
        public static extern bool SystemParametersInfo(SPI uiAction, uint uiParam, string pvParam, SPIF fWinIni);

        /// <summary>
        /// SPI System-wide parameter - Used in SystemParametersInfo function
        /// </summary>
        [Description("SPI (System-wide parameter - Used in SystemParametersInfo function )")]
        public enum SPI : uint
        {




























            /// <summary>
            /// Sets the double-click time for the mouse to the value of the uiParam parameter. The double-click time is the maximum number
            /// of milliseconds that can occur between the first and second clicks of a double-click. You can also call the SetDoubleClickTime
            /// function to set the double-click time. To get the current double-click time, call the GetDoubleClickTime function.
            /// </summary>
            SETDOUBLECLICKTIME = 0x0020,





            //#if(WINVER >= 0x0400)




            /// <summary>
            /// Retrieves the metrics associated with minimized windows. The pvParam parameter must point to a MINIMIZEDMETRICS structure
            /// that receives the information. Set the cbSize member of this structure and the uiParam parameter to sizeof(MINIMIZEDMETRICS).
            /// </summary>
            GETMINIMIZEDMETRICS = 0x002B,

            /// <summary>
            /// Sets the metrics associated with minimized windows. The pvParam parameter must point to a MINIMIZEDMETRICS structure
            /// that contains the new parameters. Set the cbSize member of this structure and the uiParam parameter to sizeof(MINIMIZEDMETRICS).
            /// </summary>
            SETMINIMIZEDMETRICS = 0x002C,






            /// <summary>
            /// Retrieves information about the HighContrast accessibility feature. The pvParam parameter must point to a HIGHCONTRAST structure
            /// that receives the information. Set the cbSize member of this structure and the uiParam parameter to sizeof(HIGHCONTRAST).
            /// For a general discussion, see remarks.
            /// Windows NT:  This value is not supported.
            /// </summary>
            /// <remarks>
            /// There is a difference between the High Contrast color scheme and the High Contrast Mode. The High Contrast color scheme changes
            /// the system colors to colors that have obvious contrast; you switch to this color scheme by using the Display Options in the control panel.
            /// The High Contrast Mode, which uses GETHIGHCONTRAST and SETHIGHCONTRAST, advises applications to modify their appearance
            /// for visually-impaired users. It involves such things as audible warning to users and customized color scheme
            /// (using the Accessibility Options in the control panel). For more information, see HIGHCONTRAST on MSDN.
            /// For more information on general accessibility features, see Accessibility on MSDN.
            /// </remarks>
            GETHIGHCONTRAST = 0x0042,

            /// <summary>
            /// Sets the parameters of the HighContrast accessibility feature. The pvParam parameter must point to a HIGHCONTRAST structure
            /// that contains the new parameters. Set the cbSize member of this structure and the uiParam parameter to sizeof(HIGHCONTRAST).
            /// Windows NT:  This value is not supported.
            /// </summary>
            SETHIGHCONTRAST = 0x0043,





























            //#endif /* WINVER >= 0x0400 */













            //#if(WINVER >= 0x0400)

            //#endif /* WINVER >= 0x0400 */



            //#if(_WIN32_WINNT >= 0x0400)

            //#endif /* _WIN32_WINNT >= 0x0400 */

            //#if (_WIN32_WINNT >= 0x0400) || (_WIN32_WINDOWS > 0x0400)











            //#endif

            //#if(WINVER >= 0x0500)



            //#endif /* WINVER >= 0x0500 */

            //#if(WINVER >= 0x0500)



            /// <summary>
            /// Enables or disables menu animation. This master switch must be on for any menu animation to occur.
            /// The pvParam parameter is a BOOL variable; set pvParam to TRUE to enable animation and FALSE to disable animation.
            /// If animation is enabled, GETMENUFADE indicates whether menus use fade or slide animation.
            /// Windows NT, Windows 95:  This value is not supported.
            /// </summary>
            SETMENUANIMATION = 0x1003,















            /// <summary>
            /// Determines whether menu fade animation is enabled. The pvParam parameter must point to a BOOL variable that receives TRUE
            /// when fade animation is enabled and FALSE when it is disabled. If fade animation is disabled, menus use slide animation.
            /// This flag is ignored unless menu animation is enabled, which you can do using the SETMENUANIMATION flag.
            /// For more information, see AnimateWindow.
            /// Windows NT, Windows Me/98/95:  This value is not supported.
            /// </summary>
            GETMENUFADE = 0x1012,





            /// <summary>
            /// Set pvParam to TRUE to enable ToolTip animation or FALSE to disable it. If enabled, you can use SETTOOLTIPFADE
            /// to specify fade or slide animation.
            /// Windows NT, Windows Me/98/95:  This value is not supported.
            /// </summary>
            SETTOOLTIPANIMATION = 0x1017,

            /// <summary>
            /// If SETTOOLTIPANIMATION is enabled, GETTOOLTIPFADE indicates whether ToolTip animation uses a fade effect or a slide effect.
            ///  The pvParam parameter must point to a BOOL variable that receives TRUE for fade animation or FALSE for slide animation.
            ///  For more information on slide and fade effects, see AnimateWindow.
            /// Windows NT, Windows Me/98/95:  This value is not supported.
            /// </summary>
            GETTOOLTIPFADE = 0x1018,

            /// <summary>
            /// If the SETTOOLTIPANIMATION flag is enabled, use SETTOOLTIPFADE to indicate whether ToolTip animation uses a fade effect
            /// or a slide effect. Set pvParam to TRUE for fade animation or FALSE for slide animation. The tooltip fade effect is possible only
            /// if the system has a color depth of more than 256 colors. For more information on the slide and fade effects,
            /// see the AnimateWindow function.
            /// Windows NT, Windows Me/98/95:  This value is not supported.
            /// </summary>
            SETTOOLTIPFADE = 0x1019,



            //#if(_WIN32_WINNT >= 0x0501)











            //#endif /* _WIN32_WINNT >= 0x0501 */











            //#if(_WIN32_WINNT >= 0x0501)

            /// <summary>
            /// Turns the Mouse ClickLock accessibility feature on or off. This feature temporarily locks down the primary mouse button
            /// when that button is clicked and held down for the time specified by SETMOUSECLICKLOCKTIME. The uiParam parameter
            /// specifies TRUE for on, or FALSE for off. The default is off. For more information, see Remarks and About Mouse Input on MSDN.
            /// Windows 2000/NT, Windows 98/95:  This value is not supported.
            /// </summary>
            SETMOUSECLICKLOCKTIME = 0x2009,











            /// <summary>
            /// Retrieves the time that notification pop-ups should be displayed, in seconds.
            /// </summary>
            GETMESSAGEDURATION = 0x2016,
        }

        [Flags]
        public enum SPIF : uint
        {
            None = 0x00,
        }

        /// <summary>Shows a Window</summary>
        /// <remarks>
        /// <para>To perform certain special effects when showing or hiding a 
        /// window, use AnimateWindow.</para>
        ///<para>The first time an application calls ShowWindow, it should use 
        ///the WinMain function's nCmdShow parameter as its nCmdShow parameter. 
        ///Subsequent calls to ShowWindow must use one of the values in the 
        ///given list, instead of the one specified by the WinMain function's 
        ///nCmdShow parameter.</para>
        ///<para>As noted in the discussion of the nCmdShow parameter, the 
        ///nCmdShow value is ignored in the first call to ShowWindow if the 
        ///program that launched the application specifies startup information 
        ///in the structure. In this case, ShowWindow uses the information 
        ///specified in the STARTUPINFO structure to show the window. On 
        ///subsequent calls, the application must call ShowWindow with nCmdShow 
        ///set to SW_SHOWDEFAULT to use the startup information provided by the 
        ///program that launched the application. This behavior is designed for 
        ///the following situations: </para>
        ///<list type="">
        ///    <item>Applications create their main window by calling CreateWindow 
        ///    with the WS_VISIBLE flag set. </item>
        ///    <item>Applications create their main window by calling CreateWindow 
        ///    with the WS_VISIBLE flag cleared, and later call ShowWindow with the 
        ///    SW_SHOW flag set to make it visible.</item>
        ///</list></remarks>
        /// <param name="hWnd">Handle to the window.</param>
        /// <param name="nCmdShow">Specifies how the window is to be shown. 
        /// This parameter is ignored the first time an application calls 
        /// ShowWindow, if the program that launched the application provides a 
        /// STARTUPINFO structure. Otherwise, the first time ShowWindow is called, 
        /// the value should be the value obtained by the WinMain function in its 
        /// nCmdShow parameter. In subsequent calls, this parameter can be one of 
        /// the WindowShowStyle members.</param>
        /// <returns>
        /// If the window was previously visible, the return value is nonzero. 
        /// If the window was previously hidden, the return value is zero.
        /// </returns>
        [DllImport(User32_DllName)]
        public static extern bool ShowWindow(IntPtr hWnd, WindowShowStyle nCmdShow);

        /// <summary>Enumeration of the different ways of showing a window using 
        /// ShowWindow</summary>
        public enum WindowShowStyle : uint
        {
            /// <summary>Hides the window and activates another window.</summary>
            /// <remarks>See SW_HIDE</remarks>
            Hide = 0,
            /// <summary>Activates the window and displays it as a maximized window.</summary>
            /// <remarks>See SW_SHOWMAXIMIZED</remarks>
            ShowMaximized = 3,
            /// <summary>Maximizes the specified window.</summary>
            /// <remarks>See SW_MAXIMIZE</remarks>
            Maximize = 3,
            /// <summary>Activates the window and displays it in its current size 
            /// and position.</summary>
            /// <remarks>See SW_SHOW</remarks>
            Show = 5,
            /// <summary>Minimizes the specified window and activates the next 
            /// top-level window in the Z order.</summary>
            /// <remarks>See SW_MINIMIZE</remarks>
            Minimize = 6,
            /// <summary>Activates and displays the window. If the window is 
            /// minimized or maximized, the system restores it to its original size 
            /// and position. An application should specify this flag when restoring 
            /// a minimized window.</summary>
            /// <remarks>See SW_RESTORE</remarks>
            Restore = 9,
        }


        [DllImport(User32_DllName)]
        public static extern bool RegisterShellHookWindow(IntPtr hWnd);

        [DllImport(User32_DllName)]
        public static extern bool SetTaskmanWindow(IntPtr hWnd);

        [DllImport(User32_DllName)]
        public static extern IntPtr GetForegroundWindow();

        [DllImport(User32_DllName)]
        public static extern bool DeregisterShellHookWindow(IntPtr hWnd);

        [DllImport(User32_DllName)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);



        [DllImport(User32_DllName)]
        public static extern bool SystemParametersInfo(SPI uiAction, uint uiParam, IntPtr pvParam, SPIF fWinIni);

        [DllImport(User32_DllName)]
        public static extern bool SystemParametersInfo(SPI uiAction, uint uiParam, ref uint pvParam, SPIF fWinIni);


        [DllImport(User32_DllName)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport(User32_DllName)]
        public static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport(User32_DllName)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport(User32_DllName, EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLong64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public static int SetWindowLong(IntPtr hWnd, WindowLongFlags nIndex, int dwNewLong)
        {
            return SetWindowLong(hWnd, (int)nIndex, dwNewLong);
        }

        public static IntPtr SetWindowLongPtr(IntPtr hWnd, WindowLongFlags nIndex, IntPtr dwNewLong)
        {
            return SetWindowLongPtr(hWnd, (int)nIndex, dwNewLong);
        }

        // This static method is required because Win32 does not support
        // SetWindowLongPtr directly
        public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
                return SetWindowLong64(hWnd, nIndex, dwNewLong);
            return (IntPtr)SetWindowLong(hWnd, nIndex, dwNewLong.ToInt32());
        }

        [DllImport(User32_DllName, SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport(User32_DllName, SetLastError = true)]
        public static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, IntPtr className, string windowTitle);

        [DllImport(User32_DllName, SetLastError = true)]
        public static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string windowTitle);

        [DllImport(User32_DllName, SetLastError = true)]
        public static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, IntPtr windowTitle);

        [DllImport(User32_DllName, CharSet = CharSet.Auto)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport(User32_DllName)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        // Begin Windows 10 blur
        // https://github.com/riverar/sample-win32-acrylicblur
        // License: MIT

        [DllImport(User32_DllName)]
        public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [StructLayout(LayoutKind.Sequential)]
        public struct WindowCompositionAttributeData
        {
            public WindowCompositionAttribute Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        public enum WindowCompositionAttribute
        {
            // ...
            WCA_ACCENT_POLICY = 19
            // ...
        }

        public enum AccentState
        {
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct AccentPolicy
        {
            public AccentState AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        //
        // End Windows 10 blur

        [DllImport(User32_DllName)]
        public static extern uint SendInput(uint numberOfInputs, INPUT[] input, int structSize);

        [DllImport(User32_DllName, SetLastError = false)]
        public static extern IntPtr GetMessageExtraInfo();

        [DllImport(User32_DllName)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, UInt32 fsModifiers, UInt32 vlc);


        public const int INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_KEYUP = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            int dx;
            int dy;
            uint mouseData;
            uint dwFlags;
            uint time;
            IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            uint uMsg;
            ushort wParamL;
            ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct MouseKeybdHardwareInputUnion
        {
            [FieldOffset(0)]
            public MOUSEINPUT mi;

            [FieldOffset(0)]
            public KEYBDINPUT ki;

            [FieldOffset(0)]
            public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public int type;
            public MouseKeybdHardwareInputUnion mkhi;
        }

        [DllImport(User32_DllName)]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, int uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport(User32_DllName)]
        public extern static short GetAsyncKeyState(int key);

        /// <summary>
        /// Sets the windows hook, do the desired event, one of hInstance or threadId must be non-null
        /// </summary>
        /// <param name="idHook">The id of the event you want to hook</param>
        /// <param name="callback">The callback.</param>
        /// <param name="hInstance">The handle you want to attach the event to, can be null</param>
        /// <param name="threadId">The thread you want to attach the event to, can be null</param>
        /// <returns>a handle to the desired hook</returns>
        [DllImport(User32_DllName)]
        public static extern IntPtr SetWindowsHookEx(int idHook, keyboardHookProc callback, IntPtr hInstance, uint threadId);

        /// <summary>
        /// defines the callback type for the hook
        /// </summary>
        public delegate int keyboardHookProc(int code, int wParam, ref keyboardHookStruct lParam);

        public struct keyboardHookStruct
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public int dwExtraInfo;
        }

        /// <summary>
        /// Unhooks the windows hook.
        /// </summary>
        /// <param name="hInstance">The hook handle that was returned from SetWindowsHookEx</param>
        /// <returns>True if successful, false otherwise</returns>
        [DllImport(User32_DllName)]
        public static extern bool UnhookWindowsHookEx(IntPtr hInstance);

        /// <summary>
        /// Calls the next hook.
        /// </summary>
        /// <param name="idHook">The hook id</param>
        /// <param name="nCode">The hook code</param>
        /// <param name="wParam">The wparam.</param>
        /// <param name="lParam">The lparam.</param>
        /// <returns></returns>
        [DllImport(User32_DllName)]
        public static extern int CallNextHookEx(IntPtr idHook, int nCode, int wParam, ref keyboardHookStruct lParam);

        /// <summary>
        /// The SetProp function adds a new entry or changes an existing entry in the property list of the specified window. The function adds a new entry to the list if the specified character string does not exist already in the list. The new entry contains the string and the handle. Otherwise, the function replaces the string's current handle with the specified handle.
        /// </summary>
        /// <remarks>
        /// <para>Before a window is destroyed (that is, before it returns from processing the WM_NCDESTROY message), an application must remove all entries it has added to the property list. The application must use the RemoveProp function to remove the entries.</para>
        /// <para>Windows 95/98/Me: SetPropW is supported by the Microsoft Layer for Unicode (MSLU). SetPropA is also supported to provide more consistent behavior across all Microsoft Windows operating systems. To use this, you must add certain files to your application, as outlined in Microsoft Layer for Unicode on Windows 95/98/Me Systems.</para>
        /// <para>Windows Vista: SetProp is subject to the restrictions of User Interface Privilege Isolation (UIPI). A process can only call this function on a window belonging to a process of lesser or equal integrity level. When UIPI blocks property changes, GetLastError will return 5.</para>
        /// </remarks>
        /// <param name="hWnd">Handle to the window whose property list receives the new entry.</param>
        /// <param name="lpString">Pointer to a null-terminated string or contains an atom that identifies a string. If this parameter is an atom, it must be a global atom created by a previous call to the GlobalAddAtom function. The atom must be placed in the low-order word of lpString; the high-order word must be zero.</param>
        /// <param name="hData">Handle to the data to be copied to the property list. The data handle can identify any value useful to the application. </param>
        /// <returns>If the data handle and string are added to the property list, the return value is nonzero. If the function fails, the return value is zero. To get extended error information, call GetLastError.</returns>
        [DllImport(User32_DllName, SetLastError = true)]
        public static extern bool SetProp(IntPtr hWnd, string lpString, IntPtr hData);

        [DllImport(User32_DllName, SetLastError = true)]
        public static extern IntPtr RemoveProp(IntPtr hWnd, string lpString);

        [DllImport(User32_DllName, SetLastError = true)]
        public static extern IntPtr GetProp(IntPtr hWnd, string lpString);

        public enum TBPFLAG
        {
            TBPF_NOPROGRESS = 0,
        }

        [DllImport(User32_DllName, SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool SendNotifyMessage(IntPtr hWnd, uint Msg, UIntPtr wParam, IntPtr lParam);

        [DllImport(User32_DllName, SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool SendNotifyMessage(IntPtr hWnd, uint Msg, uint wParam, uint lParam);

        public static IntPtr HWND_BROADCAST = new IntPtr(0xffff);
        public static int WINEVENT_OUTOFCONTEXT = 0;
        public static int WINEVENT_SKIPOWNPROCESS = 2;
        public static int EVENT_OBJECT_CLOAKED = 0x8017;
        public static int EVENT_OBJECT_UNCLOAKED = 0x8018;
        public static int EVENT_OBJECT_LOCATIONCHANGE = 0x800B;

        public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport(User32_DllName, SetLastError = true)]
        public static extern IntPtr SetWinEventHook(int eventMin, int eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, int idProcess, int idThread, int dwflags);

        [DllImport(User32_DllName, SetLastError = true)]
        public static extern int UnhookWinEvent(IntPtr hWinEventHook);

        /// <summary>
        /// Windows Messages
        /// Defined in winuser.h from Windows SDK v6.1
        /// Documentation pulled from MSDN.
        /// </summary>
        public enum WM : uint
        {
            /// <summary>
            /// The WM_NULL message performs no operation. An application sends the WM_NULL message if it wants to post a message that the recipient window will ignore.
            /// </summary>
            NULL = 0x0000,
            /// <summary>
            /// The WM_CREATE message is sent when an application requests that a window be created by calling the CreateWindowEx or CreateWindow function. (The message is sent before the function returns.) The window procedure of the new window receives this message after the window is created, but before the window becomes visible.
            /// </summary>
            CREATE = 0x0001,
            /// <summary>
            /// The WM_DESTROY message is sent when a window is being destroyed. It is sent to the window procedure of the window being destroyed after the window is removed from the screen. 
            /// This message is sent first to the window being destroyed and then to the child windows (if any) as they are destroyed. During the processing of the message, it can be assumed that all child windows still exist.
            /// /// </summary>
            DESTROY = 0x0002,
            /// <summary>
            /// The WM_MOVE message is sent after a window has been moved. 
            /// </summary>
            MOVE = 0x0003,
            /// <summary>
            /// The WM_SIZE message is sent to a window after its size has changed.
            /// </summary>
            SIZE = 0x0005,
            /// <summary>
            /// The WM_ACTIVATE message is sent to both the window being activated and the window being deactivated. If the windows use the same input queue, the message is sent synchronously, first to the window procedure of the top-level window being deactivated, then to the window procedure of the top-level window being activated. If the windows use different input queues, the message is sent asynchronously, so the window is activated immediately. 
            /// </summary>
            ACTIVATE = 0x0006,
            /// <summary>
            /// The WM_ENABLE message is sent when an application changes the enabled state of a window. It is sent to the window whose enabled state is changing. This message is sent before the EnableWindow function returns, but after the enabled state (WS_DISABLED style bit) of the window has changed. 
            /// </summary>
            ENABLE = 0x000A,
            /// <summary>
            /// An application sends a WM_SETTEXT message to set the text of a window. 
            /// </summary>
            SETTEXT = 0x000C,
            /// <summary>
            /// The WM_PAINT message is sent when the system or another application makes a request to paint a portion of an application's window. The message is sent when the UpdateWindow or RedrawWindow function is called, or by the DispatchMessage function when the application obtains a WM_PAINT message by using the GetMessage or PeekMessage function. 
            /// </summary>
            PAINT = 0x000F,
            /// <summary>
            /// The WM_CLOSE message is sent as a signal that a window or an application should terminate.
            /// </summary>
            CLOSE = 0x0010,
            /// <summary>
            /// The WM_QUIT message indicates a request to terminate an application and is generated when the application calls the PostQuitMessage function. It causes the GetMessage function to return zero.
            /// </summary>
            QUIT = 0x0012,
            /// <summary>
            /// The WM_SHOWWINDOW message is sent to a window when the window is about to be hidden or shown.
            /// </summary>
            SHOWWINDOW = 0x0018,
            /// <summary>
            /// The WM_ACTIVATEAPP message is sent when a window belonging to a different application than the active window is about to be activated. The message is sent to the application whose window is being activated and to the application whose window is being deactivated.
            /// </summary>
            ACTIVATEAPP = 0x001C,
            /// <summary>
            /// The WM_QUERYDRAGICON message is sent to a minimized (iconic) window. The window is about to be dragged by the user but does not have an icon defined for its class. An application can return a handle to an icon or cursor. The system displays this cursor or icon while the user drags the icon.
            /// </summary>
            QUERYDRAGICON = 0x0037,
            /// <summary>
            /// Active Accessibility sends the WM_GETOBJECT message to obtain information about an accessible object contained in a server application. 
            /// Applications never send this message directly. It is sent only by Active Accessibility in response to calls to AccessibleObjectFromPoint, AccessibleObjectFromEvent, or AccessibleObjectFromWindow. However, server applications handle this message. 
            /// </summary>
            GETOBJECT = 0x003D,
            /// <summary>
            /// The WM_COMPACTING message is sent to all top-level windows when the system detects more than 12.5 percent of system time over a 30- to 60-second interval is being spent compacting memory. This indicates that system memory is low.
            /// </summary>
            COMPACTING = 0x0041,
            /// <summary>
            /// The WM_WINDOWPOSCHANGED message is sent to a window whose size, position, or place in the Z order has changed as a result of a call to the SetWindowPos function or another window-management function.
            /// </summary>
            WINDOWPOSCHANGED = 0x0047,
            /// <summary>
            /// Notifies applications that the system, typically a battery-powered personal computer, is about to enter a suspended mode.
            /// Use: POWERBROADCAST
            /// </summary>
            [Obsolete]
            POWER = 0x0048,
            /// <summary>
            /// An application sends the WM_COPYDATA message to pass data to another application. 
            /// </summary>
            COPYDATA = 0x004A,
            /// <summary>
            /// Sent by a common control to its parent window when an event has occurred or the control requires some information. 
            /// </summary>
            NOTIFY = 0x004E,
            /// <summary>
            /// Indicates that the user pressed the F1 key. If a menu is active when F1 is pressed, WM_HELP is sent to the window associated with the menu; otherwise, WM_HELP is sent to the window that has the keyboard focus. If no window has the keyboard focus, WM_HELP is sent to the currently active window. 
            /// </summary>
            HELP = 0x0053,
            /// <summary>
            /// The WM_CONTEXTMENU message notifies a window that the user clicked the right mouse button (right-clicked) in the window.
            /// </summary>
            CONTEXTMENU = 0x007B,
            /// <summary>
            /// The WM_DISPLAYCHANGE message is sent to all windows when the display resolution has changed.
            /// </summary>
            DISPLAYCHANGE = 0x007E,
            /// <summary>
            /// The WM_GETICON message is sent to a window to retrieve a handle to the large or small icon associated with a window. The system displays the large icon in the ALT+TAB dialog, and the small icon in the window caption. 
            /// </summary>
            GETICON = 0x007F,
            /// <summary>
            /// An application sends the WM_SETICON message to associate a new large or small icon with a window. The system displays the large icon in the ALT+TAB dialog box, and the small icon in the window caption. 
            /// </summary>
            SETICON = 0x0080,
            /// <summary>
            /// The WM_INPUT message is sent to the window that is getting raw input. 
            /// </summary>
            INPUT = 0x00FF,
            /// <summary>
            /// The WM_KEYDOWN message is posted to the window with the keyboard focus when a nonsystem key is pressed. A nonsystem key is a key that is pressed when the ALT key is not pressed. 
            /// </summary>
            KEYDOWN = 0x0100,
            /// <summary>
            /// The WM_KEYUP message is posted to the window with the keyboard focus when a nonsystem key is released. A nonsystem key is a key that is pressed when the ALT key is not pressed, or a keyboard key that is pressed when a window has the keyboard focus. 
            /// </summary>
            KEYUP = 0x0101,
            /// <summary>
            /// The WM_CHAR message is posted to the window with the keyboard focus when a WM_KEYDOWN message is translated by the TranslateMessage function. The WM_CHAR message contains the character code of the key that was pressed. 
            /// </summary>
            CHAR = 0x0102,
            /// <summary>
            /// The WM_COMMAND message is sent when the user selects a command item from a menu, when a control sends a notification message to its parent window, or when an accelerator keystroke is translated. 
            /// </summary>
            COMMAND = 0x0111,
            /// <summary>
            /// A window receives this message when the user chooses a command from the Window menu, clicks the maximize button, minimize button, restore button, close button, or moves the form. You can stop the form from moving by filtering this out.
            /// </summary>
            SYSCOMMAND = 0x0112,
            /// <summary>
            /// The WM_TIMER message is posted to the installing thread's message queue when a timer expires. The message is posted by the GetMessage or PeekMessage function. 
            /// </summary>
            TIMER = 0x0113,
            /// <summary>
            /// The WM_LBUTTONDOWN message is posted when the user presses the left mouse button while the cursor is in the client area of a window. If the mouse is not captured, the message is posted to the window beneath the cursor. Otherwise, the message is posted to the window that has captured the mouse.
            /// </summary>
            LBUTTONDOWN = 0x0201,
            /// <summary>
            /// The WM_LBUTTONUP message is posted when the user releases the left mouse button while the cursor is in the client area of a window. If the mouse is not captured, the message is posted to the window beneath the cursor. Otherwise, the message is posted to the window that has captured the mouse.
            /// </summary>
            LBUTTONUP = 0x0202,
            /// <summary>
            /// The WM_LBUTTONDBLCLK message is posted when the user double-clicks the left mouse button while the cursor is in the client area of a window. If the mouse is not captured, the message is posted to the window beneath the cursor. Otherwise, the message is posted to the window that has captured the mouse.
            /// </summary>
            LBUTTONDBLCLK = 0x0203,
            /// <summary>
            /// The WM_RBUTTONDOWN message is posted when the user presses the right mouse button while the cursor is in the client area of a window. If the mouse is not captured, the message is posted to the window beneath the cursor. Otherwise, the message is posted to the window that has captured the mouse.
            /// </summary>
            RBUTTONDOWN = 0x0204,
            /// <summary>
            /// The WM_RBUTTONUP message is posted when the user releases the right mouse button while the cursor is in the client area of a window. If the mouse is not captured, the message is posted to the window beneath the cursor. Otherwise, the message is posted to the window that has captured the mouse.
            /// </summary>
            RBUTTONUP = 0x0205,
            /// <summary>
            /// The WM_RBUTTONDBLCLK message is posted when the user double-clicks the right mouse button while the cursor is in the client area of a window. If the mouse is not captured, the message is posted to the window beneath the cursor. Otherwise, the message is posted to the window that has captured the mouse.
            /// </summary>
            RBUTTONDBLCLK = 0x0206,
            /// <summary>
            /// The WM_MBUTTONDOWN message is posted when the user presses the middle mouse button while the cursor is in the client area of a window. If the mouse is not captured, the message is posted to the window beneath the cursor. Otherwise, the message is posted to the window that has captured the mouse.
            /// </summary>
            MBUTTONDOWN = 0x0207,
            /// <summary>
            /// The WM_MBUTTONUP message is posted when the user releases the middle mouse button while the cursor is in the client area of a window. If the mouse is not captured, the message is posted to the window beneath the cursor. Otherwise, the message is posted to the window that has captured the mouse.
            /// </summary>
            MBUTTONUP = 0x0208,
            /// <summary>
            /// The WM_MBUTTONDBLCLK message is posted when the user double-clicks the middle mouse button while the cursor is in the client area of a window. If the mouse is not captured, the message is posted to the window beneath the cursor. Otherwise, the message is posted to the window that has captured the mouse.
            /// </summary>
            MBUTTONDBLCLK = 0x0209,
            /// <summary>
            /// The WM_MOVING message is sent to a window that the user is moving. By processing this message, an application can monitor the position of the drag rectangle and, if needed, change its position.
            /// </summary>
            MOVING = 0x0216,
            /// <summary>
            /// Notifies applications that a power-management event has occurred.
            /// </summary>
            POWERBROADCAST = 0x0218,
            /// <summary>
            /// Notifies an application of a change to the hardware configuration of a device or the computer.
            /// </summary>
            DEVICECHANGE = 0x0219,
            /// <summary>
            /// The WM_MOUSEHOVER message is posted to a window when the cursor hovers over the client area of the window for the period of time specified in a prior call to TrackMouseEvent.
            /// </summary>
            MOUSEHOVER = 0x02A1,
            /// <summary>
            /// The WM_MOUSELEAVE message is posted to a window when the cursor leaves the client area of the window specified in a prior call to TrackMouseEvent.
            /// </summary>
            MOUSELEAVE = 0x02A3,
            /// <summary>
            /// An application sends a WM_CUT message to an edit control or combo box to delete (cut) the current selection, if any, in the edit control and copy the deleted text to the clipboard in CF_TEXT format. 
            /// </summary>
            CUT = 0x0300,
            /// <summary>
            /// An application sends the WM_COPY message to an edit control or combo box to copy the current selection to the clipboard in CF_TEXT format. 
            /// </summary>
            COPY = 0x0301,
            /// <summary>
            /// An application sends a WM_PASTE message to an edit control or combo box to copy the current content of the clipboard to the edit control at the current caret position. Data is inserted only if the clipboard contains data in CF_TEXT format. 
            /// </summary>
            PASTE = 0x0302,
            /// <summary>
            /// An application sends a WM_CLEAR message to an edit control or combo box to delete (clear) the current selection, if any, from the edit control. 
            /// </summary>
            CLEAR = 0x0303,
            /// <summary>
            /// An application sends a WM_UNDO message to an edit control to undo the last operation. When this message is sent to an edit control, the previously deleted text is restored or the previously added text is deleted.
            /// </summary>
            UNDO = 0x0304,
            /// <summary>
            /// The WM_HOTKEY message is posted when the user presses a hot key registered by the RegisterHotKey function. The message is placed at the top of the message queue associated with the thread that registered the hot key. 
            /// </summary>
            HOTKEY = 0x0312,
            /// <summary>
            /// The WM_PRINT message is sent to a window to request that it draw itself in the specified device context, most commonly in a printer device context.
            /// </summary>
            PRINT = 0x0317,
            /// <summary>
            /// The WM_APPCOMMAND message notifies a window that the user generated an application command event, for example, by clicking an application command button using the mouse or typing an application command key on the keyboard.
            /// </summary>
            APPCOMMAND = 0x0319,


            /// <summary>
            /// The WM_APP constant is used by applications to help define private messages, usually of the form WM_APP+X, where X is an integer value. 
            /// </summary>
            APP = 0x8000,
            /// <summary>
            /// The WM_USER constant is used by applications to help define private messages for use by private window classes, usually of the form WM_USER+X, where X is an integer value. 
            /// </summary>
            USER = 0x0400,

        }

        public enum HSHELL : uint
        {
            /// <summary>
            /// The shell should activate its main window.
            /// </summary>
            ACTIVATESHELLWINDOW = 3,
            /// <summary>
            /// The user completed an input event (for example, pressed an application command button on the mouse or an application command key on the keyboard), and the application did not handle the WM_APPCOMMAND message generated by that input.
            /// If the Shell procedure handles the WM_COMMAND message, it should not call CallNextHookEx. See the Return Value section for more information.
            /// </summary>
            APPCOMMAND = 12,
            /// <summary>
            /// A window is being minimized or maximized. The system needs the coordinates of the minimized rectangle for the window.
            /// </summary>
            GETMINRECT = 5,
            /// <summary>
            /// Keyboard language was changed or a new keyboard layout was loaded.
            /// </summary>
            LANGUAGE = 8,
            /// <summary>
            /// The title of a window in the task bar has been redrawn.
            /// </summary>
            REDRAW = 6,
            /// <summary>
            /// The user has selected the task list. A shell application that provides a task list should return TRUE to prevent Windows from starting its task list.
            /// </summary>
            TASKMAN = 7,
            /// <summary>
            /// A top-level, unowned window has been created. The window exists when the system calls this hook.
            /// </summary>
            WINDOWCREATED = 1,
            /// <summary>
            /// A top-level, unowned window is about to be destroyed. The window still exists when the system calls this hook.
            /// </summary>
            WINDOWDESTROYED = 2,
            /// <summary>
            /// The activation has changed to a different top-level, unowned window.
            /// </summary>
            WINDOWACTIVATED = 4,
            /// <summary>
            /// A top-level window is being replaced. The window exists when the system calls this hook.
            /// </summary>
            WINDOWREPLACED = 13,
            /// <summary>
            /// A top-level window is about to be replaced. The window exists when the system calls this hook.
            /// </summary>
            WINDOWREPLACING = 14,
            /// <summary>
            /// A top-level window is being terminated.
            /// </summary>
            ENDTASK = 10,
            FLASH = (REDRAW | HSHELL_HIGHBIT),
            RUDEAPPACTIVATED = (WINDOWACTIVATED | HSHELL_HIGHBIT),
            /// <summary>
            /// A window has moved to another monitor. Windows 8 and newer only.
            /// </summary>
            MONITORCHANGED = 16,
            /// <summary>
            /// A window has become full-screen. Windows 8 and newer only.
            /// </summary>
            FULLSCREENENTER = 53,
            /// <summary>
            /// A window has left full-screen. Windows 8 and newer only.
            /// </summary>
            FULLSCREENEXIT = 54
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct WNDCLASS
        {
            [MarshalAs(UnmanagedType.U4)]
            public int style;   // Class style
            public WndProcDelegate lpfnWndProc;// Pointer to the classes Window Procedure
            public int cbClsExtra;// Number of extra bytes to allocate for class
            public int cbWndExtra;// Number of extra bytes to allocate for window
            public IntPtr hInstance;// Applications instance handle Class
            public IntPtr hIcon;// Handle to the classes icon
            public IntPtr hCursor;// Handle to the classes cursor
            public IntPtr hbrBackground;// Handle to the classes background brush
            public string lpszMenuName;// Resource name of class menu
            public string lpszClassName;// Name of the Window Class
        }

        [DllImport(User32_DllName, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U2)]
        public static extern ushort RegisterClass([In] ref WNDCLASS lpwcx);

        /// <summary>
        /// Unregisters a window class, freeing the memory required for the class.
        /// </summary>
        /// <param name="lpClassName">
        /// Type: LPCTSTR
        /// A null-terminated string or a class atom. If lpClassName is a string, it specifies the window class name. 
        /// This class name must have been registered by a previous call to the RegisterClass or RegisterClassEx function. 
        /// System classes, such as dialog box controls, cannot be unregistered. If this parameter is an atom, 
        ///   it must be a class atom created by a previous call to the RegisterClass or RegisterClassEx function. 
        /// The atom must be in the low-order word of lpClassName; the high-order word must be zero.
        /// 
        /// </param>
        /// <param name="hInstance">
        /// A handle to the instance of the module that created the class.
        /// 
        /// </param>
        /// <returns>
        /// Type: BOOL
        /// If the function succeeds, the return value is nonzero.
        /// If the class could not be found or if a window still exists that was created with the class, the return value is zero. 
        /// To get extended error information, call GetLastError.
        /// 
        /// </returns>
        [DllImport(User32_DllName)]
        public static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

        /// <summary>
        /// The CreateWindowEx function creates an overlapped, pop-up, or child window with an extended window style; otherwise, this function is identical to the CreateWindow function. 
        /// </summary>
        /// <param name="dwExStyle">Specifies the extended window style of the window being created.</param>
        /// <param name="lpClassName">Pointer to a null-terminated string or a class atom created by a previous call to the RegisterClass or RegisterClassEx function. The atom must be in the low-order word of lpClassName; the high-order word must be zero. If lpClassName is a string, it specifies the window class name. The class name can be any name registered with RegisterClass or RegisterClassEx, provided that the module that registers the class is also the module that creates the window. The class name can also be any of the predefined system class names.</param>
        /// <param name="lpWindowName">Pointer to a null-terminated string that specifies the window name. If the window style specifies a title bar, the window title pointed to by lpWindowName is displayed in the title bar. When using CreateWindow to create controls, such as buttons, check boxes, and static controls, use lpWindowName to specify the text of the control. When creating a static control with the SS_ICON style, use lpWindowName to specify the icon name or identifier. To specify an identifier, use the syntax "#num". </param>
        /// <param name="dwStyle">Specifies the style of the window being created. This parameter can be a combination of window styles, plus the control styles indicated in the Remarks section.</param>
        /// <param name="x">Specifies the initial horizontal position of the window. For an overlapped or pop-up window, the x parameter is the initial x-coordinate of the window's upper-left corner, in screen coordinates. For a child window, x is the x-coordinate of the upper-left corner of the window relative to the upper-left corner of the parent window's client area. If x is set to CW_USEDEFAULT, the system selects the default position for the window's upper-left corner and ignores the y parameter. CW_USEDEFAULT is valid only for overlapped windows; if it is specified for a pop-up or child window, the x and y parameters are set to zero.</param>
        /// <param name="y">Specifies the initial vertical position of the window. For an overlapped or pop-up window, the y parameter is the initial y-coordinate of the window's upper-left corner, in screen coordinates. For a child window, y is the initial y-coordinate of the upper-left corner of the child window relative to the upper-left corner of the parent window's client area. For a list box y is the initial y-coordinate of the upper-left corner of the list box's client area relative to the upper-left corner of the parent window's client area.
        /// <para>If an overlapped window is created with the WS_VISIBLE style bit set and the x parameter is set to CW_USEDEFAULT, then the y parameter determines how the window is shown. If the y parameter is CW_USEDEFAULT, then the window manager calls ShowWindow with the SW_SHOW flag after the window has been created. If the y parameter is some other value, then the window manager calls ShowWindow with that value as the nCmdShow parameter.</para></param>
        /// <param name="nWidth">Specifies the width, in device units, of the window. For overlapped windows, nWidth is the window's width, in screen coordinates, or CW_USEDEFAULT. If nWidth is CW_USEDEFAULT, the system selects a default width and height for the window; the default width extends from the initial x-coordinates to the right edge of the screen; the default height extends from the initial y-coordinate to the top of the icon area. CW_USEDEFAULT is valid only for overlapped windows; if CW_USEDEFAULT is specified for a pop-up or child window, the nWidth and nHeight parameter are set to zero.</param>
        /// <param name="nHeight">Specifies the height, in device units, of the window. For overlapped windows, nHeight is the window's height, in screen coordinates. If the nWidth parameter is set to CW_USEDEFAULT, the system ignores nHeight.</param> <param name="hWndParent">Handle to the parent or owner window of the window being created. To create a child window or an owned window, supply a valid window handle. This parameter is optional for pop-up windows.
        /// <para>Windows 2000/XP: To create a message-only window, supply HWND_MESSAGE or a handle to an existing message-only window.</para></param>
        /// <param name="hMenu">Handle to a menu, or specifies a child-window identifier, depending on the window style. For an overlapped or pop-up window, hMenu identifies the menu to be used with the window; it can be NULL if the class menu is to be used. For a child window, hMenu specifies the child-window identifier, an integer value used by a dialog box control to notify its parent about events. The application determines the child-window identifier; it must be unique for all child windows with the same parent window.</param>
        /// <param name="hInstance">Handle to the instance of the module to be associated with the window.</param> <param name="lpParam">Pointer to a value to be passed to the window through the CREATESTRUCT structure (lpCreateParams member) pointed to by the lParam param of the WM_CREATE message. This message is sent to the created window by this function before it returns.
        /// <para>If an application calls CreateWindow to create a MDI client window, lpParam should point to a CLIENTCREATESTRUCT structure. If an MDI client window calls CreateWindow to create an MDI child window, lpParam should point to a MDICREATESTRUCT structure. lpParam may be NULL if no additional data is needed.</para></param>
        /// <returns>If the function succeeds, the return value is a handle to the new window.
        /// <para>If the function fails, the return value is NULL. To get extended error information, call GetLastError.</para>
        /// <para>This function typically fails for one of the following reasons:</para>
        /// <list type="">
        /// <item>an invalid parameter value</item>
        /// <item>the system class was registered by a different module</item>
        /// <item>The WH_CBT hook is installed and returns a failure code</item>
        /// <item>if one of the controls in the dialog template is not registered, or its window window procedure fails WM_CREATE or WM_NCCREATE</item>
        /// </list></returns>

        [DllImport(User32_DllName, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(
            ExtendedWindowStyles dwExStyle,
            ushort lpClassName,
            [MarshalAs(UnmanagedType.LPStr)] string lpWindowName,
            WindowStyles dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport(User32_DllName)]
        public static extern int GetKeyboardLayout(uint idThread);

        [DllImport(User32_DllName)]
        public static extern int GetKeyboardLayoutList(int nBuff, long[] lpList);



        [DllImport(User32_DllName)]
        public static extern bool PostMessage(int hWnd, uint msg, int wParam, long lParam);

        [DllImport(User32_DllName)]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);



        public enum VK : int
        {
            ///<summary>
            ///Control-break processing
            ///</summary>
            CANCEL = 0x03,
            ///<summary>
            ///Windows 2000/XP: X1 mouse button
            ///</summary>
            XBUTTON1 = 0x05,
            ///<summary>
            ///Windows 2000/XP: X2 mouse button
            ///</summary>
            XBUTTON2 = 0x06,
            ///<summary>
            ///BACKSPACE key
            ///</summary>
            BACK = 0x08,
            ///<summary>
            ///TAB key
            ///</summary>
            TAB = 0x09,
            ///<summary>
            ///CLEAR key
            ///</summary>
            CLEAR = 0x0C,
            ///<summary>
            ///ENTER key
            ///</summary>
            RETURN = 0x0D,
            ///<summary>
            ///SHIFT key
            ///</summary>
            SHIFT = 0x10,
            ///<summary>
            ///CTRL key
            ///</summary>
            CONTROL = 0x11,
            ///<summary>
            ///ALT key
            ///</summary>
            MENU = 0x12,
            ///<summary>
            ///PAUSE key
            ///</summary>
            PAUSE = 0x13,
            ///<summary>
            ///Input Method Editor (IME) Kana mode
            ///</summary>
            KANA = 0x15,
            ///<summary>
            ///IME Hangul mode
            ///</summary>
            HANGUL = 0x15,
            ///<summary>
            ///IME Junja mode
            ///</summary>
            JUNJA = 0x17,
            ///<summary>
            ///IME final mode
            ///</summary>
            FINAL = 0x18,
            ///<summary>
            ///IME Hanja mode
            ///</summary>
            HANJA = 0x19,
            ///<summary>
            ///IME Kanji mode
            ///</summary>
            KANJI = 0x19,
            ///<summary>
            ///ESC key
            ///</summary>
            ESCAPE = 0x1B,
            ///<summary>
            ///IME convert
            ///</summary>
            CONVERT = 0x1C,
            ///<summary>
            ///IME nonconvert
            ///</summary>
            NONCONVERT = 0x1D,
            ///<summary>
            ///IME accept
            ///</summary>
            ACCEPT = 0x1E,
            ///<summary>
            ///SPACEBAR
            ///</summary>
            SPACE = 0x20,
            ///<summary>
            ///PAGE UP key
            ///</summary>
            PRIOR = 0x21,
            ///<summary>
            ///PAGE DOWN key
            ///</summary>
            NEXT = 0x22,
            ///<summary>
            ///END key
            ///</summary>
            END = 0x23,
            ///<summary>
            ///HOME key
            ///</summary>
            HOME = 0x24,
            ///<summary>
            ///LEFT ARROW key
            ///</summary>
            LEFT = 0x25,
            ///<summary>
            ///UP ARROW key
            ///</summary>
            UP = 0x26,
            ///<summary>
            ///RIGHT ARROW key
            ///</summary>
            RIGHT = 0x27,
            ///<summary>
            ///DOWN ARROW key
            ///</summary>
            DOWN = 0x28,
            ///<summary>
            ///SELECT key
            ///</summary>
            SELECT = 0x29,
            ///<summary>
            ///PRINT key
            ///</summary>
            PRINT = 0x2A,
            ///<summary>
            ///EXECUTE key
            ///</summary>
            EXECUTE = 0x2B,
            ///<summary>
            ///PRINT SCREEN key
            ///</summary>
            SNAPSHOT = 0x2C,
            ///<summary>
            ///INS key
            ///</summary>
            INSERT = 0x2D,
            ///<summary>
            ///DEL key
            ///</summary>
            DELETE = 0x2E,
            ///<summary>
            ///HELP key
            ///</summary>
            HELP = 0x2F,
            ///<summary>
            ///A key
            ///</summary>
            KEY_A = 0x41,
            ///<summary>
            ///Left Windows key (Microsoft Natural keyboard)
            ///</summary>
            LWIN = 0x5B,
            ///<summary>
            ///Applications key (Natural keyboard)
            ///</summary>
            APPS = 0x5D,
            ///<summary>
            ///Computer Sleep key
            ///</summary>
            SLEEP = 0x5F,
            ///<summary>
            ///Multiply key
            ///</summary>
            MULTIPLY = 0x6A,
            ///<summary>
            ///Add key
            ///</summary>
            ADD = 0x6B,
            ///<summary>
            ///Separator key
            ///</summary>
            SEPARATOR = 0x6C,
            ///<summary>
            ///Subtract key
            ///</summary>
            SUBTRACT = 0x6D,
            ///<summary>
            ///Decimal key
            ///</summary>
            DECIMAL = 0x6E,
            ///<summary>
            ///Divide key
            ///</summary>
            DIVIDE = 0x6F,
            ///<summary>
            ///F1 key
            ///</summary>
            F1 = 0x70,
            ///<summary>
            ///F2 key
            ///</summary>
            F2 = 0x71,
            ///<summary>
            ///F3 key
            ///</summary>
            F3 = 0x72,
            ///<summary>
            ///F4 key
            ///</summary>
            F4 = 0x73,
            ///<summary>
            ///F5 key
            ///</summary>
            F5 = 0x74,
            ///<summary>
            ///F6 key
            ///</summary>
            F6 = 0x75,
            ///<summary>
            ///F7 key
            ///</summary>
            F7 = 0x76,
            ///<summary>
            ///F8 key
            ///</summary>
            F8 = 0x77,
            ///<summary>
            ///F9 key
            ///</summary>
            F9 = 0x78,
            ///<summary>
            ///F10 key
            ///</summary>
            F10 = 0x79,
            ///<summary>
            ///F11 key
            ///</summary>
            F11 = 0x7A,
            ///<summary>
            ///F12 key
            ///</summary>
            F12 = 0x7B,
            ///<summary>
            ///F13 key
            ///</summary>
            F13 = 0x7C,
            ///<summary>
            ///F14 key
            ///</summary>
            F14 = 0x7D,
            ///<summary>
            ///F15 key
            ///</summary>
            F15 = 0x7E,
            ///<summary>
            ///F16 key
            ///</summary>
            F16 = 0x7F,
            ///<summary>
            ///F17 key  
            ///</summary>
            F17 = 0x80,
            ///<summary>
            ///F18 key  
            ///</summary>
            F18 = 0x81,
            ///<summary>
            ///F19 key  
            ///</summary>
            F19 = 0x82,
            ///<summary>
            ///F20 key  
            ///</summary>
            F20 = 0x83,
            ///<summary>
            ///F21 key  
            ///</summary>
            F21 = 0x84,
            ///<summary>
            ///F22 key, (PPC only) Key used to lock device.
            ///</summary>
            F22 = 0x85,
            ///<summary>
            ///F23 key  
            ///</summary>
            F23 = 0x86,
            ///<summary>
            ///F24 key  
            ///</summary>
            F24 = 0x87,
            ///<summary>
            ///SCROLL LOCK key
            ///</summary>
            SCROLL = 0x91,
            ///<summary>
            ///Attn key
            ///</summary>
            ATTN = 0xF6,
            ///<summary>
            ///CrSel key
            ///</summary>
            CRSEL = 0xF7,
            ///<summary>
            ///ExSel key
            ///</summary>
            EXSEL = 0xF8,
            ///<summary>
            ///Play key
            ///</summary>
            PLAY = 0xFA,
            ///<summary>
            ///Zoom key
            ///</summary>
            ZOOM = 0xFB,
            ///<summary>
            ///PA1 key
            ///</summary>
            PA1 = 0xFD,
        }

        public const int MONITOR_DEFAULTTONEAREST = 0x00000002;


        [DllImport(User32_DllName)]
        public static extern IntPtr MonitorFromWindow(IntPtr hWnd, int dwFlags);





        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public Rect rcCaret;
        }

        [DllImport(User32_DllName, SetLastError = true)]
        public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);
    }
}

// ----- Interop\NativeMethods.UxTheme.cs -----
namespace ManagedShell.Interop
{
    public partial class NativeMethods
    {
        // Undocumented dark mode API for 1903 and later
        // https://github.com/ysc3839/win32-darkmode
        


    }
}
