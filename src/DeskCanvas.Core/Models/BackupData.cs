using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Core.Models;

/// <summary>
/// A backup file: the global application settings plus per-screen configurations
/// (each screen's widget layout and manual grid).
/// <para>
/// A full backup carries every screen (<see cref="ScreensLayout.Screens"/>); a single-screen
/// export carries exactly one screen entry inside the same structure.
/// </para>
/// </summary>
/// <param name="Version">Backup format version (<see cref="CurrentVersion"/>).</param>
/// <param name="AppSettings">Global application settings (theme, dimensions, proxy, title bar …).</param>
/// <param name="Screens">Per-screen configurations: layout + per-screen manual grid + content scale.</param>
public record BackupData(int Version, AppSettings AppSettings, ScreensLayout Screens)
{
    /// <summary>
    /// The current backup format version. v1 backups (a single flat layout list,
    /// no per-screen structure) are rejected on import.
    /// </summary>
    public const int CurrentVersion = 2;
}