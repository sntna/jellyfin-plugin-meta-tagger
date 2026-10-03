using System.Reflection;
using System.Xml.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaTagger;
using Jellyfin.Plugin.MetaTagger.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed partial class MetaTaggerRunnerTests
{
    [Theory]
    [InlineData("configuration")]
    [InlineData("data")]
    public async Task Migration_OnlySavedTaskRecordsSurvive_CannotApplyUnknownPolicyAfterRestart(string location)
    {
        // Jellyfin 12's persisted ID for ApplyMetadataTagTask, independent of this migration schema.
        var taskPath = Path.Combine(_directory, location, "ScheduledTasks", "0c909423-90d1-b9bf-307b-c427d7ce7591.js");
        Directory.CreateDirectory(Path.GetDirectoryName(taskPath)!);
        File.WriteAllText(taskPath, "[{\"Type\":\"WeeklyTrigger\",\"DayOfWeek\":\"Sunday\",\"TimeOfDayTicks\":828000000000}]");
        var original = File.ReadAllText(taskPath);
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };

        for (var restart = 0; restart < 2; restart++)
        {
            var plugin = CreatePersistedPlugin();
            var runner = CreateRunner(new PersistedConfigurationHost(plugin, [movie]));
            await new ApplyMetadataTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);

            Assert.Empty(movie.Tags);
            Assert.Equal(InstallationOrigin.Uncertain, plugin.Configuration.Installation!.Origin);
            Assert.Null(plugin.Configuration.Installation.PriorGenerationEligibility);
            Assert.Equal(original, File.ReadAllText(taskPath));
        }
    }

    [Fact]
    public async Task Migration_FreshSetup_PersistsIdentityAndKeepsExistingPreviewDefaults()
    {
        var plugin = CreatePersistedPlugin();
        var id = plugin.Configuration.Installation!.InstallationId;
        plugin = CreatePersistedPlugin();
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };

        var result = await CreateRunner(new PersistedConfigurationHost(plugin, [item]))
            .RunAsync(new NoOpProgress(), CancellationToken.None);

        Assert.True(result.PreviewOnly);
        Assert.Empty(item.Tags);
        Assert.False(plugin.Configuration.RunAfterLibraryScan);
        Assert.Equal(id, plugin.Configuration.Installation!.InstallationId);
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(InstallationOrigin.Fresh, plugin.Configuration.Installation.Origin);
        Assert.Empty(plugin.Configuration.Installation.PriorGenerationEligibility!.IncludedItemTypes!);
        Assert.False(plugin.Configuration.Installation.PriorGenerationEligibility.ApplyTask);
    }

    [Fact]
    public async Task Migration_InterruptedInitialConfigurationWrite_DoesNotProveFreshSetup()
    {
        var configurationPath = Path.Combine(_directory, "configuration", "Jellyfin.Plugin.MetaTagger.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)!);
        File.WriteAllText(configurationPath + ".interrupted.tmp", "<interrupted>");
        var plugin = CreatePersistedPlugin();
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };

        var result = await CreateRunner(new PersistedConfigurationHost(plugin, [item]))
            .RunAsync(new NoOpProgress(), CancellationToken.None, new MetaTaggerRunOptions { PreviewOnly = false });

        Assert.True(result.PreviewOnly);
        Assert.Equal("Preview fallback", result.Outcome);
        Assert.Empty(item.Tags);
        Assert.Equal(InstallationOrigin.Uncertain, plugin.Configuration.Installation!.Origin);
        Assert.Null(plugin.Configuration.Installation.PriorGenerationEligibility);
    }

    [Fact]
    public void Migration_UnreadableSavedConfiguration_DoesNotReplaceItWithNewDefaults()
    {
        var path = WriteLegacyConfiguration(previewOnly: false);
        File.WriteAllText(path, "<interrupted>");

        Assert.Throws<InvalidOperationException>(() => CreatePersistedPlugin());

        Assert.Equal("<interrupted>", File.ReadAllText(path));
    }

    [Fact]
    public async Task Migration_MissingConfigurationWithAnEmptyLedger_RemainsUncertainAfterRestart()
    {
        var dataPath = Path.Combine(_directory, "plugins", "Jellyfin.Plugin.MetaTagger");
        var store = new MetaTaggerStateStore(dataPath);
        await store.SaveAsync(new MetaTaggerState(), CancellationToken.None);
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["manual:keep"] };

        for (var restart = 0; restart < 2; restart++)
        {
            var plugin = CreatePersistedPlugin();
            var result = await CreateRunner(new PersistedConfigurationHost(plugin, [item]), store)
                .RunAsync(new NoOpProgress(), CancellationToken.None, new MetaTaggerRunOptions { PreviewOnly = false });

            Assert.True(result.PreviewOnly);
            Assert.Equal(0, result.WritesApplied);
            Assert.Equal(["manual:keep"], item.Tags);
            Assert.Equal(InstallationOrigin.Uncertain, plugin.Configuration.Installation!.Origin);
            Assert.Equal(0, plugin.Configuration.Installation.MigrationVersion);
            Assert.Null(plugin.Configuration.Installation.PriorGenerationEligibility);
        }
    }

    [Fact]
    public async Task Migration_UncertainInstallation_CannotApproveSingleItemGeneration()
    {
        var dataPath = Path.Combine(_directory, "plugins", "Jellyfin.Plugin.MetaTagger");
        await new MetaTaggerStateStore(dataPath).SaveAsync(new MetaTaggerState(), CancellationToken.None);
        var plugin = CreatePersistedPlugin();
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, [item]));

        var preview = await runner.PreviewItemAsync(item.Id, CancellationToken.None);

        Assert.Null(preview.Token);
        Assert.Equal("InstallationUnavailable", preview.Status);
        Assert.Empty(item.Tags);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Migration_SavedModeScopeAndEligibility_SurviveRestartWithoutOwningExistingTags(bool previewOnly)
    {
        WriteLegacyConfiguration(previewOnly);
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["meta:genre:legacy", "manual:keep", "unmanaged"] };
        var series = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), Genres = ["Comedy"] };
        var plugin = CreatePersistedPlugin();
        var installationId = plugin.Configuration.Installation!.InstallationId;
        var store = new MetaTaggerStateStore(_directory);

        for (var restart = 0; restart < 2; restart++)
        {
            plugin = CreatePersistedPlugin();
            var result = await CreateRunner(new PersistedConfigurationHost(plugin, [movie, series]), store)
                .RunAsync(new NoOpProgress(), CancellationToken.None);

            Assert.Equal(previewOnly, result.PreviewOnly);
            Assert.Equal(1, result.ItemsScanned);
            Assert.True(plugin.Configuration.RunAfterLibraryScan);
            Assert.False(plugin.Configuration.EnableParentalRating);
            Assert.False(plugin.Configuration.EnableAudioLanguages);
            Assert.False(plugin.Configuration.IncludeSeries);
            Assert.Empty(series.Tags);
            Assert.Contains("manual:keep", movie.Tags);
            Assert.Contains("unmanaged", movie.Tags);
            Assert.Contains("meta:genre:legacy", movie.Tags);
            Assert.Equal(installationId, plugin.Configuration.Installation!.InstallationId);
            Assert.True(plugin.Configuration.Installation.MigrationVersion > 0);
            Assert.Equal(InstallationOrigin.Established, plugin.Configuration.Installation.Origin);
            var eligibility = plugin.Configuration.Installation.PriorGenerationEligibility!;
            Assert.Equal(["Movie"], eligibility.IncludedItemTypes!);
            Assert.Equal(!previewOnly, eligibility.ConfiguredApply);
            Assert.Equal(!previewOnly, eligibility.PostScanApply);
            Assert.True(eligibility.ApplyTask);
            var ledger = await store.LoadAsync(CancellationToken.None);
            Assert.DoesNotContain("meta:genre:legacy", ledger.Items.GetValueOrDefault(movie.Id.ToString("N"))?.LastAppliedTags ?? []);
        }

        Assert.Equal(previewOnly ? ["meta:genre:legacy", "manual:keep", "unmanaged"]
            : ["meta:genre:legacy", "manual:keep", "unmanaged", "meta:genre:drama"], movie.Tags);
    }

    [Fact]
    public async Task Migration_SavedPreviewWithDeliberateApplyTask_PreservesScopeAndUnrelatedArmedActions()
    {
        var path = WriteLegacyConfiguration(previewOnly: true);
        File.WriteAllText(path, File.ReadAllText(path).Replace("</PluginConfiguration>", """
              <ForceFullScanOnNextRun>true</ForceFullScanOnNextRun>
              <RebuildTrackingLedgerOnNextRun>true</RebuildTrackingLedgerOnNextRun>
              <ClaimExistingGeneratedTagsOnNextRun>true</ClaimExistingGeneratedTagsOnNextRun>
            </PluginConfiguration>
            """, StringComparison.Ordinal));
        var plugin = CreatePersistedPlugin();
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var series = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), Genres = ["Comedy"] };
        var runner = CreateRunner(new PersistedConfigurationHost(plugin, [movie, series]));

        await new ApplyMetadataTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);
        var restarted = CreatePersistedPlugin();

        Assert.Equal(["meta:genre:drama"], movie.Tags);
        Assert.Empty(series.Tags);
        Assert.True(restarted.Configuration.PreviewOnly);
        Assert.True(restarted.Configuration.ForceFullScanOnNextRun);
        Assert.True(restarted.Configuration.RebuildTrackingLedgerOnNextRun);
        Assert.True(restarted.Configuration.ClaimExistingGeneratedTagsOnNextRun);
        Assert.True(restarted.Configuration.Installation!.PriorGenerationEligibility!.ApplyTask);
        Assert.False(restarted.Configuration.Installation.PriorGenerationEligibility.ConfiguredApply);
    }

    [Fact]
    public async Task Migration_IncompleteRecord_CannotGrantGenerationOrOverwriteTheSavedConfiguration()
    {
        var path = WriteLegacyConfiguration(previewOnly: false);
        var serializer = new PersistedXmlSerializer();
        var incomplete = (PluginConfiguration)serializer.DeserializeFromFile(typeof(PluginConfiguration), path);
        incomplete.Installation = new InstallationState { InstallationId = Guid.NewGuid(), Origin = InstallationOrigin.Established };
        serializer.SerializeToFile(incomplete, path);
        var original = File.ReadAllText(path);
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            var plugin = CreatePersistedPlugin();
            await CreateRunner(new PersistedConfigurationHost(plugin, [item]))
                .RunAsync(new NoOpProgress(), CancellationToken.None);
        });

        Assert.Empty(item.Tags);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void Migration_MissingEligibilityFields_CannotPublishCompletion()
    {
        var path = WriteLegacyConfiguration(previewOnly: false);
        var serializer = new PersistedXmlSerializer();
        var configuration = (PluginConfiguration)serializer.DeserializeFromFile(typeof(PluginConfiguration), path);
        configuration.Installation = new InstallationState
        {
            InstallationId = Guid.NewGuid(), Origin = InstallationOrigin.Established, MigrationVersion = 1,
            PriorGenerationEligibility = new GenerationEligibility { IncludedItemTypes = ["Movie"] }
        };
        serializer.SerializeToFile(configuration, path);
        var original = File.ReadAllText(path);

        Assert.Throws<InvalidDataException>(() => CreatePersistedPlugin());

        Assert.Equal(original, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("Series")]
    [InlineData("Episode")]
    [InlineData("Video")]
    public async Task Migration_SavedOptionalSourceAndItemScope_ApplyAfterRestart(string itemType)
    {
        var path = WriteLegacyConfiguration(previewOnly: false);
        var scopeSetting = itemType == "Series" ? "IncludeSeries" : itemType == "Episode" ? "IncludeEpisodes" : "IncludeVideos";
        File.WriteAllText(path, File.ReadAllText(path)
            .Replace("<IncludeMovies>true</IncludeMovies>", "<IncludeMovies>false</IncludeMovies>", StringComparison.Ordinal)
            .Replace($"<{scopeSetting}>false</{scopeSetting}>", $"<{scopeSetting}>true</{scopeSetting}>", StringComparison.Ordinal)
            .Replace("<EnableGenres>true</EnableGenres>", "<EnableGenres>false</EnableGenres><EnableStudios>true</EnableStudios>", StringComparison.Ordinal));
        BaseItem selected = itemType switch
        {
            "Series" => new MediaBrowser.Controller.Entities.TV.Series(),
            "Episode" => new MediaBrowser.Controller.Entities.TV.Episode(),
            _ => new Video()
        };
        selected.Id = Guid.NewGuid();
        selected.Genres = ["Drama"];
        selected.Studios = ["Workshop Pictures"];
        var excluded = new Movie { Id = Guid.NewGuid(), Studios = ["Workshop Pictures"] };
        var plugin = CreatePersistedPlugin();
        var id = plugin.Configuration.Installation!.InstallationId;
        plugin = CreatePersistedPlugin();

        var summary = await CreateRunner(new PersistedConfigurationHost(plugin, [selected, excluded]))
            .RunAsync(new NoOpProgress(), CancellationToken.None);

        Assert.Equal(1, summary.WritesApplied);
        Assert.Equal(["meta:studio:workshop-pictures"], selected.Tags);
        Assert.Empty(excluded.Tags);
        Assert.False(plugin.Configuration.EnableGenres);
        Assert.True(plugin.Configuration.EnableStudios);
        Assert.Equal(id, plugin.Configuration.Installation!.InstallationId);
        Assert.Equal([itemType], plugin.Configuration.Installation.PriorGenerationEligibility!.IncludedItemTypes!);
    }

    [Fact]
    public async Task Migration_UnrelatedSettingsSave_PreservesIdentityAndPriorEligibility()
    {
        WriteLegacyConfiguration(previewOnly: false);
        var plugin = CreatePersistedPlugin();
        var id = plugin.Configuration.Installation!.InstallationId;
        // A dashboard save can omit new server-managed fields or send stale values.
        plugin.UpdateConfiguration(new PluginConfiguration
        {
            PreviewOnly = true, IncludeMovies = false, IncludeEpisodes = true,
            EnableAudioLanguages = false,
            Installation = new InstallationState { InstallationId = Guid.NewGuid(), Origin = InstallationOrigin.Fresh }
        });
        plugin = CreatePersistedPlugin();
        var episode = new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), Genres = ["Drama"] };

        var result = await CreateRunner(new PersistedConfigurationHost(plugin, [episode]))
            .RunAsync(new NoOpProgress(), CancellationToken.None);

        Assert.True(result.PreviewOnly);
        Assert.Empty(episode.Tags);
        Assert.Equal(id, plugin.Configuration.Installation!.InstallationId);
        Assert.Equal(InstallationOrigin.Established, plugin.Configuration.Installation.Origin);
        Assert.Equal(["Movie"], plugin.Configuration.Installation.PriorGenerationEligibility!.IncludedItemTypes!);
        Assert.True(plugin.Configuration.Installation.PriorGenerationEligibility.ConfiguredApply);
        Assert.True(plugin.Configuration.Installation.PriorGenerationEligibility.PostScanApply);
    }

    [Fact]
    public async Task Migration_FailedSettingsSave_CannotActivateUnsavedApplyPolicy()
    {
        var path = WriteLegacyConfiguration(previewOnly: true);
        var serializer = new PersistedXmlSerializer();
        var plugin = CreatePersistedPlugin(serializer);
        var id = plugin.Configuration.Installation!.InstallationId;
        var persisted = File.ReadAllText(path);
        serializer.FailWrites = true;

        Assert.Throws<IOException>(() => plugin.UpdateConfiguration(new PluginConfiguration { PreviewOnly = false }));
        Assert.Equal(persisted, File.ReadAllText(path));
        serializer.FailWrites = false;
        var movie = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"] };
        var result = await CreateRunner(new PersistedConfigurationHost(plugin, [movie]))
            .RunAsync(new NoOpProgress(), CancellationToken.None);
        var restarted = CreatePersistedPlugin();

        Assert.True(result.PreviewOnly);
        Assert.Empty(movie.Tags);
        Assert.True(restarted.Configuration.PreviewOnly);
        Assert.Equal(id, restarted.Configuration.Installation!.InstallationId);
        Assert.False(restarted.Configuration.Installation.PriorGenerationEligibility!.ConfiguredApply);
    }

    [Fact]
    public async Task Migration_WhenConfigurationPersistenceFails_CannotWriteTagsOrReplaceSavedPolicy()
    {
        var path = WriteLegacyConfiguration(previewOnly: false);
        var original = File.ReadAllText(path);
        var item = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["manual:keep", "unmanaged"] };
        var serializer = new PersistedXmlSerializer { FailWrites = true };

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            var plugin = CreatePersistedPlugin(serializer);
            await CreateRunner(new PersistedConfigurationHost(plugin, [item]))
                .RunAsync(new NoOpProgress(), CancellationToken.None);
        });

        Assert.Equal(["manual:keep", "unmanaged"], item.Tags);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));

        serializer.FailWrites = false;
        var restarted = CreatePersistedPlugin(serializer);
        var result = await CreateRunner(new PersistedConfigurationHost(restarted, [item]))
            .RunAsync(new NoOpProgress(), CancellationToken.None);
        Assert.Equal(1, result.WritesApplied);
        Assert.Equal(InstallationOrigin.Established, restarted.Configuration.Installation!.Origin);
        Assert.Contains("meta:genre:drama", item.Tags);
    }

    [Theory]
    [InlineData("incomplete", false)]
    [InlineData("incomplete", true)]
    [InlineData("save-failure", false)]
    [InlineData("save-failure", true)]
    public async Task Migration_FailedInitialization_BlocksSurvivingApplyTask(string failure, bool isEnabled)
    {
        var path = WriteLegacyConfiguration(previewOnly: false);
        var serializer = new PersistedXmlSerializer();
        var saved = (PluginConfiguration)serializer.DeserializeFromFile(typeof(PluginConfiguration), path);
        saved.IsEnabled = isEnabled;
        saved.IncludeMovies = false;
        saved.IncludeEpisodes = true;
        if (failure == "incomplete")
        {
            saved.Installation = new InstallationState { InstallationId = Guid.NewGuid(), Origin = InstallationOrigin.Established };
        }
        serializer.SerializeToFile(saved, path);
        var original = File.ReadAllText(path);
        serializer.FailWrites = failure == "save-failure";
        var taskPath = Path.Combine(_directory, "configuration", "ScheduledTasks", "0c909423-90d1-b9bf-307b-c427d7ce7591.js");
        Directory.CreateDirectory(Path.GetDirectoryName(taskPath)!);
        const string triggers = "[{\"Type\":\"WeeklyTrigger\",\"DayOfWeek\":\"Sunday\",\"TimeOfDayTicks\":828000000000}]";
        File.WriteAllText(taskPath, triggers);
        var movie = new MigrationRecordingMovie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["manual:keep", "unmanaged"] };
        var library = DispatchProxy.Create<ILibraryManager, MigrationLibraryManager>();
        var proxy = (MigrationLibraryManager)(object)library;
        proxy.Items = [movie];
        var media = DispatchProxy.Create<IMediaSourceManager, MigrationMediaSourceManager>();
        var store = new MetaTaggerStateStore(_directory);
        await store.SaveAsync(new MetaTaggerState(), CancellationToken.None);
        var originalState = File.ReadAllText(Path.Combine(_directory, "meta-tagger-state.json"));
        var instance = typeof(Plugin).GetProperty(nameof(Plugin.Instance))!;
        var previous = Plugin.Instance;
        instance.SetValue(null, null);
        try
        {
            if (failure == "incomplete")
            {
                Assert.Throws<InvalidDataException>(() => CreatePersistedPlugin(serializer));
            }
            else
            {
                Assert.Throws<IOException>(() => CreatePersistedPlugin(serializer));
            }
            Assert.Null(Plugin.Instance);

            // Jellyfin can instantiate and execute task exports after the plugin constructor fails.
            var runner = CreateRunner(new JellyfinMetaTaggerHost(library, media), store);
            var exception = await Record.ExceptionAsync(() => new ApplyMetadataTagTask(runner)
                .ExecuteAsync(new NoOpProgress(), CancellationToken.None));

            Assert.Equal(0, movie.Writes);
            Assert.IsType<InvalidOperationException>(exception);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(new NoOpProgress(), CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.PreviewItemAsync(movie.Id, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => new LibraryPostScanTask(runner, store, new MetaTaggerClock())
                .Run(new NoOpProgress(), CancellationToken.None));
            Assert.Equal(0, movie.Writes);
            Assert.Equal(["manual:keep", "unmanaged"], movie.Tags);
            Assert.Empty(proxy.Queries);
            Assert.Equal(original, File.ReadAllText(path));
            Assert.Equal(triggers, File.ReadAllText(taskPath));
            Assert.Equal(originalState, File.ReadAllText(Path.Combine(_directory, "meta-tagger-state.json")));
        }
        finally
        {
            instance.SetValue(null, previous);
        }
    }

    [Fact]
    public async Task Migration_SuccessfulInitialization_AllowsApplyTaskUsingThePublishedPolicy()
    {
        var path = WriteLegacyConfiguration(previewOnly: false);
        var serializer = new PersistedXmlSerializer();
        var previous = Plugin.Instance;
        var instance = typeof(Plugin).GetProperty(nameof(Plugin.Instance))!;
        instance.SetValue(null, null);
        try
        {
            Assert.Throws<IOException>(() => CreatePersistedPlugin(new PersistedXmlSerializer { FailWrites = true }));
            var plugin = CreatePersistedPlugin(serializer);
            var movie = new MigrationRecordingMovie { Id = Guid.NewGuid(), Genres = ["Drama"], Tags = ["manual:keep", "unmanaged"] };
            var library = DispatchProxy.Create<ILibraryManager, MigrationLibraryManager>();
            ((MigrationLibraryManager)(object)library).Items = [movie];
            var media = DispatchProxy.Create<IMediaSourceManager, MigrationMediaSourceManager>();
            var runner = CreateRunner(new JellyfinMetaTaggerHost(library, media));

            await new ApplyMetadataTagTask(runner).ExecuteAsync(new NoOpProgress(), CancellationToken.None);

            Assert.Same(plugin, Plugin.Instance);
            Assert.Equal(1, movie.Writes);
            Assert.Equal(["manual:keep", "unmanaged", "meta:genre:drama"], movie.Tags);
            Assert.Equal(InstallationOrigin.Established, plugin.Configuration.Installation!.Origin);
            Assert.Contains("<Installation>", File.ReadAllText(path));
        }
        finally
        {
            instance.SetValue(null, previous);
        }
    }

    private string WriteLegacyConfiguration(bool previewOnly)
    {
        var path = Path.Combine(_directory, "configuration", "Jellyfin.Plugin.MetaTagger.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"""
            <PluginConfiguration>
              <IsEnabled>true</IsEnabled>
              <PreviewOnly>{previewOnly.ToString().ToLowerInvariant()}</PreviewOnly>
              <EnableGenres>true</EnableGenres>
              <EnableOfficialRating>false</EnableOfficialRating>
              <EnableAudioLanguages>false</EnableAudioLanguages>
              <IncludeMovies>true</IncludeMovies>
              <IncludeSeries>false</IncludeSeries>
              <IncludeEpisodes>false</IncludeEpisodes>
              <IncludeVideos>false</IncludeVideos>
              <RunAfterLibraryScan>true</RunAfterLibraryScan>
              <MinimumMinutesBetweenAutoRuns>0</MinimumMinutesBetweenAutoRuns>
            </PluginConfiguration>
            """);
        return path;
    }

    private Plugin CreatePersistedPlugin(PersistedXmlSerializer? serializer = null)
    {
        var paths = DispatchProxy.Create<IApplicationPaths, MigrationApplicationPaths>();
        ((MigrationApplicationPaths)(object)paths).Root = _directory;
        return new Plugin(paths, serializer ?? new PersistedXmlSerializer());
    }

    public class MigrationApplicationPaths : DispatchProxy
    {
        public string Root { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                "get_PluginConfigurationsPath" => Path.Combine(Root, "configuration"),
                "get_PluginsPath" => Path.Combine(Root, "plugins"),
                "get_ConfigurationDirectoryPath" => Path.Combine(Root, "configuration"),
                "get_DataPath" => Path.Combine(Root, "data"),
                _ when targetMethod?.ReturnType == typeof(string) => Root,
                _ => throw new NotSupportedException(targetMethod?.Name)
            };
        }
    }

    private sealed class PersistedXmlSerializer : IXmlSerializer
    {
        public bool FailWrites { get; set; }

        public object DeserializeFromStream(Type type, Stream stream)
            => new XmlSerializer(type).Deserialize(stream)!;

        public void SerializeToStream(object obj, Stream stream)
            => new XmlSerializer(obj.GetType()).Serialize(stream, obj);

        public void SerializeToFile(object obj, string file)
        {
            using var stream = File.Create(file);
            if (FailWrites)
            {
                stream.Write("<interrupted>"u8);
                throw new IOException("Injected configuration persistence failure.");
            }
            SerializeToStream(obj, stream);
        }

        public object DeserializeFromFile(Type type, string file)
        {
            using var stream = File.OpenRead(file);
            return DeserializeFromStream(type, stream);
        }

        public object DeserializeFromBytes(Type type, byte[] buffer)
        {
            using var stream = new MemoryStream(buffer);
            return DeserializeFromStream(type, stream);
        }
    }

    private sealed class PersistedConfigurationHost(Plugin plugin, IReadOnlyList<BaseItem> items) : IMetaTaggerHost
    {
        public Action? BeforeQuery { get; set; }

        public PluginConfiguration GetConfiguration() => plugin.Configuration;

        public InstallationState SaveGenerationState(Guid installationId, GenerationState generation)
            => plugin.SaveGenerationState(installationId, generation);

        public IReadOnlyList<BaseItem> GetItems(BaseItemKind[] includedItemTypes)
        {
            BeforeQuery?.Invoke();
            return items.Where(item => includedItemTypes.Length == 0
                || includedItemTypes.Any(type => type.ToString() == item.GetType().Name)).ToArray();
        }

        public BaseItem? GetItem(Guid itemId) => items.FirstOrDefault(item => item.Id == itemId);

        public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId) => [];

        public Task UpdateItemTagsAsync(BaseItem item, IReadOnlyCollection<string> tags, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            item.Tags = tags.ToArray();
            return Task.CompletedTask;
        }

        public void PublishRunConfiguration(MetaTaggerRunConfigurationPublication publication)
        {
            var mutation = publication.ApplyTo(plugin.Configuration);
            try { plugin.SaveConfiguration(); }
            catch { mutation.RestoreAcknowledgedActions(); throw; }
        }
    }

    public class MigrationLibraryManager : DispatchProxy
    {
        public IReadOnlyList<BaseItem> Items { get; set; } = [];

        public List<InternalItemsQuery> Queries { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ILibraryManager.GetItemList) && args is [InternalItemsQuery query])
            {
                Queries.Add(query);
                return Items.Where(item => (query.ItemIds.Length == 0 || query.ItemIds.Contains(item.Id))
                    && (query.IncludeItemTypes.Length == 0 || query.IncludeItemTypes.Contains(BaseItemKind.Movie)))
                    .ToArray();
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    public class MigrationMediaSourceManager : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == nameof(IMediaSourceManager.GetMediaStreams)
                ? new List<MediaStream>()
                : throw new NotSupportedException(targetMethod?.Name);
    }

    private sealed class MigrationRecordingMovie : Movie
    {
        public int Writes { get; private set; }

        public bool FailWrites { get; set; }

        public override Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailWrites) { throw new IOException("Injected Jellyfin item write failure."); }
            Writes++;
            return Task.CompletedTask;
        }
    }
}
