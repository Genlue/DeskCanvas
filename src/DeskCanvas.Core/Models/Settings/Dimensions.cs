namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// Dimensions of a widget.
/// </summary>
/// <param name="Size">Size of a 1x1 virtual grid unit in pixels (Virtual mode)</param>
/// <param name="Margin">Margin between a widget and the grid lines in pixels</param>
/// <param name="Radius">Widget's corner radius in pixels</param>
public record Dimensions(int Size, int Margin, int Radius);
