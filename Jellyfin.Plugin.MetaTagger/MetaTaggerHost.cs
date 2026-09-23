using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaTagger.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.MetaTagger;

[Flags]
internal enum MetaTaggerOneTimeAction
{
    None = 0,
    Force = 1,
    Rebuild = 2,
    Claim = 4
}

internal readonly record struct MetaTaggerRunConfigurationPublication(
    PluginConfiguration RunStartConfiguration,
    string LastRunSummaryText,
    MetaTaggerOneTimeAction AcknowledgedActions)
{
    public MetaTaggerRunConfigurationMutation ApplyTo(PluginConfiguration currentConfiguration)
    {
        var acknowledgedActions = ReferenceEquals(currentConfiguration, RunStartConfiguration)
            ? AcknowledgedActions
            : MetaTaggerOneTimeAction.None;
        var mutation = new MetaTaggerRunConfigurationMutation(
            currentConfiguration,
            acknowledgedActions,
            currentConfiguration.ForceFullScanOnNextRun,
            currentConfiguration.RebuildTrackingLedgerOnNextRun,
            currentConfiguration.ClaimExistingGeneratedTagsOnNextRun);
        currentConfiguration.LastRunSummaryText = LastRunSummaryText;
        if (acknowledgedActions.HasFlag(MetaTaggerOneTimeAction.Force))
        {
            currentConfiguration.ForceFullScanOnNextRun = false;
        }

        if (acknowledgedActions.HasFlag(MetaTaggerOneTimeAction.Rebuild))
        {
            currentConfiguration.RebuildTrackingLedgerOnNextRun = false;
        }

        if (acknowledgedActions.HasFlag(MetaTaggerOneTimeAction.Claim))
        {
            currentConfiguration.ClaimExistingGeneratedTagsOnNextRun = false;
        }

        return mutation;
    }
}

internal readonly record struct MetaTaggerRunConfigurationMutation(
    PluginConfiguration Configuration,
    MetaTaggerOneTimeAction AcknowledgedActions,
    bool OriginalForce,
    bool OriginalRebuild,
    bool OriginalClaim)
{
    public void RestoreAcknowledgedActions()
    {
        if (AcknowledgedActions.HasFlag(MetaTaggerOneTimeAction.Force))
        {
            Configuration.ForceFullScanOnNextRun = OriginalForce;
        }

        if (AcknowledgedActions.HasFlag(MetaTaggerOneTimeAction.Rebuild))
        {
            Configuration.RebuildTrackingLedgerOnNextRun = OriginalRebuild;
        }

        if (AcknowledgedActions.HasFlag(MetaTaggerOneTimeAction.Claim))
        {
            Configuration.ClaimExistingGeneratedTagsOnNextRun = OriginalClaim;
        }
    }
}

internal interface IMetaTaggerHost
{
    PluginConfiguration GetConfiguration();

    IReadOnlyList<BaseItem> GetItems(BaseItemKind[] includedItemTypes);

    BaseItem? GetItem(Guid itemId);

    IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
        => throw new NotSupportedException("Media stream lookup is unavailable.");

    MetaTaggerItemPage BrowseItems(string searchTerm, Guid? libraryId, int startIndex, int limit, Guid? parentId = null, bool hierarchical = false)
        => throw new NotSupportedException("Item browsing is unavailable.");

    IReadOnlyList<MetaTaggerLibrary> GetLibraries() => [];

    Task UpdateItemTagsAsync(
        BaseItem item,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken);

    void PublishRunConfiguration(MetaTaggerRunConfigurationPublication publication);
}

internal sealed class JellyfinMetaTaggerHost : IMetaTaggerHost
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager? _mediaSourceManager;

    public JellyfinMetaTaggerHost(ILibraryManager libraryManager, IMediaSourceManager? mediaSourceManager = null)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
    }

    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
    {
        var manager = _mediaSourceManager
            ?? throw new InvalidOperationException("Media stream lookup is unavailable.");
        // A failed lookup must propagate so stale-tag removal cannot treat it as empty metadata.
        return manager.GetMediaStreams(itemId).ToArray();
    }

    public PluginConfiguration GetConfiguration()
    {
        return Plugin.Instance?.Configuration ?? new PluginConfiguration();
    }

    public IReadOnlyList<BaseItem> GetItems(BaseItemKind[] includedItemTypes)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = includedItemTypes
        });
    }

    public IReadOnlyList<MetaTaggerLibrary> GetLibraries()
    {
        return _libraryManager.GetVirtualFolders()
            .Where(folder => Guid.TryParse(folder.ItemId, out _))
            .Select(folder => new MetaTaggerLibrary(Guid.Parse(folder.ItemId), folder.Name))
            .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public MetaTaggerItemPage BrowseItems(string searchTerm, Guid? libraryId, int startIndex, int limit, Guid? parentId = null, bool hierarchical = false)
    {
        if (libraryId.HasValue && !GetLibraries().Any(library => library.ItemId == libraryId))
        {
            return new MetaTaggerItemPage { Status = "LibraryUnavailable" };
        }

        var parent = parentId.HasValue ? GetItem(parentId.Value) : null;
        if (parentId.HasValue && parent is not Series && parent is not Season)
        {
            return new MetaTaggerItemPage { Status = "ParentUnavailable" };
        }
        BaseItemKind[] types = parent switch
        {
            Series when !string.IsNullOrWhiteSpace(searchTerm) => [BaseItemKind.Episode],
            Series => [BaseItemKind.Season],
            Season => [BaseItemKind.Episode],
            _ when hierarchical && string.IsNullOrWhiteSpace(searchTerm) => [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Video],
            _ => [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.Video]
        };
        var result = _libraryManager.GetItemsResult(new InternalItemsQuery
        {
            Recursive = parent is null || (parent is Series && !string.IsNullOrWhiteSpace(searchTerm)),
            ParentId = parentId ?? Guid.Empty,
            IncludeItemTypes = types,
            SearchTerm = searchTerm,
            AncestorIds = libraryId.HasValue ? [libraryId.Value] : [],
            StartIndex = startIndex,
            Limit = limit,
            EnableTotalRecordCount = true,
            OrderBy = parent is null
                ? [(ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending)]
                : [(ItemSortBy.IndexNumber, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending), (ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending)]
        });
        return new MetaTaggerItemPage
        {
            StartIndex = startIndex,
            TotalCount = result.TotalRecordCount,
            Items = result.Items.Select(item => new MetaTaggerBrowserItem
            {
                ItemId = item.Id, Name = item.Name, ItemType = item.GetType().Name,
                Year = item.ProductionYear, HasPrimaryImage = item.HasImage(ImageType.Primary),
                ArtworkItemIds = MetaTaggerBrowserItem.GetArtworkItemIds(item)
            }).ToArray()
        };
    }

    public BaseItem? GetItem(Guid itemId)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            ItemIds = [itemId],
            Limit = 1
        }).FirstOrDefault();
    }

    public async Task UpdateItemTagsAsync(
        BaseItem item,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = GetItem(item.Id)
            ?? throw new InvalidOperationException("The item is no longer available for writing.");
        if (current.IsLocked
            || current.LockedFields?.Contains(MetadataField.Tags) == true
            || !(current.Tags ?? []).SequenceEqual(item.Tags ?? [], StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Item tags or metadata protection changed while planning; retry the item.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var originalTags = current.Tags;
        current.Tags = tags.ToArray();
        try
        {
            await current.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            current.Tags = originalTags;
            throw;
        }
    }

    public void PublishRunConfiguration(MetaTaggerRunConfigurationPublication publication)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        var configuration = plugin.Configuration;
        var mutation = publication.ApplyTo(configuration);
        try
        {
            plugin.SaveConfiguration();
        }
        catch
        {
            mutation.RestoreAcknowledgedActions();
            throw;
        }
    }
}
