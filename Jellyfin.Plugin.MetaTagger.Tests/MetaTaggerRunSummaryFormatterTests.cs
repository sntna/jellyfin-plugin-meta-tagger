using Jellyfin.Plugin.MetaTagger;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed class MetaTaggerRunSummaryFormatterTests
{
    [Fact]
    public void Format_IncludesJellyfinLockedSkipCount()
    {
        var summary = new MetaTaggerRunSummary { ItemsSkippedLocked = 2 };

        var formatted = MetaTaggerRunSummaryFormatter.Format(summary);

        Assert.Contains("items skipped: Jellyfin locks 2", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_IncludesCoreCountsAndBudgetStatus()
    {
        var summary = new MetaTaggerRunSummary
        {
            LastRunUtc = new DateTimeOffset(2026, 6, 29, 12, 30, 0, TimeSpan.Zero),
            RunMode = "Incremental",
            ItemsScanned = 10,
            ItemsChanged = 3,
            TagsAdded = 5,
            TagsRemoved = 1,
            EstimatedWrites = 3,
            WritesApplied = 0,
            PreviewOnly = true,
            BudgetLimitReached = true,
            BudgetLimitReason = "max-items",
            PreviewChangesExportPath = "/tmp/last-preview-changes.json"
        };

        var formatted = MetaTaggerRunSummaryFormatter.Format(summary);

        Assert.Contains("2026-06-29 12:30 UTC", formatted, StringComparison.Ordinal);
        Assert.Contains("New or changed items", formatted, StringComparison.Ordinal);
        Assert.Contains("items checked 10", formatted, StringComparison.Ordinal);
        Assert.Contains("items with tag differences 3", formatted, StringComparison.Ordinal);
        Assert.Contains("estimated item updates 3", formatted, StringComparison.Ordinal);
        Assert.Contains("run limit reached: maximum items checked", formatted, StringComparison.Ordinal);
        Assert.Contains("last-preview-changes.json", formatted, StringComparison.Ordinal);
    }
}
