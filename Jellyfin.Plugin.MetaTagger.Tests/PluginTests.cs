using System.Reflection;
using Jellyfin.Plugin.MetaTagger;
using Jellyfin.Plugin.MetaTagger.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed class PluginTests
{
    [Fact]
    public void GetPages_ListsSettingsInTheDashboardMenu()
    {
        var plugin = CreatePlugin(out _);

        var page = Assert.Single(plugin.GetPages());

        Assert.True(page.EnableInMainMenu);
        Assert.Equal("local_offer", page.MenuIcon);
    }

    [Fact]
    public void GetPages_SettingsPageShowsLatestPreviewResultsWithRefreshFeedback()
    {
        var plugin = CreatePlugin(out _);

        var markup = ReadSettingsPage(plugin);

        Assert.Contains("Latest preview results", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"PreviewFeedback\"", markup, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"PreviewChanges\"", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"RefreshPreviewButton\"", markup, StringComparison.Ordinal);
        Assert.Contains("function loadLatestPreview", markup, StringComparison.Ordinal);
        Assert.Contains("ApiClient.getUrl('MetaTagger/Preview')", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void GetPages_SettingsPageReportsUnsavedSavingSavedAndFailedStates()
    {
        var plugin = CreatePlugin(out _);

        var markup = ReadSettingsPage(plugin);

        Assert.Contains("id=\"SettingsFeedback\"", markup, StringComparison.Ordinal);
        Assert.Contains("You have unsaved settings.", markup, StringComparison.Ordinal);
        Assert.Contains("Saving settings…", markup, StringComparison.Ordinal);
        Assert.Contains("Settings saved. Select Preview tag changes", markup, StringComparison.Ordinal);
        Assert.Contains("Settings could not be saved.", markup, StringComparison.Ordinal);
        Assert.Contains("Settings could not be loaded.", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void GetPages_SettingsPageDistinguishesUnavailablePreviewDetailsFromNoChanges()
    {
        var plugin = CreatePlugin(out _);

        var markup = ReadSettingsPage(plugin);

        Assert.Contains("previewProperty(result, 'status', 'Status')", markup, StringComparison.Ordinal);
        Assert.Contains("Preview details are unavailable", markup, StringComparison.Ordinal);
        Assert.Contains("No tag differences found in the items checked. No tags changed.", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveConfiguration_StoresPrefixesWithoutTheConfiguredSeparator()
    {
        var plugin = CreatePlugin(out var serializer);
        var configuration = new PluginConfiguration
        {
            GeneratedTagPrefix = "meta:",
            TagSeparator = ":",
            ManualTagPrefix = "manual:"
        };
        var previousRevision = configuration.ConfigurationRevision;

        plugin.SaveConfiguration(configuration);

        var saved = Assert.IsType<PluginConfiguration>(serializer.LastSerializedValue);
        Assert.Equal("meta", saved.GeneratedTagPrefix);
        Assert.Equal("manual", saved.ManualTagPrefix);
        Assert.Equal(previousRevision, saved.ConfigurationRevision);
    }

    [Fact]
    public void UpdateConfiguration_StoresPrefixesWithoutTheConfiguredSeparator()
    {
        var plugin = CreatePlugin(out var serializer);
        var configuration = new PluginConfiguration
        {
            GeneratedTagPrefix = "meta:",
            TagSeparator = ":",
            ManualTagPrefix = "manual:"
        };
        var previousRevision = configuration.ConfigurationRevision;

        plugin.UpdateConfiguration(configuration);

        Assert.Equal("meta", plugin.Configuration.GeneratedTagPrefix);
        Assert.Equal("manual", plugin.Configuration.ManualTagPrefix);
        Assert.NotEqual(previousRevision, plugin.Configuration.ConfigurationRevision);
        var saved = Assert.IsType<PluginConfiguration>(serializer.LastSerializedValue);
        Assert.Equal("meta", saved.GeneratedTagPrefix);
        Assert.Equal("manual", saved.ManualTagPrefix);
        Assert.Equal(plugin.Configuration.ConfigurationRevision, saved.ConfigurationRevision);
    }

    private static Plugin CreatePlugin(out CapturingXmlSerializerProxy serializerProxy)
    {
        var applicationPaths = DispatchProxy.Create<IApplicationPaths, ApplicationPathsProxy>();
        var applicationPathsProxy = Assert.IsAssignableFrom<ApplicationPathsProxy>(applicationPaths);
        applicationPathsProxy.ApplicationPath = Path.GetTempPath();

        var serializer = DispatchProxy.Create<IXmlSerializer, CapturingXmlSerializerProxy>();
        serializerProxy = Assert.IsAssignableFrom<CapturingXmlSerializerProxy>(serializer);

        return new Plugin(applicationPaths, serializer);
    }

    private static string ReadSettingsPage(Plugin plugin)
    {
        var page = Assert.Single(plugin.GetPages());
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(page.EmbeddedResourcePath!);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public class ApplicationPathsProxy : DispatchProxy
    {
        public string ApplicationPath { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            return targetMethod.ReturnType == typeof(string) ? ApplicationPath : null;
        }
    }

    public class CapturingXmlSerializerProxy : DispatchProxy
    {
        public object? LastSerializedValue { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            if (targetMethod.Name == nameof(IXmlSerializer.SerializeToFile))
            {
                LastSerializedValue = args?[0];
                return null;
            }

            if (targetMethod.ReturnType == typeof(string))
            {
                return string.Empty;
            }

            if (targetMethod.ReturnType == typeof(object)
                && args is [{ } typeValue, ..]
                && typeValue is Type type)
            {
                return Activator.CreateInstance(type);
            }

            return null;
        }
    }
}
