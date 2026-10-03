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
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return GenerationStatus(_host.GetConfiguration().Installation);
        }
        catch (InvalidOperationException)
        {
            return GenerationStatus(null);
        }
        finally { _runGate.Release(); }
    }

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
            BaselineItemCount = generation?.BaselineComplete == true ? generation.BaselineItemIds!.Length : null,
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

    private void CaptureGenerationBaseline(PluginConfiguration configuration, MetaTaggerRunBudget budget,
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
        var ids = new HashSet<string>(installation.Generation?.BaselineItemIds ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.ShouldStopBeforeItem(summary, _clock.UtcNow, out reason))
            {
                MarkBudgetLimitReached(summary, reason, 0);
                return;
            }
            ids.Add(item.Id.ToString("N"));
        }
        var generation = installation.Generation?.Copy() ?? new GenerationState
        {
            AuthorizedItemTypes = [], AuthorizedItemIds = []
        };
        generation.Revision++;
        generation.BaselineItemIds = ids.Order(StringComparer.Ordinal).ToArray();
        generation.BaselineItemCount = generation.BaselineItemIds.Length;
        generation.BaselineComplete = true;
        cancellationToken.ThrowIfCancellationRequested();
        if (budget.ShouldStopBeforeItem(summary, _clock.UtcNow, out reason))
        {
            MarkBudgetLimitReached(summary, reason, 0);
            return;
        }
        configuration.Installation = _host.SaveGenerationState(installation.InstallationId, generation);
        DescribeGenerationState(summary, configuration.Installation);
        cancellationToken.ThrowIfCancellationRequested();
        if (budget.ShouldStopBeforeItem(summary, _clock.UtcNow, out reason))
        {
            MarkBudgetLimitReached(summary, reason, 0);
        }
    }

    private static string GenerationDecision(PluginConfiguration configuration, BaseItem item, RunInvocation invocation)
    {
        var installation = configuration.Installation;
        if (installation is null || installation.Origin == InstallationOrigin.Uncertain) { return "InstallationUnavailable"; }
        var generation = installation.Generation;
        if (generation is { IsValid: false }) { return "AuthorizationUnavailable"; }
        var itemType = GenerationItemType(item);
        if (generation?.AuthorizedItemTypes?.Contains(itemType, StringComparer.Ordinal) == true
            || generation?.AuthorizedItemIds?.Contains(item.Id.ToString("N"), StringComparer.OrdinalIgnoreCase) == true)
        {
            return "Eligible";
        }
        var prior = installation.PriorGenerationEligibility;
        var priorApply = invocation == RunInvocation.PostScan ? prior?.PostScanApply
            : invocation == RunInvocation.ApplyTask ? prior?.ApplyTask : prior?.ConfiguredApply;
        if (priorApply == true && prior?.IncludedItemTypes?.Contains(itemType, StringComparer.Ordinal) == true)
        {
            return "Eligible";
        }
        if (generation?.BaselineComplete != true) { return "BaselineUnavailable"; }
        return generation.BaselineItemIds!.Contains(item.Id.ToString("N"), StringComparer.OrdinalIgnoreCase)
            ? "BackfillRequired" : "Eligible";
    }

    private static string GenerationItemType(BaseItem item) => item switch
    {
        Movie => "Movie", Series => "Series", Episode => "Episode", Video => "Video", _ => item.GetType().Name
    };
}
