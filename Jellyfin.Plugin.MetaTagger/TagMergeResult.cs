namespace Jellyfin.Plugin.MetaTagger;

public sealed class TagMergeResult
{
    public TagMergeResult(
        IReadOnlyCollection<string> finalTags,
        IReadOnlyCollection<string> addedTags,
        IReadOnlyCollection<string> removedTags,
        IReadOnlyCollection<string> previewRemovedTags,
        IReadOnlyCollection<string>? legacyTagsKept = null,
        IReadOnlyCollection<string>? legacyTagsClaimed = null)
    {
        FinalTags = finalTags;
        AddedTags = addedTags;
        RemovedTags = removedTags;
        PreviewRemovedTags = previewRemovedTags;
        LegacyTagsKept = legacyTagsKept ?? [];
        LegacyTagsClaimed = legacyTagsClaimed ?? [];
    }

    public IReadOnlyCollection<string> FinalTags { get; }

    public IReadOnlyCollection<string> AddedTags { get; }

    public IReadOnlyCollection<string> RemovedTags { get; }

    public IReadOnlyCollection<string> PreviewRemovedTags { get; }

    public IReadOnlyCollection<string> LegacyTagsKept { get; }

    public IReadOnlyCollection<string> LegacyTagsClaimed { get; }

    public bool HasChangesToApply => AddedTags.Count > 0 || RemovedTags.Count > 0;
}
