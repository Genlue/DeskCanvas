using Microsoft.Win32;
using DeskCanvas.Core.Interfaces;

namespace DeskCanvas.Core.Services;

/// <inheritdoc />
public class StartupService : IStartupService
{
    private const string RegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Full path of the running executable, quoted for the registry Run value.
    /// Quoting is required when the path contains spaces; it is harmless otherwise.
    /// </summary>
    private static string QuotedExePath
    {
        get
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path) ? "" : $"\"{path}\"";
        }
    }

    /// <inheritdoc />
    public bool IsEnabled() => IsEnabledInternal();

    /// <inheritdoc />
    public bool SetRunOnStartup(bool value) => SetRunOnStartupInternal(value);

    private static bool IsEnabledInternal()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
            return key?.GetValue(Const.AppName) != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool SetRunOnStartupInternal(bool value, bool repeat = true)
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            // CreateSubKey (not OpenSubKey) so the entry is written even if the
            // Run key is somehow missing; normally it already exists.
            var key = Registry.CurrentUser.CreateSubKey(RegistryKey, true);
            if (value)
                key.SetValue(Const.AppName, QuotedExePath);
            else
                key.DeleteValue(Const.AppName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception)
        {
            return repeat && SetRunOnStartupInternal(value, false);
        }
    }
}
