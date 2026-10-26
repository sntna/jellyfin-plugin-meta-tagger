using Jellyfin.Plugin.MetaTagger.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaTagger;

public sealed partial class MetaTaggerRunner
{
    // Read-only evaluation is also used by inspection and item Preview.
    private sealed class ItemEvaluation(
        IMetaTaggerHost host,
        MetadataProjectionService metadataProjectionService,
        MetadataTagProcessor processor,
        IMetaTaggerStateStore stateStore,
        ILogger<MetaTaggerRunner> logger,
        IMetaTaggerClock clock)
    {
        private readonly IMetaTaggerHost _host = host;
        private readonly MetadataProjectionService _metadataProjectionService = metadataProjectionService;
        private readonly MetadataTagProcessor _processor = processor;
        private readonly IMetaTaggerStateStore _stateStore = stateStore;
        private readonly ILogger<MetaTaggerRunner> _logger = logger;
        private readonly IMetaTaggerClock _clock = clock;

        internal async Task<ItemPlan> PlanAsync(Guid itemId, PluginConfiguration? draft, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = _host.GetItem(itemId);
            System.Collections.Immutable.ImmutableArray<string> expectedTags = [.. item?.Tags ?? []];
            var inspection = new MetaTaggerItemInspection
            {
                ItemId = itemId, Name = item?.Name ?? string.Empty, ItemType = item?.GetType().Name ?? string.Empty,
                PreservedTags = (item?.Tags ?? []).ToArray(),
                ArtworkItemIds = MetaTaggerBrowserItem.GetArtworkItemIds(item)
            };
            ItemPlan Unavailable(string status, string reason, SourceExplanationStatus sourceStatus = SourceExplanationStatus.NotChecked, PluginConfiguration? settings = null)
            {
                inspection.Status = status;
                inspection.Reason = reason;
                inspection.SourceExplanations = MetadataTagService.UnavailableSources(sourceStatus, reason, settings);
                return new ItemPlan(inspection, item, null, null, null, null, expectedTags);
            }

            if (item is null) { return Unavailable("Unavailable", "This item was deleted or is unavailable."); }
            // Check locks before reading the ledger or projecting any metadata, including parent data.
            if (item.IsLocked) { return Unavailable("Protected", "Jellyfin metadata lock protects this item.", SourceExplanationStatus.Protected); }
            if (item.LockedFields?.Contains(MetadataField.Tags) == true) { return Unavailable("Protected", "Jellyfin metadata lock on Tags protects this item.", SourceExplanationStatus.Protected); }
            var source = draft ?? _host.GetConfiguration();
            var validation = PluginConfigurationValidator.Validate(source);
            if (!validation.IsValid) { return Unavailable("InvalidSettings", string.Join(" ", validation.Errors)); }
            var configuration = CloneConfiguration(source);
            if (draft is not null) { configuration.Installation = _host.GetConfiguration().Installation?.Copy(); }
            inspection.ConfigurationRevision = configuration.ConfigurationRevision;
            // The reported decision reads the same records a run would, so inspection and preview
            // report the eligibility a run acts on.
            var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            inspection.GenerationEligibility = new GenerationEligibilitySnapshot(configuration.Installation, RunInvocation.ConfiguredDefault)
                .Decide(item, state.Items).ToString();
            if (configuration.Installation?.Origin == InstallationOrigin.Uncertain)
            {
                return Unavailable("InstallationUnavailable", "The saved installation policy is unavailable. Restore the plugin configuration before applying tags.");
            }
            if (!configuration.IsEnabled) { return Unavailable("Disabled", "Tagging is off in these settings. Select Turn on Meta Tagger to generate tags.", SourceExplanationStatus.Disabled); }
            if (!GetIncludedItemTypes(configuration).Any(type => type.ToString() == GenerationItemType(item)))
            {
                return Unavailable("OutOfScope", "This item type is not selected in these settings. Select it under Item types to include it.", SourceExplanationStatus.ExcludedItemType);
            }

            if ((item.Tags ?? []).Contains(TagFormat.ManualControlTag(configuration, "skip"), StringComparer.OrdinalIgnoreCase))
            {
                return Unavailable("Protected", "Skipped because this item has the manual skip tag.", SourceExplanationStatus.Protected);
            }

            if ((item.Tags ?? []).Contains(TagFormat.ManualControlTag(configuration, "lock"), StringComparer.OrdinalIgnoreCase))
            {
                return Unavailable("Protected", "Skipped because this item has the older manual lock tag, which works like the skip tag.", SourceExplanationStatus.Protected);
            }

            // Item inspection and approval never claim untracked tags or consume maintenance actions.
            configuration.ClaimExistingGeneratedTagsForCleanup = false;
            configuration.LastRunSummaryText = string.Empty;
            MetadataTagInput input;
            try { input = ProjectMetadata(item, configuration, state, cancellationToken); }
            catch (MediaStreamLookupException exception)
            {
                _logger.LogError(exception, "Meta Tagger could not read media streams for item {ItemId}.", itemId);
                return Unavailable("Failed", "Jellyfin could not read this item's tracks. No tags or plugin tag records were changed. Try again after checking the server log.",
                    SourceExplanationStatus.LookupFailed, configuration);
            }
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
            inspection.SourceExplanations = new MetadataTagService().ExplainSources(input, configuration);
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
            return new ItemPlan(inspection, item, configuration, state, result, input, expectedTags);
        }

        internal MetadataTagInput ProjectMetadata(BaseItem item, PluginConfiguration configuration, MetaTaggerState state, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<MediaStream> streams = [];
            if (configuration.EnableAudioLanguages || configuration.EnableSubtitleLanguages)
            {
                try { streams = _host.GetMediaStreams(item.Id); }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new MediaStreamLookupException(exception);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return _metadataProjectionService.Project(item, configuration.IncludeParentSeriesMetadataOnEpisodes, state.Items, streams);
        }
    }

    private sealed record ItemPlan(MetaTaggerItemInspection Inspection, MediaBrowser.Controller.Entities.BaseItem? Item,
        PluginConfiguration? Configuration, MetaTaggerState? State, MetadataTagProcessResult? Result, MetadataTagInput? Input,
        System.Collections.Immutable.ImmutableArray<string> ExpectedTags);

    private sealed class MediaStreamLookupException(Exception innerException)
        : IOException("Jellyfin media-stream lookup failed.", innerException);
}
