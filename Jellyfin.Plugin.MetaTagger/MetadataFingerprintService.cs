using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.MetaTagger.Configuration;

namespace Jellyfin.Plugin.MetaTagger;

public sealed class MetadataFingerprintService
{
    private const string NormalizerVersion = "1";

    public string CreateFingerprint(MetadataTagInput input, PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(configuration);

        var payload = new
        {
            Source = SourceMetadata(input, configuration),
            configuration.EnableGenres,
            configuration.EnableParentalRating,
            configuration.EnableExistingTagsAsKeywords,
            configuration.EnableStudios,
            configuration.EnableProductionCountries,
            configuration.EnableProviderIds,
            configuration.EnableProductionYear,
            KeywordControls = configuration.EnableExistingTagsAsKeywords
                ? new
                {
                    MaxKeywordTagsPerItem = Math.Max(0, configuration.MaxKeywordTagsPerItem),
                    ExcludedKeywordPrefixes = PluginConfigurationValidator.GetExcludedKeywordPrefixes(configuration)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                }
                : null,
            GeneratedTagNamespace = TagFormat.GeneratedNamespace(configuration),
            ManualTagNamespace = TagFormat.ManualNamespace(configuration),
            TagSeparator = TagFormat.Separator(configuration),
            NormalizerVersion
        };

        // Preserve existing fingerprints when the optional track sources remain off.
        var json = configuration.EnableAudioLanguages || configuration.EnableSubtitleLanguages
            ? JsonSerializer.Serialize(new
            {
                Metadata = payload,
                configuration.EnableAudioLanguages,
                configuration.EnableSubtitleLanguages,
                AudioLanguages = configuration.EnableAudioLanguages ? Languages(input.AudioLanguages) : [],
                SubtitleLanguages = configuration.EnableSubtitleLanguages ? Languages(input.SubtitleLanguages) : []
            })
            : JsonSerializer.Serialize(payload);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return "sha256:" + Convert.ToHexString(hash).ToLower(CultureInfo.InvariantCulture);
    }

    private static object SourceMetadata(MetadataTagInput input, PluginConfiguration configuration)
    {
        return new
        {
            Genres = configuration.EnableGenres ? Sorted(input.Genres) : [],
            ParentalRatings = configuration.EnableParentalRating ? Sorted(input.ParentalRatings) : [],
            ExistingKeywordTags = configuration.EnableExistingTagsAsKeywords ? KeywordTags(input, configuration) : [],
            Studios = configuration.EnableStudios ? Sorted(input.Studios) : [],
            ProductionCountries = configuration.EnableProductionCountries ? Sorted(input.ProductionCountries) : [],
            ProviderIds = configuration.EnableProviderIds ? ProviderIds(input) : [],
            ProductionYears = configuration.EnableProductionYear
                ? input.ProductionYears.Order().ToArray()
                : []
        };
    }

    private static object[] ProviderIds(MetadataTagInput input)
    {
        return input.ProviderIdSources
            .OrderBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(provider => provider.Value, StringComparer.OrdinalIgnoreCase)
            .Select(provider => new { provider.Name, provider.Value })
            .ToArray();
    }

    private static string[] Languages(IEnumerable<string> values)
    {
        return MetadataTagService.RecordedLanguages(values)
            .Select(TagNormalizer.NormalizeValue)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] KeywordTags(MetadataTagInput input, PluginConfiguration configuration)
    {
        return KeywordTagSelector.Select(input.KeywordSourceTags, configuration)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] Sorted(IEnumerable<string?> values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
