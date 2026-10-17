using System.Text.Json;
using DeskCanvas.Core.Interfaces;

namespace DeskCanvas.Core.Services;

/// <inheritdoc />
public class JsonParser<T> : IDataProvider<T>
{
    private readonly object sync = new();
    private readonly Func<string>? filePathAccessor;
    private string? filePath;

    /// <summary>
    /// Constructs a parser with a static file path.
    /// </summary>
    public JsonParser(string filePath)
    {
        this.filePath = filePath;
    }

    /// <summary>
    /// Constructs a parser with a dynamic file path accessor.
    /// </summary>
    public JsonParser(Func<string> filePathAccessor)
    {
        this.filePathAccessor = filePathAccessor;
    }

    /// <summary>
    /// Gets the current file path, evaluated dynamically if accessor was provided.
    /// </summary>
    protected string CurrentFilePath => filePathAccessor != null ? filePathAccessor() : (filePath ?? string.Empty);

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
        lock (sync)
        {
            if (data != null) return data;

            var path = CurrentFilePath;
            var json = File.ReadAllText(path);

            data = JsonSerializer.Deserialize<T>(json)
                   ?? throw new FormatException($"Can't deserialize {typeof(T).Name}");

            return data = Normalize(data);
        }
    }

    /// <summary>
    /// Invalidate in-memory cached data and re-read from the current file path.
    /// </summary>
    public virtual T Reload()
    {
        lock (sync)
        {
            var oldData = data;
            data = default;
            var reloaded = Get();
            DataChanged?.Invoke(this, oldData, reloaded);
            return reloaded;
        }
    }

    /// <inheritdoc />
    public virtual void Save(T newData)
    {
        lock (sync)
        {
            var oldData = data;
            DataChanging?.Invoke(this, data, newData);

            var path = CurrentFilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Commit on the same volume: write and flush a sibling temporary file, then
            // atomically replace the destination. The cache is published only after the
            // filesystem commit succeeds, so failed writes cannot expose phantom state.
            var tempPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                var json = SerializeForFile(newData);
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Move(tempPath, path, overwrite: true);
                    }
                }
                else
                {
                    File.Move(tempPath, path);
                }
                data = newData;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort cleanup; the committed destination remains authoritative.
                }
            }

            DataChanged?.Invoke(this, oldData, newData);
        }
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
