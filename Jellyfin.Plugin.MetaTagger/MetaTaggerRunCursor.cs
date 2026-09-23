namespace Jellyfin.Plugin.MetaTagger;

public sealed class MetaTaggerRunCursor
{
    public string[] PendingItemIds { get; set; } = [];

    public int NextIndex { get; set; }
}
