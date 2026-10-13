namespace DeskCanvas.Core.Interfaces;

/// <summary>
/// Implemented by widget views that render directly on the desktop canvas without
/// an outer card border, background plate, corner rounding, or padding margin.
/// </summary>
public interface IFramelessWidget
{
    /// <summary>
    /// Whether the widget is currently rendering in frameless mode. Defaults to <c>true</c>
    /// for always-frameless widgets; widgets that switch frameless mode via settings can
    /// override this property dynamically.
    /// </summary>
    bool IsFrameless => true;
}
