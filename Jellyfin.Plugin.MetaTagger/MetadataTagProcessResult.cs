namespace Jellyfin.Plugin.MetaTagger;

public sealed class MetadataTagProcessResult
{
    public MetadataTagProcessResult(
        TagMergeResult merge,
        bool shouldWriteTags,
        MetaTaggerStateItem? ledgerEntry,
        MetadataTagSkipReason? skipReason = null)
    {
        Merge = merge;
        ShouldWriteTags = shouldWriteTags;
        LedgerEntry = ledgerEntry;
        SkipReason = skipReason;
    }

    public TagMergeResult Merge { get; }

    public bool ShouldWriteTags { get; }

    public MetaTaggerStateItem? LedgerEntry { get; }

    public MetadataTagSkipReason? SkipReason { get; }
}
