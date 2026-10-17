using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Styling;

namespace Clock.Services;

/// <summary>
/// Per-screen theme variant resolution for the Clock family (the A3 seam pattern from
/// Progress/Batteries): every light/dark decision reads the variant of the window the control
/// sits on, so screens themed independently (the focus-link feature themes) render their own
/// variant instead of a uniform one. A control that cannot resolve a window (detached,
/// 组件库 preview, headless context) keeps its own <c>ActualThemeVariant</c> — exactly the
/// reading the per-control code paths used before.
/// </summary>
internal static class ClockPerScreenTheme
{
    /// <summary>
    /// Check seam: replaces the live visual→variant mapping so headless checks can exercise
    /// the per-screen path without a window. Production code never touches it; the fixture
    /// sets and clears it around each assertion.
    /// </summary>
    internal static Func<Visual, ThemeVariant?>? WindowVariantResolverForChecks;

    /// <summary>True when the visual renders with the dark variant.</summary>
    internal static bool IsDarkVariant(Visual? visual) => Resolve(visual) == ThemeVariant.Dark;

    /// <summary>
    /// The theme variant the given visual renders with, per screen: the visual's live window
    /// variant when attached, otherwise the visual's own (inherited or default) variant —
    /// the same value the previous direct read returned.
    /// </summary>
    internal static ThemeVariant Resolve(Visual? visual)
    {
        if (visual != null && WindowVariantResolverForChecks?.Invoke(visual) is { } injected)
        {
            return injected;
        }

        if (TopLevel.GetTopLevel(visual) is Control { ActualThemeVariant: { } root }
            && root != ThemeVariant.Default)
        {
            return root;
        }

        // Uniform fallback: no window to resolve against — the previous per-control reading.
        return visual is StyledElement styled ? styled.ActualThemeVariant : ThemeVariant.Default;
    }
}
