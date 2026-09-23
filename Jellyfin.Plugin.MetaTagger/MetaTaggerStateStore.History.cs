using System.Text.Json;

namespace Jellyfin.Plugin.MetaTagger;

public sealed partial class MetaTaggerStateStore
{
    private readonly SemaphoreSlim _historyGate = new(1, 1);
    private readonly HashSet<Guid> _activeRuns = [];

    public async Task SaveRunAsync(MetaTaggerRunRecord record, CancellationToken cancellationToken)
    {
        await _historyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (record.Outcome == "Running") { _activeRuns.Add(record.RunId); }
            else { _activeRuns.Remove(record.RunId); }
            var records = (await ReadHistoryIndexAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var bounded = CopyRun(record, includeItems: true);
            // Reapply bounds for records supplied by a caller or loaded from disk.
            bounded.Items = [];
            foreach (var item in record.Items) { bounded.AddItem(item); }
            await AtomicWriteAsync(RunPath(record.RunId), bounded, false, cancellationToken).ConfigureAwait(false);
            records.RemoveAll(run => run.RunId == record.RunId);
            records.Insert(0, CopyRun(bounded, includeItems: false));
            var expired = records.Skip(20).Select(run => run.RunId).ToArray();
            await AtomicWriteAsync(HistoryPath, records.Take(20).ToArray(), false, cancellationToken).ConfigureAwait(false);
            foreach (var id in expired)
            {
                File.Delete(RunPath(id));
            }
        }
        finally { _historyGate.Release(); }
    }

    public async Task<IReadOnlyList<MetaTaggerRunRecord>> LoadRunsAsync(CancellationToken cancellationToken)
    {
        await _historyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await ReadHistoryIndexAsync(cancellationToken).ConfigureAwait(false);
            foreach (var record in records) { MarkInterrupted(record); }
            return records;
        }
        finally { _historyGate.Release(); }
    }

    public async Task<MetaTaggerRunRecord?> LoadRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        await _historyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = (await ReadHistoryIndexAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(run => run.RunId == runId);
            if (entry is null) { return null; }
            try
            {
                var details = await ReadJsonAsync<MetaTaggerRunRecord>(RunPath(runId), cancellationToken).ConfigureAwait(false);
                if (details is null || details.RunId != runId) { throw new JsonException("Run identity mismatch."); }
                MarkInterrupted(details);
                return details;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                entry.DetailsAvailable = false;
                entry.Items = [];
                MarkInterrupted(entry);
                return entry;
            }
        }
        finally { _historyGate.Release(); }
    }

    private async Task<IReadOnlyList<MetaTaggerRunRecord>> ReadHistoryIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(HistoryPath))
            {
                return (await ReadJsonAsync<MetaTaggerRunRecord[]>(HistoryPath, cancellationToken).ConfigureAwait(false) ?? [])
                    .Where(run => run is not null && run.RunId != Guid.Empty).Take(20).ToArray();
            }

            // Preserve a known latest result from versions without run history.
            var summary = await LoadSummaryAsync(cancellationToken).ConfigureAwait(false);
            if (summary is null) { return []; }
            var record = new MetaTaggerRunRecord
            {
                RunId = new Guid(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(summary)).AsSpan(0, 16)),
                Operation = summary.RunMode == "ClearGeneratedTags" ? (summary.PreviewOnly ? "Cleanup preview" : "Cleanup apply") : (summary.PreviewOnly ? "Preview" : "Apply"), Invocation = "Legacy summary",
                Scope = "Scope unavailable in legacy summary",
                ConfigurationRevision = summary.ConfigurationRevision, StartedUtc = summary.LastRunUtc ?? DateTimeOffset.MinValue,
                Outcome = "Legacy summary", Summary = summary
            };
            var changes = await LoadPreviewChangesAsync(cancellationToken).ConfigureAwait(false);
            if (summary.PreviewOnly && changes.Count == summary.PreviewChangesExported
                && summary.PreviewChangesChecksum == MetaTaggerPreviewChangeChecksum.Compute(changes))
            {
                foreach (var change in changes)
                {
                    record.AddItem(new MetaTaggerRunItem
                    {
                        ItemId = change.ItemId, Name = change.ItemName ?? string.Empty, ItemType = change.ItemType ?? string.Empty,
                        Outcome = "Changes", AddedTags = change.AddedTags, RemovedTags = change.RemovedTags, PreviewRemovedTags = change.PreviewRemovedTags
                    });
                }
                record.DetailsTruncated = record.Items.Count < summary.ItemsScanned;
            }
            else { record.DetailsAvailable = false; }
            var sanitized = CopyRun(record, true);
            await AtomicWriteAsync(RunPath(record.RunId), sanitized, false, cancellationToken).ConfigureAwait(false);
            var index = new[] { CopyRun(sanitized, false) };
            await AtomicWriteAsync(HistoryPath, index, false, cancellationToken).ConfigureAwait(false);
            return index;
        }
        catch (Exception exception) when (!File.Exists(HistoryPath) && exception is FileNotFoundException)
        {
            return [];
        }
    }

    private void MarkInterrupted(MetaTaggerRunRecord record)
    {
        if (record.Outcome == "Running" && !_activeRuns.Contains(record.RunId)) { record.Outcome = "Interrupted"; }
    }

    private static MetaTaggerRunRecord CopyRun(MetaTaggerRunRecord record, bool includeItems)
    {
        var copy = JsonSerializer.Deserialize<MetaTaggerRunRecord>(JsonSerializer.SerializeToUtf8Bytes(record))!;
        copy.Summary.PreviewChangesExportPath = null;
        if (!includeItems) { copy.Items = []; }
        return copy;
    }

    private string HistoryPath => Path.Combine(_directory, "run-history.json");
    private string RunPath(Guid id) => Path.Combine(_directory, "runs", id.ToString("N") + ".json");
}
