using System.Runtime.ExceptionServices;
using Jellyfin.Plugin.MetaTagger.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaTagger;

public sealed partial class MetaTaggerRunner
{
    private enum ItemExecutionStatus { Completed, Deferred, Failed, Cancelled, Uncertain }

    private sealed record ItemOutcome(
        ItemExecutionStatus Status,
        MetaTaggerRunItem Detail,
        MetaTaggerPreviewChange? PreviewChange = null,
        ExceptionDispatchInfo? Error = null,
        TimeSpan? WriteDelay = null);

    private sealed record LibraryItemContext(
        PluginConfiguration Configuration,
        MetaTaggerState State,
        MetaTaggerRunCursor Cursor,
        MetaTaggerRunOptions Options,
        GenerationEligibilitySnapshot? Eligibility,
        MetaTaggerRunBudget Budget,
        DateTimeOffset Started,
        MetaTaggerRunSummary Summary);

    private abstract record ItemWork
    {
        private ItemWork() { }

        internal sealed record LibraryItem(BaseItem Item, LibraryItemContext Context,
            List<string>? MissingRecordedTags) : ItemWork;

        // Only preparation constructs this work. Its expected tags come from read-only evaluation.
        internal sealed record PreparedSingleItem(ItemPlan Plan, MetadataTagProcessResult Result,
            MetaTaggerRunBudget Budget, MetaTaggerRunSummary Summary) : ItemWork;
    }

    private sealed record SingleItemPreparation(
        MetaTaggerItemInspection Inspection,
        PluginConfiguration? Configuration,
        ItemWork.PreparedSingleItem? Work);

    private sealed class ItemExecution(
        IMetaTaggerHost host,
        MetadataTagProcessor processor,
        IMetaTaggerStateStore stateStore,
        IMetaTaggerClock clock,
        ILogger<MetaTaggerRunner> logger,
        ItemEvaluation evaluation)
    {
        internal async Task<SingleItemPreparation> PrepareSingleAsync(
            ItemPlan initial, MetaTaggerRunSummary summary, CancellationToken cancellationToken)
        {
            var budget = new MetaTaggerRunBudget(initial.Configuration!, summary.LastRunUtc!.Value);
            await stateStore.SaveAsync(initial.State!, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.ShouldStopBeforeItem(new MetaTaggerRunSummary(), clock.UtcNow, out var reason))
            {
                MarkBudgetLimitReached(summary, reason, 1);
                return new(initial.Inspection, initial.Configuration, null);
            }

            var plan = await evaluation.PlanAsync(initial.Inspection.ItemId, null, cancellationToken).ConfigureAwait(false);
            if (plan.Result is null) { return new(plan.Inspection, null, null); }
            var configuration = plan.Configuration!;
            budget = new MetaTaggerRunBudget(configuration, summary.LastRunUtc.Value);
            summary.ConfigurationRevision = configuration.ConfigurationRevision;
            summary.ItemsScanned = 1;
            summary.ItemsProcessed = 1;
            var result = processor.Process(plan.Input!, configuration, plan.State!,
                new MetaTaggerRunOptions { Force = true, PreviewOnly = false }, summary.LastRunUtc.Value);
            AddCounts(summary, result.Merge, configuration);
            summary.ItemsChanged = result.Merge.HasChangesToApply ? 1 : 0;
            if (budget.ShouldStopBeforeItem(new MetaTaggerRunSummary(), clock.UtcNow, out reason)
                || (result.ShouldWriteTags && !budget.CanWrite(summary, out reason)))
            {
                MarkBudgetLimitReached(summary, reason, 1);
                return new(plan.Inspection, configuration, null);
            }
            return new(plan.Inspection, configuration, new ItemWork.PreparedSingleItem(plan, result, budget, summary));
        }

        internal async Task<ItemOutcome> ExecuteAsync(ItemWork work, CancellationToken cancellationToken)
        {
            var single = work as ItemWork.PreparedSingleItem;
            var library = work as ItemWork.LibraryItem;
            var configuration = single?.Plan.Configuration ?? library!.Context.Configuration;
            var state = single?.Plan.State ?? library!.Context.State;
            var summary = single?.Summary ?? library!.Context.Summary;
            var budget = single?.Budget ?? library!.Context.Budget;
            var cursor = library?.Context.Cursor;
            var options = library?.Context.Options ?? new MetaTaggerRunOptions { Force = true, PreviewOnly = false };
            var item = single?.Plan.Item ?? library!.Item;
            var originalTags = single?.Plan.ExpectedTags ?? [.. item.Tags ?? []];
            var inspection = single?.Plan.Inspection;
            var inspectedDetail = inspection is null ? null : InspectionHistory(inspection, writeOutcome: "NotAttempted");
            var sourceExplanations = inspectedDetail?.SourceExplanations ?? [];
            var writeOutcome = "NotAttempted";
            var ownershipOutcome = "NotApplicable";
            GenerationItemEligibility? eligibility = single is null ? null : GenerationItemEligibility.Eligible;
            TagMergeResult? merge = null;
            var checkpointPending = false;
            var cursorAdvanced = false;

            MetaTaggerRunItem Describe(string outcome, string? reason = null) => new()
            {
                ItemId = item.Id.ToString("N"), Name = item.Name, ItemType = item.GetType().Name,
                Outcome = outcome, Reason = reason ?? inspection?.Reason ?? GenerationExclusionReason(eligibility),
                GenerationEligibility = eligibility?.ToString(),
                WriteOutcome = options.PreviewOnly && merge is not null ? "Proposal" : writeOutcome,
                OwnershipOutcome = ownershipOutcome,
                SourceExplanations = sourceExplanations.Count > 0 ? sourceExplanations
                    : options.ClearGeneratedTags ? [] : MetadataTagService.UnavailableSources(
                        outcome == "Protected" ? SourceExplanationStatus.Protected : SourceExplanationStatus.NotChecked,
                        reason ?? GenerationExclusionReason(eligibility) ?? "This source was not checked."),
                KeptTags = inspectedDetail?.KeptTags ?? originalTags.Except(merge?.RemovedTags ?? [], StringComparer.OrdinalIgnoreCase).ToArray(),
                AddedTags = merge?.AddedTags ?? inspectedDetail?.AddedTags ?? [],
                RemovedTags = merge?.RemovedTags ?? inspectedDetail?.RemovedTags ?? [],
                PreviewRemovedTags = merge?.PreviewRemovedTags ?? inspectedDetail?.PreviewRemovedTags ?? []
            };

            void CompleteCursor()
            {
                if (cursor is not null && !cursorAdvanced)
                {
                    AdvanceCursor(cursor, item.Id.ToString("N"));
                    cursorAdvanced = true;
                }
            }

            ItemOutcome Complete(string outcome, string? reason = null, MetaTaggerPreviewChange? change = null)
            {
                CompleteCursor();
                summary.ItemsRemaining = cursor is null ? 0 : ItemsRemaining(cursor);
                return new(ItemExecutionStatus.Completed, Describe(outcome, reason), change,
                    WriteDelay: writeOutcome == "Confirmed" ? budget.WriteDelay : null);
            }

            try
            {
                MetadataTagInput input;
                MetadataTagProcessResult result;
                if (single is not null)
                {
                    input = single.Plan.Input!;
                    result = single.Result;
                }
                else
                {
                    item = host.GetItem(item.Id)
                        ?? throw new InvalidOperationException("The queued item is no longer available.");
                    originalTags = [.. item.Tags ?? []];
                    if (item.IsLocked || item.LockedFields?.Contains(MetadataField.Tags) == true)
                    {
                        summary.ItemsSkippedLocked++;
                        return Complete("Protected", item.IsLocked
                            ? "Jellyfin metadata lock protects this item." : "Jellyfin metadata lock on Tags protects this item.");
                    }
                    if ((item.Tags ?? []).Any(tag => string.Equals(tag, TagFormat.ManualControlTag(configuration, "skip"), StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tag, TagFormat.ManualControlTag(configuration, "lock"), StringComparison.OrdinalIgnoreCase)))
                    {
                        summary.ItemsSkippedManual++;
                        return Complete("Protected", "Skipped because this item has a manual skip tag or its older lock alias.");
                    }
                    if (!options.ClearGeneratedTags && options.RunMode != MetadataTagRunMode.RebuildTrackingLedger)
                    {
                        eligibility = library!.Context.Eligibility!.Decide(item, state.Items);
                        if (!options.PreviewOnly && eligibility != GenerationItemEligibility.Eligible)
                        {
                            if (eligibility == GenerationItemEligibility.BackfillRequired) { summary.ItemsSkippedBaseline++; }
                            else { summary.ItemsSkippedEligibilityUnavailable++; }
                            return Complete(eligibility.Value.ToString());
                        }
                    }
                    input = options.ClearGeneratedTags
                        ? new MetadataTagInput
                        {
                            ItemId = item.Id.ToString("N"), ItemPath = item.Path,
                            ItemType = item.GetType().Name, ExistingTags = originalTags
                        }
                        : evaluation.ProjectMetadata(item, configuration, state, cancellationToken);
                    if (!options.ClearGeneratedTags)
                    {
                        sourceExplanations = new MetadataTagService().ExplainSources(input, configuration);
                    }
                    if (options.ClearGeneratedTags && options.PreviewOnly && options.CleanupItemId == item.Id
                        && state.Items.TryGetValue(input.ItemId, out var previous))
                    {
                        library!.MissingRecordedTags?.AddRange(previous.LastAppliedTags.Except(input.ExistingTags, StringComparer.OrdinalIgnoreCase));
                    }
                    summary.ItemsProcessed++;
                    result = processor.Process(input, configuration, state, options, library!.Context.Started);
                    if (options.PreviewOnly && eligibility == GenerationItemEligibility.BackfillRequired)
                    {
                        summary.ItemsPreviewedBaseline++;
                    }
                    if (result.SkipReason == MetadataTagSkipReason.Unchanged)
                    {
                        summary.ItemsSkippedUnchanged++;
                        return Complete("Up to date");
                    }
                    if (result.SkipReason == MetadataTagSkipReason.ManualSkip)
                    {
                        summary.ItemsSkippedManual++;
                        return Complete("Protected", "Skipped because this item has a manual skip tag or its older lock alias.");
                    }
                    AddCounts(summary, result.Merge, configuration);
                    if (result.Merge.HasChangesToApply || result.Merge.PreviewRemovedTags.Count > 0)
                    {
                        summary.ItemsChanged++;
                        if (options.PreviewOnly && result.Merge.HasChangesToApply) { summary.EstimatedWrites++; }
                        LogItemChange(configuration, options, item, result.Merge);
                    }
                }

                merge = result.Merge;
                // Scanning this item already consumed its item allowance. Only elapsed time and
                // media-write limits apply at this final gate.
                cancellationToken.ThrowIfCancellationRequested();
                if ((single is not null || result.ShouldWriteTags)
                    && (budget.ShouldStopBeforeItem(new MetaTaggerRunSummary(), clock.UtcNow, out var reason)
                        || (result.ShouldWriteTags && !budget.CanWrite(summary, out reason))))
                {
                    MarkBudgetLimitReached(summary, reason, cursor is null ? 1 : ItemsRemaining(cursor));
                    return new(ItemExecutionStatus.Deferred, Describe("Not applied", "Item update or time limit reached."));
                }

                if (result.ShouldWriteTags)
                {
                    writeOutcome = "Unconfirmed";
                    await host.UpdateItemTagsAsync(item, originalTags, merge.FinalTags, cancellationToken).ConfigureAwait(false);
                    writeOutcome = "Confirmed";
                    ownershipOutcome = "Unconfirmed";
                    summary.WritesApplied++;
                    checkpointPending = true;
                    state.Items[input.ItemId] = result.LedgerEntry
                        ?? throw new InvalidOperationException("A successful item write must have a corresponding ledger entry.");
                    CompleteCursor();
                    // Once Jellyfin confirms a write, ownership and cursor must commit together,
                    // even if cancellation was requested during the host update.
                    await stateStore.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
                    checkpointPending = false;
                    ownershipOutcome = "Confirmed";
                }
                else if (result.LedgerEntry is not null && eligibility is null or GenerationItemEligibility.Eligible)
                {
                    state.Items[input.ItemId] = result.LedgerEntry;
                    if (single is not null)
                    {
                        ownershipOutcome = "Unconfirmed";
                        await stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                        ownershipOutcome = "Confirmed";
                    }
                }

                if (library is not null && merge.PreviewRemovedTags.Count > 0)
                {
                    LogPreviewRemovals(configuration, item, merge);
                }
                var change = options.PreviewOnly && (merge.HasChangesToApply || merge.PreviewRemovedTags.Count > 0)
                    ? CreatePreviewChange(item, merge, eligibility) : null;
                return Complete(result.ShouldWriteTags ? "Applied"
                    : single is not null ? "Up to date"
                    : merge.HasChangesToApply || merge.PreviewRemovedTags.Count > 0 ? "Changes" : "Up to date",
                    single?.Plan.Inspection.Reason, change);
            }
            catch (Exception exception)
            {
                if (checkpointPending)
                {
                    summary.Outcome = "Uncertain";
                    return new(ItemExecutionStatus.Uncertain, Describe("Uncertain", "Tags were saved, but the plugin could not save its tag records. Check the item and the Jellyfin server log before previewing again."), Error: ExceptionDispatchInfo.Capture(exception));
                }
                if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    return new(ItemExecutionStatus.Cancelled, Describe("Cancelled", "Item processing stopped. Any attempted update may be unconfirmed."), Error: ExceptionDispatchInfo.Capture(exception));
                }
                if (exception is MediaStreamLookupException)
                {
                    sourceExplanations = MetadataTagService.UnavailableSources(SourceExplanationStatus.LookupFailed,
                        "Jellyfin could not read this item's tracks. No tags or plugin tag records were changed. Try again after checking the server log.", configuration);
                }
                var detail = Describe("Failed", exception is MediaStreamLookupException
                    ? "Jellyfin could not read this item's tracks. No tags or plugin tag records were changed."
                    : "Item processing failed. Check the server log.");
                // Single-item failures are counted by its coordinator's existing error presentation.
                if (library is not null)
                {
                    summary.Failures++;
                    CompleteCursor();
                    if (configuration.QuietLogging) { logger.LogError(exception, "Meta Tagger failed for item {ItemId}.", item.Id); }
                    else { logger.LogError(exception, "Meta Tagger failed for item {ItemName} ({ItemId}).", item.Name, item.Id); }
                }
                return new(ItemExecutionStatus.Failed, detail, Error: ExceptionDispatchInfo.Capture(exception));
            }
        }

        private void LogItemChange(
            PluginConfiguration configuration,
            MetaTaggerRunOptions options,
            BaseItem item,
            TagMergeResult merge)
        {
            if (configuration.QuietLogging)
            {
                logger.LogInformation(
                    "Meta Tagger {Mode} item {ItemId}: +{AddedCount} -{RemovedCount}",
                    options.PreviewOnly ? "would update" : "updating",
                    item.Id,
                    merge.AddedTags.Count,
                    merge.RemovedTags.Count);
                return;
            }

            logger.LogInformation(
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
                logger.LogInformation(
                    "Meta Tagger preview stale removals for item {ItemId}: {Count} tags",
                    item.Id,
                    merge.PreviewRemovedTags.Count);
                return;
            }

            logger.LogInformation(
                "Meta Tagger preview stale removals for {ItemName} ({ItemId}): {Tags}",
                item.Name,
                item.Id,
                string.Join(", ", merge.PreviewRemovedTags));
        }

    }
}
