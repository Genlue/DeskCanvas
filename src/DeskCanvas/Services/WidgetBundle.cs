using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using DeskCanvas.Core;

namespace DeskCanvas.Services;

/// <summary>
/// Single-file distribution support.
/// <para>
/// Extracts the embedded widget bundle and default settings into the runtime data folder
/// (<see cref="Const.DataFolder"/>), so the whole application ships as one exe while widgets
/// remain plain files on disk — hot-updatable by dropping a new DLL into the Widgets folder.
/// </para>
/// </summary>
public static class WidgetBundle
{
    private const string WidgetsZipResource = "DeskCanvas.Resources.widgets.zip";
    private const string AppSettingsResource = "DeskCanvas.Resources.appSettings.json";
    private const string LayoutResource = "DeskCanvas.Resources.layout.json";

    /// <summary>
    /// Extracts default settings (only if missing) and the widget bundle
    /// (only when the embedded version differs from the extracted one).
    /// Safe to call on every startup; no-op in portable/dev mode.
    /// </summary>
    public static void ExtractIfNeeded()
    {
        var assembly = typeof(WidgetBundle).Assembly;

        // 1. Default settings — never overwrite existing user data.
        EnsureFile(assembly, AppSettingsResource, Const.AppSettingsFile);
        EnsureFile(assembly, LayoutResource, Const.LayoutFile);

        // 2. Widget bundle — portable mode (Widgets next to exe) is authoritative, skip.
        if (Directory.Exists(Const.WidgetsFolder) && !IsPackagedMode())
            return;

        using var stream = assembly.GetManifestResourceStream(WidgetsZipResource);
        if (stream == null)
            return; // dev build without an embedded bundle (portable mode)

        // Marker is the SHA-256 hash of the embedded zip, so ANY change to the widget
        // bundle (a bumped widget DLL version, a new widget, a resource change) triggers
        // a re-extract — not just main-exe version bumps.
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        var marker = Path.Combine(Const.WidgetsFolder, ".version");

        if (File.Exists(marker) && File.ReadAllText(marker) == hash)
            return;

        if (Directory.Exists(Const.WidgetsFolder))
            Directory.Delete(Const.WidgetsFolder, true);
        Directory.CreateDirectory(Const.WidgetsFolder);

        var root = Path.GetFullPath(Const.WidgetsFolder);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            var destination = Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar));

            // Guard against path traversal (zip is self-produced, but stay defensive).
            if (!Path.GetFullPath(destination).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, true);
        }

        File.WriteAllText(marker, hash);
    }

    /// <summary>
    /// True when running the packaged single-file exe (no Widgets folder next to the exe).
    /// </summary>
    public static bool IsPackagedMode() => !Directory.Exists(Path.Combine(Const.CurrentFolder, "Widgets"));

    private static void EnsureFile(Assembly assembly, string resource, string target)
    {
        if (File.Exists(target))
            return;

        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream == null)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var file = File.Create(target);
        stream.CopyTo(file);
    }
}
