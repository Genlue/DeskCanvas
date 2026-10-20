using System.Text;
using Shortcuts.Models;

namespace Shortcuts.Services.Executors;

/// <summary>Encoding selection shared by the text-file actions.</summary>
internal static class FileText
{
    /// <summary>Upper bound for a single read or write (a shortcut is not a file manager).</summary>
    public const int MaxBytes = 5 * 1024 * 1024;

    static FileText()
    {
        // GBK / GB2312 are not in the default provider on .NET Core.
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // Already registered or unavailable: UTF-8 still works.
        }
    }

    public static Encoding Resolve(string choice) => choice switch
    {
        "gbk" => GetOrUtf8("GBK"),
        "utf8" => new UTF8Encoding(false),
        _ => new UTF8Encoding(false),
    };

    public static Encoding Detect(byte[] bytes, string choice)
    {
        if (choice is "utf8" or "gbk") return Resolve(choice);

        // auto: a BOM wins, then valid UTF-8, then GBK.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(false);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode;

        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            return GetOrUtf8("GBK");
        }
    }

    private static Encoding GetOrUtf8(string name)
    {
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return new UTF8Encoding(false);
        }
    }
}

/// <summary>Pick one file with the native dialog.</summary>
internal sealed class SelectFileExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var title = context.Text(action, "title");
        if (title.Length == 0) title = Loc.Get("Shortcuts_Param_PickFile");

        try
        {
            var path = context.Services.Prompts.PickFile(title, context.Text(action, "filter"));
            if (string.IsNullOrEmpty(path))
                return Task.FromResult(ActionResult.Fail("SelectionCancelled",
                    Loc.Get("Shortcuts_Error_SelectionCancelled")));

            return Task.FromResult(ActionResult.With(
                new Dictionary<string, RunValue> { ["path"] = RunValue.FromPath(path) }, path));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("PickerFailed", ex.Message));
        }
    }
}

/// <summary>Pick one folder with the native dialog.</summary>
internal sealed class SelectFolderExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var title = context.Text(action, "title");
        if (title.Length == 0) title = Loc.Get("Shortcuts_Param_PickFolder");

        try
        {
            var path = context.Services.Prompts.PickFolder(title);
            if (string.IsNullOrEmpty(path))
                return Task.FromResult(ActionResult.Fail("SelectionCancelled",
                    Loc.Get("Shortcuts_Error_SelectionCancelled")));

            return Task.FromResult(ActionResult.With(
                new Dictionary<string, RunValue> { ["path"] = RunValue.FromPath(path) }, path));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("PickerFailed", ex.Message));
        }
    }
}

/// <summary>True when the file or folder exists (never an error for a missing path).</summary>
internal sealed class PathExistsExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var path = LaunchTargets.Normalize(context.Text(action, "path"));
        var exists = File.Exists(path) || Directory.Exists(path);

        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["exists"] = RunValue.FromBoolean(exists) },
            exists ? Loc.Get("Shortcuts_Output_Exists") : Loc.Get("Shortcuts_Output_Missing")));
    }
}

/// <summary>Read a text file with an explicit or detected encoding.</summary>
internal sealed class ReadTextFileExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var path = LaunchTargets.Normalize(context.Text(action, "path"));
        if (!File.Exists(path))
            return Task.FromResult(ActionResult.Fail("PathNotFound", Loc.Format("Shortcuts_Error_PathNotFound", path),
                "path"));

        try
        {
            var info = new FileInfo(path);
            if (info.Length > FileText.MaxBytes)
            {
                return Task.FromResult(ActionResult.Fail("FileTooLarge",
                    Loc.Format("Shortcuts_Error_FileTooLarge", info.Length / 1024), "path"));
            }

            var bytes = File.ReadAllBytes(path);
            var encoding = FileText.Detect(bytes, context.Text(action, "encoding"));
            var text = encoding.GetString(bytes);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

            return Task.FromResult(ActionResult.With(
                new Dictionary<string, RunValue> { ["text"] = RunValue.FromText(text) },
                Loc.Format("Shortcuts_Output_FileRead", text.Length)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("ReadFailed", ex.Message, "path"));
        }
    }
}

/// <summary>
/// Write text to a file. <c>create</c> refuses to overwrite by default — silently replacing a
/// user's file is exactly the kind of side effect a workflow must opt into.
/// </summary>
internal sealed class WriteTextFileExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var path = LaunchTargets.Normalize(context.Text(action, "path"));
        var text = context.Text(action, "text");
        var mode = context.Text(action, "mode");
        var encoding = FileText.Resolve(context.Text(action, "encoding"));

        if (path.Length == 0)
            return Task.FromResult(ActionResult.Fail("EmptyParam",
                Loc.Format("Shortcuts_Error_EmptyParam", Loc.Get("Shortcuts_Param_path")), "path"));

        if (!Path.IsPathFullyQualified(path))
            return Task.FromResult(ActionResult.Fail("NotAbsolute",
                Loc.Format("Shortcuts_Error_NotAbsolute", path), "path"));

        if (encoding.GetByteCount(text) > FileText.MaxBytes)
            return Task.FromResult(ActionResult.Fail("FileTooLarge", Loc.Get("Shortcuts_Error_FileTooLargeOut"),
                "text"));

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            switch (mode)
            {
                case "append":
                    File.AppendAllText(path, text, encoding);
                    break;

                case "overwrite":
                    File.WriteAllText(path, text, encoding);
                    break;

                default:
                    if (File.Exists(path))
                    {
                        return Task.FromResult(ActionResult.Fail("FileExists",
                            Loc.Format("Shortcuts_Error_FileExists", path), "mode"));
                    }

                    File.WriteAllText(path, text, encoding);
                    break;
            }

            return Task.FromResult(ActionResult.With(
                new Dictionary<string, RunValue> { ["path"] = RunValue.FromPath(path) },
                Loc.Format("Shortcuts_Output_FileWritten", path)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("WriteFailed", ex.Message, "path"));
        }
    }
}

/// <summary>List files under a directory, optionally recursive.</summary>
internal sealed class ListFilesExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var directory = LaunchTargets.Normalize(context.Text(action, "directory"));
        if (!Directory.Exists(directory))
        {
            return Task.FromResult(ActionResult.Fail("PathNotFound",
                Loc.Format("Shortcuts_Error_PathNotFound", directory), "directory"));
        }

        var filter = context.Text(action, "filter").Trim();
        if (filter.Length == 0) filter = "*";
        var recursive = context.Bool(action, "recursive");

        try
        {
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.EnumerateFiles(directory, filter, option)
                .Take(5000)
                .Select(f => RunValue.FromPath(f))
                .ToList();

            return Task.FromResult(ActionResult.With(
                new Dictionary<string, RunValue> { ["list"] = RunValue.FromList(files) },
                Loc.Format("Shortcuts_Output_ListCount", files.Count)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("ListFailed", ex.Message, "directory"));
        }
    }
}

/// <summary>Join a base folder and a relative child path.</summary>
internal sealed class PathJoinExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var basePath = LaunchTargets.Normalize(context.Text(action, "base"));
        var child = context.Text(action, "child").Trim();

        if (basePath.Length == 0 || child.Length == 0)
        {
            return Task.FromResult(ActionResult.Fail("EmptyParam",
                Loc.Get("Shortcuts_Error_EmptyParam"), "child"));
        }

        try
        {
            var combined = Path.GetFullPath(Path.Combine(basePath, child));
            return Task.FromResult(ActionResult.With(
                new Dictionary<string, RunValue> { ["path"] = RunValue.FromPath(combined) }, combined));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("PathInvalid", ex.Message, "child"));
        }
    }
}

/// <summary>Name / extension / parent of a path.</summary>
internal sealed class PathInfoExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var path = LaunchTargets.Normalize(context.Text(action, "path"));
        if (path.Length == 0)
        {
            return Task.FromResult(ActionResult.Fail("EmptyParam",
                Loc.Format("Shortcuts_Error_EmptyParam", Loc.Get("Shortcuts_Param_path")), "path"));
        }

        try
        {
            var name = Path.GetFileName(path);
            var extension = Path.GetExtension(path);
            var parent = Path.GetDirectoryName(path) ?? string.Empty;

            return Task.FromResult(ActionResult.With(new Dictionary<string, RunValue>
            {
                ["name"] = RunValue.FromText(name),
                ["extension"] = RunValue.FromText(extension),
                ["parent"] = RunValue.FromPath(parent),
            }, name));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("PathInvalid", ex.Message, "path"));
        }
    }
}
