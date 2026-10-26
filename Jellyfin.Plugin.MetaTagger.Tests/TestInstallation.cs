using Jellyfin.Plugin.MetaTagger.Configuration;

namespace Jellyfin.Plugin.MetaTagger.Tests;

internal static class TestInstallation
{
    internal static PluginConfiguration Established(PluginConfiguration configuration)
    {
        // Existing processing fixtures model a previously authorized installation.
        // Fresh and uncertain installation behavior uses real persisted plugin setup.
        configuration.Installation ??= new InstallationState
        {
            InstallationId = Guid.NewGuid(), MigrationVersion = 1, Origin = InstallationOrigin.Established,
            PriorGenerationEligibility = new GenerationEligibility
            {
                IncludedItemTypes = ["Movie", "Series", "Episode", "Video"],
                ConfiguredApply = true, PostScanApply = true, ApplyTask = true
            },
            Generation = RecordBaseline(
                new GenerationState
                {
                    Revision = 1,
                    AuthorizedItemTypes = ["Movie", "Series", "Episode", "Video"], AuthorizedItemIds = []
                },
                [])
        };
        return configuration;
    }

    // The item IDs live in the store; the saved policy records only what proves that file.
    internal static GenerationState RecordBaseline(GenerationState generation, IReadOnlyCollection<string> itemIds)
    {
        generation.BaselineComplete = true;
        generation.BaselineItemCount = itemIds.Count;
        generation.BaselineChecksum = MetaTaggerStateStore.ComputeBaselineChecksum(itemIds);
        return generation;
    }
}

internal sealed class TestBaseline
{
    internal string[] ItemIds { get; set; } = [];

    internal Task SaveAsync(IReadOnlyCollection<string> itemIds)
    {
        ItemIds = itemIds.ToArray();
        return Task.CompletedTask;
    }

    internal Task<string[]?> LoadAsync(int? expectedCount, string? expectedChecksum)
    {
        return Task.FromResult(expectedCount == ItemIds.Length
            && string.Equals(MetaTaggerStateStore.ComputeBaselineChecksum(ItemIds), expectedChecksum, StringComparison.Ordinal)
                ? ItemIds
                : null);
    }
}
