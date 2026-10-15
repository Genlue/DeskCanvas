using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Core.Interfaces;

/// <summary>
/// Service for setting the application's theme.
/// <para>See <see cref="Theme"/></para>
/// </summary>
public interface IThemeService
{
    /// <summary>
    /// Apply the specified theme.
    /// </summary>
    /// <param name="theme">Theme to apply</param>
    public void Apply(Theme theme);

    /// <summary>
    /// Apply the theme of a whole settings snapshot. Preferred over <see cref="Apply(Theme)"/>:
    /// the 失焦自动切换 settings (<see cref="AppSettings.FocusLink"/>) ride on the snapshot and
    /// decide which light/dark pair drives the app-level theme variant.
    /// </summary>
    /// <param name="settings">Settings snapshot to apply</param>
    public void Apply(AppSettings settings);
}