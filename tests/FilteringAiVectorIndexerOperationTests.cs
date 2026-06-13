using Fynydd.Umbraco.Search.Qdrant.Indexers;
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

    private static FilteringAiVectorIndexer CreateIndexer(IAIVectorStore vectorStore) => new(
        vectorStore,
        null!,
        null!,
        null!,
        null,
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

        public Task UpsertAsync(string indexName, string documentId, string? culture, int chunkIndex, ReadOnlyMemory<float> vector, IDictionary<string, object>? metadata = null, CancellationToken cancellationToken = new()) =>
            Task.CompletedTask;

        public Task DeleteAsync(string indexName, string documentId, string? culture, CancellationToken cancellationToken = new()) =>
            Task.CompletedTask;

        public Task DeleteDocumentAsync(string indexName, string documentId, CancellationToken cancellationToken = new())
        {
            DeletedDocumentIds.Add(documentId);

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AIVectorSearchResult>> SearchAsync(string indexName, ReadOnlyMemory<float> queryVector, string? culture = null, int topK = 10, CancellationToken cancellationToken = new()) =>
            Task.FromResult<IReadOnlyList<AIVectorSearchResult>>([]);

        public Task<IReadOnlyList<AIVectorEntry>> GetVectorsByDocumentAsync(string indexName, string documentId, string? culture = null, CancellationToken cancellationToken = new()) =>
            Task.FromResult<IReadOnlyList<AIVectorEntry>>([]);

        public Task ResetAsync(string indexName, CancellationToken cancellationToken = new())
        {
            ResetIndexName = indexName;

            return Task.CompletedTask;
        }

        public Task<long> GetDocumentCountAsync(string indexName, CancellationToken cancellationToken = new()) =>
            Task.FromResult(DocumentCount);
    }
}
