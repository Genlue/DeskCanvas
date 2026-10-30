using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using Notes.Models;

namespace Notes.Services;

/// <summary>
/// Import/export of the markdown typography preset (body font plus the
/// light/dark color palettes) as a small portable JSON document. Colors are
/// normalized to full-fidelity <c>#AARRGGBB</c> strings so files written by
/// other tools stay parseable.
/// </summary>
public static class StylePreset
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The on-disk shape: everything but the per-widget enable flag.</summary>
    private sealed record PresetFile(string? Font, MarkdownPalette? Light, MarkdownPalette? Dark);

    /// <summary>Serialize a typography (the Enabled flag is not stored).</summary>
    public static string Serialize(MarkdownTypography style) =>
        JsonSerializer.Serialize(new PresetFile(style.Font, Clean(style.Light), Clean(style.Dark)), Options);

    /// <summary>
    /// Parse a preset file; null when the JSON is invalid or carries nothing
    /// usable (no font, no colors). The result is always enabled.
    /// </summary>
    public static MarkdownTypography? Parse(string json)
    {
        PresetFile? file;
        try
        {
            file = JsonSerializer.Deserialize<PresetFile>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (file == null) return null;

        var font = string.IsNullOrWhiteSpace(file.Font) ? null : file.Font.Trim();
        var light = Clean(file.Light);
        var dark = Clean(file.Dark);
        if (font == null && light == null && dark == null) return null;

        return new MarkdownTypography(true, font, light, dark);
    }

    /// <summary>Drop empty palettes and colors that do not parse.</summary>
    private static MarkdownPalette? Clean(MarkdownPalette? palette)
    {
        if (palette == null) return null;

        var cleaned = palette with
        {
            BodyColor = Normalize(palette.BodyColor),
            HeadingColor = Normalize(palette.HeadingColor),
            BoldColor = Normalize(palette.BoldColor),
            ItalicColor = Normalize(palette.ItalicColor),
            StrikeColor = Normalize(palette.StrikeColor),
            LinkColor = Normalize(palette.LinkColor),
            CodeColor = Normalize(palette.CodeColor),
            QuoteColor = Normalize(palette.QuoteColor),
        };

        return cleaned is { BodyColor: null, HeadingColor: null, BoldColor: null, ItalicColor: null,
            StrikeColor: null, LinkColor: null, CodeColor: null, QuoteColor: null }
            ? null
            : cleaned;
    }

    /// <summary>Any Color.TryParse-able spelling to a canonical #AARRGGBB (null when unparseable).</summary>
    private static string? Normalize(string? color) =>
        color != null && Color.TryParse(color, out var parsed)
            ? $"#{parsed.A:X2}{parsed.R:X2}{parsed.G:X2}{parsed.B:X2}"
            : null;
}
