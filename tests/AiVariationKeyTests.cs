using Fynydd.Umbraco.Search.Qdrant.Extensions;

namespace Umbraco.Search.Qdrant.Tests;

public sealed class AiVariationKeyTests
{
    [Fact]
    public void Create_ReturnsNullForInvariantVariation()
    {
        Assert.Null(AiVariationKey.Create(null, null));
        Assert.Null(AiVariationKey.Create(" ", " "));
    }

    [Fact]
    public void Create_CombinesCultureAndSegment()
    {
        Assert.Equal("en-US__segment__mobile", AiVariationKey.Create(" en-US ", " mobile "));
    }

    [Fact]
    public void Parse_RestoresSegmentOnlyKey()
    {
        var key = AiVariationKey.Create(null, "mobile");

        var result = AiVariationKey.Parse(key);

        Assert.Null(result.Culture);
        Assert.Equal("mobile", result.Segment);
    }

    [Fact]
    public void SearchKeys_ReturnsSpecificThenFallbacks()
    {
        var keys = AiVariationKey.SearchKeys(AiVariationKey.Create("en-US", "mobile"));

        Assert.Equal(
            [
                "en-US__segment__mobile",
                "en-US",
                "__invariant__segment__mobile",
                null
            ],
            keys);
    }

    [Fact]
    public void ToCollectionSuffix_ReplacesUnsafeCharacters()
    {
        Assert.Equal("-en-us-segment-beta", AiVariationKey.ToCollectionSuffix("en US/segment:beta"));
    }
}
