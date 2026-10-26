using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaTagger.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.MetaTagger;

public sealed partial class MetaTaggerRunner
{
    public async Task<MetaTaggerGenerationStatus> GetGenerationStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Generation state is replaced as a copy when saved, so reading it does not wait for a run.
        InstallationState? installation;
        try
        {
            installation = _host.GetConfiguration().Installation;
        }
        catch (InvalidOperationException)
        {
            return GenerationStatus(null);
        }

        var status = GenerationStatus(installation);
        if (status.BaselineStatus != "Complete") { return status; }
        // The recorded count and checksum cannot show on their own that the stored item IDs
        // are the ones they describe, and a baseline failing that check withholds generation.
        var baselineItemIds = await LoadBaselineItemIdsAsync(_stateStore, installation?.Generation, cancellationToken)
            .ConfigureAwait(false);
        return baselineItemIds is null ? UnverifiableBaselineStatus : status;
    }

    // A recorded baseline whose stored item IDs cannot be verified proves nothing, so neither
    // the reported status nor a run summary may present it as a baseline to act on.
    private static MetaTaggerGenerationStatus UnverifiableBaselineStatus
        => new() { Status = "AuthorizationUnavailable", BaselineStatus = "Unavailable" };

    private static void DescribeUnverifiableBaseline(MetaTaggerRunSummary summary)
    {
        summary.BaselineStatus = UnverifiableBaselineStatus.BaselineStatus;
        summary.BaselineItemCount = UnverifiableBaselineStatus.BaselineItemCount;
    }

    public MetaTaggerGenerationStatus ConfirmInstallation()
        => GenerationStatus(_host.ResolveUncertainInstallation());

    private static MetaTaggerGenerationStatus GenerationStatus(InstallationState? installation)
    {
        if (installation is null || installation.Origin == InstallationOrigin.Uncertain)
        {
            return new() { Status = "InstallationUnavailable", BaselineStatus = "Unavailable" };
        }
        var generation = installation.Generation;
        if (generation is { IsValid: false })
        {
            return new() { Status = "AuthorizationUnavailable", BaselineStatus = "Unavailable" };
        }
        return new()
        {
            BaselineStatus = generation is null ? "NotCaptured" : generation.BaselineComplete ? "Complete" : "Incomplete",
            BaselineItemCount = generation?.BaselineComplete == true ? generation.BaselineItemCount : null,
            EligibilityRevision = generation?.Revision ?? 0,
            AuthorizedItemTypes = generation?.AuthorizedItemTypes?.ToArray() ?? [],
            AuthorizedItemIds = generation?.AuthorizedItemIds?.ToArray() ?? [],
            PriorGenerationEligibility = installation.PriorGenerationEligibility?.Copy()
        };
    }

    private static void DescribeGenerationState(MetaTaggerRunSummary summary, InstallationState? installation)
    {
        var status = GenerationStatus(installation);
        summary.BaselineStatus = status.BaselineStatus;
        summary.BaselineItemCount = status.BaselineItemCount;
        summary.EligibilityRevision = status.EligibilityRevision;
    }

    private static readonly BaseItemKind[] SupportedGenerationTypes =
        [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.Video];

    // Null means a recorded baseline whose item IDs could not be verified, which is not
    // the same as a library that had no items when the baseline was captured.
    private static Task<string[]?> LoadBaselineItemIdsAsync(IMetaTaggerStateStore stateStore,
        GenerationState? generation, CancellationToken cancellationToken)
    {
        return generation is { BaselineComplete: true }
            ? stateStore.LoadBaselineAsync(generation.BaselineItemCount, generation.BaselineChecksum, cancellationToken)
            : Task.FromResult<string[]?>([]);
    }

    private static bool IsBulkBackfillInvocation(MetaTaggerRunOptions options, RunInvocation invocation)
        => !options.PreviewOnly && !options.ClearGeneratedTags && options.RunMode != MetadataTagRunMode.RebuildTrackingLedger
            && invocation is RunInvocation.ExplicitOptions or RunInvocation.ApplyTask;

    private void AuthorizeGenerationScope(PluginConfiguration configuration, string[] itemTypes, Guid? itemId,
        MetaTaggerRunSummary summary, CancellationToken cancellationToken)
    {
        var installation = configuration.Installation
            ?? throw new InvalidOperationException("Generation authorization is unavailable for this installation.");
        var generation = installation.Generation?.Copy() ?? new GenerationState
        {
            AuthorizedItemTypes = [], AuthorizedItemIds = []
        };
        var types = generation.AuthorizedItemTypes!.Union(itemTypes, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var ids = generation.AuthorizedItemIds!.Union(itemId is { } id ? [id.ToString("N")] : [], StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (generation.Revision == 0 || itemTypes.Except(generation.AuthorizedItemTypes!, StringComparer.Ordinal).Any()
            || itemId is { } target && !generation.AuthorizedItemIds!.Contains(target.ToString("N"), StringComparer.OrdinalIgnoreCase))
        {
            generation.AuthorizedItemTypes = types;
            generation.AuthorizedItemIds = ids;
            generation.Revision++;
            configuration.Installation = _host.SaveGenerationState(installation.InstallationId, generation);
        }
        summary.BackfillAuthorization = itemId.HasValue ? "Single item" : "Item types";
        summary.AuthorizedItemTypes = itemTypes;
        summary.AuthorizedItemId = itemId?.ToString("N");
        DescribeGenerationState(summary, configuration.Installation);
    }

    private async Task CaptureGenerationBaselineAsync(PluginConfiguration configuration, MetaTaggerRunBudget budget,
        MetaTaggerRunSummary summary, CancellationToken cancellationToken)
    {
        var installation = configuration.Installation;
        DescribeGenerationState(summary, installation);
        if (installation is null || installation.Generation?.BaselineComplete == true) { return; }
        cancellationToken.ThrowIfCancellationRequested();
        if (budget.ShouldStopBeforeItem(summary, _clock.UtcNow, out var reason))
        {
            MarkBudgetLimitReached(summary, reason, 0);
            return;
        }
        var items = _host.GetItems(SupportedGenerationTypes);
        var selectedTypes = GetIncludedItemTypes(configuration).Select(type => type.ToString()).ToHashSet(StringComparer.Ordinal);
        var itemsRemaining = items.Where(item => selectedTypes.Contains(GenerationItemType(item)))
            .Select(item => item.Id).Distinct().Count();
        // Recapturing an incomplete baseline keeps membership recorded earlier, so an item
        // that is temporarily absent from the library does not become a later addition. A
        // baseline captured before the item IDs moved records them in the saved policy, which
        // is the only place this recapture can read that membership from.
        var ids = new HashSet<string>(
            await _stateStore.LoadBaselineAsync(installation.Generation?.BaselineItemCount,
                installation.Generation?.BaselineChecksum, cancellationToken).ConfigureAwait(false)
                ?? installation.Generation?.BaselineItemIds ?? [],
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.ShouldStopBeforeItem(summary, _clock.UtcNow, out reason))
            {
                MarkBudgetLimitReached(summary, reason, itemsRemaining);
                return;
            }
            ids.Add(item.Id.ToString("N"));
        }
        var generation = installation.Generation?.Copy() ?? new GenerationState
        {
            AuthorizedItemTypes = [], AuthorizedItemIds = []
        };
        var baselineItemIds = ids.Order(StringComparer.Ordinal).ToArray();
        generation.Revision++;
        generation.BaselineItemCount = baselineItemIds.Length;
        generation.BaselineChecksum = MetaTaggerStateStore.ComputeBaselineChecksum(baselineItemIds);
        generation.BaselineComplete = true;
        // The relocated file now records the item IDs, so the saved policy stops carrying them.
        generation.BaselineItemIds = null;
        cancellationToken.ThrowIfCancellationRequested();
        if (budget.ShouldStopBeforeItem(summary, _clock.UtcNow, out reason))
        {
            MarkBudgetLimitReached(summary, reason, itemsRemaining);
            return;
        }
        // The item IDs must be readable before the saved policy claims this baseline.
        // A configuration that records a baseline its file cannot reproduce is not trusted.
        await _stateStore.SaveBaselineAsync(baselineItemIds, cancellationToken).ConfigureAwait(false);
        configuration.Installation = _host.SaveGenerationState(installation.InstallationId, generation);
        DescribeGenerationState(summary, configuration.Installation);
        cancellationToken.ThrowIfCancellationRequested();
        if (budget.ShouldStopBeforeItem(summary, _clock.UtcNow, out reason))
        {
            MarkBudgetLimitReached(summary, reason, itemsRemaining);
        }
    }

    private sealed class GenerationEligibilitySnapshot
    {
        private readonly GenerationItemEligibility? _unavailable;
        private readonly bool _baselineComplete;
        private readonly HashSet<string> _authorizedTypes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _authorizedIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _baselineIds = new(StringComparer.OrdinalIgnoreCase);

        internal GenerationEligibilitySnapshot(InstallationState? installation, RunInvocation invocation,
            string[]? baselineItemIds)
        {
            if (installation is null || installation.Origin == InstallationOrigin.Uncertain)
            {
                _unavailable = GenerationItemEligibility.InstallationUnavailable;
                return;
            }

            var generation = installation.Generation;
            if (generation is { IsValid: false })
            {
                _unavailable = GenerationItemEligibility.AuthorizationUnavailable;
                return;
            }

            _baselineComplete = generation?.BaselineComplete == true;
            if (_baselineComplete && baselineItemIds is null)
            {
                // A recorded baseline whose stored item IDs cannot be verified proves nothing.
                _unavailable = GenerationItemEligibility.AuthorizationUnavailable;
                return;
            }

            _authorizedTypes.UnionWith(generation?.AuthorizedItemTypes ?? []);
            _authorizedIds.UnionWith(generation?.AuthorizedItemIds ?? []);
            if (_baselineComplete) { _baselineIds.UnionWith(baselineItemIds!); }

            var prior = installation.PriorGenerationEligibility;
            var priorApply = invocation == RunInvocation.PostScan ? prior?.PostScanApply
                : invocation == RunInvocation.ApplyTask ? prior?.ApplyTask : prior?.ConfiguredApply;
            if (priorApply == true) { _authorizedTypes.UnionWith(prior?.IncludedItemTypes ?? []); }
        }

        // The ledger is read per item by key. Callers pass the live records so the decision never
        // rebuilds a per-item index over them.
        internal GenerationItemEligibility Decide(BaseItem item, IReadOnlyDictionary<string, MetaTaggerStateItem> ledger)
        {
            ArgumentNullException.ThrowIfNull(ledger);
            if (_unavailable is { } unavailable) { return unavailable; }
            if (_authorizedTypes.Contains(GenerationItemType(item))) { return GenerationItemEligibility.Eligible; }
            var itemId = item.Id.ToString("N");
            if (_authorizedIds.Contains(itemId)) { return GenerationItemEligibility.Eligible; }
            if (!_baselineComplete) { return GenerationItemEligibility.BaselineUnavailable; }
            if (!_baselineIds.Contains(itemId)) { return GenerationItemEligibility.Eligible; }
            // Backfill authorization withholds existing library items the plugin has never tagged.
            // An item whose records hold a confirmed write by this plugin is already managed, so
            // re-applying to it is maintenance rather than backfill.
            return HasConfirmedPluginWrite(ledger, itemId)
                ? GenerationItemEligibility.Eligible : GenerationItemEligibility.BackfillRequired;
        }
    }

    private static bool HasConfirmedPluginWrite(IReadOnlyDictionary<string, MetaTaggerStateItem> ledger, string itemId)
        // Saved records can be incomplete, so a confirmed write needs both a recorded write time
        // and recorded tags for that item.
        => ledger.TryGetValue(itemId, out var entry)
            && entry?.LastWriteUtc is not null
            && entry.LastAppliedTags?.Length > 0;

    private enum GenerationItemEligibility
    {
        NotChecked,
        Eligible,
        BackfillRequired,
        BaselineUnavailable,
        InstallationUnavailable,
        AuthorizationUnavailable
    }

    private static string? GenerationExclusionReason(GenerationItemEligibility? eligibility) => eligibility switch
    {
        GenerationItemEligibility.BackfillRequired => "Automatic generation excludes this existing library item until Apply authorizes it.",
        GenerationItemEligibility.BaselineUnavailable or GenerationItemEligibility.InstallationUnavailable
            or GenerationItemEligibility.AuthorizationUnavailable => "Automatic generation cannot verify the saved baseline or authorization.",
        _ => null
    };

    private static string GenerationItemType(BaseItem item) => item switch
    {
        Movie => "Movie", Series => "Series", Episode => "Episode", Video => "Video", _ => item.GetType().Name
    };
}
