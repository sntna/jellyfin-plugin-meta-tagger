using Jellyfin.Plugin.MetaTagger.Configuration;

namespace Jellyfin.Plugin.MetaTagger;

internal static class TagFormat
{
    public static string GeneratedNamespace(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return PluginConfigurationValidator.Validate(configuration).EffectiveGeneratedNamespace;
    }

    public static string ManualNamespace(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return PluginConfigurationValidator.Validate(configuration).EffectiveManualNamespace;
    }

    public static string Separator(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return PluginConfigurationValidator.Validate(configuration).EffectiveSeparator;
    }

    public static string GeneratedTag(PluginConfiguration configuration, string source, string normalizedValue)
    {
        var separator = Separator(configuration);
        return $"{GeneratedNamespace(configuration)}{source}{separator}{normalizedValue}";
    }

    public static string ManualControlTag(PluginConfiguration configuration, string name)
    {
        var separator = Separator(configuration);
        return $"{ManualNamespace(configuration)}tagger{separator}{name}";
    }

    public static bool IsGenerated(string tag, PluginConfiguration configuration)
    {
        return IsPrefixed(tag, GeneratedNamespace(configuration));
    }

    public static bool IsManual(string tag, PluginConfiguration configuration)
    {
        return IsPrefixed(tag, ManualNamespace(configuration));
    }

    public static string? TryGetGeneratedSource(string tag, PluginConfiguration configuration)
    {
        var generatedNamespace = GeneratedNamespace(configuration);
        if (!IsPrefixed(tag, generatedNamespace))
        {
            return null;
        }

        var separator = Separator(configuration);
        var remainder = tag[generatedNamespace.Length..];
        var separatorIndex = remainder.IndexOf(separator, StringComparison.Ordinal);
        return separatorIndex <= 0 ? null : remainder[..separatorIndex];
    }

    private static bool IsPrefixed(string tag, string prefix)
    {
        return !string.IsNullOrWhiteSpace(tag)
            && !string.IsNullOrWhiteSpace(prefix)
            && tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
