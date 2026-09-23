using Jellyfin.Plugin.MetaTagger;
using Jellyfin.Plugin.MetaTagger.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed class MetadataTagServiceTests
{
    [Fact]
    public void GenerateTags_DefaultsUseOnlyGenresRatingAndAudioLanguages()
    {
        var input = new MetadataTagInput
        {
            Genres = ["Animation"], ParentalRatings = ["PG"], AudioLanguages = ["eng"],
            SubtitleLanguages = ["spa"], KeywordSourceTags = ["friendship"], Studios = ["Pixar"],
            ProductionCountries = ["United States"], ProductionYears = [2024],
            ProviderIdSources = [new MetadataProviderId("Tmdb", "123")]
        };

        Assert.Equal(["meta:audio-language:eng", "meta:genre:animation", "meta:rating:pg"],
            new MetadataTagService().GenerateTags(input, new PluginConfiguration()));
    }

    [Fact]
    public void ExplainTags_UsesRecordedTrackLanguageCodesAndSkipsUndeterminedValues()
    {
        var input = System.Text.Json.JsonSerializer.Deserialize<MetadataTagInput>("""
            { "AudioLanguages": ["eng", " ENG ", "", "und", "UND"],
              "SubtitleLanguages": ["spa", "pt-BR", "und", " "] }
            """)!;
        var configuration = System.Text.Json.JsonSerializer.Deserialize<PluginConfiguration>("""
            { "EnableAudioLanguages": true, "EnableSubtitleLanguages": true }
            """)!;

        var sources = new MetadataTagService().ExplainTags(input, configuration);

        Assert.Equal(["meta:audio-language:eng", "meta:subtitle-language:pt-br", "meta:subtitle-language:spa"],
            sources.Select(source => source.Tag));
        Assert.Equal(["eng", " ENG "], sources.Single(source => source.Source == "audio-language").Values);
        Assert.Empty(new MetadataTagService().GenerateTags(input, new PluginConfiguration { EnableAudioLanguages = false }));
    }

    [Fact]
    public void GenerateTags_MirrorsEnabledMetadataSourcesWithoutInterpretingPolicy()
    {
        var service = new MetadataTagService();
        var input = new MetadataTagInput
        {
            Genres = ["Kids", "Animation", "Family"],
            ParentalRatings = ["TV-Y"],
            KeywordSourceTags = ["friendship", "educational", "manual:block-kids", "meta:genre:old"],
            Studios = ["Apple TV+"],
            ProductionCountries = ["United States of America"],
            ProviderIdSources =
            [
                new MetadataProviderId("Imdb", "tt123"),
                new MetadataProviderId("Tmdb", "456"),
                new MetadataProviderId("Tvdb", null)
            ],
            ProductionYears = [2023]
        };

        var tags = service.GenerateTags(input, new PluginConfiguration { EnableExistingTagsAsKeywords = true, EnableStudios = true, EnableProductionCountries = true, EnableProviderIds = true, EnableProductionYear = true }).ToArray();

        Assert.Contains("meta:rating:tv-y", tags);
        Assert.Contains("meta:genre:kids", tags);
        Assert.Contains("meta:genre:animation", tags);
        Assert.Contains("meta:genre:family", tags);
        Assert.Contains("meta:keyword:friendship", tags);
        Assert.Contains("meta:keyword:educational", tags);
        Assert.Contains("meta:studio:apple-tv-plus", tags);
        Assert.Contains("meta:country:united-states-of-america", tags);
        Assert.Contains("meta:provider:imdb", tags);
        Assert.Contains("meta:provider:tmdb", tags);
        Assert.DoesNotContain("meta:provider:tvdb", tags);
        Assert.Contains("meta:year:2023", tags);
        Assert.DoesNotContain("audience:kids", tags);
        Assert.DoesNotContain("risk:high", tags);
        Assert.DoesNotContain("tone:intense", tags);
        Assert.DoesNotContain("meta:keyword:manual-block-kids", tags);
        Assert.DoesNotContain("meta:keyword:meta-genre-old", tags);
    }

    [Fact]
    public void GenerateTags_RespectsDisabledSources()
    {
        var service = new MetadataTagService();
        var config = new PluginConfiguration
        {
            EnableGenres = false,
            EnableExistingTagsAsKeywords = false,
            EnableProviderIds = false,
            EnableProductionYear = false
        };

        var input = new MetadataTagInput
        {
            Genres = ["Horror"],
            ParentalRatings = ["R"],
            KeywordSourceTags = ["friendship"],
            ProviderIdSources = [new MetadataProviderId("Imdb", "tt123")],
            ProductionYears = [1984]
        };

        var tags = service.GenerateTags(input, config).ToArray();

        Assert.Equal(["meta:rating:r"], tags);
    }

    [Fact]
    public void GenerateTags_UsesConfiguredTagSeparatorWhenPrefixOmitsIt()
    {
        var service = new MetadataTagService();
        var config = new PluginConfiguration
        {
            EnableExistingTagsAsKeywords = true,
            GeneratedTagPrefix = "meta",
            ManualTagPrefix = "manual",
            TagSeparator = ":"
        };
        var input = new MetadataTagInput
        {
            Genres = ["Science Fiction"],
            KeywordSourceTags = ["friendship", "manual:tagger:skip", "meta:genre:old"]
        };

        var tags = service.GenerateTags(input, config).ToArray();

        Assert.Contains("meta:genre:science-fiction", tags);
        Assert.Contains("meta:keyword:friendship", tags);
        Assert.DoesNotContain("meta:keyword:manual-tagger-skip", tags);
        Assert.DoesNotContain("meta:keyword:meta-genre-old", tags);
    }

    [Fact]
    public void GenerateTags_LimitsKeywordTagsAndExcludesConfiguredPrefixes()
    {
        var service = new MetadataTagService();
        var config = new PluginConfiguration
        {
            EnableExistingTagsAsKeywords = true,
            MaxKeywordTagsPerItem = 2,
            ExcludedKeywordPrefixes = "private:, skip-"
        };
        var input = new MetadataTagInput
        {
            KeywordSourceTags = ["friendship", "private:note", "education", "skip-legacy", "adventure"]
        };

        var tags = service.GenerateTags(input, config).ToArray();

        Assert.Contains("meta:keyword:friendship", tags);
        Assert.Contains("meta:keyword:education", tags);
        Assert.DoesNotContain("meta:keyword:private-note", tags);
        Assert.DoesNotContain("meta:keyword:skip-legacy", tags);
        Assert.DoesNotContain("meta:keyword:adventure", tags);
    }

    [Fact]
    public void GenerateTags_DoesNotLetDuplicateKeywordSourceTagsConsumeKeywordLimit()
    {
        var service = new MetadataTagService();
        var config = new PluginConfiguration { EnableExistingTagsAsKeywords = true, MaxKeywordTagsPerItem = 2 };
        var input = new MetadataTagInput
        {
            KeywordSourceTags = ["friendship", "friendship", "education"]
        };

        var tags = service.GenerateTags(input, config).ToArray();

        Assert.Contains("meta:keyword:friendship", tags);
        Assert.Contains("meta:keyword:education", tags);
    }

    [Fact]
    public void GenerateTags_MirrorsCollectionShapedProjectedSources()
    {
        var service = new MetadataTagService();
        var input = new MetadataTagInput
        {
            Genres = ["Kids", "Animation", "Family"],
            ParentalRatings = ["TV-Y", "TV-PG"],
            KeywordSourceTags = ["friendship", "educational", "manual:block-kids", "meta:genre:old"],
            Studios = ["Apple TV+"],
            ProductionCountries = ["United States of America"],
            ProviderIdSources =
            [
                new MetadataProviderId("Imdb", "tt123"),
                new MetadataProviderId("Tmdb", "456"),
                new MetadataProviderId("Tvdb", null)
            ],
            ProductionYears = [2023, 2024]
        };

        var tags = service.GenerateTags(input, new PluginConfiguration { EnableExistingTagsAsKeywords = true, EnableStudios = true, EnableProductionCountries = true, EnableProviderIds = true, EnableProductionYear = true }).ToArray();

        Assert.Contains("meta:rating:tv-y", tags);
        Assert.Contains("meta:rating:tv-pg", tags);
        Assert.Contains("meta:genre:kids", tags);
        Assert.Contains("meta:genre:animation", tags);
        Assert.Contains("meta:genre:family", tags);
        Assert.Contains("meta:keyword:friendship", tags);
        Assert.Contains("meta:keyword:educational", tags);
        Assert.Contains("meta:studio:apple-tv-plus", tags);
        Assert.Contains("meta:country:united-states-of-america", tags);
        Assert.Contains("meta:provider:imdb", tags);
        Assert.Contains("meta:provider:tmdb", tags);
        Assert.DoesNotContain("meta:provider:tvdb", tags);
        Assert.Contains("meta:year:2023", tags);
        Assert.Contains("meta:year:2024", tags);
        Assert.DoesNotContain("audience:kids", tags);
        Assert.DoesNotContain("risk:high", tags);
        Assert.DoesNotContain("tone:intense", tags);
        Assert.DoesNotContain("meta:keyword:manual-block-kids", tags);
        Assert.DoesNotContain("meta:keyword:meta-genre-old", tags);
    }

    [Fact]
    public void GenerateTags_DoesNotUseCurrentItemTagsAsKeywordSources()
    {
        var service = new MetadataTagService();
        var input = new MetadataTagInput
        {
            ExistingTags = ["current-only"],
            KeywordSourceTags = ["projected-keyword"]
        };

        var tags = service.GenerateTags(input, new PluginConfiguration { EnableExistingTagsAsKeywords = true }).ToArray();

        Assert.Contains("meta:keyword:projected-keyword", tags);
        Assert.DoesNotContain("meta:keyword:current-only", tags);
    }
}
