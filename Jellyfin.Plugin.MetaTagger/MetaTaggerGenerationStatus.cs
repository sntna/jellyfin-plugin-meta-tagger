using Jellyfin.Plugin.MetaTagger.Configuration;

namespace Jellyfin.Plugin.MetaTagger;

public sealed class MetaTaggerGenerationStatus
{
    public string Status { get; init; } = "Ready";
    public string BaselineStatus { get; init; } = "NotCaptured";
    public int? BaselineItemCount { get; init; }
    public long EligibilityRevision { get; init; }
    public string[] AuthorizedItemTypes { get; init; } = [];
    public string[] AuthorizedItemIds { get; init; } = [];
    public GenerationEligibility? PriorGenerationEligibility { get; init; }
}
