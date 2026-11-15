namespace HeMiDock;

/// <summary>
/// Windows Security for the shield among the status icons: is Microsoft Defender the active antivirus (Security Center), and
/// what its icon in the notification area says – the colour of its badge (green: nothing to do, yellow: an action is
/// recommended, red: an action is needed), the same in every language.
/// </summary>
static class SecurityStatus
{
    static bool _active, _none;
    static long _checkedAt = -1;
    static readonly object Gate = new();

    /// <summary>Microsoft Defender is on – as last read by <see cref="Refresh"/> (never asked on the thread of the interface).</summary>
    public static bool DefenderActive => _active;

    /// <summary>No antivirus is on at all (Defender off and no other one): the shield shows it in red.</summary>
    public static bool NoAntivirus => _none;

    /// <summary>Asks the Security Center again, at most once a minute (with the reading of the status icons, in the background).</summary>
    public static bool Refresh()
    {
        {
            lock (Gate)
            {
                if (_checkedAt >= 0 && Environment.TickCount64 - _checkedAt < 60_000) return _active || _none;
                _checkedAt = Environment.TickCount64;
                try
                {
                    using var search = new System.Management.ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntivirusProduct");
                    var on = search.Get().Cast<System.Management.ManagementObject>()
                        .Where(p => p["productState"] is uint state && (state >> 12 & 0xF) == 1)      // 0x1000: on
                        .Select(p => p["displayName"] as string ?? "").ToList();
                    _active = on.Any(n => n.Contains("Defender", StringComparison.OrdinalIgnoreCase));
                    _none = on.Count == 0;
                }
                catch (Exception ex) { Log.Write("Security Center", ex); _active = false; _none = false; }
                // the shield is shown for Defender, and as a warning when nothing protects the PC
                return _active || _none;
            }
        }
    }

    /// <summary>0 = nothing to do, 1 = an action recommended (yellow), 2 = an action needed (red); by the badge of its icon.</summary>
    public static int StateOf(IconImage icon)
    {
        if (icon == null) return 0;
        int yellow = 0, red = 0;
        var px = icon.Pixels;                                             // BGRA, premultiplied
        for (int i = 0; i + 3 < px.Length; i += 4)
        {
            int a = px[i + 3];
            if (a < 160) continue;
            int b = px[i] * 255 / a, g = px[i + 1] * 255 / a, r = px[i + 2] * 255 / a;
            if (r > 170 && g < 110 && b < 110) red++;
            else if (r > 180 && g > 130 && b < 100 && Math.Abs(r - g) < 90) yellow++;
        }
        int least = Math.Max(4, icon.Width * icon.Height / 200);          // a few pixels of a badge, not a stray one
        return red >= least ? 2 : yellow >= least ? 1 : 0;
    }
}
