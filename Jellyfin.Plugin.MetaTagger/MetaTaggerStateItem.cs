namespace Jellyfin.Plugin.MetaTagger;

public sealed class MetaTaggerStateItem
{
    public string ItemId { get; set; } = string.Empty;

    public string? ItemPath { get; set; }

    public string? ItemType { get; set; }

    public string LastMetadataFingerprint { get; set; } = string.Empty;

    public string[] LastGeneratedTags { get; set; } = [];

    public string[] LastAppliedTags { get; set; } = [];

    public DateTimeOffset LastRunUtc { get; set; }

    public DateTimeOffset? LastWriteUtc { get; set; }

    public string PluginVersion { get; set; } = Plugin.PluginVersion;

    public string GeneratedTagPrefixAtWrite { get; set; } = "meta:";

    public string ManualTagPrefixAtWrite { get; set; } = "manual:";

    public string TagSeparatorAtWrite { get; set; } = ":";
}
