using Jellyfin.Plugin.MetaTagger.Configuration;

namespace Jellyfin.Plugin.MetaTagger;

public sealed class MetaTaggerRunOptions
{
    public MetadataTagRunMode RunMode { get; init; } = MetadataTagRunMode.Incremental;

    public bool PreviewOnly { get; init; } = true;

    public bool Force { get; init; }

    internal string? Invocation { get; set; }

    internal bool ClearGeneratedTags { get; init; }

    internal Guid? CleanupItemId { get; init; }

    internal IReadOnlyDictionary<string, MetaTaggerPreviewChange>? ApprovedCleanupChanges { get; init; }
}
