using System.Globalization;
using Jellyfin.Plugin.MetaTagger.Configuration;

namespace Jellyfin.Plugin.MetaTagger;

public sealed class MetadataTagService
{
    public IReadOnlyCollection<string> GenerateTags(MetadataTagInput input, PluginConfiguration configuration)
    {
        return ExplainTags(input, configuration).Select(source => source.Tag).ToArray();
    }

    public IReadOnlyCollection<MetaTaggerTagSource> ExplainTags(MetadataTagInput input, PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.IsEnabled)
        {
            return [];
        }

        var tags = new SortedDictionary<string, MetaTaggerTagSource>(StringComparer.OrdinalIgnoreCase);
        if (configuration.EnableGenres)
        {
            AddValues(tags, configuration, "genre", input.Genres);
        }

        if (configuration.EnableParentalRating)
        {
            AddValues(tags, configuration, "rating", input.ParentalRatings);
        }

        if (configuration.EnableExistingTagsAsKeywords)
        {
            AddValues(tags, configuration, "keyword", KeywordTagSelector.Select(input.KeywordSourceTags, configuration));
        }

        if (configuration.EnableStudios)
        {
            AddValues(tags, configuration, "studio", input.Studios);
        }

        if (configuration.EnableProductionCountries)
        {
            AddValues(tags, configuration, "country", input.ProductionCountries);
        }

        if (configuration.EnableProviderIds)
        {
            AddValues(tags, configuration, "provider", input.ProviderIdSources
                .Where(provider => !string.IsNullOrWhiteSpace(provider.Value))
                .Select(provider => provider.Name));
        }

        if (configuration.EnableProductionYear)
        {
            AddValues(tags, configuration, "year", input.ProductionYears
                .Select(year => year.ToString(CultureInfo.InvariantCulture)));
        }

        if (configuration.EnableAudioLanguages)
        {
            AddValues(tags, configuration, "audio-language", RecordedLanguages(input.AudioLanguages));
        }

        if (configuration.EnableSubtitleLanguages)
        {
            AddValues(tags, configuration, "subtitle-language", RecordedLanguages(input.SubtitleLanguages));
        }

        return tags.Values.ToArray();
    }

    internal static IEnumerable<string> RecordedLanguages(IEnumerable<string> values)
    {
        return values.Where(value => !string.IsNullOrWhiteSpace(value)
            && !string.Equals(value.Trim(), "und", StringComparison.OrdinalIgnoreCase));
    }

    private static void AddValues(
        IDictionary<string, MetaTaggerTagSource> tags,
        PluginConfiguration configuration,
        string source,
        IEnumerable<string?> values)
    {
        foreach (var value in values)
        {
            var normalized = TagNormalizer.NormalizeValue(value);
            if (normalized.Length > 0)
            {
                var tag = TagFormat.GeneratedTag(configuration, source, normalized);
                if (!tags.TryGetValue(tag, out var explanation))
                {
                    explanation = new MetaTaggerTagSource { Tag = tag, Source = source };
                    tags.Add(tag, explanation);
                }

                if (!explanation.Values.Contains(value!, StringComparer.Ordinal))
                {
                    explanation.Values.Add(value!);
                }
            }
        }
    }
}

public sealed class MetaTaggerTagSource
{
    public string Tag { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public List<string> Values { get; init; } = [];
}
