using Avalonia.Media;
using Avalonia.Styling;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

public sealed record ResolvedThemeProfile(
    Theme Theme,
    ThemeVariant Variant,
    SurfaceStyle Surface,
    ThemeColorPolicy ColorPolicy,
    MonochromeStyle? MonochromeVariant,
    Color? AccentColor,
    string ScopeSignature);
