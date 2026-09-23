using System.Xml.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MetaTagger.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string ConfigurationRevision { get; set; } = Guid.NewGuid().ToString("N");

    public bool IsEnabled { get; set; } = true;

    public string GeneratedTagPrefix { get; set; } = "meta";

    public string TagSeparator { get; set; } = ":";

    public string ManualTagPrefix { get; set; } = "manual";

    public bool EnableGenres { get; set; } = true;

    [XmlElement("EnableOfficialRating")]
    public bool EnableParentalRating { get; set; } = true;

    public bool EnableExistingTagsAsKeywords { get; set; }

    public int MaxKeywordTagsPerItem { get; set; }

    public string ExcludedKeywordPrefixes { get; set; } = string.Empty;

    public bool EnableStudios { get; set; }

    public bool EnableProductionCountries { get; set; }

    public bool EnableProviderIds { get; set; }

    public bool EnableProductionYear { get; set; }

    public bool EnableAudioLanguages { get; set; } = true;

    public bool EnableSubtitleLanguages { get; set; }

    public bool IncludeParentSeriesMetadataOnEpisodes { get; set; }

    public bool IncludeMovies { get; set; } = true;

    public bool IncludeSeries { get; set; } = true;

    public bool IncludeEpisodes { get; set; }

    public bool IncludeVideos { get; set; }

    public bool RunAfterLibraryScan { get; set; }

    public int MinimumMinutesBetweenAutoRuns { get; set; } = 30;

    public int MaxItemsPerRun { get; set; }

    public int MaxWritesPerRun { get; set; }

    public int MaxRunMinutes { get; set; }

    public int WriteDelayMilliseconds { get; set; }

    public bool PreviewOnly { get; set; } = true;

    public bool QuietLogging { get; set; }

    public string LastRunSummaryText { get; set; } = "No runs recorded.";

    public MetadataTagRunMode DefaultRunMode { get; set; } = MetadataTagRunMode.Incremental;

    public bool ForceFullScanOnNextRun { get; set; }

    public bool RebuildTrackingLedgerOnNextRun { get; set; }

    public bool ClaimExistingGeneratedTagsForCleanup { get; set; }

    public bool ClaimExistingGeneratedTagsOnNextRun { get; set; }

    public StaleTagMode StaleTagMode { get; set; } = StaleTagMode.Keep;
}
