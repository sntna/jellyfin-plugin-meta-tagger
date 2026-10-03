namespace Jellyfin.Plugin.MetaTagger;

public sealed class MetaTaggerRunSummary
{
    public Guid RunId { get; set; } = Guid.NewGuid();

    public string Invocation { get; set; } = "Unknown";

    public string Outcome { get; set; } = "Unknown";

    public string? ConfigurationRevision { get; set; }

    public DateTimeOffset? LastRunUtc { get; set; }

    public string RunMode { get; set; } = string.Empty;

    public int ItemsScanned { get; set; }

    public int ItemsProcessed { get; set; }

    public int ItemsSkippedBaseline { get; set; }

    public int ItemsPreviewedBaseline { get; set; }

    public int ItemsSkippedEligibilityUnavailable { get; set; }

    public string BackfillAuthorization { get; set; } = "None";

    public string[] AuthorizedItemTypes { get; set; } = [];

    public string? AuthorizedItemId { get; set; }

    public string BaselineStatus { get; set; } = "NotCaptured";

    public int? BaselineItemCount { get; set; }

    public long EligibilityRevision { get; set; }

    public int ItemsSkippedUnchanged { get; set; }

    public int ItemsSkippedManual { get; set; }

    public int ItemsSkippedLocked { get; set; }

    public int ItemsChanged { get; set; }

    public int TagsAdded { get; set; }

    public int TagsRemoved { get; set; }

    public int WritesApplied { get; set; }

    public int EstimatedWrites { get; set; }

    public int StaleTagsPreviewed { get; set; }

    public int LegacyTagsKept { get; set; }

    public int LegacyTagsClaimed { get; set; }

    public int Failures { get; set; }

    public bool PreviewOnly { get; set; }

    public bool BudgetLimitReached { get; set; }

    public string? BudgetLimitReason { get; set; }

    public int ItemsRemaining { get; set; }

    public int LedgerEntriesPruned { get; set; }

    public int PreviewChangesExported { get; set; }

    public string? PreviewChangesExportPath { get; set; }

    public string? PreviewChangesChecksum { get; set; }

    public Dictionary<string, int> TagsAddedBySource { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public double ElapsedMilliseconds { get; set; }
}
