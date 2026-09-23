using System.Text.Json;

namespace Jellyfin.Plugin.MetaTagger;

internal interface IMetaTaggerStateFilePromoter
{
    void Promote(string tempPath, string destinationPath);
}

internal sealed class MetaTaggerStateFilePromoter : IMetaTaggerStateFilePromoter
{
    public void Promote(string tempPath, string destinationPath)
    {
        File.Move(tempPath, destinationPath, overwrite: true);
    }
}

internal interface IMetaTaggerStateStore
{
    Task<MetaTaggerState> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(MetaTaggerState state, CancellationToken cancellationToken);

    Task SaveRunAsync(MetaTaggerRunRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

    Task SaveSummaryAsync(MetaTaggerRunSummary summary, CancellationToken cancellationToken);

    Task<string> SavePreviewChangesAsync(
        IReadOnlyCollection<MetaTaggerPreviewChange> changes,
        CancellationToken cancellationToken);
}

public sealed partial class MetaTaggerStateStore : IMetaTaggerStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _directory;
    private readonly IMetaTaggerStateFilePromoter _filePromoter;
    private readonly SemaphoreSlim _stateFileGate = new(1, 1);
    private bool _recoveredFromBackup;

    public MetaTaggerStateStore()
        : this(Plugin.Instance?.DataFolderPath ?? Path.Combine(AppContext.BaseDirectory, "meta-tagger"))
    {
    }

    public MetaTaggerStateStore(string directory)
        : this(directory, new MetaTaggerStateFilePromoter())
    {
    }

    internal MetaTaggerStateStore(string directory, IMetaTaggerStateFilePromoter filePromoter)
    {
        _directory = directory;
        _filePromoter = filePromoter;
    }

    public async Task<MetaTaggerState> LoadAsync(CancellationToken cancellationToken)
    {
        await _stateFileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(_directory, "meta-tagger-state.json");
            if (!File.Exists(path) && !File.Exists(BackupPath(path)))
            {
                return new MetaTaggerState();
            }

            try
            {
                return Normalize(await ReadJsonAsync<MetaTaggerState>(path, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OperationCanceledException && File.Exists(BackupPath(path)))
            {
                var recovered = Normalize(
                    await ReadJsonAsync<MetaTaggerState>(BackupPath(path), cancellationToken).ConfigureAwait(false));
                _recoveredFromBackup = true;
                return recovered;
            }
        }
        finally
        {
            _stateFileGate.Release();
        }
    }

    public async Task SaveAsync(MetaTaggerState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        await _stateFileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(_directory, "meta-tagger-state.json");
            await AtomicWriteAsync(path, state, createBackup: !_recoveredFromBackup, cancellationToken).ConfigureAwait(false);
            _recoveredFromBackup = false;
        }
        finally
        {
            _stateFileGate.Release();
        }
    }

    public async Task SaveSummaryAsync(MetaTaggerRunSummary summary, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var path = Path.Combine(_directory, "last-run-summary.json");
        await AtomicWriteAsync(path, summary, createBackup: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MetaTaggerRunSummary?> LoadSummaryAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, "last-run-summary.json");
        return File.Exists(path)
            ? await ReadJsonAsync<MetaTaggerRunSummary>(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task<IReadOnlyCollection<MetaTaggerPreviewChange>> LoadPreviewChangesAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, "last-preview-changes.json");
        return File.Exists(path)
            ? await ReadJsonAsync<MetaTaggerPreviewChange[]>(path, cancellationToken).ConfigureAwait(false) ?? []
            : [];
    }

    public async Task<string> SavePreviewChangesAsync(
        IReadOnlyCollection<MetaTaggerPreviewChange> changes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var path = Path.Combine(_directory, "last-preview-changes.json");
        await AtomicWriteAsync(path, changes, createBackup: false, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static string BackupPath(string path)
    {
        return path + ".bak";
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private async Task AtomicWriteAsync<T>(
        string path,
        T value,
        bool createBackup,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (createBackup && File.Exists(path))
            {
                File.Copy(path, BackupPath(path), overwrite: true);
            }

            _filePromoter.Promote(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static MetaTaggerState Normalize(MetaTaggerState? state)
    {
        if (state is null)
        {
            throw new JsonException("The tracking ledger must contain a state object.");
        }

        state.Items = new Dictionary<string, MetaTaggerStateItem>(
            state.Items ?? [],
            StringComparer.OrdinalIgnoreCase);
        state.RunCursors = NormalizeRunCursors(state.RunCursors);
        return state;
    }

    private static Dictionary<string, MetaTaggerRunCursor> NormalizeRunCursors(
        Dictionary<string, MetaTaggerRunCursor>? runCursors)
    {
        var normalized = new Dictionary<string, MetaTaggerRunCursor>(StringComparer.Ordinal);
        foreach (var (profileKey, cursor) in runCursors ?? [])
        {
            if (string.IsNullOrWhiteSpace(profileKey) || cursor is null)
            {
                continue;
            }

            var pendingItemIds = cursor.PendingItemIds ?? [];
            var nextIndex = Math.Clamp(cursor.NextIndex, 0, pendingItemIds.Length);
            var normalizedIds = new List<string>(pendingItemIds.Length);
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalizedNextIndex = 0;
            for (var index = 0; index < pendingItemIds.Length; index++)
            {
                var itemId = pendingItemIds[index]?.Trim();
                if (string.IsNullOrEmpty(itemId) || !seenIds.Add(itemId))
                {
                    continue;
                }

                normalizedIds.Add(itemId);
                if (index < nextIndex)
                {
                    normalizedNextIndex++;
                }
            }

            cursor.PendingItemIds = normalizedIds.ToArray();
            cursor.NextIndex = Math.Min(normalizedNextIndex, cursor.PendingItemIds.Length);
            normalized[profileKey.Trim()] = cursor;
        }

        return normalized;
    }
}
