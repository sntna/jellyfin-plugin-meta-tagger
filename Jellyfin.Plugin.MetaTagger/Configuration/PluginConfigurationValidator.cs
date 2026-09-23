namespace Jellyfin.Plugin.MetaTagger.Configuration;

public static class PluginConfigurationValidator
{
    private const int MaxPrefixLength = 32;
    private const int MaxSeparatorLength = 8;
    private const string DefaultGeneratedPrefix = "meta";
    private const string DefaultManualPrefix = "manual";
    private const string DefaultSeparator = ":";

    public static PluginConfigurationValidationResult Validate(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var errors = new List<string>();
        var separator = NormalizeSeparator(configuration.TagSeparator, errors);
        var generatedPrefix = NormalizePrefix(
            configuration.GeneratedTagPrefix,
            "Generated tag prefix",
            DefaultGeneratedPrefix,
            separator,
            errors);
        var manualPrefix = NormalizePrefix(
            configuration.ManualTagPrefix,
            "Manual tag prefix",
            DefaultManualPrefix,
            separator,
            errors);

        var generatedNamespace = generatedPrefix + separator;
        var manualNamespace = manualPrefix + separator;
        if (string.Equals(generatedNamespace, manualNamespace, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("The generated and manual prefixes must be different.");
            generatedNamespace = DefaultGeneratedPrefix + separator;
            manualNamespace = DefaultManualPrefix + separator;
        }

        return new PluginConfigurationValidationResult(errors, generatedNamespace, manualNamespace, separator);
    }

    public static PluginConfiguration Sanitize(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var validation = Validate(configuration);
        var generatedPrefix = RemoveTrailingSeparator(validation.EffectiveGeneratedNamespace, validation.EffectiveSeparator);
        var manualPrefix = RemoveTrailingSeparator(validation.EffectiveManualNamespace, validation.EffectiveSeparator);

        return new PluginConfiguration
        {
            ConfigurationRevision = configuration.ConfigurationRevision,
            IsEnabled = configuration.IsEnabled,
            GeneratedTagPrefix = generatedPrefix,
            TagSeparator = validation.EffectiveSeparator,
            ManualTagPrefix = manualPrefix,
            EnableGenres = configuration.EnableGenres,
            EnableParentalRating = configuration.EnableParentalRating,
            EnableExistingTagsAsKeywords = configuration.EnableExistingTagsAsKeywords,
            MaxKeywordTagsPerItem = ClampToZero(configuration.MaxKeywordTagsPerItem),
            ExcludedKeywordPrefixes = configuration.ExcludedKeywordPrefixes ?? string.Empty,
            EnableStudios = configuration.EnableStudios,
            EnableProductionCountries = configuration.EnableProductionCountries,
            EnableProviderIds = configuration.EnableProviderIds,
            EnableProductionYear = configuration.EnableProductionYear,
            EnableAudioLanguages = configuration.EnableAudioLanguages,
            EnableSubtitleLanguages = configuration.EnableSubtitleLanguages,
            IncludeParentSeriesMetadataOnEpisodes = configuration.IncludeParentSeriesMetadataOnEpisodes,
            IncludeMovies = configuration.IncludeMovies,
            IncludeSeries = configuration.IncludeSeries,
            IncludeEpisodes = configuration.IncludeEpisodes,
            IncludeVideos = configuration.IncludeVideos,
            RunAfterLibraryScan = configuration.RunAfterLibraryScan,
            MinimumMinutesBetweenAutoRuns = ClampToZero(configuration.MinimumMinutesBetweenAutoRuns),
            MaxItemsPerRun = ClampToZero(configuration.MaxItemsPerRun),
            MaxWritesPerRun = ClampToZero(configuration.MaxWritesPerRun),
            MaxRunMinutes = ClampToZero(configuration.MaxRunMinutes),
            WriteDelayMilliseconds = ClampToZero(configuration.WriteDelayMilliseconds),
            PreviewOnly = configuration.PreviewOnly,
            QuietLogging = configuration.QuietLogging,
            LastRunSummaryText = configuration.LastRunSummaryText ?? "No runs recorded.",
            DefaultRunMode = configuration.DefaultRunMode,
            ForceFullScanOnNextRun = configuration.ForceFullScanOnNextRun,
            RebuildTrackingLedgerOnNextRun = configuration.RebuildTrackingLedgerOnNextRun,
            ClaimExistingGeneratedTagsForCleanup = configuration.ClaimExistingGeneratedTagsForCleanup,
            ClaimExistingGeneratedTagsOnNextRun = configuration.ClaimExistingGeneratedTagsOnNextRun,
            StaleTagMode = configuration.StaleTagMode
        };
    }

    public static IReadOnlyCollection<string> GetExcludedKeywordPrefixes(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return (configuration.ExcludedKeywordPrefixes ?? string.Empty)
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(prefix => prefix.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeSeparator(string? value, ICollection<string> errors)
    {
        var separator = value?.Trim() ?? string.Empty;
        if (separator.Length == 0)
        {
            errors.Add("Tag separator must not be empty.");
            return DefaultSeparator;
        }

        if (separator.Length > MaxSeparatorLength || separator.Any(IsUnsafeNamespaceCharacter))
        {
            errors.Add($"Tag separator must be {MaxSeparatorLength} characters or fewer and cannot contain spaces or invisible characters.");
            return DefaultSeparator;
        }

        return separator;
    }

    private static string NormalizePrefix(
        string? value,
        string fieldName,
        string defaultPrefix,
        string separator,
        ICollection<string> errors)
    {
        var prefix = value?.Trim() ?? string.Empty;
        while (prefix.EndsWith(separator, StringComparison.Ordinal))
        {
            prefix = prefix[..^separator.Length];
        }

        if (prefix.Length == 0)
        {
            errors.Add($"{fieldName} must not be empty.");
            return defaultPrefix;
        }

        if (prefix.Length > MaxPrefixLength || prefix.Any(IsUnsafeNamespaceCharacter))
        {
            errors.Add($"{fieldName} must be {MaxPrefixLength} characters or fewer and cannot contain spaces or invisible characters.");
            return defaultPrefix;
        }

        return prefix;
    }

    private static string RemoveTrailingSeparator(string namespaceValue, string separator)
    {
        return namespaceValue.EndsWith(separator, StringComparison.Ordinal)
            ? namespaceValue[..^separator.Length]
            : namespaceValue;
    }

    private static bool IsUnsafeNamespaceCharacter(char character)
    {
        return char.IsWhiteSpace(character) || char.IsControl(character);
    }

    private static int ClampToZero(int value)
    {
        return Math.Max(0, value);
    }
}
