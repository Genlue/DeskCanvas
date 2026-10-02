using System;
using System.Collections.Generic;

namespace DeskCanvas.Core.Interfaces;

/// <summary>
/// Implemented by an <see cref="IFramelessWidget"/> that renders its material <b>inside its own
/// glyphs</b> rather than across a card rectangle.
/// <para>
/// A frameless widget has no card to frost: for the 无边框时钟 the material is the numerals
/// themselves. On the desktop that is expressed as the widget window's own native region, which the
/// widget builds from its glyph scanline spans — one top-level window per widget, so the widget can
/// simply own the window's region.
/// </para>
/// <para>
/// A sidebar host cannot do that: the sidebar's window is shared by every card in the strip, so a
/// single widget owning its region would fight every other card for it. That left the host with two
/// bad options — frost the whole grid cell the widget occupies (the cell turns into one slab of
/// frosted glass around the digits) or frost nothing (the numerals fall back to a plain translucent
/// wash). This interface is the third option: the widget publishes the spans its material may cover,
/// in its own physical-pixel space, and the host unions those spans into the window's region
/// instead of the card rectangle.
/// </para>
/// </summary>
public interface IFramelessGlassMask
{
    /// <summary>
    /// The fixed spans the material may frost, in physical pixels relative to this widget's own
    /// top-left corner. An empty list means "nothing may frost right now" (a fallback frame is
    /// showing), which a host must treat as "skip this widget", never as "frost the whole card".
    /// </summary>
    /// <param name="scaling">Render scaling of the surface the spans are wanted for.</param>
    IReadOnlyList<(int Left, int Top, int Right, int Bottom)> GetGlassSpans(double scaling);

    /// <summary>
    /// Identity of the current shape: two equal keys describe the same spans. A host caches on it —
    /// and a change is the signal to re-read, which is why <see cref="GlassMaskChanged"/> exists.
    /// </summary>
    string GlassMaskKey { get; }

    /// <summary>
    /// Raised when <see cref="GlassMaskKey"/> changed: the clock advanced a digit, the widget was
    /// resized, the font or the material changed.
    /// </summary>
    event Action? GlassMaskChanged;
}
