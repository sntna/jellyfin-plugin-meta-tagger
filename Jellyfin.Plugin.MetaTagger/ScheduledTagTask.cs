using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.MetaTagger;

public sealed class ScheduledTagTask : MetaTaggerScheduledTaskBase
{
    public ScheduledTagTask(MetaTaggerRunner runner)
        : base(runner)
    {
    }

    public override string Name => "Generate metadata tags";

    public override string Key => "MetaTaggerGenerateTags";

    public override string Description => "Checks selected item types across all libraries using saved settings. Previews changes when automatic runs use Preview; otherwise applies changes to later additions and authorized items. Existing baseline items need explicit Apply authorization. Respects locks, skip tags, and run limits.";

    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromDays(1).Ticks
            }
        ];
    }

    protected override Task ExecuteRunnerAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return Runner.RunDefaultScheduledAsync(progress, cancellationToken);
    }

    protected override MetaTaggerRunOptions? CreateOptions()
    {
        return null;
    }
}
