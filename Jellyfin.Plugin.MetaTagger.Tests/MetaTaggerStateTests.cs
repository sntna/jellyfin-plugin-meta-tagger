using Jellyfin.Plugin.MetaTagger;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed class MetaTaggerStateTests
{
    [Fact]
    public void PruneMissingItems_RemovesOnlyUnseenLedgerEntries()
    {
        var state = new MetaTaggerState
        {
            Items =
            {
                ["item-1"] = new MetaTaggerStateItem { ItemId = "item-1", ItemType = "Movie" },
                ["item-2"] = new MetaTaggerStateItem { ItemId = "item-2", ItemType = "Movie" }
            }
        };

        var removed = state.PruneMissingItems(["item-2"], ["Movie"]);

        Assert.Equal(1, removed);
        Assert.Equal(["item-2"], state.Items.Keys);
    }
}
