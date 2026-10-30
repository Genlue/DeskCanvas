using Notes.Models;

namespace Notes.Services;

/// <summary>
/// Resolves and safely reads/writes the markdown documents backing the
/// file/folder note sources.
/// </summary>
public static class NoteFiles
{
    /// <summary>
    /// The documents the widget should display for the given model, in display
    /// order (recent mode: newest first; manual mode: the user's pick order).
    /// Returns full paths. Empty for the internal source.
    /// </summary>
    public static List<string> Resolve(NoteModel model)
    {
        var result = new List<string>();
        try
        {
            switch (model.Source)
            {
                case NoteSource.File when model.Path != null && File.Exists(model.Path):
                    result.Add(model.Path);
                    break;
                case NoteSource.Folder when model.Path != null && Directory.Exists(model.Path):
                {
                    var count = Math.Clamp(model.DocumentCount, 1, 10);
                    var files = Directory.GetFiles(model.Path, "*.md", SearchOption.TopDirectoryOnly);

                    if (!model.RecentFiles && model.SelectedFiles is { Count: > 0 })
                    {
                        var order = model.SelectedFiles;
                        result = files
                            .Where(file => order.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
                            .OrderBy(file => order.FindIndex(
                                name => string.Equals(name, Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)))
                            .Take(count)
                            .ToList();
                    }
                    else
                    {
                        result = files
                            .OrderByDescending(File.GetLastWriteTimeUtc)
                            .Take(count)
                            .ToList();
                    }

                    break;
                }
            }
        }
        catch
        {
            // Unreadable folder (network share, permissions) → render as empty.
        }

        return result;
    }

    /// <summary>Read a document; null when the file is locked or vanished.</summary>
    public static string? Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Write a document, swallowing IO errors (external edits keep going).</summary>
    public static bool Write(string path, string content)
    {
        try
        {
            File.WriteAllText(path, content);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The macOS-Notes-like document title: the first non-empty text line of the
    /// markdown (without its heading marks), falling back to the file name.
    /// </summary>
    public static string TitleOf(string path, string? content)
    {
        var line = content?
            .Split('\n')
            .Select(raw => raw.Trim())
            .FirstOrDefault(raw => !string.IsNullOrWhiteSpace(raw));

        if (line != null)
        {
            line = System.Text.RegularExpressions.Regex.Replace(line, @"^[#>*\-+\s]+", "").Trim();
            if (line.Length > 60) line = string.Concat(line.AsSpan(0, 60), "…");
            if (!string.IsNullOrWhiteSpace(line)) return line;
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// Drop the first non-empty line of a document body — it is already shown as
    /// the document title (macOS Notes behavior), so rendering it again would
    /// duplicate it.
    /// </summary>
    public static string? StripTitleLine(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;

        var lines = content.Replace("\r\n", "\n").Split('\n');
        var firstIndex = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            firstIndex = i;
            break;
        }

        if (firstIndex < 0) return "";

        // Also swallow the blank line(s) right after the title.
        var start = firstIndex + 1;
        while (start < lines.Length && string.IsNullOrWhiteSpace(lines[start])) start++;

        return string.Join("\n", lines.Skip(start));
    }
}
