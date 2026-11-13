using System.Runtime.InteropServices;

namespace HeMiDock;

/// <summary>
/// The window or menu an app opens from its tray icon (OneDrive): such apps place it by the taskbar of Windows, which is hidden –
/// it is moved next to the icon.
/// </summary>
static class TrayPopup
{
    delegate bool EnumProc(IntPtr h, IntPtr param);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr param);

    static List<IntPtr> VisibleOf(uint owner)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (Native.IsWindowVisible(h))
            {
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pid == owner) list.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Call right before the click goes to the icon: what is open by then is not its pop-up.</summary>
    public static async void Follow(IntPtr iconWindow, Native.RECT iconRect, MonInfo mon, int edge)
    {
        try
        {
            Native.GetWindowThreadProcessId(iconWindow, out uint owner);
            if (owner == 0) return;
            var before = VisibleOf(owner).ToHashSet();
            before.Remove(iconWindow);
            long end = Environment.TickCount64 + 1500;
            while (Environment.TickCount64 < end)
            {
                await Task.Delay(30);
                foreach (var h in VisibleOf(owner))
                {
                    if (before.Contains(h) || (Native.GetWindowLong(h, -16) & 0xC00000) == 0xC00000 || !Native.GetWindowRect(h, out var r)) continue;   // a normal window with a title
                    int w = r.Right - r.Left, ht = r.Bottom - r.Top, gap = 8;
                    if (w < 40 || ht < 40 || w > (mon.Work.Right - mon.Work.Left) * 0.7 || ht > (mon.Work.Bottom - mon.Work.Top) * 0.95) continue;
                    int middle = (iconRect.Left + iconRect.Right) / 2;
                    bool inside = r.Left >= mon.Work.Left && r.Right <= mon.Work.Right && r.Top >= mon.Work.Top && r.Bottom <= mon.Work.Bottom;
                    // placed well by the app (a menu at the mouse): left alone
                    if (inside && Math.Abs((r.Left + r.Right) / 2 - middle) <= w / 2 + 60 && (edge == 3 ? r.Top - mon.Work.Top : mon.Work.Bottom - r.Bottom) <= 120) continue;
                    int x = Math.Clamp(middle - w / 2, mon.Work.Left + gap, mon.Work.Right - w - gap), y = edge == 3 ? mon.Work.Top + gap : mon.Work.Bottom - ht - gap;
                    if (edge is 1 or 2)
                    {
                        x = edge == 1 ? mon.Work.Left + gap : mon.Work.Right - w - gap;
                        y = Math.Clamp(iconRect.Top - ht / 2, mon.Work.Top + gap, mon.Work.Bottom - ht - gap);
                    }
                    if (r.Left != x || r.Top != y) Native.SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, 0x1 | 0x4 | 0x10);
                }
            }
        }
        catch (Exception ex) { Log.Write("Tray pop-up", ex); }
    }
}
