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
            Generation = new GenerationState
            {
                Revision = 1, BaselineComplete = true, BaselineItemIds = [], BaselineItemCount = 0,
                AuthorizedItemTypes = ["Movie", "Series", "Episode", "Video"], AuthorizedItemIds = []
            }
        };
        return configuration;
    }
}
