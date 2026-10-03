using Jellyfin.Plugin.MetaTagger.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed partial class MetaTaggerRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generation_EligibilityIndexes_AreNotRebuiltForEveryItem(bool authorized)
    {
        const int count = 1000;
        var items = Enumerable.Range(1, count).Select(i => (BaseItem)new Movie
        {
            Id = Guid.Parse(i.ToString("x32")), Genres = ["Drama"]
        }).ToArray();
        var configuration = GenerationPerformanceConfiguration(items, authorized);
        var runner = CreateRunner(new InMemoryMetaTaggerHost(configuration, items),
            new InMemoryMetaTaggerStateStore(new MetaTaggerState()));

        // Every host/store operation completes synchronously, so this thread's allocation
        // count covers the entire run without unrelated parallel tests or a timing threshold.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var run = runner.RunAsync(new NoOpProgress(), CancellationToken.None,
            new MetaTaggerRunOptions { PreviewOnly = true });
        Assert.True(run.IsCompletedSuccessfully);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(count, (await run).ItemsScanned);
        // Rebuilding the 1,000-ID hash sets for each item allocates over 70 MB.
        // Allow ample room for run history, projection, and one set of indexes.
        Assert.InRange(allocated, 1, 40_000_000);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("id")]
    [InlineData("baseline")]
    public async Task Generation_EligibilitySnapshot_RefreshesForEachRun(string change)
    {
        var movie = new Movie { Id = Guid.Parse("abcdef00-0000-0000-0000-000000000001"), Genres = ["Drama"] };
        var configuration = GenerationPerformanceConfiguration([movie], authorized: false);
        var host = new InMemoryMetaTaggerHost(configuration, [movie]);
        var runner = CreateRunner(host, new InMemoryMetaTaggerStateStore(new MetaTaggerState()));
        var first = await runner.RunAsync(new NoOpProgress(), CancellationToken.None);
        Assert.Equal(1, first.ItemsSkippedBaseline);
        Assert.Empty(host.UpdateAttempts);

        var generation = configuration.Installation!.Generation!;
        if (change == "type") { generation.AuthorizedItemTypes = ["Movie"]; }
        if (change == "id") { generation.AuthorizedItemIds = [movie.Id.ToString("N").ToUpperInvariant()]; }
        if (change == "baseline") { generation.BaselineItemIds = []; generation.BaselineItemCount = 0; }
        generation.Revision++;
        var second = await runner.RunAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(0, second.ItemsSkippedBaseline);
        Assert.Equal(1, second.WritesApplied);
        Assert.Equal(["meta:genre:drama"], movie.Tags);
    }

    [Fact]
    public async Task Generation_InvalidBaseline_BlocksEvenAuthorizedTypes()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var configuration = GenerationPerformanceConfiguration([movie], authorized: true);
        configuration.Installation!.Generation!.BaselineItemIds = ["invalid"];
        var host = new InMemoryMetaTaggerHost(configuration, [movie]);
        var runner = CreateRunner(host, new InMemoryMetaTaggerStateStore(new MetaTaggerState()));

        var summary = await runner.RunAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(1, summary.ItemsSkippedEligibilityUnavailable);
        Assert.Empty(host.UpdateAttempts);
        Assert.Empty(movie.Tags);
    }

    private static PluginConfiguration GenerationPerformanceConfiguration(IReadOnlyList<BaseItem> items, bool authorized)
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.PreviewOnly = false;
        configuration.EnableParentalRating = false;
        configuration.EnableAudioLanguages = false;
        configuration.Installation = new InstallationState
        {
            InstallationId = Guid.NewGuid(), Origin = InstallationOrigin.Fresh, MigrationVersion = 1,
            Generation = new GenerationState
            {
                Revision = 1, BaselineComplete = true, BaselineItemCount = items.Count,
                BaselineItemIds = items.Select(item => item.Id.ToString("N").ToUpperInvariant()).ToArray(),
                AuthorizedItemTypes = authorized ? ["Movie"] : [], AuthorizedItemIds = []
            }
        };
        return configuration;
    }
}
