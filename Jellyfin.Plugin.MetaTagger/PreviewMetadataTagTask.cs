namespace Jellyfin.Plugin.MetaTagger;

public sealed class PreviewMetadataTagTask : MetaTaggerScheduledTaskBase
{
    public PreviewMetadataTagTask(MetaTaggerRunner runner)
        : base(runner)
    {
    }

    public override string Name => "Preview metadata tag changes";

    public override string Key => "MetaTaggerPreviewTags";

    public override string Description => "Previews tag changes for selected item types across all libraries using saved settings and run limits. Does not change tags in Jellyfin.";

    protected override MetaTaggerRunOptions CreateOptions()
    {
        return ConfiguredOptions(previewOnly: true);
    }
}
