using Jellyfin.Plugin.MetaTagger;
using Jellyfin.Plugin.MetaTagger.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed class MetaTaggerRunBudgetTests
{
    private static readonly DateTimeOffset Started = new(2026, 6, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ShouldStopBeforeItem_StopsAtMaxItems()
    {
        var budget = new MetaTaggerRunBudget(
            new PluginConfiguration { MaxItemsPerRun = 2 },
            Started);
        var summary = new MetaTaggerRunSummary { ItemsScanned = 2 };

        var shouldStop = budget.ShouldStopBeforeItem(summary, Started, out var reason);

        Assert.True(shouldStop);
        Assert.Equal("max-items", reason);
    }

    [Fact]
    public void ShouldStopBeforeItem_StopsAtRunTimeLimit()
    {
        var budget = new MetaTaggerRunBudget(
            new PluginConfiguration { MaxRunMinutes = 5 },
            Started);
        var summary = new MetaTaggerRunSummary();

        var shouldStop = budget.ShouldStopBeforeItem(summary, Started.AddMinutes(5), out var reason);

        Assert.True(shouldStop);
        Assert.Equal("max-run-minutes", reason);
    }

    [Fact]
    public void CanWrite_StopsAtMaxWrites()
    {
        var budget = new MetaTaggerRunBudget(
            new PluginConfiguration { MaxWritesPerRun = 1 },
            Started);
        var summary = new MetaTaggerRunSummary { WritesApplied = 1 };

        var canWrite = budget.CanWrite(summary, out var reason);

        Assert.False(canWrite);
        Assert.Equal("max-writes", reason);
    }

    [Fact]
    public void WriteDelay_UsesConfiguredDelay()
    {
        var budget = new MetaTaggerRunBudget(
            new PluginConfiguration { WriteDelayMilliseconds = 250 },
            Started);

        Assert.Equal(TimeSpan.FromMilliseconds(250), budget.WriteDelay);
    }
}
