using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MetaTagger;

public sealed class MetaTaggerRunRecord
{
    public Guid RunId { get; init; } = Guid.NewGuid();
    public string Operation { get; init; } = "Preview";
    public string Invocation { get; init; } = "Dashboard";
    public string Scope { get; init; } = "Configured item types across all libraries";
    public string[] ItemTypes { get; set; } = [];
    public int DetailVersion { get; init; }
    public Dictionary<string, string>? RecordedRules { get; set; }
    public string? DetailsUnavailableReason { get; set; }
    public long PublicationRevision { get; set; }
    public string? ConfigurationRevision { get; set; }
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedUtc { get; set; }
    public string Outcome { get; set; } = "Running";
    public string? FailureReason { get; set; }
    public MetaTaggerRunSummary Summary { get; set; } = new();
    public bool DetailsAvailable { get; set; } = true;
    public bool DetailsTruncated { get; set; }
    public List<MetaTaggerRunItem> Items { get; set; } = [];
    [JsonIgnore]
    private int DetailBytes { get; set; }

    public void AddItem(MetaTaggerRunItem item)
    {
        if (Items.Count == 0 && DetailBytes == 0)
        {
            DetailBytes = JsonSerializer.SerializeToUtf8Bytes(RecordedRules).Length + 2;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(item).Length + 1;
        if (Items.Count >= 1000 || DetailBytes + bytes > 5 * 1024 * 1024)
        {
            DetailsTruncated = true;
            return;
        }

        DetailBytes += bytes;
        Items.Add(item);
    }
}

public sealed class MetaTaggerRunItem
{
    public string ItemId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ItemType { get; init; } = string.Empty;
    public string Outcome { get; init; } = "Not checked";
    public string WriteOutcome { get; init; } = "Unavailable";
    public string OwnershipOutcome { get; init; } = "Unavailable";
    public IReadOnlyCollection<string> KeptTags { get; init; } = [];
    public string? Reason { get; init; }
    public string? GenerationEligibility { get; init; }
    public IReadOnlyCollection<MetaTaggerSourceExplanation> SourceExplanations { get; init; } = [];
    public IReadOnlyCollection<string> AddedTags { get; init; } = [];
    public IReadOnlyCollection<string> RemovedTags { get; init; } = [];
    public IReadOnlyCollection<string> PreviewRemovedTags { get; init; } = [];
}
