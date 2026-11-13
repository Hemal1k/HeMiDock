using System.Text.Json;
using Microsoft.Win32;

namespace HeMiDock;

public class PinnedApp
{
    /// <summary>.exe / .lnk / folder path, or "shell:AppsFolder\&lt;AUMID&gt;" for Store apps.</summary>
    public string Path { get; set; }
    public string Arguments { get; set; }
    public string AppId { get; set; }
    public string Name { get; set; }
    /// <summary>Own picture of the icon (a copy in the settings folder), null = the app's own icon.</summary>
    public string IconPath { get; set; }
    /// <summary>An empty place between icons (like a spacer tile on a Mac) – not an app.</summary>
    public bool Spacer { get; set; }
    /// <summary>The empty place is drawn as a separator line (edit mode: "Add separator").</summary>
    public bool Line { get; set; }
}

/// <summary>Apps pinned to the dock (pinned.json). On first run the Windows taskbar pins are taken over.</summary>
static class DockState
{
    static string FilePath => AppPaths.Of("pinned.json");

    static string _text;
    static DateTime _textAt;

    /// <summary>The pins (a new list every time); the file is read again only when it changed – the dock asks on every change of the windows.</summary>
    public static List<PinnedApp> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var at = File.GetLastWriteTimeUtc(FilePath);
                if (_text == null || at != _textAt) { _text = File.ReadAllText(FilePath); _textAt = at; }
                return JsonSerializer.Deserialize<List<PinnedApp>>(_text) ?? new();
            }
        }
        catch (Exception ex) { Log.Write("DockState.Load", ex); }
        return ImportTaskbarPins();
    }

    /// <summary>The pins were saved (the dock shows them again).</summary>
    public static event Action Changed;

    public static void Save(IEnumerable<PinnedApp> pinned)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(pinned.ToList(), new JsonSerializerOptions { WriteIndented = true }));
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Write("DockState.Save", ex); }
    }

    static List<PinnedApp> ImportTaskbarPins()
    {
        var list = new List<PinnedApp>();
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        string copyDir = AppPaths.Of("Pinned");
        try
        {
            Directory.CreateDirectory(copyDir);
            foreach (var lnk in TaskbarOrder(Directory.GetFiles(dir, "*.lnk")))
            {
                string copy = Path.Combine(copyDir, Path.GetFileName(lnk));   // keep working even if the taskbar pin is removed
                File.Copy(lnk, copy, true);
                list.Add(new PinnedApp { Path = copy, Name = Path.GetFileNameWithoutExtension(lnk) });
            }
        }
        catch (Exception ex) { Log.Write("ImportTaskbarPins", ex); }
        Save(list);
        return list;
    }

    /// <summary>
    /// The taskbar pins in their order on the taskbar: Windows keeps it in the "Favorites" value of the Taskband key, a
    /// binary list in which the names of the shortcuts appear in that order (UTF-16 at an even or odd byte, or ANSI).
    /// Shortcuts not found there come last, by name.
    /// </summary>
    static IEnumerable<string> TaskbarOrder(string[] shortcuts)
    {
        byte[] blob = null;
        try { blob = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband")?.GetValue("Favorites") as byte[]; }
        catch { }
        if (blob == null || blob.Length < 4) return shortcuts.OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase);

        string even = System.Text.Encoding.Unicode.GetString(blob);
        string odd = System.Text.Encoding.Unicode.GetString(blob, 1, blob.Length - 1);
        string ansi = System.Text.Encoding.Latin1.GetString(blob);
        long Position(string file)
        {
            string name = Path.GetFileName(file);
            var found = new[]
            {
                even.IndexOf(name, StringComparison.OrdinalIgnoreCase) is var e and >= 0 ? e * 2L : long.MaxValue,
                odd.IndexOf(name, StringComparison.OrdinalIgnoreCase) is var o and >= 0 ? o * 2L + 1 : long.MaxValue,
                ansi.IndexOf(name, StringComparison.OrdinalIgnoreCase) is var a and >= 0 ? a : long.MaxValue
            };
            return found.Min();
        }
        return shortcuts.OrderBy(Position).ThenBy(s => s, StringComparer.CurrentCultureIgnoreCase);
    }
}

