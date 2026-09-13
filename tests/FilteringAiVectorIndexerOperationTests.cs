using Fynydd.Umbraco.Search.Qdrant.Indexers;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Umbraco.AI.Search.Core.Configuration;
using Umbraco.AI.Search.Core.VectorStore;
using Umbraco.Cms.Search.Core.Models.Indexing;

namespace Umbraco.Search.Qdrant.Tests;

public sealed class FilteringAiVectorIndexerOperationTests
{
    [Fact]
    public async Task DeleteAsync_DeletesEachDocumentId()
    {
        var vectorStore = new RecordingVectorStore();
        var indexer = CreateIndexer(vectorStore);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

        await indexer.DeleteAsync("index", ids);

        Assert.Equal(ids.Select(id => id.ToString("D")), vectorStore.DeletedDocumentIds);
    }

    [Fact]
    public async Task ResetAsync_ResetsVectorStoreIndex()
    {
        var vectorStore = new RecordingVectorStore();
        var indexer = CreateIndexer(vectorStore);

        await indexer.ResetAsync("index");

        Assert.Equal("index", vectorStore.ResetIndexName);
    }

    [Fact]
    public async Task GetMetadataAsync_ReturnsHealthyProviderMetadata()
    {
        var vectorStore = new RecordingVectorStore { DocumentCount = 42 };
        var indexer = CreateIndexer(vectorStore);

        var metadata = await indexer.GetMetadataAsync("index");

        Assert.Equal(42, metadata.DocumentCount);
        Assert.Equal(HealthStatus.Healthy, metadata.HealthStatus);
        Assert.Equal("ai-vector-search-provider", metadata.ProviderName);
    }

    [Fact]
    public async Task DeleteAsync_VectorStoreTimeout_CompletesWithoutEscaping()
    {
        var id = Guid.NewGuid();
        var vectorStore = new RecordingVectorStore
        {
            DeleteDocumentException = new TimeoutException("Qdrant timed out.")
        };
        var indexer = CreateIndexer(vectorStore);

        await indexer.DeleteAsync("index", [id]);

        Assert.Equal([id.ToString("D")], vectorStore.DeletedDocumentIds);
    }

    [Fact]
    public async Task ResetAsync_QdrantConnectionFailure_CompletesWithoutEscaping()
    {
        var vectorStore = new RecordingVectorStore
        {
            ResetException = new HttpRequestException("Qdrant connection failed.")
        };
        var indexer = CreateIndexer(vectorStore);

        await indexer.ResetAsync("index");

        Assert.Equal("index", vectorStore.ResetIndexName);
    }

    [Fact]
    public async Task ResetAsync_NestedSocketFailure_CompletesWithoutEscaping()
    {
        var vectorStore = new RecordingVectorStore
        {
            ResetException = new InvalidOperationException(
                "Qdrant transport failed.",
                new System.Net.Sockets.SocketException())
        };
        var indexer = CreateIndexer(vectorStore);

        await indexer.ResetAsync("index");

        Assert.Equal("index", vectorStore.ResetIndexName);
    }

    [Fact]
    public async Task GetMetadataAsync_QdrantCancelled_ReturnsUnknownMetadata()
    {
        var vectorStore = new RecordingVectorStore
        {
            DocumentCountException = new RpcException(new Status(StatusCode.Cancelled, "Qdrant cancelled the request."))
        };
        var indexer = CreateIndexer(vectorStore);

        var metadata = await indexer.GetMetadataAsync("index");

        Assert.Equal(0, metadata.DocumentCount);
        Assert.Equal(HealthStatus.Unknown, metadata.HealthStatus);
        Assert.Equal("ai-vector-search-provider", metadata.ProviderName);
    }

    [Fact]
    public async Task DeleteAsync_NonTransientFailure_Propagates()
    {
        var vectorStore = new RecordingVectorStore
        {
            DeleteDocumentException = new InvalidOperationException("Programming error.")
        };
        var indexer = CreateIndexer(vectorStore);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            indexer.DeleteAsync("index", [Guid.NewGuid()]));

        Assert.Equal("Programming error.", exception.Message);
    }

    [Fact]
    public async Task ResetAsync_NonTransientFailure_Propagates()
    {
        var vectorStore = new RecordingVectorStore
        {
            ResetException = new InvalidOperationException("Programming error.")
        };
        var indexer = CreateIndexer(vectorStore);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            indexer.ResetAsync("index"));

        Assert.Equal("Programming error.", exception.Message);
    }

    [Fact]
    public async Task GetMetadataAsync_NonTransientFailure_Propagates()
    {
        var vectorStore = new RecordingVectorStore
        {
            DocumentCountException = new InvalidOperationException("Programming error.")
        };
        var indexer = CreateIndexer(vectorStore);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            indexer.GetMetadataAsync("index"));

        Assert.Equal("Programming error.", exception.Message);
    }

    private static FilteringAiVectorIndexer CreateIndexer(IAIVectorStore vectorStore) => new(
        vectorStore,
        null!,
        null!,
        null!,
        null!,
        null!,
        null,
        null!,
        null!,
        null!,
        null!,
        Options.Create(new AIVectorSearchOptions()),
        Options.Create(new AiSearchIndexFilterOptions()),
        NullLogger<FilteringAiVectorIndexer>.Instance);

    private sealed class RecordingVectorStore : IAIVectorStore
    {
        public List<string> DeletedDocumentIds { get; } = [];

        public string? ResetIndexName { get; private set; }

        public long DocumentCount { get; init; }

        public Exception? DeleteDocumentException { get; init; }

        public Exception? ResetException { get; init; }

        public Exception? DocumentCountException { get; init; }

        public Task UpsertAsync(string indexName, string documentId, string? culture, int chunkIndex, ReadOnlyMemory<float> vector, IDictionary<string, object>? metadata = null, CancellationToken cancellationToken = new()) =>
            Task.CompletedTask;

        public Task DeleteAsync(string indexName, string documentId, string? culture, CancellationToken cancellationToken = new()) =>
            Task.CompletedTask;

        public Task DeleteDocumentAsync(string indexName, string documentId, CancellationToken cancellationToken = new())
        {
            DeletedDocumentIds.Add(documentId);

            return DeleteDocumentException is null
                ? Task.CompletedTask
                : Task.FromException(DeleteDocumentException);
        }

        public Task<IReadOnlyList<AIVectorSearchResult>> SearchAsync(string indexName, ReadOnlyMemory<float> queryVector, string? culture = null, int topK = 10, CancellationToken cancellationToken = new()) =>
            Task.FromResult<IReadOnlyList<AIVectorSearchResult>>([]);

        public Task<IReadOnlyList<AIVectorEntry>> GetVectorsByDocumentAsync(string indexName, string documentId, string? culture = null, CancellationToken cancellationToken = new()) =>
            Task.FromResult<IReadOnlyList<AIVectorEntry>>([]);

        public Task ResetAsync(string indexName, CancellationToken cancellationToken = new())
        {
            ResetIndexName = indexName;

            return ResetException is null
                ? Task.CompletedTask
                : Task.FromException(ResetException);
        }

        public Task<long> GetDocumentCountAsync(string indexName, CancellationToken cancellationToken = new()) =>
            DocumentCountException is null
                ? Task.FromResult(DocumentCount)
                : Task.FromException<long>(DocumentCountException);
    }
}
