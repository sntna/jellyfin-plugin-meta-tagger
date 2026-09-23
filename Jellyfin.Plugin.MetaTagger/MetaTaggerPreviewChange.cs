namespace Jellyfin.Plugin.MetaTagger;

public sealed class MetaTaggerPreviewChange
{
    public string ItemId { get; init; } = string.Empty;

    public string? ItemName { get; init; }

    public string? ItemPath { get; init; }

    public string? ItemType { get; init; }

    public IReadOnlyCollection<string> AddedTags { get; init; } = [];

    public IReadOnlyCollection<string> RemovedTags { get; init; } = [];

    public IReadOnlyCollection<string> PreviewRemovedTags { get; init; } = [];
}
