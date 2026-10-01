namespace DeskCanvas.ViewModels;

/// <param name="Auto">True for the wallpaper-driven "自动" option.</param>
public record DarkModeViewModel(string Name, bool? Value, bool Auto = false);
