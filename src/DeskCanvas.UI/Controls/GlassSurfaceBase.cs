using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DeskCanvas.UI.Controls;

/// <summary>
/// The glass-surface port a panel shell hosts: the control contract the shared
/// <see cref="SecondaryPanelShell"/> and the host panel window drive without knowing the
/// host's platform pipeline (wallpaper sampling, GPU/CPU renderers).
/// <para>
/// Exactly one production implementation exists — the host's <c>LiquidGlassSurface</c>,
/// which derives from this port and keeps every optical property it owns. The port
/// deliberately carries only what panel chrome and the open/close zoom need; materials
/// (the host <c>Theme</c> model) stay a host concern and are wired by the host facade.
/// Two real consumers validate the port: the shell (visibility and corner-radius
/// mirroring across template applications) and the panel window (animation-rate
/// re-parameterisation and render requests).
/// </para>
/// </summary>
public abstract class GlassSurfaceBase : Control
{
    /// <summary>The radius the surface's material is drawn with (the shell card mirrors it).</summary>
    public abstract CornerRadius CornerRadius { get; set; }

    /// <summary>Ask for a render now; <paramref name="immediate"/> bypasses the coalescing debounce.</summary>
    public abstract void RequestRender(bool immediate);

    /// <summary>
    /// Feed the interpolated card zoom scale (1.0 = at rest) and its anchor as a relative
    /// point inside the surface's bounds, so the optics animate with the visible transition.
    /// </summary>
    public abstract void SetAnimationFrameScale(double scale, Point anchorRelativeToSurface);
}
