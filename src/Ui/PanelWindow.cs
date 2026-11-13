using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace HeMiDock;

/// <summary>What the dock and the top bar have in common for the parts they share (notification area, pop-ups, menus).</summary>
interface IPanelWindow
{
    IntPtr Hwnd { get; }
    MonInfo Monitor { get; }
    /// <summary>The screen edge of the window: 0 bottom, 1 left, 2 right, 3 top.</summary>
    int PanelEdge { get; }
    /// <summary>Where menus of its parts open (away from the edge).</summary>
    FlyoutPlacementMode MenuPlacement { get; }
    /// <summary>Where an element is on the screen (pixels).</summary>
    Native.RECT ScreenRect(FrameworkElement el);
    /// <summary>A menu that keeps the window still while it is open.</summary>
    MenuFlyout NewMenu();
    /// <summary>The icons of the notification area changed (pinned / unpinned).</summary>
    void TrayChanged();
    /// <summary>Where a pop-up of this kind opens: a point on the inner side of the window and the edge.</summary>
    (int X, int Y, int Edge)? AnchorFor(string kind);
    /// <summary>Shows where a dragged icon will go.</summary>
    DropMarker Marker { get; }
}
