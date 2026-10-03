using Jellyfin.Plugin.MetaTagger;
using Jellyfin.Plugin.MetaTagger.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using System.Reflection;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed partial class MetaTaggerRunnerTests
{
    [Fact]
    public async Task Generation_PartialPreview_StartsANewCycleWhenAuthorizationChanges()
    {
        var plugin = CreateFreshGenerationPlugin();
        plugin.Configuration.MaxItemsPerRun = 1;
        plugin.SaveConfiguration();
        var first = new Movie { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Genres = ["Drama"] };
        var second = new Movie { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Genres = ["Comedy"] };
        var store = new MetaTaggerStateStore(_directory);
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, [first, second]), store);
        await new PreviewMetadataTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        var preview = await runner.PreviewItemAsync(second.Id, CancellationToken.None);
        await runner.ApplyItemAsync(second.Id, preview.Token!, CancellationToken.None);
        await new PreviewMetadataTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);

        var entry = (await store.LoadRunsAsync(CancellationToken.None)).First();
        var run = await store.LoadRunAsync(entry.RunId, CancellationToken.None);
        Assert.NotNull(run);
        Assert.Equal(first.Id.ToString("N"), Assert.Single(run.Items).ItemId);
        Assert.Equal(1, run.Summary.ItemsRemaining);
    }

    [Fact]
    public async Task Generation_QueuedApplyTask_CapturesTheSavedScopeInsideTheCoordinationGate()
    {
        var plugin = CreateFreshGenerationPlugin();
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var episode = new Episode { Id = Guid.NewGuid(), Genres = ["Comedy"] };
        var store = new BlockingLoadMetaTaggerStateStore();
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, [movie, episode]), store);
        var automatic = new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        await store.FirstLoadEntered.WaitAsync(AsyncTestTimeout);
        var apply = new ApplyMetadataTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        plugin.Configuration.IncludeMovies = false;
        plugin.Configuration.IncludeEpisodes = true;
        plugin.SaveConfiguration();
        store.ReleaseLoads();
        await automatic.WaitAsync(AsyncTestTimeout);
        await apply.WaitAsync(AsyncTestTimeout);

        Assert.Empty(movie.Tags);
        Assert.Equal(["meta:genre:comedy"], episode.Tags);
        Assert.Equal(["Episode"], (await runner.GetGenerationStatusAsync(CancellationToken.None)).AuthorizedItemTypes);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("scope")]
    [InlineData("lock")]
    [InlineData("persistence")]
    public async Task Generation_RejectedSingleItemApply_CannotPublishAuthorization(string failure)
    {
        var serializer = new PersistedXmlSerializer();
        var plugin = CreateFreshGenerationPlugin(serializer);
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, [movie]));
        var preview = await runner.PreviewItemAsync(movie.Id, CancellationToken.None);
        if (failure == "scope")
        {
            plugin.Configuration.IncludeMovies = false;
            plugin.SaveConfiguration();
        }
        if (failure == "lock") { movie.IsLocked = true; }
        if (failure == "persistence") { serializer.FailWrites = true; }
        var apply = runner.ApplyItemAsync(movie.Id, failure == "token" ? "invalid" : preview.Token!, CancellationToken.None);
        if (failure == "persistence") { await Assert.ThrowsAsync<IOException>(() => apply); }
        else { await Assert.ThrowsAsync<InvalidOperationException>(() => apply); }
        serializer.FailWrites = false;

        Assert.Empty(movie.Tags);
        Assert.Empty((await runner.GetGenerationStatusAsync(CancellationToken.None)).AuthorizedItemIds);
        plugin = CreatePersistedPlugin();
        runner = CreateRunner(new PersistedConfigurationHost(plugin, [movie]));
        Assert.Empty((await runner.GetGenerationStatusAsync(CancellationToken.None)).AuthorizedItemIds);
    }

    [Fact]
    public async Task Generation_OwnershipCheckpointFailure_ReportsTheConfirmedWriteAndRetainedAuthorization()
    {
        var plugin = CreateFreshGenerationPlugin();
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var store = new MetaTaggerStateStore(_directory, new ItemPublicationFailure(failCheckpoint: true));
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, [movie]), store);
        await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(new NoOpProgress(), CancellationToken.None,
            new MetaTaggerRunOptions { PreviewOnly = false }));
        var entry = Assert.Single(await store.LoadRunsAsync(CancellationToken.None));
        var run = await store.LoadRunAsync(entry.RunId, CancellationToken.None);
        Assert.NotNull(run);
        Assert.Equal("Uncertain", run.Outcome);
        Assert.Equal(1, run.Summary.WritesApplied);
        Assert.Equal(["meta:genre:drama"], Assert.Single(run.Items).AddedTags);
        Assert.Equal(["meta:genre:drama"], movie.Tags);
        Assert.Equal(["Movie"], (await runner.GetGenerationStatusAsync(CancellationToken.None)).AuthorizedItemTypes);
    }

    [Fact]
    public async Task Generation_TruncatedPersistedBaseline_FailsInitializationWithoutPublishingPermission()
    {
        var plugin = CreateFreshGenerationPlugin();
        var first = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var second = new Movie { Id = Guid.NewGuid(), Genres = ["Comedy"] };
        await new ScheduledTagTask(CreateRunner(new PersistedConfigurationHost(plugin, [first, second])))
            .ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        var path = Path.Combine(_directory, "configuration", "Jellyfin.Plugin.MetaTagger.xml");
        var serializer = new PersistedXmlSerializer();
        var saved = (PluginConfiguration)serializer.DeserializeFromFile(typeof(PluginConfiguration), path);
        saved.Installation!.Generation!.BaselineItemIds = [first.Id.ToString("N")];
        serializer.SerializeToFile(saved, path);
        var original = File.ReadAllText(path);

        Assert.Throws<InvalidDataException>(() => CreatePersistedPlugin());
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Empty(first.Tags);
        Assert.Empty(second.Tags);
    }

    [Fact]
    public async Task Generation_JellyfinHost_PreservesProtectionsAndIsolatesFailuresWithoutGrantingCleanupOwnership()
    {
        var plugin = CreateFreshGenerationPlugin();
        plugin.Configuration.StaleTagMode = StaleTagMode.Remove;
        plugin.SaveConfiguration();
        var failed = new MigrationRecordingMovie { Id = Guid.NewGuid(), Genres = ["Drama"], FailWrites = true };
        var healthy = new MigrationRecordingMovie
        {
            Id = Guid.NewGuid(), Genres = ["Comedy"], Tags = ["manual:keep", "Favorites", "meta:genre:legacy"]
        };
        var locked = new MigrationRecordingMovie { Id = Guid.NewGuid(), Genres = ["Family"], IsLocked = true };
        var manual = new MigrationRecordingMovie { Id = Guid.NewGuid(), Genres = ["Family"], Tags = ["manual:tagger:skip"] };
        var library = DispatchProxy.Create<ILibraryManager, MigrationLibraryManager>();
        ((MigrationLibraryManager)(object)library).Items = [failed, healthy, locked, manual];
        var media = DispatchProxy.Create<IMediaSourceManager, MigrationMediaSourceManager>();
        var runner = CreateRunner(new JellyfinMetaTaggerHost(library, media));

        await new ApplyMetadataTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        var store = new MetaTaggerStateStore(_directory);
        var summary = await store.LoadSummaryAsync(CancellationToken.None);
        Assert.Equal(1, summary!.WritesApplied);
        Assert.Equal(1, summary.Failures);
        Assert.Equal(1, summary.ItemsSkippedLocked);
        Assert.Equal(1, summary.ItemsSkippedManual);
        Assert.Equal("Partial failure", summary.Outcome);
        Assert.Equal(["manual:keep", "Favorites", "meta:genre:legacy", "meta:genre:comedy"], healthy.Tags);
        Assert.Empty(failed.Tags);
        Assert.Empty(locked.Tags);
        Assert.Equal(["manual:tagger:skip"], manual.Tags);
        var cleanup = await runner.PreviewCleanupAsync(healthy.Id, new NoOpProgress(), CancellationToken.None);
        Assert.Equal(["meta:genre:comedy"], Assert.Single(cleanup.Changes).RemovedTags);

        failed.FailWrites = false;
        plugin = CreatePersistedPlugin();
        runner = CreateRunner(new JellyfinMetaTaggerHost(library, media));
        await new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        Assert.Equal(["meta:genre:drama"], failed.Tags);
        Assert.Empty(locked.Tags);
        Assert.Equal(["manual:tagger:skip"], manual.Tags);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("items")]
    [InlineData("time")]
    public async Task Generation_PartialApply_RetainsAuthorizationAndReportsActualCoverage(string stop)
    {
        var plugin = CreateFreshGenerationPlugin();
        plugin.Configuration.MaxItemsPerRun = stop == "items" ? 1 : 0;
        plugin.Configuration.MaxRunMinutes = stop == "time" ? 1 : 0;
        plugin.SaveConfiguration();
        var first = new Movie { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Genres = ["Drama"] };
        var second = new Movie { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Genres = ["Comedy"] };
        var clock = new ItemApprovalClock();
        using var cancellation = new CancellationTokenSource();
        IProgress<double> progress = stop == "cancelled" ? new CancelOnReportProgress(cancellation)
            : stop == "time" ? new GenerationProgress(() => clock.UtcNow += TimeSpan.FromMinutes(2)) : new NoOpProgress();
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, [first, second]), clock: clock);
        var run = runner.RunAsync(progress, cancellation.Token, new MetaTaggerRunOptions { PreviewOnly = false });
        if (stop == "cancelled") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
        else { await run; }
        var store = new MetaTaggerStateStore(_directory);
        var record = Assert.Single(await store.LoadRunsAsync(CancellationToken.None));
        Assert.Equal(stop == "cancelled" ? "Cancelled" : "Budget limited", record.Outcome);
        Assert.Equal(1, record.Summary.WritesApplied);
        Assert.Equal(1, record.Summary.ItemsProcessed);
        Assert.Equal(1, record.Summary.ItemsRemaining);
        Assert.Equal("Item types", record.Summary.BackfillAuthorization);
        Assert.Equal(["Movie"], record.Summary.AuthorizedItemTypes);
        Assert.Empty(second.Tags);

        plugin = CreatePersistedPlugin();
        plugin.Configuration.MaxItemsPerRun = 0;
        plugin.Configuration.MaxRunMinutes = 0;
        plugin.SaveConfiguration();
        runner = CreateRunner(new PersistedConfigurationHost(plugin, [first, second]));
        await new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        Assert.Equal(["meta:genre:drama"], first.Tags);
        Assert.Equal(["meta:genre:comedy"], second.Tags);
    }

    private sealed class GenerationProgress(Action action) : IProgress<double>
    {
        public void Report(double value) => action();
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("cancelled")]
    [InlineData("time-limit")]
    public async Task Generation_InterruptedCapture_CannotTreatTheLibraryAsEmpty(string failure)
    {
        var plugin = CreateFreshGenerationPlugin();
        plugin.Configuration.MaxRunMinutes = 1;
        plugin.SaveConfiguration();
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var clock = new ItemApprovalClock();
        using var cancellation = new CancellationTokenSource();
        var host = new PersistedConfigurationHost(plugin, [movie])
        {
            BeforeQuery = () =>
            {
                if (failure == "unavailable") { throw new IOException("Injected library lookup failure."); }
                if (failure == "cancelled") { cancellation.Cancel(); }
                if (failure == "time-limit") { clock.UtcNow += TimeSpan.FromMinutes(2); }
            }
        };
        var runner = CreateRunner(host, clock: clock);
        var run = new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), cancellation.Token);
        if (failure == "unavailable") { await Assert.ThrowsAsync<IOException>(() => run); }
        else if (failure == "cancelled") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
        else { await run; }
        Assert.Empty(movie.Tags);
        Assert.Equal("NotCaptured", (await runner.GetGenerationStatusAsync(CancellationToken.None)).BaselineStatus);
        var record = Assert.Single(await new MetaTaggerStateStore(_directory).LoadRunsAsync(CancellationToken.None));
        Assert.Equal(failure == "cancelled" ? "Cancelled" : failure == "time-limit" ? "Budget limited" : "Failed", record.Outcome);
        Assert.Equal(0, record.Summary.ItemsProcessed);
        Assert.Equal(0, record.Summary.WritesApplied);

        plugin = CreatePersistedPlugin();
        runner = CreateRunner(new PersistedConfigurationHost(plugin, [movie]));
        await new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        Assert.Empty(movie.Tags);
        Assert.Equal("Complete", (await runner.GetGenerationStatusAsync(CancellationToken.None)).BaselineStatus);
    }

    [Fact]
    public async Task Generation_CancelledApplyWaiter_GrantsNothingAndSuccessfulRetryUsesTheCapturedBaseline()
    {
        var plugin = CreateFreshGenerationPlugin();
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var store = new BlockingLoadMetaTaggerStateStore();
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, [movie]), store);
        var automatic = new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        await store.FirstLoadEntered.WaitAsync(AsyncTestTimeout);
        using var cancellation = new CancellationTokenSource();
        var waiting = runner.RunAsync(new NoOpProgress(), cancellation.Token, new MetaTaggerRunOptions { PreviewOnly = false });
        cancellation.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting); }
        finally { store.ReleaseLoads(); }
        await automatic.WaitAsync(AsyncTestTimeout);
        Assert.Empty(movie.Tags);
        Assert.Empty((await runner.GetGenerationStatusAsync(CancellationToken.None)).AuthorizedItemTypes);

        await runner.RunAsync(new NoOpProgress(), CancellationToken.None, new MetaTaggerRunOptions { PreviewOnly = false });
        Assert.Equal(["meta:genre:drama"], movie.Tags);
        plugin = CreatePersistedPlugin();
        runner = CreateRunner(new PersistedConfigurationHost(plugin, [movie]));
        Assert.Equal(["Movie"], (await runner.GetGenerationStatusAsync(CancellationToken.None)).AuthorizedItemTypes);
    }

    [Fact]
    public async Task Generation_RecoveryOfIncompleteBaseline_KeepsPreviouslyKnownMembership()
    {
        var plugin = CreateFreshGenerationPlugin();
        var known = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var current = new Movie { Id = Guid.NewGuid(), Genres = ["Comedy"] };
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, [known, current]));
        await new PreviewMetadataTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        var serializer = new PersistedXmlSerializer();
        var path = Path.Combine(_directory, "configuration", "Jellyfin.Plugin.MetaTagger.xml");
        var saved = (PluginConfiguration)serializer.DeserializeFromFile(typeof(PluginConfiguration), path);
        saved.Installation!.Generation!.BaselineComplete = false;
        serializer.SerializeToFile(saved, path);

        plugin = CreatePersistedPlugin();
        runner = CreateRunner(new PersistedConfigurationHost(plugin, [current]));
        await new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        plugin = CreatePersistedPlugin();
        runner = CreateRunner(new PersistedConfigurationHost(plugin, [known, current]));
        await new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Empty(known.Tags);
        Assert.Empty(current.Tags);
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("post-scan")]
    [InlineData("force-scan")]
    [InlineData("rebuild")]
    [InlineData("claim")]
    [InlineData("settings")]
    [InlineData("ownership")]
    public async Task Generation_AutomaticAndMaintenanceOperations_DoNotAuthorizeExistingItems(string operation)
    {
        var plugin = CreateFreshGenerationPlugin();
        var movie = new Movie
        {
            Id = Guid.NewGuid(), Genres = ["Drama"],
            Tags = ["manual:keep", "manual:tagger:force", "Favorites", "meta:genre:legacy"]
        };
        var store = new MetaTaggerStateStore(_directory);
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, [movie]));
        switch (operation)
        {
            case "preview":
                await new PreviewMetadataTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
                break;
            case "post-scan":
                await new LibraryPostScanTask(runner, store, new MetaTaggerClock()).Run(new NoOpProgress(), CancellationToken.None);
                break;
            case "force-scan":
                await new ForceFullMetadataTagScanTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
                break;
            case "rebuild":
                await new RebuildMetadataTagLedgerTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
                break;
            case "claim":
                plugin.Configuration.ClaimExistingGeneratedTagsForCleanup = true;
                plugin.SaveConfiguration();
                await new RebuildMetadataTagLedgerTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
                break;
            case "settings":
                var configuration = PluginConfigurationValidator.Sanitize(plugin.Configuration);
                configuration.EnableStudios = true;
                plugin.UpdateConfiguration(configuration);
                break;
            case "ownership":
                await store.SaveAsync(new MetaTaggerState
                {
                    Items = new() { [movie.Id.ToString("N")] = new MetaTaggerStateItem
                    {
                        ItemId = movie.Id.ToString("N"), ItemType = "Movie", LastAppliedTags = ["meta:genre:legacy"]
                    } }
                }, CancellationToken.None);
                break;
        }
        plugin = CreatePersistedPlugin();
        runner = CreateRunner(new PersistedConfigurationHost(plugin, [movie]));
        await new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(["manual:keep", "manual:tagger:force", "Favorites", "meta:genre:legacy"], movie.Tags);
        var status = await runner.GetGenerationStatusAsync(CancellationToken.None);
        Assert.Empty(status.AuthorizedItemTypes);
        Assert.Empty(status.AuthorizedItemIds);
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("authorization")]
    public async Task Generation_FailedPersistence_DoesNotPublishPermissionAfterRestart(string phase)
    {
        var serializer = new PersistedXmlSerializer();
        var plugin = CreateFreshGenerationPlugin(serializer);
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["manual:keep", "Favorites"] };
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, [movie]));
        if (phase == "authorization")
        {
            await new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        }
        serializer.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => phase == "baseline"
            ? new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None)
            : new ApplyMetadataTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None));
        serializer.FailWrites = false;
        var status = await runner.GetGenerationStatusAsync(CancellationToken.None);
        Assert.Equal(phase == "baseline" ? "NotCaptured" : "Complete", status.BaselineStatus);
        Assert.Empty(status.AuthorizedItemTypes);
        Assert.Equal(["manual:keep", "Favorites"], movie.Tags);

        plugin = CreatePersistedPlugin();
        runner = CreateRunner(new PersistedConfigurationHost(plugin, [movie]));
        await new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        Assert.Equal(["manual:keep", "Favorites"], movie.Tags);
        Assert.Empty((await runner.GetGenerationStatusAsync(CancellationToken.None)).AuthorizedItemTypes);
    }

    [Fact]
    public async Task Generation_StatusAndPreview_ReportTheBoundaryWithoutAuthorizingBackfill()
    {
        var plugin = CreateFreshGenerationPlugin();
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, [movie]));
        var controller = new MetaTaggerDashboardController(new MetaTaggerStateStore(_directory), () => plugin.Configuration, runner);

        var before = await controller.GetGenerationStatusAsync(CancellationToken.None);
        Assert.Equal("NotCaptured", before.BaselineStatus);
        Assert.Null(before.BaselineItemCount);
        Assert.Empty(before.AuthorizedItemTypes);
        var preview = await runner.PreviewItemAsync(movie.Id, CancellationToken.None);
        Assert.Equal("BackfillRequired", preview.GenerationEligibility);
        Assert.Equal(["meta:genre:drama"], preview.AddedTags);
        var captured = await controller.GetGenerationStatusAsync(CancellationToken.None);
        Assert.Equal("Complete", captured.BaselineStatus);
        Assert.Equal(1, captured.BaselineItemCount);
        Assert.Empty(captured.AuthorizedItemIds);

        await new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        var summary = await new MetaTaggerStateStore(_directory).LoadSummaryAsync(CancellationToken.None);
        Assert.Equal(1, summary!.ItemsSkippedBaseline);
        Assert.Equal(0, summary.ItemsProcessed);
        Assert.Equal("None", summary.BackfillAuthorization);
        Assert.Contains("existing baseline excluded 1", plugin.Configuration.LastRunSummaryText);
        Assert.Empty(movie.Tags);
    }

    [Fact]
    public async Task Generation_SingleItemApply_AuthorizesOnlyItsValidatedTargetAfterRestart()
    {
        var plugin = CreateFreshGenerationPlugin();
        var target = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var other = new Movie { Id = Guid.NewGuid(), Genres = ["Comedy"] };
        var items = new List<BaseItem> { target, other };
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, items));
        var preview = await runner.PreviewItemAsync(target.Id, CancellationToken.None);
        await runner.ApplyItemAsync(target.Id, preview.Token!, CancellationToken.None);

        target.Genres = ["Family"];
        plugin = CreatePersistedPlugin();
        runner = CreateRunner(new PersistedConfigurationHost(plugin, items));
        await new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Contains("meta:genre:family", target.Tags);
        Assert.Empty(other.Tags);
    }

    [Theory]
    [InlineData("runner")]
    [InlineData("task")]
    public async Task Generation_BulkApply_AuthorizesOnlyItsFullSelectedScopeDespiteWriteLimit(string operation)
    {
        var plugin = CreateFreshGenerationPlugin();
        plugin.Configuration.MaxWritesPerRun = 1;
        plugin.SaveConfiguration();
        var first = new Movie { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Genres = ["Drama"] };
        var second = new Movie { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Genres = ["Comedy"] };
        var episode = new Episode { Id = Guid.NewGuid(), Genres = ["Family"] };
        var items = new List<BaseItem> { first, second, episode };
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, items));
        if (operation == "task")
        {
            await new ApplyMetadataTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        }
        else
        {
            await runner.RunAsync(new NoOpProgress(), CancellationToken.None, new MetaTaggerRunOptions { PreviewOnly = false });
        }
        var summary = await new MetaTaggerStateStore(_directory).LoadSummaryAsync(CancellationToken.None);
        Assert.Equal(1, summary!.WritesApplied);
        Assert.True(summary.BudgetLimitReached);
        Assert.Empty(episode.Tags);

        plugin = CreatePersistedPlugin();
        plugin.Configuration.IncludeEpisodes = true;
        plugin.Configuration.MaxWritesPerRun = 0;
        plugin.SaveConfiguration();
        runner = CreateRunner(new PersistedConfigurationHost(plugin, items));
        await new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(["meta:genre:drama"], first.Tags);
        Assert.Equal(["meta:genre:comedy"], second.Tags);
        Assert.Empty(episode.Tags);
    }

    [Fact]
    public async Task Generation_FreshDailyRun_ExcludesExistingItemsAndTagsLaterAdditionsAfterRestart()
    {
        var plugin = CreateFreshGenerationPlugin();
        var existing = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["manual:keep", "Favorites"] };
        var items = new List<BaseItem> { existing };
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, items));

        await new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(["manual:keep", "Favorites"], existing.Tags);
        Assert.Empty((await new MetaTaggerStateStore(_directory).LoadAsync(CancellationToken.None)).Items);

        var added = new Movie { Id = Guid.NewGuid(), Genres = ["Comedy"] };
        items.Add(added);
        plugin = CreatePersistedPlugin();
        runner = CreateRunner(new PersistedConfigurationHost(plugin, items));
        await new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(["manual:keep", "Favorites"], existing.Tags);
        Assert.Equal(["meta:genre:comedy"], added.Tags);
    }

    private Plugin CreateFreshGenerationPlugin(PersistedXmlSerializer? serializer = null)
    {
        var plugin = CreatePersistedPlugin(serializer);
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.EnableParentalRating = false;
        configuration.EnableAudioLanguages = false;
        configuration.PreviewOnly = false;
        configuration.RunAfterLibraryScan = true;
        configuration.MinimumMinutesBetweenAutoRuns = 0;
        plugin.UpdateConfiguration(configuration);
        return plugin;
    }
}
