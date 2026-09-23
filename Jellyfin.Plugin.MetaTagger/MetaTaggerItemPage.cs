using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.MetaTagger;

public sealed class MetaTaggerItemPage
{
    public string Status { get; init; } = "Ready";
    public int StartIndex { get; init; }
    public int TotalCount { get; init; }
    public IReadOnlyList<MetaTaggerBrowserItem> Items { get; init; } = [];
}

public sealed class MetaTaggerBrowserItem
{
    public Guid ItemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ItemType { get; init; } = string.Empty;
    public int? Year { get; init; }
    public bool HasPrimaryImage { get; init; }
    public IReadOnlyList<Guid> ArtworkItemIds { get; init; } = [];

    internal static IReadOnlyList<Guid> GetArtworkItemIds(BaseItem? item)
    {
        if (item is null) { return []; }
        Guid[] candidates = item switch
        {
            Episode episode => [item.Id, episode.SeasonId, episode.SeriesId],
            Season season => [item.Id, season.SeriesId],
            _ => [item.Id]
        };
        return candidates.Where(id => id != Guid.Empty).Distinct().ToArray();
    }
}

public sealed record MetaTaggerLibrary(Guid ItemId, string Name);
