using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace HeMiDock;

/// <summary>
/// The animations of the app, run by the compositor of Windows (not by the thread of the interface, so they stay smooth while
/// it works): the pop-ups come in, pages slide. None with the lighter drawing or the animations switched off; their length
/// follows the animation speed of the settings.
/// </summary>
static class Motion
{
    static DockSettings S => DockSettings.Current;

    /// <summary>Animations are wanted (not off, not the lighter drawing).</summary>
    public static bool On => S.AnimationSpeed != 3 && !S.LowGraphics;

    /// <summary>A length at the normal speed, made longer / shorter by the setting.</summary>
    public static TimeSpan Length(double normalMs) => TimeSpan.FromMilliseconds(normalMs * S.AnimationSpeed switch { 0 => 1.45, 2 => 0.7, _ => 1 });

    static CompositionEasingFunction _ease;
    static CompositionEasingFunction Ease(Compositor c) => _ease ??= c.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));   // a soft stop, as Windows 11

    /// <summary>
    /// Comes in: from a little nearer to <paramref name="edge"/> of the dock (0 bottom, 1 left, 2 right, 3 top) and from
    /// transparent. Also hides what the window drew before its content was ready (a flash of the old state).
    /// </summary>
    public static void In(UIElement element, int edge, double distance = 14, double ms = 190)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (!On) { Reset(visual); return; }
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var c = visual.Compositor;
        float d = (float)distance;
        var from = edge switch { 1 => new Vector3(-d, 0, 0), 2 => new Vector3(d, 0, 0), 3 => new Vector3(0, -d, 0), _ => new Vector3(0, d, 0) };
        var length = Length(ms);
        var move = c.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(0, from);
        move.InsertKeyFrame(1, Vector3.Zero, Ease(c));
        move.Duration = length;
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, Ease(c));
        fade.Duration = length * 0.75;
        visual.Properties.InsertVector3("Translation", from);
        visual.Opacity = 0;
        visual.StartAnimation("Translation", move);
        visual.StartAnimation("Opacity", fade);
    }

    /// <summary>A new part (an app that opened in the dock): grows from its middle and comes up from transparent.</summary>
    public static void Grow(FrameworkElement element, double ms = 260)
    {
        if (!On) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.Opacity = 0;
        void Run()
        {
            var c = visual.Compositor;
            visual.CenterPoint = new Vector3((float)(element.ActualWidth / 2), (float)(element.ActualHeight / 2), 0);
            var scale = c.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(0, new Vector3(0.4f, 0.4f, 1));
            scale.InsertKeyFrame(1, Vector3.One, Ease(c));
            scale.Duration = Length(ms);
            var fade = c.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 0);
            fade.InsertKeyFrame(1, 1, Ease(c));
            fade.Duration = Length(ms * 0.7);
            visual.StartAnimation("Scale", scale);
            visual.StartAnimation("Opacity", fade);
        }
        if (element.ActualWidth > 0) Run();
        else
        {
            void Loaded(object s, RoutedEventArgs e) { element.Loaded -= Loaded; Run(); }
            element.Loaded += Loaded;
        }
    }

    /// <summary>A page that replaces another in the same window: slides in from the side it is coming from.</summary>
    public static void Slide(UIElement element, bool forward) => In(element, forward ? 2 : 1, 22, 170);

    /// <summary>Shown at once at its place (no animation, or a running one stopped).</summary>
    static void Reset(Visual visual)
    {
        visual.StopAnimation("Opacity");
        visual.Opacity = 1;
        if (visual.Properties.TryGetVector3("Translation", out _) == CompositionGetValueStatus.Succeeded)
        {
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
    }
}
