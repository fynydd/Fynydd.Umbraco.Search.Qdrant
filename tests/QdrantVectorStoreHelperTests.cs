using System.Reflection;
using Fynydd.Umbraco.Search.Qdrant.VectorStores;
using Qdrant.Client.Grpc;

namespace Umbraco.Search.Qdrant.Tests;

public sealed class QdrantVectorStoreHelperTests
{
    [Theory]
    [InlineData("UmbAI_Search", null, "umbraco-qdrant-umbai_search")]
    [InlineData(" Docs ", "en-US", "umbraco-qdrant-docs-en-us")]
    [InlineData("Docs", "en-US__segment__Mobile", "umbraco-qdrant-docs-en-us__segment__mobile")]
    public void GetCollectionName_NormalizesIndexAndVariation(string indexName, string? culture, string expected)
    {
        Assert.Equal(expected, InvokeStatic<string>("GetCollectionName", indexName, culture));
    }

    [Theory]
    [InlineData("umbraco-qdrant-docs", "umbraco-qdrant-docs", true)]
    [InlineData("umbraco-qdrant-docs-en-us", "umbraco-qdrant-docs", true)]
    [InlineData("umbraco-qdrant-docsite", "umbraco-qdrant-docs", false)]
    public void IsCollectionForIndex_MatchesExactNameOrVariationSuffix(string collectionName, string prefix, bool expected)
    {
        Assert.Equal(expected, InvokeStatic<bool>("IsCollectionForIndex", collectionName, prefix));
    }

    [Fact]
    public void CreatePoint_AddsStablePayloadAndVariationMetadata()
    {
        var point = InvokeStatic<PointStruct>(
            "CreatePoint",
            "Docs",
            "A1",
            "en-US__segment__mobile",
            2,
            new ReadOnlyMemory<float>([1f, 2f]),
            new Dictionary<string, object> { ["snippet"] = "Hello", ["score"] = 3 });

        Assert.Equal("A1", point.Payload["documentId"].StringValue);
        Assert.Equal("en-US", point.Payload["culture"].StringValue);
        Assert.Equal("mobile", point.Payload["segment"].StringValue);
        Assert.Equal("Hello", point.Payload["snippet"].StringValue);
        Assert.Equal(3, point.Payload["score"].IntegerValue);
        Assert.Equal([1f, 2f], point.Vectors.Vector.Dense.Data);
    }

    [Fact]
    public void CreatePoint_UsesDeterministicPointId()
    {
        var first = InvokeStatic<PointStruct>(
            "CreatePoint",
            "Docs",
            "A1",
            "en-US",
            2,
            new ReadOnlyMemory<float>([1f]),
            null);
        var second = InvokeStatic<PointStruct>(
            "CreatePoint",
            " docs ",
            "A1",
            " en-US ",
            2,
            new ReadOnlyMemory<float>([9f]),
            null);

        Assert.Equal(first.Id.Uuid, second.Id.Uuid);
    }

    [Fact]
    public void ToQdrantValue_MapsCommonClrTypes()
    {
        Assert.Equal("Hello", InvokeStatic<Value>("ToQdrantValue", "Hello").StringValue);
        Assert.True(InvokeStatic<Value>("ToQdrantValue", true).BoolValue);
        Assert.Equal(12, InvokeStatic<Value>("ToQdrantValue", 12).IntegerValue);
        Assert.Equal(2.5, InvokeStatic<Value>("ToQdrantValue", 2.5m).DoubleValue);
    }

    [Fact]
    public void FromQdrantValue_MapsPayloadListsRecursively()
    {
        var value = new Value
        {
            ListValue = new ListValue
            {
                Values =
                {
                    new Value { StringValue = "Hello" },
                    new Value { IntegerValue = 3 }
                }
            }
        };

        var result = Assert.IsType<List<object>>(InvokeStatic("FromQdrantValue", value));

        Assert.Equal("Hello", result[0]);
        Assert.Equal(3L, result[1]);
    }

    [Fact]
    public void FromQdrantValue_MapsNullPayloadToNull()
    {
        Assert.Null(InvokeStatic("FromQdrantValue", new Value { NullValue = NullValue.NullValue }));
    }

    [Fact]
    public void CreatePayloadFilter_ReturnsNullForEmptyInput()
    {
        Assert.Null(InvokeStatic("CreatePayloadFilter", [null]));
        Assert.Null(InvokeStatic("CreatePayloadFilter", new Dictionary<string, IReadOnlyCollection<object?>?>()));
    }

    [Fact]
    public void CreatePayloadFilter_CreatesSingleValueMustCondition()
    {
        var filter = Assert.IsType<Filter>(InvokeStatic(
            "CreatePayloadFilter",
            new Dictionary<string, IReadOnlyCollection<object?>?> { ["category"] = ["Docs"] }));

        var condition = Assert.Single(filter.Must);
        Assert.Equal("category", condition.Field.Key);
        Assert.Equal("Docs", condition.Field.Match.Keyword);
    }

    [Fact]
    public void CreatePayloadFilter_CreatesNestedShouldForMultipleValues()
    {
        var filter = Assert.IsType<Filter>(InvokeStatic(
            "CreatePayloadFilter",
            new Dictionary<string, IReadOnlyCollection<object?>?> { ["category"] = ["Docs", "News"] }));

        var condition = Assert.Single(filter.Must);
        Assert.Equal(2, condition.Filter.Should.Count);
        Assert.Contains(condition.Filter.Should, item => item.Field.Match.Keyword == "Docs");
        Assert.Contains(condition.Filter.Should, item => item.Field.Match.Keyword == "News");
    }

    [Fact]
    public void CreatePayloadFilter_IgnoresBlankFieldsNullValuesAndDuplicates()
    {
        var filter = Assert.IsType<Filter>(InvokeStatic(
            "CreatePayloadFilter",
            new Dictionary<string, IReadOnlyCollection<object?>?>
            {
                [""] = ["ignored"],
                ["empty"] = [],
                ["nullable"] = [null],
                ["category"] = ["Docs", "Docs"]
            }));

        var condition = Assert.Single(filter.Must);
        Assert.Equal("category", condition.Field.Key);
        Assert.Equal("Docs", condition.Field.Match.Keyword);
    }

    [Fact]
    public void CreatePayloadMatch_MapsBooleansAndIntegers()
    {
        var boolCondition = InvokeStatic<Condition>("CreatePayloadMatch", "published", true);
        var intCondition = InvokeStatic<Condition>("CreatePayloadMatch", "count", 7);

        Assert.True(boolCondition.Field.Match.Boolean);
        Assert.Equal(7, intCondition.Field.Match.Integer);
    }

    [Fact]
    public void CreatePayloadMatch_MapsDecimalValuesToExactRange()
    {
        var condition = InvokeStatic<Condition>("CreatePayloadMatch", "price", 2.5m);

        Assert.Null(condition.Field.Match);
        Assert.Equal(2.5, condition.Field.Range.Gte);
        Assert.Equal(2.5, condition.Field.Range.Lte);
    }

    private static T InvokeStatic<T>(string methodName, params object?[] args)
    {
        return Assert.IsType<T>(InvokeStatic(methodName, args));
    }

    private static object? InvokeStatic(string methodName, params object?[] args)
    {
        var method = typeof(QdrantVectorStore).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        return method.Invoke(null, args);
    }
}
