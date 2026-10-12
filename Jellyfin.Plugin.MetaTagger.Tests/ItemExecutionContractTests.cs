using Jellyfin.Plugin.MetaTagger.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed partial class MetaTaggerRunnerTests
{
    [Theory]
    [InlineData(false, MetadataTagRunMode.FullScan)]
    [InlineData(true, MetadataTagRunMode.FullScan)]
    [InlineData(false, MetadataTagRunMode.RebuildTrackingLedger)]
    public async Task LibraryExecution_BuffersOwnershipWithoutMediaWrites(bool preview, MetadataTagRunMode mode)
    {
        var first = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["meta:genre:drama"] };
        var second = new Movie { Id = Guid.NewGuid(), Genres = ["Comedy"], Tags = ["meta:genre:comedy"] };
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration(), [first, second]);
        var store = new InMemoryMetaTaggerStateStore(new MetaTaggerState());

        var summary = await CreateRunner(host, store).RunAsync(new NoOpProgress(), CancellationToken.None,
            new MetaTaggerRunOptions { PreviewOnly = preview, RunMode = mode });

        Assert.Equal("Completed", summary.Outcome);
        Assert.Empty(host.UpdateAttempts);
        Assert.Equal(2, store.SaveAttempts.Count);
        Assert.Empty(store.SaveAttempts[0].State.Items);
        var saved = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(["meta:genre:drama"], saved.Items[first.Id.ToString("N")].LastAppliedTags);
        Assert.Equal(["meta:genre:comedy"], saved.Items[second.Id.ToString("N")].LastAppliedTags);
        Assert.Empty(saved.RunCursors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ItemObservation_DoesNotAcquireExecutionOwnershipEffects(bool preview)
    {
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["meta:genre:drama"] };
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration(), [item]);
        var store = new MetaTaggerStateStore(_directory);
        var runner = CreateRunner(host, store);

        var inspection = preview ? await runner.PreviewItemAsync(item.Id, CancellationToken.None)
            : await runner.InspectItemAsync(item.Id, null, CancellationToken.None);

        Assert.Equal("Up to date", inspection.Status);
        Assert.Empty(host.UpdateAttempts);
        Assert.Empty((await store.LoadAsync(CancellationToken.None)).Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_UsesItsRequiredSettingsSnapshotAfterStoragePreflight(bool singleItem)
    {
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Studios = ["A24"] };
        var original = new PluginConfiguration { ConfigurationRevision = "initial", PreviewOnly = false };
        var host = new InMemoryMetaTaggerHost(original, [item]);
        var changed = new PluginConfiguration
        {
            ConfigurationRevision = "changed", PreviewOnly = false, EnableGenres = false, EnableStudios = true
        };
        var store = new MetaTaggerStateStore(_directory, new StateSavedAction(() => host.ReplaceConfiguration(changed)));
        var runner = CreateRunner(host, store);

        var summary = singleItem ? await runner.ApplyItemAsync(item.Id, CancellationToken.None)
            : await runner.RunAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(1, summary.WritesApplied);
        Assert.Equal(singleItem ? ["meta:studio:a24"] : ["meta:genre:drama"], item.Tags);
        Assert.Equal(singleItem ? "changed" : "initial", summary.ConfigurationRevision);
        var detail = await store.LoadRunAsync(summary.RunId, CancellationToken.None);
        Assert.Equal(summary.ConfigurationRevision, detail!.ConfigurationRevision);
        if (singleItem)
        {
            Assert.Equal([item.Id.ToString("N")], host.CurrentConfiguration.Installation!.Generation!.AuthorizedItemIds!);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_CancellationAfterSavedTagsCannotInterruptOwnershipAndCursorCheckpoint(bool singleItem)
    {
        using var cancellation = new CancellationTokenSource();
        var first = new Movie { Id = Guid.Parse("01010101-0101-0101-0101-010101010101"), Genres = ["Drama"] };
        var second = new Movie { Id = Guid.Parse("02020202-0202-0202-0202-020202020202"), Genres = ["Comedy"] };
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration { PreviewOnly = false, WriteDelayMilliseconds = 10 }, [first, second]);
        host.AfterWrite = cancellation.Cancel;
        var store = new MetaTaggerStateStore(_directory);
        var runner = CreateRunner(host, store);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => singleItem
            ? runner.ApplyItemAsync(first.Id, cancellation.Token)
            : runner.RunAsync(new NoOpProgress(), cancellation.Token));

        var saved = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(["meta:genre:drama"], saved.Items[first.Id.ToString("N")].LastAppliedTags);
        Assert.Empty(second.Tags);
        if (!singleItem) { Assert.Equal([second.Id.ToString("N")], PendingIds(Assert.Single(saved.RunCursors).Value)); }
        var run = Assert.Single(await store.LoadRunsAsync(CancellationToken.None));
        var detail = await store.LoadRunAsync(run.RunId, CancellationToken.None);
        Assert.Equal("Cancelled", run.Outcome);
        Assert.Equal(1, run.Summary.WritesApplied);
        Assert.Equal(singleItem ? 0 : 1, run.Summary.ItemsRemaining);
        Assert.Equal("Confirmed", Assert.Single(detail!.Items).WriteOutcome);
        Assert.Equal("Confirmed", detail.Items[0].OwnershipOutcome);
    }

    private sealed class StateSavedAction(Action onFirstSave) : IMetaTaggerStateFilePromoter
    {
        private bool _saved;
        public void Promote(string temporary, string destination)
        {
            File.Move(temporary, destination, overwrite: true);
            if (!_saved && destination.EndsWith("meta-tagger-state.json", StringComparison.Ordinal))
            {
                _saved = true;
                onFirstSave();
            }
        }
    }
}
