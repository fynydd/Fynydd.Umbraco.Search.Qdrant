using Fynydd.Umbraco.Search.Qdrant.Indexers;
using Fynydd.Umbraco.Search.Qdrant.VectorStores;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Umbraco.AI.Search.Core.VectorStore;
// ReSharper disable RedundantArgumentDefaultValue

namespace Umbraco.Search.Qdrant.Tests;

public sealed class QdrantVectorStoreIntegrationTests : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder()
        .WithImage("qdrant/qdrant")
        .WithPortBinding(6334, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6334))
        .Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task InitializeAsync_CreatesDefaultCollection()
    {
        var store = CreateStore(out var client);

        await store.InitializeAsync();

        var collections = await client.ListCollectionsAsync();
        Assert.Contains("umbraco-qdrant-umbai_search", collections);
    }

    [Fact]
    public async Task InitializeAsync_PreservesCollectionWhenVectorSizeChanges()
    {
        var indexName = UniqueIndexName();
        var store = CreateStore(out var client, options =>
        {
            options.DisableDefaultIndex = true;
            options.Categories["docs"] = new SearchCategory { IndexAlias = indexName };
        });
        var collectionName = CollectionName(indexName);

        await client.CreateCollectionAsync(
            collectionName,
            new VectorParams { Size = 2, Distance = Distance.Cosine });

        await store.InitializeAsync();

        var info = await client.GetCollectionInfoAsync(collectionName);
        Assert.Equal(2UL, info.Config?.Params?.VectorsConfig?.Params?.Size);
    }

    [Fact]
    public async Task InitializeAsync_RemovesOrphanedPrefixedCollectionsWhenEnabled()
    {
        var indexName = UniqueIndexName();
        var orphanName = CollectionName(UniqueIndexName());
        var store = CreateStore(out var client, options =>
        {
            options.DisableDefaultIndex = true;
            options.Connection.RemoveOrphanedCollections = true;
            options.Categories["docs"] = new SearchCategory { IndexAlias = indexName };
        });

        await client.CreateCollectionAsync(
            orphanName,
            new VectorParams { Size = 3, Distance = Distance.Cosine });

        await store.InitializeAsync();

        var collections = await client.ListCollectionsAsync();
        Assert.Contains(CollectionName(indexName), collections);
        Assert.DoesNotContain(orphanName, collections);
    }

    [Fact]
    public async Task InitializeAsync_PreservesOrphanedPrefixedCollectionsWhenCleanupIsDisabled()
    {
        var orphanName = CollectionName(UniqueIndexName());
        var store = CreateStore(out var client, options =>
        {
            options.DisableDefaultIndex = true;
            options.Connection.RemoveOrphanedCollections = false;
            options.Categories["docs"] = new SearchCategory { IndexAlias = UniqueIndexName() };
        });

        await client.CreateCollectionAsync(
            orphanName,
            new VectorParams { Size = 3, Distance = Distance.Cosine });

        await store.InitializeAsync();

        var collections = await client.ListCollectionsAsync();
        Assert.Contains(orphanName, collections);
    }

    [Fact]
    public async Task UpsertSearchAndDeleteDocument_WorkAgainstQdrant()
    {
        var store = CreateStore(out _);
        var indexName = UniqueIndexName();
        var documentId = Guid.NewGuid().ToString("D");

        await store.UpsertManyAsync(
            indexName,
            documentId,
            [
                new AIVectorEntry(
                    documentId,
                    "en-US",
                    0,
                    new ReadOnlyMemory<float>([1f, 0f, 0f]),
                    new Dictionary<string, object>
                    {
                        ["chunkIndex"] = 0,
                        ["category"] = "Docs",
                        ["snippet"] = "Plain snippet"
                    })
            ]);

        var results = await store.SearchAsync(
            indexName,
            new ReadOnlyMemory<float>([1f, 0f, 0f]),
            "en-US",
            10,
            new Dictionary<string, IReadOnlyCollection<object?>?> { ["category"] = ["Docs"] });

        var result = Assert.Single(results);
        Assert.Equal(documentId, result.DocumentId);
        Assert.NotNull(result.Metadata);
        Assert.Equal("Plain snippet", result.Metadata["snippet"]);

        await store.DeleteDocumentAsync(indexName, documentId);

        var afterDelete = await store.SearchAsync(indexName, new ReadOnlyMemory<float>([1f, 0f, 0f]), "en-US", 10);
        Assert.Empty(afterDelete);
    }

    [Fact]
    public async Task GetVectorsByDocument_ReturnsStoredChunksAcrossCollections()
    {
        var store = CreateStore(out _);
        var indexName = UniqueIndexName();
        var documentId = Guid.NewGuid().ToString("D");

        await store.UpsertManyAsync(
            indexName,
            documentId,
            [
                new AIVectorEntry(documentId, null, 1, new ReadOnlyMemory<float>([0f, 1f, 0f]), new Dictionary<string, object> { ["chunkIndex"] = 1 }),
                new AIVectorEntry(documentId, "en-US", 0, new ReadOnlyMemory<float>([1f, 0f, 0f]), new Dictionary<string, object> { ["chunkIndex"] = 0 })
            ]);

        var entries = await store.GetVectorsByDocumentAsync(indexName, documentId);

        Assert.Equal([0, 1], entries.Select(entry => entry.ChunkIndex));
        Assert.All(entries, entry => Assert.Equal(documentId, entry.DocumentId));
    }

    [Fact]
    public async Task ResetAsync_ClearsExistingIndexCollections()
    {
        var store = CreateStore(out _);
        var indexName = UniqueIndexName();
        var documentId = Guid.NewGuid().ToString("D");

        await store.UpsertAsync(indexName, documentId, null, 0, new ReadOnlyMemory<float>([1f, 0f, 0f]));

        await store.ResetAsync(indexName);

        var results = await store.SearchAsync(indexName, new ReadOnlyMemory<float>([1f, 0f, 0f]), null, 10);
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_MergesVariationFallbackCollectionsAndOrdersByScore()
    {
        var store = CreateStore(out _);
        var indexName = UniqueIndexName();
        var cultureDocumentId = Guid.NewGuid().ToString("D");
        var invariantDocumentId = Guid.NewGuid().ToString("D");

        await store.UpsertManyAsync(
            indexName,
            cultureDocumentId,
            [
                new AIVectorEntry(cultureDocumentId, "en-US", 0, new ReadOnlyMemory<float>([0.9f, 0.1f, 0f]), new Dictionary<string, object> { ["chunkIndex"] = 0 })
            ]);
        await store.UpsertManyAsync(
            indexName,
            invariantDocumentId,
            [
                new AIVectorEntry(invariantDocumentId, null, 0, new ReadOnlyMemory<float>([1f, 0f, 0f]), new Dictionary<string, object> { ["chunkIndex"] = 0 })
            ]);

        var results = await store.SearchAsync(indexName, new ReadOnlyMemory<float>([1f, 0f, 0f]), "en-US", 10);

        Assert.Equal([invariantDocumentId, cultureDocumentId], results.Select(result => result.DocumentId));
    }

    [Fact]
    public async Task SearchAsync_AppliesTopKAfterMergingFallbackCollections()
    {
        var store = CreateStore(out _);
        var indexName = UniqueIndexName();
        var highId = Guid.NewGuid().ToString("D");
        var middleId = Guid.NewGuid().ToString("D");
        var lowId = Guid.NewGuid().ToString("D");

        await store.UpsertAsync(indexName, highId, null, 0, new ReadOnlyMemory<float>([1f, 0f, 0f]));
        await store.UpsertAsync(indexName, middleId, "en-US", 0, new ReadOnlyMemory<float>([0.8f, 0.2f, 0f]));
        await store.UpsertAsync(indexName, lowId, "en-US__segment__mobile", 0, new ReadOnlyMemory<float>([0f, 1f, 0f]));

        var results = await store.SearchAsync(indexName, new ReadOnlyMemory<float>([1f, 0f, 0f]), "en-US__segment__mobile", 2);

        Assert.Equal([highId, middleId], results.Select(result => result.DocumentId));
    }

    [Fact]
    public async Task SearchAsync_SearchesSpecificSegmentCollection()
    {
        var store = CreateStore(out _);
        var indexName = UniqueIndexName();
        var segmentDocumentId = Guid.NewGuid().ToString("D");

        await store.UpsertManyAsync(
            indexName,
            segmentDocumentId,
            [
                new AIVectorEntry(segmentDocumentId, "en-US__segment__mobile", 0, new ReadOnlyMemory<float>([1f, 0f, 0f]), new Dictionary<string, object> { ["chunkIndex"] = 0 })
            ]);

        var results = await store.SearchAsync(indexName, new ReadOnlyMemory<float>([1f, 0f, 0f]), "en-US__segment__mobile", 10);

        var result = Assert.Single(results);
        Assert.Equal(segmentDocumentId, result.DocumentId);
        Assert.Equal("mobile", result.Metadata?["segment"]);
    }

    [Fact]
    public async Task DeleteAsync_RemovesOnlyRequestedVariationCollection()
    {
        var store = CreateStore(out _);
        var indexName = UniqueIndexName();
        var documentId = Guid.NewGuid().ToString("D");

        await store.UpsertManyAsync(
            indexName,
            documentId,
            [
                new AIVectorEntry(documentId, null, 0, new ReadOnlyMemory<float>([1f, 0f, 0f]), new Dictionary<string, object> { ["chunkIndex"] = 0 }),
                new AIVectorEntry(documentId, "en-US", 0, new ReadOnlyMemory<float>([1f, 0f, 0f]), new Dictionary<string, object> { ["chunkIndex"] = 0 })
            ]);

        await store.DeleteAsync(indexName, documentId, "en-US");

        var cultureResults = await store.SearchAsync(indexName, new ReadOnlyMemory<float>([1f, 0f, 0f]), "en-US", 10);
        var invariantResults = await store.SearchAsync(indexName, new ReadOnlyMemory<float>([1f, 0f, 0f]), null, 10);

        Assert.Single(cultureResults);
        Assert.Single(invariantResults);
        Assert.All(cultureResults, result =>
        {
            Assert.NotNull(result.Metadata);
            Assert.False(result.Metadata.ContainsKey("culture"));
        });
    }

    [Fact]
    public async Task GetDocumentCountAsync_CountsDistinctDocumentsAcrossVariationCollections()
    {
        var store = CreateStore(out _);
        var indexName = UniqueIndexName();
        var firstDocumentId = Guid.NewGuid().ToString("D");
        var secondDocumentId = Guid.NewGuid().ToString("D");

        await store.UpsertManyAsync(
            indexName,
            firstDocumentId,
            [
                new AIVectorEntry(firstDocumentId, null, 0, new ReadOnlyMemory<float>([1f, 0f, 0f]), new Dictionary<string, object> { ["chunkIndex"] = 0 }),
                new AIVectorEntry(firstDocumentId, "en-US", 0, new ReadOnlyMemory<float>([1f, 0f, 0f]), new Dictionary<string, object> { ["chunkIndex"] = 0 })
            ]);
        await store.UpsertAsync(indexName, secondDocumentId, "fr-FR", 0, new ReadOnlyMemory<float>([0f, 1f, 0f]));

        var count = await store.GetDocumentCountAsync(indexName);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task GetDocumentCountAsync_PagesThroughLargeCollections()
    {
        var store = CreateStore(out var client);
        var indexName = UniqueIndexName();
        var collectionName = CollectionName(indexName);

        await client.CreateCollectionAsync(
            collectionName,
            new VectorParams { Size = 3, Distance = Distance.Cosine });
        await client.UpsertAsync(
            collectionName,
            Enumerable.Range(0, 1001)
                .Select(_ =>
                {
                    var documentId = Guid.NewGuid().ToString("D");

                    return new PointStruct
                    {
                        Id = Guid.NewGuid(),
                        Vectors = new[] { 1f, 0f, 0f },
                        Payload =
                        {
                            ["documentId"] = documentId
                        }
                    };
                })
                .ToList());

        var count = await store.GetDocumentCountAsync(indexName);

        Assert.Equal(1001, count);
    }

    [Fact]
    public async Task UpsertAsync_ConcurrentWritesForSameDocumentRemainSearchable()
    {
        var store = CreateStore(out _);
        var indexName = UniqueIndexName();
        var documentId = Guid.NewGuid().ToString("D");

        await Task.WhenAll(Enumerable.Range(0, 10).Select(chunkIndex =>
            store.UpsertAsync(indexName, documentId, null, chunkIndex, new ReadOnlyMemory<float>([1f, 0f, 0f]))));

        var entries = await store.GetVectorsByDocumentAsync(indexName, documentId);

        Assert.Equal(10, entries.Count);
        Assert.Equal(Enumerable.Range(0, 10), entries.Select(entry => entry.ChunkIndex));
    }

    [Fact]
    public async Task UpsertAsync_RecreatesCollectionWhenItDisappearsAfterBeingCached()
    {
        var store = CreateStore(out var client);
        var indexName = UniqueIndexName();
        var collectionName = CollectionName(indexName);
        var firstDocumentId = Guid.NewGuid().ToString("D");
        var secondDocumentId = Guid.NewGuid().ToString("D");

        await store.UpsertAsync(indexName, firstDocumentId, null, 0, new ReadOnlyMemory<float>([1f, 0f, 0f]));
        await client.DeleteCollectionAsync(collectionName, TimeSpan.FromSeconds(300));
        await store.UpsertAsync(indexName, secondDocumentId, null, 0, new ReadOnlyMemory<float>([1f, 0f, 0f]));

        var entries = await store.GetVectorsByDocumentAsync(indexName, secondDocumentId);

        Assert.Single(entries);
    }

    [Fact]
    public async Task ResetAsync_ClearsSegmentCollections()
    {
        var store = CreateStore(out _);
        var indexName = UniqueIndexName();
        var documentId = Guid.NewGuid().ToString("D");

        await store.UpsertAsync(indexName, documentId, "en-US__segment__mobile", 0, new ReadOnlyMemory<float>([1f, 0f, 0f]));

        await store.ResetAsync(indexName);

        var results = await store.SearchAsync(indexName, new ReadOnlyMemory<float>([1f, 0f, 0f]), "en-US__segment__mobile", 10);
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyWhenCollectionsDoNotExist()
    {
        var store = CreateStore(out _);

        var results = await store.SearchAsync(UniqueIndexName(), new ReadOnlyMemory<float>([1f, 0f, 0f]), null, 10);

        Assert.Empty(results);
    }

    [Fact]
    public async Task DeleteAsync_ReturnsWhenCollectionDoesNotExist()
    {
        var store = CreateStore(out _);

        await store.DeleteAsync(UniqueIndexName(), Guid.NewGuid().ToString("D"), "en-US");
    }

    [Fact]
    public async Task SearchAsync_PropagatesCancelledToken()
    {
        var store = CreateStore(out _);
        using var cancellationTokenSource = new CancellationTokenSource();

        await cancellationTokenSource.CancelAsync();

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            store.SearchAsync(UniqueIndexName(), new ReadOnlyMemory<float>([1f, 0f, 0f]), null, 10, cancellationTokenSource.Token));

        Assert.Equal(StatusCode.Cancelled, exception.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_ThrowsWhenQueryVectorDimensionDoesNotMatchCollection()
    {
        var store = CreateStore(out _);
        var indexName = UniqueIndexName();
        var documentId = Guid.NewGuid().ToString("D");

        await store.UpsertAsync(indexName, documentId, null, 0, new ReadOnlyMemory<float>([1f, 0f, 0f]));

        await Assert.ThrowsAsync<Grpc.Core.RpcException>(() =>
            store.SearchAsync(indexName, new ReadOnlyMemory<float>([1f, 0f]), null, 10));
    }

    [Fact]
    public async Task SearchAsync_FiltersRealQdrantPayloadsByBoolIntLongAndDecimalValues()
    {
        var store = CreateStore(out _);
        var indexName = UniqueIndexName();
        var matchingId = Guid.NewGuid().ToString("D");
        var wrongId = Guid.NewGuid().ToString("D");

        await store.UpsertManyAsync(
            indexName,
            matchingId,
            [
                new AIVectorEntry(
                    matchingId,
                    null,
                    0,
                    new ReadOnlyMemory<float>([1f, 0f, 0f]),
                    new Dictionary<string, object>
                    {
                        ["chunkIndex"] = 0,
                        ["published"] = true,
                        ["count"] = 7,
                        ["tenantId"] = 42L,
                        ["price"] = 2.5m
                    })
            ]);
        await store.UpsertManyAsync(
            indexName,
            wrongId,
            [
                new AIVectorEntry(
                    wrongId,
                    null,
                    0,
                    new ReadOnlyMemory<float>([1f, 0f, 0f]),
                    new Dictionary<string, object>
                    {
                        ["chunkIndex"] = 0,
                        ["published"] = false,
                        ["count"] = 7,
                        ["tenantId"] = 42L,
                        ["price"] = 2.5m
                    })
            ]);

        var results = await store.SearchAsync(
            indexName,
            new ReadOnlyMemory<float>([1f, 0f, 0f]),
            null,
            10,
            new Dictionary<string, IReadOnlyCollection<object?>?>
            {
                ["published"] = [true],
                ["count"] = [7],
                ["tenantId"] = [42L],
                ["price"] = [2.5m]
            });

        var result = Assert.Single(results);
        Assert.Equal(matchingId, result.DocumentId);
    }

    [Fact]
    public async Task SearchAsync_RoundTripsNullAndDatePayloads()
    {
        var store = CreateStore(out _);
        var indexName = UniqueIndexName();
        var documentId = Guid.NewGuid().ToString("D");
        var publishedAt = new DateTimeOffset(2026, 6, 7, 12, 30, 0, TimeSpan.Zero);

        await store.UpsertManyAsync(
            indexName,
            documentId,
            [
                new AIVectorEntry(
                    documentId,
                    null,
                    0,
                    new ReadOnlyMemory<float>([1f, 0f, 0f]),
                    new Dictionary<string, object>
                    {
                        ["chunkIndex"] = 0,
                        ["optional"] = null!,
                        ["publishedAt"] = publishedAt
                    })
            ]);

        var results = await store.SearchAsync(
            indexName,
            new ReadOnlyMemory<float>([1f, 0f, 0f]),
            null,
            10,
            new Dictionary<string, IReadOnlyCollection<object?>?> { ["publishedAt"] = [publishedAt.ToString("O")] });

        var result = Assert.Single(results);
        Assert.True(result.Metadata?.ContainsKey("optional"));
        Assert.Null(result.Metadata?["optional"]);
        Assert.Equal(publishedAt.ToString("O"), result.Metadata?["publishedAt"]);
    }

    private QdrantVectorStore CreateStore(out QdrantClient client, Action<AiSearchIndexFilterOptions>? configure = null)
    {
        client = new QdrantClient("localhost", _container.GetMappedPublicPort(6334));
        var options = new AiSearchIndexFilterOptions
        {
            DisableDefaultIndex = false,
            Connection = new QdrantConnectionOptions
            {
                ServerPort = _container.GetMappedPublicPort(6334),
                EmbeddingSize = 3
            }
        };

        configure?.Invoke(options);

        return new QdrantVectorStore(
            client,
            Options.Create(options),
            NullLogger<QdrantVectorStore>.Instance);
    }

    private static string UniqueIndexName() => "test_" + Guid.NewGuid().ToString("N");

    private static string CollectionName(string indexName) => "umbraco-qdrant-" + indexName.ToLowerInvariant();
}
