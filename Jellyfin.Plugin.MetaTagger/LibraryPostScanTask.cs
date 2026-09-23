using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MetaTagger;

public sealed class LibraryPostScanTask : ILibraryPostScanTask
{
    private readonly MetaTaggerRunner _runner;
    private readonly MetaTaggerStateStore _stateStore;
    private readonly IMetaTaggerClock _clock;

    public LibraryPostScanTask(
        MetaTaggerRunner runner,
        MetaTaggerStateStore stateStore,
        IMetaTaggerClock clock)
    {
        _runner = runner;
        _stateStore = stateStore;
        _clock = clock;
    }

    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var settings = _runner.GetPostScanSettings();
        if (!settings.RunAfterLibraryScan)
        {
            return;
        }

        if (settings.MinimumMinutesBetweenAutoRuns > 0)
        {
            var summary = await _stateStore.LoadSummaryAsync(cancellationToken).ConfigureAwait(false);
            if (summary?.LastRunUtc is { } lastRun
                && _clock.UtcNow - lastRun < TimeSpan.FromMinutes(settings.MinimumMinutesBetweenAutoRuns))
            {
                return;
            }
        }

        await _runner.RunPostScanAsync(progress, cancellationToken).ConfigureAwait(false);
    }
}
