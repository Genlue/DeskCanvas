using Shortcuts.Models;

namespace Shortcuts.Services.Executors;

/// <summary>Shared target validation for the launch family.</summary>
internal static class LaunchTargets
{
    /// <summary>Expand <c>%VAR%</c>, drop surrounding quotes and trim.</summary>
    public static string Normalize(string raw) =>
        Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));

    public static bool LooksLikePath(string target) =>
        target.Contains('\\', StringComparison.Ordinal) ||
        target.Contains('/', StringComparison.Ordinal) ||
        (target.Length > 1 && target[1] == ':');

    public static ActionResult Fail(string code, string messageKey, string? paramKey, params object?[] args) =>
        ActionResult.Fail(code, Loc.Format(messageKey, args), paramKey);
}

/// <summary>Open an application (or any shell-executable target) with optional arguments.</summary>
internal sealed class OpenAppExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var target = LaunchTargets.Normalize(context.Text(action, "target"));
        if (target.Length == 0)
        {
            return Task.FromResult(LaunchTargets.Fail("EmptyParam", "Shortcuts_Error_EmptyParam", "target",
                Loc.Get("Shortcuts_Param_target")));
        }

        // An absolute path that does not exist is a typo, not something the shell can resolve.
        if (LaunchTargets.LooksLikePath(target) && Path.IsPathFullyQualified(target) && !File.Exists(target))
        {
            return Task.FromResult(LaunchTargets.Fail("PathNotFound", "Shortcuts_Error_PathNotFound", "target",
                target));
        }

        var args = context.Text(action, "args").Trim();

        try
        {
            context.Services.System.OpenTarget(target, args);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("LaunchFailed",
                Loc.Format("Shortcuts_Error_NoApp", target) + " - " + ex.Message, "target"));
        }

        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["path"] = RunValue.FromPath(target) },
            Loc.Format("Shortcuts_Output_Launched", target)));
    }
}

/// <summary>Open a file with the default handler; the file must exist.</summary>
internal sealed class OpenPathExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var path = LaunchTargets.Normalize(context.Text(action, "path"));
        if (path.Length == 0)
        {
            return Task.FromResult(LaunchTargets.Fail("EmptyParam", "Shortcuts_Error_EmptyParam", "path",
                Loc.Get("Shortcuts_Param_path")));
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return Task.FromResult(LaunchTargets.Fail("PathNotFound", "Shortcuts_Error_PathNotFound", "path", path));
        }

        try
        {
            context.Services.System.OpenTarget(path, null);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("LaunchFailed",
                Loc.Format("Shortcuts_Error_NoApp", path) + " - " + ex.Message, "path"));
        }

        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["path"] = RunValue.FromPath(path) },
            Loc.Format("Shortcuts_Output_Launched", path)));
    }
}

/// <summary>Open a folder in Explorer; the path must exist so a typo fails loudly.</summary>
internal sealed class OpenFolderExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var path = LaunchTargets.Normalize(context.Text(action, "path"));
        if (path.Length == 0)
        {
            return Task.FromResult(LaunchTargets.Fail("EmptyParam", "Shortcuts_Error_EmptyParam", "path",
                Loc.Get("Shortcuts_Param_path")));
        }

        if (!Directory.Exists(path))
        {
            return Task.FromResult(LaunchTargets.Fail("PathNotFound", "Shortcuts_Error_PathNotFound", "path", path));
        }

        try
        {
            context.Services.System.OpenFolder(path);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("LaunchFailed",
                Loc.Format("Shortcuts_Error_NoApp", path) + " - " + ex.Message, "path"));
        }

        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["path"] = RunValue.FromPath(path) },
            Loc.Format("Shortcuts_Output_Launched", path)));
    }
}

/// <summary>Open a URL in the default browser; returns the URL that was actually opened.</summary>
internal sealed class OpenUrlExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var url = context.Text(action, "url").Trim();
        if (url.Length == 0 || url == "https://")
        {
            return Task.FromResult(LaunchTargets.Fail("EmptyParam", "Shortcuts_Error_EmptyParam", "url",
                Loc.Get("Shortcuts_Param_url")));
        }

        if (!url.Contains("://", StringComparison.Ordinal) &&
            !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps &&
             uri.Scheme != Uri.UriSchemeMailto))
        {
            return Task.FromResult(ActionResult.Fail("InvalidUrl", Loc.Get("Shortcuts_Error_InvalidUrl"), "url"));
        }

        try
        {
            context.Services.System.OpenUrl(url);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("LaunchFailed",
                Loc.Format("Shortcuts_Error_NoApp", url) + " - " + ex.Message, "url"));
        }

        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["url"] = RunValue.FromText(url) },
            Loc.Format("Shortcuts_Output_Launched", url)));
    }
}

/// <summary>Search the web with the chosen engine; returns the search URL.</summary>
internal sealed class WebSearchExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var query = context.Text(action, "query").Trim();
        if (query.Length == 0)
        {
            return Task.FromResult(LaunchTargets.Fail("EmptyParam", "Shortcuts_Error_EmptyParam", "query",
                Loc.Get("Shortcuts_Param_query")));
        }

        var engine = context.Text(action, "engine");
        var url = engine switch
        {
            "baidu" => "https://www.baidu.com/s?wd=",
            "google" => "https://www.google.com/search?q=",
            _ => "https://cn.bing.com/search?q=",
        } + Uri.EscapeDataString(query);

        try
        {
            context.Services.System.OpenUrl(url);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("LaunchFailed",
                Loc.Format("Shortcuts_Error_NoApp", url) + " - " + ex.Message, "query"));
        }

        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["url"] = RunValue.FromText(url) },
            Loc.Format("Shortcuts_Output_Launched", url)));
    }
}
