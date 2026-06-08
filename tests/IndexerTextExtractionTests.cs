using System.Collections;
using System.Reflection;
using Fynydd.Umbraco.Search.Qdrant.Indexers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Blocks;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Search.Core.Models.Indexing;

namespace Umbraco.Search.Qdrant.Tests;

public sealed class IndexerTextExtractionTests
{
    [Fact]
    public void GetPropertyText_StripsHtmlForNonMarkdownEditor()
    {
        var element = new FakePublishedElement("body", "Umbraco.TinyMCE", "<p>Hello <strong>syntax</strong></p>");

        var result = InvokeGetPropertyText(element, "body");

        Assert.DoesNotContain('<', result);
        Assert.DoesNotContain('>', result);
        Assert.Contains("Hello", result);
        Assert.Contains("syntax", result);
    }

    [Fact]
    public void GetPropertyText_PreservesMarkdownEditorValue()
    {
        const string markdown = "## Heading\n\n**Strong**";
        var element = new FakePublishedElement("body", "Umbraco.MarkdownEditor", markdown);

        var result = InvokeGetPropertyText(element, "body");

        Assert.Equal(markdown, result);
    }

    [Fact]
    public void ExtractTextFromFields_StripsHtmlFromIndexValues()
    {
        var indexer = CreateIndexer();
        var searchDocument = new SearchIndexDocument();
        searchDocument.SearchText.Fields["body"] = new SearchTextFieldOptions();
        var fields = new[]
        {
            new IndexField(
                "body",
                new Umbraco.Cms.Search.Core.Models.Indexing.IndexValue { Texts = ["<p>Hello <em>syntax</em></p>"] },
                null,
                null)
        };

        var texts = InvokeExtractTextFromFields(indexer, fields, searchDocument, null);

        var text = Assert.Single(texts);
        Assert.DoesNotContain('<', text);
        Assert.DoesNotContain('>', text);
        Assert.Contains("Hello", text);
        Assert.Contains("syntax", text);
    }

    [Fact]
    public void ExtractTextFromFields_AppliesConfiguredFieldWeight()
    {
        var indexer = CreateIndexer();
        var searchDocument = new SearchIndexDocument();
        searchDocument.SearchText.Fields["body"] = new SearchTextFieldOptions { Weight = 3 };
        var fields = new[]
        {
            new IndexField(
                "body",
                new Umbraco.Cms.Search.Core.Models.Indexing.IndexValue { Texts = ["<p>weighted syntax</p>"] },
                null,
                null)
        };

        var texts = InvokeExtractTextFromFields(indexer, fields, searchDocument, null);

        var text = Assert.Single(texts);
        Assert.Equal(3, text.Split("weighted syntax").Length - 1);
        Assert.DoesNotContain("<p>", text);
    }

    [Fact]
    public void RenderMarkdownTemplate_UsesFallbackAliasesAndCleansMissingHeadings()
    {
        var indexer = CreateIndexer();
        var element = new FakePublishedElement("summary", "Umbraco.TinyMCE", "<p>Fallback text</p>");
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "# {title}\n\n{title|summary}\n\n{{literal}}", element, searchDocument, null, 0);

        Assert.DoesNotContain("#", result);
        Assert.Contains("Fallback text", result);
        Assert.Contains("{literal}", result);
    }

    [Fact]
    public void GetBlockSearchText_ReturnsEmptyWhenDepthLimitIsExceeded()
    {
        var indexer = CreateIndexer();
        var searchDocument = new SearchIndexDocument();
        searchDocument.SearchText.Fields["blocks"] = new SearchTextFieldOptions();
        var block = new BlockListItem(Guid.NewGuid(), new FakePublishedElement("body", "Umbraco.TinyMCE", "<p>Too deep</p>"), null, null!);
        var element = new FakePublishedElement("blocks", "Umbraco.BlockList", new[] { block });

        var result = InvokeGetBlockSearchText(indexer, element, searchDocument, "blocks", 9);

        Assert.Empty(result);
    }

    private static string InvokeGetPropertyText(IPublishedElement element, string propertyAlias)
    {
        var method = typeof(FilteringAiVectorIndexer).GetMethod("GetPropertyText", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        return Assert.IsType<string>(method.Invoke(null, [element, propertyAlias]));
    }

    private static List<string> InvokeExtractTextFromFields(FilteringAiVectorIndexer indexer, IEnumerable<IndexField> fields, SearchIndexDocument searchDocument, IPublishedContent? content)
    {
        var method = typeof(FilteringAiVectorIndexer).GetMethod("ExtractTextFromFields", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        var parts = Assert.IsAssignableFrom<IEnumerable>(method.Invoke(indexer, [fields, searchDocument, content]));

        return parts
            .Cast<object>()
            .Select(part => Assert.IsType<string>(part.GetType().GetProperty("Text")?.GetValue(part)))
            .ToList();
    }

    private static string InvokeRenderMarkdownTemplate(FilteringAiVectorIndexer indexer, string template, IPublishedElement element, SearchIndexDocument searchDocument, IPublishedContent? content, int depth)
    {
        var method = typeof(FilteringAiVectorIndexer).GetMethod("RenderMarkdownTemplate", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        return Assert.IsType<string>(method.Invoke(indexer, [template, element, searchDocument, content, depth]));
    }

    private static List<string> InvokeGetBlockSearchText(FilteringAiVectorIndexer indexer, IPublishedElement element, SearchIndexDocument searchDocument, string fieldName, int depth)
    {
        var method = typeof(FilteringAiVectorIndexer).GetMethod("GetBlockSearchText", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        var parts = Assert.IsAssignableFrom<IEnumerable>(method.Invoke(indexer, [element, searchDocument, fieldName, depth]));

        return parts
            .Cast<object>()
            .Select(part => Assert.IsType<string>(part.GetType().GetProperty("Text")?.GetValue(part)))
            .ToList();
    }

    private static FilteringAiVectorIndexer CreateIndexer() => new(
        null!,
        null!,
        null!,
        null!,
        null!,
        null!,
        null!,
        null!,
        null!,
        Options.Create(new Umbraco.AI.Search.Core.Configuration.AIVectorSearchOptions()),
        Options.Create(new AiSearchIndexFilterOptions()),
        NullLogger<FilteringAiVectorIndexer>.Instance);

    private sealed class FakePublishedElement : IPublishedElement
    {
        private readonly FakePublishedProperty _property;

        public FakePublishedElement(string alias, string editorAlias, object? value)
        {
            _property = new FakePublishedProperty(alias, editorAlias, value);
            ContentType = new FakePublishedContentType("testElement", [_property.PropertyType]);
        }

        public IPublishedContentType ContentType { get; }

        public Guid Key { get; } = Guid.NewGuid();

        public IEnumerable<IPublishedProperty> Properties => [_property];

        public IPublishedProperty? GetProperty(string alias) =>
            alias.Equals(_property.Alias, StringComparison.OrdinalIgnoreCase) ? _property : null;
    }

    private sealed class FakePublishedProperty(string alias, string editorAlias, object? value) : IPublishedProperty
    {
        public IPublishedPropertyType PropertyType { get; } = new FakePublishedPropertyType(alias, editorAlias);

        public string Alias { get; } = alias;

        public bool HasValue(string? culture = null, string? segment = null) => value is not null;

        public object? GetSourceValue(string? culture = null, string? segment = null) => value;

        public object? GetValue(string? culture = null, string? segment = null) => value;

        public object? GetDeliveryApiValue(bool expanding, string? culture = null, string? segment = null) => value;
    }

    private sealed class FakePublishedPropertyType(string alias, string editorAlias) : IPublishedPropertyType
    {
        public IPublishedContentType ContentType { get; } = new FakePublishedContentType("testElement", []);

        public PublishedDataType DataType => null!;

        public string Alias { get; } = alias;

        public string EditorAlias => editorAlias;

        public string EditorUiAlias => editorAlias;

        public bool IsUserProperty => true;

        public ContentVariation Variations => ContentVariation.Nothing;

        public PropertyCacheLevel CacheLevel => PropertyCacheLevel.Element;

        public PropertyCacheLevel DeliveryApiCacheLevel => PropertyCacheLevel.Element;

        public PropertyCacheLevel DeliveryApiCacheLevelForExpansion => PropertyCacheLevel.Element;

        public Type ModelClrType => typeof(string);

        public Type DeliveryApiModelClrType => typeof(string);

        public Type ClrType => typeof(string);

        public bool? IsValue(object? value, PropertyValueLevel level) => value is not null;

        public object? ConvertSourceToInter(IPublishedElement owner, object? source, bool preview) => source;

        public object? ConvertInterToObject(IPublishedElement owner, PropertyCacheLevel referenceCacheLevel, object? inter, bool preview) => inter;

        public object? ConvertInterToDeliveryApiObject(IPublishedElement owner, PropertyCacheLevel referenceCacheLevel, object? inter, bool preview, bool expanding) => inter;
    }

    private sealed class FakePublishedContentType(string alias, IEnumerable<IPublishedPropertyType> propertyTypes) : IPublishedContentType
    {
        public Guid Key { get; } = Guid.NewGuid();

        public int Id => 1;

        public string Alias { get; } = alias;

        public PublishedItemType ItemType => PublishedItemType.Element;

        public HashSet<string> CompositionAliases { get; } = [];

        public ContentVariation Variations => ContentVariation.Nothing;

        public bool IsElement => true;

        public IEnumerable<IPublishedPropertyType> PropertyTypes { get; } = propertyTypes;

        public int GetPropertyIndex(string alias) => 0;

        public IPublishedPropertyType? GetPropertyType(string alias) =>
            PropertyTypes.FirstOrDefault(propertyType => propertyType.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));

        public IPublishedPropertyType? GetPropertyType(int index) => PropertyTypes.ElementAtOrDefault(index);
    }
}
