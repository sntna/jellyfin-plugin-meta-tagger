using Jellyfin.Plugin.MetaTagger;
using Jellyfin.Plugin.MetaTagger.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed partial class MetaTaggerRunnerTests
{
    [Fact]
    public async Task DashboardDuringRun_ExampleAndInspectionDoNotWaitForALibraryRun()
    {
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["favorite"] };
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration { IncludeMovies = true }, [item]);
        var store = new HoldNextLoadStateStore(new MetaTaggerStateStore(_directory));
        var runner = CreateRunner(host, store);
        var run = await StartHeldRun(store, () => runner.RunAsync(new NoOpProgress(), CancellationToken.None));
        try
        {
            var example = await runner.InspectItemAsync(item.Id, new PluginConfiguration { EnableGenres = true },
                CancellationToken.None).WaitAsync(AsyncTestTimeout);
            var inspection = await runner.InspectItemAsync(item.Id, null, CancellationToken.None).WaitAsync(AsyncTestTimeout);
            var status = await runner.GetGenerationStatusAsync(CancellationToken.None).WaitAsync(AsyncTestTimeout);

            Assert.Contains("meta:genre:drama", example.GeneratedTags);
            Assert.Equal("Changes", inspection.Status);
            Assert.NotNull(status.BaselineStatus);
            Assert.False(run.IsCompleted);
            Assert.Empty(host.UpdateAttempts);
        }
        finally
        {
            store.Release();
            await run.WaitAsync(AsyncTestTimeout);
        }
    }

    [Fact]
    public async Task DashboardDuringRun_ItemCheckReportsRunInProgressWithoutRecordingARun()
    {
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration { IncludeMovies = true }, [item]);
        var store = new HoldNextLoadStateStore(new MetaTaggerStateStore(_directory));
        var runner = CreateRunner(host, store);
        var run = await StartHeldRun(store, () => runner.RunAsync(new NoOpProgress(), CancellationToken.None));
        var runsBeforeCheck = store.SavedRunIds.Count;
        try
        {
            var check = await runner.PreviewItemAsync(item.Id, CancellationToken.None).WaitAsync(AsyncTestTimeout);

            Assert.Equal("RunInProgress", check.Status);
            Assert.Equal(item.Id, check.ItemId);
            Assert.Contains("in progress", check.Reason, StringComparison.Ordinal);
            Assert.Empty(check.AddedTags);
            Assert.Equal(runsBeforeCheck, store.SavedRunIds.Count);
        }
        finally
        {
            store.Release();
            await run.WaitAsync(AsyncTestTimeout);
        }

        Assert.Equal("Changes", (await runner.PreviewItemAsync(item.Id, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task DashboardDuringRun_TagRemovalPreviewReportsRunInProgressWithoutAToken()
    {
        var item = new Movie { Id = Guid.NewGuid(), Tags = ["meta:year:2024", "favorite"] };
        var store = new HoldNextLoadStateStore(new MetaTaggerStateStore(_directory));
        await RecordGeneratedTags(store, item, "meta:year:2024");
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration { IncludeMovies = true }, [item]);
        var runner = CreateRunner(host, store);
        var run = await StartHeldRun(store, () => runner.RunAsync(new NoOpProgress(), CancellationToken.None));
        try
        {
            var preview = await runner.PreviewCleanupAsync(item.Id, new NoOpProgress(), CancellationToken.None)
                .WaitAsync(AsyncTestTimeout);

            Assert.Null(preview.Token);
            Assert.Empty(preview.Changes);
            Assert.Equal("Run in progress", preview.Summary.Outcome);
            Assert.True(preview.Summary.PreviewOnly);
        }
        finally
        {
            store.Release();
            await run.WaitAsync(AsyncTestTimeout);
        }

        Assert.NotNull((await runner.PreviewCleanupAsync(item.Id, new NoOpProgress(), CancellationToken.None)).Token);
    }

    [Fact]
    public async Task DashboardDuringRun_TagRemovalReportsRunInProgressAndKeepsTheReviewedPreview()
    {
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["meta:year:2024", "favorite"] };
        var other = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var store = new HoldNextLoadStateStore(new MetaTaggerStateStore(_directory));
        await RecordGeneratedTags(store, item, "meta:year:2024");
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration { IncludeMovies = true }, [item, other]);
        var runner = CreateRunner(host, store);
        var preview = await runner.PreviewCleanupAsync(item.Id, new NoOpProgress(), CancellationToken.None);
        Assert.NotNull(preview.Token);
        var check = await StartHeldRun(store, () => runner.PreviewItemAsync(other.Id, CancellationToken.None));
        try
        {
            var blocked = await runner.ApplyCleanupAsync(preview.Token!, new NoOpProgress(), CancellationToken.None)
                .WaitAsync(AsyncTestTimeout);

            Assert.Equal("Run in progress", blocked.Outcome);
            Assert.Equal(0, blocked.WritesApplied);
            Assert.Empty(host.UpdateAttempts);
            Assert.Equal(["meta:year:2024", "favorite"], item.Tags);
        }
        finally
        {
            store.Release();
            await check.WaitAsync(AsyncTestTimeout);
        }

        var applied = await runner.ApplyCleanupAsync(preview.Token!, new NoOpProgress(), CancellationToken.None);
        Assert.Equal(1, applied.WritesApplied);
        Assert.Equal(["favorite"], item.Tags);
    }

    private static async Task<Task<T>> StartHeldRun<T>(HoldNextLoadStateStore store, Func<Task<T>> start)
    {
        store.HoldNextLoad();
        var run = start();
        await store.LoadHeld.WaitAsync(AsyncTestTimeout);
        return run;
    }

    private static async Task RecordGeneratedTags(IMetaTaggerStateStore store, Movie item, params string[] tags)
    {
        var state = new MetaTaggerState();
        state.Items[item.Id.ToString("N")] = new MetaTaggerStateItem { LastAppliedTags = tags };
        await store.SaveAsync(state, CancellationToken.None);
    }

    private sealed class HoldNextLoadStateStore(MetaTaggerStateStore inner) : IMetaTaggerStateStore
    {
        private TaskCompletionSource<bool> _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _holdNext;

        public Task LoadHeld => _held.Task;

        public List<Guid> SavedRunIds { get; } = [];

        public void HoldNextLoad() => Volatile.Write(ref _holdNext, 1);

        public void Release() => _release.TrySetResult(true);

        public async Task<MetaTaggerState> LoadAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _holdNext, 0) == 1)
            {
                _held.TrySetResult(true);
                await _release.Task.WaitAsync(cancellationToken);
            }

            return await inner.LoadAsync(cancellationToken);
        }

        public Task SaveAsync(MetaTaggerState state, CancellationToken cancellationToken)
            => inner.SaveAsync(state, cancellationToken);

        public Task SaveRunAsync(MetaTaggerRunRecord record, CancellationToken cancellationToken)
        {
            lock (SavedRunIds)
            {
                if (!SavedRunIds.Contains(record.RunId)) { SavedRunIds.Add(record.RunId); }
            }
            return Task.CompletedTask;
        }

        public Task SaveSummaryAsync(MetaTaggerRunSummary summary, CancellationToken cancellationToken)
            => inner.SaveSummaryAsync(summary, cancellationToken);

        public Task<string> SavePreviewChangesAsync(IReadOnlyCollection<MetaTaggerPreviewChange> changes,
            CancellationToken cancellationToken)
            => inner.SavePreviewChangesAsync(changes, cancellationToken);
    }
}
