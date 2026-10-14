using System.Text.Json;
using DeskCanvas.Core.Interfaces;

namespace DeskCanvas.Core.Services;

/// <inheritdoc />
public class JsonParser<T>(string filePath) : IDataProvider<T>
{
    /// <summary>
    /// Cached deserialized data; <c>null</c> until the first <see cref="Get"/> call.
    /// </summary>
    protected T? data;
    /// <inheritdoc />
    public event DataChangedEvent<T>? DataChanging;
    /// <inheritdoc />
    public event DataChangedEvent<T>? DataChanged;

    /// <inheritdoc />
    public virtual T Get()
    {
        if (data != null) return data;

        var json = File.ReadAllText(filePath);

        data = JsonSerializer.Deserialize<T>(json)
               ?? throw new FormatException($"Can't deserialize {typeof(T).Name}");

        return data = Normalize(data);
    }

    /// <inheritdoc />
    public void Save(T newData)
    {
        var oldData = data;
        DataChanging?.Invoke(this, data, newData);
        data = newData;

        File.WriteAllText(filePath, SerializeForFile(newData));
        DataChanged?.Invoke(this, oldData, newData);
    }

    /// <summary>
    /// Hook for transforming the in-memory value into the on-disk JSON (e.g. persisting a
    /// mode token in a form older builds can still parse). The cached in-memory value stays
    /// the semantic one — only the file content is mapped. Default: serialize as-is.
    /// </summary>
    protected virtual string SerializeForFile(T value) => JsonSerializer.Serialize(value);

    /// <summary>
    /// Hook for normalizing deserialized data (e.g. filling in defaults for
    /// settings added in newer versions). Called once after the first read.
    /// </summary>
    protected virtual T Normalize(T value) => value;
}
