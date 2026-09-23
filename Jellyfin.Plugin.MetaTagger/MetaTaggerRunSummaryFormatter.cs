using System.Globalization;

namespace Jellyfin.Plugin.MetaTagger;

public static class MetaTaggerRunSummaryFormatter
{
    public static string Format(MetaTaggerRunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var timestamp = summary.LastRunUtc?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
            ?? "unknown time";
        var preview = summary.PreviewOnly ? "preview" : "apply";
        var runMode = summary.RunMode switch
        {
            "Incremental" => "New or changed items",
            "FullScan" => "All selected item types",
            "RebuildTrackingLedger" => "Rebuild plugin tag records",
            "ClearGeneratedTags" => "Remove plugin tags",
            "ItemGeneration" => "One item",
            _ => summary.RunMode
        };
        var limit = summary.BudgetLimitReason switch
        {
            "max-items" => "maximum items checked",
            "max-writes" => "maximum items updated",
            "max-run-minutes" => "maximum run time",
            _ => summary.BudgetLimitReason
        };
        var budget = summary.BudgetLimitReached ? $", run limit reached: {limit}" : string.Empty;
        var export = string.IsNullOrWhiteSpace(summary.PreviewChangesExportPath)
            ? string.Empty
            : $", export {Path.GetFileName(summary.PreviewChangesExportPath)}";

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}: {1} ({2}), items checked {3}, items skipped: Jellyfin locks {4}, items with tag differences {5}, tags to add {6}, tags to remove {7}, estimated item updates {8}, items updated {9}, failures {10}{11}{12}.",
            timestamp,
            runMode,
            preview,
            summary.ItemsScanned,
            summary.ItemsSkippedLocked,
            summary.ItemsChanged,
            summary.TagsAdded,
            summary.TagsRemoved,
            summary.EstimatedWrites,
            summary.WritesApplied,
            summary.Failures,
            budget,
            export);
    }
}
