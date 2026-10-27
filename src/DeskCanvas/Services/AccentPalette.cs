using System;
using Avalonia;
using Avalonia.Media;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

/// <summary>
/// The one place that owns 强调色偏移 and 跟随强调色: the base-accent choice (manual colour first,
/// the OS accent second), the shift toward white/black, and the derived 纯色 background pair.
/// <para>
/// Every colour consumer downstream — the accent ramp, the 纯色 card resources, the liquid-glass
/// coating, the widget card fill, the launcher's card — reads the same baked <see cref="Theme"/>,
/// so a glass card and a solid card can never disagree about what "the background" is. The maths
/// lives here only: a second rounding rule somewhere else is exactly how the two materials drift
/// apart after a slider move.
/// </para>
/// <para>
/// The resolver itself is pure (hex/colour in, colour out) so the checks can drive it headless;
/// only <see cref="TryGetSystemAccent"/> touches <see cref="Application.Current"/>, and it is
/// guarded — no platform settings simply means "no system accent to shift", not a crash.
/// </para>
/// </summary>
public static class AccentPalette
{
    /// <summary>
    /// The user's shift, clamped to its documented range. NaN / infinity resolves to 0 rather than
    /// propagating into <see cref="Shift"/>, where it would produce a transparent or black colour
    /// on every channel.
    /// </summary>
    public static double NormalizeShift(double shift) =>
        double.IsFinite(shift) ? Math.Clamp(shift, 0, 100) : 0;

    /// <summary>
    /// The accent being shifted: the manual colour when one is stored and parseable, otherwise the
    /// system accent. A stored-but-unparseable manual value deliberately does <b>not</b> fall back
    /// to the system colour — the historic ramp cleared its overrides in that case, and silently
    /// substituting a different accent would make a corrupt value look like a deliberate choice.
    /// </summary>
    public static Color? ResolveBaseAccent(string? manualHex, Color? systemAccent)
    {
        if (manualHex != null)
            return Color.TryParse(manualHex, out var manual) ? manual : null;

        return systemAccent;
    }

    /// <summary>
    /// Pushes <paramref name="color"/> toward white (<paramref name="towardWhite"/>) or black by
    /// <paramref name="percent"/> percent of the remaining distance, per RGB channel. Alpha is
    /// untouched: the opacity slider, not this feature, owns translucency.
    /// <para>
    /// 0% returns the input colour itself (same struct, same bytes), which is what makes the
    /// feature's default a no-op for every existing settings file. 100% lands exactly on pure
    /// white / black — the slider's far end is a real endpoint, not "almost".
    /// </para>
    /// </summary>
    public static Color Shift(Color color, bool towardWhite, double percent)
    {
        var amount = NormalizeShift(percent) / 100.0;
        if (amount <= 0) return color;

        var target = towardWhite ? 255 : 0;
        byte Channel(byte value) =>
            (byte)Math.Clamp((int)Math.Round(value + (target - value) * amount), 0, 255);

        return Color.FromArgb(color.A, Channel(color.R), Channel(color.G), Channel(color.B));
    }

    /// <summary>
    /// The derived 纯色 pair for a base accent: the dark slot is pushed toward black and the light
    /// slot toward white — the opposite of the accent's own shift. The asymmetry is the point: the
    /// accent is being lifted off its card, the card is being pushed behind its text.
    /// </summary>
    public static (string Dark, string Light) ResolveBackgroundPair(Color accent, double shift) =>
        (ToHex(Shift(accent, towardWhite: false, shift)),
         ToHex(Shift(accent, towardWhite: true, shift)));

    /// <summary>
    /// Bakes the 跟随强调色 pair into the theme handed to renderers. Returns a new record — the
    /// caller's stored theme (and therefore the settings page's colour inputs and the
    /// 应用到浅色/深色 buttons) keeps the user's own hex values untouched.
    /// <para>
    /// The derivation always starts from the base accent, never from the pair already in the
    /// record, so baking an already-baked theme is idempotent. That matters because the resolution
    /// boundaries overlap on purpose (the panel resolver receives a theme the focus coordinator
    /// already resolved); a double application must not shift twice.
    /// </para>
    /// <para>
    /// 多彩 pins its own card colours and ignores user accents entirely, so it is returned
    /// unchanged — the same policy <see cref="ThemeResourceHelper.ApplyAccentRamp"/> enforces.
    /// </para>
    /// </summary>
    public static Theme ApplyDerivedBackgrounds(Theme theme, Color? systemAccent)
    {
        if (!theme.FollowAccentBackground || theme.IsColorful) return theme;

        var accent = ResolveBaseAccent(theme.AccentColor, systemAccent);
        if (accent is not { } baseAccent) return theme;

        var (dark, light) = ResolveBackgroundPair(baseAccent, theme.EffectiveAccentShift);
        return theme with { SolidBackgroundDark = dark, SolidBackgroundLight = light };
    }

    /// <summary>
    /// The live OS accent (<c>AccentColor1</c>), or <c>null</c> when there is no platform (headless
    /// checks, very early start-up). Callers pass it in explicitly so the pure maths stays testable
    /// without an <see cref="Application.Current"/>.
    /// </summary>
    public static Color? TryGetSystemAccent()
    {
        try
        {
            return Application.Current?.PlatformSettings?.GetColorValues().AccentColor1;
        }
        catch
        {
            // Platform settings are optional; a failure must not break a theme application.
            return null;
        }
    }

    /// <summary>
    /// Canonical <c>#RRGGBB</c> (or <c>#AARRGGBB</c> when the accent carries alpha). The stored
    /// pair uses the short form, and every downstream parser (Avalonia, SkiaSharp) accepts it, so
    /// the derived value is indistinguishable from a hand-picked one.
    /// </summary>
    private static string ToHex(Color color) => color.A == 255
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}
