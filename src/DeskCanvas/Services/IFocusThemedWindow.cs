namespace DeskCanvas.Services;

/// <summary>
/// A DeskCanvas window whose material follows the 失焦自动切换 state (widget cards and
/// the widgets' secondary panels — anything registered as <see cref="DesktopFurniture"/>).
/// <para>
/// With the uniform switch the window simply follows the one live global theme: a variant
/// flip reaches it through <c>ActualThemeVariantChanged</c> on its own, but a focus-state
/// change can also swap the <b>surface material</b> (毛玻璃 ↔ 液态玻璃 …) with the variant
/// staying put — so the coordinator pokes the window through this contract and it
/// re-resolves its own material (card brush, liquid-glass layer, native blur hint).
/// </para>
/// </summary>
public interface IFocusThemedWindow
{
    /// <summary>Re-resolve the window's material after the focus state changed.</summary>
    void OnFocusThemeChanged();
}

/// <summary>
/// Hosted widget content that draws theme-dependent visuals itself (the Stack widget's inner
/// cards) and resolves its colours through <see cref="FocusThemeCoordinator.ResolveVisualTheme"/>.
/// The host window pokes it when the 失焦自动切换 state changes the window's effective theme —
/// the regular settings-changed path does not fire for focus transitions.
/// </summary>
public interface IFocusThemedContent
{
    /// <summary>Re-resolve the content's theme-dependent visuals.</summary>
    void OnFocusThemeChanged();
}
