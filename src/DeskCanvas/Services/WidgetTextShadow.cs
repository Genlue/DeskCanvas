using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

/// <summary>
/// 全局元素阴影 (设置 → 外观, 默认关闭): a soft drop shadow behind every piece of text inside a
/// desktop widget window, so text stays readable over busy wallpapers. Shape-drawn parts — the
/// Music scrubber pill, the Monitor metric ring, the battery gauge icons — can float the same
/// way by carrying the <see cref="ElementShadowClass"/> class in their template.
///
/// The effect is applied in code rather than through an app-level style on purpose: the scope
/// must be exactly "content whose visual root is a desktop widget window" — settings, dialogs,
/// tray menus, secondary panels and popups keep their crisp system text. Content is caught two ways:
/// <list type="bullet">
/// <item><see cref="Update"/> re-walks every live widget window whenever the theme changes;</item>
/// <item>class handlers catch content that appears later: a <c>TextBlock.Text</c> change
/// (clock refreshes, virtualized lists, async views), and — the net — every control
/// <see cref="Control.LoadedEvent"/> inside a widget window. The Loaded net exists because
/// one-shot defers race: DataTemplate- and code-realized text (calendar grids, rendered
/// markdown) gets its text before the widget window attaches it, so a deferred apply can run
/// unrooted and be skipped forever; and markdown blocks built from <see cref="Run"/>s never
/// change the Text property at all. The apply itself is still deferred one dispatcher cycle so
/// bindings and inlines that land in the same load pass are included.</item>
/// </list>
///
/// <para>
/// <b>Both text hosts have to be covered.</b> A <see cref="TextBlock"/> draws its own text, but an
/// editable label is a <see cref="TextBox"/> — and a TextBox renders through a
/// <see cref="TextPresenter"/> (which is a <see cref="Control"/>, not a TextBlock). Shadowing only
/// TextBlocks therefore left every editable label crisp: the 待办清单 rows (each item is a
/// <c>ClickThroughTextBox</c>) and the widget-side editable names kept their unshadowed text while
/// the rest of the card was shadowed — which reads as "the shadow only covers part of the text".
/// </para>
///
/// <para>
/// <b>Shape parts opt in via the marker class, never via type.</b> Progress bars draw through
/// template parts (the Music scrubber is two <c>Border</c>s, the Monitor ring is a <c>Path</c>),
/// and a blanket "shadow every Border/Path" would catch every container too — a parent's effect
/// re-blurs its children's already-rendered shadows, which reads as a muddy halo.
/// </para>
///
/// <para>
/// <b>Effects render in the element's local coordinate space.</b> Ancestors that scale — the
/// Viewbox inside every dashboard/metric view — shrink or grow the on-screen shadow with them:
/// at 0.2× a 6 DIP blur lands as 1.2 screen pixels hugging the glyph, which reads as "no
/// shadow" (the big clock digits, scaled UP, looked fine — exactly the "some text has a shadow,
/// some doesn't" complaint). <see cref="GetEffect"/> therefore divides the effect's blur and
/// offset by the element's cumulative render scale, clamped so the intended depth is never
/// amplified on tiny content.
/// </para>
/// </summary>
public static class WidgetTextShadow
{
    /// <summary>Marker class for template parts that opt into the shape shadow, e.g.
    /// <c>&lt;Border Classes="elem-shadow" .../&gt;</c>. A class, not an attached property, so
    /// widget XAML needs no reference back to this assembly.</summary>
    public const string ElementShadowClass = "elem-shadow";

    /// <summary>Effect parameters are tuned for 1:1 rendering; below this render scale they are
    /// amplified only up to a 2× cap, so heavily down-scaled content (calendar grids in a
    /// Viewbox) gets a visible-but-proportionate shadow instead of a foggy halo.</summary>
    private const double MinCompensatedScale = 0.5;

    private static double? textStrength;   // null = shadow off
    private static double? elementStrength;
    private static readonly Dictionary<(bool Text, int Scale), DropShadowEffect> effectCache = new();

    /// <summary>Install the late-content class handlers. Call once at application startup.</summary>
    public static void Install()
    {
        // In-place text changes: clock refreshes, virtualized lists, async views. Text usually
        // arrives while the block is already rooted, so this is the hot path for live updates.
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((tb, _) => ApplyTextDeferred(tb));

        // The Loaded net. DataTemplate- and code-realized text (calendar grids, rendered
        // markdown) usually gets its text BEFORE the widget window attaches it, so a one-shot
        // text-changed apply can run unrooted, fail the widget-window check and be skipped for
        // good: nothing re-fires for text that never changes again (calendar numbers, note
        // bodies). And markdown blocks built from Runs never change the Text property at all.
        // Loaded fires per control once it is attached to a loaded root, which makes the scope
        // check exact instead of race-prone; it is also the entry point for marked shape parts
        // (icons, progress pills, rings). The root check runs before the dispatcher post so
        // content in settings/dialogs never queues a job.
        Control.LoadedEvent.AddClassHandler<Control>((control, _) =>
        {
            if (control.GetVisualRoot() is not Views.Widget) return;
            if (IsTextHost(control)) ApplyTextDeferred(control);
            else if (IsMarked(control)) ApplyElementDeferred(control);
        });
    }

    /// <summary>Rebuild the shared effect parameters from the theme and re-apply them to every
    /// live widget window. A <c>null</c> strength disables the shadow. Safe to call from any
    /// thread: the wallpaper auto-theme path can raise a theme apply from a background callback.</summary>
    public static void Update(Theme theme)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Update(theme));
            return;
        }

        textStrength = theme.TextShadowEnabled ? theme.EffectiveTextShadowStrength : null;
        elementStrength = theme.TextShadowEnabled ? theme.EffectiveTextShadowStrength : null;
        effectCache.Clear();

        var lifetime = Application.Current?.ApplicationLifetime
                       as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        if (lifetime == null) return;

        foreach (var window in lifetime.Windows.OfType<Views.Widget>().ToList())
        {
            ApplyToWindow(window);
        }
    }

    /// <summary>Apply the current effects (or clear them) on every text host and marked shape part
    /// below <paramref name="root"/>. Text hosts win over the marker so a marked TextBlock never
    /// gets its shadow re-blurred by the shape effect.</summary>
    public static void ApplyToWindow(Visual root)
    {
        foreach (var visual in root.GetVisualDescendants())
        {
            if (IsTextHost(visual)) visual.Effect = GetEffect(text: true, visual);
            else if (IsMarked(visual)) visual.Effect = GetEffect(text: false, visual);
        }
    }

    /// <summary>
    /// Apply the current effect to one content host once it is attached: a TextBlock's text is
    /// usually set during construction, before the visual root exists, so the root check has
    /// to wait for the next dispatcher cycle.
    /// </summary>
    private static void ApplyTextDeferred(Visual visual) => ApplyDeferred(visual, isText: true);

    private static void ApplyElementDeferred(Visual visual) => ApplyDeferred(visual, isText: false);

    private static void ApplyDeferred(Visual visual, bool isText)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (visual.GetVisualRoot() is not Views.Widget) return;
            visual.Effect = GetEffect(isText, visual);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>True for the visuals that actually draw text inside a widget card.</summary>
    private static bool IsTextHost(Visual visual) => visual is TextBlock or TextPresenter;

    private static bool IsMarked(Visual visual) =>
        visual is StyledElement element && element.Classes.Contains(ElementShadowClass);

    /// <summary>The shared effect for one text/shape family at the element's render scale, or
    /// null while the shadow is off. Instances are cached per quantized scale so the compositor
    /// sees a handful of shared effects, not one per element.</summary>
    private static DropShadowEffect? GetEffect(bool text, Visual visual)
    {
        var strength = text ? textStrength : elementStrength;
        if (strength == null) return null;

        var renderScale = EstimateRenderScale(visual);
        var compensated = Math.Max(renderScale, MinCompensatedScale);
        var key = (text, (int)Math.Round(compensated * 100));
        if (!effectCache.TryGetValue(key, out var effect))
        {
            effect = text ? Build(strength.Value, compensated) : BuildShape(strength.Value, compensated);
            effectCache[key] = effect;
        }
        return effect;
    }

    /// <summary>Cumulative scale of the render transforms between the element and its root:
    /// the factor by which the renderer shrinks (or grows) the effect's locally-specified
    /// blur and offset on screen. Reads the matrix off <see cref="ITransform"/> because the
    /// transforms on this path are usually immutable (Viewbox builds an ImmutableTransform,
    /// which is not a <see cref="Transform"/>).</summary>
    private static double EstimateRenderScale(Visual visual)
    {
        var scale = 1.0;
        for (Visual? current = visual; current != null; current = current.GetVisualParent())
        {
            if (current.RenderTransform is not ITransform transform) continue;
            var matrix = transform.Value;
            var sx = Math.Sqrt(matrix.M11 * matrix.M11 + matrix.M21 * matrix.M21);
            var sy = Math.Sqrt(matrix.M12 * matrix.M12 + matrix.M22 * matrix.M22);
            var factor = (sx + sy) / 2;
            if (factor > 0.0001) scale *= factor;
        }
        return scale;
    }

    /// <summary>
    /// Build the text shadow for a 0-1 strength. Blur, offset and opacity all scale together
    /// so the slider reads as one continuous "soft → strong" dial rather than three unrelated
    /// knobs; the offset points down-right (≈45°) so shadows fall like the labels on iOS/macOS
    /// home screens. <paramref name="renderScale"/> converts screen DIPs into the element's
    /// local units (see the class remark on scaled ancestors).
    /// </summary>
    private static DropShadowEffect Build(double strength, double renderScale)
    {
        var depth = 1 + strength * 1.5;               // 1 → 2.5
        var offset = depth * 0.707 / renderScale;     // down-right ≈ 45°
        return new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = (2 + strength * 8) / renderScale,  // 2 → 10
            OffsetX = offset,
            OffsetY = offset,
            Opacity = 0.25 + strength * 0.6                 // 0.25 → 0.85
        };
    }

    /// <summary>
    /// Shape variant of <see cref="Build"/> for marked template parts (progress pills, rings,
    /// icons): same strength dial, but blur and offset pulled in — the text-tuned 10px blur
    /// around a 3px scrubber reads as a smudge rather than a lift. The offset stays down-right.
    /// </summary>
    private static DropShadowEffect BuildShape(double strength, double renderScale)
    {
        var offset = (0.5 + strength) / renderScale;    // 0.5 → 1.5
        return new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = (1 + strength * 4) / renderScale,  // 1 → 5
            OffsetX = offset,
            OffsetY = offset,
            Opacity = 0.2 + strength * 0.5                  // 0.2 → 0.7
        };
    }
}
