using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace HeMiDock;

/// <summary>
/// The acrylic of Windows that stays on while the window is not active: the standard one turns into a plain colour then,
/// and the dock is never the active window. Light or dark as the theme of the app (<see cref="Follow"/>).
/// </summary>
sealed partial class AlwaysActiveAcrylic : SystemBackdrop
{
    DesktopAcrylicController _controller;
    SystemBackdropConfiguration _config;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        _config = new SystemBackdropConfiguration { IsInputActive = true, Theme = Current };
        _controller = new DesktopAcrylicController();
        _controller.SetSystemBackdropConfiguration(_config);
        _controller.AddSystemBackdropTarget(target);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
    }

    /// <summary>
    /// Windows tells every window when the active window changes (opening the settings did it): the default of WinUI then
    /// applies its own configuration, and right after the start of the dock it threw "the parameter is incorrect" (target)
    /// inside the message of Windows – which ends the whole app (the settings opened in the first seconds crashed it). This
    /// acrylic keeps its own configuration (always active, the theme of the app), so it only takes the theme here.
    /// </summary>
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot) => Follow();

    /// <summary>
    /// The theme of the app now (ThemeWatch calls it with every change). It was taken from the content of the window when the
    /// backdrop was connected – the pop-ups get another content right after that, and their acrylic stayed dark in the light theme.
    /// </summary>
    public void Follow()
    {
        if (_config != null) _config.Theme = Current;
    }

    static SystemBackdropTheme Current => HeMiDock.Theme.IsLight ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;
}
