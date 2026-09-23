using Jellyfin.Plugin.MetaTagger.Configuration;

namespace Jellyfin.Plugin.MetaTagger;

public sealed class MetaTaggerItemInspection
{
    public Guid ItemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ItemType { get; init; } = string.Empty;
    public IReadOnlyList<Guid> ArtworkItemIds { get; init; } = [];
    public string Status { get; set; } = "Ready";
    public string? Reason { get; set; }
    public string? ConfigurationRevision { get; set; }
    public string? Token { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    public IReadOnlyCollection<string> MissingTagsWithSourceOff { get; set; } = [];
    public IReadOnlyCollection<string> MissingRecordedTags { get; set; } = [];
    public IReadOnlyCollection<string> AddedTags { get; set; } = [];
    public IReadOnlyCollection<string> RemovedTags { get; set; } = [];
    public IReadOnlyCollection<string> PreviewRemovedTags { get; set; } = [];
    public IReadOnlyCollection<string> OwnedTags { get; set; } = [];
    public IReadOnlyCollection<string> ManualTags { get; set; } = [];
    public IReadOnlyCollection<string> PreservedTags { get; set; } = [];
    public IReadOnlyCollection<MetaTaggerTagSource> Sources { get; set; } = [];
    public IReadOnlyCollection<string> GeneratedTags { get; set; } = [];
}

public sealed class MetaTaggerExampleRequest
{
    public Guid ItemId { get; set; }
    public PluginConfiguration Configuration { get; set; } = new();
}

public sealed class MetaTaggerItemApplyRequest
{
    public string Token { get; set; } = string.Empty;
}
