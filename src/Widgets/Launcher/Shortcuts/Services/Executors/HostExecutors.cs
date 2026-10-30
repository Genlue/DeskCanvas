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

    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        if (context.Services.AppSettings is not { } provider)
            return Task.FromResult(ActionResult.Fail("NoService", Loc.Get("Shortcuts_Error_NoService")));

        var settings = provider.Get();
        var mode = context.Text(action, "mode");
        SurfaceStyle? next = mode switch
        {
            "acrylic" => SurfaceStyle.Acrylic,
            "liquidglass" or "liquidglassv2" => SurfaceStyle.LiquidGlass,
            "colorful" or "fullcolor" => SurfaceStyle.FullColor,
            _ => CycleNext(settings.Theme.Surface),
        };

        try
        {
            provider.Save(settings with { Theme = settings.Theme with { Surface = next } });
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("ThemeFailed", ex.Message, "mode"));
        }

        var value = SurfaceValue(next);
        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["surface"] = RunValue.FromText(value) },
            Loc.Format("Shortcuts_Output_Theme", Loc.Get(SurfaceLabelKey(next)))));
    }

    private static SurfaceStyle CycleNext(SurfaceStyle? current)
    {
        var index = current is { } style ? Array.IndexOf(CycleOrder, style) : -1;
        return CycleOrder[(index + 1) % CycleOrder.Length];
    }

    internal static string SurfaceValue(SurfaceStyle? style) => style switch
    {
        SurfaceStyle.LiquidGlass => "liquidglass",
        SurfaceStyle.FullColor => "colorful",
        _ => "acrylic",
    };

    internal static string SurfaceLabelKey(SurfaceStyle? style) => style switch
    {
        SurfaceStyle.Acrylic => "Shortcuts_Surface_acrylic",
        SurfaceStyle.LiquidGlass => "Shortcuts_Surface_liquidglass",
        SurfaceStyle.FullColor => "Shortcuts_Surface_colorful",
        _ => "Shortcuts_Surface_acrylic",
    };
}

/// <summary>
/// Dark / light / follow-system. <c>toggle</c> is strictly two-state (dark ↔ light) and
/// <c>cycle</c> walks dark → light → system, so the two behaviours are no longer conflated.
/// </summary>
internal sealed class ThemeDarkModeExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        if (context.Services.AppSettings is not { } provider)
            return Task.FromResult(ActionResult.Fail("NoService", Loc.Get("Shortcuts_Error_NoService")));

        var settings = provider.Get();
        var mode = context.Text(action, "mode");
        var current = settings.Theme.DarkMode;

        bool? next = mode switch
        {
            "dark" => true,
            "light" => false,
            "system" => null,
            "cycle" => current is true ? false : current is false ? null : true,
            // toggle: exactly two states; a system-following theme becomes dark first.
            _ => current is true ? false : true,
        };

        try
        {
            provider.Save(settings with { Theme = settings.Theme with { DarkMode = next } });
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("ThemeFailed", ex.Message, "mode"));
        }

        var isDark = next ?? IsDarkNow();
        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["dark"] = RunValue.FromBoolean(isDark) },
            Loc.Get(isDark ? "Shortcuts_Dark_dark" : "Shortcuts_Dark_light")));
    }

    private static bool IsDarkNow()
    {
        var variant = Avalonia.Application.Current?.ActualThemeVariant;
        return variant == Avalonia.Styling.ThemeVariant.Dark;
    }
}

/// <summary>Show / hide the desktop widgets through the host's WidgetFactory.</summary>
internal sealed class WidgetsVisibilityExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        if (context.Services.WidgetFactory is not { } factory)
            return Task.FromResult(ActionResult.Fail("NoService", Loc.Get("Shortcuts_Error_NoService")));

        var mode = context.Text(action, "mode");
        var hide = mode switch
        {
            "hide" => true,
            "show" => false,
            _ => !factory.WidgetsHidden,
        };

        try
        {
            factory.SetWidgetsHidden(hide);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("VisibilityFailed", ex.Message, "mode"));
        }

        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["visible"] = RunValue.FromBoolean(!hide) },
            Loc.Get(hide ? "Shortcuts_Output_WidgetsHidden" : "Shortcuts_Output_WidgetsShown")));
    }
}

/// <summary>
/// Append a reminder to <c>reminders.json</c> — parsed as a raw JSON tree so unknown fields
/// written by the Reminders widget are preserved (the widget's file watcher picks the addition
/// up live, so there is exactly one reminder store).
/// </summary>
internal sealed class AddReminderExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var title = context.Text(action, "title").Trim();
        if (title.Length == 0)
        {
            return Task.FromResult(ActionResult.Fail("EmptyParam",
                Loc.Format("Shortcuts_Error_EmptyParam", Loc.Get("Shortcuts_Param_title")), "title"));
        }

        var dueText = context.Text(action, "due").Trim();
        DateTime? due = null;
        if (dueText.Length > 0)
        {
            if (!DateTime.TryParse(dueText, System.Globalization.CultureInfo.CurrentCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed))
            {
                return Task.FromResult(ActionResult.Fail("DateInvalid",
                    Loc.Format("Shortcuts_Error_NotDate", Loc.Get("Shortcuts_Param_due"), dueText), "due"));
            }

            due = parsed;
        }

        var notes = context.Text(action, "notes").Trim();

        // Same path rule as the Reminders store: a legacy file next to the exe wins.
        var inCurrent = Path.Combine(Const.CurrentFolder, "reminders.json");
        var path = File.Exists(inCurrent) ? inCurrent : Const.RemindersFile;

        JsonObject root;
        if (File.Exists(path))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
            }
            catch
            {
                // Never overwrite a file we cannot understand.
                return Task.FromResult(ActionResult.Fail("ReminderFileUnreadable",
                    Loc.Get("Shortcuts_Error_ReminderFile")));
            }
        }
        else
        {
            root = new JsonObject();
        }

        if (root["Reminders"] is not JsonArray reminders)
        {
            reminders = new JsonArray();
            root["Reminders"] = reminders;
        }

        var item = new JsonObject
        {
            ["Completed"] = false,
            ["Title"] = title,
        };
        if (due is { } dueAt) item["DueAt"] = dueAt.ToString("o");
        if (notes.Length > 0) item["Notes"] = notes;

        reminders.Add(item);

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("ReminderWriteFailed", ex.Message));
        }

        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue>
            {
                ["due"] = due is { } value ? RunValue.FromDate(value) : RunValue.Null,
            },
            Loc.Format("Shortcuts_Output_ReminderAdded", title)));
    }
}

internal sealed class OpenDataFolderExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        try
        {
            DataFolderService.OpenInShell(Const.DataFolder);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("OpenFolderFailed", ex.Message));
        }

        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["path"] = RunValue.FromPath(Const.DataFolder) },
            Loc.Get("Shortcuts_Output_DataFolder")));
    }
}

/// <summary>
/// Run another shortcut and return its typed result. The child shares the root run's budget and
/// cancellation, and its confirmation is handled by the single run entry point.
/// </summary>
internal sealed class RunShortcutExecutor : IActionExecutor
{
    private const int MaxDepth = 8;

    public async Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var target = context.Text(action, "target").Trim();
        if (target.Length == 0)
        {
            return ActionResult.Fail("EmptyParam",
                Loc.Format("Shortcuts_Error_EmptyParam", Loc.Get("Shortcuts_Param_shortcut")), "target");
        }

        var document = ShortcutsStore.Get();
        var child = document.Shortcuts.FirstOrDefault(s => s.Id == target)
                    ?? document.Shortcuts.FirstOrDefault(s =>
                        string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase));

        if (child == null)
            return ActionResult.Fail("TargetMissing", Loc.Format("Shortcuts_Error_TargetMissing", target), "target");

        // Direct and indirect recursion are both blocked: the chain holds every shortcut on the
        // call stack, so A → B → A is caught at the second A.
        if (context.Chain.Contains(child.Id, StringComparer.Ordinal))
            return ActionResult.Fail("Recursion", Loc.Format("Shortcuts_Error_Recursion", child.Name), "target");

        if (context.Chain.Count >= MaxDepth)
            return ActionResult.Fail("TooDeep", Loc.Get("Shortcuts_Error_TooDeep"), "target");

        var inputs = ParseArguments(context.Text(action, "arguments"));
        if (inputs.Error != null) return ActionResult.Fail(inputs.Error);

        var services = context.Services.WithChain([.. context.Chain, child.Id]);
        var options = new RunOptions
        {
            ParentRunId = context.RunId,
            CancellationToken = context.Token,
            Budget = context.Budget,
            Inputs = inputs.Values,
            Depth = context.Chain.Count,
        };

        var result = await ShortcutRunner.RunAsync(child, services, options).ConfigureAwait(true);

        if (result.Busy)
            return ActionResult.Fail("ChildBusy", Loc.Format("Shortcuts_Error_ChildBusy", child.Name));

        // Cancellation is not a failure to recover from: it must travel up so the parent run
        // reports "cancelled" instead of "failed".
        if (result.Cancelled) throw new OperationCanceledException(context.Token);

        if (!result.Success)
        {
            return ActionResult.Fail("ChildFailed",
                Loc.Format("Shortcuts_Error_ChildFailed", child.Name, result.Error ?? string.Empty));
        }

        var value = result.ReturnValue ?? RunValue.Null;
        return ActionResult.With(
            new Dictionary<string, RunValue> { ["result"] = value },
            Loc.Format("Shortcuts_Output_ChildRan", child.Name));
    }

    private static (IReadOnlyDictionary<string, RunValue>? Values, ActionError? Error) ParseArguments(string raw)
    {
        var map = new Dictionary<string, RunValue>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return (map, null);

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (null, ActionError.Of("ArgumentsInvalid", Loc.Get("Shortcuts_Error_Arguments")));

            foreach (var property in document.RootElement.EnumerateObject())
            {
                map[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.Number => RunValue.FromNumber(property.Value.GetDouble()),
                    JsonValueKind.True => RunValue.FromBoolean(true),
                    JsonValueKind.False => RunValue.FromBoolean(false),
                    JsonValueKind.Null => RunValue.Null,
                    JsonValueKind.Array => RunValue.FromList(property.Value.EnumerateArray()
                        .Select(item => RunValue.FromText(item.ToString()))),
                    JsonValueKind.Object => RunValue.FromJson(property.Value.GetRawText()),
                    _ => RunValue.FromText(property.Value.GetString()),
                };
            }
        }
        catch (JsonException)
        {
            return (null, ActionError.Of("ArgumentsInvalid", Loc.Get("Shortcuts_Error_Arguments")));
        }

        return (map, null);
    }
}
