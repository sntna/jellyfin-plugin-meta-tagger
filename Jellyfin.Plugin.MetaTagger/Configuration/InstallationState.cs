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

    internal InstallationState Copy() => new()
    {
        InstallationId = InstallationId,
        MigrationVersion = MigrationVersion,
        Origin = Origin,
        PriorGenerationEligibility = PriorGenerationEligibility?.Copy()
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
