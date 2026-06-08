using Fynydd.Umbraco.Search.Qdrant.Searchers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Umbraco.AI.Core.Embeddings;
using Umbraco.AI.Search.Core.Configuration;
using Umbraco.AI.Search.Core.VectorStore;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Models.Searching;

namespace Umbraco.Search.Qdrant.Tests;

public sealed class FilteringAiVectorSearcherTests
{
    [Fact]
    public async Task SearchAsync_ReturnsEmptyResultForBlankQuery()
    {
        var vectorStore = new FakeVectorStore([]);
        var searcher = CreateSearcher(vectorStore);

        var result = await searcher.SearchAsync("index", " ", null, null, null, null, null, null, 0, 10, 0);

        Assert.Equal(0, result.Total);
        Assert.Empty(result.Documents);
        Assert.Equal(0, vectorStore.SearchCalls);
    }

    [Fact]
    public async Task SearchAsync_FiltersByScoreAndAccessThenGroupsByDocument()
    {
        var allowedMemberId = Guid.NewGuid();
        var blockedMemberId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var lowerScoreDuplicate = new AIVectorSearchResult(documentId.ToString("D"), 0.91, new Dictionary<string, object>());
        var vectorStore = new FakeVectorStore(
        [
            new AIVectorSearchResult(documentId.ToString("D"), 0.95, new Dictionary<string, object>()),
            lowerScoreDuplicate,
            new AIVectorSearchResult(Guid.NewGuid().ToString("D"), 0.20, new Dictionary<string, object>()),
            new AIVectorSearchResult(Guid.NewGuid().ToString("D"), 0.99, new Dictionary<string, object> { ["accessIds"] = blockedMemberId.ToString("D") }),
            new AIVectorSearchResult(Guid.NewGuid().ToString("D"), 0.98, new Dictionary<string, object> { ["accessIds"] = allowedMemberId.ToString("D") })
        ]);
        var searcher = CreateSearcher(vectorStore, new AIVectorSearchOptions { DefaultTopK = 25, MinScore = 0.5 });

        var result = await searcher.SearchAsync(
            "index",
            "syntax",
            null,
            null,
            null,
            "en-US",
            "mobile",
            new AccessContext(allowedMemberId, []),
            0,
            10,
            0);

        var documents = result.Documents.ToList();
        Assert.Equal(2, result.Total);
        Assert.Equal(documentId, documents[1].Id);
        Assert.Equal("en-US__segment__mobile", vectorStore.LastCulture);
        Assert.Equal(25, vectorStore.LastTopK);
    }

    [Fact]
    public async Task SearchAsync_AppliesSkipAndTakeAfterGrouping()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var vectorStore = new FakeVectorStore(
        [
            new AIVectorSearchResult(ids[0].ToString("D"), 0.9, new Dictionary<string, object>()),
            new AIVectorSearchResult(ids[1].ToString("D"), 0.8, new Dictionary<string, object>()),
            new AIVectorSearchResult(ids[2].ToString("D"), 0.7, new Dictionary<string, object>())
        ]);
        var searcher = CreateSearcher(vectorStore, new AIVectorSearchOptions { DefaultTopK = 10, MinScore = 0 });

        var result = await searcher.SearchAsync("index", "syntax", null, null, null, null, null, null, 1, 1, 0);

        var document = Assert.Single(result.Documents);
        Assert.Equal(3, result.Total);
        Assert.Equal(ids[1], document.Id);
    }

    [Fact]
    public async Task SearchAsync_UsesObjectTypeMetadataWhenPresent()
    {
        var id = Guid.NewGuid();
        var vectorStore = new FakeVectorStore(
        [
            new AIVectorSearchResult(id.ToString("D"), 0.9, new Dictionary<string, object> { ["objectType"] = nameof(UmbracoObjectTypes.Media) })
        ]);
        var searcher = CreateSearcher(vectorStore);

        var result = await searcher.SearchAsync("index", "syntax", null, null, null, null, null, null, 0, 10, 0);

        var document = Assert.Single(result.Documents);
        Assert.Equal(UmbracoObjectTypes.Media, document.ObjectType);
    }

    [Fact]
    public async Task SearchAsync_DefaultsMissingObjectTypeToDocument()
    {
        var id = Guid.NewGuid();
        var searcher = CreateSearcher(new FakeVectorStore(
        [
            new AIVectorSearchResult(id.ToString("D"), 0.9, new Dictionary<string, object>())
        ]));

        var result = await searcher.SearchAsync("index", "syntax", null, null, null, null, null, null, 0, 10, 0);

        var document = Assert.Single(result.Documents);
        Assert.Equal(UmbracoObjectTypes.Document, document.ObjectType);
    }

    [Fact]
    public async Task SearchAsync_IgnoresInvalidDocumentIds()
    {
        var validId = Guid.NewGuid();
        var searcher = CreateSearcher(new FakeVectorStore(
        [
            new AIVectorSearchResult("not-a-guid", 0.99, new Dictionary<string, object>()),
            new AIVectorSearchResult(validId.ToString("D"), 0.90, new Dictionary<string, object>())
        ]));

        var result = await searcher.SearchAsync("index", "syntax", null, null, null, null, null, null, 0, 10, 0);

        var document = Assert.Single(result.Documents);
        Assert.Equal(validId, document.Id);
    }

    [Fact]
    public async Task SearchAsync_IncludesResultsOnMinScoreBoundary()
    {
        var boundaryId = Guid.NewGuid();
        var belowId = Guid.NewGuid();
        var searcher = CreateSearcher(
            new FakeVectorStore(
            [
                new AIVectorSearchResult(boundaryId.ToString("D"), 0.5, new Dictionary<string, object>()),
                new AIVectorSearchResult(belowId.ToString("D"), 0.49, new Dictionary<string, object>())
            ]),
            new AIVectorSearchOptions { DefaultTopK = 10, MinScore = 0.5 });

        var result = await searcher.SearchAsync("index", "syntax", null, null, null, null, null, null, 0, 10, 0);

        var document = Assert.Single(result.Documents);
        Assert.Equal(boundaryId, document.Id);
    }

    [Fact]
    public async Task SearchAsync_AllowsProtectedResultWhenAccessBypassesProtection()
    {
        var id = Guid.NewGuid();
        var vectorStore = new FakeVectorStore(
        [
            new AIVectorSearchResult(id.ToString("D"), 0.9, new Dictionary<string, object> { ["accessIds"] = Guid.NewGuid().ToString("D") })
        ]);
        var searcher = CreateSearcher(vectorStore);

        var result = await searcher.SearchAsync("index", "syntax", null, null, null, null, null, AccessContext.BypassProtection(), 0, 10, 0);

        var document = Assert.Single(result.Documents);
        Assert.Equal(id, document.Id);
    }

    [Fact]
    public async Task SearchAsync_AllowsProtectedResultForMatchingGroup()
    {
        var id = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var vectorStore = new FakeVectorStore(
        [
            new AIVectorSearchResult(id.ToString("D"), 0.9, new Dictionary<string, object> { ["accessIds"] = groupId.ToString("D") })
        ]);
        var searcher = CreateSearcher(vectorStore);

        var result = await searcher.SearchAsync("index", "syntax", null, null, null, null, null, new AccessContext(Guid.NewGuid(), [groupId]), 0, 10, 0);

        var document = Assert.Single(result.Documents);
        Assert.Equal(id, document.Id);
    }

    [Fact]
    public async Task SearchAsync_TreatsNonStringAccessIdsAsPublic()
    {
        var id = Guid.NewGuid();
        var vectorStore = new FakeVectorStore(
        [
            new AIVectorSearchResult(id.ToString("D"), 0.9, new Dictionary<string, object> { ["accessIds"] = Guid.NewGuid() })
        ]);
        var searcher = CreateSearcher(vectorStore);

        var result = await searcher.SearchAsync("index", "syntax", null, null, null, null, null, null, 0, 10, 0);

        var document = Assert.Single(result.Documents);
        Assert.Equal(id, document.Id);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyResultWhenVectorStoreThrows()
    {
        var searcher = CreateSearcher(new ThrowingVectorStore());

        var result = await searcher.SearchAsync("index", "syntax", null, null, null, null, null, null, 0, 10, 0);

        Assert.Equal(0, result.Total);
        Assert.Empty(result.Documents);
    }

    private static FilteringAiVectorSearcher CreateSearcher(FakeVectorStore vectorStore, AIVectorSearchOptions? options = null) =>
        new(
            vectorStore,
            new FakeEmbeddingService(),
            Options.Create(options ?? new AIVectorSearchOptions { DefaultTopK = 10, MinScore = 0 }),
            NullLogger<FilteringAiVectorSearcher>.Instance);

    private class FakeVectorStore(IReadOnlyList<AIVectorSearchResult> results) : IAIVectorStore
    {
        public int SearchCalls { get; private set; }

        public string? LastCulture { get; private set; }

        public int LastTopK { get; private set; }

        public Task UpsertAsync(string indexName, string documentId, string? culture, int chunkIndex, ReadOnlyMemory<float> vector, IDictionary<string, object>? metadata = null, CancellationToken cancellationToken = new()) => Task.CompletedTask;

        public Task DeleteAsync(string indexName, string documentId, string? culture, CancellationToken cancellationToken = new()) => Task.CompletedTask;

        public Task DeleteDocumentAsync(string indexName, string documentId, CancellationToken cancellationToken = new()) => Task.CompletedTask;

        public virtual Task<IReadOnlyList<AIVectorSearchResult>> SearchAsync(string indexName, ReadOnlyMemory<float> queryVector, string? culture = null, int topK = 10, CancellationToken cancellationToken = new())
        {
            SearchCalls++;
            LastCulture = culture;
            LastTopK = topK;

            return Task.FromResult(results);
        }

        public Task<IReadOnlyList<AIVectorEntry>> GetVectorsByDocumentAsync(string indexName, string documentId, string? culture = null, CancellationToken cancellationToken = new()) =>
            Task.FromResult<IReadOnlyList<AIVectorEntry>>([]);

        public Task ResetAsync(string indexName, CancellationToken cancellationToken = new()) => Task.CompletedTask;

        public Task<long> GetDocumentCountAsync(string indexName, CancellationToken cancellationToken = new()) => Task.FromResult(0L);
    }

    private sealed class ThrowingVectorStore() : FakeVectorStore([])
    {
        public override Task<IReadOnlyList<AIVectorSearchResult>> SearchAsync(string indexName, ReadOnlyMemory<float> queryVector, string? culture = null, int topK = 10, CancellationToken cancellationToken = new()) =>
            throw new InvalidOperationException("Search failed.");
    }

    private sealed class FakeEmbeddingService : IAIEmbeddingService
    {
        public Task<Embedding<float>> GenerateEmbeddingAsync(string text, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Embedding<float>(new[] { 1f, 2f, 3f }));

        public Task<Embedding<float>> GenerateEmbeddingAsync(Guid profileId, string text, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            GenerateEmbeddingAsync(text, options, cancellationToken);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateEmbeddingsAsync(IEnumerable<string> texts, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(texts.Select(_ => new Embedding<float>(new[] { 1f, 2f, 3f }))));

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateEmbeddingsAsync(Guid profileId, IEnumerable<string> texts, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            GenerateEmbeddingsAsync(texts, options, cancellationToken);

        public Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingGeneratorAsync(Guid? profileId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Embedding<float>> GenerateEmbeddingAsync(Action<AIEmbeddingBuilder> builder, string text, CancellationToken cancellationToken = default) =>
            GenerateEmbeddingAsync(text, null, cancellationToken);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateEmbeddingsAsync(Action<AIEmbeddingBuilder> builder, IEnumerable<string> texts, CancellationToken cancellationToken = default) =>
            GenerateEmbeddingsAsync(texts, null, cancellationToken);

        public Task<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGeneratorAsync(Action<AIEmbeddingBuilder> builder, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
