using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MetaTagger;

public sealed class MetaTaggerRunRecord
{
    public Guid RunId { get; init; } = Guid.NewGuid();
    public string Operation { get; init; } = "Preview";
    public string Invocation { get; init; } = "Dashboard";
    public string Scope { get; init; } = "Configured item types across all libraries";
    public string[] ItemTypes { get; init; } = [];
    public string? ConfigurationRevision { get; init; }
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedUtc { get; set; }
    public string Outcome { get; set; } = "Running";
    public MetaTaggerRunSummary Summary { get; set; } = new();
    public bool DetailsAvailable { get; set; } = true;
    public bool DetailsTruncated { get; set; }
    public List<MetaTaggerRunItem> Items { get; set; } = [];
    [JsonIgnore]
    private int DetailBytes { get; set; }

    public void AddItem(MetaTaggerRunItem item)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(item).Length;
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
    public string? Reason { get; init; }
    public IReadOnlyCollection<string> AddedTags { get; init; } = [];
    public IReadOnlyCollection<string> RemovedTags { get; init; } = [];
    public IReadOnlyCollection<string> PreviewRemovedTags { get; init; } = [];
}
