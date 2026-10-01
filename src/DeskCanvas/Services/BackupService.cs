using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace DeskCanvas.Services;

/// <summary>
/// Serialization of the app state into a single JSON backup file and back
/// (<see cref="BackupData"/>). The file is a plain, human-readable JSON document
/// so the user can inspect it before importing.
/// <para>
/// Full backup = global settings + all screens; single-screen export = the same
/// structure with exactly one screen entry (import targets that screen by
/// identity match, falling back to the primary screen).
/// </para>
/// </summary>
public static class BackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Create a full backup snapshot (global settings + all screens).</summary>
    public static BackupData Create(IAppSettingsProvider appSettings, ILayoutProvider layout) =>
        new(BackupData.CurrentVersion, appSettings.Get(), layout.Get());

    /// <summary>Create a single-screen backup snapshot (global settings + one screen).</summary>
    public static BackupData CreateSingle(IAppSettingsProvider appSettings, ScreenLayout screen) =>
        new(BackupData.CurrentVersion, appSettings.Get(), new ScreensLayout([screen]));

    /// <summary>Serialize a backup to JSON text.</summary>
    public static string ToJson(BackupData backup) => JsonSerializer.Serialize(backup, JsonOptions);

    /// <summary>
    /// Parse and validate backup JSON.
    /// </summary>
    /// <exception cref="FormatException">Unsupported version or missing/invalid sections.</exception>
    public static BackupData Parse(string json)
    {
        BackupData? backup;
        try
        {
            backup = JsonSerializer.Deserialize<BackupData>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The backup file is not valid JSON.", ex);
        }

        if (backup?.Version != BackupData.CurrentVersion)
            throw new FormatException($"Unsupported backup version: {backup?.Version}.");
        if (backup.AppSettings == null || backup.AppSettings.Theme == null)
            throw new FormatException("The backup file is missing the settings section.");
        if (backup.Screens == null || backup.Screens.Screens is not { Count: > 0 })
            throw new FormatException("The backup file is missing the screen layout section.");

        return backup;
    }

    /// <summary>Write a backup to a stream (UTF-8 JSON text).</summary>
    public static async Task WriteAsync(Stream stream, BackupData backup)
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(ToJson(backup));
        await writer.FlushAsync();
    }

    /// <summary>Read and validate a backup from a stream.</summary>
    public static async Task<BackupData> ReadAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(await reader.ReadToEndAsync());
    }
}