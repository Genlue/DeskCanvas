using Avalonia;
using Avalonia.Controls.Primitives;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Locales;
using DeskCanvas.Services;

namespace DeskCanvas.Views.Controls;

/// <summary>
/// Host facade of the shared panel shell (the real implementation lives in
/// <see cref="global::DeskCanvas.UI.Controls.SecondaryPanelShell"/>): this type keeps the old
/// namespace, the old members and the old <c>avares://DeskCanvas</c> style URI for existing
/// consumers, and adds exactly the host-owned pieces the shared library must not know:
/// <list type="bullet">
/// <item><see cref="GlassMaterial"/> / <see cref="SurfaceAppearance"/> — typed with the host
/// <c>Theme</c> / <c>PanelAppearance</c> models; the decomposition into the shared shell's
/// paintable properties runs here, in the same order as the pre-extraction shell.</item>
/// <item>The platform glass surface: the facade instantiates the host's
/// <see cref="LiquidGlassSurface"/> (the single production implementation of the shared
/// <see cref="global::DeskCanvas.UI.Controls.GlassSurfaceBase"/> port) into the shared shell's
/// slot and feeds it the resolved optics. IgnoresAncestorTransform keeps its pipeline-wide
/// default (true).</item>
/// <item>Locale injection: the shared close button label is refreshed right before every
/// template application, matching the pre-extraction shell's template-time localization.</item>
/// </list>
/// All chrome logic (parts, close semantics, backdrop dismissal, drag handle, glass slot
/// mirroring) lives in the shared implementation.
/// </summary>
public class SecondaryPanelShell : global::DeskCanvas.UI.Controls.SecondaryPanelShell
{
    /// <summary>The optics handed to the glass surface; null while glass is off.</summary>
    public static readonly StyledProperty<Theme?> GlassMaterialProperty =
        AvaloniaProperty.Register<SecondaryPanelShell, Theme?>(nameof(GlassMaterial));

    /// <summary>The host's material result. Setting it decomposes into the paintable properties.</summary>
    public static readonly StyledProperty<PanelAppearance?> SurfaceAppearanceProperty =
        AvaloniaProperty.Register<SecondaryPanelShell, PanelAppearance?>(nameof(SurfaceAppearance));

    static SecondaryPanelShell()
    {
        SurfaceAppearanceProperty.Changed.AddClassHandler<SecondaryPanelShell>(OnSurfaceAppearanceChanged);
        GlassMaterialProperty.Changed.AddClassHandler<SecondaryPanelShell>(OnGlassMaterialChanged);
    }

    public SecondaryPanelShell()
    {
        // The platform glass surface is a host concern: create the single implementation of the
        // shared port and hand it to the shell's slot. Material/optics flow in through
        // GlassMaterial below; the shared shell mirrors visibility and corner radius.
        GlassSurface = new LiquidGlassSurface();
    }

    public Theme? GlassMaterial { get => GetValue(GlassMaterialProperty); set => SetValue(GlassMaterialProperty, value); }
    public PanelAppearance? SurfaceAppearance { get => GetValue(SurfaceAppearanceProperty); set => SetValue(SurfaceAppearanceProperty, value); }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        // Live locale read (as the pre-extraction shell did inside OnApplyTemplate) so a culture
        // switch after start-up still names the close button correctly.
        CloseButtonLabel = Locale.SecondaryPanel_Close;
        base.OnApplyTemplate(e);
    }

    private static void OnSurfaceAppearanceChanged(SecondaryPanelShell shell, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.NewValue is not PanelAppearance appearance) return;

        // Decomposition only — business brushes the plugin set on its OWN controls are untouched,
        // and nothing here writes back into SurfaceAppearance (no notification loop). Same
        // assignment order as the pre-extraction shell.
        shell.CardBackground = appearance.CardBackground;
        shell.CardBorderBrush = appearance.CardBorderBrush;
        shell.GlassMaterial = appearance.GlassMaterial;
        shell.UseRenderedGlass = appearance.UseRenderedGlass;
        shell.TitleForeground = appearance.TitleBrush;
        shell.SubtitleForeground = appearance.SubtitleBrush;
        shell.IconForeground = appearance.IconBrush;
        shell.CornerRadius = new CornerRadius(appearance.CornerRadius);
    }

    private static void OnGlassMaterialChanged(SecondaryPanelShell shell, AvaloniaPropertyChangedEventArgs args)
    {
        // The template binding of the pre-extraction shell fed the surface's Material directly;
        // the facade keeps that single path through the GlassMaterial property.
        if (shell.GlassSurface is LiquidGlassSurface surface)
            surface.Material = args.NewValue as Theme;
    }
}
