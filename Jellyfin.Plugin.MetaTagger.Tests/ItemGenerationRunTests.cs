using Jellyfin.Plugin.MetaTagger;
using Jellyfin.Plugin.MetaTagger.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed partial class MetaTaggerRunnerTests
{
    [Fact]
    public async Task ItemRun_QueuedTimeDoesNotConsumeExecutionBudget()
    {
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration { MaxRunMinutes = 1 }, [item]);
        var store = new BlockingLoadMetaTaggerStateStore();
        var clock = new ItemApprovalClock();
        var runner = CreateRunner(host, store, clock);
        var occupyingRun = runner.RunAsync(new NoOpProgress(), CancellationToken.None);
        await store.FirstLoadEntered.WaitAsync(AsyncTestTimeout);
        try
        {
            var queued = runner.StartItemApply(item.Id);
            Assert.Equal("Queued", queued.State);
            clock.UtcNow += TimeSpan.FromMinutes(2);
            store.ReleaseLoads();
            var completed = await WaitForItemRun(runner, queued.RunId);
            Assert.Equal(1, completed.Summary!.WritesApplied);
            Assert.Equal("Completed", completed.State);
            Assert.Contains("meta:genre:drama", item.Tags);
        }
        finally { store.ReleaseLoads(); await occupyingRun.WaitAsync(AsyncTestTimeout); }
    }

    [Fact]
    public async Task ItemRun_CancelsQueuedWorkWithoutWritingAndRejectsDuplicateStarts()
    {
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration(), [item]);
        var store = new BlockingLoadMetaTaggerStateStore();
        var runner = CreateRunner(host, store);
        var occupyingRun = runner.RunAsync(new NoOpProgress(), CancellationToken.None);
        await store.FirstLoadEntered.WaitAsync(AsyncTestTimeout);
        try
        {
            var queued = runner.StartItemApply(item.Id);
            Assert.Throws<InvalidOperationException>(() => runner.StartItemApply(item.Id));
            Assert.True(runner.CancelItemRun(queued.RunId));
            var cancelled = await WaitForItemRun(runner, queued.RunId);
            Assert.Equal("Cancelled", cancelled.State);
            Assert.Equal(0, cancelled.Summary!.WritesApplied);
            Assert.Empty(host.UpdateAttempts);
            Assert.False(runner.CancelItemRun(queued.RunId));
        }
        finally { store.ReleaseLoads(); await occupyingRun.WaitAsync(AsyncTestTimeout); }
    }

    [Fact]
    public async Task ItemRun_ReportsCurrentWritesAndOldCancellationCannotStopANewRun()
    {
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var runner = CreateRunner(new InMemoryMetaTaggerHost(new PluginConfiguration(), [item]));
        var first = runner.StartItemApply(item.Id);
        var completed = await WaitForItemRun(runner, first.RunId);
        Assert.Equal("Completed", completed.State);
        Assert.Equal(1, completed.Summary!.WritesApplied);
        item.Genres = ["Comedy"];
        var second = runner.StartItemApply(item.Id);
        Assert.False(runner.CancelItemRun(first.RunId));
        Assert.Equal("Completed", (await WaitForItemRun(runner, second.RunId)).State);
        Assert.Contains("meta:genre:comedy", item.Tags);
    }

    [Fact]
    public async Task ItemRun_ControllerCanCancelRunningWorkAndReportsConfirmedWrites()
    {
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var config = new PluginConfiguration();
        var host = new InMemoryMetaTaggerHost(config, [item]);
        var store = new MetaTaggerStateStore(_directory);
        var clock = new CancellableItemClock();
        var runner = CreateRunner(host, store, clock);
        var controller = new MetaTaggerDashboardController(store, () => config, runner);
        var accepted = Assert.IsType<Microsoft.AspNetCore.Mvc.AcceptedResult>(controller.StartItemApply(item.Id).Result);
        var run = Assert.IsType<MetaTaggerItemRun>(accepted.Value);
        await clock.DelayEntered.Task.WaitAsync(AsyncTestTimeout);
        Assert.Equal("Running", controller.GetItemRun(run.RunId).Value!.State);
        Assert.IsType<Microsoft.AspNetCore.Mvc.AcceptedResult>(controller.CancelItemRun(run.RunId));
        var stopped = await WaitForItemRun(runner, run.RunId);
        Assert.Equal("Cancelled", stopped.State);
        Assert.Equal(1, stopped.Summary!.WritesApplied);
        Assert.Equal(["meta:genre:drama"], (await store.LoadAsync(CancellationToken.None)).Items[item.Id.ToString("N")].LastAppliedTags);
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(controller.CancelItemRun(run.RunId));
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(controller.GetItemRun(Guid.NewGuid()).Result);
    }

    [Fact]
    public async Task ItemRun_RecoveryAndCancellationUseNewestRunBeyondRetentionLimit()
    {
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var runner = CreateRunner(new InMemoryMetaTaggerHost(new PluginConfiguration(), [item]));
        Guid firstId = default;
        for (var index = 0; index < 22; index++)
        {
            var run = runner.StartItemApply(item.Id);
            if (index == 0) { firstId = run.RunId; }
            try { Assert.Equal(run.RunId, runner.GetCurrentItemRun()!.RunId); }
            finally { await WaitForItemRun(runner, run.RunId); }
        }
        Assert.Null(runner.GetItemRun(firstId));
    }

    private sealed class CancellableItemClock : IMetaTaggerClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        internal TaskCompletionSource<bool> DelayEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            DelayEntered.TrySetResult(true);
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private static async Task<MetaTaggerItemRun> WaitForItemRun(MetaTaggerRunner runner, Guid id)
    {
        using var timeout = new CancellationTokenSource(AsyncTestTimeout);
        while (true)
        {
            var run = runner.GetItemRun(id)!;
            if (run.Summary is not null) { return run; }
            await Task.Delay(10, timeout.Token);
        }
    }
}
