using Jellyfin.Plugin.MetaTagger.Configuration;
using System.Xml.Serialization;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed class PluginConfigurationValidatorTests
{
    [Fact]
    public void Configuration_DefaultsToThreeSourcesMoviesAndSeriesWithPreviewOnly()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader("<PluginConfiguration />");
        var configuration = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        Assert.True(configuration.EnableGenres);
        Assert.True(configuration.EnableParentalRating);
        Assert.False(configuration.EnableExistingTagsAsKeywords);
        Assert.False(configuration.EnableStudios);
        Assert.False(configuration.EnableProductionCountries);
        Assert.False(configuration.EnableProviderIds);
        Assert.False(configuration.EnableProductionYear);
        Assert.True(configuration.PreviewOnly);
        Assert.Equal(StaleTagMode.Keep, configuration.StaleTagMode);
        Assert.True(configuration.EnableAudioLanguages);
        Assert.True(configuration.IncludeMovies);
        Assert.True(configuration.IncludeSeries);
        Assert.False(configuration.IncludeEpisodes);
        Assert.False(configuration.IncludeVideos);
        Assert.False(configuration.IncludeParentSeriesMetadataOnEpisodes);
        Assert.False(configuration.RunAfterLibraryScan);
        Assert.Equal(MetadataTagRunMode.Incremental, configuration.DefaultRunMode);
        Assert.False(configuration.EnableSubtitleLanguages);

        configuration.EnableAudioLanguages = true;
        configuration.EnableSubtitleLanguages = true;
        var sanitized = PluginConfigurationValidator.Sanitize(configuration);
        using var writer = new StringWriter();
        serializer.Serialize(writer, sanitized);
        using var savedReader = new StringReader(writer.ToString());
        var restored = Assert.IsType<PluginConfiguration>(serializer.Deserialize(savedReader));
        Assert.True(restored.EnableAudioLanguages);
        Assert.True(restored.EnableSubtitleLanguages);
    }

    [Fact]
    public void Configuration_LegacyOfficialRatingElementPopulatesParentalRatingSetting()
    {
        const string xml = "<PluginConfiguration><EnableOfficialRating>false</EnableOfficialRating></PluginConfiguration>";
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader(xml);

        var configuration = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        Assert.False(configuration.EnableParentalRating);
    }

    [Fact]
    public void Validate_RejectsEqualEffectiveNamespaces()
    {
        var config = new PluginConfiguration
        {
            GeneratedTagPrefix = "same:",
            ManualTagPrefix = "same",
            TagSeparator = ":"
        };

        var result = PluginConfigurationValidator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("prefixes", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("meta:", result.EffectiveGeneratedNamespace);
        Assert.Equal("manual:", result.EffectiveManualNamespace);
    }

    [Fact]
    public void Validate_RejectsBlankUnsafeOrLongNamespaceParts()
    {
        var config = new PluginConfiguration
        {
            GeneratedTagPrefix = new string('a', 33),
            ManualTagPrefix = "manual bad",
            TagSeparator = "\t"
        };

        var result = PluginConfigurationValidator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Generated tag prefix", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("Manual tag prefix", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("Tag separator", StringComparison.Ordinal));
        Assert.Equal("meta:", result.EffectiveGeneratedNamespace);
        Assert.Equal("manual:", result.EffectiveManualNamespace);
        Assert.Equal(":", result.EffectiveSeparator);
    }

    [Fact]
    public void Sanitize_ClampsNegativeResourceControls()
    {
        var config = new PluginConfiguration
        {
            MaxItemsPerRun = -1,
            MaxWritesPerRun = -5,
            MaxRunMinutes = -10,
            WriteDelayMilliseconds = -25,
            MinimumMinutesBetweenAutoRuns = -60,
            MaxKeywordTagsPerItem = -2
        };

        var sanitized = PluginConfigurationValidator.Sanitize(config);

        Assert.Equal(0, sanitized.MaxItemsPerRun);
        Assert.Equal(0, sanitized.MaxWritesPerRun);
        Assert.Equal(0, sanitized.MaxRunMinutes);
        Assert.Equal(0, sanitized.WriteDelayMilliseconds);
        Assert.Equal(0, sanitized.MinimumMinutesBetweenAutoRuns);
        Assert.Equal(0, sanitized.MaxKeywordTagsPerItem);
    }
}
