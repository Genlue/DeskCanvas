using System.Text.Json;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace DeskCanvas.Core.Services;

/// <inheritdoc cref="ILayoutProvider" />
public class LayoutProvider() : JsonParser<ScreensLayout>(Const.LayoutFile), ILayoutProvider
{
    /// <inheritdoc />
    public override ScreensLayout Get()
    {
        if (data != null) return data;

        string json;
        try
        {
            json = File.ReadAllText(Const.LayoutFile);
        }
        catch (FileNotFoundException)
        {
            // First run without a layout file — empty primary entry.
            return data = Normalize(new ScreensLayout(
                [new ScreenLayout(ScreensLayout.LegacyPrimaryId, null, null, null, null, null, [])]));
        }

        try
        {
            data = JsonSerializer.Deserialize<ScreensLayout>(json);
        }
        catch (JsonException)
        {
            // Format v1: a plain WidgetLayout array → wrap as the legacy primary screen.
            var legacy = JsonSerializer.Deserialize<List<WidgetLayout>>(json) ?? [];
            data = ScreensLayout.FromLegacy(legacy);
        }

        if (data == null)
            throw new FormatException($"Can't deserialize {nameof(ScreensLayout)}");

        return data = Normalize(data).Deduplicate();
    }

    /// <inheritdoc />
    protected override ScreensLayout Normalize(ScreensLayout value) => value with
    {
        // Old files may lack the field; missing or empty still yields a usable file.
        Screens = value.Screens ?? [],
        // The on-disk format is upgraded to v3 once read and normalized.
        Version = Math.Max(value.Version, 3)
    };
}