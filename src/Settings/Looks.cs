namespace HeMiDock;

/// <summary>The ready-made looks of the guide and of Settings → General.</summary>
static class Looks
{
    /// <summary>Name, what it sets, and (top bar, full width, Start layout) for the small picture of the guide.</summary>
    internal static readonly (string Name, Action<DockSettings> Set, bool Bar, int Full, int Start)[] Presets =
    {
        ("Classic", s =>
        {
            s.TopBar = false; s.DockPosition = 0; s.DockFull = 1; s.BottomMargin = 0; s.StartLayout = 1; s.HoverEffect = 1;
            s.IconSize = 34; s.IconSpacing = 10; s.ShowSeparators = true; s.ShowSearchButtons = false; s.ShowTaskViewButton = false; s.ShowDesktopButton = true;
        }, false, 1, 1),
        ("Modern", s =>
        {
            s.TopBar = false; s.DockPosition = 0; s.DockFull = 2; s.BottomMargin = 0; s.StartLayout = 0; s.HoverEffect = 1;
            s.IconSize = 32; s.IconSpacing = 12; s.ShowSeparators = false; s.ShowSearchButtons = true; s.ShowTaskViewButton = true; s.ShowDesktopButton = true;
        }, false, 2, 0),
        ("Floating", s =>
        {
            s.TopBar = true; s.SysIconsOnBar = true; s.DockPosition = 0; s.DockFull = 0; s.BottomMargin = 5; s.StartLayout = 2; s.HoverEffect = 0; s.Magnification = 1.3; s.MagnificationSpread = 3;
            s.IconSize = 40; s.IconSpacing = 13; s.ShowSeparators = false; s.ShowSearchButtons = true; s.ShowTaskViewButton = false;
        }, true, 0, 2),
        ("Dock look", s =>
        {
            s.TopBar = false; s.DockPosition = 0; s.DockFull = 0; s.BottomMargin = 2; s.StartLayout = 1; s.HoverEffect = 2; s.Magnification = 1.15; s.MagnificationSpread = 3;
            s.IconSize = 34; s.IconSpacing = 13; s.ShowSeparators = false; s.ShowSearchButtons = false; s.ShowTaskViewButton = false; s.ShowDesktopButton = true; s.StartAppsGrid = true;
        }, false, 0, 1),
        ("Custom", null, false, 0, -1),
    };
}
