using Jellyfin.Plugin.MetaTagger;
using Jellyfin.Plugin.MetaTagger.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed partial class MetaTaggerRunnerTests
{
    [Fact]
    public async Task InspectItemAsync_ExplainsDisabledMissingAndRecordedSources()
    {
        var item = new Movie { Id = Guid.NewGuid() };
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration { EnableAudioLanguages = true }, [item]);
        host.StreamLookup = _ => [
            new MediaStream { Type = MediaStreamType.Audio, Language = "eng" },
            new MediaStream { Type = MediaStreamType.Audio, Language = " ENG ", Title = "Commentary" },
            new MediaStream { Type = MediaStreamType.Audio, Language = "spa" },
            new MediaStream { Type = MediaStreamType.Audio, Language = "und" },
            new MediaStream { Type = MediaStreamType.Subtitle, Language = "fra" }];

        var result = await CreateRunner(host).InspectItemAsync(item.Id, null, CancellationToken.None);

        Assert.Equal(["meta:audio-language:eng", "meta:audio-language:spa"], result.GeneratedTags);
        Assert.Equal(SourceExplanationStatus.Generated, Assert.Single(result.SourceExplanations, s => s.Source == "audio-language").Status);
        Assert.Equal(SourceExplanationStatus.Disabled, Assert.Single(result.SourceExplanations, s => s.Source == "subtitle-language").Status);
        Assert.Equal(SourceExplanationStatus.MissingData, Assert.Single(result.SourceExplanations, s => s.Source == "genre").Status);
        Assert.Empty(host.UpdateAttempts);
    }
    [Theory]
    [InlineData("disabled", SourceExplanationStatus.Disabled)]
    [InlineData("excluded", SourceExplanationStatus.ExcludedItemType)]
    [InlineData("locked", SourceExplanationStatus.Protected)]
    [InlineData("skip", SourceExplanationStatus.Protected)]
    [InlineData("failure", SourceExplanationStatus.LookupFailed)]
    [InlineData("blank", SourceExplanationStatus.NoRecordedLanguage)]
    [InlineData("und", SourceExplanationStatus.NoRecordedLanguage)]
    [InlineData("absent", SourceExplanationStatus.MissingData)]
    public async Task PreviewItemAsync_ExplainsWhyAudioProducedNoTags(string scenario, SourceExplanationStatus expected)
    {
        var item = new Movie { Id = Guid.NewGuid(), IsLocked = scenario == "locked",
            Tags = scenario == "skip" ? ["manual:tagger:skip"] : [] };
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration
        {
            EnableAudioLanguages = true, IsEnabled = scenario != "disabled", IncludeMovies = scenario != "excluded"
        }, [item]);
        host.StreamLookup = _ => scenario == "failure" ? throw new IOException("Injected stream failure")
            : scenario is "blank" or "und" ? [new MediaStream { Type = MediaStreamType.Audio, Language = scenario == "blank" ? " " : " UND " }] : [];
        var store = new MetaTaggerStateStore(_directory);
        var runner = CreateRunner(host, store);

        var inspection = await runner.PreviewItemAsync(item.Id, CancellationToken.None);

        Assert.Equal(expected, Assert.Single(inspection.SourceExplanations, s => s.Source == "audio-language").Status);
        Assert.Empty(inspection.GeneratedTags);
        Assert.Null(inspection.Token);
        Assert.Empty(host.UpdateAttempts);
        var controller = new MetaTaggerDashboardController(store, runner);
        var history = Assert.Single(await controller.GetRunsAsync(CancellationToken.None));
        var detail = (await controller.GetRunAsync(history.RunId, CancellationToken.None)).Value;
        Assert.Equal(expected, Assert.Single(Assert.Single(detail!.Items).SourceExplanations, s => s.Source == "audio-language").Status);
        if (scenario == "failure") { Assert.Equal(1, detail.Summary.Failures); }
    }

    [Fact]
    public async Task InspectItemAsync_SeriesExplainsThatItHasNoOwnTracks()
    {
        var series = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid() };
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration { EnableAudioLanguages = true }, [series]);
        var result = await CreateRunner(host).InspectItemAsync(series.Id, null, CancellationToken.None);
        var source = Assert.Single(result.SourceExplanations, s => s.Source == "audio-language");
        Assert.Equal(SourceExplanationStatus.NoItemTracks, source.Status);
        Assert.Contains("Episode languages are not combined", source.Reason);
        Assert.Empty(result.GeneratedTags);
    }
    [Fact]
    public async Task RunAsync_RecordsSourceResultsWhilePreservingFailedAndProtectedItems()
    {
        var failed = new Movie { Id = Guid.NewGuid(), Tags = ["meta:audio-language:eng", "manual:keep"] };
        var locked = new Movie { Id = Guid.NewGuid(), IsLocked = true, Tags = ["meta:audio-language:eng"] };
        var next = new Movie { Id = Guid.NewGuid(), Tags = ["Favorites", "manual:keep"] };
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration
        { EnableAudioLanguages = true, EnableSubtitleLanguages = true, PreviewOnly = false, StaleTagMode = StaleTagMode.Remove }, [failed, locked, next]);
        host.StreamLookup = id => id == failed.Id ? throw new IOException("Injected failure") : [
            new MediaStream { Type = MediaStreamType.Audio, Language = "eng" },
            new MediaStream { Type = MediaStreamType.Audio, Language = " ENG ", Title = "Commentary" },
            new MediaStream { Type = MediaStreamType.Audio, Language = "spa", Title = "Commentary only" },
            new MediaStream { Type = MediaStreamType.Audio, Language = "und" },
            new MediaStream { Type = MediaStreamType.Subtitle, Language = "fra" }];
        var state = new MetaTaggerState();
        foreach (var item in new[] { failed, locked })
        {
            state.Items[item.Id.ToString("N")] = new MetaTaggerStateItem
            { ItemId = item.Id.ToString("N"), ItemType = "Movie", LastAppliedTags = ["meta:audio-language:eng"] };
        }
        var store = new MetaTaggerStateStore(_directory);
        await store.SaveAsync(state, CancellationToken.None);
        var runner = CreateRunner(host, store);
        var summary = await runner.RunAsync(new NoOpProgress(), CancellationToken.None);
        var controller = new MetaTaggerDashboardController(store, runner);
        var run = (await controller.GetRunAsync(summary.RunId, CancellationToken.None)).Value!;
        Assert.Equal(1, summary.Failures);
        Assert.Equal(1, summary.WritesApplied);
        Assert.Equal(SourceExplanationStatus.LookupFailed, Assert.Single(run.Items.Single(i => i.ItemId == failed.Id.ToString("N")).SourceExplanations, s => s.Source == "audio-language").Status);
        Assert.Equal(SourceExplanationStatus.Protected, Assert.Single(run.Items.Single(i => i.ItemId == locked.Id.ToString("N")).SourceExplanations, s => s.Source == "audio-language").Status);
        Assert.Equal(SourceExplanationStatus.Generated, Assert.Single(run.Items.Single(i => i.ItemId == next.Id.ToString("N")).SourceExplanations, s => s.Source == "audio-language").Status);
        Assert.Equal(["meta:audio-language:eng", "manual:keep"], failed.Tags);
        Assert.Equal(["meta:audio-language:eng"], locked.Tags);
        var saved = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(["meta:audio-language:eng"], saved.Items[failed.Id.ToString("N")].LastAppliedTags);
        Assert.Equal(["meta:audio-language:eng"], saved.Items[locked.Id.ToString("N")].LastAppliedTags);
        Assert.Equal(["Favorites", "manual:keep", "meta:audio-language:eng", "meta:audio-language:spa", "meta:subtitle-language:fra"], next.Tags);
    }
    [Fact]
    public async Task ApplyItemAsync_RecordsLookupFailureDuringFinalRevalidation()
    {
        var item = new Movie { Id = Guid.NewGuid(), Tags = ["manual:keep"] };
        var host = new InMemoryMetaTaggerHost(new PluginConfiguration { EnableAudioLanguages = true }, [item]);
        var lookups = 0;
        host.StreamLookup = _ => ++lookups == 3 ? throw new IOException("Final lookup failed")
            : [new MediaStream { Type = MediaStreamType.Audio, Language = "eng" }];
        var store = new MetaTaggerStateStore(_directory);
        var runner = CreateRunner(host, store);
        var preview = await runner.PreviewItemAsync(item.Id, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ApplyItemAsync(item.Id, preview.Token!, CancellationToken.None));
        var controller = new MetaTaggerDashboardController(store, runner);
        var history = (await controller.GetRunsAsync(CancellationToken.None)).Single(r => r.Operation == "Apply");
        var result = (await controller.GetRunAsync(history.RunId, CancellationToken.None)).Value!;
        Assert.Equal(SourceExplanationStatus.LookupFailed, Assert.Single(Assert.Single(result.Items).SourceExplanations, s => s.Source == "audio-language").Status);
        Assert.Empty(host.UpdateAttempts);
        Assert.Equal(["manual:keep"], item.Tags);
    }
}
