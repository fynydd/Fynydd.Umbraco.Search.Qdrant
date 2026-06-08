using Argentini.Umbraco.Search.Qdrant.Indexers;
using Argentini.Umbraco.Search.Qdrant.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.AI.Core.Embeddings;
using Umbraco.AI.Core.Models;
using Umbraco.AI.Core.Profiles;
using Umbraco.AI.Search.Core.Chunking;
using Umbraco.AI.Search.Core.Configuration;
using Umbraco.AI.Search.Core.VectorStore;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Search.Core.Models.Indexing;

namespace Umbraco.Search.Qdrant.Tests;

public sealed class FilteringAiVectorIndexerAddOrUpdateTests
{
    [Fact]
    public async Task AddOrUpdateAsync_StripsHtmlSnippetAndWritesVector()
    {
        var harness = CreateHarness();
        var contentTypeKey = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var fields = CreateFields(contentTypeKey, "<p>Hello <strong>syntax</strong></p>");

        await harness.Indexer.AddOrUpdateAsync("index", documentId, UmbracoObjectTypes.Document, [new Variation("en-US", null)], fields, null);

        var upsert = Assert.Single(harness.VectorStore.Upserts);
        Assert.Equal(documentId.ToString("D"), upsert.DocumentId);
        Assert.Equal("en-US", upsert.Culture);
        Assert.NotNull(upsert.Metadata);
        var snippet = Assert.IsType<string>(upsert.Metadata["snippet"]);
        Assert.Equal("Hello **syntax**", snippet);
        Assert.DoesNotContain('<', snippet);
        Assert.Equal("Document", upsert.Metadata["objectType"]);
        Assert.Equal("en-US", upsert.Metadata["culture"]);
    }

    [Fact]
    public async Task AddOrUpdateAsync_AddsProtectionMetadata()
    {
        var harness = CreateHarness();
        var accessIds = new[] { Guid.NewGuid(), Guid.NewGuid() };

        await harness.Indexer.AddOrUpdateAsync(
            "index",
            Guid.NewGuid(),
            UmbracoObjectTypes.Document,
            [new Variation(null, null)],
            CreateFields(Guid.NewGuid(), "Secret text"),
            new ContentProtection(accessIds));

        var upsert = Assert.Single(harness.VectorStore.Upserts);
        Assert.NotNull(upsert.Metadata);
        Assert.Equal(string.Join(",", accessIds), upsert.Metadata["accessIds"]);
    }

    [Fact]
    public async Task AddOrUpdateAsync_DoesNotReplaceVectorsWhenEmbeddingCountMismatches()
    {
        var harness = CreateHarness(embeddingCount: 0);

        await harness.Indexer.AddOrUpdateAsync(
            "index",
            Guid.NewGuid(),
            UmbracoObjectTypes.Document,
            [new Variation(null, null)],
            CreateFields(Guid.NewGuid(), "Text"),
            null);

        Assert.Empty(harness.VectorStore.DeletedDocuments);
        Assert.Empty(harness.VectorStore.Upserts);
    }

    [Fact]
    public async Task AddOrUpdateAsync_SkipsWhenNoDefaultProfileExists()
    {
        var harness = CreateHarness(hasDefaultProfile: false);

        await harness.Indexer.AddOrUpdateAsync(
            "index",
            Guid.NewGuid(),
            UmbracoObjectTypes.Document,
            [new Variation(null, null)],
            CreateFields(Guid.NewGuid(), "Text"),
            null);

        Assert.Empty(harness.VectorStore.DeletedDocuments);
        Assert.Empty(harness.VectorStore.Upserts);
    }

    [Fact]
    public async Task AddOrUpdateAsync_UsesMediaCacheForMediaItems()
    {
        var harness = CreateHarness();
        var mediaId = Guid.NewGuid();

        await harness.Indexer.AddOrUpdateAsync(
            "index",
            mediaId,
            UmbracoObjectTypes.Media,
            [new Variation(null, null)],
            CreateFields(Guid.NewGuid(), "Media text"),
            null);

        harness.MediaCache.Received(1).GetById(mediaId);
        harness.ContentCache.DidNotReceive().GetById(mediaId);
        Assert.Single(harness.VectorStore.Upserts);
    }

    [Fact]
    public async Task AddOrUpdateAsync_DoesNotReplaceVectorsWhenOneVariationEmbeddingFails()
    {
        var harness = CreateHarness(throwOnEmbeddingCall: 2);

        await harness.Indexer.AddOrUpdateAsync(
            "index",
            Guid.NewGuid(),
            UmbracoObjectTypes.Document,
            [new Variation("en-US", null), new Variation("da-DK", null)],
            [
                new IndexField("Umb_ContentTypeId", new IndexValue { Keywords = [Guid.NewGuid().ToString("D")] }, null, null),
                new IndexField("body", new IndexValue { Texts = ["English"] }, "en-US", null),
                new IndexField("body", new IndexValue { Texts = ["Danish"] }, "da-DK", null)
            ],
            null);

        Assert.Empty(harness.VectorStore.DeletedDocuments);
        Assert.Empty(harness.VectorStore.Upserts);
    }

    private static IReadOnlyList<IndexField> CreateFields(Guid contentTypeKey, string text) =>
    [
        new IndexField(
            "Umb_ContentTypeId",
            new IndexValue { Keywords = [contentTypeKey.ToString("D")] },
            null,
            null),
        new IndexField(
            "body",
            new IndexValue { Texts = [text] },
            null,
            null)
    ];

    private static Harness CreateHarness(bool hasDefaultProfile = true, int? embeddingCount = null, int? throwOnEmbeddingCall = null)
    {
        var vectorStore = new RecordingVectorStore();
        var profileService = Substitute.For<IAIProfileService>();
        profileService.HasDefaultProfileAsync(Arg.Any<AICapability>(), Arg.Any<CancellationToken>()).Returns(hasDefaultProfile);

        var embeddingService = Substitute.For<IAIEmbeddingService>();
        var embeddingCallCount = 0;
        embeddingService
            .GenerateEmbeddingsAsync(Arg.Any<Action<AIEmbeddingBuilder>>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                embeddingCallCount++;

                if (throwOnEmbeddingCall == embeddingCallCount)
                    throw new InvalidOperationException("Embedding failed.");

                var texts = call.ArgAt<IEnumerable<string>>(1).ToList();
                var count = embeddingCount ?? texts.Count;

                return new GeneratedEmbeddings<Embedding<float>>(
                    Enumerable.Range(0, count).Select(_ => new Embedding<float>(new[] { 1f, 0f, 0f })));
            });

        var textChunker = Substitute.For<IAITextChunker>();
        textChunker.ChunkText(Arg.Any<string>(), Arg.Any<AITextChunkingOptions>())
            .Returns(call =>
            {
                var text = call.ArgAt<string>(0);

                return [new AITextChunk(text, 0, 0, text.Length)];
            });

        var tokenCounter = Substitute.For<IAITokenCounter>();
        tokenCounter.CountTokens(Arg.Any<string>()).Returns(0);

        var contentType = Substitute.For<IContentType>();
        contentType.Alias.Returns("article");

        var contentTypeService = Substitute.For<IContentTypeService>();
        contentTypeService.Get(Arg.Any<Guid>()).Returns(contentType);

        var context = Substitute.For<IUmbracoContext>();
        var contentCache = Substitute.For<IPublishedContentCache>();
        var mediaCache = Substitute.For<IPublishedMediaCache>();
        contentCache.GetById(Arg.Any<Guid>()).Returns((IPublishedContent?)null);
        mediaCache.GetById(Arg.Any<Guid>()).Returns((IPublishedContent?)null);
        context.Content.Returns(contentCache);
        context.Media.Returns(mediaCache);

        var contextAccessor = Substitute.For<IUmbracoContextAccessor>();
        var contextFactory = Substitute.For<IUmbracoContextFactory>();
        contextFactory.EnsureUmbracoContext().Returns(_ => new UmbracoContextReference(context, true, contextAccessor));

        var variationContextAccessor = Substitute.For<IVariationContextAccessor>();
        var replacementProvider = Substitute.For<ITextReplacementProvider>();
        replacementProvider.GetReplacements().Returns(new Dictionary<string, string>());

        var filterOptions = new AiSearchIndexFilterOptions
        {
            Categories =
            {
                ["docs"] = new SearchCategory
                {
                    IndexAlias = "index",
                    Indexing =
                    [
                        new SearchIndexDocument
                        {
                            DocumentTypeAliases = ["article"],
                            SearchText =
                            {
                                Fields =
                                {
                                    ["body"] = new SearchTextFieldOptions()
                                }
                            }
                        }
                    ]
                }
            }
        };

        var indexer = new FilteringAiVectorIndexer(
            vectorStore,
            profileService,
            embeddingService,
            textChunker,
            tokenCounter,
            contentTypeService,
            contextFactory,
            variationContextAccessor,
            replacementProvider,
            Options.Create(new AIVectorSearchOptions { ChunkSize = 100, ChunkOverlap = 0 }),
            Options.Create(filterOptions),
            NullLogger<FilteringAiVectorIndexer>.Instance);

        return new Harness(indexer, vectorStore, contentCache, mediaCache);
    }

    private sealed record Harness(FilteringAiVectorIndexer Indexer, RecordingVectorStore VectorStore, IPublishedContentCache ContentCache, IPublishedMediaCache MediaCache);

    private sealed class RecordingVectorStore : IAIVectorStore
    {
        public List<string> DeletedDocuments { get; } = [];

        public List<AIVectorEntry> Upserts { get; } = [];

        public Task UpsertAsync(string indexName, string documentId, string? culture, int chunkIndex, ReadOnlyMemory<float> vector, IDictionary<string, object>? metadata = null, CancellationToken cancellationToken = new())
        {
            Upserts.Add(new AIVectorEntry(documentId, culture, chunkIndex, vector, metadata ?? new Dictionary<string, object>()));

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string indexName, string documentId, string? culture, CancellationToken cancellationToken = new()) => Task.CompletedTask;

        public Task DeleteDocumentAsync(string indexName, string documentId, CancellationToken cancellationToken = new())
        {
            DeletedDocuments.Add(documentId);

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AIVectorSearchResult>> SearchAsync(string indexName, ReadOnlyMemory<float> queryVector, string? culture = null, int topK = 10, CancellationToken cancellationToken = new()) =>
            Task.FromResult<IReadOnlyList<AIVectorSearchResult>>([]);

        public Task<IReadOnlyList<AIVectorEntry>> GetVectorsByDocumentAsync(string indexName, string documentId, string? culture = null, CancellationToken cancellationToken = new()) =>
            Task.FromResult<IReadOnlyList<AIVectorEntry>>([]);

        public Task ResetAsync(string indexName, CancellationToken cancellationToken = new()) => Task.CompletedTask;

        public Task<long> GetDocumentCountAsync(string indexName, CancellationToken cancellationToken = new()) => Task.FromResult(0L);
    }
}
