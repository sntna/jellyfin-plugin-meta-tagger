using Jellyfin.Plugin.MetaTagger;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed partial class MetaTaggerRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generation_BaselineQueryTimeLimit_ReportsSelectedItemsRemaining(bool previewOnly)
    {
        var plugin = CreateFreshGenerationPlugin();
        plugin.Configuration.MaxRunMinutes = 1;
        plugin.SaveConfiguration();
        var first = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var second = new Movie { Id = Guid.NewGuid(), Genres = ["Comedy"] };
        var excluded = new Episode { Id = Guid.NewGuid(), Genres = ["Family"] };
        var clock = new ItemApprovalClock();
        var queries = 0;
        var host = new PersistedConfigurationHost(plugin, [first, second, first, excluded])
        {
            BeforeQuery = () =>
            {
                queries++;
                clock.UtcNow += TimeSpan.FromMinutes(2);
            }
        };
        var store = new MetaTaggerStateStore(_directory);
        var runner = CreateRunner(host, store, clock);

        var summary = await runner.RunAsync(new NoOpProgress(), CancellationToken.None,
            new MetaTaggerRunOptions { PreviewOnly = previewOnly });

        Assert.True(summary.BudgetLimitReached);
        Assert.Equal(2, summary.ItemsRemaining);
        Assert.Equal(0, summary.ItemsProcessed);
        Assert.Equal(0, summary.WritesApplied);
        var record = Assert.Single(await store.LoadRunsAsync(CancellationToken.None));
        Assert.Equal("Budget limited", record.Outcome);
        Assert.Equal(2, record.Summary.ItemsRemaining);
        Assert.Equal(0, record.Summary.ItemsProcessed);
        Assert.Equal(1, queries);
        Assert.Equal(previewOnly ? "NotCaptured" : "Incomplete",
            (await runner.GetGenerationStatusAsync(CancellationToken.None)).BaselineStatus);
        Assert.Empty(first.Tags);
        Assert.Empty(second.Tags);
        Assert.Empty(excluded.Tags);
    }
}
