namespace Jellyfin.Plugin.MetaTagger;

public sealed class ApplyMetadataTagTask : MetaTaggerScheduledTaskBase
{
    public ApplyMetadataTagTask(MetaTaggerRunner runner)
        : base(runner)
    {
    }

    public override string Name => "Apply metadata tag changes";

    public override string Key => "MetaTaggerApplyTags";

    protected override bool AuthorizesBackfill => true;

    public override string Description => "Authorizes backfill for the selected item types across all libraries, then applies current metadata and saved settings even when automatic runs use Preview. Authorization remains after a partial run. Only removes recorded plugin tags and respects manual tags, locks, skip tags, and run limits.";

    protected override MetaTaggerRunOptions CreateOptions()
    {
        return ConfiguredOptions(previewOnly: false);
    }
}
