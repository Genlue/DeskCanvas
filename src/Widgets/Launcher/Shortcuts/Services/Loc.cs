using Shortcuts.Locales;

namespace Shortcuts.Services;

/// <summary>Resource lookup by key for dynamic (non-XAML) strings.</summary>
public static class Loc
{
    public static string Get(string? key) =>
        string.IsNullOrEmpty(key)
            ? string.Empty
            : Locale.ResourceManager.GetString(key, Locale.Culture) ?? key;

    public static string Format(string key, params object?[] args) =>
        string.Format(Get(key), args);
}
