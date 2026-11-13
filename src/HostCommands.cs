using Microsoft.UI.Xaml.Controls;

namespace HeMiDock;

/// <summary>What the logic asks of the interface (<see cref="Host"/>), done by the windows of the app.</summary>
static class HostCommands
{
    public static void Connect()
    {
        KeyboardHook.StartPressed += () => App.Dock?.ToggleStart(false);
        KeyboardHook.SearchPressed += () => App.Dock?.ToggleStart(true);
        KeyboardHook.PowerMenuPressed += () => App.Dock?.ShowPowerMenu();
        KeyboardHook.ControlPressed += () => App.Dock?.ToggleControlCenter();
        KeyboardHook.AppKeyPressed += i => App.Dock?.ActivateApp(i);
        KeyboardHook.ShortcutPressed += Hotkeys.Run;

        Host.Command = Run;
        Host.ShowMenu = items => App.Dock?.ShowMenuAtCursor(items);
        Host.AskText = TextPrompt.AskAsync;
        Host.PinToDock = path => App.Dock?.Pin(path);
        Host.Launching = key => App.Dock?.MarkLaunching(key);
        Host.ToastAnchor = mon => App.TopBar?.ClockAnchor(mon) ?? App.Dock?.ClockAnchor(mon);   // the clock is in the top bar while it is on
        // the zones over the screen (see ZoneLayer): the accent of the app as 0xAARRGGBB
        Host.ShowZones = (mon, zones, hot) => { int c = Theme.AccentColorRef; ZoneLayer.Show(mon, zones, hot, 0xFF000000u | (uint)((c & 0xFF) << 16 | (c & 0xFF00) | (c >> 16 & 0xFF))); };
        Host.HideZones = ZoneLayer.Hide;
        Host.ShowInfo = InfoWindow.Show;
    }

    /// <summary>A command of a shortcut or of the logic (see <see cref="Host.Command"/>).</summary>
    static void Run(string id)
    {
        var dock = App.Dock;
        if (dock == null) return;
        switch (id)
        {
            case "spotlight": SpotlightWindow.Toggle(); break;
            case "startsearch": dock.ToggleStart(true); break;       // "Search" of the Start button menu
            case "start": dock.ToggleStart(false); break;
            case "powermenu": dock.ShowPowerMenu(); break;
            case "control": dock.ToggleControlCenter(); break;
            case "calendar": dock.ToggleCalendar(); break;
            case "mute": AudioControl.ToggleMute(); break;
            case "settings": dock.ShowSettings(); break;
            case var s when s.StartsWith("setting:"):                // a setting found in Start: its page, pointed at
                var parts = s["setting:".Length..].Split('\n');
                SettingsWindow.OpenSetting(parts[0], parts.Length > 1 ? parts[1] : null);
                break;
            default: Diag.Write("Command", "unknown: " + id); break;
        }
    }
}
