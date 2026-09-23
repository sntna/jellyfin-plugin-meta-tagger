using Jellyfin.Plugin.MetaTagger.Configuration;

namespace Jellyfin.Plugin.MetaTagger;

public sealed class TagMergeService
{
    public TagMergeResult Merge(
        IEnumerable<string> existingTags,
        IEnumerable<string> generatedTags,
        PluginConfiguration configuration,
        IEnumerable<string>? ownedTags = null)
    {
        ArgumentNullException.ThrowIfNull(existingTags);
        ArgumentNullException.ThrowIfNull(generatedTags);
        ArgumentNullException.ThrowIfNull(configuration);

        var existing = DistinctPreservingOrder(existingTags);
        var generated = DistinctPreservingOrder(generatedTags);
        var generatedSet = generated.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ownedSet = (ownedTags ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentGeneratedExisting = existing
            .Where(tag => TagFormat.IsGenerated(tag, configuration))
            .Where(tag => !TagFormat.IsManual(tag, configuration))
            .ToArray();
        var managedExisting = existing
            .Where(tag => !TagFormat.IsManual(tag, configuration))
            .Where(tag => TagFormat.IsGenerated(tag, configuration) || ownedSet.Contains(tag))
            .ToArray();
        var legacyTags = currentGeneratedExisting
            .Where(tag => !ownedSet.Contains(tag))
            .ToArray();
        var staleManaged = managedExisting
            .Where(tag => ownedSet.Contains(tag) || configuration.ClaimExistingGeneratedTagsForCleanup)
            .Where(tag => !generatedSet.Contains(tag))
            .ToArray();

        var staleSet = staleManaged.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var final = new List<string>();

        foreach (var tag in existing)
        {
            if (configuration.StaleTagMode == StaleTagMode.Remove && staleSet.Contains(tag))
            {
                continue;
            }

            final.Add(tag);
        }

        foreach (var tag in generated)
        {
            if (!final.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                final.Add(tag);
            }
        }

        var finalSet = final.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingSet = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = final.Where(tag => !existingSet.Contains(tag)).ToArray();
        var removed = configuration.StaleTagMode == StaleTagMode.Remove
            ? existing.Where(tag => !finalSet.Contains(tag)).ToArray()
            : [];
        var previewRemoved = configuration.StaleTagMode == StaleTagMode.Preview ? staleManaged : [];
        var legacyClaimed = configuration.ClaimExistingGeneratedTagsForCleanup
            ? legacyTags
            : legacyTags.Where(generatedSet.Contains).ToArray();
        var legacyKept = legacyTags
            .Where(tag => !legacyClaimed.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        return new TagMergeResult(final, added, removed, previewRemoved, legacyKept, legacyClaimed);
    }

    private static IReadOnlyCollection<string> DistinctPreservingOrder(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();
            if (seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

}
