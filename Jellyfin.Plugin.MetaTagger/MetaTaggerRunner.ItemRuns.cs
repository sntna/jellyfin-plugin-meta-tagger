using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaTagger;

public sealed partial class MetaTaggerRunner
{
    private readonly object _itemRunsLock = new();
    private readonly Dictionary<Guid, ItemRun> _itemRuns = [];
    private readonly Queue<Guid> _itemRunOrder = new();
    private ItemRun? _currentItemRun;
    private const int RetainedItemRuns = 20;

    public MetaTaggerItemRun StartItemApply(Guid itemId)
    {
        lock (_itemRunsLock)
        {
            if (_currentItemRun is { Finished: false })
            {
                throw new InvalidOperationException("An item Apply is already queued or running. Wait for it or stop it before starting another.");
            }
            while (_itemRuns.Count >= RetainedItemRuns) { _itemRuns.Remove(_itemRunOrder.Dequeue()); }
            var run = new ItemRun(itemId, CreateItemSummary());
            _itemRuns.Add(run.Summary.RunId, run);
            _itemRunOrder.Enqueue(run.Summary.RunId);
            _currentItemRun = run;
            _ = ExecuteItemRunAsync(run);
            return Snapshot(run);
        }
    }

    public MetaTaggerItemRun? GetCurrentItemRun()
    {
        lock (_itemRunsLock) { return _currentItemRun is { } run ? Snapshot(run) : null; }
    }

    public MetaTaggerItemRun? GetItemRun(Guid runId)
    {
        lock (_itemRunsLock) { return _itemRuns.TryGetValue(runId, out var run) ? Snapshot(run) : null; }
    }

    public bool CancelItemRun(Guid runId)
    {
        lock (_itemRunsLock)
        {
            if (!_itemRuns.TryGetValue(runId, out var run) || run.Finished) { return false; }
            run.State = "Cancelling";
            run.Cancellation.Cancel();
            return true;
        }
    }

    private async Task ExecuteItemRunAsync(ItemRun run)
    {
        // Return the run ID before waiting for the shared coordinator or accessing Jellyfin.
        await Task.Yield();
        try
        {
            await ApplyItemCoreAsync(run.ItemId, run.Summary, run.Cancellation.Token, () =>
            {
                lock (_itemRunsLock)
                {
                    if (!run.Cancellation.IsCancellationRequested) { run.State = "Running"; }
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
            var wasQueued = run.Summary.Outcome == "Unknown";
            var record = wasQueued ? CreateRunRecord(run.Summary, "Apply", run.ItemId.ToString("N")) : null;
            run.Summary.Outcome = "Cancelled";
            if (record is not null) { await PublishHistoryAsync(record, finished: true).ConfigureAwait(false); }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Item generation run {RunId} failed", run.Summary.RunId);
            if (run.Summary.Outcome != "Uncertain") { run.Summary.Outcome = "Failed"; }
            run.Error = exception is InvalidOperationException ? exception.Message
                : "The item run failed. Check its history and the Jellyfin server log before retrying.";
        }
        finally
        {
            lock (_itemRunsLock)
            {
                run.State = run.Summary.Outcome;
                run.Finished = true;
                run.Cancellation.Dispose();
            }
        }
    }

    private MetaTaggerRunSummary CreateItemSummary() => new()
    {
        LastRunUtc = _clock.UtcNow, RunMode = "ItemGeneration", PreviewOnly = false, ItemsRemaining = 1
    };

    private static MetaTaggerItemRun Snapshot(ItemRun run) => new()
    {
        RunId = run.Summary.RunId, ItemId = run.ItemId, State = run.State,
        Summary = run.Finished ? run.Summary : null, Error = run.Finished ? run.Error : null
    };

    private sealed class ItemRun(Guid itemId, MetaTaggerRunSummary summary)
    {
        internal Guid ItemId { get; } = itemId;
        internal MetaTaggerRunSummary Summary { get; } = summary;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal string State { get; set; } = "Queued";
        internal bool Finished { get; set; }
        internal string? Error { get; set; }
    }
}

public sealed class MetaTaggerItemRun
{
    public Guid RunId { get; init; }
    public Guid ItemId { get; init; }
    public string State { get; init; } = "Queued";
    public MetaTaggerRunSummary? Summary { get; init; }
    public string? Error { get; init; }
}
