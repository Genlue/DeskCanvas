using System.Text;

namespace Timetable.Services;

/// <summary>
/// Loads the built-in JSON writing guide (an embedded Markdown document) and extracts
/// the ready-to-copy AI prompt between its marker comments. Both are cached lazily —
/// the document is a few kilobytes and never changes at runtime.
/// </summary>
public static class TimetableGuide
{
    private const string ResourceName = "Timetable.Assets.timetable-json-guide.md";
    private const string PromptStart = "<!-- AI-PROMPT:START -->";
    private const string PromptEnd = "<!-- AI-PROMPT:END -->";

    private static readonly Lazy<string> fullText = new(LoadFullText);
    private static readonly Lazy<string> prompt = new(ExtractPrompt);

    /// <summary>The whole guide, shown in the format-guide window.</summary>
    public static string FullText => fullText.Value;

    /// <summary>The copyable AI prompt (the marked section of the guide).</summary>
    public static string Prompt => prompt.Value;

    private static string LoadFullText()
    {
        try
        {
            using var stream = typeof(TimetableGuide).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null) return "（写法文档资源缺失，请重新安装组件）";

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return "（写法文档读取失败）";
        }
    }

    private static string ExtractPrompt()
    {
        var text = FullText;
        var start = text.IndexOf(PromptStart, StringComparison.Ordinal);
        var end = text.IndexOf(PromptEnd, StringComparison.Ordinal);
        if (start < 0 || end <= start) return text;

        return text[(start + PromptStart.Length)..end].Trim();
    }
}
