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
    private readonly ItemEvaluation _itemEvaluation;
    private readonly ItemExecution _itemExecution;
    private readonly IMetaTaggerStateStore _stateStore;
    private readonly ILogger<MetaTaggerRunner> _logger;
    private readonly IMetaTaggerClock _clock;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    // Dashboard checks report a busy runner instead of queueing behind a library run.
    internal const string RunInProgressOutcome = "Run in progress";
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
        _itemEvaluation = new ItemEvaluation(host, metadataProjectionService, processor, stateStore, logger, clock);
        _itemExecution = new ItemExecution(host, processor, stateStore, clock, logger, _itemEvaluation);
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
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new MetaTaggerCleanupPreview { ItemId = itemId, Summary = RunInProgressSummary(previewOnly: true) };
        }
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
        // The reviewed preview stays valid; nothing was removed while another run held the gate.
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) { return RunInProgressSummary(previewOnly: false); }
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

    private MetaTaggerRunSummary RunInProgressSummary(bool previewOnly) => new()
    {
        LastRunUtc = _clock.UtcNow, PreviewOnly = previewOnly, RunMode = "ClearGeneratedTags", Outcome = RunInProgressOutcome
    };

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

    internal Task<MetaTaggerRunSummary> RunTaskAsync(IProgress<double> progress, CancellationToken cancellationToken,
        MetaTaggerRunOptions options, bool authorizeBackfill)
        => RunSerializedAsync(progress, cancellationToken, options,
            authorizeBackfill ? RunInvocation.ApplyTask : RunInvocation.ConfiguredTask);

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
        var uncertainInstallation = configuration.Installation?.Origin == InstallationOrigin.Uncertain;
        if (uncertainInstallation && !options.ClearGeneratedTags)
        {
            options = WithPreviewOnly(options);
        }
        configuration.ClaimExistingGeneratedTagsForCleanup |= invocation is RunInvocation.ConfiguredDefault or RunInvocation.DefaultScheduled or RunInvocation.PostScan
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
        if (!options.ClearGeneratedTags) { DescribeGenerationState(summary, configuration.Installation); }

        var record = CreateRunRecord(summary,
            options.ClearGeneratedTags ? (options.PreviewOnly ? "Cleanup preview" : "Cleanup apply") : (options.PreviewOnly ? "Preview" : "Apply"),
            options.CleanupItemId?.ToString("N") ?? (options.ClearGeneratedTags ? "Entire library" : "Configured item types across all libraries"),
            summary.Invocation, options.ClearGeneratedTags ? [] : GetIncludedItemTypes(configuration).Select(type => type.ToString()).ToArray(), configuration);
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

            if (!uncertainInstallation && IsBulkBackfillInvocation(options, invocation))
            {
                AuthorizeGenerationScope(configuration, includedItemTypes.Select(type => type.ToString()).ToArray(), null,
                    summary, cancellationToken);
            }

            var loadedState = await LoadStateAsync(configuration, cancellationToken).ConfigureAwait(false);
            var state = loadedState.State;
            // An unreadable ledger stays untouched so its records can be recovered; this run only previews.
            var ledgerUnreadable = loadedState.ForcePreviewOnly;
            var cycleDegraded = loadedState.ForcePreviewOnly || uncertainInstallation;
            if (uncertainInstallation && !options.ClearGeneratedTags) { summary.Outcome = "Preview fallback"; }
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

            if (!options.ClearGeneratedTags && !uncertainInstallation)
            {
                await CaptureGenerationBaselineAsync(configuration, budget, summary, cancellationToken).ConfigureAwait(false);
                if (summary.BudgetLimitReached) { return summary; }
            }

            var skipsGeneration = options.ClearGeneratedTags || options.RunMode == MetadataTagRunMode.RebuildTrackingLedger;
            string[]? baselineItemIds = skipsGeneration
                ? []
                : await LoadBaselineItemIdsAsync(_stateStore, configuration.Installation?.Generation, cancellationToken)
                    .ConfigureAwait(false);
            var generationSnapshot = skipsGeneration
                ? null : new GenerationEligibilitySnapshot(configuration.Installation, invocation, baselineItemIds);
            // The summary described the recorded baseline before its item IDs were read, so a
            // baseline this run cannot verify is still reported as one it can act on.
            if (generationSnapshot is not null && baselineItemIds is null) { DescribeUnverifiableBaseline(summary); }
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
            var runProfileKey = CreateRunProfileKey(configuration, options, includedItemTypes, invocation);
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
                if (!ledgerUnreadable) { await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false); }
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
            summary.ItemsRemaining = cycleSize;
            var itemContext = new LibraryItemContext(configuration, state, cursor, options, generationSnapshot, budget, started, summary);
            var historyPublished = _clock.UtcNow;
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
                    var result = await _itemExecution.ExecuteAsync(
                        new ItemWork.LibraryItem(item, itemContext, missingRecordedTags), cancellationToken).ConfigureAwait(false);
                    record.AddItem(result.Detail);
                    if (result.PreviewChange is not null) { previewChanges.Add(result.PreviewChange); }
                    if (result.Status == ItemExecutionStatus.Deferred) { break; }
                    if (result.Status is ItemExecutionStatus.Uncertain or ItemExecutionStatus.Cancelled) { result.Error!.Throw(); }
                    if (result.WriteDelay is { } delay)
                    {
                        // Show the write before a configured pause.
                        historyPublished = await PublishHistoryProgressAsync(record, historyPublished, force: delay > TimeSpan.Zero)
                            .ConfigureAwait(false);
                        await _clock.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    summary.ItemsRemaining = ItemsRemaining(cursor);
                    historyPublished = await PublishHistoryProgressAsync(record, historyPublished).ConfigureAwait(false);
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

            if (!ledgerUnreadable) { await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false); }
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
            record.FailureReason = "The run did not complete. Inspect item outcomes and the Jellyfin server log before retrying.";
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
        IEnumerable<BaseItemKind> includedItemTypes,
        RunInvocation invocation)
    {
        var canonical = new StringBuilder();
        AppendProfileValue(canonical, "1");
        AppendProfileValue(canonical, Plugin.PluginVersion);
        AppendProfileValue(canonical, ((int)options.RunMode).ToString(CultureInfo.InvariantCulture));
        AppendProfileValue(canonical, options.PreviewOnly ? "1" : "0");
        AppendProfileValue(canonical, options.Force ? "1" : "0");
        if (!options.ClearGeneratedTags)
        {
            // Single-item authorizations also advance the revision; they must not restart a library cycle.
            var generation = configuration.Installation?.Generation;
            AppendProfileValue(canonical, generation?.BaselineComplete == true ? "1" : "0");
            AppendProfileValue(canonical, string.Join(',', (generation?.AuthorizedItemTypes ?? []).Order(StringComparer.Ordinal)));
            AppendProfileValue(canonical, invocation == RunInvocation.PostScan ? "post-scan"
                : IsBulkBackfillInvocation(options, invocation) ? "backfill" : "automatic");
        }
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

    private static MetaTaggerPreviewChange CreatePreviewChange(BaseItem item, TagMergeResult merge, GenerationItemEligibility? eligibility)
    {
        return new MetaTaggerPreviewChange
        {
            ItemId = item.Id.ToString("N"),
            ItemName = item.Name,
            ItemPath = item.Path,
            ItemType = item.GetType().Name,
            GenerationEligibility = eligibility?.ToString(),
            AddedTags = merge.AddedTags,
            RemovedTags = merge.RemovedTags,
            PreviewRemovedTags = merge.PreviewRemovedTags
        };
    }

    private static PluginConfiguration CloneConfiguration(PluginConfiguration configuration)
    {
        return PluginConfigurationValidator.Sanitize(configuration);
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
        ApplyTask,
        ConfiguredTask,
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
