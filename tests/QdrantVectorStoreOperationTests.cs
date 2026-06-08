using Argentini.Umbraco.Search.Qdrant.Indexers;
using Argentini.Umbraco.Search.Qdrant.VectorStores;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Umbraco.AI.Search.Core.VectorStore;

namespace Umbraco.Search.Qdrant.Tests;

public sealed class QdrantVectorStoreOperationTests
{
    [Fact]
    public async Task UpsertAsync_ReturnsWithoutQdrantCallWhenIndexIsUnknown()
    {
        var store = CreateOfflineStore(new AiSearchIndexFilterOptions { DisableDefaultIndex = true });

        await store.UpsertAsync("unknown", "document", null, 0, new ReadOnlyMemory<float>([1f, 0f, 0f]));
    }

    [Fact]
    public async Task UpsertManyAsync_ReturnsWithoutQdrantCallForEmptyBatch()
    {
        var store = CreateOfflineStore(new AiSearchIndexFilterOptions
        {
            DisableDefaultIndex = false,
            Connection = { EmbeddingSize = 3 }
        });

        await store.UpsertManyAsync("UmbAI_Search", "document", []);
    }

    [Fact]
    public async Task UpsertManyAsync_ThrowsWhenEntryVectorDimensionDoesNotMatch()
    {
        var store = CreateOfflineStore(new AiSearchIndexFilterOptions
        {
            DisableDefaultIndex = false,
            Connection = { EmbeddingSize = 3 }
        });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.UpsertManyAsync(
                "UmbAI_Search",
                "document",
                [new AIVectorEntry("document", null, 0, new ReadOnlyMemory<float>([1f, 0f]), null)]));
    }

    private static QdrantVectorStore CreateOfflineStore(AiSearchIndexFilterOptions options) =>
        new(
            new QdrantClient("127.0.0.1", 1),
            Options.Create(options),
            NullLogger<QdrantVectorStore>.Instance);
}
