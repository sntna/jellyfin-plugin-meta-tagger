using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MetaTagger;

/// <summary>A source's result for one item and the settings used to inspect it.</summary>
public sealed class MetaTaggerSourceExplanation
{
    public string Source { get; init; } = string.Empty;
    public SourceExplanationStatus Status { get; init; }
    public string Reason { get; init; } = string.Empty;
    public IReadOnlyCollection<string> Tags { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<SourceExplanationStatus>))]
public enum SourceExplanationStatus
{
    Generated,
    Disabled,
    MissingData,
    NoItemTracks,
    NoRecordedLanguage,
    ExcludedItemType,
    Protected,
    LookupFailed,
    NotChecked
}
