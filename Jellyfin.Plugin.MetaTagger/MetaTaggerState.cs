namespace Jellyfin.Plugin.MetaTagger;

public sealed class MetaTaggerState
{
    public int SchemaVersion { get; set; } = 1;

    public string PluginVersion { get; set; } = Plugin.PluginVersion;

    public Dictionary<string, MetaTaggerStateItem> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, MetaTaggerRunCursor> RunCursors { get; set; } = new(StringComparer.Ordinal);

    public int PruneMissingItems(
        IEnumerable<string> existingItemIds,
        IEnumerable<string> includedItemTypes)
    {
        ArgumentNullException.ThrowIfNull(existingItemIds);
        ArgumentNullException.ThrowIfNull(includedItemTypes);

        var existing = existingItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var includedTypes = includedItemTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staleIds = Items
            .Where(item => !existing.Contains(item.Key)
                && item.Value?.ItemType is { } itemType
                && includedTypes.Contains(itemType))
            .Select(item => item.Key)
            .ToArray();

        foreach (var itemId in staleIds)
        {
            Items.Remove(itemId);
        }

        return staleIds.Length;
    }
}
