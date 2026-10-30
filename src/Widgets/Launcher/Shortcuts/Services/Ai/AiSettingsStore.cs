using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Shortcuts.Services.Ai;

/// <summary>
/// Persists <see cref="AiSettings"/> as <c>shortcuts-ai.json</c> inside the shared data
/// folder (next to <c>shortcuts.json</c>). Never throws: a missing or corrupted file simply
/// yields the defaults, and a failed save is logged and swallowed — a settings write must
/// never take the editor down.
/// </summary>
public static class AiSettingsStore
{
    private const string FileName = "shortcuts-ai.json";

    /// <summary>Full path of the settings file inside <see cref="DeskCanvas.Core.Const.DataFolder"/>.</summary>
    public static string FilePath => Path.Combine(DeskCanvas.Core.Const.DataFolder, FileName);

    /// <summary>Load the settings; any problem (missing/corrupt file, bad field) yields defaults.</summary>
    public static AiSettings Load()
    {
        var settings = new AiSettings();
        try
        {
            if (!File.Exists(FilePath)) return settings;

            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return settings;

            // Field-by-field tolerance: a bad value for one field keeps the default for that
            // field instead of discarding the whole file.
            if (ReadString(root, "BaseUrl") is { } baseUrl && !string.IsNullOrWhiteSpace(baseUrl))
                settings.BaseUrl = baseUrl;

            if (ReadString(root, "ApiKey") is { } apiKey)
                settings.ApiKey = apiKey;

            if (ReadString(root, "Model") is { } model && !string.IsNullOrWhiteSpace(model))
                settings.Model = model;

            if (root.TryGetProperty("Temperature", out var temperature) &&
                temperature.ValueKind == JsonValueKind.Number &&
                temperature.TryGetDouble(out var value) &&
                !double.IsNaN(value) && !double.IsInfinity(value))
            {
                settings.Temperature = value;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Shortcuts.AiSettingsStore] Load failed, using defaults: {ex.Message}");
        }

        return settings;
    }

    /// <summary>Atomically write the settings (tmp file + move); failures are logged, not thrown.</summary>
    public static void Save(AiSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var tmp = FilePath + ".tmp";
            using (var writer = new StreamWriter(tmp, append: false))
            {
                writer.Write(JsonSerializer.Serialize(settings, new JsonSerializerOptions
                {
                    WriteIndented = true,
                }));
            }

            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Shortcuts.AiSettingsStore] Save failed: {ex.Message}");
        }
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
