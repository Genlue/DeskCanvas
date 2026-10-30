namespace Timetable.Services;

/// <summary>
/// Text-render memory budget helpers.
/// </summary>
/// <remarks>
/// <para>
/// Fractional font sizes are memory poison. Every distinct size × every distinct glyph gets its
/// own rasterised strike inside the native text stack, and nothing in that cache is scoped to a
/// widget — it lives and grows with the process. The week grid derives its sizes continuously
/// from live geometry (<c>Math.Clamp(colW * 0.28, rowH * 0.34, …)</c>), so a resize drag walks
/// through hundreds of distinct sizes and each one pays a full CJK glyph set (measured:
/// ~1.5 MB native per fractional size on a 30-size probe, with the managed heap flat — the cost
/// is entirely native).
/// </para>
/// <para>
/// Quantising every computed size to half-pixel steps caps the distinct sizes in any clamp range
/// at a handful (9.5–13 px → at most 8 values). The sub-pixel difference is visually
/// indistinguishable at widget font sizes, and the native strike cache stops growing on resize.
/// </para>
/// </remarks>
internal static class TextMetrics
{
    /// <summary>Snap a font size to the nearest 0.5 px step.</summary>
    public static double Quantize(double size) =>
        Math.Round(size * 2.0, MidpointRounding.AwayFromZero) / 2.0;
}
