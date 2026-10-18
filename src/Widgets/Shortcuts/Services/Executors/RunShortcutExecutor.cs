using Shortcuts.Models;

namespace Shortcuts.Services.Executors;

/// <summary>
/// Run another shortcut by id or display name. The nesting chain blocks direct/indirect
/// recursion, and the target's own ConfirmBeforeRun flag is honoured before it starts.
/// </summary>
internal sealed class RunShortcutExecutor : IActionExecutor
{
    private const int MaxDepth = 8;

    public async Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var targetKey = context.Resolve(action, "target").Trim();
        if (targetKey.Length == 0)
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_EmptyParam",
                Loc.Get("Shortcuts_Param_shortcut")));

        var document = ShortcutsStore.Get();
        var target = document.Shortcuts.FirstOrDefault(s =>
                         string.Equals(s.Id, targetKey, StringComparison.OrdinalIgnoreCase))
                     ?? document.Shortcuts.FirstOrDefault(s =>
                         string.Equals(s.Name, targetKey, StringComparison.OrdinalIgnoreCase));

        if (target == null)
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_ShortcutNotFound", targetKey));

        var chain = new List<string>(context.Chain) { context.Shortcut.Id };
        if (chain.Contains(target.Id, StringComparer.Ordinal))
            throw new ActionFailedException(Loc.Get("Shortcuts_Error_Recursion"));

        if (chain.Count >= MaxDepth)
            throw new ActionFailedException(Loc.Get("Shortcuts_Error_Depth"));

        if (target.ConfirmBeforeRun)
        {
            if (!await context.ConfirmAsync(Loc.Format("Shortcuts_ConfirmRunMessage", target.Name))
                    .ConfigureAwait(true))
                throw new ActionFailedException(Loc.Get("Shortcuts_Error_ConfirmReject"));
        }

        var services = context.Services.WithChain(chain);
        var result = await ShortcutRunner.RunAsync(target, services).ConfigureAwait(true);
        if (result.Busy)
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_ShortcutBusy", target.Name));

        if (!result.Success && !result.Cancelled)
        {
            throw new ActionFailedException(string.IsNullOrEmpty(result.Error)
                ? Loc.Format("Shortcuts_Error_Failed", target.Name)
                : result.Error);
        }

        if (result.Cancelled)
            throw new ActionFailedException(Loc.Get("Shortcuts_Error_ConfirmReject"));

        return Loc.Format("Shortcuts_Output_RanShortcut", target.Name);
    }
}
