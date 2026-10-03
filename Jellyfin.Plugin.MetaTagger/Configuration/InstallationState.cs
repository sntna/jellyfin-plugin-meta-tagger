namespace Jellyfin.Plugin.MetaTagger.Configuration;

public enum InstallationOrigin
{
    Uncertain,
    Fresh,
    Established
}

public sealed class InstallationState
{
    public Guid InstallationId { get; set; }

    public int MigrationVersion { get; set; }

    public InstallationOrigin Origin { get; set; }

    public GenerationEligibility? PriorGenerationEligibility { get; set; }

    public GenerationState? Generation { get; set; }

    internal InstallationState Copy() => new()
    {
        InstallationId = InstallationId,
        MigrationVersion = MigrationVersion,
        Origin = Origin,
        PriorGenerationEligibility = PriorGenerationEligibility?.Copy(),
        Generation = Generation?.Copy()
    };
}

public sealed class GenerationState
{
    public long Revision { get; set; }
    public bool BaselineComplete { get; set; }
    public string[]? BaselineItemIds { get; set; }
    public int? BaselineItemCount { get; set; }
    public string[]? AuthorizedItemTypes { get; set; }
    public string[]? AuthorizedItemIds { get; set; }

    internal bool IsValid => Revision > 0
        && (!BaselineComplete || BaselineItemIds is not null && BaselineItemCount == BaselineItemIds.Length)
        && (BaselineItemIds ?? []).All(id => Guid.TryParseExact(id, "N", out _))
        && AuthorizedItemTypes is not null
        && AuthorizedItemTypes.All(type => type is "Movie" or "Series" or "Episode" or "Video")
        && AuthorizedItemIds is not null
        && AuthorizedItemIds.All(id => Guid.TryParseExact(id, "N", out _));

    internal GenerationState Copy() => new()
    {
        Revision = Revision,
        BaselineComplete = BaselineComplete,
        BaselineItemIds = BaselineItemIds?.ToArray(),
        BaselineItemCount = BaselineItemCount,
        AuthorizedItemTypes = AuthorizedItemTypes?.ToArray(),
        AuthorizedItemIds = AuthorizedItemIds?.ToArray()
    };
}

public sealed class GenerationEligibility
{
    public string[]? IncludedItemTypes { get; set; }

    public bool? ConfiguredApply { get; set; }

    public bool? ApplyTask { get; set; }

    public bool? PostScanApply { get; set; }

    internal GenerationEligibility Copy() => new()
    {
        IncludedItemTypes = IncludedItemTypes?.ToArray(),
        ConfiguredApply = ConfiguredApply,
        ApplyTask = ApplyTask,
        PostScanApply = PostScanApply
    };
}
