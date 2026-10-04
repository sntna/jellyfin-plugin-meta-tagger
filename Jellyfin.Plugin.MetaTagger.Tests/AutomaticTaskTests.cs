using Jellyfin.Plugin.MetaTagger;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed partial class MetaTaggerRunnerTests
{
    [Fact]
    public void ScheduledTasks_UseMetaTaggerCategoryAndPreserveKeysAndDefaultTriggers()
    {
        var runner = CreateRunner(new InMemoryMetaTaggerHost(ConfigurationWithNoItemTypes()));
        MetaTaggerScheduledTaskBase[] tasks = [new ScheduledTagTask(runner), new PreviewMetadataTagTask(runner),
            new ApplyMetadataTagTask(runner), new ForceFullMetadataTagScanTask(runner), new RebuildMetadataTagLedgerTask(runner)];
        Assert.Equal(["MetaTaggerGenerateTags", "MetaTaggerPreviewTags", "MetaTaggerApplyTags", "MetaTaggerForceFullScan", "MetaTaggerRebuildLedger"],
            tasks.Select(task => task.Key));
        Assert.All(tasks, task => Assert.Equal("Meta Tagger", task.Category));
        var daily = Assert.Single(tasks[0].GetDefaultTriggers());
        Assert.Equal(MediaBrowser.Model.Tasks.TaskTriggerInfoType.IntervalTrigger, daily.Type);
        Assert.Equal(TimeSpan.FromDays(1).Ticks, daily.IntervalTicks);
        Assert.All(tasks.Skip(1), task => Assert.Empty(task.GetDefaultTriggers()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticFreshDefaults_ExcludeBaselineAndApplyLaterAdditions(bool postScan)
    {
        var plugin = CreatePersistedPlugin();
        var existing = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["manual:keep"] };
        var items = new List<BaseItem> { existing };
        var clock = new ItemApprovalClock();
        var store = new MetaTaggerStateStore(_directory);
        async Task Run()
        {
            var runner = CreateRunner(new PersistedConfigurationHost(plugin, items), store, clock);
            if (postScan) { await new LibraryPostScanTask(runner, store, clock).Run(new NoOpProgress(), CancellationToken.None); }
            else { await new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None); }
        }

        await Run();
        plugin = CreatePersistedPlugin();
        Assert.True(plugin.Configuration.Installation!.Generation!.BaselineComplete);
        Assert.Equal(["manual:keep"], existing.Tags);
        var added = new Movie { Id = Guid.NewGuid(), Genres = ["Comedy"] };
        items.Add(added);
        clock.UtcNow += TimeSpan.FromMinutes(30);
        await Run();

        Assert.Equal(["manual:keep"], existing.Tags);
        Assert.Contains("meta:genre:comedy", added.Tags);
        Assert.False((await store.LoadSummaryAsync(CancellationToken.None))!.PreviewOnly);
    }

    [Fact]
    public async Task PostScanCooldown_RetainsExplicitSkipWithoutExtendingCooldownOrChangingTags()
    {
        var plugin = CreatePersistedPlugin();
        var clock = new ItemApprovalClock();
        var store = new MetaTaggerStateStore(_directory);
        var items = new List<BaseItem> { new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] } };
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, items), store, clock);
        var task = new LibraryPostScanTask(runner, store, clock);
        await task.Run(new NoOpProgress(), CancellationToken.None);
        var completed = await store.LoadSummaryAsync(CancellationToken.None);
        var added = new Movie { Id = Guid.NewGuid(), Genres = ["Comedy"] };
        items.Add(added);
        clock.UtcNow += TimeSpan.FromMinutes(10);
        await task.Run(new NoOpProgress(), CancellationToken.None);

        var skip = (await new MetaTaggerStateStore(_directory).LoadRunsAsync(CancellationToken.None))[0];
        Assert.Equal("Skipped: cooldown", skip.Outcome);
        Assert.Equal("PostScan", skip.Invocation);
        Assert.Equal("Automatic run", skip.Operation);
        Assert.Equal(0, skip.Summary.ItemsScanned);
        Assert.Equal(0, skip.Summary.WritesApplied);
        Assert.Equal(completed!.RunId, (await store.LoadSummaryAsync(CancellationToken.None))!.RunId);
        Assert.Empty(added.Tags);

        clock.UtcNow += TimeSpan.FromMinutes(20);
        await task.Run(new NoOpProgress(), CancellationToken.None);
        Assert.Contains("meta:genre:comedy", added.Tags);
        Assert.Equal("Completed", (await store.LoadRunsAsync(CancellationToken.None))[0].Outcome);
    }
}
