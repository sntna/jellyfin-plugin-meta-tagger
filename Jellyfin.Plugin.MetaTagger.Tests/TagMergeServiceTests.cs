using Jellyfin.Plugin.MetaTagger;
using Jellyfin.Plugin.MetaTagger.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed class TagMergeServiceTests
{
    [Fact]
    public void Merge_KeepsExistingTagsAndStaleGeneratedTagsByDefault()
    {
        var service = new TagMergeService();
        var existing = new[] { "friendship", "manual:block-kids", "meta:genre:old" };
        var generated = new[] { "meta:genre:kids", "meta:rating:tv-y" };

        var result = service.Merge(existing, generated, new PluginConfiguration());

        Assert.Equal(
            ["friendship", "manual:block-kids", "meta:genre:old", "meta:genre:kids", "meta:rating:tv-y"],
            result.FinalTags);
        Assert.Equal(["meta:genre:kids", "meta:rating:tv-y"], result.AddedTags);
        Assert.Empty(result.RemovedTags);
        Assert.True(result.HasChangesToApply);
    }

    [Fact]
    public void Merge_KeepsUnownedLegacyGeneratedTagsWhenRemoveConfigured()
    {
        var service = new TagMergeService();
        var existing = new[] { "friendship", "manual:block-kids", "meta:genre:old", "meta:genre:kids" };
        var generated = new[] { "meta:genre:kids", "meta:rating:tv-y" };
        var config = new PluginConfiguration { StaleTagMode = StaleTagMode.Remove };

        var result = service.Merge(existing, generated, config);

        Assert.Equal(["friendship", "manual:block-kids", "meta:genre:old", "meta:genre:kids", "meta:rating:tv-y"], result.FinalTags);
        Assert.Equal(["meta:rating:tv-y"], result.AddedTags);
        Assert.Empty(result.RemovedTags);
        Assert.True(result.HasChangesToApply);
    }

    [Fact]
    public void Merge_RemovesOnlyLedgerOwnedStaleTagsWhenConfigured()
    {
        var service = new TagMergeService();
        var existing = new[] { "friendship", "meta:genre:old", "meta:keyword:legacy", "meta:genre:kids" };
        var generated = new[] { "meta:genre:kids", "meta:rating:tv-y" };
        var config = new PluginConfiguration { StaleTagMode = StaleTagMode.Remove };

        var result = service.Merge(existing, generated, config, ["meta:genre:old", "meta:genre:kids"]);

        Assert.Equal(["friendship", "meta:keyword:legacy", "meta:genre:kids", "meta:rating:tv-y"], result.FinalTags);
        Assert.Equal(["meta:rating:tv-y"], result.AddedTags);
        Assert.Equal(["meta:genre:old"], result.RemovedTags);
        Assert.Equal(["meta:keyword:legacy"], result.LegacyTagsKept);
    }

    [Fact]
    public void Merge_PreviewsOwnedStaleGeneratedTagRemovalWithoutApplyingIt()
    {
        var service = new TagMergeService();
        var existing = new[] { "friendship", "meta:genre:old" };
        var generated = new[] { "meta:rating:tv-y" };
        var config = new PluginConfiguration { StaleTagMode = StaleTagMode.Preview };

        var result = service.Merge(existing, generated, config, ["meta:genre:old"]);

        Assert.Equal(["friendship", "meta:genre:old", "meta:rating:tv-y"], result.FinalTags);
        Assert.Equal(["meta:rating:tv-y"], result.AddedTags);
        Assert.Equal(["meta:genre:old"], result.PreviewRemovedTags);
        Assert.Empty(result.RemovedTags);
        Assert.True(result.HasChangesToApply);
    }

    [Fact]
    public void Merge_ClaimsLegacyGeneratedTagsOnlyWhenConfigured()
    {
        var service = new TagMergeService();
        var existing = new[] { "friendship", "meta:genre:old", "meta:genre:kids" };
        var generated = new[] { "meta:genre:kids" };
        var config = new PluginConfiguration
        {
            StaleTagMode = StaleTagMode.Remove,
            ClaimExistingGeneratedTagsForCleanup = true
        };

        var result = service.Merge(existing, generated, config);

        Assert.Equal(["friendship", "meta:genre:kids"], result.FinalTags);
        Assert.Equal(["meta:genre:old"], result.RemovedTags);
        Assert.Equal(["meta:genre:old", "meta:genre:kids"], result.LegacyTagsClaimed);
    }

    [Fact]
    public void Merge_NeverRemovesManualTagsWhenPrefixesOverlap()
    {
        var service = new TagMergeService();
        var existing = new[] { "meta:manual:curated", "meta:genre:old" };
        var config = new PluginConfiguration
        {
            GeneratedTagPrefix = "meta:",
            ManualTagPrefix = "meta:manual:",
            StaleTagMode = StaleTagMode.Remove
        };

        var result = service.Merge(existing, [], config, ["meta:manual:curated", "meta:genre:old"]);

        Assert.Equal(["meta:manual:curated"], result.FinalTags);
        Assert.Equal(["meta:genre:old"], result.RemovedTags);
    }

    [Fact]
    public void Merge_PreservesOldOwnedCurrentManualTagWhileClaimingCurrentGeneratedLegacyTag()
    {
        var service = new TagMergeService();
        var config = new PluginConfiguration
        {
            GeneratedTagPrefix = "meta",
            ManualTagPrefix = "archive",
            StaleTagMode = StaleTagMode.Remove,
            ClaimExistingGeneratedTagsForCleanup = true
        };

        var result = service.Merge(
            ["friendship", "archive:curated", "meta:genre:legacy"],
            ["meta:genre:animation"],
            config,
            ["ARCHIVE:CURATED"]);

        Assert.Equal(["friendship", "archive:curated", "meta:genre:animation"], result.FinalTags);
        Assert.Equal(["meta:genre:animation"], result.AddedTags);
        Assert.Equal(["meta:genre:legacy"], result.RemovedTags);
        Assert.Empty(result.PreviewRemovedTags);
        Assert.Equal(["meta:genre:legacy"], result.LegacyTagsClaimed);
        Assert.Empty(result.LegacyTagsKept);
    }

    [Theory]
    [InlineData("projected", ":", "meta:genre:classic", "projected:genre:classic")]
    [InlineData("meta", "|", "meta:genre:classic", "meta|genre|classic")]
    public void Merge_RemovesExactOwnedTagAfterGeneratedNamespaceChange(
        string generatedPrefix,
        string separator,
        string oldOwnedTag,
        string newGeneratedTag)
    {
        var service = new TagMergeService();
        var config = new PluginConfiguration
        {
            GeneratedTagPrefix = generatedPrefix,
            TagSeparator = separator,
            StaleTagMode = StaleTagMode.Remove
        };

        var result = service.Merge(
            ["friendship", oldOwnedTag],
            [newGeneratedTag],
            config,
            [oldOwnedTag]);

        Assert.Equal(["friendship", newGeneratedTag], result.FinalTags);
        Assert.Equal([newGeneratedTag], result.AddedTags);
        Assert.Equal([oldOwnedTag], result.RemovedTags);
        Assert.Empty(result.PreviewRemovedTags);
        Assert.Empty(result.LegacyTagsClaimed);
        Assert.Empty(result.LegacyTagsKept);
    }

    [Fact]
    public void Merge_MatchesExactOwnedOldNamespaceTagCaseInsensitivelyWithoutDuplicates()
    {
        var service = new TagMergeService();
        var config = new PluginConfiguration
        {
            GeneratedTagPrefix = "projected",
            StaleTagMode = StaleTagMode.Remove,
            ClaimExistingGeneratedTagsForCleanup = true
        };

        var result = service.Merge(
            ["friendship", "Meta:Genre:Classic", "meta:genre:classic", "meta:genre:classic-extra"],
            ["projected:genre:classic", "PROJECTED:GENRE:CLASSIC"],
            config,
            ["META:GENRE:CLASSIC", "meta:genre:classic"]);

        Assert.Equal(
            ["friendship", "meta:genre:classic-extra", "projected:genre:classic"],
            result.FinalTags);
        Assert.Equal(["projected:genre:classic"], result.AddedTags);
        Assert.Equal(["Meta:Genre:Classic"], result.RemovedTags);
        Assert.Empty(result.PreviewRemovedTags);
        Assert.Empty(result.LegacyTagsClaimed);
        Assert.Empty(result.LegacyTagsKept);
    }
}
