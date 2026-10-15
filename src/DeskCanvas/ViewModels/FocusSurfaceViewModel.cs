using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.ViewModels;

/// <summary>
/// One entry of the per-state 主题 combo of 失焦自动切换: a surface material, or
/// <c>null</c> = "follow the shared theme" (<see cref="FocusLinkSettings.FocusedSurface"/>).
/// </summary>
public record FocusSurfaceViewModel(string Name, SurfaceStyle? Value);
