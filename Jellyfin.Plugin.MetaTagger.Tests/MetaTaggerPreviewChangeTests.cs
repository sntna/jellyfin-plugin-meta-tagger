using Jellyfin.Plugin.MetaTagger;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed class MetaTaggerPreviewChangeTests
{
    [Fact]
    public async Task SavePreviewChangesAsync_PersistsDryRunExport()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MetaTaggerStateStore(directory);
            var changes = new[]
            {
                new MetaTaggerPreviewChange
                {
                    ItemId = "item-1",
                    ItemName = "Movie",
                    AddedTags = ["meta:genre:animation"],
                    RemovedTags = ["meta:genre:old"],
                    PreviewRemovedTags = ["meta:keyword:stale"]
                }
            };

            var path = await store.SavePreviewChangesAsync(changes, CancellationToken.None);

            var json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"itemId\": \"item-1\"", json, StringComparison.Ordinal);
            Assert.Contains("\"addedTags\"", json, StringComparison.Ordinal);
            Assert.EndsWith("last-preview-changes.json", path, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
