using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.MetaTagger;

public sealed class MetadataProjectionService
{
    private readonly Func<BaseItem, BaseItem?> _parentResolver;

    public MetadataProjectionService()
        : this(DefaultParentResolver)
    {
    }

    internal MetadataProjectionService(Func<BaseItem, BaseItem?> parentResolver)
    {
        _parentResolver = parentResolver ?? throw new ArgumentNullException(nameof(parentResolver));
    }

    public MetadataTagInput Project(
        BaseItem item,
        bool includeParentSeriesMetadataOnEpisodes,
        IReadOnlyDictionary<string, MetaTaggerStateItem>? trackedItems = null,
        IReadOnlyList<MediaStream>? itemMediaStreams = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        var sourceItems = new List<BaseItem> { item };
        if (includeParentSeriesMetadataOnEpisodes
            && item is Episode
            && _parentResolver(item) is Series series)
        {
            sourceItems.Add(series);
        }

        return new MetadataTagInput
        {
            ItemId = item.Id.ToString("N"),
            ItemPath = item.Path,
            ItemType = item.GetType().Name,
            ExistingTags = item.Tags ?? [],
            KeywordSourceTags = SourceKeywords(sourceItems, trackedItems),
            Genres = SourceStrings(sourceItems, source => source.Genres),
            ParentalRatings = SourceValues(sourceItems, EffectiveParentalRating),
            Studios = SourceStrings(sourceItems, source => source.Studios),
            ProductionCountries = SourceStrings(sourceItems, source => source.ProductionLocations),
            AudioLanguages = SourceLanguages(itemMediaStreams, MediaStreamType.Audio),
            SubtitleLanguages = SourceLanguages(itemMediaStreams, MediaStreamType.Subtitle),
            ProviderIdSources = SourceProviderIds(sourceItems),
            ProductionYears = sourceItems
                .Select(source => source.ProductionYear)
                .Where(year => year.HasValue)
                .Select(year => year!.Value)
                .ToArray()
        };
    }

    private static string[] SourceLanguages(IReadOnlyList<MediaStream>? streams, MediaStreamType type)
    {
        return MetadataTagService.RecordedLanguages((streams ?? [])
                .Where(stream => stream.Type == type)
                .Select(stream => stream.Language))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static BaseItem? DefaultParentResolver(BaseItem item)
    {
        return item is Episode episode ? episode.Series : null;
    }

    private static string[] SourceStrings(IEnumerable<BaseItem> items, Func<BaseItem, IEnumerable<string>?> selector)
    {
        return items.SelectMany(item => selector(item) ?? []).ToArray();
    }

    private static string[] SourceKeywords(
        IEnumerable<BaseItem> items,
        IReadOnlyDictionary<string, MetaTaggerStateItem>? trackedItems)
    {
        var keywords = new List<string>();
        foreach (var item in items)
        {
            MetaTaggerStateItem? trackedItem = null;
            trackedItems?.TryGetValue(item.Id.ToString("N"), out trackedItem);
            var ownedTags = (trackedItem?.LastAppliedTags ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            keywords.AddRange((item.Tags ?? []).Where(tag => !ownedTags.Contains(tag)));
        }

        return keywords.ToArray();
    }

    private static string[] SourceValues(IEnumerable<BaseItem> items, Func<BaseItem, string?> selector)
    {
        return items
            .Select(selector)
            .Where(value => value is not null)
            .Select(value => value!)
            .ToArray();
    }

    private static string? EffectiveParentalRating(BaseItem item)
    {
        return string.IsNullOrEmpty(item.CustomRating) ? item.OfficialRating : item.CustomRating;
    }

    private static MetadataProviderId[] SourceProviderIds(IEnumerable<BaseItem> items)
    {
        return items
            .SelectMany(item => item.ProviderIds is null
                ? Enumerable.Empty<KeyValuePair<string, string?>>()
                : item.ProviderIds)
            .Select(pair => new MetadataProviderId(pair.Key, pair.Value))
            .ToArray();
    }
}
