using System.Text.Json;
using Jellyfin.Plugin.MetaTagger.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed partial class MetaTaggerRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task History_RestartAfterPreviewOrProtectionRetainsActualRemainingCoverage(bool protectedItem)
    {
        using var cancellation = new CancellationTokenSource();
        var first = new Movie { Id = Guid.Parse("01010101-0101-0101-0101-010101010101"), Genres = ["Drama"],
            Tags = protectedItem ? ["manual:tagger:skip"] : [] };
        var second = new Movie { Id = Guid.Parse("02020202-0202-0202-0202-020202020202"), Genres = ["Comedy"] };
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration { EnableAudioLanguages = false }, [first, second]);
        var disk = new MetaTaggerStateStore(_directory);
        MetaTaggerRunRecord? snapshot = null;
        var progress = new GenerationProgress(() =>
        {
            var reopened = new MetaTaggerStateStore(_directory);
            var entry = reopened.LoadRunsAsync(CancellationToken.None).GetAwaiter().GetResult().Single();
            snapshot = reopened.LoadRunAsync(entry.RunId, CancellationToken.None).GetAwaiter().GetResult();
            cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateRunner(host, disk)
            .RunAsync(progress, cancellation.Token));
        Assert.Equal("Interrupted", snapshot!.Outcome);
        Assert.Equal(1, snapshot.Summary.ItemsScanned);
        Assert.Equal(1, snapshot.Summary.ItemsRemaining);
        Assert.Equal(first.Id.ToString("N"), Assert.Single(snapshot.Items).ItemId);
        Assert.Equal(protectedItem ? "Protected" : "Changes", snapshot.Items[0].Outcome);
        Assert.Empty(host.UpdateAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task History_RestartDuringWriteDelayRetainsConfirmedCoverageAndRetryDoesNotRepeatWrites(bool itemApply)
    {
        using var cancellation = new CancellationTokenSource();
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration { PreviewOnly = false, WriteDelayMilliseconds = 10, EnableAudioLanguages = false }, [movie]);
        var disk = new MetaTaggerStateStore(_directory);
        var clock = new CancellableItemClock();
        var runner = CreateRunner(host, disk, clock);
        var runTask = itemApply ? runner.ApplyItemAsync(movie.Id, cancellation.Token)
            : runner.RunAsync(new NoOpProgress(), cancellation.Token);
        await clock.DelayEntered.Task.WaitAsync(AsyncTestTimeout);
        var reopened = new MetaTaggerStateStore(_directory);
        try
        {
            var entry = Assert.Single(await reopened.LoadRunsAsync(CancellationToken.None));
            var detail = (await reopened.LoadRunAsync(entry.RunId, CancellationToken.None))!;
            Assert.Equal("Interrupted", detail.Outcome);
            Assert.Equal(1, detail.Summary.WritesApplied);
            Assert.Equal(1, detail.Summary.ItemsScanned);
            Assert.Equal(0, detail.Summary.ItemsRemaining);
            Assert.Equal("Confirmed", Assert.Single(detail.Items).WriteOutcome);
            Assert.Equal("Confirmed", detail.Items[0].OwnershipOutcome);
            Assert.Equal(["meta:genre:drama"], detail.Items[0].AddedTags);
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        }
        var retry = CreateRunner(host, reopened);
        var retried = itemApply ? await retry.ApplyItemAsync(movie.Id, CancellationToken.None)
            : await retry.RunAsync(new NoOpProgress(), CancellationToken.None);
        Assert.Equal(0, retried.WritesApplied);
        Assert.Single(host.UpdateAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task History_ApplyDistinguishesConfirmedAndUnconfirmedWritesWithTheirAttemptedChanges(bool failWrite)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["Favorites", "manual:keep", "meta:year:2023"] };
        var configuration = new PluginConfiguration { PreviewOnly = false, EnableAudioLanguages = false, StaleTagMode = StaleTagMode.Remove };
        var host = new InMemoryMetaTaggerHost(configuration, [movie], failWriteItemId: failWrite ? movie.Id.ToString("N") : null);
        var store = new MetaTaggerStateStore(_directory);
        var state = new MetaTaggerState();
        state.Items[movie.Id.ToString("N")] = new MetaTaggerStateItem { LastAppliedTags = ["meta:year:2023"] };
        await store.SaveAsync(state, CancellationToken.None);
        var runner = CreateRunner(host, store);
        var summary = await runner.RunAsync(new NoOpProgress(), CancellationToken.None);
        var detail = (await store.LoadRunAsync(summary.RunId, CancellationToken.None))!;
        var item = Assert.Single(detail.Items);
        Assert.Equal(failWrite ? "Unconfirmed" : "Confirmed", item.WriteOutcome);
        Assert.Equal(failWrite ? "NotApplicable" : "Confirmed", item.OwnershipOutcome);
        Assert.Equal(["meta:genre:drama"], item.AddedTags);
        Assert.Equal(["meta:year:2023"], item.RemovedTags);
        Assert.Equal(["Favorites", "manual:keep"], item.KeptTags);
        Assert.Equal(failWrite ? 0 : 1, detail.Summary.WritesApplied);
        Assert.Equal(failWrite ? "Partial failure" : "Completed", detail.Outcome);
        Assert.Equal(["meta:genre:drama"], Assert.Single(item.SourceExplanations, source => source.Source == "genre").Tags);
    }

    [Fact]
    public async Task History_ItemApplyUsesTheRulesAndRevisionFromItsFinalRevalidation()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var config = new PluginConfiguration { ConfigurationRevision = "initial", EnableAudioLanguages = false };
        var host = new InMemoryMetaTaggerHost(config, [movie]);
        var disk = new MetaTaggerStateStore(_directory);
        var loads = 0;
        var store = new ItemStateBoundary(disk, () =>
        {
            if (++loads == 2)
            {
                config.ConfigurationRevision = "actual";
                config.GeneratedTagPrefix = "actual";
                config.IncludeSeries = false;
            }
        });
        var runner = CreateRunner(host, store);
        await runner.PreviewItemAsync(movie.Id, CancellationToken.None);
        var summary = await runner.ApplyItemAsync(movie.Id, CancellationToken.None);
        var detail = (await disk.LoadRunAsync(summary.RunId, CancellationToken.None))!;
        Assert.Equal("actual", detail.ConfigurationRevision);
        Assert.Equal("actual", detail.RecordedRules!["Generated tag prefix"]);
        Assert.Equal(["Movie"], detail.ItemTypes);
        Assert.Equal(["actual:genre:drama"], Assert.Single(detail.Items).AddedTags);
        Assert.Equal("Confirmed", detail.Items[0].WriteOutcome);
    }

    [Fact]
    public async Task History_BudgetLimitedBackfillRetainsPermissionSeparatelyFromActualCoverage()
    {
        var first = new Movie { Id = Guid.Parse("01010101-0101-0101-0101-010101010101"), Genres = ["Drama"] };
        var second = new Movie { Id = Guid.Parse("02020202-0202-0202-0202-020202020202"), Genres = ["Comedy"] };
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration { MaxItemsPerRun = 1, EnableAudioLanguages = false }, [first, second]);
        var store = new MetaTaggerStateStore(_directory);
        var summary = await CreateRunner(host, store).RunTaskAsync(new NoOpProgress(), CancellationToken.None,
            new MetaTaggerRunOptions { PreviewOnly = false }, authorizeBackfill: true);
        var detail = (await new MetaTaggerStateStore(_directory).LoadRunAsync(summary.RunId, CancellationToken.None))!;
        Assert.Equal("Item types", detail.Summary.BackfillAuthorization);
        Assert.Equal(1, detail.Summary.ItemsScanned);
        Assert.Equal(1, detail.Summary.WritesApplied);
        Assert.Equal(1, detail.Summary.ItemsRemaining);
        Assert.True(detail.Summary.BudgetLimitReached);
        Assert.Equal("Budget limited", detail.Outcome);
        Assert.Equal(first.Id.ToString("N"), Assert.Single(detail.Items).ItemId);
        Assert.Equal("Confirmed", detail.Items[0].WriteOutcome);
    }

    [Fact]
    public async Task History_RetainsRulesKeptTagsAndProposalsAcrossLaterEditsAndRestart()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Recorded movie", Genres = ["Drama"], Tags = ["Favorites", "manual:keep"] };
        var configuration = new PluginConfiguration { EnableAudioLanguages = false, MaxItemsPerRun = 7 };
        var host = new InMemoryMetaTaggerHost(configuration, [movie]);
        var store = new MetaTaggerStateStore(_directory);
        var runner = CreateRunner(host, store);
        var preview = await runner.PreviewItemAsync(movie.Id, CancellationToken.None);
        var run = Assert.Single(await store.LoadRunsAsync(CancellationToken.None));
        configuration.GeneratedTagPrefix = "changed";
        configuration.MaxItemsPerRun = 99;
        movie.Genres = ["Comedy"];
        movie.Name = "Later movie";
        movie.Tags = ["Later tags"];
        preview.AddedTags = [];
        await runner.PreviewItemAsync(movie.Id, CancellationToken.None);

        var detail = (await new MetaTaggerStateStore(_directory).LoadRunAsync(run.RunId, CancellationToken.None))!;
        var json = JsonSerializer.SerializeToElement(detail);
        Assert.True(json.TryGetProperty("RecordedRules", out var rules), "History must retain the rules used by this run.");
        Assert.Equal("meta", rules.GetProperty("Generated tag prefix").GetString());
        Assert.Equal("7", rules.GetProperty("Item limit").GetString());
        Assert.Equal(["Movie", "Series"], detail.ItemTypes);
        var item = Assert.Single(detail.Items);
        Assert.Equal("Recorded movie", item.Name);
        Assert.Equal(["meta:genre:drama"], item.AddedTags);
        Assert.Equal("Proposal", json.GetProperty("Items")[0].GetProperty("WriteOutcome").GetString());
        Assert.Equal(["Favorites", "manual:keep"], json.GetProperty("Items")[0].GetProperty("KeptTags").EnumerateArray().Select(tag => tag.GetString()).Order(StringComparer.Ordinal));
        Assert.Empty(host.UpdateAttempts);
    }
}
