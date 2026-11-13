#if DEBUG
using System.Text.Json;

namespace HeMiDock;

/// <summary>
/// Debug only: HeMiDock.exe --selftest (with the dock stopped) checks the debug log and the export / import of the settings
/// with the pinned apps – exported, changed, imported back, exported again and compared. Every file it touches is put back
/// exactly as it was afterwards; the result goes to %TEMP%\hemidock-selftest.txt.
/// </summary>
static class SelfTest
{
    static readonly string[] Files = { "settings.json", "pinned.json", "startpins.json", "startpins-own.json", "tray.json" };

    public static void Run()
    {
        var report = new List<string> { "HeMiDock self test " + DateTime.Now };
        var saved = Files.ToDictionary(f => f, f => File.Exists(AppPaths.Of(f)) ? File.ReadAllBytes(AppPaths.Of(f)) : null);
        string backups = AppPaths.Of("Backups"), log = Path.Combine(AppPaths.Logs, "debug-log.txt");
        var backupsBefore = Directory.Exists(backups) ? Directory.GetDirectories(backups).ToHashSet() : new HashSet<string>();
        byte[] logBefore = File.Exists(log) ? File.ReadAllBytes(log) : null;
        string a = Path.Combine(Path.GetTempPath(), "hemidock-selftest-a.json"), b = Path.Combine(Path.GetTempPath(), "hemidock-selftest-b.json");
        try
        {
            PinTransfer.Connect();
            var s = DockSettings.Current;

            // debug mode: a line written goes into debug-log.txt
            bool wasDebug = s.DebugMode;
            long before = File.Exists(log) ? new FileInfo(log).Length : 0;
            s.DebugMode = true;
            Diag.Apply();
            Diag.Write("SelfTest", "debug mode writes");
            long after = File.Exists(log) ? new FileInfo(log).Length : 0;
            report.Add($"Debug log: {(after > before ? "OK" : "FAILED")} ({before} → {after} bytes)");
            s.DebugMode = wasDebug;
            Diag.Apply();

            // export with the pins
            s.Export(a, pins: true);
            report.Add($"Export: {new FileInfo(a).Length} bytes, pins inside: {DockSettings.HasPins(a)}");

            // a setting changed, then the import has to bring it back
            var oldSize = s.IconSize;
            s.IconSize = oldSize + 2;
            bool ok = s.Import(a, pins: true);
            report.Add($"Import: {(ok ? "OK" : "FAILED")}, changed value back: {(s.IconSize == oldSize ? "yes" : "NO")}");

            // exported again: the same settings and pins as before
            s.Export(b, pins: true);
            var diff = Compare(a, b);
            report.Add(diff.Count == 0 ? "Export → import → export: identical" : "Differs after import: " + string.Join(", ", diff));
        }
        catch (Exception ex) { report.Add("ERROR " + ex); }
        finally
        {
            foreach (var (name, bytes) in saved)
            {
                try
                {
                    if (bytes != null) File.WriteAllBytes(AppPaths.Of(name), bytes);
                    else if (File.Exists(AppPaths.Of(name))) File.Delete(AppPaths.Of(name));
                }
                catch (Exception ex) { report.Add($"RESTORE FAILED {name}: {ex.Message}"); }
            }
            try
            {
                if (logBefore != null) File.WriteAllBytes(log, logBefore);
                else if (File.Exists(log)) File.Delete(log);
                if (Directory.Exists(backups))
                    foreach (var made in Directory.GetDirectories(backups).Where(d => !backupsBefore.Contains(d))) Directory.Delete(made, true);
                File.Delete(a);
                File.Delete(b);
            }
            catch (Exception ex) { report.Add("CLEANUP FAILED: " + ex.Message); }
            report.Add("Files restored: " + string.Join(", ", saved.Where(x => x.Value == null || File.Exists(AppPaths.Of(x.Key)) && File.ReadAllBytes(AppPaths.Of(x.Key)).SequenceEqual(x.Value)).Select(x => x.Key)));
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "hemidock-selftest.txt"), report);
    }

    /// <summary>Top-level keys whose values differ (or are only in one file).</summary>
    static List<string> Compare(string first, string second)
    {
        using var x = JsonDocument.Parse(File.ReadAllText(first));
        using var y = JsonDocument.Parse(File.ReadAllText(second));
        var one = x.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetRawText());
        var two = y.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetRawText());
        return one.Keys.Union(two.Keys).Where(k => !one.TryGetValue(k, out var v1) || !two.TryGetValue(k, out var v2) || v1 != v2).ToList();
    }
}
#endif
