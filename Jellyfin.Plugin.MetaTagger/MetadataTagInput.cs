namespace Jellyfin.Plugin.MetaTagger;

public sealed class MetadataTagInput
{
    public string ItemId { get; init; } = string.Empty;

    public string? ItemPath { get; init; }

    public string? ItemType { get; init; }

    public IReadOnlyCollection<string> ExistingTags { get; init; } = [];

    public IReadOnlyCollection<string> KeywordSourceTags { get; init; } = [];

    public IReadOnlyCollection<string> Genres { get; init; } = [];

    public IReadOnlyCollection<string> ParentalRatings { get; init; } = [];

    public IReadOnlyCollection<string> Studios { get; init; } = [];

    public IReadOnlyCollection<string> ProductionCountries { get; init; } = [];

    public IReadOnlyCollection<MetadataProviderId> ProviderIdSources { get; init; } = [];

    public IReadOnlyCollection<int> ProductionYears { get; init; } = [];

    public IReadOnlyCollection<string> AudioLanguages { get; init; } = [];

    public IReadOnlyCollection<string> SubtitleLanguages { get; init; } = [];
}
