using System.Globalization;
using Jellyfin.Plugin.MetaTagger.Configuration;

namespace Jellyfin.Plugin.MetaTagger;

// Display data only. Historical rules are never deserialized into an Apply request.
internal static class MetaTaggerRecordedRules
{
    internal static Dictionary<string, string> Capture(PluginConfiguration configuration) => new()
    {
        ["Tagging enabled"] = Flag(configuration.IsEnabled),
        ["Generated tag prefix"] = configuration.GeneratedTagPrefix,
        ["Tag separator"] = configuration.TagSeparator,
        ["Manual tag prefix"] = configuration.ManualTagPrefix,
        ["Genres"] = Flag(configuration.EnableGenres),
        ["Parental rating"] = Flag(configuration.EnableParentalRating),
        ["Existing tags as keywords"] = Flag(configuration.EnableExistingTagsAsKeywords),
        ["Keyword limit"] = Number(configuration.MaxKeywordTagsPerItem),
        ["Excluded keyword prefixes"] = configuration.ExcludedKeywordPrefixes,
        ["Studios"] = Flag(configuration.EnableStudios),
        ["Production countries"] = Flag(configuration.EnableProductionCountries),
        ["Metadata providers"] = Flag(configuration.EnableProviderIds),
        ["Production year"] = Flag(configuration.EnableProductionYear),
        ["Audio languages"] = Flag(configuration.EnableAudioLanguages),
        ["Subtitle languages"] = Flag(configuration.EnableSubtitleLanguages),
        ["Parent series metadata on episodes"] = Flag(configuration.IncludeParentSeriesMetadataOnEpisodes),
        ["Outdated tag policy"] = configuration.StaleTagMode.ToString(),
        ["Claim existing generated tags"] = Flag(configuration.ClaimExistingGeneratedTagsForCleanup),
        ["Item limit"] = Number(configuration.MaxItemsPerRun),
        ["Write limit"] = Number(configuration.MaxWritesPerRun),
        ["Time limit in minutes"] = Number(configuration.MaxRunMinutes),
        ["Write delay in milliseconds"] = Number(configuration.WriteDelayMilliseconds),
        ["Post-scan enabled"] = Flag(configuration.RunAfterLibraryScan),
        ["Post-scan cooldown in minutes"] = Number(configuration.MinimumMinutesBetweenAutoRuns)
    };

    private static string Flag(bool value) => value ? "On" : "Off";
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
