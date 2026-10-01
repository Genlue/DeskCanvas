using System.Text.Json;

namespace DeskCanvas.Core.Models;

/// <summary>
/// Layout of a single widget, stored in <c>layout.json</c>.
/// </summary>
/// <param name="Type">Assembly name of the widget.</param>
/// <param name="SubType">UserControl name of the widget.</param>
/// <param name="X">X coordinate of the widget's top-left corner.</param>
/// <param name="Y">Y coordinate of the widget's top-left corner.</param>
/// <param name="Width">Width of the widget.</param>
/// <param name="Height">Height of the widget.</param>
/// <param name="Settings">Widget's model as <see cref="JsonElement"/></param>
/// <param name="ContentScale">This widget's content scale (0.5× – 2×).
/// <c>null</c> falls back to the screen's scale, then to 1.0.</param>
public record WidgetLayout(string Type, string SubType, int X, int Y, int Width, int Height, JsonElement? Settings,
    double? ContentScale = null)
{
    /// <summary>
    /// Get the widget's model.
    /// </summary>
    /// <typeparam name="T">Type of the widget's model.</typeparam>
    /// <returns>Widget's model.</returns>
    public T? GetModel<T>() => (T?) GetModel(typeof(T));

    /// <summary>
    /// Identity comparison that ignores <see cref="Settings"/>: <see cref="JsonElement"/>
    /// has no structural equality across documents, so two records parsed separately
    /// (or re-serialized) never compare equal even with identical content. Used to find
    /// a widget's stored entry when the original object reference is gone (legacy
    /// migration, screen hot-plug, ownership transfer) instead of appending a duplicate.
    /// </summary>
    public bool SameWidgetAs(WidgetLayout other) =>
        Type == other.Type && SubType == other.SubType &&
        X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;

    /// <summary>
    /// Locate <paramref name="target"/> inside a stored layout list: by reference first
    /// (the common case — the list still holds the very object the caller owns), then by
    /// <see cref="SameWidgetAs"/> identity for entries that were re-parsed from disk.
    /// <para>
    /// Returns <c>-1</c> when the widget is genuinely absent, which callers must treat as
    /// "this widget is no longer part of the layout" — never as "append a new entry".
    /// <see cref="List{T}.IndexOf(T)"/> / <c>!=</c> cannot be used instead: the record's value
    /// equality compares <see cref="Settings"/> (<see cref="JsonElement"/>) by document
    /// reference, so two entries parsed from different documents never compare equal even
    /// with identical content.
    /// </para>
    /// </summary>
    public static int IndexOfIdentity(IReadOnlyList<WidgetLayout> layout, WidgetLayout target)
    {
        for (var i = 0; i < layout.Count; i++)
            if (ReferenceEquals(layout[i], target)) return i;

        for (var i = 0; i < layout.Count; i++)
            if (layout[i].SameWidgetAs(target)) return i;

        return -1;
    }
    
    /// <summary>
    /// Get the widget's model.
    /// </summary>
    /// <param name="type">Type of the widget's model.</param>
    /// <returns>Widget's model.</returns>
    public object? GetModel(Type? type)
    {
        if (type == null || !Settings.HasValue) 
            return null;

        try
        {
            return Settings.Value.Deserialize(type);
        }
        catch (Exception)
        {
            return null;
        }
    }
}