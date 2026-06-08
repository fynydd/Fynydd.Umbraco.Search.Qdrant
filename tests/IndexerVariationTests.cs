using System.Reflection;
using Argentini.Umbraco.Search.Qdrant.Indexers;
using Umbraco.Cms.Search.Core.Models.Indexing;

namespace Umbraco.Search.Qdrant.Tests;

public sealed class IndexerVariationTests
{
    [Fact]
    public void GetVariants_UsesExplicitVariationsWhenProvided()
    {
        var fields = new[]
        {
            new IndexField("body", new Umbraco.Cms.Search.Core.Models.Indexing.IndexValue(), "en-US", null)
        };
        var variations = InvokeGetVariants([new Variation("da-DK", "mobile")], fields);

        var variation = Assert.Single(variations);
        Assert.Equal("da-DK", variation.Culture);
        Assert.Equal("mobile", variation.Segment);
    }

    [Fact]
    public void GetVariants_FallsBackToFieldVariationsAndDeduplicates()
    {
        var fields = new[]
        {
            new IndexField("body", new Umbraco.Cms.Search.Core.Models.Indexing.IndexValue(), "en-US", null),
            new IndexField("title", new Umbraco.Cms.Search.Core.Models.Indexing.IndexValue(), "en-US", null),
            new IndexField("summary", new Umbraco.Cms.Search.Core.Models.Indexing.IndexValue(), "en-US", "mobile")
        };

        var variations = InvokeGetVariants([], fields);

        Assert.Equal(2, variations.Count);
        Assert.Contains(variations, variation => variation.Culture == "en-US" && variation.Segment is null);
        Assert.Contains(variations, variation => variation.Culture == "en-US" && variation.Segment == "mobile");
    }

    [Fact]
    public void GetVariants_ReturnsInvariantWhenNoVariationDataExists()
    {
        var variation = Assert.Single(InvokeGetVariants([], []));

        Assert.Null(variation.Culture);
        Assert.Null(variation.Segment);
    }

    [Fact]
    public void FieldAppliesToVariation_AllowsInvariantFieldsForSpecificVariation()
    {
        var field = new IndexField("body", new Umbraco.Cms.Search.Core.Models.Indexing.IndexValue(), null, null);

        Assert.True(InvokeFieldAppliesToVariation(field, new Variation("en-US", "mobile")));
    }

    [Fact]
    public void FieldAppliesToVariation_RequiresMatchingCultureAndSegment()
    {
        var field = new IndexField("body", new Umbraco.Cms.Search.Core.Models.Indexing.IndexValue(), "en-US", "mobile");

        Assert.True(InvokeFieldAppliesToVariation(field, new Variation("en-US", "mobile")));
        Assert.False(InvokeFieldAppliesToVariation(field, new Variation("da-DK", "mobile")));
        Assert.False(InvokeFieldAppliesToVariation(field, new Variation("en-US", "desktop")));
    }

    private static List<Variation> InvokeGetVariants(IEnumerable<Variation> variations, IReadOnlyCollection<IndexField> fields)
    {
        var method = typeof(FilteringAiVectorIndexer).GetMethod("GetVariants", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        return Assert.IsAssignableFrom<IEnumerable<Variation>>(method.Invoke(null, [variations, fields])).ToList();
    }

    private static bool InvokeFieldAppliesToVariation(IndexField field, Variation variation)
    {
        var method = typeof(FilteringAiVectorIndexer).GetMethod("FieldAppliesToVariation", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        return Assert.IsType<bool>(method.Invoke(null, [field, variation]));
    }
}
