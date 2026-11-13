// HeMiDock: the types of WPF and Windows Forms that ManagedShell used, replaced by own ones, so that this part of the app
// runs without WPF and Windows Forms. Written for HeMiDock (not taken from ManagedShell).

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ManagedShell.Common.Enums
{
    /// <summary>A button of the mouse (instead of System.Windows.Input.MouseButton).</summary>
    public enum MouseButton { Left, Middle, Right, XButton1, XButton2 }
}

namespace ManagedShell.Common.Helpers
{
    /// <summary>A picture as pixels: premultiplied BGRA, rows from the top (instead of a WPF ImageSource).</summary>
    public sealed class ShellImage
    {
        public ShellImage(byte[] pixels, int width, int height) { Pixels = pixels; Width = width; Height = height; }
        public byte[] Pixels { get; }
        public int Width { get; }
        public int Height { get; }
    }

    /// <summary>
    /// The thread the shell was started on (the thread of the user interface): work of other threads is handed to it, as
    /// the dispatcher of WPF did. Set by <see cref="Use"/> before anything else of the shell starts.
    /// </summary>
    public static class ShellDispatcher
    {
        static SynchronizationContext _context;
        static int _thread;

        public static void Use(SynchronizationContext context)
        {
            _context = context;
            _thread = Environment.CurrentManagedThreadId;
        }

        public static bool CheckAccess() => _context == null || Environment.CurrentManagedThreadId == _thread;

        /// <summary>Runs on the thread of the shell and waits for it.</summary>
        public static void Invoke(Action action)
        {
            if (CheckAccess()) { action(); return; }
            // posted and waited for (the context of WinUI does not send): at most a few seconds, never stuck for good
            Exception error = null;
            using var done = new ManualResetEventSlim();
            _context.Post(_ => { try { action(); } catch (Exception ex) { error = ex; } finally { done.Set(); } }, null);
            done.Wait(TimeSpan.FromSeconds(5));
            if (error != null) throw error;
        }

        /// <summary>Runs on the thread of the shell later.</summary>
        public static void BeginInvoke(Action action)
        {
            if (_context == null) action();
            else _context.Post(_ => action(), null);
        }
    }

    /// <summary>A timer whose ticks come on the thread of the shell (instead of the DispatcherTimer of WPF).</summary>
    public sealed class ShellTimer
    {
        Timer _timer;
        int _busy;

        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);
        public event EventHandler Tick;
        public bool IsEnabled => _timer != null;

        public void Start()
        {
            if (_timer != null) return;
            _timer = new Timer(_ =>
            {
                if (Interlocked.Exchange(ref _busy, 1) == 1) return;     // the last tick still waits: not another one
                ShellDispatcher.BeginInvoke(() =>
                {
                    try { if (_timer != null) Tick?.Invoke(this, EventArgs.Empty); }
                    finally { Interlocked.Exchange(ref _busy, 0); }
                });
            }, null, Interval, Interval);
        }

        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}

namespace ManagedShell.Common.SupportingClasses
{
    /// <summary>A message of a window (instead of System.Windows.Forms.Message).</summary>
    public struct Message
    {
        public IntPtr HWnd;
        public int Msg;
        public IntPtr WParam;
        public IntPtr LParam;
        public IntPtr Result;
    }

    /// <summary>How a window is made (instead of System.Windows.Forms.CreateParams).</summary>
    public sealed class CreateParams
    {
        public string Caption { get; set; }
        public int Style { get; set; }
        public int ExStyle { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public IntPtr Parent { get; set; }
    }

    /// <summary>
    /// A hidden window that only receives messages (instead of the NativeWindow of Windows Forms): its messages go to
    /// <see cref="MessageReceived"/>, the ones not handled to Windows.
    /// </summary>
    public class NativeWindowEx
    {
        public delegate void MessageReceivedEventHandler(ref Message m, ref bool handled);
        delegate IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASS { public uint style; public WndProc proc; public int cls, wnd; public IntPtr inst, icon, cursor, brush; public string menu, name; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClass(ref WNDCLASS c);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWindowEx(int ex, string cls, string title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

        static int _count;
        readonly WndProc _proc;                                          // kept: Windows calls it for as long as the window lives

        public NativeWindowEx() { _proc = Proc; }

        public event MessageReceivedEventHandler MessageReceived;

        public IntPtr Handle { get; private set; }

        public virtual void CreateHandle(CreateParams cp)
        {
            if (Handle != IntPtr.Zero) return;
            var inst = GetModuleHandle(null);
            string name = "HeMiDock.ShellMessages." + Interlocked.Increment(ref _count);
            var wc = new WNDCLASS { proc = _proc, inst = inst, name = name };
            RegisterClass(ref wc);
            Handle = CreateWindowEx(cp.ExStyle, name, cp.Caption ?? "", cp.Style, cp.X, cp.Y, cp.Width, cp.Height, cp.Parent, IntPtr.Zero, inst, IntPtr.Zero);
        }

        public void DestroyHandle()
        {
            if (Handle == IntPtr.Zero) return;
            DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }

        IntPtr Proc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            var m = new Message { HWnd = hwnd, Msg = msg, WParam = wParam, LParam = lParam };
            bool handled = false;
            try { MessageReceived?.Invoke(ref m, ref handled); }
            catch (Exception ex) { ManagedShell.Common.Logging.ShellLogger.Error("NativeWindowEx: " + ex.Message); }
            return handled ? m.Result : DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }
}
