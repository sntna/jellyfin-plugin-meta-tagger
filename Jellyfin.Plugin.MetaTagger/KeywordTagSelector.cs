using Jellyfin.Plugin.MetaTagger.Configuration;

namespace Jellyfin.Plugin.MetaTagger;

internal static class KeywordTagSelector
{
    public static IReadOnlyCollection<string> Select(IEnumerable<string> existingTags, PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(existingTags);
        ArgumentNullException.ThrowIfNull(configuration);

        var excludedPrefixes = PluginConfigurationValidator.GetExcludedKeywordPrefixes(configuration);
        var limit = Math.Max(0, configuration.MaxKeywordTagsPerItem);
        var selected = existingTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Where(tag => !TagFormat.IsGenerated(tag, configuration))
            .Where(tag => !TagFormat.IsManual(tag, configuration))
            .Where(tag => !HasExcludedPrefix(tag, excludedPrefixes))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        if (limit > 0)
        {
            selected = selected.Take(limit);
        }

        return selected.ToArray();
    }

    private static bool HasExcludedPrefix(string tag, IReadOnlyCollection<string> excludedPrefixes)
    {
        return excludedPrefixes.Any(prefix => tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
