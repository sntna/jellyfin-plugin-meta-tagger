using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MetaTagger;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<MetadataTagService>();
        serviceCollection.AddSingleton<MetadataFingerprintService>();
        serviceCollection.AddSingleton<TagMergeService>();
        serviceCollection.AddSingleton<MetadataProjectionService>();
        serviceCollection.AddSingleton<MetadataTagProcessor>();
        serviceCollection.AddSingleton<MetaTaggerStateStore>();
        serviceCollection.AddSingleton<IMetaTaggerClock, MetaTaggerClock>();
        serviceCollection.AddSingleton<MetaTaggerRunner>();
        serviceCollection.AddSingleton<IScheduledTask, ScheduledTagTask>();
        serviceCollection.AddSingleton<IScheduledTask, PreviewMetadataTagTask>();
        serviceCollection.AddSingleton<IScheduledTask, ApplyMetadataTagTask>();
        serviceCollection.AddSingleton<IScheduledTask, ForceFullMetadataTagScanTask>();
        serviceCollection.AddSingleton<IScheduledTask, RebuildMetadataTagLedgerTask>();
        serviceCollection.AddSingleton<ILibraryPostScanTask, LibraryPostScanTask>();
    }
}
