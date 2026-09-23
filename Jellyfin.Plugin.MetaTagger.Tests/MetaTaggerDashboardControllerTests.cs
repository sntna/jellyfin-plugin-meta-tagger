using Jellyfin.Plugin.MetaTagger;
using Jellyfin.Plugin.MetaTagger.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed class MetaTaggerDashboardControllerTests : IDisposable
{
    private const string ConfigurationRevision = "test-configuration-revision";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetLatestPreviewAsync_ReturnsItemAndTagChangesFromLatestPreviewRun()
    {
        var store = new MetaTaggerStateStore(_directory);
        var exportPath = await store.SavePreviewChangesAsync(
            [
                new MetaTaggerPreviewChange
                {
                    ItemId = "item-1",
                    ItemName = "Big Buck Bunny",
                    ItemType = "Movie",
                    AddedTags = ["meta:genre:animation", "meta:genre:comedy"]
                }
            ],
            CancellationToken.None);
        await store.SaveSummaryAsync(
            new MetaTaggerRunSummary
            {
                LastRunUtc = new DateTimeOffset(2026, 8, 19, 21, 33, 34, TimeSpan.Zero),
                RunMode = "FullScan",
                ItemsScanned = 4,
                ItemsChanged = 1,
                TagsAdded = 2,
                EstimatedWrites = 1,
                PreviewOnly = true,
                PreviewChangesExported = 1,
                PreviewChangesExportPath = exportPath,
                ConfigurationRevision = ConfigurationRevision,
                PreviewChangesChecksum = MetaTaggerPreviewChangeChecksum.Compute(
                    [
                        new MetaTaggerPreviewChange
                        {
                            ItemId = "item-1",
                            ItemName = "Big Buck Bunny",
                            ItemType = "Movie",
                            AddedTags = ["meta:genre:animation", "meta:genre:comedy"]
                        }
                    ])
            },
            CancellationToken.None);

        var controller = CreateController(store);

        var result = await controller.GetLatestPreviewAsync(CancellationToken.None);

        Assert.NotNull(result.Summary);
        Assert.True(result.Summary.PreviewOnly);
        Assert.Equal(1, result.Summary.ItemsChanged);
        var change = Assert.Single(result.Changes);
        Assert.Equal("Big Buck Bunny", change.ItemName);
        Assert.Equal(["meta:genre:animation", "meta:genre:comedy"], change.AddedTags);
    }

    [Fact]
    public async Task GetLatestPreviewAsync_ZeroChangePreviewDoesNotReturnStaleExport()
    {
        var store = new MetaTaggerStateStore(_directory);
        await store.SavePreviewChangesAsync(
            [new MetaTaggerPreviewChange { ItemId = "stale-item", AddedTags = ["meta:genre:stale"] }],
            CancellationToken.None);
        await store.SaveSummaryAsync(
            new MetaTaggerRunSummary
            {
                LastRunUtc = new DateTimeOffset(2026, 8, 20, 1, 2, 3, TimeSpan.Zero),
                RunMode = "FullScan",
                ItemsScanned = 4,
                PreviewOnly = true,
                PreviewChangesExported = 0,
                PreviewChangesExportPath = null,
                ConfigurationRevision = ConfigurationRevision
            },
            CancellationToken.None);

        var result = await CreateController(store)
            .GetLatestPreviewAsync(CancellationToken.None);

        Assert.Equal("NoChanges", result.Status);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public async Task GetLatestPreviewAsync_ConfigurationChangedAfterPreview_ReturnsUnavailable()
    {
        var store = new MetaTaggerStateStore(_directory);
        var changes = new[]
        {
            new MetaTaggerPreviewChange { ItemId = "item-1", AddedTags = ["meta:genre:animation"] }
        };
        var exportPath = await store.SavePreviewChangesAsync(changes, CancellationToken.None);
        await store.SaveSummaryAsync(
            new MetaTaggerRunSummary
            {
                PreviewOnly = true,
                PreviewChangesExported = 1,
                PreviewChangesExportPath = exportPath,
                ConfigurationRevision = "preview-revision",
                PreviewChangesChecksum = MetaTaggerPreviewChangeChecksum.Compute(changes)
            },
            CancellationToken.None);

        var controller = new MetaTaggerDashboardController(
            store,
            () => new PluginConfiguration { ConfigurationRevision = "current-revision" });

        var result = await controller.GetLatestPreviewAsync(CancellationToken.None);

        Assert.Equal("Unavailable", result.Status);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public async Task GetLatestPreviewAsync_ExportDoesNotMatchSummary_ReturnsUnavailable()
    {
        var store = new MetaTaggerStateStore(_directory);
        var changes = new[]
        {
            new MetaTaggerPreviewChange { ItemId = "new-run", AddedTags = ["meta:genre:drama"] }
        };
        var exportPath = await store.SavePreviewChangesAsync(changes, CancellationToken.None);
        await store.SaveSummaryAsync(
            new MetaTaggerRunSummary
            {
                PreviewOnly = true,
                PreviewChangesExported = 1,
                PreviewChangesExportPath = exportPath,
                ConfigurationRevision = "same-revision",
                PreviewChangesChecksum = "checksum-from-previous-run"
            },
            CancellationToken.None);

        var controller = new MetaTaggerDashboardController(
            store,
            () => new PluginConfiguration { ConfigurationRevision = "same-revision" });

        var result = await controller.GetLatestPreviewAsync(CancellationToken.None);

        Assert.Equal("Unavailable", result.Status);
        Assert.Empty(result.Changes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static MetaTaggerDashboardController CreateController(MetaTaggerStateStore store)
    {
        return new MetaTaggerDashboardController(
            store,
            () => new PluginConfiguration { ConfigurationRevision = ConfigurationRevision });
    }
}
