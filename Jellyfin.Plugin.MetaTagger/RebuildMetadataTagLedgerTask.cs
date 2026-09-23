using Jellyfin.Plugin.MetaTagger.Configuration;

namespace Jellyfin.Plugin.MetaTagger;

public sealed class RebuildMetadataTagLedgerTask : MetaTaggerScheduledTaskBase
{
    public RebuildMetadataTagLedgerTask(MetaTaggerRunner runner)
        : base(runner)
    {
    }

    public override string Name => "Rebuild plugin tag records";

    public override string Key => "MetaTaggerRebuildLedger";

    public override string Description => "Rechecks items and updates the plugin records used to track tags and skip unchanged items. Does not change tags in Jellyfin.";

    protected override MetaTaggerRunOptions CreateOptions()
    {
        return ConfiguredOptions(
            previewOnly: true,
            runMode: MetadataTagRunMode.RebuildTrackingLedger);
    }
}
