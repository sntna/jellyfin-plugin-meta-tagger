namespace Jellyfin.Plugin.MetaTagger;

public sealed class MetaTaggerDashboardPreview
{
    public string Status { get; init; } = "NoRuns";

    public MetaTaggerRunSummary? Summary { get; init; }

    public IReadOnlyCollection<MetaTaggerPreviewChange> Changes { get; init; } = [];
}
