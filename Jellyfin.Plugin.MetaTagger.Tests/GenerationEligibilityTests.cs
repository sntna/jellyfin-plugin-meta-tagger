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
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Generation_FreshDefaults_ItemApplyAndOptionalCheckPreserveAutomaticBoundary(bool checkFirst, bool postScan)
    {
        var plugin = CreatePersistedPlugin();
        Assert.False(plugin.Configuration.PreviewOnly);
        Assert.True(plugin.Configuration.RunAfterLibraryScan);
        var target = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["manual:keep", "Favorites"] };
        var excluded = new Movie { Id = Guid.NewGuid(), Genres = ["Comedy"], Tags = ["custom:keep"] };
        var items = new List<BaseItem> { target, excluded };
        var store = new MetaTaggerStateStore(_directory);
        var clock = new ItemApprovalClock();
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, items), store, clock);
        if (checkFirst)
        {
            await runner.PreviewItemAsync(target.Id, CancellationToken.None);
            var checkedStatus = await runner.GetGenerationStatusAsync(CancellationToken.None);
            Assert.Empty(checkedStatus.AuthorizedItemIds);
            Assert.Empty(checkedStatus.AuthorizedItemTypes);
            Assert.Equal(["manual:keep", "Favorites"], target.Tags);
        }
        target.Genres = ["Adventure"];
        var run = await WaitForItemRun(runner, runner.StartItemApply(target.Id).RunId);
        Assert.Equal("Completed", run.State);
        Assert.Equal(1, run.Summary!.WritesApplied);
        Assert.Equal(["Favorites", "manual:keep", "meta:genre:adventure"], target.Tags.Order());
        Assert.Equal(["custom:keep"], excluded.Tags);

        plugin = CreatePersistedPlugin();
        var added = new Movie { Id = Guid.NewGuid(), Genres = ["Animation"] };
        items.Add(added);
        target.Genres = ["Mystery"];
        runner = CreateRunner(new PersistedConfigurationHost(plugin, items), store, clock);
        if (postScan)
        {
            var task = new LibraryPostScanTask(runner, store, clock);
            var lastSummary = await store.LoadSummaryAsync(CancellationToken.None);
            await task.Run(new NoOpProgress(), CancellationToken.None);
            Assert.Equal("Skipped: cooldown", (await store.LoadRunsAsync(CancellationToken.None))[0].Outcome);
            Assert.Equal(lastSummary!.RunId, (await store.LoadSummaryAsync(CancellationToken.None))!.RunId);
            Assert.Empty(added.Tags);
            clock.UtcNow += TimeSpan.FromMinutes(30);
            await task.Run(new NoOpProgress(), CancellationToken.None);
        }
        else { await new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None); }

        Assert.Contains("meta:genre:mystery", target.Tags);
        Assert.Contains("manual:keep", target.Tags);
        Assert.Contains("Favorites", target.Tags);
        Assert.Equal(["custom:keep"], excluded.Tags);
        Assert.Contains("meta:genre:animation", added.Tags);
        var status = await runner.GetGenerationStatusAsync(CancellationToken.None);
        Assert.Equal([target.Id.ToString("N")], status.AuthorizedItemIds);
        Assert.Empty(status.AuthorizedItemTypes);
    }

    [Fact]
    public async Task Generation_DirectItemApply_RecalculatesCurrentDataAndAuthorizesOnlyTarget()
    {
        var plugin = CreateFreshGenerationPlugin();
        var target = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["Favorites"] };
        var other = new Movie { Id = Guid.NewGuid(), Genres = ["Comedy"] };
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, [target, other]));
        var result = await runner.ApplyItemAsync(target.Id, CancellationToken.None);
        Assert.Equal(1, result.WritesApplied);
        Assert.Equal(["Favorites", "meta:genre:drama"], target.Tags);
        Assert.Empty(other.Tags);
        var status = await runner.GetGenerationStatusAsync(CancellationToken.None);
        Assert.Equal([target.Id.ToString("N")], status.AuthorizedItemIds);
        Assert.Empty(status.AuthorizedItemTypes);
    }

    [Theory]
    [InlineData("bulk")]
    [InlineData("item")]
    public async Task Generation_PreviewHistory_ReportsAutomaticExclusionAndPreservesExistingOwnership(string operation)
    {
        var plugin = CreateFreshGenerationPlugin();
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["Favorites", "meta:genre:old"] };
        var store = new MetaTaggerStateStore(_directory);
        await store.SaveAsync(new MetaTaggerState
        {
            Items = new() { [movie.Id.ToString("N")] = new MetaTaggerStateItem
            {
                ItemId = movie.Id.ToString("N"), ItemType = "Movie", LastMetadataFingerprint = "existing fingerprint",
                LastAppliedTags = ["meta:genre:old"]
            } }
        }, CancellationToken.None);
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, [movie]), store);
        if (operation == "bulk") { await new PreviewMetadataTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None); }
        else { await runner.PreviewItemAsync(movie.Id, CancellationToken.None); }

        var entry = Assert.Single(await store.LoadRunsAsync(CancellationToken.None));
        var run = await store.LoadRunAsync(entry.RunId, CancellationToken.None);
        Assert.NotNull(run);
        var item = Assert.Single(run.Items);
        Assert.Equal("BackfillRequired", item.GenerationEligibility);
        Assert.Contains("Automatic generation excludes", item.Reason);
        Assert.Equal(["meta:genre:drama"], item.AddedTags);
        Assert.Equal(1, run.Summary.ItemsPreviewedBaseline);
        Assert.Equal(0, run.Summary.ItemsSkippedBaseline);
        if (operation == "bulk")
        {
            Assert.Equal("BackfillRequired", Assert.Single(await store.LoadPreviewChangesAsync(CancellationToken.None)).GenerationEligibility);
        }
        var ownership = (await store.LoadAsync(CancellationToken.None)).Items[movie.Id.ToString("N")];
        Assert.Equal("existing fingerprint", ownership.LastMetadataFingerprint);
        Assert.Equal(["meta:genre:old"], ownership.LastAppliedTags);
        Assert.Equal(["Favorites", "meta:genre:old"], movie.Tags);
    }

    [Fact]
    public async Task Generation_SingleItemPreview_StopsWhenBaselineCaptureConsumesTheTimeBudget()
    {
        var plugin = CreateFreshGenerationPlugin();
        plugin.Configuration.MaxRunMinutes = 1;
        plugin.SaveConfiguration();
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var clock = new ItemApprovalClock();
        var host = new PersistedConfigurationHost(plugin, [movie])
        {
            BeforeQuery = () => clock.UtcNow += TimeSpan.FromMinutes(2)
        };
        var store = new MetaTaggerStateStore(_directory);
        var runner = CreateRunner(host, store, clock);
        var preview = await runner.PreviewItemAsync(movie.Id, CancellationToken.None);

        Assert.Null(preview.Token);
        Assert.Equal("Budget limited", preview.Status);
        Assert.Equal("NotChecked", preview.GenerationEligibility);
        var record = Assert.Single(await store.LoadRunsAsync(CancellationToken.None));
        Assert.Equal("Budget limited", record.Outcome);
        Assert.Equal(0, record.Summary.ItemsProcessed);
        Assert.Equal(1, record.Summary.ItemsRemaining);
        Assert.Equal("NotCaptured", (await runner.GetGenerationStatusAsync(CancellationToken.None)).BaselineStatus);
        Assert.Empty(movie.Tags);
    }

    [Theory]
    [InlineData("item-types", true)]
    [InlineData("single-item", false)]
    public async Task Generation_PartialPreview_StartsANewCycleOnlyWhenItemTypeAuthorizationChanges(
        string authorization, bool restarts)
    {
        var plugin = CreateFreshGenerationPlugin();
        plugin.Configuration.MaxItemsPerRun = 1;
        plugin.SaveConfiguration();
        var first = new Movie { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Genres = ["Drama"] };
        var second = new Movie { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Genres = ["Comedy"] };
        var store = new MetaTaggerStateStore(_directory);
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, [first, second]), store);
        await new PreviewMetadataTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        if (authorization == "item-types")
        {
            await new ApplyMetadataTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        }
        else
        {
            await runner.PreviewItemAsync(second.Id, CancellationToken.None);
            await runner.ApplyItemAsync(second.Id, CancellationToken.None);
        }
        await new PreviewMetadataTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);

        var entry = (await store.LoadRunsAsync(CancellationToken.None)).First();
        var run = await store.LoadRunAsync(entry.RunId, CancellationToken.None);
        Assert.NotNull(run);
        Assert.Equal((restarts ? first : second).Id.ToString("N"), Assert.Single(run.Items).ItemId);
        Assert.Equal(restarts ? 1 : 0, run.Summary.ItemsRemaining);
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
    [InlineData("scope")]
    [InlineData("lock")]
    [InlineData("persistence")]
    [InlineData("baseline-persistence")]
    public async Task Generation_RejectedSingleItemApply_CannotPublishAuthorization(string failure)
    {
        var serializer = new PersistedXmlSerializer();
        var plugin = CreateFreshGenerationPlugin(serializer);
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, [movie]));
        if (failure != "baseline-persistence") { await runner.PreviewItemAsync(movie.Id, CancellationToken.None); }
        if (failure == "scope")
        {
            plugin.Configuration.IncludeMovies = false;
            plugin.SaveConfiguration();
        }
        if (failure == "lock") { movie.IsLocked = true; }
        if (failure is "persistence" or "baseline-persistence") { serializer.FailWrites = true; }
        var apply = runner.ApplyItemAsync(movie.Id, CancellationToken.None);
        if (failure is "persistence" or "baseline-persistence") { await Assert.ThrowsAsync<IOException>(() => apply); }
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
    public async Task Generation_CapturedBaseline_KeepsItemIdsOutOfTheSavedConfiguration()
    {
        var plugin = CreateFreshGenerationPlugin();
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };

        await new ScheduledTagTask(CreateRunner(new PersistedConfigurationHost(plugin, [movie])))
            .ExecuteAsync(new NoOpProgress(), CancellationToken.None);

        var configurationPath = Path.Combine(_directory, "configuration", "Jellyfin.Plugin.MetaTagger.xml");
        Assert.DoesNotContain(movie.Id.ToString("N"), await File.ReadAllTextAsync(configurationPath),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(movie.Id.ToString("N"),
            await File.ReadAllTextAsync(Path.Combine(_directory, "meta-tagger-baseline.json")),
            StringComparison.OrdinalIgnoreCase);
        var recorded = ((PluginConfiguration)new PersistedXmlSerializer()
            .DeserializeFromFile(typeof(PluginConfiguration), configurationPath)).Installation!.Generation!;
        Assert.True(recorded.BaselineComplete);
        Assert.Equal(1, recorded.BaselineItemCount);
        Assert.StartsWith("sha256:", recorded.BaselineChecksum);
        Assert.Equal("Complete", (await CreateRunner(new PersistedConfigurationHost(CreatePersistedPlugin(), [movie]))
            .GetGenerationStatusAsync(CancellationToken.None)).BaselineStatus);
    }

    [Fact]
    public async Task Generation_ConfigurationFromBeforeTheBaselineMoved_RecapturesInsteadOfFailing()
    {
        var plugin = CreateFreshGenerationPlugin();
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        await new ScheduledTagTask(CreateRunner(new PersistedConfigurationHost(plugin, [movie])))
            .ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        await RecordBaselineItemIdsInConfigurationAsync(movie.Id);

        var restarted = CreatePersistedPlugin();
        var summary = await CreateRunner(new PersistedConfigurationHost(restarted, [movie]))
            .RunAsync(new NoOpProgress(), CancellationToken.None);

        // The baseline is recaptured from the current library, so the item stays held back.
        Assert.Equal(0, summary.WritesApplied);
        Assert.Empty(movie.Tags);
        var recorded = restarted.Configuration.Installation!.Generation!;
        Assert.True(recorded.BaselineComplete);
        Assert.Equal(1, recorded.BaselineItemCount);
        Assert.StartsWith("sha256:", recorded.BaselineChecksum);
    }

    [Fact]
    public async Task Generation_ConfigurationFromBeforeTheBaselineMoved_KeepsMembershipOfAnAbsentItem()
    {
        var plugin = CreateFreshGenerationPlugin();
        var present = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var absent = new Movie { Id = Guid.NewGuid(), Genres = ["Comedy"] };
        var items = new List<BaseItem> { present, absent };
        await new ScheduledTagTask(CreateRunner(new PersistedConfigurationHost(plugin, items)))
            .ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        await RecordBaselineItemIdsInConfigurationAsync(present.Id, absent.Id);

        // The first run after the upgrade cannot see one of the items the baseline recorded,
        // as when a library location is offline while the recapture happens.
        var restarted = CreatePersistedPlugin();
        await CreateRunner(new PersistedConfigurationHost(restarted, [present]))
            .RunAsync(new NoOpProgress(), CancellationToken.None);

        // It comes back. It was in the library before the upgrade, so it is existing content
        // held back for explicit authorization, not a later addition to tag automatically.
        restarted = CreatePersistedPlugin();
        var summary = await CreateRunner(new PersistedConfigurationHost(restarted, items))
            .RunAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(0, summary.WritesApplied);
        Assert.Equal(2, summary.ItemsSkippedBaseline);
        Assert.Empty(present.Tags);
        Assert.Empty(absent.Tags);
        var recordedAfter = restarted.Configuration.Installation!.Generation!;
        Assert.Equal(2, recordedAfter.BaselineItemCount);
        Assert.Null(recordedAfter.BaselineItemIds);
    }

    [Fact]
    public async Task Generation_TruncatedPersistedBaseline_BlocksGenerationWithoutPublishingPermission()
    {
        var plugin = CreateFreshGenerationPlugin();
        var first = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var second = new Movie { Id = Guid.NewGuid(), Genres = ["Comedy"] };
        var items = new List<BaseItem> { first, second };
        await new ScheduledTagTask(CreateRunner(new PersistedConfigurationHost(plugin, items)))
            .ExecuteAsync(new NoOpProgress(), CancellationToken.None);

        // A later addition is normally eligible without any authorization. A baseline whose
        // stored item IDs no longer reproduce the recorded checksum must hold back even that.
        var added = new Movie { Id = Guid.NewGuid(), Genres = ["Horror"] };
        items.Add(added);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "meta-tagger-baseline.json"), $"[\"{first.Id:N}\"]");
        var serializer = new PersistedXmlSerializer();
        var configurationPath = Path.Combine(_directory, "configuration", "Jellyfin.Plugin.MetaTagger.xml");
        GenerationState Recorded() => ((PluginConfiguration)serializer
            .DeserializeFromFile(typeof(PluginConfiguration), configurationPath)).Installation!.Generation!;
        var before = Recorded();

        var summary = await CreateRunner(new PersistedConfigurationHost(CreatePersistedPlugin(), items))
            .RunAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(0, summary.WritesApplied);
        Assert.Equal(3, summary.ItemsSkippedEligibilityUnavailable);
        Assert.Empty(first.Tags);
        Assert.Empty(second.Tags);
        Assert.Empty(added.Tags);
        // The run published no permission it could not account for.
        var after = Recorded();
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.BaselineChecksum, after.BaselineChecksum);
        Assert.Equal(before.BaselineItemCount, after.BaselineItemCount);
        Assert.Empty(after.AuthorizedItemTypes!);
    }

    [Fact]
    public async Task Generation_UnverifiableBaseline_ReportsThatItWithholdsGeneration()
    {
        var plugin = CreateFreshGenerationPlugin();
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var items = new List<BaseItem> { movie };
        await new ScheduledTagTask(CreateRunner(new PersistedConfigurationHost(plugin, items)))
            .ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        Assert.Equal("Complete", (await CreateRunner(new PersistedConfigurationHost(CreatePersistedPlugin(), items))
            .GetGenerationStatusAsync(CancellationToken.None)).BaselineStatus);

        // The stored item IDs no longer reproduce the recorded checksum. The saved policy still
        // records a count and a checksum, so nothing about the configuration itself looks wrong.
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "meta-tagger-baseline.json"), $"[\"{Guid.NewGuid():N}\"]");
        var runner = CreateRunner(new PersistedConfigurationHost(CreatePersistedPlugin(), items));

        var status = await runner.GetGenerationStatusAsync(CancellationToken.None);
        var summary = await runner.RunAsync(new NoOpProgress(), CancellationToken.None);

        // The run withholds every item, so neither surface may report a baseline to act on.
        Assert.Equal(1, summary.ItemsSkippedEligibilityUnavailable);
        Assert.Equal(0, summary.WritesApplied);
        Assert.Empty(movie.Tags);
        Assert.Equal("AuthorizationUnavailable", status.Status);
        Assert.Equal("Unavailable", status.BaselineStatus);
        Assert.Null(status.BaselineItemCount);
        Assert.Equal("Unavailable", summary.BaselineStatus);
        Assert.Null(summary.BaselineItemCount);
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
    [InlineData("unavailable", false)]
    [InlineData("cancelled", false)]
    [InlineData("time-limit", false)]
    [InlineData("unavailable", true)]
    [InlineData("cancelled", true)]
    [InlineData("time-limit", true)]
    public async Task Generation_InterruptedCapture_CannotTreatTheLibraryAsEmpty(string failure, bool directApply)
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
        Task run = directApply ? runner.ApplyItemAsync(movie.Id, cancellation.Token)
            : new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), cancellation.Token);
        if (failure == "unavailable") { await Assert.ThrowsAsync<IOException>(() => run); }
        else if (failure == "cancelled") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
        else { await run; }
        Assert.Empty(movie.Tags);
        Assert.Empty((await runner.GetGenerationStatusAsync(CancellationToken.None)).AuthorizedItemIds);
        Assert.Equal("NotCaptured", (await runner.GetGenerationStatusAsync(CancellationToken.None)).BaselineStatus);
        var record = Assert.Single(await new MetaTaggerStateStore(_directory).LoadRunsAsync(CancellationToken.None));
        Assert.Equal(failure == "cancelled" ? "Cancelled" : failure == "time-limit" ? "Budget limited" : "Failed", record.Outcome);
        Assert.Equal(0, record.Summary.ItemsProcessed);
        Assert.Equal(0, record.Summary.WritesApplied);

        plugin = CreatePersistedPlugin();
        runner = CreateRunner(new PersistedConfigurationHost(plugin, [movie]));
        await new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        Assert.Empty(movie.Tags);
        Assert.Empty((await runner.GetGenerationStatusAsync(CancellationToken.None)).AuthorizedItemIds);
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
        // The baseline this run captured lives in this store, so a restart reads it from there.
        runner = CreateRunner(new PersistedConfigurationHost(plugin, [movie]), store);
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
        await runner.ApplyItemAsync(target.Id, CancellationToken.None);

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

    [Fact]
    public async Task Generation_ManagedBaselineItem_RestoresTagsRemovedOutsideThePlugin()
    {
        // A Jellyfin "Replace all metadata" refresh empties the item's tags, including the
        // tags this plugin wrote and still records as its own.
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["Favorites"] };
        var store = new InMemoryMetaTaggerStateStore(RecordedWriteLedger(movie, ["meta:genre:drama"]));
        var configuration = GenerationPerformanceConfiguration([movie], authorized: false, store);
        var host = new InMemoryMetaTaggerHost(configuration, [movie]);
        var runner = CreateRunner(host, store);

        var summary = await runner.RunAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(0, summary.ItemsSkippedBaseline);
        Assert.Equal(1, summary.WritesApplied);
        Assert.Equal(["Favorites", "meta:genre:drama"], movie.Tags);
        Assert.Equal(["meta:genre:drama"], store.SavedState!.Items[movie.Id.ToString("N")].LastAppliedTags);
        var status = await runner.GetGenerationStatusAsync(CancellationToken.None);
        Assert.Empty(status.AuthorizedItemTypes);
        Assert.Empty(status.AuthorizedItemIds);
    }

    [Theory]
    [InlineData("no records")]
    [InlineData("no recorded write")]
    [InlineData("no recorded tags")]
    public async Task Generation_BaselineItemWithoutARecordedWrite_StillRequiresBackfillAuthorization(string records)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["Favorites"] };
        var ledger = records switch
        {
            "no recorded write" => RecordedWriteLedger(movie, ["meta:genre:drama"], recordWrite: false),
            "no recorded tags" => RecordedWriteLedger(movie, []),
            _ => new MetaTaggerState()
        };
        var store = new InMemoryMetaTaggerStateStore(ledger);
        var configuration = GenerationPerformanceConfiguration([movie], authorized: false, store);
        var host = new InMemoryMetaTaggerHost(configuration, [movie]);
        var runner = CreateRunner(host, store);

        var summary = await runner.RunAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(1, summary.ItemsSkippedBaseline);
        Assert.Equal(0, summary.WritesApplied);
        Assert.Empty(host.UpdateAttempts);
        Assert.Equal(["Favorites"], movie.Tags);
        var inspection = await runner.InspectItemAsync(movie.Id, null, CancellationToken.None);
        Assert.Equal("BackfillRequired", inspection.GenerationEligibility);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Generation_ItemInspection_ReportsTheEligibilityARunActsOn(bool recordedWrite)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["Favorites"] };
        var store = new InMemoryMetaTaggerStateStore(RecordedWriteLedger(movie, ["meta:genre:drama"], recordedWrite));
        var configuration = GenerationPerformanceConfiguration([movie], authorized: false, store);
        var host = new InMemoryMetaTaggerHost(configuration, [movie]);
        var runner = CreateRunner(host, store);

        var inspection = await runner.InspectItemAsync(movie.Id, null, CancellationToken.None);
        var summary = await runner.RunAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(recordedWrite ? "Eligible" : "BackfillRequired", inspection.GenerationEligibility);
        Assert.Equal(recordedWrite ? 1 : 0, summary.WritesApplied);
        Assert.Equal(recordedWrite ? 0 : 1, summary.ItemsSkippedBaseline);
        Assert.Equal(recordedWrite ? ["Favorites", "meta:genre:drama"] : ["Favorites"], movie.Tags);
    }

    [Fact]
    public async Task Generation_RecapturedBaseline_RestoresTagsRemovedOutsideThePluginOnTheNextAutomaticRun()
    {
        var plugin = CreateFreshGenerationPlugin();
        var items = new List<BaseItem>();
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, items));
        await new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);

        // The plugin tags an item added after the baseline, so its own records hold that write.
        var added = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["Favorites"] };
        items.Add(added);
        plugin = CreatePersistedPlugin();
        runner = CreateRunner(new PersistedConfigurationHost(plugin, items));
        await new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        Assert.Equal(["Favorites", "meta:genre:drama"], added.Tags);

        // A later baseline recapture records the item as part of the existing library.
        var serializer = new PersistedXmlSerializer();
        var path = Path.Combine(_directory, "configuration", "Jellyfin.Plugin.MetaTagger.xml");
        var saved = (PluginConfiguration)serializer.DeserializeFromFile(typeof(PluginConfiguration), path);
        saved.Installation!.Generation!.BaselineComplete = false;
        serializer.SerializeToFile(saved, path);
        plugin = CreatePersistedPlugin();
        runner = CreateRunner(new PersistedConfigurationHost(plugin, items));
        await new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        var status = await runner.GetGenerationStatusAsync(CancellationToken.None);
        Assert.Equal("Complete", status.BaselineStatus);
        Assert.Equal(1, status.BaselineItemCount);
        Assert.Empty(status.AuthorizedItemTypes);
        Assert.Empty(status.AuthorizedItemIds);

        // Jellyfin replaces all metadata for the item, which empties its tags.
        added.Tags = [];
        plugin = CreatePersistedPlugin();
        runner = CreateRunner(new PersistedConfigurationHost(plugin, items));
        await new ScheduledTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(["meta:genre:drama"], added.Tags);
        var summary = await new MetaTaggerStateStore(_directory).LoadSummaryAsync(CancellationToken.None);
        Assert.Equal(0, summary!.ItemsSkippedBaseline);
        Assert.Equal(1, summary.WritesApplied);
    }

    [Fact]
    public async Task Generation_PreviewOfAManagedItem_KeepsTheRecordsThatMakeItEligible()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["Favorites"] };
        var store = new InMemoryMetaTaggerStateStore(RecordedWriteLedger(movie, ["meta:genre:drama"]));
        var configuration = GenerationPerformanceConfiguration([movie], authorized: false, store);
        var host = new InMemoryMetaTaggerHost(configuration, [movie]);
        var runner = CreateRunner(host, store);

        // A check between the refresh and the next Apply reports the proposal without writing,
        // so it must not forget that the plugin wrote these tags.
        var preview = await runner.RunAsync(new NoOpProgress(), CancellationToken.None,
            new MetaTaggerRunOptions { PreviewOnly = true });
        Assert.Equal(0, preview.WritesApplied);
        Assert.Equal(["Favorites"], movie.Tags);
        var recorded = store.SavedState!.Items[movie.Id.ToString("N")];
        Assert.Equal(["meta:genre:drama"], recorded.LastAppliedTags);
        Assert.NotNull(recorded.LastWriteUtc);

        var summary = await runner.RunAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(0, summary.ItemsSkippedBaseline);
        Assert.Equal(1, summary.WritesApplied);
        Assert.Equal(["Favorites", "meta:genre:drama"], movie.Tags);
    }

    [Fact]
    public async Task Generation_RecordRebuildOfAManagedItem_KeepsTheRecordsThatMakeItEligible()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["Favorites"] };
        var store = new InMemoryMetaTaggerStateStore(RecordedWriteLedger(movie, ["meta:genre:drama"]));
        var configuration = GenerationPerformanceConfiguration([movie], authorized: false, store);
        var host = new InMemoryMetaTaggerHost(configuration, [movie]);
        var runner = CreateRunner(host, store);

        // Rebuilding the records is what a user is likely to reach for after noticing missing
        // tags, so it must not forfeit the item's restoration.
        await new RebuildMetadataTagLedgerTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        Assert.Empty(host.UpdateAttempts);
        Assert.Equal(["Favorites"], movie.Tags);
        var recorded = store.SavedState!.Items[movie.Id.ToString("N")];
        Assert.Equal(["meta:genre:drama"], recorded.LastAppliedTags);
        Assert.NotNull(recorded.LastWriteUtc);
        var status = await runner.GetGenerationStatusAsync(CancellationToken.None);
        Assert.Empty(status.AuthorizedItemTypes);
        Assert.Empty(status.AuthorizedItemIds);

        var summary = await runner.RunAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(0, summary.ItemsSkippedBaseline);
        Assert.Equal(1, summary.WritesApplied);
        Assert.Equal(["Favorites", "meta:genre:drama"], movie.Tags);
    }

    [Fact]
    public async Task Generation_RecordRebuild_StillDropsRecordsOfTagsItNoLongerGenerates()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["Favorites"] };
        var store = new InMemoryMetaTaggerStateStore(
            RecordedWriteLedger(movie, ["meta:genre:drama", "meta:genre:retired"]));
        var configuration = GenerationPerformanceConfiguration([movie], authorized: false, store);
        var host = new InMemoryMetaTaggerHost(configuration, [movie]);
        var runner = CreateRunner(host, store);

        await new RebuildMetadataTagLedgerTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);

        // A record of a tag that is both absent and no longer generated is stale, not a repair.
        Assert.Equal(["meta:genre:drama"], store.SavedState!.Items[movie.Id.ToString("N")].LastAppliedTags);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Generation_PostScanUnderAsymmetricPriorEligibility_RestoresOnlyItemsThePluginWrote(bool recordedWrite)
    {
        // A 0.1.0 installation that applied tags on demand but never after a library scan, so the
        // adopted policy authorizes Movie for a configured run and not for post-scan. The other
        // eligibility fixtures authorize every invocation alike, which hides that difference.
        WriteLegacyConfiguration(previewOnly: false, runAfterLibraryScan: false);
        var plugin = CreatePersistedPlugin();
        var prior = plugin.Configuration.Installation!.PriorGenerationEligibility!;
        Assert.Equal(["Movie"], prior.IncludedItemTypes!);
        Assert.True(prior.ConfiguredApply);
        Assert.False(prior.PostScanApply);

        // The invocation this policy does authorize captures the baseline and writes the tag, so
        // the item belongs to the existing library and is managed by this plugin at once.
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, [movie]));
        await runner.RunAsync(new NoOpProgress(), CancellationToken.None);
        Assert.Equal(["meta:genre:drama"], movie.Tags);
        var store = new MetaTaggerStateStore(_directory);
        if (!recordedWrite)
        {
            // Records that hold applied tags without a write time never confirmed a write here.
            var state = await store.LoadAsync(CancellationToken.None);
            state.Items[movie.Id.ToString("N")].LastWriteUtc = null;
            await store.SaveAsync(state, CancellationToken.None);
        }

        // Jellyfin replaces all metadata for the item, which empties its tags.
        movie.Tags = [];
        // The user then turns post-scan on. The saved policy still never authorized post-scan.
        var configuration = PluginConfigurationValidator.Sanitize(plugin.Configuration);
        configuration.RunAfterLibraryScan = true;
        plugin.UpdateConfiguration(configuration);
        plugin = CreatePersistedPlugin();
        runner = CreateRunner(new PersistedConfigurationHost(plugin, [movie]));

        await new LibraryPostScanTask(runner, store, new MetaTaggerClock()).Run(new NoOpProgress(), CancellationToken.None);

        // A confirmed write makes restoration maintenance, which the invocation gate does not
        // withhold. Without one the item is ordinary existing library content, still held back.
        var summary = await store.LoadSummaryAsync(CancellationToken.None);
        Assert.Equal(recordedWrite ? 1 : 0, summary!.WritesApplied);
        Assert.Equal(recordedWrite ? 0 : 1, summary.ItemsSkippedBaseline);
        Assert.Equal<string>(recordedWrite ? ["meta:genre:drama"] : [], movie.Tags);
        var status = await runner.GetGenerationStatusAsync(CancellationToken.None);
        Assert.Empty(status.AuthorizedItemTypes);
        Assert.Empty(status.AuthorizedItemIds);
        Assert.False(status.PriorGenerationEligibility!.PostScanApply);
    }

    private static MetaTaggerState RecordedWriteLedger(BaseItem item, string[] recordedTags, bool recordWrite = true)
    {
        var itemId = item.Id.ToString("N");
        return new MetaTaggerState
        {
            Items = new(StringComparer.OrdinalIgnoreCase)
            {
                [itemId] = new MetaTaggerStateItem
                {
                    ItemId = itemId, ItemType = item.GetType().Name, LastGeneratedTags = recordedTags,
                    LastAppliedTags = recordedTags, LastRunUtc = DateTimeOffset.UnixEpoch,
                    LastWriteUtc = recordWrite ? DateTimeOffset.UnixEpoch : null
                }
            }
        };
    }

    // Rewrites the saved policy the way a build from before the relocation recorded it: the
    // item IDs in the configuration, no checksum, and nothing stored beside the ledger.
    private async Task RecordBaselineItemIdsInConfigurationAsync(params Guid[] itemIds)
    {
        var configurationPath = Path.Combine(_directory, "configuration", "Jellyfin.Plugin.MetaTagger.xml");
        var saved = await File.ReadAllTextAsync(configurationPath);
        var checksum = saved[saved.IndexOf("<BaselineChecksum>", StringComparison.Ordinal)..
            (saved.IndexOf("</BaselineChecksum>", StringComparison.Ordinal) + "</BaselineChecksum>".Length)];
        var elements = string.Concat(itemIds.Select(itemId => $"<string>{itemId:N}</string>"));
        await File.WriteAllTextAsync(configurationPath,
            saved.Replace(checksum, $"<BaselineItemIds>{elements}</BaselineItemIds>", StringComparison.Ordinal));
        File.Delete(Path.Combine(_directory, "meta-tagger-baseline.json"));
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
