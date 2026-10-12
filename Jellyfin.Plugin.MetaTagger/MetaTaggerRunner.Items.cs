using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.MetaTagger.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.MetaTagger;

public sealed partial class MetaTaggerRunner
{
    public IReadOnlyList<MetaTaggerLibrary> GetLibraries() => _host.GetLibraries();

    public MetaTaggerItemPage BrowseItems(string? searchTerm, Guid? libraryId, int startIndex = 0, int limit = 25, Guid? parentId = null, bool hierarchical = false)
    {
        var term = searchTerm?.Trim() ?? string.Empty;
        if (term.Length > 200 || startIndex < 0 || limit < 1 || limit > 50)
        {
            throw new ArgumentException("Use a search of at most 200 characters, a nonnegative offset, and a page size from 1 to 50.");
        }

        return _host.BrowseItems(term, libraryId, startIndex, limit, parentId, hierarchical);
    }

    public async Task<MetaTaggerItemInspection> InspectItemAsync(
        Guid itemId, PluginConfiguration? draft, CancellationToken cancellationToken)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await _itemEvaluation.PlanAsync(itemId, draft, cancellationToken).ConfigureAwait(false)).Inspection;
        }
        finally { _runGate.Release(); }
    }

    public async Task<MetaTaggerItemInspection> PreviewItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        MetaTaggerRunRecord? record = null;
        var summary = new MetaTaggerRunSummary { LastRunUtc = _clock.UtcNow, PreviewOnly = true, RunMode = "ItemGeneration" };
        try
        {
            var configuration = CloneConfiguration(_host.GetConfiguration());
            summary.ConfigurationRevision = configuration.ConfigurationRevision;
            DescribeGenerationState(summary, configuration.Installation);
            record = CreateRunRecord(summary, "Preview", itemId.ToString("N"), configuration: configuration);
            await PublishHistoryAsync(record).ConfigureAwait(false);
            if (configuration.IsEnabled && configuration.Installation?.Origin != InstallationOrigin.Uncertain)
            {
                CaptureGenerationBaseline(configuration, new MetaTaggerRunBudget(configuration, summary.LastRunUtc!.Value),
                    summary, cancellationToken);
            }
            if (summary.BudgetLimitReached)
            {
                summary.ItemsRemaining = 1;
                var limited = new MetaTaggerItemInspection
                {
                    ItemId = itemId, ConfigurationRevision = configuration.ConfigurationRevision,
                    Status = "Budget limited", GenerationEligibility = GenerationItemEligibility.NotChecked.ToString(),
                    Reason = "The time limit was reached before this item could be checked. Preview this item again."
                };
                record.AddItem(InspectionHistory(limited, "Not checked", "NotAttempted"));
                return limited;
            }
            var plan = await _itemEvaluation.PlanAsync(itemId, configuration, cancellationToken).ConfigureAwait(false);
            var inspection = plan.Inspection;
            summary.ItemsScanned = 1;
            if (plan.Result is not null)
            {
                summary.ItemsProcessed = 1;
                if (inspection.GenerationEligibility == nameof(GenerationItemEligibility.BackfillRequired)) { summary.ItemsPreviewedBaseline = 1; }
                AddCounts(summary, plan.Result.Merge, configuration);
                summary.ItemsChanged = inspection.Status == "Changes" ? 1 : 0;
                summary.EstimatedWrites = plan.Result.Merge.HasChangesToApply ? 1 : 0;
            }
            else if (inspection.Status == "Protected")
            {
                if (inspection.Reason?.StartsWith("Jellyfin", StringComparison.Ordinal) == true) { summary.ItemsSkippedLocked = 1; }
                else { summary.ItemsSkippedManual = 1; }
            }
            else
            {
                summary.Outcome = inspection.Status;
                if (inspection.Status == "Failed") { summary.Failures++; }
            }
            record.AddItem(InspectionHistory(inspection, writeOutcome: plan.Result is null ? "NotAttempted" : "Proposal"));
            return inspection;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { summary.Outcome = "Cancelled"; throw; }
        catch { summary.Outcome = "Failed"; throw; }
        finally
        {
            if (record is not null) { await PublishHistoryAsync(record, finished: true).ConfigureAwait(false); }
            _runGate.Release();
        }
    }

    public Task<MetaTaggerRunSummary> ApplyItemAsync(Guid itemId, CancellationToken cancellationToken)
        => ApplyItemCoreAsync(itemId, CreateItemSummary(), cancellationToken);

    private async Task<MetaTaggerRunSummary> ApplyItemCoreAsync(Guid itemId, MetaTaggerRunSummary summary,
        CancellationToken cancellationToken, Action? onStarted = null)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        MetaTaggerRunRecord? record = null;
        ItemPlan? plan = null;
        MetaTaggerItemInspection? inspection = null;
        ItemOutcome? execution = null;
        try
        {
            // Waiting for another run must not consume this item's execution budget.
            summary.LastRunUtc = _clock.UtcNow;
            onStarted?.Invoke();
            var savedConfiguration = _host.GetConfiguration();
            summary.ConfigurationRevision = savedConfiguration.ConfigurationRevision;
            DescribeGenerationState(summary, savedConfiguration.Installation);
            summary.ItemsRemaining = 1;
            record = CreateRunRecord(summary, "Apply", itemId.ToString("N"), configuration: CloneConfiguration(savedConfiguration));
            await PublishHistoryAsync(record).ConfigureAwait(false);
            plan = await _itemEvaluation.PlanAsync(itemId, null, cancellationToken).ConfigureAwait(false);
            inspection = plan.Inspection;
            if (plan.Result is null)
            {
                summary.ItemsScanned = 1;
                throw new InvalidOperationException(plan.Inspection.Reason ?? "This item cannot be updated.");
            }
            var configuration = plan.Configuration!;
            var started = summary.LastRunUtc!.Value;
            var budget = new MetaTaggerRunBudget(configuration, started);
            if (budget.ShouldStopBeforeItem(summary, _clock.UtcNow, out var reason))
            {
                MarkBudgetLimitReached(summary, reason, 1);
                return summary;
            }
            // Optional checking must not change when later additions become eligible.
            CaptureGenerationBaseline(configuration, budget, summary, cancellationToken);
            if (summary.BudgetLimitReached)
            {
                summary.ItemsRemaining = 1;
                return summary;
            }
            var preparation = await _itemExecution.PrepareSingleAsync(plan, summary, cancellationToken).ConfigureAwait(false);
            inspection = preparation.Inspection;
            if (preparation.Configuration is { } preparedConfiguration)
            {
                record.ConfigurationRevision = preparedConfiguration.ConfigurationRevision;
                record.ItemTypes = GetIncludedItemTypes(preparedConfiguration).Select(type => type.ToString()).ToArray();
                record.RecordedRules = MetaTaggerRecordedRules.Capture(preparedConfiguration);
            }
            if (summary.BudgetLimitReached) { return summary; }
            if (preparation.Work is null)
            {
                throw new InvalidOperationException(inspection.Reason ?? "This item cannot be updated.");
            }
            AuthorizeGenerationScope(preparation.Configuration!, [], inspection.ItemId, summary, cancellationToken);
            inspection.GenerationEligibility = GenerationItemEligibility.Eligible.ToString();
            execution = await _itemExecution.ExecuteAsync(preparation.Work, cancellationToken).ConfigureAwait(false);
            record.AddItem(execution.Detail);
            if (execution.Status == ItemExecutionStatus.Deferred) { return summary; }
            execution.Error?.Throw();
            if (execution.WriteDelay is { } delay)
            {
                await PublishHistoryAsync(record).ConfigureAwait(false);
                await _clock.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                CompleteOutcome(summary);
                return summary;
            }
            CompleteOutcome(summary);
            await _stateStore.SaveSummaryAsync(summary, cancellationToken).ConfigureAwait(false);
            return summary;
        }
        catch (Exception exception) when (execution?.Status == ItemExecutionStatus.Uncertain)
        {
            summary.Outcome = "Uncertain";
            if (record is not null) { record.FailureReason = "Tags were saved, but the plugin could not save its tag records. Inspect the item before retrying."; }
            _logger.LogError(exception, "Meta Tagger item {ItemId} was written but its ownership checkpoint failed. Inspect the item before retrying.", itemId);
            throw new InvalidOperationException("Tags were saved, but the plugin could not save its tag records. Check the item and the Jellyfin server log, then preview the item again before retrying.", exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { summary.Outcome = "Cancelled"; throw; }
        catch
        {
            summary.Outcome = "Failed"; summary.Failures++;
            if (record is not null) { record.FailureReason = "Item processing failed. Inspect the item and the Jellyfin server log before retrying."; }
            throw;
        }
        finally
        {
            if (record is not null)
            {
                if (record.Items.Count == 0 && inspection is not null) { record.AddItem(InspectionHistory(inspection, summary.BudgetLimitReached ? "Not applied" : summary.Outcome, "NotAttempted", "NotApplicable")); }
                if (record.Items.Count == 0 && inspection is null)
                {
                    record.AddItem(new MetaTaggerRunItem
                    {
                        ItemId = itemId.ToString("N"), Outcome = summary.Outcome, WriteOutcome = "NotAttempted",
                        OwnershipOutcome = "NotApplicable", Reason = "Processing stopped before item details were available."
                    });
                }
                await PublishHistoryAsync(record, finished: true).ConfigureAwait(false);
            }
            _runGate.Release();
        }
    }

    private static MetaTaggerRunItem InspectionHistory(MetaTaggerItemInspection inspection, string? outcome = null,
        string writeOutcome = "Proposal", string ownershipOutcome = "NotApplicable")
    {
        Enum.TryParse<GenerationItemEligibility>(inspection.GenerationEligibility, out var eligibility);
        return new MetaTaggerRunItem
        {
            ItemId = inspection.ItemId.ToString("N"), Name = inspection.Name, ItemType = inspection.ItemType,
            Outcome = outcome ?? inspection.Status, Reason = inspection.Reason ?? GenerationExclusionReason(eligibility),
            GenerationEligibility = inspection.GenerationEligibility, SourceExplanations = inspection.SourceExplanations,
            WriteOutcome = writeOutcome, OwnershipOutcome = ownershipOutcome,
            KeptTags = inspection.ManualTags.Concat(inspection.OwnedTags).Concat(inspection.PreservedTags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            AddedTags = inspection.AddedTags, RemovedTags = inspection.RemovedTags, PreviewRemovedTags = inspection.PreviewRemovedTags
        };
    }

}
