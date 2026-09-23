using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaTagger.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaTagger;

public sealed partial class MetaTaggerRunner
{
    private readonly IMetaTaggerHost _host;
    private readonly MetadataProjectionService _metadataProjectionService;
    private readonly MetadataTagProcessor _processor;
    private readonly IMetaTaggerStateStore _stateStore;
    private readonly ILogger<MetaTaggerRunner> _logger;
    private readonly IMetaTaggerClock _clock;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private MetaTaggerCleanupPreview? _pendingCleanup;

    public MetaTaggerRunner(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        MetadataProjectionService metadataProjectionService,
        MetadataTagProcessor processor,
        MetaTaggerStateStore stateStore,
        ILogger<MetaTaggerRunner> logger,
        IMetaTaggerClock clock)
        : this(
            new JellyfinMetaTaggerHost(libraryManager, mediaSourceManager),
            metadataProjectionService,
            processor,
            stateStore,
            logger,
            clock)
    {
    }

    internal MetaTaggerRunner(
        IMetaTaggerHost host,
        MetadataProjectionService metadataProjectionService,
        MetadataTagProcessor processor,
        IMetaTaggerStateStore stateStore,
        ILogger<MetaTaggerRunner> logger,
        IMetaTaggerClock clock)
    {
        _host = host;
        _metadataProjectionService = metadataProjectionService;
        _processor = processor;
        _stateStore = stateStore;
        _logger = logger;
        _clock = clock;
    }

    public Task<MetaTaggerRunSummary> RunAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken,
        MetaTaggerRunOptions? requestedOptions = null)
    {
        return requestedOptions is null
            ? RunConfiguredDefaultAsync(progress, cancellationToken)
            : RunSerializedAsync(progress, cancellationToken, requestedOptions, RunInvocation.ExplicitOptions);
    }

    public IReadOnlyCollection<MetaTaggerCleanupItem> SearchCleanupItems(string searchTerm)
    {
        var term = searchTerm?.Trim() ?? string.Empty;
        if (term.Length < 2 || term.Length > 200)
        {
            return [];
        }

        return _host.GetItems([])
            .Where(item => item.Name?.Contains(term, StringComparison.OrdinalIgnoreCase) == true
                || item.Id.ToString("N").Equals(term.Replace("-", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id)
            .Take(50)
            .Select(item => new MetaTaggerCleanupItem
            {
                ItemId = item.Id,
                Name = item.Name,
                ItemType = item.GetType().Name,
                Path = item.Path
            })
            .ToArray();
    }

    public async Task<MetaTaggerCleanupPreview> PreviewCleanupAsync(
        Guid? itemId,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _pendingCleanup = null;
            var changes = new List<MetaTaggerPreviewChange>();
            var missingRecordedTags = new List<string>();
            var summary = await RunCoordinatedAsync(
                progress,
                cancellationToken,
                new MetaTaggerRunOptions { ClearGeneratedTags = true, CleanupItemId = itemId },
                RunInvocation.ExplicitOptions,
                changes, missingRecordedTags).ConfigureAwait(false);
            var preview = new MetaTaggerCleanupPreview
            {
                Token = changes.Count > 0 ? Guid.NewGuid().ToString("N") : null,
                ItemId = itemId,
                Summary = summary,
                Changes = changes.ToArray(),
                MissingRecordedTags = missingRecordedTags.ToArray()
            };
            _pendingCleanup = preview;
            return preview;
        }
        finally
        {
            _runGate.Release();
        }
    }

    public async Task<MetaTaggerRunSummary> ApplyCleanupAsync(
        string token,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var preview = _pendingCleanup;
            if (string.IsNullOrWhiteSpace(token)
                || preview?.Token != token
                || preview.Summary.ConfigurationRevision != _host.GetConfiguration().ConfigurationRevision)
            {
                throw new InvalidOperationException("The tag removal preview expired or settings changed. Preview tag removal again.");
            }

            _pendingCleanup = null;
            return await RunCoordinatedAsync(
                progress,
                cancellationToken,
                new MetaTaggerRunOptions
                {
                    ClearGeneratedTags = true,
                    CleanupItemId = preview.ItemId,
                    PreviewOnly = false,
                    ApprovedCleanupChanges = preview.Changes.ToDictionary(change => change.ItemId, StringComparer.OrdinalIgnoreCase)
                },
                RunInvocation.ExplicitOptions).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
        }
    }

    internal Task<MetaTaggerRunSummary> RunConfiguredDefaultAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        return RunSerializedAsync(progress, cancellationToken, requestedOptions: null, RunInvocation.ConfiguredDefault);
    }

    internal Task<MetaTaggerRunSummary> RunPostScanAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => RunSerializedAsync(progress, cancellationToken, null, RunInvocation.PostScan);

    internal (bool RunAfterLibraryScan, int MinimumMinutesBetweenAutoRuns) GetPostScanSettings()
    {
        var configuration = CloneConfiguration(_host.GetConfiguration());
        return (configuration.RunAfterLibraryScan, configuration.MinimumMinutesBetweenAutoRuns);
    }

    internal Task<MetaTaggerRunSummary> RunDefaultScheduledAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        return RunSerializedAsync(progress, cancellationToken, requestedOptions: null, RunInvocation.DefaultScheduled);
    }

    private async Task<MetaTaggerRunSummary> RunSerializedAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken,
        MetaTaggerRunOptions? requestedOptions,
        RunInvocation invocation)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _pendingCleanup = null;
            return await RunCoordinatedAsync(progress, cancellationToken, requestedOptions, invocation).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task<MetaTaggerRunSummary> RunCoordinatedAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken,
        MetaTaggerRunOptions? requestedOptions,
        RunInvocation invocation,
        List<MetaTaggerPreviewChange>? capturedChanges = null,
        List<string>? missingRecordedTags = null)
    {
        var started = _clock.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var sourceConfiguration = _host.GetConfiguration();
        var validation = PluginConfigurationValidator.Validate(sourceConfiguration);
        foreach (var error in validation.Errors)
        {
            _logger.LogWarning("Meta Tagger configuration issue: {ConfigurationIssue}", error);
        }

        var configuredActions = OneTimeActionSnapshot.Capture(sourceConfiguration);
        var selectedActions = invocation is RunInvocation.DefaultScheduled
            ? configuredActions.Selected
            : MetaTaggerOneTimeAction.None;
        var options = requestedOptions ?? ResolveOptions(sourceConfiguration, configuredActions);
        var configuration = CloneConfiguration(sourceConfiguration);
        configuration.ClaimExistingGeneratedTagsForCleanup |= invocation is not RunInvocation.ExplicitOptions
            && configuredActions.Claim;
        if (options.ClearGeneratedTags)
        {
            configuration.StaleTagMode = StaleTagMode.Remove;
            configuration.ClaimExistingGeneratedTagsForCleanup = false;
        }

        var budget = new MetaTaggerRunBudget(configuration, started);
        var previewChanges = new List<MetaTaggerPreviewChange>();

        var summary = new MetaTaggerRunSummary
        {
            ConfigurationRevision = sourceConfiguration.ConfigurationRevision,
            LastRunUtc = started,
            Invocation = options.Invocation ?? invocation.ToString(),
            RunMode = options.ClearGeneratedTags ? "ClearGeneratedTags" : options.RunMode.ToString(),
            PreviewOnly = options.PreviewOnly
        };

        var record = CreateRunRecord(summary,
            options.ClearGeneratedTags ? (options.PreviewOnly ? "Cleanup preview" : "Cleanup apply") : (options.PreviewOnly ? "Preview" : "Apply"),
            options.CleanupItemId?.ToString("N") ?? (options.ClearGeneratedTags ? "Entire library" : "Configured item types across all libraries"),
            summary.Invocation, options.ClearGeneratedTags ? [] : GetIncludedItemTypes(configuration).Select(type => type.ToString()).ToArray());
        MetaTaggerRunCursor? activeCursor = null;
        await PublishHistoryAsync(record).ConfigureAwait(false);
        try
        {
            if (!configuration.IsEnabled && !options.ClearGeneratedTags)
            {
                summary.Outcome = "Disabled";
                _logger.LogInformation("Meta Tagger is disabled; skipping run.");
                return summary;
            }

            var includedItemTypes = options.ClearGeneratedTags ? [] : GetIncludedItemTypes(configuration);
            if (includedItemTypes.Length == 0 && !options.ClearGeneratedTags)
            {
                summary.Outcome = "No item types selected";
                _logger.LogInformation("Meta Tagger has no enabled item types; skipping run.");
                return summary;
            }

            var loadedState = await LoadStateAsync(configuration, cancellationToken).ConfigureAwait(false);
            var state = loadedState.State;
            var cycleDegraded = loadedState.ForcePreviewOnly;
            if (loadedState.ForcePreviewOnly)
            {
                options = WithPreviewOnly(options);
                summary.PreviewOnly = true;
                summary.Outcome = "Preview fallback";
            }

            if (!options.PreviewOnly && configuration.StaleTagMode == StaleTagMode.Remove)
            {
                var ledgerPreparation = await EnsureLedgerWritableAsync(
                        state,
                        configuration,
                        options,
                        cancellationToken)
                    .ConfigureAwait(false);
                options = ledgerPreparation.Options;
                cycleDegraded |= ledgerPreparation.ForcePreviewOnly;
                summary.PreviewOnly = options.PreviewOnly;
                if (ledgerPreparation.ForcePreviewOnly) { summary.Outcome = "Preview fallback"; }
            }

            var items = _host.GetItems(includedItemTypes);
            var scopedItems = options.CleanupItemId is { } cleanupItemId
                ? items.Where(item => item.Id == cleanupItemId)
                : items;
            if (options.ClearGeneratedTags)
            {
                scopedItems = scopedItems.Where(item => state.Items.TryGetValue(item.Id.ToString("N"), out var entry)
                    && entry?.LastAppliedTags?.Length > 0);
            }

            if (options.ApprovedCleanupChanges is { } approved)
            {
                scopedItems = scopedItems.Where(item => approved.ContainsKey(item.Id.ToString("N")));
            }

            var itemsById = ItemsById(scopedItems);
            var scopedItemIds = itemsById.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var runProfileKey = CreateRunProfileKey(configuration, options, includedItemTypes);
            if (options.ApprovedCleanupChanges is not null)
            {
                // A fresh approval covers its entire preview, even after an earlier cleanup stopped.
                state.RunCursors.Remove(runProfileKey);
            }

            var cursorAlreadyExisted = state.RunCursors.TryGetValue(runProfileKey, out var cursor);
            if (!cursorAlreadyExisted)
            {
                cursor = new MetaTaggerRunCursor
                {
                    PendingItemIds = itemsById.Keys
                        .Order(StringComparer.Ordinal)
                        .ToArray()
                };
                state.RunCursors[runProfileKey] = cursor;
                await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var pendingItemIds = cursor!.PendingItemIds ?? [];
                var nextIndex = Math.Clamp(cursor.NextIndex, 0, pendingItemIds.Length);
                cursor.PendingItemIds = pendingItemIds
                    .Skip(nextIndex)
                    .Where(scopedItemIds.Contains)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                cursor.NextIndex = 0;
            }

            activeCursor = cursor;
            var cycleSize = cursor!.PendingItemIds.Length;
            while (cursor.NextIndex < cursor.PendingItemIds.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (budget.ShouldStopBeforeItem(summary, _clock.UtcNow, out var budgetReason))
                {
                    MarkBudgetLimitReached(summary, budgetReason, ItemsRemaining(cursor));
                    break;
                }

                var itemId = cursor.PendingItemIds[cursor.NextIndex];
                var item = itemsById[itemId];
                summary.ItemsScanned++;
                try
                {
                    var result = await ProcessItemAsync(
                            item,
                            configuration,
                            state,
                            cursor,
                            options,
                            budget,
                            started,
                            summary,
                            record,
                            cancellationToken,
                            missingRecordedTags)
                        .ConfigureAwait(false);
                    if (result.IsTerminal && !result.CursorAdvancePersisted)
                    {
                        AdvanceCursor(cursor, itemId);
                    }

                    if (result.PreviewChange is not null)
                    {
                        previewChanges.Add(result.PreviewChange);
                    }

                    if (!result.IsTerminal)
                    {
                        break;
                    }
                }
                finally
                {
                    if (cycleSize > 0)
                    {
                        progress.Report(cursor.NextIndex * 100d / cycleSize);
                    }
                }
            }

            summary.ItemsRemaining = ItemsRemaining(cursor);
            if (summary.ItemsRemaining == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ShouldPruneLedger(options))
                {
                    summary.LedgerEntriesPruned = state.PruneMissingItems(
                        scopedItemIds,
                        includedItemTypes.Select(itemType => itemType.ToString()));
                }

                state.RunCursors.Remove(runProfileKey);
            }

            if (options.ApprovedCleanupChanges is not null)
            {
                // The consumed approval cannot resume. Remaining work needs another preview.
                state.RunCursors.Remove(runProfileKey);
            }

            if (options.PreviewOnly && previewChanges.Count > 0)
            {
                summary.PreviewChangesChecksum = MetaTaggerPreviewChangeChecksum.Compute(previewChanges);
                summary.PreviewChangesExportPath = await _stateStore
                    .SavePreviewChangesAsync(previewChanges, cancellationToken)
                    .ConfigureAwait(false);
                summary.PreviewChangesExported = previewChanges.Count;
            }

            await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            summary.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

            CompleteOutcome(summary);
            await _stateStore.SaveSummaryAsync(summary, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            capturedChanges?.AddRange(previewChanges);
            var acknowledgedActions = IsCompletedCleanCycle(summary, cycleDegraded)
                ? selectedActions
                : MetaTaggerOneTimeAction.None;
            _host.PublishRunConfiguration(new MetaTaggerRunConfigurationPublication(
                sourceConfiguration,
                MetaTaggerRunSummaryFormatter.Format(summary),
                acknowledgedActions));

            _logger.LogInformation(
                "Meta Tagger completed: run {RunMode}, scanned {ItemsScanned}, skipped unchanged {ItemsSkippedUnchanged}, skipped manual {ItemsSkippedManual}, skipped locked {ItemsSkippedLocked}, changed {ItemsChanged}, added {TagsAdded}, removed {TagsRemoved}, writes {WritesApplied}, estimated writes {EstimatedWrites}, previewed stale removals {StaleTagsPreviewed}, legacy kept {LegacyTagsKept}, legacy claimed {LegacyTagsClaimed}, pruned ledger entries {LedgerEntriesPruned}, failures {Failures}, budget reached {BudgetLimitReached}, budget reason {BudgetLimitReason}, preview only {PreviewOnly}, elapsed {ElapsedMilliseconds} ms.",
                summary.RunMode,
                summary.ItemsScanned,
                summary.ItemsSkippedUnchanged,
                summary.ItemsSkippedManual,
                summary.ItemsSkippedLocked,
                summary.ItemsChanged,
                summary.TagsAdded,
                summary.TagsRemoved,
                summary.WritesApplied,
                summary.EstimatedWrites,
                summary.StaleTagsPreviewed,
                summary.LegacyTagsKept,
                summary.LegacyTagsClaimed,
                summary.LedgerEntriesPruned,
                summary.Failures,
                summary.BudgetLimitReached,
                summary.BudgetLimitReason,
                summary.PreviewOnly,
                summary.ElapsedMilliseconds);

            return summary;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (summary.Outcome != "Uncertain") { summary.Outcome = "Cancelled"; }
            throw;
        }
        catch
        {
            if (summary.Outcome != "Uncertain") { summary.Outcome = "Failed"; }
            throw;
        }
        finally
        {
            stopwatch.Stop();
            summary.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            if (activeCursor is not null) { summary.ItemsRemaining = ItemsRemaining(activeCursor); }
            await PublishHistoryAsync(record, finished: true).ConfigureAwait(false);
        }
    }

    private async Task<ItemProcessingResult> ProcessItemAsync(
        BaseItem item,
        PluginConfiguration configuration,
        MetaTaggerState state,
        MetaTaggerRunCursor cursor,
        MetaTaggerRunOptions options,
        MetaTaggerRunBudget budget,
        DateTimeOffset started,
        MetaTaggerRunSummary summary,
        MetaTaggerRunRecord record,
        CancellationToken cancellationToken,
        List<string>? missingRecordedTags)
    {
        void RecordItem(string outcome, string? reason = null, TagMergeResult? merge = null)
        {
            record.AddItem(new MetaTaggerRunItem
            {
                ItemId = item.Id.ToString("N"), Name = item.Name, ItemType = item.GetType().Name, Outcome = outcome, Reason = reason,
                AddedTags = merge?.AddedTags ?? [], RemovedTags = merge?.RemovedTags ?? [], PreviewRemovedTags = merge?.PreviewRemovedTags ?? []
            });
        }

        var checkpointPending = false;
        var cursorAdvancePersisted = false;

        try
        {
            item = _host.GetItem(item.Id)
                ?? throw new InvalidOperationException("The queued item is no longer available.");
            if (item.IsLocked || item.LockedFields?.Contains(MetadataField.Tags) == true)
            {
                summary.ItemsSkippedLocked++;
                RecordItem("Protected", item.IsLocked ? "Jellyfin metadata lock protects this item." : "Jellyfin metadata lock on Tags protects this item.");
                return ItemProcessingResult.Completed();
            }

            if ((item.Tags ?? []).Any(tag => string.Equals(tag, TagFormat.ManualControlTag(configuration, "skip"), StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, TagFormat.ManualControlTag(configuration, "lock"), StringComparison.OrdinalIgnoreCase)))
            {
                summary.ItemsSkippedManual++;
                RecordItem("Protected", "Skipped because this item has a manual skip tag or its older lock alias.");
                return ItemProcessingResult.Completed();
            }

            var input = options.ClearGeneratedTags
                ? new MetadataTagInput
                {
                    ItemId = item.Id.ToString("N"),
                    ItemPath = item.Path,
                    ItemType = item.GetType().Name,
                    ExistingTags = item.Tags ?? []
                }
                : ProjectMetadata(item, configuration, state, cancellationToken);
            // Capture only the selected item's current comparison before refreshing ownership.
            if (options.ClearGeneratedTags && options.PreviewOnly && options.CleanupItemId == item.Id
                && state.Items.TryGetValue(input.ItemId, out var previous))
            {
                missingRecordedTags?.AddRange(previous.LastAppliedTags.Except(input.ExistingTags, StringComparer.OrdinalIgnoreCase));
            }
            var result = _processor.Process(input, configuration, state, options, started);
            if (result.SkipReason is MetadataTagSkipReason.Unchanged)
            {
                summary.ItemsSkippedUnchanged++;
                RecordItem("Up to date");
                return ItemProcessingResult.Completed();
            }

            if (result.SkipReason is MetadataTagSkipReason.ManualSkip)
            {
                summary.ItemsSkippedManual++;
                RecordItem("Protected", "Skipped because this item has a manual skip tag or its older lock alias.");
                return ItemProcessingResult.Completed();
            }

            var merge = result.Merge;
            AddCounts(summary, merge, configuration);

            if (merge.HasChangesToApply || merge.PreviewRemovedTags.Count > 0)
            {
                summary.ItemsChanged++;
                if (options.PreviewOnly && merge.HasChangesToApply)
                {
                    summary.EstimatedWrites++;
                }

                LogItemChange(configuration, options, item, merge);

                if (result.ShouldWriteTags)
                {
                    if (!budget.CanWrite(summary, out var budgetReason))
                    {
                        MarkBudgetLimitReached(summary, budgetReason, ItemsRemaining(cursor));
                        RecordItem("Not checked", "Item update limit reached.");
                        return ItemProcessingResult.Deferred();
                    }

                    await _host
                        .UpdateItemTagsAsync(item, merge.FinalTags, cancellationToken)
                        .ConfigureAwait(false);
                    checkpointPending = true;
                    var ledgerEntry = result.LedgerEntry
                        ?? throw new InvalidOperationException(
                            "A successful item write must have a corresponding ledger entry.");
                    state.Items[input.ItemId] = ledgerEntry;
                    AdvanceCursor(cursor, input.ItemId);
                    await _stateStore.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
                    checkpointPending = false;
                    cursorAdvancePersisted = true;
                    summary.WritesApplied++;
                    RecordItem("Applied", merge: merge);
                    await _clock.DelayAsync(budget.WriteDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            if (!result.ShouldWriteTags && result.LedgerEntry is not null)
            {
                state.Items[input.ItemId] = result.LedgerEntry;
            }

            if (merge.PreviewRemovedTags.Count > 0)
            {
                LogPreviewRemovals(configuration, item, merge);
            }

            if (!result.ShouldWriteTags) { RecordItem(merge.HasChangesToApply || merge.PreviewRemovedTags.Count > 0 ? "Changes" : "Up to date", merge: merge); }
            var previewChange = options.PreviewOnly
                && (merge.HasChangesToApply || merge.PreviewRemovedTags.Count > 0)
                    ? CreatePreviewChange(item, merge)
                    : null;
            return ItemProcessingResult.Completed(cursorAdvancePersisted, previewChange);
        }
        catch (Exception) when (checkpointPending)
        {
            summary.Outcome = "Uncertain";
            RecordItem("Uncertain", "Tags were saved, but the plugin could not save its tag records. Check the item and the Jellyfin server log before previewing again.");
            throw;
        }
        catch (Exception exception) when (!checkpointPending
            && (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            summary.Failures++;
            RecordItem("Failed", "Item processing failed. Check the server log.");
            if (configuration.QuietLogging) { _logger.LogError(exception, "Meta Tagger failed for item {ItemId}.", item.Id); }
            else { _logger.LogError(exception, "Meta Tagger failed for item {ItemName} ({ItemId}).", item.Name, item.Id); }
            return ItemProcessingResult.Completed(cursorAdvancePersisted);
        }
    }

    private static Dictionary<string, BaseItem> ItemsById(IEnumerable<BaseItem> items)
    {
        var itemsById = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            itemsById.TryAdd(item.Id.ToString("N"), item);
        }

        return itemsById;
    }

    private static void AdvanceCursor(MetaTaggerRunCursor cursor, string itemId)
    {
        if (cursor.NextIndex >= cursor.PendingItemIds.Length
            || !string.Equals(
                cursor.PendingItemIds[cursor.NextIndex],
                itemId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The run cursor can advance only its current pending item.");
        }

        cursor.NextIndex++;
    }

    private static int ItemsRemaining(MetaTaggerRunCursor cursor)
    {
        return Math.Max(0, cursor.PendingItemIds.Length - cursor.NextIndex);
    }

    private static string CreateRunProfileKey(
        PluginConfiguration configuration,
        MetaTaggerRunOptions options,
        IEnumerable<BaseItemKind> includedItemTypes)
    {
        var canonical = new StringBuilder();
        AppendProfileValue(canonical, "1");
        AppendProfileValue(canonical, Plugin.PluginVersion);
        AppendProfileValue(canonical, ((int)options.RunMode).ToString(CultureInfo.InvariantCulture));
        AppendProfileValue(canonical, options.PreviewOnly ? "1" : "0");
        AppendProfileValue(canonical, options.Force ? "1" : "0");
        if (options.ClearGeneratedTags)
        {
            AppendProfileValue(canonical, "cleanup");
            AppendProfileValue(canonical, options.CleanupItemId?.ToString("N") ?? "all");
            foreach (var itemId in (options.ApprovedCleanupChanges?.Keys ?? []).Order(StringComparer.Ordinal))
            {
                AppendProfileValue(canonical, itemId);
            }
        }

        var canonicalItemTypes = includedItemTypes
            .OrderBy(itemType => (int)itemType)
            .ToArray();
        AppendProfileValue(canonical, canonicalItemTypes.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var itemType in canonicalItemTypes)
        {
            AppendProfileValue(canonical, ((int)itemType).ToString(CultureInfo.InvariantCulture));
        }

        AppendProfileValue(canonical, configuration.GeneratedTagPrefix);
        AppendProfileValue(canonical, configuration.TagSeparator);
        AppendProfileValue(canonical, configuration.ManualTagPrefix);
        AppendProfileValue(canonical, configuration.EnableGenres ? "1" : "0");
        AppendProfileValue(canonical, configuration.EnableParentalRating ? "1" : "0");
        AppendProfileValue(canonical, configuration.EnableExistingTagsAsKeywords ? "1" : "0");
        if (configuration.EnableExistingTagsAsKeywords)
        {
            AppendProfileValue(canonical, configuration.MaxKeywordTagsPerItem.ToString(CultureInfo.InvariantCulture));
            var canonicalExcludedPrefixes = PluginConfigurationValidator.GetExcludedKeywordPrefixes(configuration)
                .Select(prefix => prefix.ToLowerInvariant())
                .Order(StringComparer.Ordinal)
                .ToArray();
            AppendProfileValue(canonical, canonicalExcludedPrefixes.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var excludedPrefix in canonicalExcludedPrefixes)
            {
                AppendProfileValue(canonical, excludedPrefix);
            }
        }

        AppendProfileValue(canonical, configuration.EnableStudios ? "1" : "0");
        AppendProfileValue(canonical, configuration.EnableProductionCountries ? "1" : "0");
        AppendProfileValue(canonical, configuration.EnableProviderIds ? "1" : "0");
        AppendProfileValue(canonical, configuration.EnableProductionYear ? "1" : "0");
        if (configuration.EnableAudioLanguages || configuration.EnableSubtitleLanguages)
        {
            AppendProfileValue(canonical, "track-languages");
            AppendProfileValue(canonical, configuration.EnableAudioLanguages ? "1" : "0");
            AppendProfileValue(canonical, configuration.EnableSubtitleLanguages ? "1" : "0");
        }
        AppendProfileValue(canonical, configuration.IncludeParentSeriesMetadataOnEpisodes ? "1" : "0");
        AppendProfileValue(canonical, configuration.ClaimExistingGeneratedTagsForCleanup ? "1" : "0");
        AppendProfileValue(canonical, ((int)configuration.StaleTagMode).ToString(CultureInfo.InvariantCulture));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return "v1-" + Convert.ToHexString(hash).ToLower(CultureInfo.InvariantCulture);
    }

    private static void AppendProfileValue(StringBuilder builder, string value)
    {
        builder
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append(';');
    }

    private sealed record ItemProcessingResult(
        bool IsTerminal,
        bool CursorAdvancePersisted,
        MetaTaggerPreviewChange? PreviewChange)
    {
        public static ItemProcessingResult Completed(
            bool cursorAdvancePersisted = false,
            MetaTaggerPreviewChange? previewChange = null)
        {
            return new ItemProcessingResult(true, cursorAdvancePersisted, previewChange);
        }

        public static ItemProcessingResult Deferred()
        {
            return new ItemProcessingResult(false, false, null);
        }
    }

    private static void AddCounts(MetaTaggerRunSummary summary, TagMergeResult merge, PluginConfiguration configuration)
    {
        summary.TagsAdded += merge.AddedTags.Count;
        summary.TagsRemoved += merge.RemovedTags.Count;
        summary.StaleTagsPreviewed += merge.PreviewRemovedTags.Count;
        summary.LegacyTagsKept += merge.LegacyTagsKept.Count;
        summary.LegacyTagsClaimed += merge.LegacyTagsClaimed.Count;

        foreach (var tag in merge.AddedTags)
        {
            var source = TagFormat.TryGetGeneratedSource(tag, configuration);
            if (source is null)
            {
                continue;
            }

            summary.TagsAddedBySource[source] = summary.TagsAddedBySource.TryGetValue(source, out var count)
                ? count + 1
                : 1;
        }
    }

    private async Task<(MetaTaggerState State, bool ForcePreviewOnly)> LoadStateAsync(
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            return (state, false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Meta Tagger could not read its tracking ledger; forcing preview-safe cleanup.");
            configuration.StaleTagMode = configuration.StaleTagMode == StaleTagMode.Remove
                ? StaleTagMode.Preview
                : configuration.StaleTagMode;
            return (new MetaTaggerState(), true);
        }
    }

    private async Task<(MetaTaggerRunOptions Options, bool ForcePreviewOnly)> EnsureLedgerWritableAsync(
        MetaTaggerState state,
        PluginConfiguration configuration,
        MetaTaggerRunOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return (options, false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Meta Tagger could not write its tracking ledger; forcing preview-only cleanup.");
            configuration.StaleTagMode = StaleTagMode.Preview;
            return (WithPreviewOnly(options), true);
        }
    }

    private static MetaTaggerRunOptions ResolveOptions(
        PluginConfiguration configuration,
        OneTimeActionSnapshot configuredActions)
    {
        return new MetaTaggerRunOptions
        {
            RunMode = configuredActions.Rebuild
                ? MetadataTagRunMode.RebuildTrackingLedger
                : configuredActions.Force
                    ? MetadataTagRunMode.FullScan
                    : configuration.DefaultRunMode,
            PreviewOnly = configuration.PreviewOnly,
            Force = configuredActions.Force
        };
    }

    private static bool IsCompletedCleanCycle(
        MetaTaggerRunSummary summary,
        bool cycleDegraded)
    {
        return summary.ItemsRemaining == 0
            && !summary.BudgetLimitReached
            && summary.Failures == 0
            && !cycleDegraded;
    }

    private static MetaTaggerRunOptions WithPreviewOnly(MetaTaggerRunOptions options)
    {
        return new MetaTaggerRunOptions
        {
            RunMode = options.RunMode,
            PreviewOnly = true,
            Force = options.Force,
            ClearGeneratedTags = options.ClearGeneratedTags,
            CleanupItemId = options.CleanupItemId,
            ApprovedCleanupChanges = options.ApprovedCleanupChanges
        };
    }

    private static bool ShouldPruneLedger(MetaTaggerRunOptions options)
    {
        return !options.ClearGeneratedTags
            && options.RunMode is MetadataTagRunMode.FullScan or MetadataTagRunMode.RebuildTrackingLedger;
    }

    private static void MarkBudgetLimitReached(MetaTaggerRunSummary summary, string reason, int itemsRemaining)
    {
        summary.BudgetLimitReached = true;
        summary.BudgetLimitReason = reason;
        summary.ItemsRemaining = Math.Max(0, itemsRemaining);
    }

    private void LogItemChange(
        PluginConfiguration configuration,
        MetaTaggerRunOptions options,
        BaseItem item,
        TagMergeResult merge)
    {
        if (configuration.QuietLogging)
        {
            _logger.LogInformation(
                "Meta Tagger {Mode} item {ItemId}: +{AddedCount} -{RemovedCount}",
                options.PreviewOnly ? "would update" : "updating",
                item.Id,
                merge.AddedTags.Count,
                merge.RemovedTags.Count);
            return;
        }

        _logger.LogInformation(
            "Meta Tagger {Mode} item {ItemName} ({ItemId}): +{AddedCount} -{RemovedCount}",
            options.PreviewOnly ? "would update" : "updating",
            item.Name,
            item.Id,
            merge.AddedTags.Count,
            merge.RemovedTags.Count);
    }

    private void LogPreviewRemovals(PluginConfiguration configuration, BaseItem item, TagMergeResult merge)
    {
        if (configuration.QuietLogging)
        {
            _logger.LogInformation(
                "Meta Tagger preview stale removals for item {ItemId}: {Count} tags",
                item.Id,
                merge.PreviewRemovedTags.Count);
            return;
        }

        _logger.LogInformation(
            "Meta Tagger preview stale removals for {ItemName} ({ItemId}): {Tags}",
            item.Name,
            item.Id,
            string.Join(", ", merge.PreviewRemovedTags));
    }

    private static MetaTaggerPreviewChange CreatePreviewChange(BaseItem item, TagMergeResult merge)
    {
        return new MetaTaggerPreviewChange
        {
            ItemId = item.Id.ToString("N"),
            ItemName = item.Name,
            ItemPath = item.Path,
            ItemType = item.GetType().Name,
            AddedTags = merge.AddedTags,
            RemovedTags = merge.RemovedTags,
            PreviewRemovedTags = merge.PreviewRemovedTags
        };
    }

    private static PluginConfiguration CloneConfiguration(PluginConfiguration configuration)
    {
        return PluginConfigurationValidator.Sanitize(configuration);
    }

    private MetadataTagInput ProjectMetadata(BaseItem item, PluginConfiguration configuration, MetaTaggerState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var streams = configuration.EnableAudioLanguages || configuration.EnableSubtitleLanguages
            ? _host.GetMediaStreams(item.Id)
            : [];
        cancellationToken.ThrowIfCancellationRequested();
        return _metadataProjectionService.Project(item, configuration.IncludeParentSeriesMetadataOnEpisodes, state.Items, streams);
    }

    private static BaseItemKind[] GetIncludedItemTypes(PluginConfiguration configuration)
    {
        var itemTypes = new List<BaseItemKind>();

        if (configuration.IncludeMovies)
        {
            itemTypes.Add(BaseItemKind.Movie);
        }

        if (configuration.IncludeSeries)
        {
            itemTypes.Add(BaseItemKind.Series);
        }

        if (configuration.IncludeEpisodes)
        {
            itemTypes.Add(BaseItemKind.Episode);
        }

        if (configuration.IncludeVideos)
        {
            itemTypes.Add(BaseItemKind.Video);
        }

        return itemTypes.ToArray();
    }

    private enum RunInvocation
    {
        ConfiguredDefault,
        ExplicitOptions,
        DefaultScheduled,
        PostScan
    }

    private readonly record struct OneTimeActionSnapshot(
        bool Force,
        bool Rebuild,
        bool Claim)
    {
        public MetaTaggerOneTimeAction Selected
        {
            get
            {
                var selected = Rebuild
                    ? MetaTaggerOneTimeAction.Rebuild
                    : Force
                        ? MetaTaggerOneTimeAction.Force
                        : MetaTaggerOneTimeAction.None;
                return Claim ? selected | MetaTaggerOneTimeAction.Claim : selected;
            }
        }

        public static OneTimeActionSnapshot Capture(PluginConfiguration configuration)
        {
            return new OneTimeActionSnapshot(
                configuration.ForceFullScanOnNextRun,
                configuration.RebuildTrackingLedgerOnNextRun,
                configuration.ClaimExistingGeneratedTagsOnNextRun);
        }
    }
}
