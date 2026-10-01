using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

/// <summary>
/// 全局文字阴影 (设置 → 外观, 默认关闭): a soft drop shadow behind every piece of text inside a
/// desktop widget window, so text stays readable over busy wallpapers.
///
/// The effect is applied in code rather than through an app-level style on purpose: the scope
/// must be exactly "text whose visual root is a desktop widget window" — settings, dialogs,
/// tray menus, secondary panels and popups keep their crisp system text. Text is caught two ways:
/// <list type="bullet">
/// <item><see cref="Update"/> re-walks every live widget window whenever the theme changes;</item>
/// <item>class handlers catch text that appears later — a <c>TextBlock.Text</c> change
/// (virtualized lists, async views) or a freshly loaded <see cref="TextPresenter"/>. The apply is
/// deferred one dispatcher cycle because a TextBlock's text is usually set before it is attached,
/// so the root check has to run after attachment.</item>
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
/// </summary>
public static class WidgetTextShadow
{
    private static IEffect? current;

    /// <summary>Install the late-text class handlers. Call once at application startup.</summary>
    public static void Install()
    {
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((tb, _) => ApplyDeferred(tb));

        // TextBox (and anything else template-driven) draws through a TextPresenter. Loaded is the
        // right moment: the presenter only exists once its template has been applied, and by then
        // the visual root is known.
        Control.LoadedEvent.AddClassHandler<TextPresenter>((presenter, _) => ApplyDeferred(presenter));
    }

    /// <summary>Rebuild the shared effect from the theme and re-apply it to every live widget window.
    /// A <c>null</c> effect disables the shadow. Safe to call from any thread: the wallpaper
    /// auto-theme path can raise a theme apply from a background callback.</summary>
    public static void Update(Theme theme)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Update(theme));
            return;
        }

        current = theme.TextShadowEnabled ? Build(theme.EffectiveTextShadowStrength) : null;

        var lifetime = Application.Current?.ApplicationLifetime
                       as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        if (lifetime == null) return;

        foreach (var window in lifetime.Windows.OfType<Views.Widget>().ToList())
        {
            ApplyToWindow(window);
        }
    }

    /// <summary>Apply the current shadow (or clear it) on every text host below <paramref name="root"/>.</summary>
    public static void ApplyToWindow(Visual root)
    {
        var effect = current;
        foreach (var visual in root.GetVisualDescendants())
        {
            if (IsTextHost(visual)) visual.Effect = effect;
        }
    }

    /// <summary>
    /// Apply the current effect to one text host once it is attached: the text property is
    /// usually set during construction, before the visual root exists, so the root check has
    /// to wait for the next dispatcher cycle.
    /// </summary>
    private static void ApplyDeferred(Visual visual)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (current == null)
            {
                // Shadow globally off: make sure a stale effect never survives a toggle.
                if (visual.Effect != null) visual.Effect = null;
                return;
            }

            if (visual.GetVisualRoot() is Views.Widget)
                visual.Effect = current;
        }, DispatcherPriority.Loaded);
    }

    /// <summary>True for the visuals that actually draw text inside a widget card.</summary>
    private static bool IsTextHost(Visual visual) => visual is TextBlock or TextPresenter;

    /// <summary>
    /// Build the shared shadow for a 0-1 strength. Blur, offset and opacity all scale together
    /// so the slider reads as one continuous "soft → strong" dial rather than three unrelated
    /// knobs; the offset points down-right (≈45°) so shadows fall like the labels on iOS/macOS
    /// home screens.
    /// </summary>
    private static DropShadowEffect Build(double strength)
    {
        var depth = 1 + strength * 1.5;     // 1 → 2.5
        var offset = depth * 0.707;         // down-right ≈ 45°
        return new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 2 + strength * 8,  // 2 → 10
            OffsetX = offset,
            OffsetY = offset,
            Opacity = 0.25 + strength * 0.6 // 0.25 → 0.85
        };
    }
}
