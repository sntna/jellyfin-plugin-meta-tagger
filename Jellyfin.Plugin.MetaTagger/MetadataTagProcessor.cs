using Jellyfin.Plugin.MetaTagger.Configuration;

namespace Jellyfin.Plugin.MetaTagger;

public sealed class MetadataTagProcessor
{
    private readonly MetadataTagService _tagService;
    private readonly MetadataFingerprintService _fingerprintService;
    private readonly TagMergeService _mergeService;

    public MetadataTagProcessor(
        MetadataTagService tagService,
        MetadataFingerprintService fingerprintService,
        TagMergeService mergeService)
    {
        _tagService = tagService;
        _fingerprintService = fingerprintService;
        _mergeService = mergeService;
    }

    public MetadataTagProcessResult Process(
        MetadataTagInput input,
        PluginConfiguration configuration,
        MetaTaggerState state,
        MetaTaggerRunOptions options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);

        if (HasManualTag(input, configuration, "skip")
            || HasManualTag(input, configuration, "lock"))
        {
            return Skipped(MetadataTagSkipReason.ManualSkip);
        }

        var generated = options.ClearGeneratedTags
            ? Array.Empty<string>()
            : _tagService.GenerateTags(input, configuration);
        state.Items.TryGetValue(input.ItemId, out var previous);
        var ownedTags = previous?.LastAppliedTags ?? [];
        var cleanupOwnership = options.ApprovedCleanupChanges is { } approved
            ? ownedTags.Where(tag => approved.TryGetValue(input.ItemId, out var change)
                && change.RemovedTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            : ownedTags;
        var merge = _mergeService.Merge(input.ExistingTags, generated, configuration, cleanupOwnership);
        var fingerprint = options.ClearGeneratedTags
            ? string.Empty
            : _fingerprintService.CreateFingerprint(input, configuration);
        var force = options.Force || HasManualTag(input, configuration, "force");

        if (!options.ClearGeneratedTags
            && options.RunMode == MetadataTagRunMode.Incremental
            && !force
            && previous?.LastMetadataFingerprint == fingerprint
            && !merge.HasChangesToApply
            && merge.PreviewRemovedTags.Count == 0
            && merge.LegacyTagsClaimed.Count == 0)
        {
            return new MetadataTagProcessResult(merge, false, null, MetadataTagSkipReason.Unchanged);
        }

        var rebuildOnly = options.RunMode == MetadataTagRunMode.RebuildTrackingLedger;
        var shouldWrite = !options.PreviewOnly && !rebuildOnly && merge.HasChangesToApply;
        var finalTags = shouldWrite ? merge.FinalTags : input.ExistingTags;
        var entry = CreateEntry(input, configuration, generated, ownedTags, finalTags, merge, fingerprint, now, shouldWrite);

        return new MetadataTagProcessResult(merge, shouldWrite, entry);
    }

    private static MetadataTagProcessResult Skipped(MetadataTagSkipReason reason)
    {
        return new MetadataTagProcessResult(new TagMergeResult([], [], [], []), false, null, reason);
    }

    private static MetaTaggerStateItem CreateEntry(
        MetadataTagInput input,
        PluginConfiguration configuration,
        IReadOnlyCollection<string> generated,
        IEnumerable<string> ownedTags,
        IEnumerable<string> finalTags,
        TagMergeResult merge,
        string fingerprint,
        DateTimeOffset now,
        bool wroteTags)
    {
        var finalSet = finalTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var appliedTags = generated
            .Where(finalSet.Contains)
            .Concat(merge.LegacyTagsClaimed.Where(finalSet.Contains))
            .Concat(ownedTags.Where(finalSet.Contains))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new MetaTaggerStateItem
        {
            ItemId = input.ItemId,
            ItemPath = input.ItemPath,
            ItemType = input.ItemType,
            LastMetadataFingerprint = fingerprint,
            LastGeneratedTags = generated.ToArray(),
            LastAppliedTags = appliedTags,
            LastRunUtc = now,
            LastWriteUtc = wroteTags ? now : null,
            PluginVersion = Plugin.PluginVersion,
            GeneratedTagPrefixAtWrite = TagFormat.GeneratedNamespace(configuration),
            ManualTagPrefixAtWrite = TagFormat.ManualNamespace(configuration),
            TagSeparatorAtWrite = TagFormat.Separator(configuration)
        };
    }

    private static bool HasManualTag(MetadataTagInput input, PluginConfiguration configuration, string name)
    {
        var tag = TagFormat.ManualControlTag(configuration, name);
        return input.ExistingTags.Contains(tag, StringComparer.OrdinalIgnoreCase);
    }
}
