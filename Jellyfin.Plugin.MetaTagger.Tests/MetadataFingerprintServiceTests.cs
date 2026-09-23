using Jellyfin.Plugin.MetaTagger;
using Jellyfin.Plugin.MetaTagger.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed class MetadataFingerprintServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateFingerprint_TracksOnlyEnabledLanguagesAndIgnoresDuplicateUndeterminedTracks(bool audio)
    {
        var service = new MetadataFingerprintService();
        var configuration = new PluginConfiguration { EnableAudioLanguages = audio, EnableSubtitleLanguages = !audio };
        var first = new MetadataTagInput { AudioLanguages = ["eng"], SubtitleLanguages = ["spa"] };
        var changed = new MetadataTagInput { AudioLanguages = ["fra"], SubtitleLanguages = ["deu"] };
        var same = new MetadataTagInput
        {
            AudioLanguages = audio ? [" ENG ", "eng", "und", ""] : ["fra"],
            SubtitleLanguages = audio ? ["deu"] : [" SPA ", "spa", "und", ""]
        };

        Assert.NotEqual(service.CreateFingerprint(first, configuration), service.CreateFingerprint(changed, configuration));
        Assert.Equal(service.CreateFingerprint(first, configuration), service.CreateFingerprint(same, configuration));
        Assert.NotEqual(service.CreateFingerprint(first, configuration), service.CreateFingerprint(first, new PluginConfiguration { EnableAudioLanguages = false }));
        Assert.Equal(service.CreateFingerprint(first, new PluginConfiguration { EnableAudioLanguages = false }), service.CreateFingerprint(changed, new PluginConfiguration { EnableAudioLanguages = false }));
    }

    [Fact]
    public void CreateFingerprint_IgnoresGeneratedAndManualTags()
    {
        var service = new MetadataFingerprintService();
        var config = new PluginConfiguration { EnableExistingTagsAsKeywords = true };
        var input = new MetadataTagInput
        {
            Genres = ["Animation"],
            KeywordSourceTags = ["friendship", "meta:genre:old", "manual:tagger:skip"]
        };
        var sameMetadata = new MetadataTagInput
        {
            Genres = ["Animation"],
            KeywordSourceTags = ["friendship", "meta:rating:tv-y", "manual:custom"]
        };

        Assert.Equal(
            service.CreateFingerprint(input, config),
            service.CreateFingerprint(sameMetadata, config));
    }

    [Fact]
    public void CreateFingerprint_ChangesWhenRelevantSourceConfigurationChanges()
    {
        var service = new MetadataFingerprintService();
        var input = new MetadataTagInput
        {
            Genres = ["Animation"],
            KeywordSourceTags = ["friendship"]
        };

        var first = service.CreateFingerprint(input, new PluginConfiguration());
        var second = service.CreateFingerprint(input, new PluginConfiguration { EnableGenres = false });

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CreateFingerprint_ChangesWhenTagSeparatorChanges()
    {
        var service = new MetadataFingerprintService();
        var input = new MetadataTagInput
        {
            Genres = ["Animation"],
            KeywordSourceTags = ["friendship"]
        };

        var first = service.CreateFingerprint(input, new PluginConfiguration { TagSeparator = ":" });
        var second = service.CreateFingerprint(input, new PluginConfiguration { TagSeparator = "|" });

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CreateFingerprint_IgnoresExcludedKeywordPrefixes()
    {
        var service = new MetadataFingerprintService();
        var config = new PluginConfiguration { EnableExistingTagsAsKeywords = true, ExcludedKeywordPrefixes = "private:" };
        var first = new MetadataTagInput
        {
            KeywordSourceTags = ["friendship", "private:note"]
        };
        var second = new MetadataTagInput
        {
            KeywordSourceTags = ["friendship", "private:different"]
        };

        Assert.Equal(
            service.CreateFingerprint(first, config),
            service.CreateFingerprint(second, config));
    }

    [Fact]
    public void CreateFingerprint_ChangesWhenKeywordLimitChanges()
    {
        var service = new MetadataFingerprintService();
        var input = new MetadataTagInput
        {
            KeywordSourceTags = ["friendship", "education"]
        };

        var first = service.CreateFingerprint(input, new PluginConfiguration { EnableExistingTagsAsKeywords = true, MaxKeywordTagsPerItem = 1 });
        var second = service.CreateFingerprint(input, new PluginConfiguration { EnableExistingTagsAsKeywords = true, MaxKeywordTagsPerItem = 2 });

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CreateFingerprint_IgnoresKeywordControlsWhenKeywordSourceIsDisabled()
    {
        var service = new MetadataFingerprintService();
        var input = new MetadataTagInput
        {
            KeywordSourceTags = ["friendship", "education"]
        };

        var first = service.CreateFingerprint(input, new PluginConfiguration
        {
            EnableExistingTagsAsKeywords = false,
            MaxKeywordTagsPerItem = 1,
            ExcludedKeywordPrefixes = "private:"
        });
        var second = service.CreateFingerprint(input, new PluginConfiguration
        {
            EnableExistingTagsAsKeywords = false,
            MaxKeywordTagsPerItem = 2,
            ExcludedKeywordPrefixes = "skip-"
        });

        Assert.Equal(first, second);
    }

    [Fact]
    public void CreateFingerprint_IgnoresDisabledSourceValues()
    {
        var service = new MetadataFingerprintService();
        var config = new PluginConfiguration
        {
            EnableGenres = false,
            EnableStudios = false
        };
        var first = new MetadataTagInput
        {
            Genres = ["Animation"],
            Studios = ["Studio A"]
        };
        var second = new MetadataTagInput
        {
            Genres = ["Drama"],
            Studios = ["Studio B"]
        };

        Assert.Equal(
            service.CreateFingerprint(first, config),
            service.CreateFingerprint(second, config));
    }

    [Fact]
    public void CreateFingerprint_IgnoresSourceValueOrdering()
    {
        var service = new MetadataFingerprintService();
        var config = new PluginConfiguration { EnableExistingTagsAsKeywords = true };
        var first = new MetadataTagInput
        {
            Genres = ["Animation", "Family"],
            Studios = ["Apple TV+", "BBC"],
            ProviderIdSources = [new MetadataProviderId("Tmdb", "1"), new MetadataProviderId("Imdb", "2")]
        };
        var second = new MetadataTagInput
        {
            Genres = ["Family", "Animation"],
            Studios = ["BBC", "Apple TV+"],
            ProviderIdSources = [new MetadataProviderId("Imdb", "2"), new MetadataProviderId("Tmdb", "1")]
        };

        Assert.Equal(
            service.CreateFingerprint(first, config),
            service.CreateFingerprint(second, config));
    }

    [Fact]
    public void CreateFingerprint_UsesKeywordSourceTagsInsteadOfCurrentItemTags()
    {
        var service = new MetadataFingerprintService();
        var config = new PluginConfiguration { EnableExistingTagsAsKeywords = true };
        var first = new MetadataTagInput
        {
            ExistingTags = ["current-a"],
            KeywordSourceTags = ["friendship"]
        };
        var second = new MetadataTagInput
        {
            ExistingTags = ["current-b"],
            KeywordSourceTags = ["friendship"]
        };
        var changedSource = new MetadataTagInput
        {
            ExistingTags = ["current-a"],
            KeywordSourceTags = ["education"]
        };

        Assert.Equal(
            service.CreateFingerprint(first, config),
            service.CreateFingerprint(second, config));
        Assert.NotEqual(
            service.CreateFingerprint(first, config),
            service.CreateFingerprint(changedSource, config));
    }

    [Fact]
    public void CreateFingerprint_ChangesWhenProjectedEffectiveSourceChanges()
    {
        var service = new MetadataFingerprintService();
        var config = new PluginConfiguration();
        var first = new MetadataTagInput
        {
            Genres = ["Animation"],
            ParentalRatings = ["TV-Y"],
            ProviderIdSources = [new MetadataProviderId("Imdb", "tt123")],
            ProductionYears = [2023]
        };
        var second = new MetadataTagInput
        {
            Genres = ["Drama"],
            ParentalRatings = ["TV-PG"],
            ProviderIdSources = [new MetadataProviderId("Imdb", "tt999")],
            ProductionYears = [2024]
        };

        Assert.NotEqual(
            service.CreateFingerprint(first, config),
            service.CreateFingerprint(second, config));
    }

    [Fact]
    public void CreateFingerprint_IgnoresParentSeriesProjectionOptionAfterProjection()
    {
        var service = new MetadataFingerprintService();
        var input = new MetadataTagInput
        {
            Genres = ["Animation"],
            ParentalRatings = ["TV-Y"],
            ProviderIdSources = [new MetadataProviderId("Imdb", "tt123")],
            ProductionYears = [2023]
        };

        var first = service.CreateFingerprint(input, new PluginConfiguration
        {
            IncludeParentSeriesMetadataOnEpisodes = false
        });
        var second = service.CreateFingerprint(input, new PluginConfiguration
        {
            IncludeParentSeriesMetadataOnEpisodes = true
        });

        Assert.Equal(first, second);
    }
}
