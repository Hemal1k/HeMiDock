using System.Globalization;

namespace HeMiDock;

/// <summary>
/// HeMiDock.exe --check [file]: checks the logic that needs no window (the calculator and the converter of the search, the
/// window zones, the keyboard shortcuts) and ends – before anything of WinUI is loaded, so it also runs on the build server
/// of GitHub, against the trimmed portable version people download. The results go into the file (else check.txt next to
/// the exe); the exit code is the number of failed checks. Nothing of the user (settings, registry) is read or written.
/// </summary>
static class LogicCheck
{
    public static int Run(string file)
    {
        var lines = new List<string>();
        int failed = 0;
        void Check(string name, Func<(bool Ok, string Got)> test)
        {
            string got;
            bool ok;
            try { (ok, got) = test(); }
            catch (Exception ex) { ok = false; got = ex.GetType().Name + ": " + ex.Message; }
            if (!ok) failed++;
            lines.Add($"{(ok ? "ok  " : "FAIL")}  {name}{(string.IsNullOrEmpty(got) ? "" : "   → " + got)}");
        }
        // a number as the app writes it (in the language of the PC): the thousands apart by spaces, non-breaking spaces or commas
        static double? Number(string s) =>
            s != null && double.TryParse(s.Replace(" ", "").Replace(" ", "").Replace(" ", ""), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out double d) ? d : null;

        // ── the calculator of the search
        foreach (var (text, expect) in new[] { ("2+3*4", 14.0), ("(10-4)/2", 3.0), ("7 x 6", 42.0), ("100 / 8", 12.5) })
            Check($"calculator \"{text}\" = {expect}", () => (SearchEngine.TryCalc(text, out var r) && Number(r) == expect, r));
        foreach (var text in new[] { "2024-10", "123", "abc", "+420 777 123 456" })
            Check($"calculator leaves \"{text}\" alone", () => (!SearchEngine.TryCalc(text, out var r), r));

        // ── the converter of the search (units and temperatures; currencies need the internet and are left out)
        foreach (var (text, expect) in new[] { ("10 km to m", 10000.0), ("100 °C to °F", 212.0), ("1 kg v g", 1000.0), ("2 h in min", 120.0) })
            Check($"converter \"{text}\" = {expect}", () => (Converter.TryConvert(text, out _, out var number, out _) && Number(number) == expect, number));
        Check("converter refuses \"10 km to kg\"", () => (!Converter.TryConvert("10 km to kg", out var r, out _, out _), r));

        // ── the names of sound devices: a monitor without the number of its port and the driver, the others whole
        foreach (var (name, expect) in new[] { ("4 - Odyssey G95SC (AMD High Definition Audio Device)", "Odyssey G95SC"), ("12 - DELL U2723QE (NVIDIA High Definition Audio)", "DELL U2723QE"),
                                               ("Speakers (Realtek(R) Audio)", "Speakers (Realtek(R) Audio)"), ("Headset (Fractal Scape Dongle)", "Headset (Fractal Scape Dongle)") })
            Check($"sound device \"{name}\" = \"{expect}\"", () => (AudioDevices.Shown(name) == expect, AudioDevices.Shown(name)));

        // ── the window zones of the settings
        Check("zones: two halves, the broken one left out", () =>
        {
            var z = WindowManager.ParseZones("0,0,50,100;50,0,50,100;nonsense;10,10,0,50");
            return (z.Count == 2 && Math.Abs(z[1].X - 0.5) < 1e-9 && Math.Abs(z[1].Height - 1) < 1e-9, $"{z.Count} zones");
        });
        Check("zones: empty", () => (WindowManager.ParseZones("").Count == 0, null));

        // ── the keyboard shortcuts as stored in the settings
        Check("shortcuts: \"spotlight=1:32|settings=0:0|bad\"", () =>
        {
            var map = Hotkeys.Parse("spotlight=1:32|settings=0:0|bad");
            bool ok = map.Count == 2 && map["spotlight"] == new Hotkeys.Combo(Hotkeys.Alt, 0x20) && !map["settings"].IsSet;
            return (ok, string.Join(", ", map.Select(p => $"{p.Key}={p.Value.Mods}:{p.Value.Key}")));
        });
        Check("shortcuts: the code of Ctrl+Shift+K", () => (new Hotkeys.Combo(Hotkeys.Ctrl | Hotkeys.Shift, 0x4B).Code == ((Hotkeys.Ctrl | Hotkeys.Shift) << 16 | 0x4B), null));

        // ── the files of an imported settings file: pictures and web pins only, never programs or scripts (only paths, nothing written)
        const string folder = @"C:\HeMiDockCheck";                       // a made-up folder of the app: the real one is not touched
        string own = Path.Combine(folder, "Icons", "x"), pinned = Path.Combine(folder, "Pinned");
        foreach (var ext in new[] { ".exe", ".dll", ".ps1", ".bat", ".cmd", ".vbs", ".lnk" })
            Check($"import refuses a {ext} file", () => (PinTransfer.Allowed(own + ext, new byte[] { 1, 2 }, pinned) == null, null));
        Check("import takes a .png picture", () => (PinTransfer.Allowed(own + ".png", new byte[] { 1, 2 }, pinned) != null, null));
        Check("import takes a taskbar shortcut in Pinned", () => (PinTransfer.Allowed(Path.Combine(pinned, "App.lnk"), new byte[] { 1 }, pinned) != null, null));
        Check("import makes a web pin again from its https address", () =>
        {
            var b = PinTransfer.Allowed(own + ".url", System.Text.Encoding.UTF8.GetBytes("[InternetShortcut]\r\nURL=https://example.com/\r\nIconFile=C:\\evil.exe\r\n"), pinned);
            string text = b == null ? null : System.Text.Encoding.UTF8.GetString(b);
            return (text == "[InternetShortcut]\r\nURL=https://example.com/\r\n", text);
        });
        Check("import refuses a web pin of file://", () => (PinTransfer.Allowed(own + ".url", System.Text.Encoding.UTF8.GetBytes("[InternetShortcut]\r\nURL=file:///C:/Windows/System32/cmd.exe\r\n"), pinned) == null, null));

        // ── the diagnostic data: the calendar hidden, the user folder shortened
        Check("diagnostic data hide the calendar and the user folder", () =>
        {
            string got = Diag.Anonymize("{\n  \"CalendarIcs\": \"https://cal.example/private/abc.ics\",\n  \"Path\": \"C:\\\\Users\\\\Jana\\\\Desktop\"\n}\n[Setting] CalendarIcs = https://old.example/x\nopened C:\\Users\\Jana\\file.txt, not C:\\Users\\Janaxx",
                "https://cal.example/private/abc.ics", @"C:\Users\Jana");
            bool ok = !got.Contains("cal.example") && !got.Contains("old.example") && got.Contains("%USERPROFILE%\\\\Desktop")
                && got.Contains(@"%USERPROFILE%\file.txt") && got.Contains(@"C:\Users\Janaxx");
            return (ok, got.Replace("\n", " | "));
        });

        // ── the internet calendar: webcal is https, http is refused
        Check("calendar webcal:// is read over https://", () => (IcsCalendar.Address("webcal://cal.example/a.ics") == "https://cal.example/a.ics", IcsCalendar.Address("webcal://cal.example/a.ics")));
        Check("calendar http:// is refused", () => (IcsCalendar.IsPlainHttp(" HTTP://cal.example/a.ics") && !IcsCalendar.IsPlainHttp("https://cal.example/a.ics"), null));

        lines.Insert(0, $"HeMiDock {AppInfo.Version} check of the logic, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {CultureInfo.CurrentCulture.Name}: {(failed == 0 ? "all passed" : failed + " failed")}");
        try { File.WriteAllLines(file ?? Path.Combine(AppContext.BaseDirectory, "check.txt"), lines); } catch { }
        return failed;
    }
}
