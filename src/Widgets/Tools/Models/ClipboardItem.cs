using System;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;

namespace Tools.Models;

public class ClipboardItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipboardType Type { get; set; }
    public string? Text { get; set; }
    public string[]? FilePaths { get; set; }
    public string? ImagePath { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsPinned { get; set; }

    [JsonIgnore]
    public Bitmap? Thumbnail { get; set; }

    private static bool IsZh => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string RelativeTime
    {
        get
        {
            var span = DateTime.Now - Timestamp;
            if (span.TotalSeconds < 60) return IsZh ? "刚刚" : "Just now";
            if (span.TotalMinutes < 60) return IsZh ? $"{(int)span.TotalMinutes} 分钟前" : $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24) return IsZh ? $"{(int)span.TotalHours} 小时前" : $"{(int)span.TotalHours}h ago";
            return Timestamp.ToString("MM-dd HH:mm");
        }
    }

    [JsonIgnore]
    public bool IsLink => !string.IsNullOrWhiteSpace(Text) && (Text.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase) || Text.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    [JsonIgnore]
    public bool IsCode => !string.IsNullOrWhiteSpace(Text) && !IsLink && (Text.Contains("git ") || Text.Contains("npm ") || Text.Contains("const ") || Text.Contains("function") || Text.Contains("class ") || Text.Contains("public ") || Text.Contains("def ") || (Text.Contains("{") && Text.Contains("}")));

    [JsonIgnore]
    public bool IsPlainText => !string.IsNullOrWhiteSpace(Text) && !IsLink && !IsCode;

    [JsonIgnore]
    public string TypeGlyph => Type switch
    {
        ClipboardType.Text => "📝",
        ClipboardType.Image => "🖼️",
        ClipboardType.Files => "📁",
        _ => "📋"
    };

    [JsonIgnore]
    public string DisplayTitle => Type switch
    {
        ClipboardType.Text => string.IsNullOrWhiteSpace(Text) ? (IsZh ? "(空白文本)" : "(Empty text)") : Text.Replace("\r", " ").Replace("\n", " ").Trim(),
        ClipboardType.Image => IsZh ? $"图片 ({ImageWidth}×{ImageHeight})" : $"Image ({ImageWidth}×{ImageHeight})",
        ClipboardType.Files => FilePaths?.Length > 0 ? Path.GetFileName(FilePaths[0]) : (IsZh ? "文件" : "File"),
        _ => IsZh ? "剪贴板条目" : "Clipboard item"
    };

    [JsonIgnore]
    public string DisplaySubtitle => Type switch
    {
        ClipboardType.Text => IsZh ? $"{Text?.Length ?? 0} 字符" : $"{Text?.Length ?? 0} chars",
        ClipboardType.Image => IsZh ? $"{ImageWidth}×{ImageHeight} 像素" : $"{ImageWidth}×{ImageHeight} px",
        ClipboardType.Files => FilePaths?.Length > 1 ? (IsZh ? $"等共 {FilePaths.Length} 个文件" : $"+{FilePaths.Length - 1} more ({FilePaths.Length} files)") : (FilePaths?.Length == 1 ? FilePaths[0] : ""),
        _ => ""
    };
}
