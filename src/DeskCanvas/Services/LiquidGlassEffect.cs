using System;
using SkiaSharp;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services.LiquidGlass;

namespace DeskCanvas.Services;

/// <summary>
/// Adapter between a widget <see cref="LiquidGlassRenderer.Frame"/> and the reusable
/// <see cref="LiquidGlassPass"/> core.
/// <para>
/// The material itself — its SkSL, its shape handling, its coating — lives in
/// <see cref="LiquidGlassPass"/>. This type only translates: a frame plus the theme's optics become
/// a <see cref="LiquidGlassBackdrop"/> and a <see cref="LiquidGlassParams"/>. Anything that wants
/// glass without a widget frame (a popup, a canvas item, a one-off render) should call the core
/// directly and skip this.
/// </para>
/// </summary>
public static class LiquidGlassEffect
{
    /// <summary>
    /// True when the runtime shader compiles on this build. Always check this before selecting the
    /// GPU path.
    /// </summary>
    public static bool IsSupported => LiquidGlassPass.IsSupported;

    /// <summary>The compilation error, when <see cref="IsSupported"/> is false.</summary>
    public static string? CompileError => LiquidGlassPass.CompileError;

    /// <summary>
    /// Every value the shader needs, derived from the frame and the 液态玻璃 optics.
    /// </summary>
    public readonly record struct Params(
        float Width, float Height, float Radius,
        float DestOriginX, float DestOriginY, float DestToRender,
        float SourceOriginX, float SourceOriginY, float SourceScale,
        float RefrHeight, float RefrAmount, float DepthEffect, float GradRadius,
        float Chroma, float Saturation, float Tint,
        SKColor Coating,
        float StrokeHalf, float StrokeFeather, float StrokeAlpha, float Falloff,
        float LightX, float LightY,
        float CornerGap = 0.5f, float HighlightBottom = 0.88f,
        float Sheen = 0.12f, float SheenDepth = 4.5f,
        float DarkWidth = 0.6f, float DarkAlpha = 0.22f,
        SKColor? HighlightColor = null, SKColor? DarkColor = null,
        float CornerContinuity = 1.15f);

    /// <summary>
    /// Mirror of the CPU coefficient block. <paramref name="sourceScale"/> converts render pixels
    /// to backdrop-texture pixels.
    /// <para>
    /// <paramref name="regionX"/> / <paramref name="regionY"/> are the backdrop's desktop-space
    /// origin. The shader maps a card pixel to a texel with <c>(srcOrigin + p) * srcScale</c>, so a
    /// region-limited backdrop only needs its origin subtracted here — <b>the SkSL is untouched</b>.
    /// A full-desktop backdrop passes zeros, which makes this the identity.
    /// </para>
    /// </summary>
    public static Params BuildParams(LiquidGlassRenderer.Frame frame, float sourceScale,
        float destWidth = 0f, float destHeight = 0f, int regionX = 0, int regionY = 0)
    {
        var optics = frame.Theme.EffectiveLiquidGlass;
        var scale = frame.Scale;
        var width = frame.Width;
        var height = frame.Height;
        var minSide = Math.Min(width, height);

        if (destWidth <= 0f || destHeight <= 0f)
        {
            destWidth = width;
            destHeight = height;
        }
        var destToRender = width / destWidth;

        var radius = Math.Clamp(frame.Radius * scale, 0f, minSide / 2f);
        var lens = LiquidGlassLens.Compute(optics.Refraction, minSide, radius);

        var colorHex = frame.Dark
            ? frame.Theme.EffectiveSolidBackgroundDark
            : frame.Theme.EffectiveSolidBackgroundLight;
        if (!SKColor.TryParse(colorHex, out var coating)) coating = frame.Dark ? new SKColor(46, 46, 46) : SKColors.White;
        var tint = (float)(double.IsFinite(frame.Theme.OpacityLevel) ? Math.Clamp(frame.Theme.OpacityLevel, 0, 1) : 0.20);

        var angle = (float)(optics.HighlightAngle * Math.PI / 180.0);
        var strokeHalf = (float)(optics.HighlightHalfWidth * scale);
        var (highlightColor, darkColor) = ResolveHighlightColors(frame.Theme, frame.SystemAccent);

        return new Params(
            Width: width, Height: height, Radius: radius,
            DestOriginX: 0f, DestOriginY: 0f, DestToRender: destToRender,
            SourceOriginX: frame.DesktopX - regionX,
            SourceOriginY: frame.DesktopY - regionY,
            SourceScale: sourceScale,
            RefrHeight: lens.Band,
            RefrAmount: lens.Amount,
            DepthEffect: lens.DepthEffect,
            GradRadius: lens.GradRadius,
            Chroma: (float)(optics.Dispersion / 100.0),
            Saturation: 1f + (float)LiquidGlassSettings.VibrancySaturationBoost * (float)(optics.Vibrancy / 100.0),
            Tint: tint,
            Coating: coating,
            StrokeHalf: strokeHalf,
            StrokeFeather: (float)(optics.HighlightFeather * scale),
            StrokeAlpha: (float)(optics.Highlight / LiquidGlassSettings.HighlightReference),
            Falloff: (float)optics.HighlightFalloffFactor,
            LightX: MathF.Cos(angle), LightY: MathF.Sin(angle),
            CornerGap: (float)(optics.HighlightCornerGap * scale),
            HighlightBottom: (float)optics.HighlightBottom,
            Sheen: (float)optics.HighlightSheen,
            SheenDepth: (float)(optics.HighlightSheenDepth * scale),
            DarkWidth: (float)(optics.HighlightDarkWidth * scale),
            DarkAlpha: (float)optics.HighlightDarkAlpha,
            HighlightColor: highlightColor,
            DarkColor: darkColor,
            CornerContinuity: (float)optics.CornerContinuity);
    }

    /// <summary>
    /// Resolves the light/dark accent highlight pair from the theme's manual accent (or OS
    /// system accent when following system), shifted toward white / black by the configured
    /// highlight shift percentages. Falls back to white / black when no accent is available.
    /// </summary>
    internal static (SKColor Light, SKColor Dark) ResolveHighlightColors(Theme theme, SKColor? systemAccent)
    {
        SKColor? baseAccent = null;
        if (theme.AccentColor != null)
        {
            if (SKColor.TryParse(theme.AccentColor, out var manual))
                baseAccent = manual;
        }
        else
        {
            baseAccent = systemAccent;
        }

        if (baseAccent is not { } accent)
            return (SKColors.White, SKColors.Black);

        var optics = theme.EffectiveLiquidGlass;
        return (
            ShiftAccent(accent, towardWhite: true, optics.HighlightLightShift),
            ShiftAccent(accent, towardWhite: false, optics.HighlightDarkShift));
    }

    private static SKColor ShiftAccent(SKColor color, bool towardWhite, double percent)
    {
        var amount = double.IsFinite(percent) ? Math.Clamp(percent, 0.0, 100.0) / 100.0 : 0.0;
        if (amount <= 0.0) return new SKColor(color.Red, color.Green, color.Blue);

        var target = towardWhite ? 255.0 : 0.0;
        byte Channel(byte value) =>
            (byte)Math.Clamp((int)Math.Round(value + (target - value) * amount), 0, 255);

        return new SKColor(Channel(color.Red), Channel(color.Green), Channel(color.Blue));
    }

    /// <summary>The core parameters this frame's optics describe.</summary>
    public static LiquidGlassParams ToCoreParams(in Params p) => new()
    {
        RefractionBand = p.RefrHeight,
        RefractionAmount = p.RefrAmount,
        DepthEffect = p.DepthEffect,
        Dispersion = p.Chroma,
        Vibrancy = p.Saturation,
        Coating = new LiquidGlassCoating(p.Coating, p.Tint),
        StrokeHalf = p.StrokeHalf,
        StrokeFeather = p.StrokeFeather,
        StrokeAlpha = p.StrokeAlpha,
        HighlightFalloff = p.Falloff,
        LightDirection = new SKPoint(p.LightX, p.LightY),
        CornerGap = p.CornerGap,
        HighlightBottom = p.HighlightBottom,
        Sheen = p.Sheen,
        SheenDepth = p.SheenDepth,
        DarkWidth = p.DarkWidth,
        DarkAlpha = p.DarkAlpha,
        HighlightColor = p.HighlightColor ?? SKColors.White,
        DarkColor = p.DarkColor ?? SKColors.Black,
        CornerContinuity = p.CornerContinuity
    };

    /// <summary>The backdrop this frame's sampling describes.</summary>
    public static LiquidGlassBackdrop ToBackdrop(SKBitmap source, in Params p) =>
        new(source, p.SourceOriginX, p.SourceOriginY, p.SourceScale);

    /// <summary>
    /// Build a shader for one card. <paramref name="source"/> is the shared blurred backdrop.
    /// Returns null when the shader cannot be built, in which case the caller must fall back to
    /// the CPU bitmap.
    /// </summary>
    public static SKShader? Create(SKBitmap source, in Params p) => Create(source, p, null);

    /// <summary>
    /// Build a shader for one card with an explicit outline. <paramref name="outline"/> null keeps
    /// the rounded rectangle the frame describes; a polygon (see <see cref="LiquidGlassShape"/>)
    /// replaces it, which is how a host morphs a card into any closed shape without touching the
    /// material.
    /// </summary>
    public static SKShader? Create(SKBitmap source, in Params p, LiquidGlassShape? outline)
    {
        if (source == null) return null;

        var shape = outline ?? LiquidGlassShape.RoundedRect(
            new SKSize(p.Width, p.Height), p.Radius, p.GradRadius);

        return LiquidGlassPass.CreateShader(
            ToBackdrop(source, p),
            shape,
            ToCoreParams(p),
            new SKPoint(p.DestOriginX, p.DestOriginY),
            p.DestToRender);
    }

    /// <summary>The optical model's SkSL source, for diagnostics.</summary>
    public static string SourceForDiagnostics => LiquidGlassPass.ShaderSource;
}
