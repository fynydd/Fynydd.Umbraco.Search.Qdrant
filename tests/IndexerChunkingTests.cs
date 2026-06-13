using System.Reflection;
using Fynydd.Umbraco.Search.Qdrant.Indexers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Umbraco.AI.Search.Core.Chunking;
using Umbraco.AI.Search.Core.Configuration;

namespace Umbraco.Search.Qdrant.Tests;

public sealed class IndexerChunkingTests
{
    [Fact]
    public void ApplyTextReplacements_AppliesLongestKeysFirstAndIgnoresEmptyKeys()
    {
        var result = InvokeApplyTextReplacements(
            "hello world",
            new Dictionary<string, string>
            {
                ["hello"] = "hi",
                ["hello world"] = "done",
                [""] = "ignored"
            });

        Assert.Equal("done", result);
    }

    [Fact]
    public void CreateSectionContext_ReturnsFirstMarkdownHeading()
    {
        var result = InvokeStatic<string>("CreateSectionContext", "Intro\n\n## Heading\nBody");

        Assert.Equal("## Heading", result);
    }

    [Fact]
    public void CreateChunks_ChunksPrefixWhenTextIsEmpty()
    {
        var chunker = new FakeTextChunker(text => [new AITextChunk(text, 0, 0, text.Length)]);
        var indexer = CreateIndexer(chunker);

        var chunks = InvokeCreateChunks(indexer, string.Empty, "# Title\n\n", new SearchIndexDocument(), new AITextChunkingOptions());

        var chunk = Assert.Single(chunks);
        Assert.Equal("# Title", chunk);
    }

    [Fact]
    public void CreateChunks_AddsSectionHeadingContextWhenChunkDoesNotStartWithHeading()
    {
        var chunker = new FakeTextChunker(_ => [new AITextChunk("Body text", 0, 0, 9)]);
        var indexer = CreateIndexer(chunker);
        var searchDocument = new SearchIndexDocument();

        var chunks = InvokeCreateChunks(indexer, "## Details\n\nBody text", "> Breadcrumb\n\n", searchDocument, new AITextChunkingOptions());

        var chunk = Assert.Single(chunks);
        Assert.StartsWith("> Breadcrumb\n\n## Details\n\nBody text", chunk);
    }

    [Fact]
    public void CreateChunks_DoesNotDuplicateSectionHeadingWhenChunkStartsWithHeading()
    {
        var chunker = new FakeTextChunker(_ => [new AITextChunk("## Details\n\nBody text", 0, 0, 22)]);
        var indexer = CreateIndexer(chunker);
        var searchDocument = new SearchIndexDocument();

        var chunks = InvokeCreateChunks(indexer, "## Details\n\nBody text", "> Breadcrumb\n\n", searchDocument, new AITextChunkingOptions());

        var chunk = Assert.Single(chunks);
        Assert.Equal(1, chunk.Split("## Details").Length - 1);
    }

    [Fact]
    public void CreateChunks_UsesWholeTextWhenHeadingAwareChunkingDisabled()
    {
        var chunker = new FakeTextChunker(text => [new AITextChunk(text, 0, 0, text.Length)]);
        var indexer = CreateIndexer(chunker);
        var searchDocument = new SearchIndexDocument
        {
            Chunking = new SearchChunkingOptions { UseHeadingAwareChunking = false }
        };

        InvokeCreateChunks(indexer, "# One\n\nBody\n\n# Two\n\nMore", string.Empty, searchDocument, new AITextChunkingOptions());

        Assert.Single(chunker.Inputs);
        Assert.Contains("# Two", chunker.Inputs[0]);
    }

    private static string InvokeApplyTextReplacements(string text, IReadOnlyDictionary<string, string> replacements)
    {
        var method = typeof(FilteringAiVectorIndexer).GetMethod("ApplyTextReplacements", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        return Assert.IsType<string>(method.Invoke(null, [text, replacements]));
    }

    private static T InvokeStatic<T>(string methodName, params object?[] args)
    {
        var method = typeof(FilteringAiVectorIndexer).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        return Assert.IsType<T>(method.Invoke(null, args));
    }

    private static List<string> InvokeCreateChunks(
        FilteringAiVectorIndexer indexer,
        string text,
        string prefix,
        SearchIndexDocument searchDocument,
        AITextChunkingOptions options)
    {
        var method = typeof(FilteringAiVectorIndexer).GetMethod("CreateChunks", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        var chunks = Assert.IsAssignableFrom<IEnumerable<object>>(method.Invoke(indexer, [text, prefix, searchDocument, options]));

        return chunks
            .Select(chunk => Assert.IsType<string>(chunk.GetType().GetProperty("Text")?.GetValue(chunk)))
            .ToList();
    }

    private static FilteringAiVectorIndexer CreateIndexer(IAITextChunker textChunker) => new(
        null!,
        null!,
        null!,
        textChunker,
        null!,
        null!,
        null,
        null,
        null!,
        null!,
        null!,
        Options.Create(new AIVectorSearchOptions()),
        Options.Create(new AiSearchIndexFilterOptions()),
        NullLogger<FilteringAiVectorIndexer>.Instance);

    private sealed class FakeTextChunker(Func<string, IReadOnlyList<AITextChunk>> chunkFactory) : IAITextChunker
    {
        public List<string> Inputs { get; } = [];

        public IReadOnlyList<AITextChunk> ChunkText(string text, AITextChunkingOptions options)
        {
            Inputs.Add(text);

            return chunkFactory(text);
        }
    }
}
