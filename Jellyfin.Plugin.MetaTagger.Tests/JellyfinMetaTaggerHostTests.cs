using Jellyfin.Data.Enums;
using System.Reflection;
using Jellyfin.Plugin.MetaTagger;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed class JellyfinMetaTaggerHostTests
{
    [Fact]
    public void GetMediaStreams_ReturnsRecordedTracksAndPropagatesLookupFailures()
    {
        var manager = DispatchProxy.Create<IMediaSourceManager, MediaSourceManagerProxy>();
        var proxy = (MediaSourceManagerProxy)(object)manager;
        var library = DispatchProxy.Create<ILibraryManager, LibraryManagerProxy>();
        var host = new JellyfinMetaTaggerHost(library, manager);
        var itemId = Guid.NewGuid();

        Assert.Equal("eng", Assert.Single(host.GetMediaStreams(itemId)).Language);
        Assert.Equal(itemId, proxy.ItemId);
        proxy.Fail = true;
        Assert.Throws<IOException>(() => host.GetMediaStreams(itemId));
    }

    public class MediaSourceManagerProxy : DispatchProxy
    {
        public Guid ItemId { get; private set; }
        public bool Fail { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IMediaSourceManager.GetMediaStreams) && args is [Guid itemId])
            {
                ItemId = itemId;
                if (Fail) { throw new IOException("Injected stream lookup failure"); }
                return new List<MediaStream> { new() { Type = MediaStreamType.Audio, Language = "eng" } };
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    [Fact]
    public void BrowseItems_PassesSearchLibraryAndPageToJellyfinWithoutMaterializingLibrary()
    {
        var manager = DispatchProxy.Create<ILibraryManager, LibraryManagerProxy>();
        var proxy = (LibraryManagerProxy)(object)manager;
        var libraryId = Guid.NewGuid();
        proxy.LibraryId = libraryId;
        var host = new JellyfinMetaTaggerHost(manager);
        var result = host.BrowseItems("Dune", libraryId, 25, 25);
        Assert.Equal(60, result.TotalCount);
        Assert.Equal("Dune", Assert.Single(result.Items).Name);
        Assert.Equal(25, proxy.BrowseQuery!.StartIndex);
        Assert.Equal(25, proxy.BrowseQuery.Limit);
        Assert.Equal("Dune", proxy.BrowseQuery.SearchTerm);
        Assert.Contains(libraryId, proxy.BrowseQuery.AncestorIds);
        Assert.NotEmpty(proxy.BrowseQuery.OrderBy);
    }

    [Fact]
    public void HierarchicalBrowse_UsesPagedSeasonAndEpisodeQueriesAndSearchStillFindsEpisodes()
    {
        var manager = DispatchProxy.Create<ILibraryManager, LibraryManagerProxy>();
        var proxy = (LibraryManagerProxy)(object)manager;
        var host = new JellyfinMetaTaggerHost(manager);
        var series = new Series { Id = Guid.NewGuid() };
        var season = new Season { Id = Guid.NewGuid(), SeriesId = series.Id };
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, SeasonId = season.Id };

        host.BrowseItems("", null, 0, 25, hierarchical: true);
        Assert.DoesNotContain(BaseItemKind.Episode, proxy.BrowseQuery!.IncludeItemTypes);
        host.BrowseItems("Pilot", null, 0, 25, hierarchical: true);
        Assert.Contains(BaseItemKind.Episode, proxy.BrowseQuery!.IncludeItemTypes);

        proxy.CurrentItem = series;
        proxy.BrowseItems = [season];
        var seasons = host.BrowseItems("", proxy.LibraryId, 25, 25, series.Id, true);
        Assert.Equal(series.Id, proxy.BrowseQuery!.ParentId);
        Assert.False(proxy.BrowseQuery.Recursive);
        Assert.Equal([BaseItemKind.Season], proxy.BrowseQuery.IncludeItemTypes);
        Assert.Contains(proxy.LibraryId, proxy.BrowseQuery.AncestorIds);
        Assert.Equal(25, proxy.BrowseQuery.StartIndex);
        Assert.Equal([season.Id, series.Id], Assert.Single(seasons.Items).ArtworkItemIds);

        host.BrowseItems("Pilot", null, 0, 25, series.Id, true);
        Assert.True(proxy.BrowseQuery!.Recursive);
        Assert.Equal([BaseItemKind.Episode], proxy.BrowseQuery.IncludeItemTypes);

        proxy.CurrentItem = season;
        proxy.BrowseItems = [episode];
        var episodes = host.BrowseItems("", null, 0, 25, season.Id, true);
        Assert.Equal(season.Id, proxy.BrowseQuery!.ParentId);
        Assert.Equal([BaseItemKind.Episode], proxy.BrowseQuery.IncludeItemTypes);
        Assert.Equal(ItemSortBy.IndexNumber, proxy.BrowseQuery.OrderBy.First().Item1);
        Assert.Equal([episode.Id, season.Id, series.Id], Assert.Single(episodes.Items).ArtworkItemIds);
    }

    [Fact]
    public void BrowseItems_UnavailableContainersDoNotBroadenTheQuery()
    {
        var manager = DispatchProxy.Create<ILibraryManager, LibraryManagerProxy>();
        var proxy = (LibraryManagerProxy)(object)manager;
        var host = new JellyfinMetaTaggerHost(manager);
        Assert.Equal("LibraryUnavailable", host.BrowseItems("", Guid.NewGuid(), 0, 25).Status);
        Assert.Equal("ParentUnavailable", host.BrowseItems("", null, 0, 25, Guid.NewGuid(), true).Status);
        proxy.CurrentItem = new Movie { Id = Guid.NewGuid() };
        Assert.Equal("ParentUnavailable", host.BrowseItems("", null, 0, 25, proxy.CurrentItem.Id, true).Status);
        Assert.Null(proxy.BrowseQuery);
    }

    [Fact]
    public async Task UpdateItemTagsAsync_PersistsLatestUnrelatedMetadata()
    {
        var planned = new RecordingMovie { Id = Guid.NewGuid(), Tags = ["manual:keep"], Overview = "Old description" };
        var current = new RecordingMovie { Id = planned.Id, Tags = ["manual:keep"], Overview = "New description" };
        var host = CreateHost(current);

        await host.UpdateItemTagsAsync(planned, ["manual:keep", "meta:genre:drama"], CancellationToken.None);

        Assert.Equal("New description", current.PersistedOverview);
        Assert.NotNull(current.PersistedTags);
        Assert.Equal(["manual:keep", "meta:genre:drama"], current.PersistedTags);
        Assert.Null(planned.PersistedTags);
    }

    [Theory]
    [InlineData("tags")]
    [InlineData("item-lock")]
    [InlineData("tags-lock")]
    public async Task UpdateItemTagsAsync_RejectsTagOrProtectionChangesSincePlanning(string change)
    {
        var planned = new RecordingMovie { Id = Guid.NewGuid(), Tags = ["manual:keep"] };
        var current = new RecordingMovie { Id = planned.Id, Tags = ["manual:keep"] };
        if (change == "tags")
        {
            current.Tags = ["manual:keep", "manual:tagger:skip"];
        }
        else if (change == "item-lock")
        {
            current.IsLocked = true;
        }
        else
        {
            current.LockedFields = [MetadataField.Tags];
        }

        var expectedTags = current.Tags.ToArray();

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateHost(current)
            .UpdateItemTagsAsync(planned, ["manual:keep", "meta:genre:drama"], CancellationToken.None));

        Assert.Null(current.PersistedTags);
        Assert.Equal(expectedTags, current.Tags);
    }

    [Fact]
    public async Task UpdateItemTagsAsync_FailedRepositoryWriteRestoresInMemoryTagsForRetry()
    {
        var planned = new RecordingMovie { Id = Guid.NewGuid(), Tags = ["manual:keep"] };
        var current = new RecordingMovie
        {
            Id = planned.Id,
            Tags = ["manual:keep"],
            WriteFailure = new IOException("Injected repository failure")
        };
        var host = CreateHost(current);

        await Assert.ThrowsAsync<IOException>(() => host
            .UpdateItemTagsAsync(planned, ["manual:keep", "meta:genre:drama"], CancellationToken.None));

        Assert.Equal(["manual:keep"], current.Tags);
        Assert.Null(current.PersistedTags);
        current.WriteFailure = null;
        await host.UpdateItemTagsAsync(planned, ["manual:keep", "meta:genre:drama"], CancellationToken.None);
        Assert.NotNull(current.PersistedTags);
        Assert.Equal(["manual:keep", "meta:genre:drama"], current.PersistedTags);
    }

    [Fact]
    public async Task UpdateItemTagsAsync_WhenAlreadyCanceled_DoesNotStartPersistence()
    {
        var planned = new RecordingMovie { Id = Guid.NewGuid(), Tags = ["manual:keep"] };
        var current = new RecordingMovie { Id = planned.Id, Tags = ["manual:keep"] };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateHost(current)
            .UpdateItemTagsAsync(planned, ["meta:genre:drama"], new CancellationToken(canceled: true)));

        Assert.Equal(["manual:keep"], current.Tags);
        Assert.Null(current.PersistedTags);
    }

    private static JellyfinMetaTaggerHost CreateHost(RecordingMovie current)
    {
        var libraryManager = DispatchProxy.Create<ILibraryManager, LibraryManagerProxy>();
        ((LibraryManagerProxy)(object)libraryManager).CurrentItem = current;
        return new JellyfinMetaTaggerHost(libraryManager);
    }

    public class LibraryManagerProxy : DispatchProxy
    {
        public BaseItem? CurrentItem { get; set; }
        public Guid LibraryId { get; set; }
        public InternalItemsQuery? BrowseQuery { get; set; }
        public BaseItem[]? BrowseItems { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ILibraryManager.GetVirtualFolders))
            {
                return new List<VirtualFolderInfo> { new() { ItemId = LibraryId.ToString("N"), Name = "Movies" } };
            }
            if (targetMethod?.Name == nameof(ILibraryManager.GetItemsResult) && args is [InternalItemsQuery browse])
            {
                BrowseQuery = browse;
                return new MediaBrowser.Model.Querying.QueryResult<BaseItem>
                {
                    Items = BrowseItems ?? [new Movie { Id = Guid.NewGuid(), Name = "Dune", ProductionYear = 2024 }],
                    TotalRecordCount = 60
                };
            }
            if (targetMethod?.Name == nameof(ILibraryManager.GetItemList)
                && args is [InternalItemsQuery query])
            {
                return CurrentItem is { } item && query.ItemIds.Contains(item.Id) ? new BaseItem[] { item } : [];
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private sealed class RecordingMovie : Movie
    {
        public string? PersistedOverview { get; private set; }

        public string[]? PersistedTags { get; private set; }

        public Exception? WriteFailure { get; set; }

        public override Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken)
        {
            if (WriteFailure is not null)
            {
                return Task.FromException(WriteFailure);
            }

            PersistedOverview = Overview;
            PersistedTags = Tags.ToArray();
            return Task.CompletedTask;
        }
    }
}
