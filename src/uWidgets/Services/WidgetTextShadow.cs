using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using uWidgets.Core.Models.Settings;

namespace uWidgets.Services;

/// <summary>
/// 全局文字阴影 (设置 → 外观, 默认关闭): a soft drop shadow behind every TextBlock inside a
/// desktop widget window, so text stays readable over busy wallpapers.
///
/// The effect is applied in code rather than through an app-level style on purpose: the scope
/// must be exactly "TextBlocks whose visual root is a desktop widget window" — settings,
/// dialogs, tray menus, secondary panels and popups keep their crisp system text. TextBlocks
/// are caught two ways:
/// <list type="bullet">
/// <item><see cref="Update"/> re-walks every live widget window whenever the theme changes;</item>
/// <item>a class handler on <c>TextBlock.TextProperty</c> catches text that appears later
/// (virtualized lists, async views, newly added reminders/clipboard entries). The apply is
/// deferred one dispatcher cycle because a TextBlock's text is usually set before it is
/// attached, so the root check has to run after attachment.</item>
/// </list>
/// </summary>
public static class WidgetTextShadow
{
    private static IEffect? current;

    /// <summary>Install the text-change class handler. Call once at application startup.</summary>
    public static void Install()
    {
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((tb, _) => ApplyDeferred(tb));
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

    /// <summary>Apply the current shadow (or clear it) on every TextBlock below <paramref name="root"/>.</summary>
    public static void ApplyToWindow(Visual root)
    {
        var effect = current;
        foreach (var textBlock in root.GetVisualDescendants().OfType<TextBlock>())
        {
            textBlock.Effect = effect;
        }
    }

    /// <summary>
    /// Apply the current effect to one TextBlock once it is attached: the text property is
    /// usually set during construction, before the visual root exists, so the root check has
    /// to wait for the next dispatcher cycle.
    /// </summary>
    private static void ApplyDeferred(TextBlock textBlock)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (current == null)
            {
                // Shadow globally off: make sure a stale effect never survives a toggle.
                if (textBlock.Effect != null) textBlock.Effect = null;
                return;
            }

            if (textBlock.GetVisualRoot() is Views.Widget)
                textBlock.Effect = current;
        }, DispatcherPriority.Loaded);
    }

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
