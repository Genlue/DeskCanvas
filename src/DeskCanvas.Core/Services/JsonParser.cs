using System.Text.Json;
using DeskCanvas.Core.Interfaces;

namespace DeskCanvas.Core.Services;

/// <inheritdoc />
public class JsonParser<T>(string filePath) : IDataProvider<T>
{
    private readonly object sync = new();
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

            var json = File.ReadAllText(filePath);

            data = JsonSerializer.Deserialize<T>(json)
                   ?? throw new FormatException($"Can't deserialize {typeof(T).Name}");

            return data = Normalize(data);
        }
    }

    /// <inheritdoc />
    public virtual void Save(T newData)
    {
        lock (sync)
        {
            var oldData = data;
            DataChanging?.Invoke(this, data, newData);

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Commit on the same volume: write and flush a sibling temporary file, then
            // atomically replace the destination. The cache is published only after the
            // filesystem commit succeeds, so failed writes cannot expose phantom state.
            var tempPath = $"{filePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
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

                if (File.Exists(filePath))
                {
                    try
                    {
                        File.Replace(tempPath, filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Move(tempPath, filePath, overwrite: true);
                    }
                }
                else
                {
                    File.Move(tempPath, filePath);
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
