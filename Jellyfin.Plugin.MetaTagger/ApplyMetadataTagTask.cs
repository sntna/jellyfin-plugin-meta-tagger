namespace Jellyfin.Plugin.MetaTagger;

public sealed class ApplyMetadataTagTask : MetaTaggerScheduledTaskBase
{
    public ApplyMetadataTagTask(MetaTaggerRunner runner)
        : base(runner)
    {
    }

    public override string Name => "Apply metadata tag changes";

    public override string Key => "MetaTaggerApplyTags";

    public override string Description => "Rechecks current metadata and saved settings across all libraries, then applies changes even when automatic runs are set to preview. Changes may differ from an earlier preview. Only removes recorded plugin tags. Keeps manual tags and respects locks, skip tags, and run limits.";

    protected override MetaTaggerRunOptions CreateOptions()
    {
        return ConfiguredOptions(previewOnly: false);
    }
}
