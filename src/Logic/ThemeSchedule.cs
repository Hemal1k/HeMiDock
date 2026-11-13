using System.Runtime.InteropServices;

namespace HeMiDock;

/// <summary>
/// Settings → General: the light and dark mode of Windows switched by itself – by sunrise and sunset (at the city of the
/// weather, otherwise a place guessed from the time zone) or at own times. Off by default; Windows is changed only at the
/// moment of a switch, so a mode chosen by hand in between stays until the next one.
/// </summary>
static class ThemeSchedule
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    static Timer _timer;
    static bool? _last;
    static (string City, double Lat, double Lon)? _place;

    public static void Apply()
    {
        if (DockSettings.Current.OsThemeSchedule == 0) { _timer?.Dispose(); _timer = null; _last = null; return; }
        _last = null;                                                    // a changed schedule applies at once
        _timer ??= new Timer(_ => Check(), null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        _ = Task.Run(Check);
    }

    static readonly object Gate = new();

    static void Check()
    {
        lock (Gate)
        {
            try
            {
                var s = DockSettings.Current;
                if (s.OsThemeSchedule == 0) return;
                bool light = LightNow(s, DateTime.Now);
                if (_last == light) return;
                _last = light;
                Set(light);
            }
            catch (Exception ex) { Log.Write("Theme schedule", ex); }
        }
    }

    /// <summary>Should Windows be light at <paramref name="now"/>?</summary>
    public static bool LightNow(DockSettings s, DateTime now)
    {
        var (from, to) = s.OsThemeSchedule == 1 ? SunTimes(now) : (Parse(s.OsThemeLightAt, 7), Parse(s.OsThemeDarkAt, 19));
        var t = now.TimeOfDay;
        return from <= to ? t >= from && t < to : t >= from || t < to;
    }

    static TimeSpan Parse(string text, int hours) => TimeSpan.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1) ? t : TimeSpan.FromHours(hours);

    /// <summary>Sunrise and sunset today (local time).</summary>
    public static (TimeSpan Rise, TimeSpan Set) SunTimes(DateTime day)
    {
        var (lat, lon) = Place();
        double offset = TimeZoneInfo.Local.GetUtcOffset(day).TotalHours;
        TimeSpan At(bool rising, TimeSpan fallback) => Sun(day.DayOfYear, lat, lon, rising) is double ut ? TimeSpan.FromHours(((ut + offset) % 24 + 24) % 24) : fallback;
        return (At(true, TimeSpan.FromHours(7)), At(false, TimeSpan.FromHours(19)));
    }

    /// <summary>The city of the weather (looked up once), otherwise the middle of the time zone at 50° north.</summary>
    static (double Lat, double Lon) Place()
    {
        string city = DockSettings.Current.WeatherCity.Trim();
        if (city.Length > 0 && _place?.City != city)
        {
            try
            {
                var found = Weather.SearchAsync(city, 1).GetAwaiter().GetResult().FirstOrDefault();
                if (found != null) _place = (city, found.Lat, found.Lon);
            }
            catch { }
        }
        if (_place is { } p && p.City == city) return (p.Lat, p.Lon);
        return (50, TimeZoneInfo.Local.BaseUtcOffset.TotalHours * 15);
    }

    /// <summary>The time of sunrise / sunset in UTC hours (the algorithm of the Almanac for Computers); null at polar day or night.</summary>
    static double? Sun(int dayOfYear, double lat, double lon, bool rising)
    {
        const double zenith = 90.833, rad = Math.PI / 180;
        double lngHour = lon / 15;
        double t = dayOfYear + ((rising ? 6 : 18) - lngHour) / 24;
        double m = 0.9856 * t - 3.289;
        double l = (m + 1.916 * Math.Sin(m * rad) + 0.020 * Math.Sin(2 * m * rad) + 282.634) % 360;
        if (l < 0) l += 360;
        double ra = Math.Atan(0.91764 * Math.Tan(l * rad)) / rad;
        ra = (ra % 360 + 360) % 360;
        ra += Math.Floor(l / 90) * 90 - Math.Floor(ra / 90) * 90;
        ra /= 15;
        double sinDec = 0.39782 * Math.Sin(l * rad), cosDec = Math.Cos(Math.Asin(sinDec));
        double cosH = (Math.Cos(zenith * rad) - sinDec * Math.Sin(lat * rad)) / (cosDec * Math.Cos(lat * rad));
        if (cosH > 1 || cosH < -1) return null;
        double h = (rising ? 360 - Math.Acos(cosH) / rad : Math.Acos(cosH) / rad) / 15;
        double local = h + ra - 0.06571 * t - 6.622;
        return ((local - lngHour) % 24 + 24) % 24;
    }

    /// <summary>Windows is light now (its apps).</summary>
    public static bool WindowsLight
    {
        get
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
    }

    /// <summary>Windows light or dark now, chosen in Settings → General → Windows theme (in the background: it tells all windows).</summary>
    public static void SetWindows(bool light)
    {
        try { if (Write(light)) Task.Run(Broadcast); }                  // the registry at once (the settings read it back), the message after
        catch (Exception ex) { Log.Write("Windows theme", ex); }
    }

    /// <summary>The mode of Windows and of its apps, then the message all windows follow (as the switch of Settings does).</summary>
    static void Set(bool light)
    {
        if (!Write(light)) return;
        Broadcast();
        Diag.Write("Theme", light ? "light (schedule)" : "dark (schedule)");
    }

    /// <summary>The mode written into the registry; false when it was so already.</summary>
    static bool Write(bool light)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key);
        int v = light ? 1 : 0;
        if (k.GetValue("AppsUseLightTheme") is int a && a == v && k.GetValue("SystemUsesLightTheme") is int b && b == v) return false;
        k.SetValue("AppsUseLightTheme", v, Microsoft.Win32.RegistryValueKind.DWord);
        k.SetValue("SystemUsesLightTheme", v, Microsoft.Win32.RegistryValueKind.DWord);
        return true;
    }

    static void Broadcast() => SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "ImmersiveColorSet", 0x0002, 200, out _);   // WM_SETTINGCHANGE, SMTO_ABORTIFHUNG
}
