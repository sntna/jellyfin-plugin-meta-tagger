using Jellyfin.Plugin.MetaTagger.Configuration;
using MediaBrowser.Common.Extensions;

namespace Jellyfin.Plugin.MetaTagger;

public sealed partial class Plugin
{
    private readonly Lock _configurationGate = new();
    private InstallationState _installation = null!;

    internal InstallationState SaveGenerationState(Guid installationId, GenerationState generation)
    {
        lock (_configurationGate)
        {
            if (_installation.InstallationId != installationId || _installation.Origin == InstallationOrigin.Uncertain
                || !generation.IsValid)
            {
                throw new InvalidOperationException("Generation authorization is unavailable for this installation.");
            }
            var installation = _installation.Copy();
            installation.Generation = generation.Copy();
            var configuration = PluginConfigurationValidator.Sanitize(Configuration);
            configuration.Installation = installation.Copy();
            SaveConfigurationAtomically(configuration);
            _installation = installation;
            Configuration.Installation = installation.Copy();
            return installation.Copy();
        }
    }

    private PluginConfiguration PrepareConfiguration(PluginConfiguration configuration)
    {
        var sanitized = PluginConfigurationValidator.Sanitize(configuration);
        sanitized.Installation = _installation.Copy();
        return sanitized;
    }

    private void InitializeConfiguration()
    {
        PluginConfiguration configuration;
        var origin = InstallationOrigin.Established;
        try
        {
            configuration = (PluginConfiguration)XmlSerializer.DeserializeFromFile(
                typeof(PluginConfiguration), ConfigurationFilePath);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            configuration = new PluginConfiguration();
            origin = HasPriorInstallationState() ? InstallationOrigin.Uncertain : InstallationOrigin.Fresh;
        }

        if (configuration.Installation is { } installation)
        {
            ValidateInstallation(installation);
            _installation = installation.Copy();
        }
        else
        {
            var includedTypes = new Dictionary<string, bool>
            {
                ["Movie"] = configuration.IncludeMovies,
                ["Series"] = configuration.IncludeSeries,
                ["Episode"] = configuration.IncludeEpisodes,
                ["Video"] = configuration.IncludeVideos
            };
            _installation = new InstallationState
            {
                InstallationId = Guid.NewGuid(),
                MigrationVersion = origin == InstallationOrigin.Uncertain ? 0 : 1,
                Origin = origin,
                PriorGenerationEligibility = origin == InstallationOrigin.Established
                    ? new GenerationEligibility
                    {
                        IncludedItemTypes = includedTypes.Where(type => type.Value).Select(type => type.Key).ToArray(),
                        ConfiguredApply = configuration.IsEnabled && !configuration.PreviewOnly,
                        ApplyTask = configuration.IsEnabled,
                        PostScanApply = configuration.IsEnabled && !configuration.PreviewOnly && configuration.RunAfterLibraryScan
                    }
                    : origin == InstallationOrigin.Fresh ? new GenerationEligibility
                    {
                        IncludedItemTypes = [], ConfiguredApply = false, ApplyTask = false, PostScanApply = false
                    } : null
            };
            // Persist identity and eligibility together, before exposing this installation to generation.
            SaveConfiguration(configuration);
        }

        configuration.Installation = _installation.Copy();
        Configuration = configuration;
    }

    private static void ValidateInstallation(InstallationState installation)
    {
        var eligibility = installation.PriorGenerationEligibility;
        var valid = installation.InstallationId != Guid.Empty && (installation.Origin switch
        {
            InstallationOrigin.Uncertain => installation.MigrationVersion == 0 && eligibility is null,
            InstallationOrigin.Fresh => installation.MigrationVersion == 1 && eligibility is
                { ConfiguredApply: false, ApplyTask: false, PostScanApply: false, IncludedItemTypes.Length: 0 },
            InstallationOrigin.Established => installation.MigrationVersion == 1
                && eligibility is { ConfiguredApply: not null, ApplyTask: not null, PostScanApply: not null, IncludedItemTypes: { } types }
                && types.All(type => type is "Movie" or "Series" or "Episode" or "Video")
                && (eligibility.ConfiguredApply != true || eligibility.ApplyTask == true)
                && (eligibility.PostScanApply != true || eligibility.ConfiguredApply == true),
            _ => false
        });
        if (!valid || installation.Generation is { IsValid: false })
        {
            throw new InvalidDataException("The installation migration record is incomplete or unsupported. Restore the plugin configuration before generating tags.");
        }
    }

    private bool HasPriorInstallationState()
    {
        // Jellyfin owns these files and derives their IDs from the task type names.
        Type[] taskTypes = [typeof(ScheduledTagTask), typeof(ApplyMetadataTagTask), typeof(PreviewMetadataTagTask),
            typeof(ForceFullMetadataTagScanTask), typeof(RebuildMetadataTagLedgerTask)];
        var taskFiles = taskTypes.Select(type => type.FullName!.GetMD5() + ".js").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in new[] { ApplicationPaths.ConfigurationDirectoryPath, ApplicationPaths.DataPath })
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(Path.Combine(directory, "ScheduledTasks"))
                    .Any(path => taskFiles.Contains(Path.GetFileName(path))))
                {
                    return true;
                }
            }
            catch (DirectoryNotFoundException) { }
        }

        try
        {
            if (Directory.EnumerateFileSystemEntries(ApplicationPaths.PluginConfigurationsPath, ConfigurationFileName + ".*").Any())
            {
                return true;
            }
        }
        catch (DirectoryNotFoundException) { }

        var assemblyName = Path.GetFileNameWithoutExtension(AssemblyFilePath);
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(ApplicationPaths.PluginsPath)
                .Where(path => Path.GetFileName(path).Equals(assemblyName, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path).StartsWith(assemblyName + "_", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path).Equals(Name, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path).StartsWith(Name + "_", StringComparison.OrdinalIgnoreCase)))
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any(path => Path.GetFileName(path) is
                    "meta-tagger-state.json" or "meta-tagger-state.json.bak" or "last-run-summary.json"
                    or "last-preview-changes.json" or "run-history.json" or "runs"))
                {
                    return true;
                }
            }
        }
        catch (DirectoryNotFoundException) { }
        return false;
    }

    private void SaveConfigurationAtomically(PluginConfiguration configuration)
    {
        var path = ConfigurationFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            XmlSerializer.SerializeToFile(configuration, tempPath);
            using (var stream = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) { File.Delete(tempPath); }
        }
    }
}
