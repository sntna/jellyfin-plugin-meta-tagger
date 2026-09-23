using Jellyfin.Plugin.MetaTagger.Configuration;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.MetaTagger;

public abstract class MetaTaggerScheduledTaskBase : IScheduledTask
{
    private readonly MetaTaggerRunner _runner;

    protected MetaTaggerScheduledTaskBase(MetaTaggerRunner runner)
    {
        _runner = runner;
    }

    public abstract string Name { get; }

    public abstract string Key { get; }

    public abstract string Description { get; }

    public string Category => "Library";

    private protected MetaTaggerRunner Runner => _runner;

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return ExecuteRunnerAsync(progress, cancellationToken);
    }

    public virtual IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return [];
    }

    protected virtual Task ExecuteRunnerAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var options = CreateOptions();
        if (options is not null) { options.Invocation = Key; }
        return _runner.RunAsync(progress, cancellationToken, options);
    }

    protected abstract MetaTaggerRunOptions? CreateOptions();

    protected static MetaTaggerRunOptions ConfiguredOptions(bool previewOnly, MetadataTagRunMode? runMode = null, bool force = false)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        return new MetaTaggerRunOptions
        {
            RunMode = runMode ?? configuration.DefaultRunMode,
            PreviewOnly = previewOnly,
            Force = force
        };
    }
}
