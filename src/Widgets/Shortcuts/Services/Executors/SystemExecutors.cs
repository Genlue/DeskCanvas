using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Shortcuts.Models;

namespace Shortcuts.Services.Executors;

/// <summary>Volume: sets a level and reports the level the machine actually ended up at.</summary>
internal sealed class SetVolumeExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var level = context.Number(action, "level");
        var system = context.Services.System;

        try
        {
            system.SetVolume(level);
            var actual = system.GetVolume();
            return Task.FromResult(ActionResult.With(
                new Dictionary<string, RunValue> { ["level"] = RunValue.FromNumber(actual) },
                Loc.Format("Shortcuts_Output_Volume", actual)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("VolumeFailed", ex.Message, "level"));
        }
    }
}

/// <summary>Flip mute and report the resulting state (never touches the volume level).</summary>
internal sealed class MuteToggleExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var system = context.Services.System;
        try
        {
            var next = !system.GetMute();
            system.SetMute(next);
            var actual = system.GetMute();
            return Task.FromResult(ActionResult.With(
                new Dictionary<string, RunValue> { ["muted"] = RunValue.FromBoolean(actual) },
                Loc.Get(actual ? "Shortcuts_Output_Muted" : "Shortcuts_Output_Unmuted")));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("MuteFailed", ex.Message));
        }
    }
}

/// <summary>Set mute to an explicit value — the deterministic counterpart of the toggle.</summary>
internal sealed class SetMuteExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var muted = context.Bool(action, "muted");
        var system = context.Services.System;

        try
        {
            system.SetMute(muted);
            var actual = system.GetMute();
            return Task.FromResult(ActionResult.With(
                new Dictionary<string, RunValue> { ["muted"] = RunValue.FromBoolean(actual) },
                Loc.Get(actual ? "Shortcuts_Output_Muted" : "Shortcuts_Output_Unmuted")));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("MuteFailed", ex.Message, "muted"));
        }
    }
}

internal sealed class LockScreenExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        try
        {
            context.Services.System.LockWorkStation();
            return Task.FromResult(ActionResult.Ok(Loc.Get("Shortcuts_Output_Locked")));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("LockFailed", ex.Message));
        }
    }
}

internal sealed class SleepExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        try
        {
            context.Services.System.Suspend();
            return Task.FromResult(ActionResult.Ok(Loc.Get("Shortcuts_Output_Sleep")));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("SleepFailed", ex.Message));
        }
    }
}

/// <summary>Capture a screenshot and report the file that was actually written.</summary>
internal sealed class ScreenshotExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var scope = context.Text(action, "scope") == "primary" ? ScreenshotScope.Primary : ScreenshotScope.Virtual;
        var directory = context.Text(action, "directory");

        try
        {
            var file = context.Services.System.CaptureScreenshot(directory, scope);
            return Task.FromResult(ActionResult.With(
                new Dictionary<string, RunValue> { ["path"] = RunValue.FromPath(file) },
                Loc.Format("Shortcuts_Output_Screenshot", file)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("ScreenshotFailed", ex.Message, "directory"));
        }
    }
}

internal sealed class EmptyRecycleBinExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        try
        {
            context.Services.System.EmptyRecycleBin();
            return Task.FromResult(ActionResult.Ok(Loc.Get("Shortcuts_Output_RecycleBin")));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("RecycleBinFailed", ex.Message));
        }
    }
}

/// <summary>Wait for a number of seconds; produces no data, only the exec exit.</summary>
internal sealed class WaitExecutor : IActionExecutor
{
    public async Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var seconds = context.Number(action, "seconds");
        if (seconds < 0) seconds = 0;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), context.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (context.Token.IsCancellationRequested)
        {
            throw;
        }

        return ActionResult.Ok(Loc.Format("Shortcuts_Output_Waited", seconds));
    }
}

/// <summary>Show a notification; the message is the effect, so nothing is returned as data.</summary>
internal sealed class NotifyExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var message = context.Text(action, "message");
        if (message.Length == 0)
        {
            return Task.FromResult(ActionResult.Fail("EmptyParam",
                Loc.Format("Shortcuts_Error_EmptyParam", Loc.Get("Shortcuts_Param_message")), "message"));
        }

        var title = context.Text(action, "title");

        try
        {
            context.Services.System.ShowNotification(message, title.Length == 0 ? null : title);
            return Task.FromResult(ActionResult.Ok(message));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("NotifyFailed", ex.Message));
        }
    }
}

internal sealed class MinimizeAllExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        try
        {
            context.Services.System.MinimizeAll();
            return Task.FromResult(ActionResult.Ok(Loc.Get("Shortcuts_Output_Minimized")));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("MinimizeFailed", ex.Message));
        }
    }
}

/// <summary>Media transport; "no active session" is reported, not thrown.</summary>
internal sealed class MediaExecutor(MediaAction mediaAction) : IActionExecutor
{
    public async Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        try
        {
            var handled = await context.Services.System.MediaTransportAsync(mediaAction).ConfigureAwait(true);
            return handled
                ? ActionResult.Ok(Loc.Get("Shortcuts_Output_Media"))
                : ActionResult.Fail("NoMediaSession", Loc.Get("Shortcuts_Error_NoMedia"));
        }
        catch (Exception ex)
        {
            return ActionResult.Fail("MediaFailed", ex.Message);
        }
    }
}

/// <summary>
/// Run a command in PowerShell or cmd and capture its streams. PowerShell receives the script
/// through <c>-EncodedCommand</c> (base64 UTF-16), so quotes, newlines and non-ASCII text need
/// no escaping; cmd receives it on stdin, the only channel that runs multi-line cmd input
/// without a temp file. The step reports <c>stdout</c> / <c>stderr</c> / <c>exitCode</c>, and a
/// non-zero exit code fails the step while <c>failOnExit</c> is on.
/// </summary>
internal sealed class RunCommandExecutor : IActionExecutor
{
    public async Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var shell = context.Text(action, "shell");
        if (string.IsNullOrWhiteSpace(shell)) shell = "powershell";

        var command = context.Text(action, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            return ActionResult.Fail("EmptyParam",
                Loc.Format("Shortcuts_Error_EmptyParam", Loc.Get("Shortcuts_Param_command")), "command");
        }

        var workingDir = context.Text(action, "workingDir").Trim();
        if (workingDir.Length > 0 && !Directory.Exists(workingDir))
        {
            return ActionResult.Fail("PathNotFound",
                Loc.Format("Shortcuts_Error_PathNotFound", workingDir), "workingDir");
        }

        var timeoutSeconds = context.Number(action, "timeout");
        if (timeoutSeconds <= 0) timeoutSeconds = 30;
        var failOnExit = !string.Equals(context.Text(action, "failOnExit"), "false", StringComparison.OrdinalIgnoreCase);

        try
        {
            // The run token and the per-parameter timeout are linked: either one stops the child.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var (stdout, stderr, exitCode) = await RunAsync(shell, command, workingDir, cts.Token);

            var outputs = new Dictionary<string, RunValue>
            {
                ["stdout"] = RunValue.FromText(stdout),
                ["stderr"] = RunValue.FromText(stderr),
                ["exitCode"] = RunValue.FromNumber(exitCode),
            };

            if (exitCode != 0 && failOnExit)
            {
                var detail = stderr.Trim().Length > 0 ? "\n" + Truncate(stderr.Trim(), 400) : string.Empty;
                return new ActionResult(outputs, Loc.Format("Shortcuts_Output_CommandExit", exitCode),
                    ActionError.Of("CommandFailed",
                        Loc.Format("Shortcuts_Error_CommandFailed", exitCode) + detail, "command"));
            }

            return ActionResult.With(outputs, Loc.Format("Shortcuts_Output_CommandExit", exitCode));
        }
        catch (OperationCanceledException) when (context.Token.IsCancellationRequested)
        {
            // The run itself was cancelled — let the runner report it as such.
            throw;
        }
        catch (OperationCanceledException)
        {
            return ActionResult.Fail("CommandTimeout",
                Loc.Format("Shortcuts_Error_CommandTimeout", timeoutSeconds), "timeout");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            // Only pwsh can realistically be missing; powershell.exe and cmd.exe ship with Windows.
            return ActionResult.Fail("ShellNotFound", Loc.Get("Shortcuts_Error_ShellNotFound"), "shell");
        }
        catch (Exception ex)
        {
            return ActionResult.Fail("CommandFailed", ex.Message, "command");
        }
    }

    private static async Task<(string StdOut, string StdErr, int ExitCode)> RunAsync(
        string shell, string command, string workingDir, CancellationToken token)
    {
        using var process = BuildProcess(shell, command, workingDir);

        process.Start();

        // Both streams must be drained while the child runs; reading them only after exit would
        // deadlock on any command that fills a pipe buffer.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
        var stderrTask = process.StandardError.ReadToEndAsync(token);

        if (process.StartInfo.RedirectStandardInput)
        {
            await process.StandardInput.WriteAsync(BuildCmdScript(command).AsMemory(), token);
            process.StandardInput.Close();
        }

        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            // Timeout or cancel: take the whole tree down, then surface the cancellation.
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        return (await stdoutTask, await stderrTask, process.ExitCode);
    }

    private static Process BuildProcess(string shell, string command, string workingDir)
    {
        var isCmd = string.Equals(shell, "cmd", StringComparison.OrdinalIgnoreCase);
        var isPwsh = string.Equals(shell, "pwsh", StringComparison.OrdinalIgnoreCase);

        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = isCmd, // PowerShell gets an encoded command line instead
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (workingDir.Length > 0) start.WorkingDirectory = workingDir;

        if (isCmd)
        {
            // cmd writes in the OEM code page (GBK on Chinese systems): decode with the real
            // code page instead of assuming UTF-8 and garbling every non-ASCII byte. The script
            // itself is written to stdin in the same encoding, or cmd would misread it.
            start.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            start.Arguments = "/d /q";
            var oem = OemEncoding();
            start.StandardInputEncoding = oem;
            start.StandardOutputEncoding = oem;
            start.StandardErrorEncoding = oem;
        }
        else
        {
            start.FileName = isPwsh
                ? "pwsh"
                : Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

            // Force UTF-8 console output so the wrapper can decode without guessing the code
            // page; the assignment is a prefix statement, never part of the user's script.
            var script = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8\n" + command;
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            start.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
        }

        return new Process { StartInfo = start };
    }

    /// <summary>cmd reads the script from stdin: normalize line endings, then exit.</summary>
    private static string BuildCmdScript(string command) =>
        command.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n") + "\r\nexit\r\n";

    private static Encoding OemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // Already registered or unavailable — the fallback below still holds.
        }

        try
        {
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
