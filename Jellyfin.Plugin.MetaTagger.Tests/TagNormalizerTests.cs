using Jellyfin.Plugin.MetaTagger;
using Xunit;

namespace Jellyfin.Plugin.MetaTagger.Tests;

public sealed class TagNormalizerTests
{
    [Theory]
    [InlineData("Science Fiction", "science-fiction")]
    [InlineData("TV-Y7", "tv-y7")]
    [InlineData("Apple TV+", "apple-tv-plus")]
    [InlineData("United States of America", "united-states-of-america")]
    [InlineData("based on children's book", "based-on-childrens-book")]
    [InlineData("  repeated   separators___and spaces  ", "repeated-separators-and-spaces")]
    [InlineData("München", "munchen")]
    public void NormalizeValue_ReturnsStableAsciiSlug(string input, string expected)
    {
        Assert.Equal(expected, TagNormalizer.NormalizeValue(input));
    }
}
