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
            return (await PlanItemAsync(itemId, draft, cancellationToken).ConfigureAwait(false)).Inspection;
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
            record = CreateRunRecord(summary, "Preview", itemId.ToString("N"));
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
                record.AddItem(InspectionHistory(limited, "Not checked"));
                return limited;
            }
            var plan = await PlanItemAsync(itemId, configuration, cancellationToken).ConfigureAwait(false);
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
            else { summary.Outcome = inspection.Status; }
            record.AddItem(InspectionHistory(inspection));
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
        var checkpointPending = false;
        try
        {
            // Waiting for another run must not consume this item's execution budget.
            summary.LastRunUtc = _clock.UtcNow;
            onStarted?.Invoke();
            var savedConfiguration = _host.GetConfiguration();
            summary.ConfigurationRevision = savedConfiguration.ConfigurationRevision;
            DescribeGenerationState(summary, savedConfiguration.Installation);
            record = CreateRunRecord(summary, "Apply", itemId.ToString("N"));
            await PublishHistoryAsync(record).ConfigureAwait(false);
            plan = await PlanItemAsync(itemId, null, cancellationToken).ConfigureAwait(false);
            if (plan.Result is null)
            {
                throw new InvalidOperationException(plan.Inspection.Reason ?? "This item cannot be updated.");
            }
            var configuration = plan.Configuration!;
            var state = plan.State!;
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
            // Confirm storage can checkpoint before crossing the media-write boundary.
            await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.ShouldStopBeforeItem(new MetaTaggerRunSummary(), _clock.UtcNow, out reason))
            {
                MarkBudgetLimitReached(summary, reason, 1);
                return summary;
            }
            // Recalculate after the storage wait using current metadata and saved settings.
            plan = await PlanItemAsync(itemId, null, cancellationToken).ConfigureAwait(false);
            if (plan.Result is null)
            {
                throw new InvalidOperationException(plan.Inspection.Reason ?? "This item cannot be updated.");
            }
            configuration = plan.Configuration!;
            state = plan.State!;
            budget = new MetaTaggerRunBudget(configuration, started);
            summary.ConfigurationRevision = configuration.ConfigurationRevision;
            summary.ItemsScanned = 1;
            summary.ItemsProcessed = 1;
            var result = _processor.Process(plan.Input!, configuration, state,
                new MetaTaggerRunOptions { Force = true, PreviewOnly = false }, started);
            AddCounts(summary, result.Merge, configuration);
            summary.ItemsChanged = result.Merge.HasChangesToApply ? 1 : 0;
            if (budget.ShouldStopBeforeItem(new MetaTaggerRunSummary(), _clock.UtcNow, out reason)
                || (result.Merge.HasChangesToApply && !budget.CanWrite(summary, out reason)))
            {
                MarkBudgetLimitReached(summary, reason, 1);
                return summary;
            }
            AuthorizeGenerationScope(configuration, [], itemId, summary, cancellationToken);
            plan.Inspection.GenerationEligibility = GenerationItemEligibility.Eligible.ToString();
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.ShouldStopBeforeItem(new MetaTaggerRunSummary(), _clock.UtcNow, out reason))
            {
                MarkBudgetLimitReached(summary, reason, 1);
                return summary;
            }
            if (!result.Merge.HasChangesToApply)
            {
                record.AddItem(InspectionHistory(plan.Inspection, "Up to date"));
                CompleteOutcome(summary);
                return summary;
            }
            await _host.UpdateItemTagsAsync(plan.Item!, result.Merge.FinalTags, cancellationToken).ConfigureAwait(false);
            checkpointPending = true;
            summary.WritesApplied = 1;
            state.Items[itemId.ToString("N")] = result.LedgerEntry!;
            await _stateStore.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
            checkpointPending = false;
            record.AddItem(InspectionHistory(plan.Inspection, "Applied"));
            await _clock.DelayAsync(budget.WriteDelay, cancellationToken).ConfigureAwait(false);
            CompleteOutcome(summary);
            await _stateStore.SaveSummaryAsync(summary, cancellationToken).ConfigureAwait(false);
            return summary;
        }
        catch (Exception exception) when (checkpointPending)
        {
            summary.Outcome = "Uncertain";
            _logger.LogError(exception, "Meta Tagger item {ItemId} was written but its ownership checkpoint failed. Inspect the item before retrying.", itemId);
            throw new InvalidOperationException("Tags were saved, but the plugin could not save its tag records. Check the item and the Jellyfin server log, then preview the item again before retrying.", exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { summary.Outcome = "Cancelled"; throw; }
        catch { summary.Outcome = "Failed"; summary.Failures++; throw; }
        finally
        {
            if (record is not null)
            {
                if (record.Items.Count == 0 && plan is not null) { record.AddItem(InspectionHistory(plan.Inspection, summary.BudgetLimitReached ? "Not checked" : summary.Outcome)); }
                await PublishHistoryAsync(record, finished: true).ConfigureAwait(false);
            }
            _runGate.Release();
        }
    }

    private static MetaTaggerRunItem InspectionHistory(MetaTaggerItemInspection inspection, string? outcome = null)
    {
        Enum.TryParse<GenerationItemEligibility>(inspection.GenerationEligibility, out var eligibility);
        return new MetaTaggerRunItem
        {
            ItemId = inspection.ItemId.ToString("N"), Name = inspection.Name, ItemType = inspection.ItemType,
            Outcome = outcome ?? inspection.Status, Reason = inspection.Reason ?? GenerationExclusionReason(eligibility),
            GenerationEligibility = inspection.GenerationEligibility,
            AddedTags = inspection.AddedTags, RemovedTags = inspection.RemovedTags, PreviewRemovedTags = inspection.PreviewRemovedTags
        };
    }

    private async Task<ItemPlan> PlanItemAsync(Guid itemId, PluginConfiguration? draft, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = _host.GetItem(itemId);
        var inspection = new MetaTaggerItemInspection
        {
            ItemId = itemId, Name = item?.Name ?? string.Empty, ItemType = item?.GetType().Name ?? string.Empty,
            ArtworkItemIds = MetaTaggerBrowserItem.GetArtworkItemIds(item)
        };
        ItemPlan Unavailable(string status, string reason)
        {
            inspection.Status = status;
            inspection.Reason = reason;
            return new ItemPlan(inspection, item, null, null, null, null);
        }

        if (item is null) { return Unavailable("Unavailable", "This item was deleted or is unavailable."); }
        // Check locks before reading the ledger or projecting any metadata, including parent data.
        if (item.IsLocked) { return Unavailable("Protected", "Jellyfin metadata lock protects this item."); }
        if (item.LockedFields?.Contains(MetadataField.Tags) == true) { return Unavailable("Protected", "Jellyfin metadata lock on Tags protects this item."); }
        var source = draft ?? _host.GetConfiguration();
        var validation = PluginConfigurationValidator.Validate(source);
        if (!validation.IsValid) { return Unavailable("InvalidSettings", string.Join(" ", validation.Errors)); }
        var configuration = CloneConfiguration(source);
        if (draft is not null) { configuration.Installation = _host.GetConfiguration().Installation?.Copy(); }
        inspection.ConfigurationRevision = configuration.ConfigurationRevision;
        inspection.GenerationEligibility = new GenerationEligibilitySnapshot(configuration.Installation, RunInvocation.ConfiguredDefault).Decide(item).ToString();
        if (configuration.Installation?.Origin == InstallationOrigin.Uncertain)
        {
            return Unavailable("InstallationUnavailable", "The saved installation policy is unavailable. Restore the plugin configuration before applying tags.");
        }
        if (!configuration.IsEnabled) { return Unavailable("Disabled", "Tagging is off in these settings. Select Turn on Meta Tagger to generate tags."); }
        if (!GetIncludedItemTypes(configuration).Any(type => type.ToString() == GenerationItemType(item)))
        {
            return Unavailable("OutOfScope", "This item type is not selected in these settings. Select it under Item types to include it.");
        }

        if ((item.Tags ?? []).Contains(TagFormat.ManualControlTag(configuration, "skip"), StringComparer.OrdinalIgnoreCase))
        {
            return Unavailable("Protected", "Skipped because this item has the manual skip tag.");
        }

        if ((item.Tags ?? []).Contains(TagFormat.ManualControlTag(configuration, "lock"), StringComparer.OrdinalIgnoreCase))
        {
            return Unavailable("Protected", "Skipped because this item has the older manual lock tag, which works like the skip tag.");
        }

        // Item inspection and approval never claim untracked tags or consume maintenance actions.
        configuration.ClaimExistingGeneratedTagsForCleanup = false;
        configuration.LastRunSummaryText = string.Empty;
        var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var input = ProjectMetadata(item, configuration, state, cancellationToken);
        var result = _processor.Process(input, configuration, state, new MetaTaggerRunOptions { Force = true }, _clock.UtcNow);
        var merge = result.Merge;
        state.Items.TryGetValue(input.ItemId, out var owned);
        inspection.MissingRecordedTags = (owned?.LastAppliedTags ?? [])
            .Except(input.ExistingTags, StringComparer.OrdinalIgnoreCase).ToArray();
        inspection.MissingTagsWithSourceOff = inspection.MissingRecordedTags.Where(tag =>
            !merge.AddedTags.Contains(tag, StringComparer.OrdinalIgnoreCase)
            && (TagFormat.TryGetGeneratedSource(tag, configuration)?.ToLowerInvariant() switch
            {
                "genre" => !configuration.EnableGenres,
                "rating" => !configuration.EnableParentalRating,
                "keyword" => !configuration.EnableExistingTagsAsKeywords,
                "studio" => !configuration.EnableStudios,
                "country" => !configuration.EnableProductionCountries,
                "provider" => !configuration.EnableProviderIds,
                "year" => !configuration.EnableProductionYear,
                "audio-language" => !configuration.EnableAudioLanguages,
                "subtitle-language" => !configuration.EnableSubtitleLanguages,
                _ => false
            })).ToArray();
        inspection.Sources = new MetadataTagService().ExplainTags(input, configuration);
        inspection.GeneratedTags = result.LedgerEntry?.LastGeneratedTags ?? [];
        inspection.AddedTags = merge.AddedTags;
        inspection.RemovedTags = merge.RemovedTags;
        inspection.PreviewRemovedTags = merge.PreviewRemovedTags;
        var retained = input.ExistingTags.Where(tag => !merge.RemovedTags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToArray();
        inspection.ManualTags = retained.Where(tag => TagFormat.IsManual(tag, configuration)).ToArray();
        inspection.OwnedTags = retained.Where(tag => !TagFormat.IsManual(tag, configuration)
            && (owned?.LastAppliedTags ?? []).Contains(tag, StringComparer.OrdinalIgnoreCase)).ToArray();
        inspection.PreservedTags = retained.Where(tag => !TagFormat.IsManual(tag, configuration)
            && !inspection.OwnedTags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToArray();
        inspection.Status = merge.HasChangesToApply || merge.PreviewRemovedTags.Count > 0 ? "Changes" : "Up to date";
        return new ItemPlan(inspection, item, configuration, state, result, input);
    }

    private sealed record ItemPlan(MetaTaggerItemInspection Inspection, MediaBrowser.Controller.Entities.BaseItem? Item,
        PluginConfiguration? Configuration, MetaTaggerState? State, MetadataTagProcessResult? Result, MetadataTagInput? Input);
}
