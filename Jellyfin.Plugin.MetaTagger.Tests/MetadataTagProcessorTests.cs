using Jellyfin.Plugin.MetaTagger;
using Jellyfin.Plugin.MetaTagger.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed class MetadataTagProcessorTests
{
    [Fact]
    public void Process_SkipsManualSkipWithoutWritingOrUpdatingLedger()
    {
        var result = CreateProcessor().Process(
            Item(existingTags: ["manual:tagger:skip"], genres: ["Animation"]),
            new PluginConfiguration(),
            new MetaTaggerState(),
            new MetaTaggerRunOptions { PreviewOnly = false },
            Now);

        Assert.Equal(MetadataTagSkipReason.ManualSkip, result.SkipReason);
        Assert.False(result.ShouldWriteTags);
        Assert.Null(result.LedgerEntry);
    }

    [Fact]
    public void Process_LegacyManualLockAliasUsesManualSkipWithoutWritingOrUpdatingLedger()
    {
        var result = CreateProcessor().Process(
            Item(existingTags: ["manual:tagger:lock"], genres: ["Animation"]),
            new PluginConfiguration(),
            new MetaTaggerState(),
            new MetaTaggerRunOptions { PreviewOnly = false },
            Now);

        Assert.Equal(MetadataTagSkipReason.ManualSkip, result.SkipReason);
        Assert.False(result.ShouldWriteTags);
        Assert.Null(result.LedgerEntry);
    }

    [Fact]
    public void Process_IncrementalSkipsUnchangedLedgerBackedItem()
    {
        var processor = CreateProcessor();
        var config = new PluginConfiguration();
        var item = Item(existingTags: ["meta:genre:animation"], genres: ["Animation"]);
        var state = StateWithCurrentEntry(processor, item, config);

        var result = processor.Process(
            item,
            config,
            state,
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.Incremental },
            Now);

        Assert.Equal(MetadataTagSkipReason.Unchanged, result.SkipReason);
        Assert.False(result.ShouldWriteTags);
    }

    [Fact]
    public void Process_ManualForceBypassesIncrementalSkip()
    {
        var processor = CreateProcessor();
        var config = new PluginConfiguration();
        var item = Item(existingTags: ["meta:genre:animation", "manual:tagger:force"], genres: ["Animation"]);
        var state = StateWithCurrentEntry(processor, item, config);

        var result = processor.Process(
            item,
            config,
            state,
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.Incremental },
            Now);

        Assert.Null(result.SkipReason);
        Assert.False(result.ShouldWriteTags);
        Assert.NotNull(result.LedgerEntry);
    }

    [Fact]
    public void Process_FullScanReprocessesUnchangedItem()
    {
        var processor = CreateProcessor();
        var config = new PluginConfiguration();
        var item = Item(existingTags: ["meta:genre:animation"], genres: ["Animation"]);
        var state = StateWithCurrentEntry(processor, item, config);

        var result = processor.Process(
            item,
            config,
            state,
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan },
            Now);

        Assert.Null(result.SkipReason);
        Assert.False(result.ShouldWriteTags);
        Assert.NotNull(result.LedgerEntry);
    }

    [Fact]
    public void Process_RebuildLedgerDoesNotWriteJellyfinTags()
    {
        var result = CreateProcessor().Process(
            Item(existingTags: ["friendship", "meta:genre:animation"], genres: ["Animation"]),
            new PluginConfiguration(),
            new MetaTaggerState(),
            new MetaTaggerRunOptions
            {
                RunMode = MetadataTagRunMode.RebuildTrackingLedger,
                PreviewOnly = false
            },
            Now);

        Assert.False(result.ShouldWriteTags);
        Assert.NotNull(result.LedgerEntry);
        Assert.Equal(["meta:genre:animation"], result.LedgerEntry.LastAppliedTags);
    }

    [Fact]
    public void Process_MarksMatchingLegacyGeneratedTagsOwned()
    {
        var result = CreateProcessor().Process(
            Item(existingTags: ["meta:genre:animation"], genres: ["Animation"]),
            new PluginConfiguration(),
            new MetaTaggerState(),
            new MetaTaggerRunOptions(),
            Now);

        Assert.NotNull(result.LedgerEntry);
        Assert.Equal(["meta:genre:animation"], result.LedgerEntry.LastAppliedTags);
    }

    [Fact]
    public void Process_ClaimModeMarksKeptLegacyGeneratedTagsOwned()
    {
        var config = new PluginConfiguration { ClaimExistingGeneratedTagsForCleanup = true };

        var result = CreateProcessor().Process(
            Item(existingTags: ["meta:genre:legacy"], genres: ["Animation"]),
            config,
            new MetaTaggerState(),
            new MetaTaggerRunOptions(),
            Now);

        Assert.NotNull(result.LedgerEntry);
        Assert.Equal(["meta:genre:legacy"], result.LedgerEntry.LastAppliedTags);
    }

    [Fact]
    public void Process_KeepThenRemove_RemovesRetainedOwnedStaleTag()
    {
        var removed = PreserveOwnedStaleTagThenRemove(
            StaleTagMode.Keep,
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan, PreviewOnly = false });

        Assert.Equal(["meta:genre:classic"], removed.Merge.RemovedTags);
    }

    [Fact]
    public void Process_StalePreviewThenRemove_RemovesRetainedOwnedStaleTag()
    {
        var removed = PreserveOwnedStaleTagThenRemove(
            StaleTagMode.Preview,
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan, PreviewOnly = false });

        Assert.Equal(["meta:genre:classic"], removed.Merge.RemovedTags);
    }

    [Fact]
    public void Process_PreviewOnlyRemoveThenApply_RemovesRetainedOwnedStaleTag()
    {
        var removed = PreserveOwnedStaleTagThenRemove(
            StaleTagMode.Remove,
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan, PreviewOnly = true });

        Assert.Equal(["meta:genre:classic"], removed.Merge.RemovedTags);
    }

    [Fact]
    public void Process_RebuildThenRemove_RemovesRetainedOwnedStaleTag()
    {
        var removed = PreserveOwnedStaleTagThenRemove(
            StaleTagMode.Remove,
            new MetaTaggerRunOptions
            {
                RunMode = MetadataTagRunMode.RebuildTrackingLedger,
                PreviewOnly = false
            });

        Assert.Equal(["meta:genre:classic"], removed.Merge.RemovedTags);
    }

    [Fact]
    public void Process_ApplyingRemoveDropsOwnershipOfRemovedTag()
    {
        var processor = CreateProcessor();
        var config = new PluginConfiguration
        {
            EnableExistingTagsAsKeywords = false,
            StaleTagMode = StaleTagMode.Remove
        };
        var owned = processor.Process(
            Item(existingTags: ["meta:genre:classic"], genres: ["Classic"]),
            config,
            new MetaTaggerState(),
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan, PreviewOnly = false },
            Now);

        var removed = processor.Process(
            Item(existingTags: ["meta:genre:classic"], genres: []),
            config,
            StateFrom(owned),
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan, PreviewOnly = false },
            Now.AddMinutes(1));

        Assert.Empty(removed.LedgerEntry!.LastAppliedTags);
    }

    [Fact]
    public void Process_DropsOwnershipOfExternallyAbsentTag()
    {
        var processor = CreateProcessor();
        var config = new PluginConfiguration { EnableExistingTagsAsKeywords = false };
        var owned = processor.Process(
            Item(existingTags: ["meta:genre:classic"], genres: ["Classic"]),
            config,
            new MetaTaggerState(),
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan, PreviewOnly = false },
            Now);

        var absent = processor.Process(
            Item(existingTags: [], genres: []),
            config,
            StateFrom(owned),
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan, PreviewOnly = false },
            Now.AddMinutes(1));

        Assert.Empty(absent.LedgerEntry!.LastAppliedTags);
    }

    [Fact]
    public void Process_DoesNotInferOwnershipFromGeneratedPrefix()
    {
        var result = CreateProcessor().Process(
            Item(existingTags: ["meta:genre:legacy"], genres: []),
            new PluginConfiguration { EnableExistingTagsAsKeywords = false },
            new MetaTaggerState(),
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan, PreviewOnly = false },
            Now);

        Assert.Empty(result.LedgerEntry!.LastAppliedTags);
    }

    [Fact]
    public void Process_RetainsOwnedTagsCaseInsensitivelyWithoutDuplicates()
    {
        var state = new MetaTaggerState
        {
            Items =
            {
                ["item-1"] = new MetaTaggerStateItem
                {
                    ItemId = "item-1",
                    LastAppliedTags = ["META:GENRE:CLASSIC", "meta:genre:classic"]
                }
            }
        };

        var result = CreateProcessor().Process(
            Item(existingTags: ["Meta:Genre:Classic"], genres: []),
            new PluginConfiguration { EnableExistingTagsAsKeywords = false },
            state,
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan, PreviewOnly = false },
            Now);

        Assert.Equal(["META:GENRE:CLASSIC"], result.LedgerEntry!.LastAppliedTags);
    }

    [Theory]
    [InlineData("projected", ":", "meta:genre:classic", "projected:genre:classic")]
    [InlineData("meta", "|", "meta:genre:classic", "meta|genre|classic")]
    public void Process_GeneratedNamespaceChangeMigratesExactOwnedTag(
        string generatedPrefix,
        string separator,
        string oldOwnedTag,
        string newGeneratedTag)
    {
        var processor = CreateProcessor();
        var oldConfig = ConfigurationForMigration();
        var previous = processor.Process(
            Item(existingTags: ["friendship", oldOwnedTag], genres: ["Classic"]),
            oldConfig,
            new MetaTaggerState(),
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan, PreviewOnly = false },
            Now);
        var newConfig = ConfigurationForMigration(
            generatedPrefix,
            separator,
            StaleTagMode.Remove);

        var result = processor.Process(
            Item(existingTags: ["friendship", oldOwnedTag], genres: ["Classic"]),
            newConfig,
            StateFrom(previous),
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan, PreviewOnly = false },
            Now.AddMinutes(1));

        Assert.Equal(["friendship", newGeneratedTag], result.Merge.FinalTags);
        Assert.Equal([newGeneratedTag], result.Merge.AddedTags);
        Assert.Equal([oldOwnedTag], result.Merge.RemovedTags);
        Assert.Empty(result.Merge.PreviewRemovedTags);
        Assert.Empty(result.Merge.LegacyTagsClaimed);
        Assert.Empty(result.Merge.LegacyTagsKept);
        Assert.True(result.ShouldWriteTags);
        Assert.Equal([newGeneratedTag], result.LedgerEntry!.LastAppliedTags);
    }

    [Theory]
    [InlineData(StaleTagMode.Keep, false)]
    [InlineData(StaleTagMode.Preview, true)]
    public void Process_NonRemovingNamespaceMigrationRetainsOwnershipForLaterRemove(
        StaleTagMode preservingMode,
        bool previewsRemoval)
    {
        const string OldOwnedTag = "meta:genre:classic";
        const string NewGeneratedTag = "projected:genre:classic";
        var processor = CreateProcessor();
        var previous = processor.Process(
            Item(existingTags: ["friendship", OldOwnedTag], genres: ["Classic"]),
            ConfigurationForMigration(),
            new MetaTaggerState(),
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan, PreviewOnly = false },
            Now);
        var newConfig = ConfigurationForMigration("projected", ":", preservingMode);

        var preserved = processor.Process(
            Item(existingTags: ["friendship", OldOwnedTag], genres: ["Classic"]),
            newConfig,
            StateFrom(previous),
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan, PreviewOnly = false },
            Now.AddMinutes(1));

        Assert.Equal(["friendship", OldOwnedTag, NewGeneratedTag], preserved.Merge.FinalTags);
        Assert.Equal([NewGeneratedTag], preserved.Merge.AddedTags);
        Assert.Empty(preserved.Merge.RemovedTags);
        Assert.Equal(
            previewsRemoval ? [OldOwnedTag] : Array.Empty<string>(),
            preserved.Merge.PreviewRemovedTags);
        Assert.Empty(preserved.Merge.LegacyTagsClaimed);
        Assert.Empty(preserved.Merge.LegacyTagsKept);
        Assert.True(preserved.ShouldWriteTags);
        Assert.Equal([NewGeneratedTag, OldOwnedTag], preserved.LedgerEntry!.LastAppliedTags);

        newConfig.StaleTagMode = StaleTagMode.Remove;
        var removed = processor.Process(
            Item(existingTags: preserved.Merge.FinalTags, genres: ["Classic"]),
            newConfig,
            StateFrom(preserved),
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan, PreviewOnly = false },
            Now.AddMinutes(2));

        Assert.Equal(["friendship", NewGeneratedTag], removed.Merge.FinalTags);
        Assert.Empty(removed.Merge.AddedTags);
        Assert.Equal([OldOwnedTag], removed.Merge.RemovedTags);
        Assert.Empty(removed.Merge.PreviewRemovedTags);
        Assert.Empty(removed.Merge.LegacyTagsClaimed);
        Assert.Empty(removed.Merge.LegacyTagsKept);
        Assert.True(removed.ShouldWriteTags);
        Assert.Equal([NewGeneratedTag], removed.LedgerEntry!.LastAppliedTags);
    }

    [Fact]
    public void Process_PreviewOnlyNamespaceMigrationRetainsOwnershipUntilApply()
    {
        const string OldOwnedTag = "meta:genre:classic";
        const string NewGeneratedTag = "projected:genre:classic";
        var processor = CreateProcessor();
        var previous = processor.Process(
            Item(existingTags: ["friendship", OldOwnedTag], genres: ["Classic"]),
            ConfigurationForMigration(),
            new MetaTaggerState(),
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan, PreviewOnly = false },
            Now);
        var newConfig = ConfigurationForMigration("projected", ":", StaleTagMode.Remove);

        var preview = processor.Process(
            Item(existingTags: ["friendship", OldOwnedTag], genres: ["Classic"]),
            newConfig,
            StateFrom(previous),
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan, PreviewOnly = true },
            Now.AddMinutes(1));

        Assert.Equal(["friendship", NewGeneratedTag], preview.Merge.FinalTags);
        Assert.Equal([NewGeneratedTag], preview.Merge.AddedTags);
        Assert.Equal([OldOwnedTag], preview.Merge.RemovedTags);
        Assert.Empty(preview.Merge.PreviewRemovedTags);
        Assert.Empty(preview.Merge.LegacyTagsClaimed);
        Assert.Empty(preview.Merge.LegacyTagsKept);
        Assert.False(preview.ShouldWriteTags);
        Assert.Equal([OldOwnedTag], preview.LedgerEntry!.LastAppliedTags);

        var applied = processor.Process(
            Item(existingTags: ["friendship", OldOwnedTag], genres: ["Classic"]),
            newConfig,
            StateFrom(preview),
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan, PreviewOnly = false },
            Now.AddMinutes(2));

        Assert.Equal(["friendship", NewGeneratedTag], applied.Merge.FinalTags);
        Assert.Equal([NewGeneratedTag], applied.Merge.AddedTags);
        Assert.Equal([OldOwnedTag], applied.Merge.RemovedTags);
        Assert.Empty(applied.Merge.PreviewRemovedTags);
        Assert.Empty(applied.Merge.LegacyTagsClaimed);
        Assert.Empty(applied.Merge.LegacyTagsKept);
        Assert.True(applied.ShouldWriteTags);
        Assert.Equal([NewGeneratedTag], applied.LedgerEntry!.LastAppliedTags);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Process_DoesNotUseAuditNamespaceMetadataToClaimUnownedOldTag(bool claimExistingTags)
    {
        const string OldUnownedTag = "meta:genre:classic";
        const string NewGeneratedTag = "projected:genre:classic";
        var state = new MetaTaggerState
        {
            Items =
            {
                ["item-1"] = new MetaTaggerStateItem
                {
                    ItemId = "item-1",
                    LastAppliedTags = [],
                    GeneratedTagPrefixAtWrite = "meta:",
                    ManualTagPrefixAtWrite = "manual:",
                    TagSeparatorAtWrite = ":"
                }
            }
        };
        var config = ConfigurationForMigration("projected", ":", StaleTagMode.Remove);
        config.ClaimExistingGeneratedTagsForCleanup = claimExistingTags;

        var result = CreateProcessor().Process(
            Item(existingTags: ["friendship", OldUnownedTag], genres: ["Classic"]),
            config,
            state,
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan, PreviewOnly = false },
            Now);

        Assert.Equal(["friendship", OldUnownedTag, NewGeneratedTag], result.Merge.FinalTags);
        Assert.Equal([NewGeneratedTag], result.Merge.AddedTags);
        Assert.Empty(result.Merge.RemovedTags);
        Assert.Empty(result.Merge.PreviewRemovedTags);
        Assert.Empty(result.Merge.LegacyTagsClaimed);
        Assert.Empty(result.Merge.LegacyTagsKept);
        Assert.True(result.ShouldWriteTags);
        Assert.Equal([NewGeneratedTag], result.LedgerEntry!.LastAppliedTags);
        Assert.Equal("projected:", result.LedgerEntry.GeneratedTagPrefixAtWrite);
        Assert.Equal(":", result.LedgerEntry.TagSeparatorAtWrite);
    }

    [Fact]
    public void Process_PreservesOldOwnedCurrentManualTagAndCurrentNamespaceClaimBehavior()
    {
        var state = new MetaTaggerState
        {
            Items =
            {
                ["item-1"] = new MetaTaggerStateItem
                {
                    ItemId = "item-1",
                    LastAppliedTags = ["ARCHIVE:CURATED"],
                    GeneratedTagPrefixAtWrite = "archive:",
                    ManualTagPrefixAtWrite = "manual:",
                    TagSeparatorAtWrite = ":"
                }
            }
        };
        var config = ConfigurationForMigration("meta", ":", StaleTagMode.Remove);
        config.ManualTagPrefix = "archive";
        config.ClaimExistingGeneratedTagsForCleanup = true;

        var result = CreateProcessor().Process(
            Item(
                existingTags: ["friendship", "archive:curated", "meta:genre:legacy"],
                genres: ["Animation"]),
            config,
            state,
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan, PreviewOnly = false },
            Now);

        Assert.Equal(
            ["friendship", "archive:curated", "meta:genre:animation"],
            result.Merge.FinalTags);
        Assert.Equal(["meta:genre:animation"], result.Merge.AddedTags);
        Assert.Equal(["meta:genre:legacy"], result.Merge.RemovedTags);
        Assert.Empty(result.Merge.PreviewRemovedTags);
        Assert.Equal(["meta:genre:legacy"], result.Merge.LegacyTagsClaimed);
        Assert.Empty(result.Merge.LegacyTagsKept);
        Assert.True(result.ShouldWriteTags);
        Assert.Equal(
            ["meta:genre:animation", "ARCHIVE:CURATED"],
            result.LedgerEntry!.LastAppliedTags);
    }

    [Fact]
    public void Process_UsesOnlyCurrentItemTagsForManualControls()
    {
        var result = CreateProcessor().Process(
            new MetadataTagInput
            {
                ItemId = "item-1",
                ItemPath = "/media/item.mkv",
                ItemType = "Episode",
                ExistingTags = [],
                KeywordSourceTags = ["manual:tagger:skip", "friendship"],
                Genres = ["Animation"]
            },
            new PluginConfiguration { EnableExistingTagsAsKeywords = true, IncludeEpisodes = true },
            new MetaTaggerState(),
            new MetaTaggerRunOptions { PreviewOnly = false },
            Now);

        Assert.Null(result.SkipReason);
        Assert.True(result.ShouldWriteTags);
        Assert.Contains("meta:genre:animation", result.Merge.AddedTags);
        Assert.Contains("meta:keyword:friendship", result.Merge.AddedTags);
        Assert.DoesNotContain("meta:keyword:manual-tagger-skip", result.Merge.AddedTags);
    }

    private static readonly DateTimeOffset Now = new(2026, 6, 29, 0, 0, 0, TimeSpan.Zero);

    private static MetadataTagProcessor CreateProcessor()
    {
        return new MetadataTagProcessor(
            new MetadataTagService(),
            new MetadataFingerprintService(),
            new TagMergeService());
    }

    private static PluginConfiguration ConfigurationForMigration(
        string generatedPrefix = "meta",
        string separator = ":",
        StaleTagMode staleTagMode = StaleTagMode.Keep)
    {
        return new PluginConfiguration
        {
            GeneratedTagPrefix = generatedPrefix,
            TagSeparator = separator,
            EnableExistingTagsAsKeywords = false,
            StaleTagMode = staleTagMode
        };
    }

    private static MetadataTagInput Item(
        IReadOnlyCollection<string> existingTags,
        IReadOnlyCollection<string> genres)
    {
        return new MetadataTagInput
        {
            ItemId = "item-1",
            ItemPath = "/media/item.mkv",
            ItemType = "Movie",
            ExistingTags = existingTags,
            KeywordSourceTags = existingTags,
            Genres = genres
        };
    }

    private static MetaTaggerState StateWithCurrentEntry(
        MetadataTagProcessor processor,
        MetadataTagInput item,
        PluginConfiguration config)
    {
        var initial = processor.Process(
            item,
            config,
            new MetaTaggerState(),
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan },
            Now);

        return new MetaTaggerState
        {
            Items = { [item.ItemId] = initial.LedgerEntry! }
        };
    }

    private static MetadataTagProcessResult PreserveOwnedStaleTagThenRemove(
        StaleTagMode preservingStaleTagMode,
        MetaTaggerRunOptions preservingOptions)
    {
        var processor = CreateProcessor();
        var config = new PluginConfiguration
        {
            EnableExistingTagsAsKeywords = false,
            StaleTagMode = preservingStaleTagMode
        };
        var owned = processor.Process(
            Item(existingTags: ["meta:genre:classic"], genres: ["Classic"]),
            config,
            new MetaTaggerState(),
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan, PreviewOnly = false },
            Now);
        var preserved = processor.Process(
            Item(existingTags: ["meta:genre:classic"], genres: []),
            config,
            StateFrom(owned),
            preservingOptions,
            Now.AddMinutes(1));

        config.StaleTagMode = StaleTagMode.Remove;
        return processor.Process(
            Item(existingTags: ["meta:genre:classic"], genres: []),
            config,
            StateFrom(preserved),
            new MetaTaggerRunOptions { RunMode = MetadataTagRunMode.FullScan, PreviewOnly = false },
            Now.AddMinutes(2));
    }

    private static MetaTaggerState StateFrom(MetadataTagProcessResult result)
    {
        return new MetaTaggerState
        {
            Items = { [result.LedgerEntry!.ItemId] = result.LedgerEntry }
        };
    }
}
