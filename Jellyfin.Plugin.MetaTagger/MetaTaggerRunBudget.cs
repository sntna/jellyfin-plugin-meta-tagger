using Jellyfin.Plugin.MetaTagger.Configuration;

namespace Jellyfin.Plugin.MetaTagger;

public sealed class MetaTaggerRunBudget
{
    private readonly PluginConfiguration _configuration;
    private readonly DateTimeOffset _startedUtc;

    public MetaTaggerRunBudget(PluginConfiguration configuration, DateTimeOffset startedUtc)
    {
        _configuration = PluginConfigurationValidator.Sanitize(configuration);
        _startedUtc = startedUtc;
        WriteDelay = TimeSpan.FromMilliseconds(_configuration.WriteDelayMilliseconds);
    }

    public TimeSpan WriteDelay { get; }

    public bool ShouldStopBeforeItem(MetaTaggerRunSummary summary, DateTimeOffset nowUtc, out string reason)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (_configuration.MaxItemsPerRun > 0 && summary.ItemsScanned >= _configuration.MaxItemsPerRun)
        {
            reason = "max-items";
            return true;
        }

        if (_configuration.MaxRunMinutes > 0 && nowUtc - _startedUtc >= TimeSpan.FromMinutes(_configuration.MaxRunMinutes))
        {
            reason = "max-run-minutes";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    public bool CanWrite(MetaTaggerRunSummary summary, out string reason)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (_configuration.MaxWritesPerRun > 0 && summary.WritesApplied >= _configuration.MaxWritesPerRun)
        {
            reason = "max-writes";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
