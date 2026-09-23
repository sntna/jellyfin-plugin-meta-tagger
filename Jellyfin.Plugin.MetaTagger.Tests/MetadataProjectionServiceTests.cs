using Jellyfin.Plugin.MetaTagger;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using System.Reflection;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed class MetadataProjectionServiceTests
{
    [Fact]
    public void MetadataTagInput_ExposesOnlyProjectedMetadataMembers()
    {
        var properties = typeof(MetadataTagInput)
            .GetProperties()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "AudioLanguages",
                "ExistingTags",
                "Genres",
                "ItemId",
                "ItemPath",
                "ItemType",
                "KeywordSourceTags",
                "ParentalRatings",
                "ProductionCountries",
                "ProductionYears",
                "ProviderIdSources",
                "Studios",
                "SubtitleLanguages"
            ],
            properties.Select(property => property.Name).ToArray());

        AssertPropertyType(properties, "ProviderIdSources", typeof(IReadOnlyCollection<MetadataProviderId>));
        AssertPropertyType(properties, "ParentalRatings", typeof(IReadOnlyCollection<string>));
        AssertPropertyType(properties, "ProductionYears", typeof(IReadOnlyCollection<int>));
        AssertPropertyType(properties, "AudioLanguages", typeof(IReadOnlyCollection<string>));
        AssertPropertyType(properties, "SubtitleLanguages", typeof(IReadOnlyCollection<string>));
    }

    [Fact]
    public void Project_MovieExtractsCurrentTagsAndEffectiveSourceFacts()
    {
        var service = new MetadataProjectionService();
        var movie = new Movie
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Path = "/media/sample.mkv",
            Tags = ["friendship", "manual:tagger:skip"],
            Genres = ["Animation", "Family"],
            OfficialRating = "PG",
            Studios = ["Studio A"],
            ProductionLocations = ["United States"],
            ProviderIds = new Dictionary<string, string?>
            {
                ["Imdb"] = "tt123",
                ["Tmdb"] = "456"
            },
            ProductionYear = 2024
        };

        var input = service.Project(movie, includeParentSeriesMetadataOnEpisodes: false);

        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", input.ItemId);
        Assert.Equal("/media/sample.mkv", input.ItemPath);
        Assert.Equal("Movie", input.ItemType);
        Assert.Equal(["friendship", "manual:tagger:skip"], input.ExistingTags);
        Assert.Equal(["friendship", "manual:tagger:skip"], input.KeywordSourceTags);
        Assert.Equal(["Animation", "Family"], input.Genres);
        Assert.Equal(["PG"], input.ParentalRatings);
        Assert.Equal(["Studio A"], input.Studios);
        Assert.Equal(["United States"], input.ProductionCountries);
        Assert.Contains(input.ProviderIdSources, provider => provider.Name == "Imdb" && provider.Value == "tt123");
        Assert.Contains(input.ProviderIdSources, provider => provider.Name == "Tmdb" && provider.Value == "456");
        Assert.Equal([2024], input.ProductionYears);
    }

    [Fact]
    public void Project_CustomRatingOverridesOfficialRatingForJellyfinParentalControls()
    {
        var service = new MetadataProjectionService();
        var movie = new Movie
        {
            OfficialRating = "PG",
            CustomRating = "R"
        };

        var input = service.Project(movie, includeParentSeriesMetadataOnEpisodes: false);

        Assert.Equal(["R"], input.ParentalRatings);
    }

    [Fact]
    public void Project_EpisodeExcludesParentSeriesMetadataByDefault()
    {
        var service = new MetadataProjectionService(_ => Series());

        var input = service.Project(Episode(), includeParentSeriesMetadataOnEpisodes: false);

        Assert.Equal(["episode-tag"], input.ExistingTags);
        Assert.Equal(["episode-tag"], input.KeywordSourceTags);
        Assert.Equal(["Episode Genre"], input.Genres);
    }

    [Fact]
    public void Project_EpisodeIncludesParentSeriesMetadataWhenEnabled()
    {
        var service = new MetadataProjectionService(_ => Series());

        var input = service.Project(Episode(), includeParentSeriesMetadataOnEpisodes: true);

        Assert.Equal(["episode-tag"], input.ExistingTags);
        Assert.Equal(["episode-tag", "series-tag"], input.KeywordSourceTags);
        Assert.Equal(["Episode Genre", "Series Genre"], input.Genres);
        Assert.Equal(["TV-PG", "TV-Y"], input.ParentalRatings);
        Assert.Contains(input.ProviderIdSources, provider => provider.Name == "EpisodeDb" && provider.Value == "episode-1");
        Assert.Contains(input.ProviderIdSources, provider => provider.Name == "SeriesDb" && provider.Value == "series-1");
        Assert.Equal([2024, 2023], input.ProductionYears);
    }

    [Fact]
    public void Project_ExcludesOnlyEachSourcesOwnedTagsFromKeywordsAcrossNamespaces()
    {
        var episode = Episode();
        episode.Tags = ["episode-topic", "META:genre:episode", "meta:genre:series"];
        var series = Series();
        series.Tags = ["series-topic", "meta:genre:series"];
        var service = new MetadataProjectionService(_ => series);
        var trackedItems = new Dictionary<string, MetaTaggerStateItem>
        {
            [episode.Id.ToString("N")] = new() { LastAppliedTags = ["meta:genre:episode"] },
            [series.Id.ToString("N")] = new() { LastAppliedTags = ["meta:genre:series"] }
        };

        var input = service.Project(episode, includeParentSeriesMetadataOnEpisodes: true, trackedItems);

        Assert.Equal(["episode-topic", "meta:genre:series", "series-topic"], input.KeywordSourceTags);
        Assert.Equal(["episode-topic", "META:genre:episode", "meta:genre:series"], input.ExistingTags);
        Assert.Equal(["Episode Genre", "Series Genre"], input.Genres);
    }

    [Fact]
    public void Project_EpisodeExcludesNonSeriesParentMetadata()
    {
        var nonSeriesParent = new MediaBrowser.Controller.Entities.Video
        {
            Tags = ["video-parent-tag"],
            Genres = ["Video Parent Genre"]
        };
        var service = new MetadataProjectionService(_ => nonSeriesParent);

        var input = service.Project(Episode(), includeParentSeriesMetadataOnEpisodes: true);

        Assert.Equal(["episode-tag"], input.KeywordSourceTags);
        Assert.Equal(["Episode Genre"], input.Genres);
    }

    private static MediaBrowser.Controller.Entities.TV.Episode Episode()
    {
        return new MediaBrowser.Controller.Entities.TV.Episode
        {
            Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Path = "/media/show/s01e01.mkv",
            Tags = ["episode-tag"],
            Genres = ["Episode Genre"],
            OfficialRating = "TV-PG",
            ProviderIds = new Dictionary<string, string?>
            {
                ["EpisodeDb"] = "episode-1"
            },
            ProductionYear = 2024
        };
    }

    private static MediaBrowser.Controller.Entities.TV.Series Series()
    {
        return new MediaBrowser.Controller.Entities.TV.Series
        {
            Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            Path = "/media/show",
            Tags = ["series-tag"],
            Genres = ["Series Genre"],
            OfficialRating = "TV-Y",
            ProviderIds = new Dictionary<string, string?>
            {
                ["SeriesDb"] = "series-1"
            },
            ProductionYear = 2023
        };
    }

    private static void AssertPropertyType(PropertyInfo[] properties, string propertyName, Type expectedType)
    {
        var property = Assert.Single(properties, property => property.Name == propertyName);
        Assert.Equal(expectedType, property.PropertyType);
    }
}
