using System.Globalization;
using Jellyfin.Plugin.MetaTagger.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MetaTagger;

public sealed partial class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static readonly Guid PluginId = Guid.Parse("4851185f-7284-4cad-9eeb-2c73576bb214");

    public const string PluginVersion = "0.1.0";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        InitializeConfiguration();
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Meta Tagger";

    public override Guid Id => PluginId;

    public override void SaveConfiguration(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lock (_configurationGate)
        {
            var sanitized = PrepareConfiguration(configuration);
            SaveConfigurationAtomically(sanitized);
        }
    }

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lock (_configurationGate)
        {
            var sanitized = PrepareConfiguration((PluginConfiguration)configuration);
            sanitized.ConfigurationRevision = Guid.NewGuid().ToString("N");
            SaveConfigurationAtomically(sanitized);
            Configuration = sanitized;
            ConfigurationChanged?.Invoke(this, sanitized);
        }
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EnableInMainMenu = true,
                MenuIcon = "local_offer",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            }
        ];
    }
}
