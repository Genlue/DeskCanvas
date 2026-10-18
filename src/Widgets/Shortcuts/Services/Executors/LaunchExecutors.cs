using System.Diagnostics;
using Shortcuts.Models;

namespace Shortcuts.Services.Executors;

/// <summary>Open an application (or any shell-executable target) with optional arguments.</summary>
internal sealed class OpenAppExecutor : IActionExecutor
{
    public Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var target = Environment.ExpandEnvironmentVariables(context.Resolve(action, "target").Trim());
        if (target.Length == 0)
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_EmptyParam", Loc.Get("Shortcuts_Param_target")));

        var args = context.Resolve(action, "args").Trim();
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            };
            if (args.Length > 0) startInfo.Arguments = args;
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_NoApp", target) + " - " + ex.Message);
        }

        return Task.FromResult<string?>(Path.GetFileNameWithoutExtension(target));
    }
}

/// <summary>Open a file, folder or shell target with the default handler.</summary>
internal sealed class OpenPathExecutor : IActionExecutor
{
    public Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var path = Environment.ExpandEnvironmentVariables(context.Resolve(action, "path").Trim());
        if (path.Length == 0)
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_EmptyParam", Loc.Get("Shortcuts_Param_path")));

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_NoApp", path) + " - " + ex.Message);
        }

        return Task.FromResult<string?>(Path.GetFileName(path.TrimEnd('\\', '/')));
    }
}

/// <summary>Open a folder in Explorer; the path must exist so a typo fails loudly.</summary>
internal sealed class OpenFolderExecutor : IActionExecutor
{
    public Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var path = Environment.ExpandEnvironmentVariables(context.Resolve(action, "path").Trim());
        if (path.Length == 0)
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_EmptyParam", Loc.Get("Shortcuts_Param_path")));

        if (!Directory.Exists(path))
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_PathNotFound", path));

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_NoApp", path) + " - " + ex.Message);
        }

        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return Task.FromResult<string?>(Loc.Format("Shortcuts_Output_OpenedFolder",
            string.IsNullOrEmpty(name) ? path : name));
    }
}

/// <summary>Open a URL in the default browser.</summary>
internal sealed class OpenUrlExecutor : IActionExecutor
{
    public Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var url = context.ResolveOrDefault(action, "url").Trim();
        if (url.Length == 0 || url == "https://")
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_EmptyParam", Loc.Get("Shortcuts_Param_url")));

        if (!url.Contains("://", StringComparison.Ordinal) && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_NoApp", url) + " - " + ex.Message);
        }

        return Task.FromResult<string?>(url);
    }
}

/// <summary>Search the web with the chosen engine.</summary>
internal sealed class WebSearchExecutor : IActionExecutor
{
    public Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var query = context.Resolve(action, "query").Trim();
        if (query.Length == 0)
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_EmptyParam", Loc.Get("Shortcuts_Param_query")));

        var engine = context.ResolveOrDefault(action, "engine");
        var url = engine switch
        {
            "baidu" => "https://www.baidu.com/s?wd=",
            "google" => "https://www.google.com/search?q=",
            _ => "https://cn.bing.com/search?q=",
        } + Uri.EscapeDataString(query);

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_NoApp", url) + " - " + ex.Message);
        }

        return Task.FromResult<string?>(query);
    }
}
