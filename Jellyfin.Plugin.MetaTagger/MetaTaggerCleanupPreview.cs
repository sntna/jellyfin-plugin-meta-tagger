namespace Jellyfin.Plugin.MetaTagger;

public sealed class MetaTaggerCleanupPreview
{
    public string? Token { get; init; }

    public IReadOnlyCollection<string> MissingRecordedTags { get; init; } = [];

    public Guid? ItemId { get; init; }

    public required MetaTaggerRunSummary Summary { get; init; }

    public IReadOnlyCollection<MetaTaggerPreviewChange> Changes { get; init; } = [];
}
