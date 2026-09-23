using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaTagger;

public sealed partial class MetaTaggerRunner
{
    private MetaTaggerRunRecord CreateRunRecord(MetaTaggerRunSummary summary, string operation, string scope,
        string invocation = "Dashboard", string[]? itemTypes = null)
    {
        summary.Outcome = "Running";
        return new MetaTaggerRunRecord
        {
            RunId = summary.RunId, Operation = operation, Scope = scope, Invocation = invocation,
            ConfigurationRevision = summary.ConfigurationRevision, StartedUtc = summary.LastRunUtc ?? _clock.UtcNow,
            Summary = summary, ItemTypes = itemTypes ?? []
        };
    }

    private async Task PublishHistoryAsync(MetaTaggerRunRecord record, bool finished = false)
    {
        if (finished)
        {
            CompleteOutcome(record.Summary);
            record.Outcome = record.Summary.Outcome;
            record.EndedUtc = _clock.UtcNow;
        }

        try
        {
            await _stateStore.SaveRunAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // History is display data; the ownership checkpoint remains authoritative.
            _logger.LogError(exception, "Meta Tagger run history could not be published for {RunId}. Do not repeat writes based on missing history.", record.RunId);
        }
    }

    private static void CompleteOutcome(MetaTaggerRunSummary summary)
    {
        if (summary.Outcome == "Running")
        {
            summary.Outcome = summary.BudgetLimitReached ? "Budget limited" : summary.Failures > 0 ? "Partial failure" : "Completed";
        }
    }
}
