namespace DeskCanvas.Core.Models;

/// <summary>Geometry coherence and deterministic ownership for extended and cloned desktops.</summary>
public static class DisplayTopology
{
    /// <summary>Clone outputs share one desktop rectangle; only distinct rectangles must agree.</summary>
    public static bool HasSameGeometry(
        IEnumerable<(int X, int Y, int Width, int Height)> desktop,
        IEnumerable<(int X, int Y, int Width, int Height)> outputs) =>
        desktop.ToHashSet().SetEquals(outputs);

    /// <summary>Keep a surviving owner of an exact rectangle, otherwise use stable device order.</summary>
    public static string? SelectDevice(IEnumerable<string> exactGeometryDevices, string? previousDevice)
    {
        var names = exactGeometryDevices.ToList();
        return names.FirstOrDefault(name => string.Equals(name, previousDevice, StringComparison.OrdinalIgnoreCase))
            ?? names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }
}
