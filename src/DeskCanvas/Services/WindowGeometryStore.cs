using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskCanvas.Core;

namespace DeskCanvas.Services;

/// <summary>Stored geometry of one edit-widget window.</summary>
public sealed class WindowGeometryEntry
{
    [JsonPropertyName("widthDip")] public double WidthDip { get; set; }
    [JsonPropertyName("heightDip")] public double HeightDip { get; set; }

    /// <summary>Best-effort screen signature (primary flag or "WxH@X,Y"); null when unknown.</summary>
    [JsonPropertyName("monitorId")] public string? MonitorId { get; set; }

    [JsonPropertyName("centerXRatio")] public double CenterXRatio { get; set; } = 0.5;
    [JsonPropertyName("centerYRatio")] public double CenterYRatio { get; set; } = 0.5;
}

/// <summary>The ui-state.json document.</summary>
public sealed class WindowGeometryFile
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = WindowGeometryStore.SchemaVersion;

    [JsonPropertyName("editWidgetWindows")]
    public Dictionary<string, WindowGeometryEntry> EditWidgetWindows { get; set; } = new();
}

/// <summary>
/// Persists edit-widget window geometry in its OWN UI-state file
/// (<see cref="Const.DataFolder"/>/ui-state.json) — never in AppSettings: writing window
/// geometry must not trigger the theme/region re-application pipeline. Keyed by the settings
/// view type ("&lt;assembly&gt;:&lt;full type name&gt;"), so repeated open/close reuses the
/// same slot instead of growing per instance.
/// <para>
/// Writes are atomic (temp file + replace) and best-effort: a failed write never blocks the
/// window close. This deliberately does NOT reuse <c>JsonParser.Save</c> (plain
/// File.WriteAllText, no atomicity).
/// </para>
/// </summary>
public static class WindowGeometryStore
{
    public const int SchemaVersion = 1;

    public static string FilePath => Path.Combine(Const.DataFolder, "ui-state.json");

    /// <summary>Slot key of a settings view type; survives restarts, bounded by widget kinds.</summary>
    public static string ComposeKey(Type settingsViewType)
        => $"{settingsViewType.Assembly.GetName().Name}:{settingsViewType.FullName}";

    /// <summary>Loads the stored entry for a key; null when absent/unreadable/invalid.</summary>
    public static WindowGeometryEntry? TryGet(string key)
    {
        var file = TryLoad();
        if (file is null) return null;
        if (!file.EditWidgetWindows.TryGetValue(key, out var entry)) return null;

        // Sanity: a corrupt entry must not produce a zero-size or NaN window.
        if (!IsFinitePositive(entry.WidthDip) || !IsFinitePositive(entry.HeightDip)) return null;
        if (!IsFiniteRatio(entry.CenterXRatio) || !IsFiniteRatio(entry.CenterYRatio)) return null;
        return entry;
    }

    /// <summary>Stores the entry for a key and persists atomically; failures are swallowed.</summary>
    public static void Save(string key, WindowGeometryEntry entry)
    {
        try
        {
            var file = TryLoad() ?? new WindowGeometryFile();
            file.SchemaVersion = SchemaVersion;
            file.EditWidgetWindows[key] = entry;

            var path = FilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(file, SerializerOptions);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // UI state is best-effort: never block the window close on a failed write.
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private static WindowGeometryFile? TryLoad()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return null;

            var file = JsonSerializer.Deserialize<WindowGeometryFile>(File.ReadAllText(path), SerializerOptions);
            if (file is null || file.SchemaVersion != SchemaVersion) return null;
            file.EditWidgetWindows ??= new Dictionary<string, WindowGeometryEntry>();
            return file;
        }
        catch
        {
            // Missing / corrupt / partially written files fall back to "no stored geometry".
            return null;
        }
    }

    private static bool IsFinitePositive(double value)
        => double.IsFinite(value) && value > 0;

    private static bool IsFiniteRatio(double value)
        => double.IsFinite(value) && value >= 0 && value <= 1;
}
