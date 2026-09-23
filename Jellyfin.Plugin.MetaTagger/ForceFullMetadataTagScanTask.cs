using Jellyfin.Plugin.MetaTagger.Configuration;

namespace Jellyfin.Plugin.MetaTagger;

public sealed class ForceFullMetadataTagScanTask : MetaTaggerScheduledTaskBase
{
    public ForceFullMetadataTagScanTask(MetaTaggerRunner runner)
        : base(runner)
    {
    }

    public override string Name => "Check all items for metadata tag changes";

    public override string Key => "MetaTaggerForceFullScan";

    public override string Description => "Rechecks all selected item types, including unchanged items. Previews changes when Preview scheduled and post-scan runs is checked; otherwise applies them. Respects locks, skip tags, and run limits.";

    protected override MetaTaggerRunOptions CreateOptions()
    {
        var configuration = Plugin.Instance?.Configuration;
        return ConfiguredOptions(
            previewOnly: configuration?.PreviewOnly ?? true,
            runMode: MetadataTagRunMode.FullScan,
            force: true);
    }
}
