using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using Jellyfin.Plugin.MetaTagger;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed class PluginVersionContractTests
{
    private const string ExpectedDevelopmentVersion = "0.1.0";
    private const string ExpectedFourPartVersion = "0.1.0.0";
    private const string ExpectedTargetAbi = "12.0.0.0";

    [Fact]
    public void DevelopmentVersion_IsAlignedAcrossReleaseSurfaces()
    {
        Assert.Equal(ExpectedDevelopmentVersion, Plugin.PluginVersion);

        using var manifest = JsonDocument.Parse(ReadResource("VersionContract.Manifest"));
        var manifestVersion = manifest.RootElement[0].GetProperty("versions")[0];
        Assert.Equal(ExpectedFourPartVersion, manifestVersion.GetProperty("version").GetString());
        Assert.Equal(ExpectedTargetAbi, manifestVersion.GetProperty("targetAbi").GetString());

        var pluginAssembly = typeof(Plugin).Assembly;
        Assert.Equal(new Version(ExpectedFourPartVersion), pluginAssembly.GetName().Version);
        Assert.Equal(
            ExpectedFourPartVersion,
            pluginAssembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version);
        Assert.Equal(
            ExpectedDevelopmentVersion,
            pluginAssembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion
                .Split('+')[0]);
        Assert.Equal(
            ExpectedDevelopmentVersion,
            pluginAssembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "PackageVersion")
                .Value);
    }

    [Fact]
    public void PluginAssembly_TargetsJellyfin12Host()
    {
        var pluginAssembly = typeof(Plugin).Assembly;
        Assert.Equal(
            ".NETCoreApp,Version=v10.0",
            pluginAssembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);
        foreach (var name in new[] { "MediaBrowser.Controller", "MediaBrowser.Model" })
        {
            var reference = Assert.Single(pluginAssembly.GetReferencedAssemblies(), assembly => assembly.Name == name);
            Assert.Equal(new Version(ExpectedTargetAbi), reference.Version);
        }
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(PluginVersionContractTests).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
