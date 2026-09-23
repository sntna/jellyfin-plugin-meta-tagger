using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaTagger.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed class MetaTaggerResumableCycleTests
{
    private static readonly JsonSerializerOptions StateJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RunAsync_ItemBudget_VisitsEntireStableCycleAcrossInvocations()
    {
        var configuration = MovieConfiguration();
        configuration.MaxItemsPerRun = 2;
        var host = new InMemoryHost(
            configuration,
            [
                Movie("11111111-1111-1111-1111-111111111111", "Drama"),
                Movie("22222222-2222-2222-2222-222222222222", "Comedy"),
                Movie("33333333-3333-3333-3333-333333333333", "Mystery"),
                Movie("44444444-4444-4444-4444-444444444444", "Thriller")
            ]);
        var stateStore = new InMemoryStateStore();
        var runner = CreateRunner(host, stateStore);
        var options = new MetaTaggerRunOptions { PreviewOnly = false };

        var first = await runner.RunAsync(new NoOpProgress(), CancellationToken.None, options);
        configuration.MaxItemsPerRun = 3;
        var second = await runner.RunAsync(new NoOpProgress(), CancellationToken.None, options);

        Assert.True(first.BudgetLimitReached);
        Assert.Equal(2, first.ItemsRemaining);
        Assert.False(second.BudgetLimitReached);
        Assert.Equal(0, second.ItemsRemaining);
        Assert.Equal(
            [
                "11111111111111111111111111111111",
                "22222222222222222222222222222222",
                "33333333333333333333333333333333",
                "44444444444444444444444444444444"
            ],
            host.UpdateAttempts);
        Assert.Empty(stateStore.State.RunCursors);
    }

    [Fact]
    public async Task RunAsync_MaxRunMinutes_ResumesAfterTimeBudgetChanges()
    {
        var configuration = MovieConfiguration();
        configuration.MaxRunMinutes = 1;
        var host = new InMemoryHost(
            configuration,
            [
                Movie("11111111-1111-1111-1111-111111111111", "Drama"),
                Movie("22222222-2222-2222-2222-222222222222", "Comedy"),
                Movie("33333333-3333-3333-3333-333333333333", "Mystery")
            ]);
        var stateStore = new InMemoryStateStore();
        var clock = new ManualClock();
        var runner = CreateRunner(host, stateStore, clock);
        var options = new MetaTaggerRunOptions { PreviewOnly = false };

        var first = await runner.RunAsync(
            new AdvanceClockOnFirstReport(clock, TimeSpan.FromMinutes(1)),
            CancellationToken.None,
            options);

        Assert.True(first.BudgetLimitReached);
        Assert.Equal("max-run-minutes", first.BudgetLimitReason);
        Assert.Equal(1, first.ItemsScanned);
        Assert.Equal(2, first.ItemsRemaining);
        Assert.Equal(
            ["22222222222222222222222222222222", "33333333333333333333333333333333"],
            PendingIds(Assert.Single(stateStore.State.RunCursors).Value));

        configuration.MaxRunMinutes = 0;
        var second = await runner.RunAsync(new NoOpProgress(), CancellationToken.None, options);

        Assert.Equal(2, second.ItemsScanned);
        Assert.False(second.BudgetLimitReached);
        Assert.Equal(0, second.ItemsRemaining);
        Assert.Equal(
            [
                "11111111111111111111111111111111",
                "22222222222222222222222222222222",
                "33333333333333333333333333333333"
            ],
            host.UpdateAttempts);
        Assert.Empty(stateStore.State.RunCursors);
    }

    [Fact]
    public async Task RunAsync_WriteBudget_DefersRequiredWriteAtFrontOfPendingCursor()
    {
        var configuration = MovieConfiguration();
        configuration.MaxWritesPerRun = 1;
        var host = new InMemoryHost(
            configuration,
            [
                Movie("11111111-1111-1111-1111-111111111111", "Drama"),
                Movie("22222222-2222-2222-2222-222222222222", "Comedy"),
                Movie("33333333-3333-3333-3333-333333333333", "Mystery")
            ]);
        var stateStore = new InMemoryStateStore();
        var runner = CreateRunner(host, stateStore);
        var options = new MetaTaggerRunOptions { PreviewOnly = false };

        var first = await runner.RunAsync(new NoOpProgress(), CancellationToken.None, options);

        Assert.True(first.BudgetLimitReached);
        Assert.Equal("max-writes", first.BudgetLimitReason);
        Assert.Equal(2, first.ItemsScanned);
        Assert.Equal(2, first.ItemsRemaining);
        Assert.Equal(["11111111111111111111111111111111"], host.UpdateAttempts);
        Assert.Equal(
            ["22222222222222222222222222222222", "33333333333333333333333333333333"],
            PendingIds(Assert.Single(stateStore.State.RunCursors).Value));

        configuration.MaxWritesPerRun = 2;
        var second = await runner.RunAsync(new NoOpProgress(), CancellationToken.None, options);

        Assert.False(second.BudgetLimitReached);
        Assert.Equal(0, second.ItemsRemaining);
        Assert.Equal(
            [
                "11111111111111111111111111111111",
                "22222222222222222222222222222222",
                "33333333333333333333333333333333"
            ],
            host.UpdateAttempts);
        Assert.Empty(stateStore.State.RunCursors);
    }

    [Fact]
    public async Task RunAsync_UnchangedAndManualPrefix_AdvancesToLaterItems()
    {
        var configuration = MovieConfiguration();
        var firstMovie = Movie("11111111-1111-1111-1111-111111111111", "Drama");
        var host = new InMemoryHost(configuration, [firstMovie]);
        var stateStore = new InMemoryStateStore();
        var runner = CreateRunner(host, stateStore);
        var options = new MetaTaggerRunOptions { PreviewOnly = false };

        await runner.RunAsync(new NoOpProgress(), CancellationToken.None, options);
        host.UpdateAttempts.Clear();
        configuration.MaxItemsPerRun = 2;
        host.Items =
        [
            firstMovie,
            new Movie
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Tags = ["manual:tagger:skip"]
            },
            Movie("33333333-3333-3333-3333-333333333333", "Mystery"),
            Movie("44444444-4444-4444-4444-444444444444", "Thriller")
        ];

        var first = await runner.RunAsync(new NoOpProgress(), CancellationToken.None, options);
        var second = await runner.RunAsync(new NoOpProgress(), CancellationToken.None, options);

        Assert.Equal(1, first.ItemsSkippedUnchanged);
        Assert.Equal(1, first.ItemsSkippedManual);
        Assert.True(first.BudgetLimitReached);
        Assert.False(second.BudgetLimitReached);
        Assert.Equal(
            [
                "33333333333333333333333333333333",
                "44444444444444444444444444444444"
            ],
            host.UpdateAttempts);
        Assert.Empty(stateStore.State.RunCursors);
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("preview")]
    [InlineData("force")]
    [InlineData("types")]
    [InlineData("behavior")]
    [InlineData("audio-languages")]
    [InlineData("subtitle-languages")]
    public async Task RunAsync_DistinctRunProfile_DoesNotConsumeExistingCursor(string difference)
    {
        var configuration = MovieConfiguration();
        configuration.MaxItemsPerRun = 1;
        configuration.EnableAudioLanguages = false;
        var host = new InMemoryHost(
            configuration,
            [
                Movie("11111111-1111-1111-1111-111111111111", "Drama"),
                Movie("22222222-2222-2222-2222-222222222222", "Comedy")
            ]);
        var stateStore = new InMemoryStateStore();
        var runner = CreateRunner(host, stateStore);
        var baseline = new MetaTaggerRunOptions { PreviewOnly = false };

        await runner.RunAsync(new NoOpProgress(), CancellationToken.None, baseline);

        var distinct = difference switch
        {
            "mode" => new MetaTaggerRunOptions
            {
                RunMode = MetadataTagRunMode.FullScan,
                PreviewOnly = false
            },
            "preview" => new MetaTaggerRunOptions { PreviewOnly = true },
            "force" => new MetaTaggerRunOptions { PreviewOnly = false, Force = true },
            "types" => IncludeVideos(configuration, baseline),
            "behavior" => DisableGenres(configuration, baseline),
            "audio-languages" => EnableLanguages(configuration, baseline, audio: true),
            "subtitle-languages" => EnableLanguages(configuration, baseline, audio: false),
            _ => throw new ArgumentOutOfRangeException(nameof(difference))
        };

        await runner.RunAsync(new NoOpProgress(), CancellationToken.None, distinct);

        Assert.Equal(2, stateStore.State.RunCursors.Count);
        Assert.All(
            stateStore.State.RunCursors.Values,
            cursor => Assert.Equal(["22222222222222222222222222222222"], PendingIds(cursor)));

        configuration.IncludeVideos = false;
        configuration.EnableGenres = true;
        configuration.EnableAudioLanguages = false;
        configuration.EnableSubtitleLanguages = false;
        await runner.RunAsync(new NoOpProgress(), CancellationToken.None, baseline);

        var remainingCursor = Assert.Single(stateStore.State.RunCursors).Value;
        Assert.Equal(["22222222222222222222222222222222"], PendingIds(remainingCursor));
        Assert.Equal(
            ["11111111111111111111111111111111", "22222222222222222222222222222222"],
            host.UpdateAttempts);
    }

    [Theory]
    [InlineData(MetadataTagRunMode.FullScan)]
    [InlineData(MetadataTagRunMode.RebuildTrackingLedger)]
    public async Task RunAsync_MultiBatchDestructiveCycle_PrunesOnlyWhenCursorCompletes(
        MetadataTagRunMode runMode)
    {
        const string missingMovieId = "missing-movie";
        const string newlyScopedMovieId = "33333333333333333333333333333333";
        const string newlyScopedFingerprint = "sha256:not-yet-processed";
        var configuration = MovieConfiguration();
        configuration.MaxItemsPerRun = 1;
        var firstMovie = Movie("11111111-1111-1111-1111-111111111111", "Drama");
        var secondMovie = Movie("22222222-2222-2222-2222-222222222222", "Comedy");
        var host = new InMemoryHost(
            configuration,
            [firstMovie, secondMovie]);
        var stateStore = new InMemoryStateStore(new MetaTaggerState
        {
            Items =
            {
                [missingMovieId] = new MetaTaggerStateItem
                {
                    ItemId = missingMovieId,
                    ItemType = "Movie"
                },
                [newlyScopedMovieId] = new MetaTaggerStateItem
                {
                    ItemId = newlyScopedMovieId,
                    ItemType = "Movie",
                    LastMetadataFingerprint = newlyScopedFingerprint
                }
            }
        });
        var runner = CreateRunner(host, stateStore);
        var options = new MetaTaggerRunOptions
        {
            RunMode = runMode,
            PreviewOnly = true
        };

        var first = await runner.RunAsync(new NoOpProgress(), CancellationToken.None, options);

        Assert.True(first.BudgetLimitReached);
        Assert.Equal(0, first.LedgerEntriesPruned);
        Assert.Contains(missingMovieId, stateStore.State.Items.Keys);
        Assert.Single(stateStore.State.RunCursors);

        host.Items =
        [
            firstMovie,
            secondMovie,
            Movie("33333333-3333-3333-3333-333333333333", "Mystery")
        ];
        var second = await runner.RunAsync(new NoOpProgress(), CancellationToken.None, options);

        Assert.False(second.BudgetLimitReached);
        Assert.Equal(1, second.ItemsScanned);
        Assert.Equal(1, second.LedgerEntriesPruned);
        Assert.DoesNotContain(missingMovieId, stateStore.State.Items.Keys);
        Assert.Equal(
            newlyScopedFingerprint,
            stateStore.State.Items[newlyScopedMovieId].LastMetadataFingerprint);
        Assert.Empty(stateStore.State.RunCursors);
    }

    [Fact]
    public async Task RunAsync_CycleSnapshot_IsDeterministicUniqueAndDropsRemovedPendingIds()
    {
        var configuration = MovieConfiguration();
        configuration.MaxItemsPerRun = 1;
        var duplicateId = "22222222-2222-2222-2222-222222222222";
        var host = new InMemoryHost(
            configuration,
            [
                Movie("33333333-3333-3333-3333-333333333333", "Mystery"),
                Movie(duplicateId, "Comedy"),
                Movie("44444444-4444-4444-4444-444444444444", "Thriller"),
                Movie("11111111-1111-1111-1111-111111111111", "Drama"),
                Movie(duplicateId, "Comedy")
            ]);
        var stateStore = new InMemoryStateStore();
        var runner = CreateRunner(host, stateStore);
        var options = new MetaTaggerRunOptions { PreviewOnly = false };

        var first = await runner.RunAsync(new NoOpProgress(), CancellationToken.None, options);

        Assert.Equal(["11111111111111111111111111111111"], host.UpdateAttempts);
        Assert.Equal(3, first.ItemsRemaining);

        configuration.MaxItemsPerRun = 0;
        configuration.MaxRunMinutes = 5;
        host.Items =
        [
            Movie("00000000-0000-0000-0000-000000000001", "New"),
            Movie("33333333-3333-3333-3333-333333333333", "Mystery"),
            Movie(duplicateId, "Comedy")
        ];

        var second = await runner.RunAsync(new NoOpProgress(), CancellationToken.None, options);

        Assert.False(second.BudgetLimitReached);
        Assert.Equal(2, second.ItemsScanned);
        Assert.Equal(0, second.ItemsRemaining);
        Assert.Equal(
            [
                "11111111111111111111111111111111",
                "22222222222222222222222222222222",
                "33333333333333333333333333333333"
            ],
            host.UpdateAttempts);
        Assert.Empty(stateStore.State.RunCursors);

        await runner.RunAsync(new NoOpProgress(), CancellationToken.None, options);

        Assert.Equal("00000000000000000000000000000001", host.UpdateAttempts[^1]);
    }

    [Fact]
    public async Task RunAsync_IsolatedItemFailure_AdvancesCursor()
    {
        var configuration = MovieConfiguration();
        configuration.MaxItemsPerRun = 1;
        var firstId = "11111111111111111111111111111111";
        var host = new InMemoryHost(
            configuration,
            [
                Movie("11111111-1111-1111-1111-111111111111", "Drama"),
                Movie("22222222-2222-2222-2222-222222222222", "Comedy")
            ]);
        host.FailingWriteItemIds.Add(firstId);
        var stateStore = new InMemoryStateStore();
        var runner = CreateRunner(host, stateStore);
        var options = new MetaTaggerRunOptions { PreviewOnly = false };

        var first = await runner.RunAsync(new NoOpProgress(), CancellationToken.None, options);
        var second = await runner.RunAsync(new NoOpProgress(), CancellationToken.None, options);

        Assert.Equal(1, first.Failures);
        Assert.Equal(0, second.Failures);
        Assert.Equal(
            [firstId, "22222222222222222222222222222222"],
            host.UpdateAttempts);
        Assert.Empty(stateStore.State.RunCursors);
    }

    private static MetaTaggerRunner CreateRunner(
        IMetaTaggerHost host,
        IMetaTaggerStateStore stateStore,
        IMetaTaggerClock? clock = null)
    {
        return new MetaTaggerRunner(
            host,
            new MetadataProjectionService(),
            new MetadataTagProcessor(
                new MetadataTagService(),
                new MetadataFingerprintService(),
                new TagMergeService()),
            stateStore,
            NullLogger<MetaTaggerRunner>.Instance,
            clock ?? new FixedClock());
    }

    private static PluginConfiguration MovieConfiguration()
    {
        return new PluginConfiguration
        {
            IncludeMovies = true,
            IncludeSeries = false,
            IncludeEpisodes = false,
            IncludeVideos = false
        };
    }

    private static MetaTaggerRunOptions IncludeVideos(
        PluginConfiguration configuration,
        MetaTaggerRunOptions options)
    {
        configuration.IncludeVideos = true;
        return options;
    }

    private static MetaTaggerRunOptions DisableGenres(
        PluginConfiguration configuration,
        MetaTaggerRunOptions options)
    {
        configuration.EnableGenres = false;
        return options;
    }

    private static Movie Movie(string id, string genre)
    {
        return new Movie
        {
            Id = Guid.Parse(id),
            Genres = [genre]
        };
    }

    private static MetaTaggerRunOptions EnableLanguages(PluginConfiguration configuration, MetaTaggerRunOptions options, bool audio)
    {
        configuration.EnableAudioLanguages = audio;
        configuration.EnableSubtitleLanguages = !audio;
        return options;
    }

    private static string[] PendingIds(MetaTaggerRunCursor cursor)
    {
        return cursor.PendingItemIds.Skip(cursor.NextIndex).ToArray();
    }

    private static MetaTaggerState Snapshot(MetaTaggerState state)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(state, StateJsonOptions);
        var snapshot = JsonSerializer.Deserialize<MetaTaggerState>(json, StateJsonOptions)
            ?? throw new InvalidOperationException("Could not clone in-memory metadata tagger state.");
        snapshot.Items = new Dictionary<string, MetaTaggerStateItem>(
            snapshot.Items,
            StringComparer.OrdinalIgnoreCase);
        snapshot.RunCursors = new Dictionary<string, MetaTaggerRunCursor>(
            snapshot.RunCursors,
            StringComparer.Ordinal);
        return snapshot;
    }

    private sealed class InMemoryHost(
        PluginConfiguration configuration,
        IReadOnlyList<BaseItem> items) : IMetaTaggerHost
    {
        public IReadOnlyList<BaseItem> Items { get; set; } = items;

        public List<string> UpdateAttempts { get; } = [];

        public IReadOnlyList<MediaBrowser.Model.Entities.MediaStream> GetMediaStreams(Guid itemId) => [];

        public HashSet<string> FailingWriteItemIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public PluginConfiguration GetConfiguration()
        {
            return configuration;
        }

        public IReadOnlyList<BaseItem> GetItems(BaseItemKind[] includedItemTypes)
        {
            return Items;
        }

        public BaseItem? GetItem(Guid itemId)
        {
            return Items.FirstOrDefault(item => item.Id == itemId);
        }

        public Task UpdateItemTagsAsync(
            BaseItem item,
            IReadOnlyCollection<string> tags,
            CancellationToken cancellationToken)
        {
            var itemId = item.Id.ToString("N");
            UpdateAttempts.Add(itemId);
            if (FailingWriteItemIds.Contains(itemId))
            {
                return Task.FromException(new IOException("Injected Jellyfin update failure."));
            }

            item.Tags = tags.ToArray();
            return Task.CompletedTask;
        }

        public void PublishRunConfiguration(MetaTaggerRunConfigurationPublication publication)
        {
            publication.ApplyTo(configuration);
        }
    }

    private sealed class InMemoryStateStore : IMetaTaggerStateStore
    {
        private MetaTaggerState _state;

        public InMemoryStateStore(MetaTaggerState? state = null)
        {
            _state = Snapshot(state ?? new MetaTaggerState());
        }

        public MetaTaggerState State => Snapshot(_state);

        public Task<MetaTaggerState> LoadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(Snapshot(_state));
        }

        public Task SaveAsync(MetaTaggerState state, CancellationToken cancellationToken)
        {
            _state = Snapshot(state);
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

    private sealed class NoOpProgress : IProgress<double>
    {
        public void Report(double value)
        {
        }
    }

    private sealed class FixedClock : IMetaTaggerClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class ManualClock : IMetaTaggerClock
    {
        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public void Advance(TimeSpan elapsed)
        {
            UtcNow += elapsed;
        }
    }

    private sealed class AdvanceClockOnFirstReport(ManualClock clock, TimeSpan elapsed) : IProgress<double>
    {
        private bool _advanced;

        public void Report(double value)
        {
            if (!_advanced)
            {
                _advanced = true;
                clock.Advance(elapsed);
            }
        }
    }
}
