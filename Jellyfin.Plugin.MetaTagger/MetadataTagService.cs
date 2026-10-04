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

    public IReadOnlyCollection<MetaTaggerSourceExplanation> ExplainSources(MetadataTagInput input, PluginConfiguration configuration)
    {
        var tags = ExplainTags(input, configuration);
        return SourceSettings(configuration).Select(source =>
        {
            var generated = tags.Where(tag => tag.Source == source.Name).Select(tag => tag.Tag).ToArray();
            var status = !configuration.IsEnabled || !source.Enabled ? SourceExplanationStatus.Disabled
                : generated.Length > 0 ? SourceExplanationStatus.Generated
                : SourceExplanationStatus.MissingData;
            if (status == SourceExplanationStatus.MissingData && source.Name is "audio-language" or "subtitle-language")
            {
                var count = source.Name == "audio-language" ? input.AudioTrackCount : input.SubtitleTrackCount;
                if (count > 0) { status = SourceExplanationStatus.NoRecordedLanguage; }
                else if (input.ItemType == "Series") { status = SourceExplanationStatus.NoItemTracks; }
            }
            return new MetaTaggerSourceExplanation { Source = source.Name, Status = status, Reason = Reason(status), Tags = generated };
        }).ToArray();
    }

    internal static IReadOnlyCollection<MetaTaggerSourceExplanation> UnavailableSources(
        SourceExplanationStatus status, string reason, PluginConfiguration? configuration = null)
    {
        return SourceSettings(configuration ?? new PluginConfiguration()).Select(source =>
        {
            var effective = status == SourceExplanationStatus.LookupFailed
                ? !source.Enabled ? SourceExplanationStatus.Disabled
                    : source.Name is "audio-language" or "subtitle-language" ? status : SourceExplanationStatus.NotChecked
                : status;
            return new MetaTaggerSourceExplanation
            {
                Source = source.Name, Status = effective,
                Reason = effective == status ? reason : Reason(effective)
            };
        }).ToArray();
    }

    private static (string Name, bool Enabled)[] SourceSettings(PluginConfiguration configuration) =>
    [
        ("genre", configuration.EnableGenres), ("rating", configuration.EnableParentalRating),
        ("keyword", configuration.EnableExistingTagsAsKeywords), ("studio", configuration.EnableStudios),
        ("country", configuration.EnableProductionCountries), ("provider", configuration.EnableProviderIds),
        ("year", configuration.EnableProductionYear), ("audio-language", configuration.EnableAudioLanguages),
        ("subtitle-language", configuration.EnableSubtitleLanguages)
    ];

    private static string Reason(SourceExplanationStatus status) => status switch
    {
        SourceExplanationStatus.Generated => "Generated tags from this item's metadata.",
        SourceExplanationStatus.Disabled => "This source is off in these settings.",
        SourceExplanationStatus.NoItemTracks => "This series has no tracks of its own. Episode languages are not combined onto the series.",
        SourceExplanationStatus.NoRecordedLanguage => "The tracks have no usable recorded language codes. Blank and undetermined codes do not produce tags.",
        SourceExplanationStatus.MissingData => "No usable values are recorded for this source.",
        _ => "This source was not checked because item processing could not complete."
    };

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
