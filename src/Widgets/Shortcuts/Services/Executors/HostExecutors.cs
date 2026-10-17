using System.Text.Json;
using System.Text.Json.Nodes;
using DeskCanvas.Core;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;
using Shortcuts.Models;

namespace Shortcuts.Services.Executors;

/// <summary>Cycle/pick the widget surface material through app settings (live re-theme).</summary>
internal sealed class ThemeSurfaceExecutor : IActionExecutor
{
    private static readonly SurfaceStyle[] CycleOrder =
    [
        SurfaceStyle.Acrylic,
        SurfaceStyle.LiquidGlass,
        SurfaceStyle.FullColor,
    ];

    public Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var provider = context.Services.AppSettings;
        if (provider == null)
            throw new ActionFailedException(Loc.Get("Shortcuts_Error_NoService"));

        var settings = provider.Get();
        var mode = context.ResolveOrDefault(action, "mode");
        SurfaceStyle? next = mode switch
        {
            "acrylic" => SurfaceStyle.Acrylic,
            "liquidglass" or "liquidglassv2" => SurfaceStyle.LiquidGlass,
            "colorful" or "fullcolor" => SurfaceStyle.FullColor,
            _ => CycleNext(settings.Theme.Surface),
        };

        provider.Save(settings with { Theme = settings.Theme with { Surface = next } });
        return Task.FromResult<string?>(Loc.Format("Shortcuts_Output_Theme", Loc.Get(SurfaceLabelKey(next))));
    }

    private static SurfaceStyle CycleNext(SurfaceStyle? current)
    {
        var index = current is { } style ? Array.IndexOf(CycleOrder, style) : -1;
        return CycleOrder[(index + 1) % CycleOrder.Length];
    }

    internal static string SurfaceLabelKey(SurfaceStyle? style) => style switch
    {
        SurfaceStyle.Acrylic => "Shortcuts_Surface_acrylic",
        SurfaceStyle.LiquidGlass => "Shortcuts_Surface_liquidglass",
        SurfaceStyle.FullColor => "Shortcuts_Surface_colorful",
        _ => "Shortcuts_Surface_acrylic",
    };
}

/// <summary>Dark / light / follow-system, through the same app settings the Appearance page writes.</summary>
internal sealed class ThemeDarkModeExecutor : IActionExecutor
{
    public Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var provider = context.Services.AppSettings;
        if (provider == null)
            throw new ActionFailedException(Loc.Get("Shortcuts_Error_NoService"));

        var settings = provider.Get();
        var mode = context.ResolveOrDefault(action, "mode");
        bool? next = mode switch
        {
            "dark" => true,
            "light" => false,
            "system" => null,
            // toggle cycles dark -> light -> follow system -> dark
            _ => settings.Theme.DarkMode is true ? false
                : settings.Theme.DarkMode is false ? null
                : true,
        };

        provider.Save(settings with { Theme = settings.Theme with { DarkMode = next } });
        var labelKey = next is true ? "Shortcuts_Dark_dark"
            : next is false ? "Shortcuts_Dark_light"
            : "Shortcuts_Dark_system";
        return Task.FromResult<string?>(Loc.Format("Shortcuts_Output_Theme", Loc.Get(labelKey)));
    }
}

/// <summary>Show / hide the desktop widgets through the host's WidgetFactory.</summary>
internal sealed class WidgetsVisibilityExecutor : IActionExecutor
{
    public Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var factory = context.Services.WidgetFactory;
        if (factory == null)
            throw new ActionFailedException(Loc.Get("Shortcuts_Error_NoService"));

        var mode = context.ResolveOrDefault(action, "mode");
        var hide = mode switch
        {
            "hide" => true,
            "show" => false,
            _ => !factory.WidgetsHidden,
        };

        factory.SetWidgetsHidden(hide);
        return Task.FromResult<string?>(Loc.Get(hide
            ? "Shortcuts_Output_WidgetsHidden"
            : "Shortcuts_Output_WidgetsShown"));
    }
}

/// <summary>
/// Append a reminder to <c>reminders.json</c> — parsed as a raw JSON tree so unknown
/// fields written by the Reminders widget are preserved (the widget's file watcher
/// picks the addition up live).
/// </summary>
internal sealed class AddReminderExecutor : IActionExecutor
{
    public Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var title = context.Resolve(action, "title").Trim();
        if (title.Length == 0)
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_EmptyParam", Loc.Get("Shortcuts_Param_title")));

        // Same path rule as the Reminders store: a legacy file next to the exe wins.
        var inCurrent = Path.Combine(Const.CurrentFolder, "reminders.json");
        var path = File.Exists(inCurrent) ? inCurrent : Const.RemindersFile;

        JsonNode? root = null;
        if (File.Exists(path))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(path));
            }
            catch
            {
                // Never overwrite a file we cannot understand.
                throw new ActionFailedException(Loc.Get("Shortcuts_Error_NoService"));
            }
        }

        if (root is not JsonObject obj)
            obj = new JsonObject();

        if (obj["Reminders"] is not JsonArray reminders)
        {
            reminders = new JsonArray();
            obj["Reminders"] = reminders;
        }
        reminders.Add(new JsonObject
        {
            ["Completed"] = false,
            ["Title"] = title,
        });

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            throw new ActionFailedException(ex.Message);
        }

        return Task.FromResult<string?>(Loc.Format("Shortcuts_Output_ReminderAdded", title));
    }
}

internal sealed class OpenDataFolderExecutor : IActionExecutor
{
    public Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        try
        {
            DataFolderService.OpenInShell(Const.DataFolder);
        }
        catch (Exception ex)
        {
            throw new ActionFailedException(ex.Message);
        }

        return Task.FromResult<string?>(Loc.Get("Shortcuts_Output_DataFolder"));
    }
}
