namespace Jellyfin.Plugin.MetaTagger;

public sealed class MetaTaggerCleanupRequest
{
    public Guid? ItemId { get; init; }
}

public sealed class MetaTaggerCleanupApplyRequest
{
    public string Token { get; init; } = string.Empty;
}

public sealed class MetaTaggerCleanupItem
{
    public Guid ItemId { get; init; }

    public string? Name { get; init; }

    public string? ItemType { get; init; }

    public string? Path { get; init; }
}
