using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaTagger;
using Jellyfin.Plugin.MetaTagger.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed class MetaTaggerRunnerTests : IDisposable
{
    private static readonly TimeSpan AsyncTestTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions StateSnapshotJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InspectItemAsync_DraftLanguagesReadTracksOnceWithoutChangingSavedSettings()
    {
        var item = new Movie { Id = Guid.NewGuid() };
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration { EnableAudioLanguages = false }, [item]);
        var lookups = new List<Guid>();
        host.StreamLookup = id =>
        {
            lookups.Add(id);
            return [new MediaStream { Type = MediaStreamType.Audio, Language = "eng" },
                new MediaStream { Type = MediaStreamType.Subtitle, Language = "spa", IsForced = true }];
        };
        var runner = CreateRunner(host);
        await runner.InspectItemAsync(item.Id, null, CancellationToken.None);
        Assert.Empty(lookups);

        var example = await runner.InspectItemAsync(item.Id,
            new PluginConfiguration { EnableAudioLanguages = true, EnableSubtitleLanguages = true }, CancellationToken.None);

        Assert.Equal(["meta:audio-language:eng", "meta:subtitle-language:spa"], example.GeneratedTags);
        Assert.Equal([item.Id], lookups);
        Assert.False(host.CurrentConfiguration.EnableAudioLanguages);
        Assert.False(host.CurrentConfiguration.EnableSubtitleLanguages);
        Assert.Empty(host.UpdateAttempts);
        Assert.Null(example.Token);
    }

    [Theory]
    [InlineData("item-lock")]
    [InlineData("tags-lock")]
    [InlineData("manual-skip")]
    public async Task RunAsync_ProtectedItemsDoNotReadStreams(string protection)
    {
        var item = new Movie { Id = Guid.NewGuid(), Tags = protection == "manual-skip" ? ["manual:tagger:skip"] : [],
            IsLocked = protection == "item-lock", LockedFields = protection == "tags-lock" ? [MetadataField.Tags] : [] };
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration { EnableAudioLanguages = true }, [item]);
        host.StreamLookup = _ => throw new IOException("Protected item metadata must not be read.");

        var result = await CreateRunner(host).RunAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(0, result.Failures);
        Assert.Equal(1, result.ItemsSkippedLocked + result.ItemsSkippedManual);
        Assert.Empty(host.UpdateAttempts);
    }

    [Fact]
    public async Task RunAsync_StreamLookupFailureKeepsOwnedLanguagesAndContinuesOtherItems()
    {
        var failed = new Movie { Id = Guid.NewGuid(), Tags = ["meta:audio-language:eng", "manual:keep"] };
        var next = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var configuration = new PluginConfiguration { EnableAudioLanguages = true, PreviewOnly = false, StaleTagMode = StaleTagMode.Remove };
        var host = new InMemoryMetaTaggerHost(configuration, [failed, next]);
        host.StreamLookup = id => id == failed.Id ? throw new IOException("Injected stream failure") : [];
        var store = new MetaTaggerStateStore(_directory);
        var state = new MetaTaggerState();
        state.Items[failed.Id.ToString("N")] = new MetaTaggerStateItem { ItemId = failed.Id.ToString("N"), ItemType = "Movie", LastAppliedTags = ["meta:audio-language:eng"] };
        await store.SaveAsync(state, CancellationToken.None);

        var result = await CreateRunner(host, store).RunAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(1, result.Failures);
        Assert.Equal(["meta:audio-language:eng", "manual:keep"], failed.Tags);
        Assert.Equal(["meta:audio-language:eng"], (await store.LoadAsync(CancellationToken.None)).Items[failed.Id.ToString("N")].LastAppliedTags);
        Assert.Equal(next.Id.ToString("N"), Assert.Single(host.UpdateAttempts).ItemId);
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("subtitle")]
    [InlineData("settings")]
    [InlineData("lookup-failure")]
    public async Task ApplyItemAsync_RejectsChangedTrackLanguagesOrSettingsAndLookupFailures(string change)
    {
        var item = new Movie { Id = Guid.NewGuid() };
        var configuration = new PluginConfiguration { EnableAudioLanguages = true, EnableSubtitleLanguages = true };
        var host = new InMemoryMetaTaggerHost(configuration, [item]);
        var audio = new MediaStream { Type = MediaStreamType.Audio, Language = "eng" };
        var subtitle = new MediaStream { Type = MediaStreamType.Subtitle, Language = "spa" };
        host.StreamLookup = _ => [audio, subtitle];
        var runner = CreateRunner(host);
        var preview = await runner.PreviewItemAsync(item.Id, CancellationToken.None);
        Assert.NotNull(preview.Token);
        if (change == "audio") { audio.Language = "fra"; }
        if (change == "subtitle") { subtitle.Language = "deu"; }
        if (change == "settings") { configuration.EnableAudioLanguages = false; }
        if (change == "lookup-failure") { host.StreamLookup = _ => throw new IOException("Injected stream failure"); }

        if (change == "lookup-failure")
        {
            await Assert.ThrowsAsync<IOException>(() => runner.ApplyItemAsync(item.Id, preview.Token!, CancellationToken.None));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ApplyItemAsync(item.Id, preview.Token!, CancellationToken.None));
        }

        Assert.Empty(host.UpdateAttempts);
        Assert.Empty(item.Tags);
    }

    [Fact]
    public async Task InspectItemAsync_EpisodeInheritsSeriesFactsButReadsOnlyItsOwnTracks()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid() };
        var series = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var configuration = new PluginConfiguration
        {
            IncludeEpisodes = true, EnableAudioLanguages = true, EnableSubtitleLanguages = true, IncludeParentSeriesMetadataOnEpisodes = true
        };
        var host = new InMemoryMetaTaggerHost(configuration, [episode, series]);
        var lookups = new List<Guid>();
        host.StreamLookup = id =>
        {
            lookups.Add(id);
            return id == episode.Id
                ? [new MediaStream { Type = MediaStreamType.Audio, Language = "jpn" },
                    new MediaStream { Type = MediaStreamType.Subtitle, Language = "eng" },
                    new MediaStream { Type = MediaStreamType.Video, Language = "deu" }]
                : [new MediaStream { Type = MediaStreamType.Audio, Language = "fra" }];
        };

        var result = await CreateRunner(host, projection: new MetadataProjectionService(_ => series))
            .InspectItemAsync(episode.Id, null, CancellationToken.None);

        Assert.Equal(["meta:audio-language:jpn", "meta:genre:drama", "meta:subtitle-language:eng"], result.GeneratedTags);
        Assert.Equal([episode.Id], lookups);
    }

    [Fact]
    public async Task InspectItemAsync_CancellationDuringStreamLookupStopsTheItem()
    {
        using var cancellation = new CancellationTokenSource();
        var item = new Movie { Id = Guid.NewGuid() };
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration { EnableAudioLanguages = true }, [item]);
        host.StreamLookup = _ =>
        {
            cancellation.Cancel();
            return [new MediaStream { Type = MediaStreamType.Audio, Language = "eng" }];
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateRunner(host)
            .InspectItemAsync(item.Id, null, cancellation.Token));

        Assert.Empty(host.UpdateAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyItemAsync_PublicationFailureCannotRepeatConfirmedMediaWrites(bool failCheckpoint)
    {
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration(), [item]);
        var store = new MetaTaggerStateStore(_directory, new ItemPublicationFailure(failCheckpoint));
        var runner = CreateRunner(host, store);
        var preview = await runner.PreviewItemAsync(item.Id, CancellationToken.None);
        if (failCheckpoint)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ApplyItemAsync(item.Id, preview.Token!, CancellationToken.None));
            Assert.Contains("could not save its tag records", error.Message);
            var run = (await store.LoadRunsAsync(CancellationToken.None))[0];
            Assert.Equal("Uncertain", run.Outcome);
            Assert.Equal(1, run.Summary.WritesApplied);
        }
        else
        {
            var summary = await runner.ApplyItemAsync(item.Id, preview.Token!, CancellationToken.None);
            Assert.Equal(1, summary.WritesApplied);
            Assert.Equal(["meta:genre:drama"], (await store.LoadAsync(CancellationToken.None)).Items[item.Id.ToString("N")].LastAppliedTags);
        }
        Assert.Equal(["meta:genre:drama"], item.Tags);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ApplyItemAsync(item.Id, preview.Token!, CancellationToken.None));
        Assert.Single(host.UpdateAttempts);
    }

    private sealed class ItemPublicationFailure(bool failCheckpoint) : IMetaTaggerStateFilePromoter
    {
        private int _stateWrites;
        public void Promote(string tempPath, string destinationPath)
        {
            if (failCheckpoint && destinationPath.EndsWith("meta-tagger-state.json", StringComparison.Ordinal) && ++_stateWrites == 2)
            {
                throw new IOException("Injected checkpoint failure");
            }
            if (!failCheckpoint && destinationPath.Contains(Path.DirectorySeparatorChar + "runs" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new IOException("Injected history publication failure");
            }
            File.Move(tempPath, destinationPath, overwrite: true);
        }
    }

    [Fact]
    public async Task RunAsync_CancellationDuringDelayKeepsAppliedDetailAndRemainingWork()
    {
        using var cancellation = new CancellationTokenSource();
        var first = new Movie { Id = Guid.Parse("01010101-0101-0101-0101-010101010101"), Genres = ["Drama"] };
        var second = new Movie { Id = Guid.Parse("02020202-0202-0202-0202-020202020202"), Genres = ["Comedy"] };
        var store = new MetaTaggerStateStore(_directory);
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration { PreviewOnly = false, WriteDelayMilliseconds = 10 }, [first, second]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateRunner(host, store, new CancelDuringDelayMetaTaggerClock(cancellation))
            .RunAsync(new NoOpProgress(), cancellation.Token));
        var run = Assert.Single(await store.LoadRunsAsync(CancellationToken.None));
        var detail = await store.LoadRunAsync(run.RunId, CancellationToken.None);
        Assert.Equal("Cancelled", detail!.Outcome);
        Assert.Equal(1, detail.Summary.WritesApplied);
        Assert.Equal(1, detail.Summary.ItemsRemaining);
        Assert.Equal("Applied", Assert.Single(detail.Items).Outcome);
    }

    [Fact]
    public async Task ApplyItemAsync_RevalidationThatExceedsTimeBudgetDoesNotWrite()
    {
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var config = new PluginConfiguration { MaxRunMinutes = 1 };
        var host = new InMemoryMetaTaggerHost(config, [item]);
        var clock = new ItemApprovalClock();
        var loads = 0;
        var store = new ItemStateBoundary(new MetaTaggerStateStore(_directory), () =>
        {
            if (++loads == 3) { clock.UtcNow += TimeSpan.FromMinutes(2); }
        });
        var runner = CreateRunner(host, store, clock);
        var preview = await runner.PreviewItemAsync(item.Id, CancellationToken.None);
        var summary = await runner.ApplyItemAsync(item.Id, preview.Token!, CancellationToken.None);
        Assert.True(summary.BudgetLimitReached);
        Assert.Empty(host.UpdateAttempts);
        Assert.Equal(1, summary.ItemsRemaining);
    }

    private sealed class ItemStateBoundary(MetaTaggerStateStore store, Action onLoad) : IMetaTaggerStateStore
    {
        public Task<MetaTaggerState> LoadAsync(CancellationToken token) { onLoad(); return store.LoadAsync(token); }
        public Task SaveAsync(MetaTaggerState state, CancellationToken token) => store.SaveAsync(state, token);
        public Task SaveRunAsync(MetaTaggerRunRecord record, CancellationToken token) => store.SaveRunAsync(record, token);
        public Task SaveSummaryAsync(MetaTaggerRunSummary summary, CancellationToken token) => store.SaveSummaryAsync(summary, token);
        public Task<string> SavePreviewChangesAsync(IReadOnlyCollection<MetaTaggerPreviewChange> changes, CancellationToken token) => store.SavePreviewChangesAsync(changes, token);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("tags")]
    [InlineData("ownership")]
    [InlineData("settings")]
    [InlineData("item-lock")]
    [InlineData("tags-lock")]
    [InlineData("skip")]
    [InlineData("legacy-lock")]
    [InlineData("disabled")]
    [InlineData("other-target")]
    [InlineData("restart")]
    [InlineData("expiry")]
    public async Task ApplyItemAsync_RejectsObsoleteOrMisdirectedApprovalWithoutWriting(string change)
    {
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var config = new PluginConfiguration { EnableExistingTagsAsKeywords = false };
        var host = new InMemoryMetaTaggerHost(config, [item]);
        var store = new MetaTaggerStateStore(_directory);
        var clock = new ItemApprovalClock();
        var runner = CreateRunner(host, store, clock);
        var preview = await runner.PreviewItemAsync(item.Id, CancellationToken.None);
        var target = item.Id;
        switch (change)
        {
            case "metadata": item.Genres = ["Comedy"]; break;
            case "tags": item.Tags = ["Favorites"]; break;
            case "ownership":
                var state = await store.LoadAsync(CancellationToken.None);
                state.Items[item.Id.ToString("N")] = new MetaTaggerStateItem { LastAppliedTags = ["meta:old"] };
                await store.SaveAsync(state, CancellationToken.None); break;
            case "settings": config.ConfigurationRevision = "new-revision"; break;
            case "item-lock": item.IsLocked = true; break;
            case "tags-lock": item.LockedFields = [MediaBrowser.Model.Entities.MetadataField.Tags]; break;
            case "skip": item.Tags = ["manual:tagger:skip"]; break;
            case "legacy-lock": item.Tags = ["manual:tagger:lock"]; break;
            case "disabled": config.IsEnabled = false; break;
            case "other-target": target = Guid.NewGuid(); break;
            case "restart": runner = CreateRunner(host, store, clock); break;
            case "expiry": clock.UtcNow += TimeSpan.FromMinutes(15); break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ApplyItemAsync(target, preview.Token!, CancellationToken.None));
        Assert.Empty(host.UpdateAttempts);
    }

    [Theory]
    [InlineData("case")]
    [InlineData("no-record")]
    [InlineData("protected")]
    [InlineData("manual-skip")]
    public async Task ItemPreview_DoesNotInventMissingTagHistoryOrPromiseProtectedRestoration(string scenario)
    {
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"],
            Tags = scenario == "case" ? ["META:GENRE:DRAMA"] : scenario == "manual-skip" ? ["manual:tagger:skip"] : [],
            IsLocked = scenario == "protected" };
        var store = new MetaTaggerStateStore(_directory);
        var state = new MetaTaggerState();
        if (scenario != "no-record")
        {
            state.Items[item.Id.ToString("N")] = new MetaTaggerStateItem { LastAppliedTags = ["meta:genre:drama"] };
        }
        await store.SaveAsync(state, CancellationToken.None);
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration { EnableExistingTagsAsKeywords = false }, [item]);
        var runner = CreateRunner(host, store);
        var preview = await runner.PreviewItemAsync(item.Id, CancellationToken.None);
        Assert.Empty(preview.MissingRecordedTags);
        if (scenario != "no-record") { Assert.Null(preview.Token); }
        if (scenario is "protected" or "manual-skip")
        {
            Assert.Equal("Protected", preview.Status);
            var cleanup = await runner.PreviewCleanupAsync(item.Id, new NoOpProgress(), CancellationToken.None);
            Assert.Empty(cleanup.MissingRecordedTags);
            Assert.Null(cleanup.Token);
        }
        Assert.Empty(host.UpdateAttempts);
    }

    [Theory]
    [InlineData(StaleTagMode.Remove)]
    [InlineData(StaleTagMode.Keep)]
    [InlineData(StaleTagMode.Preview)]
    public async Task ItemPreview_PrefixChangeExplainsOnlyTheActualPlan(StaleTagMode mode)
    {
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["meta:genre:drama", "meta:unowned", "manual:favorite"] };
        var config = new PluginConfiguration { GeneratedTagPrefix = "new", EnableExistingTagsAsKeywords = false, StaleTagMode = mode };
        var store = new MetaTaggerStateStore(_directory);
        var state = new MetaTaggerState();
        state.Items[item.Id.ToString("N")] = new MetaTaggerStateItem { LastAppliedTags = ["meta:genre:drama", "meta:year:2023"] };
        await store.SaveAsync(state, CancellationToken.None);
        var runner = CreateRunner(new InMemoryMetaTaggerHost(config, [item]), store);
        var preview = await runner.PreviewItemAsync(item.Id, CancellationToken.None);
        Assert.Equal(["new:genre:drama"], preview.AddedTags);
        Assert.Equal(["meta:year:2023"], preview.MissingRecordedTags);
        Assert.Empty(preview.MissingTagsWithSourceOff);
        Assert.Equal(mode == StaleTagMode.Remove ? ["meta:genre:drama"] : Array.Empty<string>(), preview.RemovedTags);
        Assert.Equal(mode == StaleTagMode.Remove ? Array.Empty<string>() : ["meta:genre:drama"], preview.OwnedTags);
        Assert.Equal(["meta:unowned"], preview.PreservedTags);
        await runner.ApplyItemAsync(item.Id, preview.Token!, CancellationToken.None);
        Assert.Contains("new:genre:drama", item.Tags);
        Assert.Contains("meta:unowned", item.Tags);
        Assert.Contains("manual:favorite", item.Tags);
        Assert.Equal(mode != StaleTagMode.Remove, item.Tags.Contains("meta:genre:drama"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ItemPreview_MissingTagNoLongerGeneratedNeedsNoActionAndExplainsSourceOnlyWhenOff(bool sourceOff)
    {
        var item = new Movie { Id = Guid.NewGuid(), Genres = sourceOff ? ["Drama"] : [] };
        var config = new PluginConfiguration { EnableGenres = !sourceOff, EnableExistingTagsAsKeywords = false };
        var store = new MetaTaggerStateStore(_directory);
        var state = new MetaTaggerState();
        state.Items[item.Id.ToString("N")] = new MetaTaggerStateItem { LastAppliedTags = ["meta:genre:drama"] };
        await store.SaveAsync(state, CancellationToken.None);
        var host = new InMemoryMetaTaggerHost(config, [item]);
        var preview = await CreateRunner(host, store).PreviewItemAsync(item.Id, CancellationToken.None);
        Assert.Equal(["meta:genre:drama"], preview.MissingRecordedTags);
        Assert.Equal(sourceOff ? ["meta:genre:drama"] : Array.Empty<string>(), preview.MissingTagsWithSourceOff);
        Assert.Empty(preview.AddedTags);
        Assert.Empty(preview.RemovedTags);
        Assert.Null(preview.Token);
        Assert.Empty(host.UpdateAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_MissingRecordedTagsAreInformationalAndDoNotAuthorizeRemoval(bool hasPresentTag)
    {
        var item = new Movie { Id = Guid.NewGuid(), Tags = hasPresentTag ? ["meta:year:2024", "favorite"] : ["favorite"] };
        var store = new MetaTaggerStateStore(_directory);
        var state = new MetaTaggerState();
        state.Items[item.Id.ToString("N")] = new MetaTaggerStateItem
        {
            LastAppliedTags = ["meta:genre:drama", "meta:year:2024"]
        };
        await store.SaveAsync(state, CancellationToken.None);
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration(), [item]);
        var runner = CreateRunner(host, store);
        var preview = await runner.PreviewCleanupAsync(item.Id, new NoOpProgress(), CancellationToken.None);

        Assert.Equal(hasPresentTag ? ["meta:genre:drama"] : new[] { "meta:genre:drama", "meta:year:2024" }, preview.MissingRecordedTags);
        Assert.Equal(hasPresentTag ? 1 : 0, preview.Summary.ItemsChanged);
        Assert.Empty(host.UpdateAttempts);
        if (hasPresentTag)
        {
            Assert.Equal(["meta:year:2024"], Assert.Single(preview.Changes).RemovedTags);
            var applied = await runner.ApplyCleanupAsync(preview.Token!, new NoOpProgress(), CancellationToken.None);
            Assert.Equal(1, applied.WritesApplied);
            Assert.Equal(["favorite"], item.Tags);
        }
        else
        {
            Assert.Null(preview.Token);
            Assert.Empty(preview.Changes);
        }
        var refreshed = await runner.PreviewCleanupAsync(item.Id, new NoOpProgress(), CancellationToken.None);
        Assert.Empty(refreshed.MissingRecordedTags);
        Assert.Null(refreshed.Token);
    }

    [Fact]
    public async Task ItemPreview_ExplainsMissingRecordedTagWithoutWritingAndApplyRestoresIt()
    {
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["edited-drama", "manual:favorite"] };
        var config = new PluginConfiguration { EnableExistingTagsAsKeywords = false };
        var store = new MetaTaggerStateStore(_directory);
        var state = new MetaTaggerState();
        state.Items[item.Id.ToString("N")] = new MetaTaggerStateItem { LastAppliedTags = ["meta:genre:drama"] };
        await store.SaveAsync(state, CancellationToken.None);
        var host = new InMemoryMetaTaggerHost(config, [item]);
        var runner = CreateRunner(host, store);

        var preview = await runner.PreviewItemAsync(item.Id, CancellationToken.None);

        Assert.Equal(["meta:genre:drama"], preview.MissingRecordedTags);
        Assert.Equal(["meta:genre:drama"], preview.AddedTags);
        Assert.Equal(["edited-drama"], preview.PreservedTags);
        Assert.Empty(host.UpdateAttempts);
        await runner.ApplyItemAsync(item.Id, preview.Token!, CancellationToken.None);
        Assert.Contains("meta:genre:drama", item.Tags);
        Assert.Contains("edited-drama", item.Tags);
        Assert.Contains("manual:favorite", item.Tags);
        Assert.Empty((await runner.InspectItemAsync(item.Id, null, CancellationToken.None)).MissingRecordedTags);
    }

    [Theory]
    [InlineData(StaleTagMode.Keep, false)]
    [InlineData(StaleTagMode.Preview, true)]
    public async Task ItemGeneration_KeepAndPreviewPreserveOldOwnedAndUntrackedTags(StaleTagMode mode, bool potentialRemoval)
    {
        var item = new Movie { Id = Guid.NewGuid(), ProductionYear = 2024, Tags = ["meta:year:2023", "meta:unowned"] };
        var config = new PluginConfiguration { EnableProductionYear = true, EnableExistingTagsAsKeywords = false, StaleTagMode = mode, ClaimExistingGeneratedTagsForCleanup = true };
        var store = new MetaTaggerStateStore(_directory);
        var state = new MetaTaggerState();
        state.Items[item.Id.ToString("N")] = new MetaTaggerStateItem { LastAppliedTags = ["meta:year:2023"] };
        await store.SaveAsync(state, CancellationToken.None);
        var runner = CreateRunner(new InMemoryMetaTaggerHost(config, [item]), store);
        var preview = await runner.PreviewItemAsync(item.Id, CancellationToken.None);
        Assert.Empty(preview.RemovedTags);
        Assert.Equal(potentialRemoval ? ["meta:year:2023"] : Array.Empty<string>(), preview.PreviewRemovedTags);
        Assert.Contains("meta:unowned", preview.PreservedTags);
        await runner.ApplyItemAsync(item.Id, preview.Token!, CancellationToken.None);
        Assert.Equal(["meta:year:2023", "meta:unowned", "meta:year:2024"], item.Tags);
    }

    private sealed class ItemApprovalClock : IMetaTaggerClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task ItemGeneration_RecordsPreviewAndApplyButNotDraftExamples()
    {
        var item = new Movie { Id = Guid.NewGuid(), Name = "Dune", Genres = ["Science Fiction"] };
        var config = new PluginConfiguration { EnableExistingTagsAsKeywords = false };
        var store = new MetaTaggerStateStore(_directory);
        var runner = CreateRunner(new InMemoryMetaTaggerHost(config, [item]), store);
        await runner.InspectItemAsync(item.Id, config, CancellationToken.None);
        Assert.Empty(await store.LoadRunsAsync(CancellationToken.None));
        var preview = await runner.PreviewItemAsync(item.Id, CancellationToken.None);
        await runner.ApplyItemAsync(item.Id, preview.Token!, CancellationToken.None);
        var runs = await store.LoadRunsAsync(CancellationToken.None);
        Assert.Equal(["Apply", "Preview"], runs.Select(run => run.Operation));
        Assert.All(runs, run => Assert.Equal(item.Id.ToString("N"), run.Scope));
        var apply = await store.LoadRunAsync(runs[0].RunId, CancellationToken.None);
        Assert.Equal("Applied", Assert.Single(apply!.Items).Outcome);
    }

    [Fact]
    public async Task RunAsync_CancelledAttemptRetainsItsIdentityAndProtectedItemDetails()
    {
        using var cancellation = new CancellationTokenSource();
        var locked = new Movie { Id = Guid.Parse("01010101-0101-0101-0101-010101010101"), Name = "Locked", IsLocked = true };
        var writable = new Movie { Id = Guid.Parse("02020202-0202-0202-0202-020202020202"), Genres = ["Drama"] };
        var store = new MetaTaggerStateStore(_directory);
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration { PreviewOnly = false }, [locked, writable],
            cancelRunOnWriteItemId: writable.Id.ToString("N"), runCancellation: cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateRunner(host, store)
            .RunAsync(new NoOpProgress(), cancellation.Token));
        var record = Assert.Single(await new MetaTaggerStateStore(_directory).LoadRunsAsync(CancellationToken.None));
        Assert.Equal("Cancelled", record.Outcome);
        Assert.NotNull(record.EndedUtc);
        var details = await store.LoadRunAsync(record.RunId, CancellationToken.None);
        Assert.Equal("Protected", Assert.Single(details!.Items, item => item.ItemId == locked.Id.ToString("N")).Outcome);
        Assert.Equal("Jellyfin metadata lock protects this item.", details.Items[0].Reason);
    }

    [Fact]
    public async Task ApplyItemAsync_ChangesOnlyReviewedItemAndLeavesPendingScanAndMaintenanceIntact()
    {
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Science Fiction"], OfficialRating = "PG-13", ProductionYear = 2024,
            Tags = ["Favorites", "manual:tagger:force", "meta:year:2023"] };
        var other = new Movie { Id = Guid.NewGuid(), Tags = ["meta:genre:drama", "Favorite"] };
        var configuration = new PluginConfiguration { EnableProductionYear = true, EnableExistingTagsAsKeywords = false, StaleTagMode = StaleTagMode.Remove,
            ForceFullScanOnNextRun = true, RebuildTrackingLedgerOnNextRun = true, ClaimExistingGeneratedTagsOnNextRun = true };
        var host = new InMemoryMetaTaggerHost(configuration, [item, other]);
        var store = new MetaTaggerStateStore(_directory);
        var state = new MetaTaggerState();
        state.Items[item.Id.ToString("N")] = new MetaTaggerStateItem { LastAppliedTags = ["meta:year:2023"] };
        state.Items[other.Id.ToString("N")] = new MetaTaggerStateItem { LastAppliedTags = ["meta:genre:drama"] };
        state.RunCursors["scheduled"] = new MetaTaggerRunCursor { PendingItemIds = [item.Id.ToString("N"), other.Id.ToString("N")] };
        await store.SaveAsync(state, CancellationToken.None);
        var runner = CreateRunner(host, store);
        var preview = await runner.PreviewItemAsync(item.Id, CancellationToken.None);
        var summary = await runner.ApplyItemAsync(item.Id, preview.Token!, CancellationToken.None);
        Assert.Equal(1, summary.WritesApplied);
        Assert.Equal(["Favorites", "manual:tagger:force", "meta:genre:science-fiction", "meta:rating:pg-13", "meta:year:2024"], item.Tags);
        Assert.Equal(["meta:genre:drama", "Favorite"], other.Tags);
        var reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(["meta:genre:drama"], reloaded.Items[other.Id.ToString("N")].LastAppliedTags);
        Assert.Equal(0, reloaded.RunCursors["scheduled"].NextIndex);
        Assert.True(configuration.ForceFullScanOnNextRun && configuration.RebuildTrackingLedgerOnNextRun && configuration.ClaimExistingGeneratedTagsOnNextRun);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ApplyItemAsync(item.Id, preview.Token!, CancellationToken.None));
        Assert.Single(host.UpdateAttempts);
    }

    [Fact]
    public async Task InspectItemAsync_ExplainsEffectiveRatingAndDeduplicatesNormalizedValues()
    {
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Science Fiction", "science-fiction"],
            OfficialRating = "PG", CustomRating = "PG-13", Tags = ["Favorites", "manual:tagger:force", "old:owned"] };
        var state = new MetaTaggerState();
        state.Items[item.Id.ToString("N")] = new MetaTaggerStateItem { LastAppliedTags = ["old:owned"] };
        var store = new MetaTaggerStateStore(_directory);
        await store.SaveAsync(state, CancellationToken.None);
        var result = await CreateRunner(new InMemoryMetaTaggerHost(new PluginConfiguration { EnableExistingTagsAsKeywords = true }, [item]), store)
            .InspectItemAsync(item.Id, null, CancellationToken.None);
        Assert.Equal(["meta:genre:science-fiction", "meta:keyword:favorites", "meta:rating:pg-13"], result.GeneratedTags);
        var rating = Assert.Single(result.Sources, source => source.Source == "rating");
        Assert.Equal(["PG-13"], rating.Values);
        Assert.Equal("meta:rating:pg-13", rating.Tag);
        Assert.Single(result.Sources, source => source.Source == "genre");
    }

    [Fact]
    public async Task PreviewItemAsync_DunePlansOnlyOwnedChangesWithoutAdvancingPendingWork()
    {
        var item = new Movie { Id = Guid.NewGuid(), Name = "Dune", Genres = ["Science Fiction"], OfficialRating = "PG-13", ProductionYear = 2024,
            Tags = ["Favorites", "manual:tagger:force", "meta:year:2023"] };
        var config = new PluginConfiguration { EnableProductionYear = true, EnableExistingTagsAsKeywords = false, EnableStudios = false,
            EnableProductionCountries = false, EnableProviderIds = false, StaleTagMode = StaleTagMode.Remove };
        var host = new InMemoryMetaTaggerHost(config, [item]);
        var store = new MetaTaggerStateStore(_directory);
        var state = new MetaTaggerState();
        state.Items[item.Id.ToString("N")] = new MetaTaggerStateItem { LastAppliedTags = ["meta:year:2023"] };
        state.RunCursors["scheduled"] = new MetaTaggerRunCursor { PendingItemIds = [item.Id.ToString("N"), "other"] };
        await store.SaveAsync(state, CancellationToken.None);
        var preview = await CreateRunner(host, store).PreviewItemAsync(item.Id, CancellationToken.None);
        Assert.Equal(["meta:genre:science-fiction", "meta:rating:pg-13", "meta:year:2024"], preview.AddedTags);
        Assert.Equal(["meta:year:2023"], preview.RemovedTags);
        Assert.Equal(["Favorites"], preview.PreservedTags);
        Assert.Equal(["manual:tagger:force"], preview.ManualTags);
        Assert.NotNull(preview.Token);
        Assert.Empty(host.UpdateAttempts);
        var reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(["meta:year:2023"], reloaded.Items[item.Id.ToString("N")].LastAppliedTags);
        Assert.Equal(0, reloaded.RunCursors["scheduled"].NextIndex);
    }

    [Fact]
    public async Task InspectItemAsync_DraftGenresToggleChangesExampleWithoutSavingOrWriting()
    {
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Science Fiction"], Tags = ["Favorites"] };
        var config = new PluginConfiguration { EnableExistingTagsAsKeywords = false };
        var host = new InMemoryMetaTaggerHost(config, [item]);
        var runner = CreateRunner(host);
        var draft = new PluginConfiguration { EnableExistingTagsAsKeywords = false };
        var before = await runner.InspectItemAsync(item.Id, draft, CancellationToken.None);
        Assert.Equal(["meta:genre:science-fiction"], before.GeneratedTags);
        draft.EnableGenres = false;
        var after = await runner.InspectItemAsync(item.Id, draft, CancellationToken.None);
        Assert.Empty(after.GeneratedTags);
        Assert.True(config.EnableGenres);
        Assert.Equal(["Favorites"], item.Tags);
        Assert.Empty(host.UpdateAttempts);
        Assert.Empty((await new MetaTaggerStateStore(_directory).LoadAsync(CancellationToken.None)).Items);
    }

    [Fact]
    public async Task RunAsync_RefreshesQueuedMetadataAndManualTagsBeforePlanning()
    {
        var snapshot = new Movie { Id = Guid.NewGuid(), Tags = ["old-manual"], Genres = ["Animation"] };
        var current = new Movie { Id = snapshot.Id, Tags = ["new-manual"], Genres = ["Drama"] };
        var host = new InMemoryMetaTaggerHost(
            new PluginConfiguration { PreviewOnly = false, EnableExistingTagsAsKeywords = false },
            [snapshot])
        {
            CurrentItems = [current]
        };

        var summary = await CreateRunner(host).RunAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(1, summary.WritesApplied);
        Assert.Equal(["new-manual", "meta:genre:drama"], Assert.Single(host.UpdateAttempts).Tags);
    }

    [Fact]
    public async Task Cleanup_PreviewThenApply_RemovesOwnedTagsAcrossAllTypesWhileTaggingIsDisabled()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IsEnabled = false;
        configuration.ClaimExistingGeneratedTagsForCleanup = true;
        var movie = new Movie
        {
            Id = Guid.Parse("01010101-0101-0101-0101-010101010101"),
            Genres = ["Animation"],
            Tags = ["meta:genre:animation", "custom:old-tag", "meta:unowned", "Favorite", "manual:note"]
        };
        var audio = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Id = Guid.Parse("02020202-0202-0202-0202-020202020202"),
            Tags = ["old:genre:rock", "Music"]
        };
        var state = new MetaTaggerState();
        state.Items[movie.Id.ToString("N")] = new MetaTaggerStateItem
        {
            ItemId = movie.Id.ToString("N"), ItemType = "Movie",
            LastAppliedTags = ["meta:genre:animation", "custom:old-tag", "manual:note"]
        };
        state.Items[audio.Id.ToString("N")] = new MetaTaggerStateItem
        {
            ItemId = audio.Id.ToString("N"), ItemType = "Audio", LastAppliedTags = ["old:genre:rock"]
        };
        var store = new MetaTaggerStateStore(_directory);
        await store.SaveAsync(state, CancellationToken.None);
        var host = new InMemoryMetaTaggerHost(configuration, [movie, audio]);
        var runner = CreateRunner(host, store);

        var preview = await runner.PreviewCleanupAsync(null, new NoOpProgress(), CancellationToken.None);

        Assert.Equal(2, preview.Changes.Count);
        Assert.All(preview.Changes, change => Assert.Empty(change.AddedTags));
        Assert.Empty(host.UpdateAttempts);
        Assert.All(host.Queries, Assert.Empty);
        Assert.NotNull(preview.Token);

        var summary = await runner.ApplyCleanupAsync(preview.Token!, new NoOpProgress(), CancellationToken.None);

        Assert.Equal(2, summary.WritesApplied);
        Assert.Equal(["meta:unowned", "Favorite", "manual:note"], movie.Tags);
        Assert.Equal(["Music"], audio.Tags);
        Assert.Empty((await store.LoadAsync(CancellationToken.None)).Items[audio.Id.ToString("N")].LastAppliedTags);
    }

    [Fact]
    public async Task Cleanup_ItemChangedAfterPreview_PreservesNewTagsAndOtherItemsOwnership()
    {
        var configuration = ConfigurationWithNoItemTypes();
        var target = WritableMovie("01010101-0101-0101-0101-010101010101", "Animation");
        var other = WritableMovie("02020202-0202-0202-0202-020202020202", "Drama");
        target.Tags = ["meta:genre:animation"];
        other.Tags = ["meta:genre:drama"];
        var store = new MetaTaggerStateStore(_directory);
        var state = new MetaTaggerState();
        foreach (var item in new[] { target, other })
        {
            state.Items[item.Id.ToString("N")] = new MetaTaggerStateItem
            {
                ItemId = item.Id.ToString("N"), ItemType = "Movie", LastAppliedTags = item.Tags
            };
        }

        await store.SaveAsync(state, CancellationToken.None);
        var host = new InMemoryMetaTaggerHost(configuration, [target, other]);
        var runner = CreateRunner(host, store);
        var preview = await runner.PreviewCleanupAsync(target.Id, new NoOpProgress(), CancellationToken.None);
        Assert.Equal(target.Id.ToString("N"), Assert.Single(preview.Changes).ItemId);
        target.Tags = ["meta:genre:animation", "new:owned-tag", "New favorite"];
        state = await store.LoadAsync(CancellationToken.None);
        state.Items[target.Id.ToString("N")].LastAppliedTags = ["meta:genre:animation", "new:owned-tag"];
        await store.SaveAsync(state, CancellationToken.None);

        var summary = await runner.ApplyCleanupAsync(preview.Token!, new NoOpProgress(), CancellationToken.None);

        Assert.Equal(1, summary.WritesApplied);
        Assert.Equal(["new:owned-tag", "New favorite"], target.Tags);
        Assert.Equal(["meta:genre:drama"], other.Tags);
        state = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(["new:owned-tag"], state.Items[target.Id.ToString("N")].LastAppliedTags);
        Assert.Equal(["meta:genre:drama"], state.Items[other.Id.ToString("N")].LastAppliedTags);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.ApplyCleanupAsync(preview.Token!, new NoOpProgress(), CancellationToken.None));
    }

    [Fact]
    public async Task CleanupController_SearchSelectPreviewAndApply_ClearsOnlySelectedItem()
    {
        var target = WritableMovie("01010101-0101-0101-0101-010101010101", "Animation");
        target.Name = "Big Buck Bunny";
        target.Tags = ["meta:genre:animation", "Favorite"];
        var store = new MetaTaggerStateStore(_directory);
        var state = new MetaTaggerState();
        state.Items[target.Id.ToString("N")] = new MetaTaggerStateItem
        {
            ItemId = target.Id.ToString("N"), ItemType = "Movie", LastAppliedTags = ["meta:genre:animation"]
        };
        await store.SaveAsync(state, CancellationToken.None);
        var runner = CreateRunner(new InMemoryMetaTaggerHost(ConfigurationWithNoItemTypes(), [target]), store);
        var controller = new MetaTaggerDashboardController(store, runner);

        var item = Assert.Single(controller.SearchCleanupItems("bUnNy"));
        Assert.Equal(target.Id, item.ItemId);
        var preview = await controller.PreviewCleanupAsync(
            new MetaTaggerCleanupRequest { ItemId = item.ItemId }, CancellationToken.None);
        Assert.Equal(["meta:genre:animation"], Assert.Single(preview.Changes).RemovedTags);
        Assert.Equal(["meta:genre:animation", "Favorite"], target.Tags);
        var applied = await controller.ApplyCleanupAsync(
            new MetaTaggerCleanupApplyRequest { Token = preview.Token! }, CancellationToken.None);
        Assert.Equal(1, applied.Value!.WritesApplied);
        Assert.Equal(["Favorite"], target.Tags);
    }

    [Fact]
    public async Task Cleanup_WriteBudget_LeavesUnwrittenOwnershipForNextPreview()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.MaxWritesPerRun = 1;
        var first = WritableMovie("01010101-0101-0101-0101-010101010101", "Animation");
        var second = WritableMovie("02020202-0202-0202-0202-020202020202", "Drama");
        var store = new MetaTaggerStateStore(_directory);
        var state = new MetaTaggerState();
        foreach (var item in new[] { first, second })
        {
            item.Tags = ["meta:old", "Favorite"];
            state.Items[item.Id.ToString("N")] = new MetaTaggerStateItem
            {
                ItemId = item.Id.ToString("N"), ItemType = "Movie", LastAppliedTags = ["meta:old"]
            };
        }

        await store.SaveAsync(state, CancellationToken.None);
        var runner = CreateRunner(new InMemoryMetaTaggerHost(configuration, [first, second]), store);
        var preview = await runner.PreviewCleanupAsync(null, new NoOpProgress(), CancellationToken.None);
        var result = await runner.ApplyCleanupAsync(preview.Token!, new NoOpProgress(), CancellationToken.None);

        Assert.True(result.BudgetLimitReached);
        Assert.Equal(1, result.WritesApplied);
        Assert.Equal(1, result.ItemsRemaining);
        Assert.Equal(["Favorite"], first.Tags);
        Assert.Equal(["meta:old", "Favorite"], second.Tags);
        var remaining = await runner.PreviewCleanupAsync(null, new NoOpProgress(), CancellationToken.None);
        Assert.Equal(second.Id.ToString("N"), Assert.Single(remaining.Changes).ItemId);
        await runner.ApplyCleanupAsync(remaining.Token!, new NoOpProgress(), CancellationToken.None);
        Assert.Equal(["Favorite"], second.Tags);
    }

    [Fact]
    public async Task Cleanup_LocksAndManualOptOut_ProtectItemsBeforePreviewAndApply()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.ManualTagPrefix = "control";
        configuration.TagSeparator = "|";
        var locked = WritableMovie("01010101-0101-0101-0101-010101010101", "Animation");
        var skipped = WritableMovie("02020202-0202-0202-0202-020202020202", "Drama");
        var changed = WritableMovie("03030303-0303-0303-0303-030303030303", "Comedy");
        locked.IsLocked = true;
        locked.Tags = ["meta:old"];
        skipped.Tags = ["meta:old", "control|tagger|skip"];
        changed.Tags = ["meta:old"];
        var store = new MetaTaggerStateStore(_directory);
        var state = new MetaTaggerState();
        foreach (var item in new[] { locked, skipped, changed })
        {
            state.Items[item.Id.ToString("N")] = new MetaTaggerStateItem
            {
                ItemId = item.Id.ToString("N"), ItemType = "Movie", LastAppliedTags = ["meta:old"]
            };
        }

        await store.SaveAsync(state, CancellationToken.None);
        var host = new InMemoryMetaTaggerHost(configuration, [locked, skipped, changed]);
        var runner = CreateRunner(host, store);
        var preview = await runner.PreviewCleanupAsync(null, new NoOpProgress(), CancellationToken.None);
        Assert.Equal(1, preview.Summary.ItemsSkippedLocked);
        Assert.Equal(1, preview.Summary.ItemsSkippedManual);
        Assert.Equal(changed.Id.ToString("N"), Assert.Single(preview.Changes).ItemId);
        changed.LockedFields = [MediaBrowser.Model.Entities.MetadataField.Tags];

        var result = await runner.ApplyCleanupAsync(preview.Token!, new NoOpProgress(), CancellationToken.None);

        Assert.Equal(1, result.ItemsSkippedLocked);
        Assert.Empty(host.UpdateAttempts);
        Assert.Equal(["meta:old"], changed.Tags);
        Assert.All((await store.LoadAsync(CancellationToken.None)).Items.Values,
            entry => Assert.Equal(["meta:old"], entry.LastAppliedTags));
    }

    [Fact]
    public async Task Cleanup_ConfigurationChangedAfterPreview_RejectsApplyWithoutWrites()
    {
        var configuration = ConfigurationWithNoItemTypes();
        var movie = WritableMovie("01010101-0101-0101-0101-010101010101", "Animation");
        movie.Tags = ["meta:old"];
        var store = new MetaTaggerStateStore(_directory);
        var state = new MetaTaggerState();
        state.Items[movie.Id.ToString("N")] = new MetaTaggerStateItem
        {
            ItemId = movie.Id.ToString("N"), LastAppliedTags = ["meta:old"]
        };
        await store.SaveAsync(state, CancellationToken.None);
        var host = new InMemoryMetaTaggerHost(configuration, [movie]);
        var runner = CreateRunner(host, store);
        var preview = await runner.PreviewCleanupAsync(null, new NoOpProgress(), CancellationToken.None);
        configuration.ConfigurationRevision = Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.ApplyCleanupAsync(preview.Token!, new NoOpProgress(), CancellationToken.None));

        Assert.Empty(host.UpdateAttempts);
        Assert.Equal(["meta:old"], movie.Tags);
    }

    [Fact]
    public async Task Cleanup_CancellationAfterWrite_CheckpointsRemovalAndPreservesRemainingOwnership()
    {
        var first = WritableMovie("01010101-0101-0101-0101-010101010101", "Animation");
        var second = WritableMovie("02020202-0202-0202-0202-020202020202", "Drama");
        var state = new MetaTaggerState();
        foreach (var item in new[] { first, second })
        {
            item.Tags = ["meta:old"];
            state.Items[item.Id.ToString("N")] = new MetaTaggerStateItem
            {
                ItemId = item.Id.ToString("N"), ItemType = "Movie", LastAppliedTags = ["meta:old"]
            };
        }

        var store = new MetaTaggerStateStore(_directory);
        await store.SaveAsync(state, CancellationToken.None);
        var host = new InMemoryMetaTaggerHost(ConfigurationWithNoItemTypes(), [first, second]);
        using var cancellation = new CancellationTokenSource();
        var runner = CreateRunner(host, store, new CancelDuringDelayMetaTaggerClock(cancellation));
        var preview = await runner.PreviewCleanupAsync(null, new NoOpProgress(), CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.ApplyCleanupAsync(preview.Token!, new NoOpProgress(), cancellation.Token));

        Assert.Empty(first.Tags);
        Assert.Equal(["meta:old"], second.Tags);
        state = await store.LoadAsync(CancellationToken.None);
        Assert.Empty(state.Items[first.Id.ToString("N")].LastAppliedTags);
        Assert.Equal(["meta:old"], state.Items[second.Id.ToString("N")].LastAppliedTags);
        var remaining = await CreateRunner(host, store).PreviewCleanupAsync(null, new NoOpProgress(), CancellationToken.None);
        Assert.Equal(second.Id.ToString("N"), Assert.Single(remaining.Changes).ItemId);
    }

    [Fact]
    public async Task Cleanup_ItemWriteFailure_ContinuesAndKeepsFailedItemsOwnership()
    {
        var first = WritableMovie("01010101-0101-0101-0101-010101010101", "Animation");
        var second = WritableMovie("02020202-0202-0202-0202-020202020202", "Drama");
        var state = new MetaTaggerState();
        foreach (var item in new[] { first, second })
        {
            item.Tags = ["meta:old"];
            state.Items[item.Id.ToString("N")] = new MetaTaggerStateItem
            {
                ItemId = item.Id.ToString("N"), ItemType = "Movie", LastAppliedTags = ["meta:old"]
            };
        }

        var store = new MetaTaggerStateStore(_directory);
        await store.SaveAsync(state, CancellationToken.None);
        var host = new InMemoryMetaTaggerHost(ConfigurationWithNoItemTypes(), [first, second],
            failWriteItemId: first.Id.ToString("N"));
        var runner = CreateRunner(host, store);
        var preview = await runner.PreviewCleanupAsync(null, new NoOpProgress(), CancellationToken.None);
        var result = await runner.ApplyCleanupAsync(preview.Token!, new NoOpProgress(), CancellationToken.None);

        Assert.Equal(1, result.Failures);
        Assert.Equal(1, result.WritesApplied);
        Assert.Equal(["meta:old"], first.Tags);
        Assert.Empty(second.Tags);
        var remaining = await runner.PreviewCleanupAsync(null, new NoOpProgress(), CancellationToken.None);
        Assert.Equal(first.Id.ToString("N"), Assert.Single(remaining.Changes).ItemId);
    }

    [Fact]
    public async Task Cleanup_UnownedLibraryRecords_DoNotConsumeBudgetOrAcquireLedgerEntries()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.MaxItemsPerRun = 1;
        var unrelated = new Folder
        {
            Id = Guid.Parse("01010101-0101-0101-0101-010101010101"), Tags = ["Favorite"]
        };
        var owned = WritableMovie("02020202-0202-0202-0202-020202020202", "Drama");
        owned.Tags = ["meta:genre:drama"];
        var state = new MetaTaggerState();
        state.Items[owned.Id.ToString("N")] = new MetaTaggerStateItem
        {
            ItemId = owned.Id.ToString("N"), ItemType = "Movie", LastAppliedTags = owned.Tags
        };
        var store = new MetaTaggerStateStore(_directory);
        await store.SaveAsync(state, CancellationToken.None);
        var runner = CreateRunner(new InMemoryMetaTaggerHost(configuration, [unrelated, owned]), store);

        var preview = await runner.PreviewCleanupAsync(null, new NoOpProgress(), CancellationToken.None);

        Assert.Equal(owned.Id.ToString("N"), Assert.Single(preview.Changes).ItemId);
        Assert.Equal(1, preview.Summary.ItemsScanned);
        Assert.False(preview.Summary.BudgetLimitReached);
        Assert.Equal(owned.Id.ToString("N"), Assert.Single((await store.LoadAsync(CancellationToken.None)).Items).Key);
    }

    [Fact]
    public async Task Cleanup_NewPreviewAfterTagging_ProcessesItsWholeApprovedPlan()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.MaxWritesPerRun = 1;
        var first = WritableMovie("01010101-0101-0101-0101-010101010101", "Animation");
        var second = WritableMovie("02020202-0202-0202-0202-020202020202", "Drama");
        var state = new MetaTaggerState();
        foreach (var item in new[] { first, second })
        {
            item.Tags = ["meta:old"];
            state.Items[item.Id.ToString("N")] = new MetaTaggerStateItem
            {
                ItemId = item.Id.ToString("N"), ItemType = "Movie", LastAppliedTags = ["meta:old"]
            };
        }

        var store = new MetaTaggerStateStore(_directory);
        await store.SaveAsync(state, CancellationToken.None);
        var runner = CreateRunner(new InMemoryMetaTaggerHost(configuration, [first, second]), store);
        var preview = await runner.PreviewCleanupAsync(null, new NoOpProgress(), CancellationToken.None);
        await runner.ApplyCleanupAsync(preview.Token!, new NoOpProgress(), CancellationToken.None);
        Assert.Empty(first.Tags);
        await runner.RunAsync(new NoOpProgress(), CancellationToken.None,
            new MetaTaggerRunOptions { PreviewOnly = false });
        Assert.Equal(["meta:genre:animation"], first.Tags);
        var freshPreview = await runner.PreviewCleanupAsync(null, new NoOpProgress(), CancellationToken.None);
        Assert.Equal(2, freshPreview.Changes.Count);

        var result = await runner.ApplyCleanupAsync(freshPreview.Token!, new NoOpProgress(), CancellationToken.None);

        Assert.Empty(first.Tags);
        Assert.Equal(["meta:old"], second.Tags);
        Assert.Equal(1, result.ItemsRemaining);
        Assert.True(result.BudgetLimitReached);
    }

    [Fact]
    public async Task RunAsync_PreviewChanges_BindsSummaryToConfigurationAndExport()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        var host = new InMemoryMetaTaggerHost(
            configuration,
            [
                new Movie
                {
                    Id = Guid.Parse("01010101-0101-0101-0101-010101010101"),
                    Genres = ["Animation"]
                }
            ]);
        var runner = CreateRunner(host, new MetaTaggerStateStore(_directory));

        var summary = await runner.RunAsync(
            new NoOpProgress(),
            CancellationToken.None,
            new MetaTaggerRunOptions { PreviewOnly = true });

        Assert.Equal(configuration.ConfigurationRevision, summary.ConfigurationRevision);
        Assert.False(string.IsNullOrWhiteSpace(summary.PreviewChangesChecksum));
        Assert.Equal(1, summary.PreviewChangesExported);
    }

    [Fact]
    public async Task ScheduledTagTask_Disabled_RetainsAllArmedActions()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IsEnabled = false;
        configuration.ForceFullScanOnNextRun = true;
        configuration.RebuildTrackingLedgerOnNextRun = true;
        configuration.ClaimExistingGeneratedTagsOnNextRun = true;
        var host = new InMemoryMetaTaggerHost(configuration, failOnQuery: true);
        var runner = CreateRunner(host, new FailOnAccessMetaTaggerStateStore());
        var task = new ScheduledTagTask(runner);

        await task.ExecuteAsync(new NoOpProgress(), CancellationToken.None);

        Assert.True(configuration.ForceFullScanOnNextRun);
        Assert.True(configuration.RebuildTrackingLedgerOnNextRun);
        Assert.True(configuration.ClaimExistingGeneratedTagsOnNextRun);
        Assert.Equal(0, host.ConfigurationSaveCount);
    }

    [Fact]
    public async Task ScheduledTagTask_CompletedZeroItemCycle_AcknowledgesForce()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.ForceFullScanOnNextRun = true;
        configuration.ClaimExistingGeneratedTagsForCleanup = true;
        var host = new InMemoryMetaTaggerHost(configuration);
        var runner = CreateRunner(host, new InMemoryMetaTaggerStateStore(new MetaTaggerState()));
        var task = new ScheduledTagTask(runner);

        await task.ExecuteAsync(new NoOpProgress(), CancellationToken.None);

        Assert.False(configuration.ForceFullScanOnNextRun);
        Assert.True(configuration.ClaimExistingGeneratedTagsForCleanup);
        Assert.Equal(1, host.ConfigurationSaveCount);
    }

    [Fact]
    public async Task RunDefaultScheduledAsync_StateLoadFallback_RetainsSelectedActions()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.ForceFullScanOnNextRun = true;
        var host = new InMemoryMetaTaggerHost(configuration);
        var runner = CreateRunner(host, new LoadFailingMetaTaggerStateStore());

        var summary = await runner.RunDefaultScheduledAsync(new NoOpProgress(), CancellationToken.None);

        Assert.True(summary.PreviewOnly);
        Assert.True(configuration.ForceFullScanOnNextRun);
    }

    [Fact]
    public async Task RunDefaultScheduledAsync_LedgerWriteFallback_RetainsSelectedActions()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.PreviewOnly = false;
        configuration.StaleTagMode = StaleTagMode.Remove;
        configuration.ForceFullScanOnNextRun = true;
        var host = new InMemoryMetaTaggerHost(configuration);
        var runner = CreateRunner(host, new FirstSaveFailingMetaTaggerStateStore());

        var summary = await runner.RunDefaultScheduledAsync(new NoOpProgress(), CancellationToken.None);

        Assert.True(summary.PreviewOnly);
        Assert.True(configuration.ForceFullScanOnNextRun);
    }

    [Fact]
    public async Task RunConfiguredDefaultAsync_CompletedCycle_DoesNotConsumeScheduledActions()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.ForceFullScanOnNextRun = true;
        configuration.RebuildTrackingLedgerOnNextRun = true;
        configuration.ClaimExistingGeneratedTagsOnNextRun = true;
        var host = new InMemoryMetaTaggerHost(configuration);
        var runner = CreateRunner(host, new InMemoryMetaTaggerStateStore(new MetaTaggerState()));

        var summary = await runner.RunConfiguredDefaultAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(MetadataTagRunMode.RebuildTrackingLedger.ToString(), summary.RunMode);
        Assert.True(configuration.ForceFullScanOnNextRun);
        Assert.True(configuration.RebuildTrackingLedgerOnNextRun);
        Assert.True(configuration.ClaimExistingGeneratedTagsOnNextRun);
    }

    [Fact]
    public async Task LibraryPostScanTask_ConfiguredDefault_DoesNotConsumeScheduledActions()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.RunAfterLibraryScan = true;
        configuration.MinimumMinutesBetweenAutoRuns = 0;
        configuration.ForceFullScanOnNextRun = true;
        var host = new InMemoryMetaTaggerHost(configuration);
        var stateStore = new MetaTaggerStateStore(_directory);
        var runner = CreateRunner(host, stateStore);
        var task = new LibraryPostScanTask(runner, stateStore, new MetaTaggerClock());

        await task.Run(new NoOpProgress(), CancellationToken.None);

        Assert.True(configuration.ForceFullScanOnNextRun);
        Assert.Equal(1, host.ConfigurationSaveCount);
    }

    [Fact]
    public async Task RunDefaultScheduledAsync_AllItemTypesDisabled_RetainsAllArmedActions()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.ForceFullScanOnNextRun = true;
        configuration.RebuildTrackingLedgerOnNextRun = true;
        configuration.ClaimExistingGeneratedTagsOnNextRun = true;
        var host = new InMemoryMetaTaggerHost(configuration, failOnQuery: true);
        var runner = CreateRunner(host, new FailOnAccessMetaTaggerStateStore());

        await runner.RunDefaultScheduledAsync(new NoOpProgress(), CancellationToken.None);

        Assert.True(configuration.ForceFullScanOnNextRun);
        Assert.True(configuration.RebuildTrackingLedgerOnNextRun);
        Assert.True(configuration.ClaimExistingGeneratedTagsOnNextRun);
        Assert.Equal(0, host.ConfigurationSaveCount);
    }

    [Fact]
    public async Task RunDefaultScheduledAsync_RunCancellation_RetainsAllArmedActions()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.ForceFullScanOnNextRun = true;
        configuration.RebuildTrackingLedgerOnNextRun = true;
        configuration.ClaimExistingGeneratedTagsOnNextRun = true;
        var host = new InMemoryMetaTaggerHost(
            configuration,
            [new Movie { Id = Guid.Parse("10101010-1010-1010-1010-101010101010") }]);
        var runner = CreateRunner(host, new InMemoryMetaTaggerStateStore(new MetaTaggerState()));
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunDefaultScheduledAsync(new CancelOnReportProgress(cancellation), cancellation.Token));

        Assert.True(configuration.ForceFullScanOnNextRun);
        Assert.True(configuration.RebuildTrackingLedgerOnNextRun);
        Assert.True(configuration.ClaimExistingGeneratedTagsOnNextRun);
        Assert.Equal(0, host.ConfigurationSaveCount);
    }

    [Fact]
    public async Task RunDefaultScheduledAsync_BudgetedCycle_ResumesBeforeAcknowledgingSelectedActions()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.MaxItemsPerRun = 1;
        configuration.ForceFullScanOnNextRun = true;
        configuration.RebuildTrackingLedgerOnNextRun = true;
        configuration.ClaimExistingGeneratedTagsOnNextRun = true;
        var host = new InMemoryMetaTaggerHost(
            configuration,
            [
                new Movie { Id = Guid.Parse("20202020-2020-2020-2020-202020202020") },
                new Movie { Id = Guid.Parse("30303030-3030-3030-3030-303030303030") }
            ]);
        var stateStore = new InMemoryMetaTaggerStateStore(new MetaTaggerState());
        var runner = CreateRunner(host, stateStore);

        var first = await runner.RunDefaultScheduledAsync(new NoOpProgress(), CancellationToken.None);

        Assert.True(first.BudgetLimitReached);
        Assert.Equal(1, first.ItemsScanned);
        Assert.Equal(1, first.ItemsRemaining);
        Assert.True(configuration.ForceFullScanOnNextRun);
        Assert.True(configuration.RebuildTrackingLedgerOnNextRun);
        Assert.True(configuration.ClaimExistingGeneratedTagsOnNextRun);

        var second = await runner.RunDefaultScheduledAsync(new NoOpProgress(), CancellationToken.None);

        Assert.False(second.BudgetLimitReached);
        Assert.Equal(1, second.ItemsScanned);
        Assert.Equal(0, second.ItemsRemaining);
        Assert.True(configuration.ForceFullScanOnNextRun);
        Assert.False(configuration.RebuildTrackingLedgerOnNextRun);
        Assert.False(configuration.ClaimExistingGeneratedTagsOnNextRun);
        Assert.Empty((await stateStore.LoadAsync(CancellationToken.None)).RunCursors);
    }

    [Fact]
    public async Task RunDefaultScheduledAsync_RebuildAndForce_ResumesLegacyForceProfileCursor()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.MaxItemsPerRun = 1;
        configuration.ForceFullScanOnNextRun = true;
        configuration.RebuildTrackingLedgerOnNextRun = true;
        var host = new InMemoryMetaTaggerHost(
            configuration,
            [
                new Movie { Id = Guid.Parse("31313131-3131-3131-3131-313131313131") },
                new Movie { Id = Guid.Parse("32323232-3232-3232-3232-323232323232") }
            ]);
        var stateStore = new InMemoryMetaTaggerStateStore(new MetaTaggerState());
        var runner = CreateRunner(host, stateStore);

        var partial = await runner.RunAsync(
            new NoOpProgress(),
            CancellationToken.None,
            new MetaTaggerRunOptions
            {
                RunMode = MetadataTagRunMode.RebuildTrackingLedger,
                PreviewOnly = true,
                Force = true
            });

        Assert.Equal(1, partial.ItemsRemaining);

        var completed = await runner.RunDefaultScheduledAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(1, completed.ItemsScanned);
        Assert.Equal(0, completed.ItemsRemaining);
        Assert.True(configuration.ForceFullScanOnNextRun);
        Assert.False(configuration.RebuildTrackingLedgerOnNextRun);
        Assert.Empty((await stateStore.LoadAsync(CancellationToken.None)).RunCursors);
    }

    [Fact]
    public async Task RunDefaultScheduledAsync_IsolatedItemFailure_RetainsSelectedActions()
    {
        const string itemId = "40404040404040404040404040404040";
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.PreviewOnly = false;
        configuration.ForceFullScanOnNextRun = true;
        var host = new InMemoryMetaTaggerHost(
            configuration,
            [WritableMovie("40404040-4040-4040-4040-404040404040", "Drama")],
            failWriteItemId: itemId);
        var runner = CreateRunner(host, new InMemoryMetaTaggerStateStore(new MetaTaggerState()));

        var summary = await runner.RunDefaultScheduledAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(1, summary.Failures);
        Assert.True(configuration.ForceFullScanOnNextRun);
    }

    [Fact]
    public async Task RunDefaultScheduledAsync_StatePublicationFailure_RetainsSelectedActions()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.ForceFullScanOnNextRun = true;
        var host = new InMemoryMetaTaggerHost(configuration);
        var runner = CreateRunner(host, new FailingCheckpointMetaTaggerStateStore());

        await Assert.ThrowsAsync<IOException>(
            () => runner.RunDefaultScheduledAsync(new NoOpProgress(), CancellationToken.None));

        Assert.True(configuration.ForceFullScanOnNextRun);
        Assert.Equal(0, host.ConfigurationSaveCount);
    }

    [Fact]
    public async Task RunDefaultScheduledAsync_PreviewPublicationFailure_RetainsSelectedActions()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.ForceFullScanOnNextRun = true;
        var host = new InMemoryMetaTaggerHost(
            configuration,
            [WritableMovie("50505050-5050-5050-5050-505050505050", "Comedy")]);
        var runner = CreateRunner(host, new PreviewFailingMetaTaggerStateStore());

        await Assert.ThrowsAsync<IOException>(
            () => runner.RunDefaultScheduledAsync(new NoOpProgress(), CancellationToken.None));

        Assert.True(configuration.ForceFullScanOnNextRun);
        Assert.Equal(0, host.ConfigurationSaveCount);
    }

    [Fact]
    public async Task RunDefaultScheduledAsync_SummaryPublicationFailure_RetainsSelectedActions()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.ForceFullScanOnNextRun = true;
        var host = new InMemoryMetaTaggerHost(configuration);
        var runner = CreateRunner(host, new SummaryFailingMetaTaggerStateStore());

        await Assert.ThrowsAsync<IOException>(
            () => runner.RunDefaultScheduledAsync(new NoOpProgress(), CancellationToken.None));

        Assert.True(configuration.ForceFullScanOnNextRun);
        Assert.Equal(0, host.ConfigurationSaveCount);
    }

    [Fact]
    public async Task RunDefaultScheduledAsync_ConfigurationSaveFailure_RestoresAttemptedActionsAndThrows()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.ForceFullScanOnNextRun = true;
        configuration.RebuildTrackingLedgerOnNextRun = true;
        configuration.ClaimExistingGeneratedTagsOnNextRun = true;
        var host = new InMemoryMetaTaggerHost(configuration, failOnConfigurationSave: true);
        var runner = CreateRunner(host, new InMemoryMetaTaggerStateStore(new MetaTaggerState()));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunDefaultScheduledAsync(new NoOpProgress(), CancellationToken.None));

        Assert.Equal("Injected configuration save failure.", exception.Message);
        var attempted = Assert.Single(host.ConfigurationSaveAttempts);
        Assert.True(attempted.Force);
        Assert.False(attempted.Rebuild);
        Assert.False(attempted.Claim);
        Assert.True(configuration.ForceFullScanOnNextRun);
        Assert.True(configuration.RebuildTrackingLedgerOnNextRun);
        Assert.True(configuration.ClaimExistingGeneratedTagsOnNextRun);
        Assert.NotEqual("No runs recorded.", configuration.LastRunSummaryText);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task RunDefaultScheduledAsync_ConfigurationSaveFailure_RestoresExactOriginalActions(
        bool force,
        bool rebuild,
        bool claim)
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.ForceFullScanOnNextRun = force;
        configuration.RebuildTrackingLedgerOnNextRun = rebuild;
        configuration.ClaimExistingGeneratedTagsOnNextRun = claim;
        var host = new InMemoryMetaTaggerHost(configuration, failOnConfigurationSave: true);
        var runner = CreateRunner(host, new InMemoryMetaTaggerStateStore(new MetaTaggerState()));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunDefaultScheduledAsync(new NoOpProgress(), CancellationToken.None));

        Assert.Equal("Injected configuration save failure.", exception.Message);
        Assert.Equal((false, false, false), Assert.Single(host.ConfigurationSaveAttempts));
        Assert.Equal(force, configuration.ForceFullScanOnNextRun);
        Assert.Equal(rebuild, configuration.RebuildTrackingLedgerOnNextRun);
        Assert.Equal(claim, configuration.ClaimExistingGeneratedTagsOnNextRun);
    }

    [Fact]
    public async Task RunDefaultScheduledAsync_ReplacedConfiguration_RetainsReplacementAndCapturedIntent()
    {
        var capturedConfiguration = ConfigurationWithNoItemTypes();
        capturedConfiguration.IncludeMovies = true;
        capturedConfiguration.ForceFullScanOnNextRun = true;
        var replacementConfiguration = ConfigurationWithNoItemTypes();
        replacementConfiguration.IncludeMovies = true;
        replacementConfiguration.GeneratedTagPrefix = "replacement";
        replacementConfiguration.RebuildTrackingLedgerOnNextRun = true;
        replacementConfiguration.ClaimExistingGeneratedTagsOnNextRun = true;
        var host = new InMemoryMetaTaggerHost(capturedConfiguration);
        var stateStore = new BlockingLoadMetaTaggerStateStore();
        var runner = CreateRunner(host, stateStore);

        var run = runner.RunDefaultScheduledAsync(new NoOpProgress(), CancellationToken.None);
        try
        {
            await stateStore.FirstLoadEntered.WaitAsync(AsyncTestTimeout);
            host.ReplaceConfiguration(replacementConfiguration);
        }
        finally
        {
            stateStore.ReleaseLoads();
        }

        await run.WaitAsync(AsyncTestTimeout);

        Assert.True(capturedConfiguration.ForceFullScanOnNextRun);
        Assert.Equal("No runs recorded.", capturedConfiguration.LastRunSummaryText);
        Assert.Same(replacementConfiguration, host.CurrentConfiguration);
        Assert.Equal("replacement", replacementConfiguration.GeneratedTagPrefix);
        Assert.False(replacementConfiguration.ForceFullScanOnNextRun);
        Assert.True(replacementConfiguration.RebuildTrackingLedgerOnNextRun);
        Assert.True(replacementConfiguration.ClaimExistingGeneratedTagsOnNextRun);
        Assert.NotEqual("No runs recorded.", replacementConfiguration.LastRunSummaryText);
        Assert.Same(replacementConfiguration, Assert.Single(host.SavedConfigurations));
    }

    [Fact]
    public async Task RunDefaultScheduledAsync_NewActionOnCapturedConfiguration_RemainsArmed()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.ForceFullScanOnNextRun = true;
        var host = new InMemoryMetaTaggerHost(configuration);
        var stateStore = new BlockingLoadMetaTaggerStateStore();
        var runner = CreateRunner(host, stateStore);

        var run = runner.RunDefaultScheduledAsync(new NoOpProgress(), CancellationToken.None);
        try
        {
            await stateStore.FirstLoadEntered.WaitAsync(AsyncTestTimeout);
            configuration.RebuildTrackingLedgerOnNextRun = true;
        }
        finally
        {
            stateStore.ReleaseLoads();
        }

        await run.WaitAsync(AsyncTestTimeout);

        Assert.False(configuration.ForceFullScanOnNextRun);
        Assert.True(configuration.RebuildTrackingLedgerOnNextRun);
    }

    [Theory]
    [InlineData(true, false, false, false, false, false)]
    [InlineData(false, false, true, false, false, false)]
    [InlineData(true, true, false, true, false, false)]
    [InlineData(true, false, true, false, false, false)]
    [InlineData(true, true, true, true, false, false)]
    public async Task RunDefaultScheduledAsync_CleanCycle_AcknowledgesOnlySelectedActions(
        bool force,
        bool rebuild,
        bool claim,
        bool expectedForce,
        bool expectedRebuild,
        bool expectedClaim)
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.EnableExistingTagsAsKeywords = false;
        configuration.ForceFullScanOnNextRun = force;
        configuration.RebuildTrackingLedgerOnNextRun = rebuild;
        configuration.ClaimExistingGeneratedTagsOnNextRun = claim;
        var host = new InMemoryMetaTaggerHost(
            configuration,
            [
                new Movie
                {
                    Id = Guid.Parse("60606060-6060-6060-6060-606060606060"),
                    Tags = ["meta:genre:legacy"]
                }
            ]);
        var runner = CreateRunner(host, new InMemoryMetaTaggerStateStore(new MetaTaggerState()));

        var summary = await runner.RunDefaultScheduledAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(claim ? 1 : 0, summary.LegacyTagsClaimed);
        Assert.Equal(expectedForce, configuration.ForceFullScanOnNextRun);
        Assert.Equal(expectedRebuild, configuration.RebuildTrackingLedgerOnNextRun);
        Assert.Equal(expectedClaim, configuration.ClaimExistingGeneratedTagsOnNextRun);
    }

    [Fact]
    public async Task RunAsync_ConfiguredActions_DoNotConsumeScheduledActions()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.ForceFullScanOnNextRun = true;
        configuration.RebuildTrackingLedgerOnNextRun = true;
        configuration.ClaimExistingGeneratedTagsOnNextRun = true;
        var host = new InMemoryMetaTaggerHost(configuration);
        var runner = CreateRunner(host, new InMemoryMetaTaggerStateStore(new MetaTaggerState()));

        var summary = await runner.RunAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(MetadataTagRunMode.RebuildTrackingLedger.ToString(), summary.RunMode);
        Assert.True(configuration.ForceFullScanOnNextRun);
        Assert.True(configuration.RebuildTrackingLedgerOnNextRun);
        Assert.True(configuration.ClaimExistingGeneratedTagsOnNextRun);
    }

    [Fact]
    public async Task RunAsync_ExplicitOptions_DoNotConsumeScheduledActions()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.ForceFullScanOnNextRun = true;
        configuration.RebuildTrackingLedgerOnNextRun = true;
        configuration.ClaimExistingGeneratedTagsOnNextRun = true;
        var host = new InMemoryMetaTaggerHost(configuration);
        var runner = CreateRunner(host, new InMemoryMetaTaggerStateStore(new MetaTaggerState()));

        var summary = await runner.RunAsync(
            new NoOpProgress(),
            CancellationToken.None,
            new MetaTaggerRunOptions
            {
                RunMode = MetadataTagRunMode.Incremental,
                PreviewOnly = true
            });

        Assert.Equal(MetadataTagRunMode.Incremental.ToString(), summary.RunMode);
        Assert.True(configuration.ForceFullScanOnNextRun);
        Assert.True(configuration.RebuildTrackingLedgerOnNextRun);
        Assert.True(configuration.ClaimExistingGeneratedTagsOnNextRun);
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("apply")]
    [InlineData("force")]
    [InlineData("rebuild")]
    public async Task ExplicitScheduledTaskOptions_DoNotConsumeScheduledActions(string taskName)
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.ForceFullScanOnNextRun = true;
        configuration.RebuildTrackingLedgerOnNextRun = true;
        configuration.ClaimExistingGeneratedTagsOnNextRun = true;
        var host = new InMemoryMetaTaggerHost(configuration);
        var runner = CreateRunner(host, new InMemoryMetaTaggerStateStore(new MetaTaggerState()));
        MetaTaggerScheduledTaskBase task = taskName switch
        {
            "preview" => new PreviewMetadataTagTask(runner),
            "apply" => new ApplyMetadataTagTask(runner),
            "force" => new ForceFullMetadataTagScanTask(runner),
            "rebuild" => new RebuildMetadataTagLedgerTask(runner),
            _ => throw new ArgumentOutOfRangeException(nameof(taskName))
        };

        await task.ExecuteAsync(new NoOpProgress(), CancellationToken.None);

        Assert.True(configuration.ForceFullScanOnNextRun);
        Assert.True(configuration.RebuildTrackingLedgerOnNextRun);
        Assert.True(configuration.ClaimExistingGeneratedTagsOnNextRun);
    }

    [Fact]
    public async Task RunAsync_AllItemTypesDisabled_DoesNotQueryOrPersist()
    {
        var host = new InMemoryMetaTaggerHost(ConfigurationWithNoItemTypes(), failOnQuery: true);
        var stateStore = new FailOnAccessMetaTaggerStateStore();
        var runner = CreateRunner(host, stateStore);

        var summary = await runner.RunAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Empty(host.Queries);
        Assert.Equal(0, summary.ItemsScanned);
        Assert.Equal(0, summary.ItemsChanged);
        Assert.Equal(0, summary.WritesApplied);
        Assert.Equal(0, summary.LedgerEntriesPruned);
        Assert.Empty(stateStore.Operations);
    }

    [Fact]
    public async Task RunAsync_OnlyMoviesEnabled_QueriesExactlyMovies()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        var host = new InMemoryMetaTaggerHost(configuration);
        var runner = CreateRunner(host);

        await runner.RunAsync(new NoOpProgress(), CancellationToken.None);

        var query = Assert.Single(host.Queries);
        Assert.Equal([BaseItemKind.Movie], query);
    }

    [Fact]
    public async Task RunAsync_JellyfinMetadataLockedItem_DoesNotWriteOrTrackTags()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        var movie = WritableMovie("12121212-1212-1212-1212-121212121212", "Animation");
        movie.IsLocked = true;
        var host = new InMemoryMetaTaggerHost(configuration, [movie]);
        var stateStore = new InMemoryMetaTaggerStateStore(new MetaTaggerState());
        var runner = CreateRunner(host, stateStore);

        var summary = await runner.RunAsync(
            new NoOpProgress(),
            CancellationToken.None,
            new MetaTaggerRunOptions { PreviewOnly = false });

        Assert.Equal(1, summary.ItemsScanned);
        Assert.Equal(1, summary.ItemsSkippedLocked);
        Assert.Empty(host.UpdateAttempts);
        Assert.Empty((await stateStore.LoadAsync(CancellationToken.None)).Items);
    }

    [Fact]
    public async Task RunAsync_JellyfinTagsLockedItem_DoesNotWriteOrTrackTags()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        var movie = WritableMovie("13131313-1313-1313-1313-131313131313", "Family");
        movie.LockedFields = [MediaBrowser.Model.Entities.MetadataField.Tags];
        var host = new InMemoryMetaTaggerHost(configuration, [movie]);
        var stateStore = new InMemoryMetaTaggerStateStore(new MetaTaggerState());
        var runner = CreateRunner(host, stateStore);

        var summary = await runner.RunAsync(
            new NoOpProgress(),
            CancellationToken.None,
            new MetaTaggerRunOptions { PreviewOnly = false });

        Assert.Equal(1, summary.ItemsScanned);
        Assert.Equal(1, summary.ItemsSkippedLocked);
        Assert.Empty(host.UpdateAttempts);
        Assert.Empty((await stateStore.LoadAsync(CancellationToken.None)).Items);
    }

    [Fact]
    public async Task RunAsync_CanceledDuringPostWriteDelay_CheckpointsOwnership()
    {
        const string itemId = "11111111111111111111111111111111";
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.WriteDelayMilliseconds = 250;
        var host = new InMemoryMetaTaggerHost(
            configuration,
            [
                new Movie
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Genres = ["Animation"]
                }
            ]);
        using var cancellation = new CancellationTokenSource();
        var clock = new CancelDuringDelayMetaTaggerClock(cancellation);
        var runner = CreateRunner(host, new MetaTaggerStateStore(_directory), clock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(
                new NoOpProgress(),
                cancellation.Token,
                new MetaTaggerRunOptions { PreviewOnly = false }));

        var write = Assert.Single(host.UpdateAttempts);
        Assert.Equal(itemId, write.ItemId);
        Assert.Equal(["meta:genre:animation"], write.Tags);
        Assert.Equal([TimeSpan.FromMilliseconds(250)], clock.Delays);

        var reloaded = await new MetaTaggerStateStore(_directory).LoadAsync(CancellationToken.None);
        var ledgerEntry = Assert.Single(reloaded.Items);
        Assert.Equal(itemId, ledgerEntry.Key);
        Assert.Equal(["meta:genre:animation"], ledgerEntry.Value.LastAppliedTags);
        var cursor = Assert.Single(reloaded.RunCursors).Value;
        Assert.Equal(1, cursor.NextIndex);
        Assert.Empty(PendingIds(cursor));
    }

    [Fact]
    public async Task RunAsync_LaterWriteFailure_PreservesEarlierCheckpoint()
    {
        const string firstItemId = "22222222222222222222222222222222";
        const string secondItemId = "33333333333333333333333333333333";
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        var host = new InMemoryMetaTaggerHost(
            configuration,
            [
                WritableMovie("22222222-2222-2222-2222-222222222222", "Drama"),
                WritableMovie("33333333-3333-3333-3333-333333333333", "Comedy")
            ],
            failWriteItemId: secondItemId);
        var stateStore = new InMemoryMetaTaggerStateStore(new MetaTaggerState());
        var runner = CreateRunner(host, stateStore);

        var summary = await runner.RunAsync(
            new NoOpProgress(),
            CancellationToken.None,
            new MetaTaggerRunOptions { PreviewOnly = false });

        Assert.Equal(2, summary.ItemsScanned);
        Assert.Equal(1, summary.WritesApplied);
        Assert.Equal(1, summary.Failures);
        Assert.Equal([firstItemId, secondItemId], host.UpdateAttempts.Select(write => write.ItemId));
        Assert.Equal(3, stateStore.SaveAttempts.Count);
        var checkpoint = stateStore.SaveAttempts[1];
        Assert.False(checkpoint.Token.CanBeCanceled);
        Assert.Contains(firstItemId, checkpoint.State.Items.Keys);
        Assert.DoesNotContain(secondItemId, checkpoint.State.Items.Keys);

        var reloaded = await stateStore.LoadAsync(CancellationToken.None);
        var ledgerEntry = Assert.Single(reloaded.Items);
        Assert.Equal(firstItemId, ledgerEntry.Key);
        Assert.Equal(["meta:genre:drama"], ledgerEntry.Value.LastAppliedTags);
        Assert.Empty(reloaded.RunCursors);
    }

    [Fact]
    public async Task RunAsync_LaterWriteIsCanceled_PreservesEarlierCheckpoint()
    {
        const string firstItemId = "77777777777777777777777777777777";
        const string secondItemId = "88888888888888888888888888888888";
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        using var cancellation = new CancellationTokenSource();
        var host = new InMemoryMetaTaggerHost(
            configuration,
            [
                WritableMovie("77777777-7777-7777-7777-777777777777", "Fantasy"),
                WritableMovie("88888888-8888-8888-8888-888888888888", "History")
            ],
            cancelRunOnWriteItemId: secondItemId,
            runCancellation: cancellation);
        var stateStore = new InMemoryMetaTaggerStateStore(new MetaTaggerState());
        var runner = CreateRunner(host, stateStore);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(
                new NoOpProgress(),
                cancellation.Token,
                new MetaTaggerRunOptions { PreviewOnly = false }));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal([firstItemId, secondItemId], host.UpdateAttempts.Select(write => write.ItemId));
        Assert.Equal(2, stateStore.SaveAttempts.Count);
        var checkpoint = stateStore.SaveAttempts[1];
        Assert.False(checkpoint.Token.CanBeCanceled);
        Assert.Contains(firstItemId, checkpoint.State.Items.Keys);
        Assert.DoesNotContain(secondItemId, checkpoint.State.Items.Keys);

        var reloaded = await stateStore.LoadAsync(CancellationToken.None);
        var ledgerEntry = Assert.Single(reloaded.Items);
        Assert.Equal(firstItemId, ledgerEntry.Key);
        Assert.Equal(["meta:genre:fantasy"], ledgerEntry.Value.LastAppliedTags);
        var cursor = Assert.Single(reloaded.RunCursors).Value;
        Assert.Equal([secondItemId], PendingIds(cursor));
    }

    [Fact]
    public async Task RunAsync_UnrelatedWriteCancellation_IsolatesFailureAndContinues()
    {
        const string canceledItemId = "99999999999999999999999999999999";
        const string writtenItemId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        using var runCancellation = new CancellationTokenSource();
        var host = new InMemoryMetaTaggerHost(
            configuration,
            [
                WritableMovie("99999999-9999-9999-9999-999999999999", "Crime"),
                WritableMovie("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Music")
            ],
            throwUnrelatedCancellationItemId: canceledItemId);
        var stateStore = new InMemoryMetaTaggerStateStore(new MetaTaggerState());
        var runner = CreateRunner(host, stateStore);

        var summary = await runner.RunAsync(
            new NoOpProgress(),
            runCancellation.Token,
            new MetaTaggerRunOptions { PreviewOnly = false });

        Assert.False(runCancellation.IsCancellationRequested);
        Assert.Equal(2, summary.ItemsScanned);
        Assert.Equal(1, summary.Failures);
        Assert.Equal(1, summary.WritesApplied);
        Assert.Equal([canceledItemId, writtenItemId], host.UpdateAttempts.Select(write => write.ItemId));

        var reloaded = await stateStore.LoadAsync(CancellationToken.None);
        var ledgerEntry = Assert.Single(reloaded.Items);
        Assert.Equal(writtenItemId, ledgerEntry.Key);
        Assert.Equal(["meta:genre:music"], ledgerEntry.Value.LastAppliedTags);
        Assert.Empty(reloaded.RunCursors);
    }

    [Fact]
    public async Task RunAsync_CheckpointFailure_AbortsBeforeLaterWrite()
    {
        const string firstItemId = "44444444444444444444444444444444";
        const string secondItemId = "55555555555555555555555555555555";
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        var host = new InMemoryMetaTaggerHost(
            configuration,
            [
                WritableMovie("44444444-4444-4444-4444-444444444444", "Mystery"),
                WritableMovie("55555555-5555-5555-5555-555555555555", "Thriller")
            ]);
        var stateStore = new FailingCheckpointMetaTaggerStateStore();
        var runner = CreateRunner(host, stateStore);

        var exception = await Assert.ThrowsAsync<IOException>(
            () => runner.RunAsync(
                new NoOpProgress(),
                CancellationToken.None,
                new MetaTaggerRunOptions { PreviewOnly = false }));

        Assert.Equal("Injected ownership checkpoint failure.", exception.Message);
        var write = Assert.Single(host.UpdateAttempts);
        Assert.Equal(firstItemId, write.ItemId);
        Assert.Equal(["meta:genre:mystery"], write.Tags);
        Assert.Equal(2, stateStore.SaveAttempts.Count);
        var attemptedCheckpoint = stateStore.SaveAttempts[1];
        Assert.False(attemptedCheckpoint.Token.CanBeCanceled);
        Assert.Contains(firstItemId, attemptedCheckpoint.State.Items.Keys);
        Assert.Equal([secondItemId], PendingIds(Assert.Single(attemptedCheckpoint.State.RunCursors).Value));
        var reloaded = await stateStore.LoadAsync(CancellationToken.None);
        Assert.Empty(reloaded.Items);
        Assert.Equal(
            [firstItemId, secondItemId],
            PendingIds(Assert.Single(reloaded.RunCursors).Value));
    }

    [Fact]
    public async Task RunAsync_CheckpointCancellation_AbortsBeforeLaterWrite()
    {
        const string firstItemId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string secondItemId = "cccccccccccccccccccccccccccccccc";
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        var host = new InMemoryMetaTaggerHost(
            configuration,
            [
                WritableMovie("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "Western"),
                WritableMovie("cccccccc-cccc-cccc-cccc-cccccccccccc", "War")
            ]);
        var stateStore = new FailingCheckpointMetaTaggerStateStore(
            new OperationCanceledException("Injected ownership checkpoint cancellation.", CancellationToken.None));
        var runner = CreateRunner(host, stateStore);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => runner.RunAsync(
                new NoOpProgress(),
                CancellationToken.None,
                new MetaTaggerRunOptions { PreviewOnly = false }));

        Assert.Equal("Injected ownership checkpoint cancellation.", exception.Message);
        Assert.Equal(CancellationToken.None, exception.CancellationToken);
        var write = Assert.Single(host.UpdateAttempts);
        Assert.Equal(firstItemId, write.ItemId);
        Assert.Equal(2, stateStore.SaveAttempts.Count);
        var attemptedCheckpoint = stateStore.SaveAttempts[1];
        Assert.False(attemptedCheckpoint.Token.CanBeCanceled);
        Assert.Contains(firstItemId, attemptedCheckpoint.State.Items.Keys);
        Assert.Equal([secondItemId], PendingIds(Assert.Single(attemptedCheckpoint.State.RunCursors).Value));
        var reloaded = await stateStore.LoadAsync(CancellationToken.None);
        Assert.Empty(reloaded.Items);
        Assert.Equal(
            [firstItemId, secondItemId],
            PendingIds(Assert.Single(reloaded.RunCursors).Value));
    }

    [Fact]
    public async Task RunAsync_FailedWrite_DoesNotCheckpointOwnership()
    {
        const string itemId = "66666666666666666666666666666666";
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        var host = new InMemoryMetaTaggerHost(
            configuration,
            [WritableMovie("66666666-6666-6666-6666-666666666666", "Adventure")],
            failWriteItemId: itemId);
        var stateStore = new InMemoryMetaTaggerStateStore(new MetaTaggerState());
        var runner = CreateRunner(host, stateStore);

        var summary = await runner.RunAsync(
            new NoOpProgress(),
            CancellationToken.None,
            new MetaTaggerRunOptions { PreviewOnly = false });

        Assert.Equal(1, summary.ItemsScanned);
        Assert.Equal(0, summary.WritesApplied);
        Assert.Equal(1, summary.Failures);
        Assert.Single(host.UpdateAttempts);
        Assert.Equal(2, stateStore.SaveAttempts.Count);
        var finalSave = stateStore.SaveAttempts[^1];
        Assert.DoesNotContain(itemId, finalSave.State.Items.Keys);
        var reloaded = await stateStore.LoadAsync(CancellationToken.None);
        Assert.DoesNotContain(itemId, reloaded.Items.Keys);
        Assert.Empty(reloaded.RunCursors);
    }

    [Fact]
    public async Task RunAsync_ConcurrentCalls_OnlyOneEntersCoordinatedLifecycle()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        var host = new InMemoryMetaTaggerHost(configuration);
        var stateStore = new BlockingLoadMetaTaggerStateStore();
        var runner = CreateRunner(host, stateStore);

        var firstRun = runner.RunAsync(new NoOpProgress(), CancellationToken.None);
        await stateStore.FirstLoadEntered.WaitAsync(AsyncTestTimeout);

        var secondRun = runner.RunAsync(new NoOpProgress(), CancellationToken.None);
        try
        {
            Assert.Empty(host.Queries);
            Assert.Equal(1, host.ConfigurationReadCount);
            Assert.Equal(1, stateStore.LoadCount);
        }
        finally
        {
            stateStore.ReleaseLoads();
            await Task.WhenAll(firstRun, secondRun).WaitAsync(AsyncTestTimeout);
        }
    }

    [Fact]
    public async Task RunAsync_ConcurrentCalls_SecondProceedsAfterFirstCompletes()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        var host = new InMemoryMetaTaggerHost(configuration);
        var stateStore = new BlockingLoadMetaTaggerStateStore();
        var runner = CreateRunner(host, stateStore);

        var firstRun = runner.RunAsync(new NoOpProgress(), CancellationToken.None);
        await stateStore.FirstLoadEntered.WaitAsync(AsyncTestTimeout);
        var secondRun = runner.RunAsync(new NoOpProgress(), CancellationToken.None);

        Assert.False(secondRun.IsCompleted);
        stateStore.ReleaseLoads();

        await firstRun.WaitAsync(AsyncTestTimeout);
        await secondRun.WaitAsync(AsyncTestTimeout);
        Assert.Equal(2, stateStore.LoadCount);
        Assert.Equal(2, host.Queries.Count);
    }

    [Fact]
    public async Task RunAsync_ConcurrentCalls_SecondSnapshotsAfterFirstPublishesLifecycle()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        var host = new InMemoryMetaTaggerHost(configuration);
        var stateStore = new BlockingSummaryMetaTaggerStateStore();
        var runner = CreateRunner(host, stateStore);

        var firstRun = runner.RunAsync(new NoOpProgress(), CancellationToken.None);
        await stateStore.FirstSummarySaveEntered.WaitAsync(AsyncTestTimeout);
        var secondRun = runner.RunAsync(new NoOpProgress(), CancellationToken.None);

        try
        {
            Assert.Equal(1, host.ConfigurationReadCount);
            Assert.Equal(0, host.ConfigurationSaveCount);
            Assert.False(secondRun.IsCompleted);
        }
        finally
        {
            stateStore.ReleaseSummarySave();
            await Task.WhenAll(firstRun, secondRun).WaitAsync(AsyncTestTimeout);
        }

        Assert.Equal(1, host.ConfigurationSaveCountAtSecondRead);
    }

    [Fact]
    public async Task RunAsync_CanceledWaiter_DoesNotSnapshotOrEnterBody()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        var host = new InMemoryMetaTaggerHost(configuration);
        var stateStore = new BlockingLoadMetaTaggerStateStore();
        var runner = CreateRunner(host, stateStore);
        using var cancellation = new CancellationTokenSource();

        var firstRun = runner.RunAsync(new NoOpProgress(), CancellationToken.None);
        await stateStore.FirstLoadEntered.WaitAsync(AsyncTestTimeout);
        var canceledRun = runner.RunAsync(new NoOpProgress(), cancellation.Token);

        try
        {
            Assert.False(canceledRun.IsCompleted);
            Assert.Equal(1, host.ConfigurationReadCount);
            Assert.Equal(1, stateStore.LoadCount);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await canceledRun.WaitAsync(AsyncTestTimeout));

            Assert.Equal(1, host.ConfigurationReadCount);
            Assert.Equal(1, stateStore.LoadCount);
        }
        finally
        {
            stateStore.ReleaseLoads();
            await firstRun.WaitAsync(AsyncTestTimeout);
        }

        await runner.RunAsync(new NoOpProgress(), CancellationToken.None).WaitAsync(AsyncTestTimeout);
        Assert.Equal(2, host.ConfigurationReadCount);
        Assert.Equal(2, stateStore.LoadCount);
    }

    [Fact]
    public async Task RunAsync_FirstRunFails_ReleasesGateForSecondRun()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        var host = new InMemoryMetaTaggerHost(configuration, failFirstQuery: true);
        var stateStore = new BlockingLoadMetaTaggerStateStore();
        var runner = CreateRunner(host, stateStore);

        var firstRun = runner.RunAsync(new NoOpProgress(), CancellationToken.None);
        await stateStore.FirstLoadEntered.WaitAsync(AsyncTestTimeout);
        var secondRun = runner.RunAsync(new NoOpProgress(), CancellationToken.None);

        stateStore.ReleaseLoads();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await firstRun.WaitAsync(AsyncTestTimeout));
        await secondRun.WaitAsync(AsyncTestTimeout);
        Assert.Equal(2, host.ConfigurationReadCount);
        Assert.Equal(2, stateStore.LoadCount);
        Assert.Equal(2, host.Queries.Count);
    }

    [Fact]
    public async Task RunAsync_FirstRunCanceled_ReleasesGateForSecondRun()
    {
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        var host = new InMemoryMetaTaggerHost(configuration);
        var stateStore = new BlockingLoadMetaTaggerStateStore();
        var runner = CreateRunner(host, stateStore);
        using var cancellation = new CancellationTokenSource();

        var firstRun = runner.RunAsync(new NoOpProgress(), cancellation.Token);
        await stateStore.FirstLoadEntered.WaitAsync(AsyncTestTimeout);
        var secondRun = runner.RunAsync(new NoOpProgress(), CancellationToken.None);

        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await firstRun.WaitAsync(AsyncTestTimeout));
            await secondRun.WaitAsync(AsyncTestTimeout);
        }
        finally
        {
            stateStore.ReleaseLoads();
        }

        Assert.Equal(2, host.ConfigurationReadCount);
        Assert.Equal(2, stateStore.LoadCount);
        Assert.Single(host.Queries);
    }

    [Theory]
    [InlineData(MetadataTagRunMode.FullScan)]
    [InlineData(MetadataTagRunMode.RebuildTrackingLedger)]
    public async Task RunAsync_CompletedMovieScope_PrunesOnlyMissingMovies(MetadataTagRunMode runMode)
    {
        const string returnedMovieId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string missingMovieId = "missing-movie";
        const string excludedEpisodeId = "excluded-episode";
        const string legacyNullTypeId = "legacy-null-type";
        const string unknownTypeId = "unknown-type";
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        var state = new MetaTaggerState
        {
            Items =
            {
                [returnedMovieId] = StateItem(returnedMovieId, "Movie"),
                [missingMovieId] = StateItem(missingMovieId, "mOvIe"),
                [excludedEpisodeId] = StateItem(excludedEpisodeId, "Episode"),
                [legacyNullTypeId] = StateItem(legacyNullTypeId, null),
                [unknownTypeId] = StateItem(unknownTypeId, "LegacyMedia")
            }
        };
        var stateStore = new InMemoryMetaTaggerStateStore(state);
        var host = new InMemoryMetaTaggerHost(
            configuration,
            [new Movie { Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa") }]);
        var runner = CreateRunner(host, stateStore);

        var summary = await runner.RunAsync(
            new NoOpProgress(),
            CancellationToken.None,
            new MetaTaggerRunOptions { RunMode = runMode, PreviewOnly = true });

        Assert.Equal(1, summary.LedgerEntriesPruned);
        var savedState = Assert.IsType<MetaTaggerState>(stateStore.SavedState);
        Assert.DoesNotContain(missingMovieId, savedState.Items.Keys);
        Assert.Contains(returnedMovieId, savedState.Items.Keys);
        Assert.Contains(excludedEpisodeId, savedState.Items.Keys);
        Assert.Contains(legacyNullTypeId, savedState.Items.Keys);
        Assert.Contains(unknownTypeId, savedState.Items.Keys);
    }

    [Fact]
    public async Task RunAsync_IncrementalMovieScope_DoesNotPruneMissingMovie()
    {
        const string missingMovieId = "missing-movie";
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        var stateStore = new InMemoryMetaTaggerStateStore(new MetaTaggerState
        {
            Items =
            {
                [missingMovieId] = StateItem(missingMovieId, "Movie")
            }
        });
        var runner = CreateRunner(new InMemoryMetaTaggerHost(configuration), stateStore);

        var summary = await runner.RunAsync(
            new NoOpProgress(),
            CancellationToken.None,
            new MetaTaggerRunOptions
            {
                RunMode = MetadataTagRunMode.Incremental,
                PreviewOnly = true
            });

        Assert.Equal(0, summary.LedgerEntriesPruned);
        var savedState = Assert.IsType<MetaTaggerState>(stateStore.SavedState);
        Assert.Contains(missingMovieId, savedState.Items.Keys);
    }

    [Fact]
    public async Task RunAsync_BudgetIncompleteFullMovieScope_DoesNotPrune()
    {
        const string unprocessedMovieId = "cccccccccccccccccccccccccccccccc";
        const string missingMovieId = "missing-movie";
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        configuration.MaxItemsPerRun = 1;
        var stateStore = new InMemoryMetaTaggerStateStore(new MetaTaggerState
        {
            Items =
            {
                [unprocessedMovieId] = StateItem(unprocessedMovieId, "Movie"),
                [missingMovieId] = StateItem(missingMovieId, "Movie")
            }
        });
        var host = new InMemoryMetaTaggerHost(
            configuration,
            [
                new Movie { Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb") },
                new Movie { Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc") }
            ]);
        var runner = CreateRunner(host, stateStore);

        var summary = await runner.RunAsync(
            new NoOpProgress(),
            CancellationToken.None,
            new MetaTaggerRunOptions
            {
                RunMode = MetadataTagRunMode.FullScan,
                PreviewOnly = true
            });

        Assert.True(summary.BudgetLimitReached);
        Assert.Equal(1, summary.ItemsScanned);
        Assert.Equal(1, summary.ItemsRemaining);
        Assert.Equal(0, summary.LedgerEntriesPruned);
        var savedState = Assert.IsType<MetaTaggerState>(stateStore.SavedState);
        Assert.Contains(unprocessedMovieId, savedState.Items.Keys);
        Assert.Contains(missingMovieId, savedState.Items.Keys);
    }

    [Fact]
    public async Task RunAsync_CanceledAfterFinalItem_DoesNotPruneLedger()
    {
        const string returnedMovieId = "dddddddddddddddddddddddddddddddd";
        const string missingMovieId = "missing-movie";
        var configuration = ConfigurationWithNoItemTypes();
        configuration.IncludeMovies = true;
        var state = new MetaTaggerState
        {
            Items =
            {
                [missingMovieId] = StateItem(missingMovieId, "Movie")
            }
        };
        var stateStore = new InMemoryMetaTaggerStateStore(state);
        var host = new InMemoryMetaTaggerHost(
            configuration,
            [new Movie { Id = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd") }]);
        var runner = CreateRunner(host, stateStore);
        using var cancellation = new CancellationTokenSource();

        var exception = await Record.ExceptionAsync(() => runner.RunAsync(
            new CancelOnReportProgress(cancellation),
            cancellation.Token,
            new MetaTaggerRunOptions
            {
                RunMode = MetadataTagRunMode.FullScan,
                PreviewOnly = true
            }));

        Assert.Contains(missingMovieId, state.Items.Keys);
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        var reloaded = await stateStore.LoadAsync(CancellationToken.None);
        Assert.Contains(missingMovieId, reloaded.Items.Keys);
        Assert.Equal(
            [returnedMovieId],
            PendingIds(Assert.Single(reloaded.RunCursors).Value));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private MetaTaggerRunner CreateRunner(
        IMetaTaggerHost host,
        IMetaTaggerStateStore? stateStore = null,
        IMetaTaggerClock? clock = null,
        MetadataProjectionService? projection = null)
    {
        return new MetaTaggerRunner(
            host,
            projection ?? new MetadataProjectionService(),
            new MetadataTagProcessor(
                new MetadataTagService(),
                new MetadataFingerprintService(),
                new TagMergeService()),
            stateStore ?? new MetaTaggerStateStore(_directory),
            NullLogger<MetaTaggerRunner>.Instance,
            clock ?? new MetaTaggerClock());
    }

    private static PluginConfiguration ConfigurationWithNoItemTypes()
    {
        return new PluginConfiguration
        {
            IncludeMovies = false,
            IncludeSeries = false,
            IncludeEpisodes = false,
            IncludeVideos = false
        };
    }

    private static MetaTaggerStateItem StateItem(string itemId, string? itemType)
    {
        return new MetaTaggerStateItem
        {
            ItemId = itemId,
            ItemType = itemType
        };
    }

    private static Movie WritableMovie(string id, string genre)
    {
        return new Movie
        {
            Id = Guid.Parse(id),
            Genres = [genre]
        };
    }

    private static string[] PendingIds(MetaTaggerRunCursor cursor)
    {
        return cursor.PendingItemIds.Skip(cursor.NextIndex).ToArray();
    }

    private static MetaTaggerState SnapshotState(MetaTaggerState state)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(state, StateSnapshotJsonOptions);
        var snapshot = JsonSerializer.Deserialize<MetaTaggerState>(json, StateSnapshotJsonOptions)
            ?? throw new InvalidOperationException("The in-memory state snapshot could not be deserialized.");
        snapshot.Items = new Dictionary<string, MetaTaggerStateItem>(
            snapshot.Items,
            StringComparer.OrdinalIgnoreCase);
        snapshot.RunCursors = new Dictionary<string, MetaTaggerRunCursor>(
            snapshot.RunCursors,
            StringComparer.Ordinal);
        return snapshot;
    }

    private sealed class InMemoryMetaTaggerHost(
        PluginConfiguration configuration,
        IReadOnlyList<BaseItem>? items = null,
        bool failOnQuery = false,
        bool failFirstQuery = false,
        string? failWriteItemId = null,
        string? throwUnrelatedCancellationItemId = null,
        string? cancelRunOnWriteItemId = null,
        CancellationTokenSource? runCancellation = null,
        bool failOnConfigurationSave = false) : IMetaTaggerHost
    {
        private PluginConfiguration _configuration = configuration;
        private int _configurationReadCount;
        private int _configurationSaveCount;
        private int _configurationSaveCountAtSecondRead = -1;
        private int _queryCount;

        public List<BaseItemKind[]> Queries { get; } = [];

        public IReadOnlyList<BaseItem> CurrentItems { get; set; } = items ?? [];

        public Func<Guid, IReadOnlyList<MediaStream>> StreamLookup { get; set; } = _ => [];

        public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId) => StreamLookup(itemId);

        public List<(string ItemId, string[] Tags)> UpdateAttempts { get; } = [];

        public List<(bool Force, bool Rebuild, bool Claim)> ConfigurationSaveAttempts { get; } = [];

        public List<PluginConfiguration> SavedConfigurations { get; } = [];

        public PluginConfiguration CurrentConfiguration => Volatile.Read(ref _configuration);

        public int ConfigurationReadCount => Volatile.Read(ref _configurationReadCount);

        public int ConfigurationSaveCount => Volatile.Read(ref _configurationSaveCount);

        public int ConfigurationSaveCountAtSecondRead => Volatile.Read(ref _configurationSaveCountAtSecondRead);

        public PluginConfiguration GetConfiguration()
        {
            var readNumber = Interlocked.Increment(ref _configurationReadCount);
            if (readNumber == 2)
            {
                Volatile.Write(ref _configurationSaveCountAtSecondRead, ConfigurationSaveCount);
            }

            return CurrentConfiguration;
        }

        public IReadOnlyList<BaseItem> GetItems(BaseItemKind[] includedItemTypes)
        {
            Queries.Add([.. includedItemTypes]);
            var queryNumber = Interlocked.Increment(ref _queryCount);
            if (failOnQuery || (failFirstQuery && queryNumber == 1))
            {
                throw new InvalidOperationException("The Jellyfin host must not be queried for an empty item-type scope.");
            }

            return items ?? [];
        }

        public BaseItem? GetItem(Guid itemId)
        {
            return CurrentItems.FirstOrDefault(item => item.Id == itemId);
        }

        public Task UpdateItemTagsAsync(
            BaseItem item,
            IReadOnlyCollection<string> tags,
            CancellationToken cancellationToken)
        {
            var itemId = item.Id.ToString("N");
            UpdateAttempts.Add((itemId, tags.ToArray()));
            if (string.Equals(itemId, failWriteItemId, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromException(new IOException("Injected Jellyfin update failure."));
            }

            if (string.Equals(itemId, throwUnrelatedCancellationItemId, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromCanceled(new CancellationToken(canceled: true));
            }

            if (string.Equals(itemId, cancelRunOnWriteItemId, StringComparison.OrdinalIgnoreCase))
            {
                (runCancellation ?? throw new InvalidOperationException("Run cancellation source is required."))
                    .Cancel();
                return Task.FromCanceled(cancellationToken);
            }

            item.Tags = tags.ToArray();
            return Task.CompletedTask;
        }

        public void PublishRunConfiguration(MetaTaggerRunConfigurationPublication publication)
        {
            var currentConfiguration = CurrentConfiguration;
            var mutation = publication.ApplyTo(currentConfiguration);
            Interlocked.Increment(ref _configurationSaveCount);
            ConfigurationSaveAttempts.Add((
                currentConfiguration.ForceFullScanOnNextRun,
                currentConfiguration.RebuildTrackingLedgerOnNextRun,
                currentConfiguration.ClaimExistingGeneratedTagsOnNextRun));
            SavedConfigurations.Add(currentConfiguration);
            if (failOnConfigurationSave)
            {
                mutation.RestoreAcknowledgedActions();
                throw new InvalidOperationException("Injected configuration save failure.");
            }
        }

        public void ReplaceConfiguration(PluginConfiguration replacement)
        {
            Volatile.Write(ref _configuration, replacement);
        }
    }

    private sealed class BlockingLoadMetaTaggerStateStore : IMetaTaggerStateStore
    {
        private readonly TaskCompletionSource<bool> _firstLoadEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseLoads =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _loadCount;

        public Task FirstLoadEntered => _firstLoadEntered.Task;

        public int LoadCount => Volatile.Read(ref _loadCount);

        public async Task<MetaTaggerState> LoadAsync(CancellationToken cancellationToken)
        {
            var loadNumber = Interlocked.Increment(ref _loadCount);
            if (loadNumber == 1)
            {
                _firstLoadEntered.TrySetResult(true);
                await _releaseLoads.Task.WaitAsync(cancellationToken);
            }

            return new MetaTaggerState();
        }

        public Task SaveAsync(MetaTaggerState state, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task SaveSummaryAsync(MetaTaggerRunSummary summary, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<string> SavePreviewChangesAsync(
            IReadOnlyCollection<MetaTaggerPreviewChange> changes,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(string.Empty);
        }

        public void ReleaseLoads()
        {
            _releaseLoads.TrySetResult(true);
        }
    }

    private sealed class BlockingSummaryMetaTaggerStateStore : IMetaTaggerStateStore
    {
        private readonly TaskCompletionSource<bool> _firstSummarySaveEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseSummarySave =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _summarySaveCount;

        public Task FirstSummarySaveEntered => _firstSummarySaveEntered.Task;

        public Task<MetaTaggerState> LoadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new MetaTaggerState());
        }

        public Task SaveAsync(MetaTaggerState state, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public async Task SaveSummaryAsync(MetaTaggerRunSummary summary, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _summarySaveCount) == 1)
            {
                _firstSummarySaveEntered.TrySetResult(true);
                await _releaseSummarySave.Task.WaitAsync(cancellationToken);
            }
        }

        public Task<string> SavePreviewChangesAsync(
            IReadOnlyCollection<MetaTaggerPreviewChange> changes,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(string.Empty);
        }

        public void ReleaseSummarySave()
        {
            _releaseSummarySave.TrySetResult(true);
        }
    }

    private sealed class InMemoryMetaTaggerStateStore : IMetaTaggerStateStore
    {
        private MetaTaggerState _persistedState;

        public InMemoryMetaTaggerStateStore(MetaTaggerState state)
        {
            _persistedState = SnapshotState(state);
        }

        public MetaTaggerState? SavedState { get; private set; }

        public List<(MetaTaggerState State, CancellationToken Token)> SaveAttempts { get; } = [];

        public Task<MetaTaggerState> LoadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(SnapshotState(_persistedState));
        }

        public Task SaveAsync(MetaTaggerState savedState, CancellationToken cancellationToken)
        {
            SaveAttempts.Add((SnapshotState(savedState), cancellationToken));
            _persistedState = SnapshotState(savedState);
            SavedState = SnapshotState(_persistedState);
            return Task.CompletedTask;
        }

        public Task SaveSummaryAsync(MetaTaggerRunSummary summary, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<string> SavePreviewChangesAsync(
            IReadOnlyCollection<MetaTaggerPreviewChange> changes,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(string.Empty);
        }
    }

    private sealed class FailingCheckpointMetaTaggerStateStore : IMetaTaggerStateStore
    {
        private readonly Exception _failure;
        private MetaTaggerState _persistedState = new();
        private int _saveCount;

        public FailingCheckpointMetaTaggerStateStore(Exception? failure = null)
        {
            _failure = failure ?? new IOException("Injected ownership checkpoint failure.");
        }

        public List<(MetaTaggerState State, CancellationToken Token)> SaveAttempts { get; } = [];

        public Task<MetaTaggerState> LoadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(SnapshotState(_persistedState));
        }

        public Task SaveAsync(MetaTaggerState state, CancellationToken cancellationToken)
        {
            SaveAttempts.Add((SnapshotState(state), cancellationToken));
            if (Interlocked.Increment(ref _saveCount) == 1)
            {
                _persistedState = SnapshotState(state);
                return Task.CompletedTask;
            }

            return Task.FromException(_failure);
        }

        public Task SaveSummaryAsync(MetaTaggerRunSummary summary, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<string> SavePreviewChangesAsync(
            IReadOnlyCollection<MetaTaggerPreviewChange> changes,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(string.Empty);
        }
    }

    private sealed class LoadFailingMetaTaggerStateStore : IMetaTaggerStateStore
    {
        public Task<MetaTaggerState> LoadAsync(CancellationToken cancellationToken)
        {
            return Task.FromException<MetaTaggerState>(new IOException("Injected state load failure."));
        }

        public Task SaveAsync(MetaTaggerState state, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task SaveSummaryAsync(MetaTaggerRunSummary summary, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<string> SavePreviewChangesAsync(
            IReadOnlyCollection<MetaTaggerPreviewChange> changes,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(string.Empty);
        }
    }

    private sealed class PreviewFailingMetaTaggerStateStore : IMetaTaggerStateStore
    {
        public Task<MetaTaggerState> LoadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new MetaTaggerState());
        }

        public Task SaveAsync(MetaTaggerState state, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task SaveSummaryAsync(MetaTaggerRunSummary summary, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<string> SavePreviewChangesAsync(
            IReadOnlyCollection<MetaTaggerPreviewChange> changes,
            CancellationToken cancellationToken)
        {
            return Task.FromException<string>(new IOException("Injected preview publication failure."));
        }
    }

    private sealed class SummaryFailingMetaTaggerStateStore : IMetaTaggerStateStore
    {
        public Task<MetaTaggerState> LoadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new MetaTaggerState());
        }

        public Task SaveAsync(MetaTaggerState state, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task SaveSummaryAsync(MetaTaggerRunSummary summary, CancellationToken cancellationToken)
        {
            return Task.FromException(new IOException("Injected summary publication failure."));
        }

        public Task<string> SavePreviewChangesAsync(
            IReadOnlyCollection<MetaTaggerPreviewChange> changes,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(string.Empty);
        }
    }

    private sealed class FirstSaveFailingMetaTaggerStateStore : IMetaTaggerStateStore
    {
        private int _saveCount;

        public Task<MetaTaggerState> LoadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new MetaTaggerState());
        }

        public Task SaveAsync(MetaTaggerState state, CancellationToken cancellationToken)
        {
            return Interlocked.Increment(ref _saveCount) == 1
                ? Task.FromException(new IOException("Injected ledger write failure."))
                : Task.CompletedTask;
        }

        public Task SaveSummaryAsync(MetaTaggerRunSummary summary, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<string> SavePreviewChangesAsync(
            IReadOnlyCollection<MetaTaggerPreviewChange> changes,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(string.Empty);
        }
    }

    private sealed class NoOpProgress : IProgress<double>
    {
        public void Report(double value)
        {
        }
    }

    private sealed class CancelDuringDelayMetaTaggerClock(CancellationTokenSource cancellation)
        : IMetaTaggerClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            cancellation.Cancel();
            return Task.FromCanceled(cancellationToken);
        }
    }

    private sealed class CancelOnReportProgress(CancellationTokenSource cancellation) : IProgress<double>
    {
        public void Report(double value)
        {
            cancellation.Cancel();
        }
    }

    private sealed class FailOnAccessMetaTaggerStateStore : IMetaTaggerStateStore
    {
        public List<string> Operations { get; } = [];

        public Task<MetaTaggerState> LoadAsync(CancellationToken cancellationToken)
        {
            return Unexpected<MetaTaggerState>(nameof(LoadAsync));
        }

        public Task SaveAsync(MetaTaggerState state, CancellationToken cancellationToken)
        {
            return Unexpected(nameof(SaveAsync));
        }

        public Task SaveSummaryAsync(MetaTaggerRunSummary summary, CancellationToken cancellationToken)
        {
            return Unexpected(nameof(SaveSummaryAsync));
        }

        public Task<string> SavePreviewChangesAsync(
            IReadOnlyCollection<MetaTaggerPreviewChange> changes,
            CancellationToken cancellationToken)
        {
            return Unexpected<string>(nameof(SavePreviewChangesAsync));
        }

        private Task Unexpected(string operation)
        {
            Operations.Add(operation);
            return Task.FromException(new InvalidOperationException($"State persistence must not perform {operation}."));
        }

        private Task<T> Unexpected<T>(string operation)
        {
            Operations.Add(operation);
            return Task.FromException<T>(new InvalidOperationException($"State persistence must not perform {operation}."));
        }
    }
}
