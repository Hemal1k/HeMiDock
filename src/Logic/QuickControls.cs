using NAudio.CoreAudioApi;
using Windows.Devices.Radios;

namespace HeMiDock;

enum ControlType { Slider, Toggle, Choice, Action }

/// <summary>
/// A system control in the results of the search: "volume" gives the slider of the volume right there, "wifi" its switch,
/// "power mode" the choice, "lock" the action (as the search of Windows 11 will). The value is read when the row is shown;
/// Restart and Shut down ask for a second Enter.
/// </summary>
sealed class QuickControl
{
    public string Id { get; init; }
    /// <summary>The English name (shown through the translation).</summary>
    public string Name { get; init; }
    public string Glyph { get; init; }
    public ControlType Type { get; init; }
    /// <summary>What finds it, in both languages, "|" between them (compared without diacritics).</summary>
    public string Keywords { get; init; }
    /// <summary>Slider 0–100, toggle 0 / 1, choice the index; null = not on this PC.</summary>
    public Func<Task<double?>> Get { get; init; }
    public Func<double, Task> Set { get; init; }
    /// <summary>The items of a choice.</summary>
    public Func<List<string>> Options { get; init; }
    public Action Run { get; init; }
    /// <summary>An action that needs a second Enter (Restart, Shut down).</summary>
    public bool Confirm { get; init; }
    /// <summary>The control of the row shown right now (switched by Enter).</summary>
    public object Widget { get; set; }
    /// <summary>The first Enter of an action with <see cref="Confirm"/>: the second one within a few seconds runs it.</summary>
    public DateTime Armed { get; set; }
}

static class QuickControls
{
    static Task<double?> Value(Func<double?> read) => Task.Run(() => { try { return read(); } catch (Exception ex) { Log.Write("Quick control", ex); return (double?)null; } });
    static Task Do(Action write) => Task.Run(() => { try { write(); } catch (Exception ex) { Log.Write("Quick control", ex); } });
    static double? Bool(bool? b) => b is bool v ? (v ? 1 : 0) : null;

    static async Task<double?> RadioState(RadioKind kind) => await RadioSwitch.FindAsync(kind) is { } r ? (r.State == Windows.Devices.Radios.RadioState.On ? 1 : 0) : null;
    static async Task SetRadio(RadioKind kind, double v)
    {
        if (await RadioSwitch.FindAsync(kind) is { } r) await r.SetStateAsync(v > 0.5 ? Windows.Devices.Radios.RadioState.On : Windows.Devices.Radios.RadioState.Off);
    }

    static readonly Guid[] PowerModes = { PowerMode.Efficiency, PowerMode.Balanced, PowerMode.Performance };

    static List<MMDevice> Outputs()
    {
        using var e = new MMDeviceEnumerator();
        return AudioDevices.Active(e, DataFlow.Render);
    }

    /// <summary>Every control (the workspaces as they are saved now).</summary>
    public static IEnumerable<QuickControl> All()
    {
        yield return new() { Id = "volume", Name = "Volume", Glyph = "", Type = ControlType.Slider, Keywords = "volume|sound|hlasitost|zvuk|hlas",
            Get = () => Value(() => AudioControl.Now() is { } v ? Math.Round(v.Level * 100) : null), Set = v => Do(() => AudioControl.SetLevel((float)(v / 100))) };
        yield return new() { Id = "brightness", Name = "Brightness", Glyph = "", Type = ControlType.Slider, Keywords = "brightness|display|jas|displej",
            Get = () => Value(() => DisplayBrightness.Get()), Set = v => Do(() => DisplayBrightness.Set((int)Math.Round(v))) };
        yield return new() { Id = "mute", Name = "Mute the sound", Glyph = "", Type = ControlType.Toggle, Keywords = "mute|sound|volume|ztlumit|zvuk|hlasitost",
            Get = () => Value(() => Bool(AudioControl.Now()?.Mute)), Set = v => Do(() => { if ((AudioControl.Now()?.Mute ?? false) != v > 0.5) AudioControl.ToggleMute(); }) };
        yield return new() { Id = "wifi", Name = "Wi-Fi", Glyph = "", Type = ControlType.Toggle, Keywords = "wifi|wi-fi|wlan|wireless|sit|bezdratova",
            Get = () => RadioState(RadioKind.WiFi), Set = v => SetRadio(RadioKind.WiFi, v) };
        yield return new() { Id = "bluetooth", Name = "Bluetooth", Glyph = "", Type = ControlType.Toggle, Keywords = "bluetooth|bt",
            Get = () => RadioState(RadioKind.Bluetooth), Set = v => SetRadio(RadioKind.Bluetooth, v) };
        yield return new() { Id = "airplane", Name = "Airplane mode", Glyph = "", Type = ControlType.Toggle, Keywords = "airplane|flight|letadlo|rezim v letadle",
            Get = () => Value(() => Bool(AirplaneMode.IsOn)), Set = v => Do(() => AirplaneMode.Set(v > 0.5)) };
        yield return new() { Id = "dnd", Name = "Do not disturb", Glyph = "", Type = ControlType.Toggle, Keywords = "do not disturb|dnd|focus|notifications|nerusit|oznameni",
            Get = () => Value(() => Bool(Dnd.IsOn)), Set = v => Do(() => Dnd.Set(v > 0.5)) };
        yield return new() { Id = "dark", Name = "Dark mode", Glyph = "", Type = ControlType.Toggle, Keywords = "dark mode|dark|theme|light|tmavy rezim|tmavy|svetly|motiv",
            Get = () => Value(() => Bool(!ThemeSchedule.WindowsLight)), Set = v => Do(() => ThemeSchedule.SetWindows(v < 0.5)) };
        yield return new() { Id = "awake", Name = "Scheduled shutdown", Glyph = "", Type = ControlType.Toggle, Keywords = "scheduled shutdown|shutdown timer|planovane vypnuti|vypnuti|keep awake|awake|caffeine|no sleep|nespat|udrzet vzhuru|vzhuru",
            Get = () => Value(() => Bool(KeepAwake.IsOn)), Set = v => Do(() => KeepAwake.Set(v > 0.5)) };
        yield return new() { Id = "mic", Name = "Mute the microphone", Glyph = "", Type = ControlType.Toggle, Keywords = "microphone|mic|mute|mikrofon|ztlumit",
            Get = () => Value(() => Bool(AudioControl.MicNow()?.Mute)), Set = v => Do(() => { if ((AudioControl.MicNow()?.Mute ?? false) != v > 0.5) AudioControl.ToggleMicMute(); }) };
        yield return new() { Id = "saver", Name = "Battery saver", Glyph = "", Type = ControlType.Toggle, Keywords = "battery saver|energy saver|battery|uspora baterie|uspora energie|baterie",
            Get = () => Value(() => Bool(BatterySaver.IsOn)), Set = v => Do(() => BatterySaver.Set(v > 0.5)) };
        yield return new() { Id = "power", Name = "Power mode", Glyph = "", Type = ControlType.Choice, Keywords = "power mode|performance|efficiency|balanced|rezim napajeni|napajeni|vykon|usporny|vyvazeny",
            Options = () => new() { L.T("Best power efficiency"), L.T("Balanced"), L.T("Best performance") },
            Get = () => Value(() => PowerMode.Current is Guid g && Array.IndexOf(PowerModes, g) is int i && i >= 0 ? i : null),
            Set = v => Do(() => PowerMode.Set(PowerModes[Math.Clamp((int)v, 0, 2)])) };
        yield return new() { Id = "output", Name = "Sound output", Glyph = "", Type = ControlType.Choice, Keywords = "sound output|output|speakers|headphones|audio device|zvukovy vystup|vystup|reproduktory|sluchatka|zvukove zarizeni",
            Options = () => Outputs().Select(d => AudioDevices.Shown(d.FriendlyName)).ToList(),
            Get = () => Value(() => AudioControl.Now()?.Name is string now && Outputs().FindIndex(d => d.FriendlyName == now) is int i && i >= 0 ? i : null),
            Set = v => Do(() => { var all = Outputs(); if ((int)v >= 0 && (int)v < all.Count) AudioDevices.SetDefault(all[(int)v].ID); }) };

        yield return new() { Id = "lock", Name = "Lock", Glyph = "", Type = ControlType.Action, Keywords = "lock|zamknout|zamek", Run = () => Fg.Run("rundll32.exe", "user32.dll,LockWorkStation") };
        yield return new() { Id = "sleep", Name = "Sleep", Glyph = "", Type = ControlType.Action, Keywords = "sleep|suspend|uspat|spanek", Run = () => Fg.Run("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0") };
        yield return new() { Id = "signout", Name = "Sign out", Glyph = "", Type = ControlType.Action, Keywords = "sign out|log off|logout|odhlasit", Run = () => Fg.Run("shutdown.exe", "/l") };
        yield return new() { Id = "restart", Name = "Restart", Glyph = "", Type = ControlType.Action, Keywords = "restart|reboot|restartovat", Confirm = true, Run = () => Fg.Run("shutdown.exe", "/r /t 0") };
        yield return new() { Id = "shutdown", Name = "Shut down", Glyph = "", Type = ControlType.Action, Keywords = "shut down|shutdown|power off|vypnout pc|vypnout pocitac|vypnout", Confirm = true, Run = () => Fg.Run("shutdown.exe", "/s /t 0") };
        yield return new() { Id = "bin", Name = "Empty the Recycle Bin", Glyph = "", Type = ControlType.Action, Keywords = "empty recycle bin|recycle bin|bin|trash|vysypat kos|kos", Run = () => RecycleBin.Empty(IntPtr.Zero) };
        yield return new() { Id = "screenshot", Name = "Screenshot", Glyph = "", Type = ControlType.Action, Keywords = "screenshot|snip|print screen|snimek obrazovky|snimek|vystrizek", Run = () => Fg.Open("ms-screenclip:") };
        yield return new() { Id = "clipboard", Name = "Clear the clipboard history", Glyph = "", Type = ControlType.Action, Keywords = "clear clipboard|clipboard|vymazat schranku|schranka|historie schranky", Run = ClipboardHistory.ClearAll };
        foreach (var name in WindowManager.Workspaces().Keys.OrderBy(n => n))
            yield return new() { Id = "ws:" + name, Name = L.T("Open workspace {0}", name), Glyph = "", Type = ControlType.Action, Keywords = $"workspace|pracovni prostor|{name}", Run = () => WindowManager.Open(name) };
    }

    /// <summary>The controls the text finds (a word that starts with it, or a longer text that names one).</summary>
    public static List<StartEntry> Find(string normalized)
    {
        var res = new List<StartEntry>();
        if (normalized.Length < 2) return res;
        foreach (var c in All())
        {
            var words = c.Keywords.Split('|');
            bool hit = words.Any(w => w.StartsWith(normalized, StringComparison.Ordinal) || w.Split(' ').Any(p => p.StartsWith(normalized, StringComparison.Ordinal)))
                || words.Any(w => w.Length >= 4 && normalized.Length > w.Length && normalized.Contains(w, StringComparison.Ordinal));
            if (!hit) continue;
            res.Add(new StartEntry
            {
                Kind = EntryKind.Control, Name = c.Id.StartsWith("ws:") ? c.Name : L.T(c.Name), Glyph = c.Glyph, Key = c.Id, Control = c,
                Subtitle = c.Type == ControlType.Action ? (c.Confirm ? L.T("Enter twice") : L.T("Enter")) : null, Group = L.T("Quick controls")
            });
        }
        return res;
    }
}
