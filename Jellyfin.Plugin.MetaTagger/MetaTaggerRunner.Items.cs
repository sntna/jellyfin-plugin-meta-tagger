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

    private readonly Dictionary<string, ItemApproval> _itemApprovals = new(StringComparer.Ordinal);
    private static readonly TimeSpan ItemApprovalLifetime = TimeSpan.FromMinutes(15);
    private const int MaximumItemApprovals = 100;

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
            record = CreateRunRecord(summary, "Preview", itemId.ToString("N"));
            await PublishHistoryAsync(record).ConfigureAwait(false);
            var plan = await PlanItemAsync(itemId, configuration, cancellationToken).ConfigureAwait(false);
            var inspection = plan.Inspection;
            summary.ItemsScanned = 1;
            if (plan.Result is not null)
            {
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
            if (plan.Result?.Merge.HasChangesToApply == true && inspection.Status == "Changes")
            {
                foreach (var key in _itemApprovals.Where(pair => pair.Value.ExpiresUtc <= _clock.UtcNow).Select(pair => pair.Key).ToArray())
                {
                    _itemApprovals.Remove(key);
                }
                while (_itemApprovals.Count >= MaximumItemApprovals)
                {
                    _itemApprovals.Remove(_itemApprovals.MinBy(pair => pair.Value.ExpiresUtc).Key);
                }
                inspection.Token = Guid.NewGuid().ToString("N");
                inspection.ExpiresUtc = _clock.UtcNow + ItemApprovalLifetime;
                _itemApprovals[inspection.Token] = new ItemApproval(itemId, inspection.ExpiresUtc.Value, plan.ApprovalHash!);
            }
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

    public async Task<MetaTaggerRunSummary> ApplyItemAsync(Guid itemId, string token, CancellationToken cancellationToken)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        MetaTaggerRunRecord? record = null;
        ItemPlan? plan = null;
        var checkpointPending = false;
        var summary = new MetaTaggerRunSummary
        {
            LastRunUtc = _clock.UtcNow, RunMode = "ItemGeneration", PreviewOnly = false
        };
        try
        {
            if (string.IsNullOrWhiteSpace(token) || !_itemApprovals.Remove(token, out var approval)
                || approval.ItemId != itemId || approval.ExpiresUtc <= _clock.UtcNow)
            {
                throw new InvalidOperationException("Item approval expired or was already used. Preview this item again.");
            }
            summary.ConfigurationRevision = _host.GetConfiguration().ConfigurationRevision;
            record = CreateRunRecord(summary, "Apply", itemId.ToString("N"));
            await PublishHistoryAsync(record).ConfigureAwait(false);
            plan = await PlanItemAsync(itemId, null, cancellationToken).ConfigureAwait(false);
            if (plan.ApprovalHash != approval.Hash || plan.Inspection.Status != "Changes")
            {
                throw new InvalidOperationException("This item, its locks, plugin tag records, or settings changed. Preview this item again.");
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
            summary.ItemsScanned = 1;
            var result = _processor.Process(plan.Input!, configuration, state,
                new MetaTaggerRunOptions { Force = true, PreviewOnly = false }, started);
            AddCounts(summary, result.Merge, configuration);
            summary.ItemsChanged = result.Merge.HasChangesToApply ? 1 : 0;
            if (!budget.CanWrite(summary, out reason))
            {
                MarkBudgetLimitReached(summary, reason, 1);
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
            // Revalidate after the storage wait. This never broadens the approved plan.
            var current = await PlanItemAsync(itemId, null, cancellationToken).ConfigureAwait(false);
            if (current.ApprovalHash != approval.Hash)
            {
                throw new InvalidOperationException("The item changed before its tags could be saved. Preview this item again.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.ShouldStopBeforeItem(new MetaTaggerRunSummary(), _clock.UtcNow, out reason))
            {
                MarkBudgetLimitReached(summary, reason, 1);
                return summary;
            }
            await _host.UpdateItemTagsAsync(current.Item!, result.Merge.FinalTags, cancellationToken).ConfigureAwait(false);
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
            _logger.LogError(exception, "Meta Tagger item {ItemId} was written but its ownership checkpoint failed. Do not repeat the consumed approval.", itemId);
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
        return new MetaTaggerRunItem
        {
            ItemId = inspection.ItemId.ToString("N"), Name = inspection.Name, ItemType = inspection.ItemType,
            Outcome = outcome ?? inspection.Status, Reason = inspection.Reason,
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
            return new ItemPlan(inspection, item, null, null, null, null, null);
        }

        if (item is null) { return Unavailable("Unavailable", "This item was deleted or is unavailable."); }
        // Check locks before reading the ledger or projecting any metadata, including parent data.
        if (item.IsLocked) { return Unavailable("Protected", "Jellyfin metadata lock protects this item."); }
        if (item.LockedFields?.Contains(MetadataField.Tags) == true) { return Unavailable("Protected", "Jellyfin metadata lock on Tags protects this item."); }
        var source = draft ?? _host.GetConfiguration();
        var validation = PluginConfigurationValidator.Validate(source);
        if (!validation.IsValid) { return Unavailable("InvalidSettings", string.Join(" ", validation.Errors)); }
        var configuration = CloneConfiguration(source);
        inspection.ConfigurationRevision = configuration.ConfigurationRevision;
        if (!configuration.IsEnabled) { return Unavailable("Disabled", "Tagging is off in these settings. Select Turn on Meta Tagger to generate tags."); }
        if (!GetIncludedItemTypes(configuration).Any(type => type.ToString() == item.GetType().Name))
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
        var binding = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            itemId, configuration,
            Metadata = new MetadataFingerprintService().CreateFingerprint(input, configuration),
            input.ExistingTags, Ownership = owned?.LastAppliedTags ?? [],
            inspection.AddedTags, inspection.RemovedTags, inspection.PreviewRemovedTags
        });
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(binding));
        return new ItemPlan(inspection, item, configuration, state, result, hash, input);
    }

    private sealed record ItemApproval(Guid ItemId, DateTimeOffset ExpiresUtc, string Hash);
    private sealed record ItemPlan(MetaTaggerItemInspection Inspection, MediaBrowser.Controller.Entities.BaseItem? Item,
        PluginConfiguration? Configuration, MetaTaggerState? State, MetadataTagProcessResult? Result, string? ApprovalHash, MetadataTagInput? Input);
}
