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
            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Some documents are genuinely optional: a data folder that has never been
                // written (first run, a hand-deleted settings file, a half-migrated upgrade)
                // has no file yet, and "missing" means "start from the defaults", not "crash".
                // The subclass decides whether it can supply them; everything else keeps the
                // platform error.
                var fallback = CreateDefaultIfMissing();
                if (fallback is null) throw;

                // Publish through the regular commit path so the file exists from now on and
                // every other reader (including code that reads the file directly) sees it.
                // Normalized like any deserialized value: the on-disk and in-memory shapes of a
                // document must not differ depending on whether it was read or seeded.
                var seeded = Normalize(fallback);
                Save(seeded);
                return seeded;
            }

            data = JsonSerializer.Deserialize<T>(json)
                   ?? throw new FormatException($"Can't deserialize {typeof(T).Name}");

            return data = Normalize(data);
        }
    }

    /// <summary>
    /// Value to publish and persist when the document file does not exist, or <c>null</c> to let
    /// the platform error surface. Default <c>null</c>: only documents whose absence is a valid
    /// state (the settings file of a brand-new data folder) opt in.
    /// </summary>
    protected virtual T? CreateDefaultIfMissing() => default;

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

    /// <summary>
    /// Explicit interface implementation of <see cref="IDataProvider{T}.Reload"/>.
    /// <para>
    /// <b>Required, not decorative.</b> The interface member is <c>void Reload()</c> with a
    /// no-op default body ("if supported"), while <see cref="Reload"/> returns <c>T</c> — a
    /// returning method cannot implicitly implement a void one, so without this mapping every
    /// interface-typed call (<c>ProfileService.SwitchProfile</c>/<c>RenameProfile</c> reloading
    /// through <see cref="IAppSettingsProvider"/>/<see cref="ILayoutProvider"/>)
    /// silently kept the previous data cached. That made a profile switch a no-op in memory:
    /// the new profile's seeded files were on disk, but the desktop kept the outgoing profile's
    /// widgets and the first layout save then wrote them into the new profile directory.
    /// </para>
    /// </summary>
    void IDataProvider<T>.Reload() => Reload();

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
