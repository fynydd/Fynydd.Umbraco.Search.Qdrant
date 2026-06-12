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
    public void ExtractTextFromFields_ResolvesAndWeightsDotNotationFields()
    {
        var indexer = CreateIndexer();
        var technology = new FakePublishedElement(("description", "Umbraco.TinyMCE", "<p>Weighted path field</p>"));
        var content = new FakePublishedContent("Current", ("technology", "Umbraco.MultiNodeTreePicker", technology));
        var searchDocument = new SearchIndexDocument();
        searchDocument.SearchText.Fields["technology.description"] = new SearchTextFieldOptions { Weight = 2 };

        var texts = InvokeExtractTextFromFields(indexer, [], searchDocument, content);

        var text = Assert.Single(texts);
        Assert.Equal(2, text.Split("Weighted path field").Length - 1);
        Assert.DoesNotContain("<p>", text);
    }

    [Fact]
    public void ExtractTextFromFields_PrefersFullDotNotationWeightOverFinalAliasWeight()
    {
        var indexer = CreateIndexer();
        var technology = new FakePublishedElement(("description", "Umbraco.TinyMCE", "<p>Specific path weight</p>"));
        var content = new FakePublishedContent("Current", ("technology", "Umbraco.MultiNodeTreePicker", technology));
        var searchDocument = new SearchIndexDocument();
        searchDocument.SearchText.Fields["description"] = new SearchTextFieldOptions { Weight = 5 };
        searchDocument.SearchText.Fields["technology.description"] = new SearchTextFieldOptions { Weight = 2 };

        var texts = InvokeExtractTextFromFields(indexer, [], searchDocument, content);

        var text = Assert.Single(texts);
        Assert.Equal(2, text.Split("Specific path weight").Length - 1);
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

    [Fact]
    public void RenderMarkdownTemplate_ResolvesSinglePickerPropertyWithDotNotation()
    {
        var indexer = CreateIndexer();
        var author = new FakePublishedElement(("Name", "Umbraco.TextBox", "Ada Lovelace"));
        var element = new FakePublishedElement(("author", "Umbraco.MultiNodeTreePicker", author));
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{author.Name}", element, searchDocument, null, 0);

        Assert.Equal("Ada Lovelace", result);
    }

    [Fact]
    public void RenderMarkdownTemplate_ResolvesMultiplePickerPropertiesWithDotNotation()
    {
        var indexer = CreateIndexer();
        var csharp = new FakePublishedElement(("description", "Umbraco.TinyMCE", "<p>C# language</p>"));
        var qdrant = new FakePublishedElement(("description", "Umbraco.TinyMCE", "<p>Qdrant vectors</p>"));
        var element = new FakePublishedElement(("technology", "Umbraco.MultiNodeTreePicker", new[] { csharp, qdrant }));
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{technology.description}", element, searchDocument, null, 0);

        Assert.Contains("C# language", result);
        Assert.Contains("Qdrant vectors", result);
        Assert.DoesNotContain("<p>", result);
    }

    [Fact]
    public void RenderMarkdownTemplate_ResolvesBlockListItemPropertyWithDotNotation()
    {
        var indexer = CreateIndexer();
        var blockContent = new FakePublishedElement(("description", "Umbraco.TinyMCE", "<p>Block description</p>"));
        var block = new BlockListItem(Guid.NewGuid(), blockContent, null, null!);
        var element = new FakePublishedElement(("technology", "Umbraco.BlockList", new[] { block }));
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{technology.description}", element, searchDocument, null, 0);

        Assert.Equal("Block description", result);
    }

    [Fact]
    public void RenderMarkdownTemplate_ResolvesNestedPickerAndBlockListPropertiesWithDotNotation()
    {
        var indexer = CreateIndexer();
        var vendor = new FakePublishedElement(("description", "Umbraco.TinyMCE", "<p>Vendor details</p>"));
        var cardContent = new FakePublishedElement(("vendor", "Umbraco.MultiNodeTreePicker", vendor));
        var card = new BlockListItem(Guid.NewGuid(), cardContent, null, null!);
        var sectionContent = new FakePublishedElement(("cards", "Umbraco.BlockList", new[] { card }));
        var section = new BlockListItem(Guid.NewGuid(), sectionContent, null, null!);
        var element = new FakePublishedElement(("sections", "Umbraco.BlockList", new[] { section }));
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{sections.cards.vendor.description}", element, searchDocument, null, 0);

        Assert.Equal("Vendor details", result);
    }

    [Fact]
    public void RenderMarkdownTemplate_AppliesWeightFromFullDotNotationField()
    {
        var indexer = CreateIndexer();
        var technology = new FakePublishedElement(("description", "Umbraco.TinyMCE", "<p>Weighted dot text</p>"));
        var element = new FakePublishedElement(("technology", "Umbraco.MultiNodeTreePicker", technology));
        var searchDocument = new SearchIndexDocument();
        searchDocument.SearchText.Fields["technology.description"] = new SearchTextFieldOptions { Weight = 3 };

        var result = InvokeRenderMarkdownTemplate(indexer, "{technology.description}", element, searchDocument, null, 0);

        Assert.Equal(3, result.Split("Weighted dot text").Length - 1);
    }

    [Fact]
    public void ChunkingContextLists_CanResolveDotNotationAliases()
    {
        var indexer = CreateIndexer();
        var titleSource = new FakePublishedElement(("name", "Umbraco.TextBox", "Context title"));
        var sectionSource = new FakePublishedElement(("heading", "Umbraco.TextBox", "Context section"));
        var categorySource = new FakePublishedElement(("label", "Umbraco.TextBox", "Context category"));
        var summarySource = new FakePublishedElement(("summary", "Umbraco.TinyMCE", "<p>Context summary</p>"));
        var content = new FakePublishedContent(
            "Current",
            ("author", "Umbraco.MultiNodeTreePicker", titleSource),
            ("section", "Umbraco.MultiNodeTreePicker", sectionSource),
            ("category", "Umbraco.MultiNodeTreePicker", categorySource),
            ("related", "Umbraco.MultiNodeTreePicker", summarySource));
        var searchDocument = new SearchIndexDocument();
        searchDocument.Chunking.Context.TitlePropertyAliases.Add("author.name");
        searchDocument.Chunking.Context.SectionTitlePropertyAliases.Add("section.heading");
        searchDocument.Chunking.Context.CategoryPropertyAliases.Add("category.label");
        searchDocument.Chunking.Context.AdditionalPropertyAliases.Add("related.summary");

        Assert.Equal("Context title", InvokeResolveContextPropertyText(indexer, searchDocument.Chunking.Context.TitlePropertyAliases.Single(), content, searchDocument));
        Assert.Equal("Context section", InvokeResolveContextPropertyText(indexer, searchDocument.Chunking.Context.SectionTitlePropertyAliases.Single(), content, searchDocument));
        Assert.Equal("Context category", InvokeResolveContextPropertyText(indexer, searchDocument.Chunking.Context.CategoryPropertyAliases.Single(), content, searchDocument));
        Assert.Equal("Context summary", InvokeResolveContextPropertyText(indexer, searchDocument.Chunking.Context.AdditionalPropertyAliases.Single(), content, searchDocument));
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

    private static string InvokeResolveContextPropertyText(FilteringAiVectorIndexer indexer, string propertyAlias, IPublishedContent content, SearchIndexDocument searchDocument)
    {
        var method = typeof(FilteringAiVectorIndexer).GetMethod("ResolveContextPropertyText", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        return Assert.IsType<string>(method.Invoke(indexer, [propertyAlias, content, searchDocument, content]));
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

    private class FakePublishedElement : IPublishedElement
    {
        private readonly List<FakePublishedProperty> _properties;

        public FakePublishedElement(string alias, string editorAlias, object? value)
            : this((alias, editorAlias, value))
        {
        }

        public FakePublishedElement(params (string Alias, string EditorAlias, object? Value)[] properties)
        {
            _properties = properties
                .Select(property => new FakePublishedProperty(property.Alias, property.EditorAlias, property.Value))
                .ToList();
            ContentType = new FakePublishedContentType("testElement", _properties.Select(property => property.PropertyType));
        }

        public IPublishedContentType ContentType { get; }

        public Guid Key { get; } = Guid.NewGuid();

        public IEnumerable<IPublishedProperty> Properties => _properties;

        public IPublishedProperty? GetProperty(string alias) =>
            _properties.FirstOrDefault(property => alias.Equals(property.Alias, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakePublishedContent(string name, params (string Alias, string EditorAlias, object? Value)[] properties)
        : FakePublishedElement(properties), IPublishedContent
    {
        public int Id => 1;

        public string Name { get; } = name;

        public string UrlSegment => name.ToLowerInvariant();

        public int SortOrder => 0;

        public int Level => 1;

        public string Path => "-1,1";

        public int? TemplateId => null;

        public int CreatorId => 0;

        public DateTime CreateDate => DateTime.UtcNow;

        public int WriterId => 0;

        public DateTime UpdateDate => DateTime.UtcNow;

        public IReadOnlyDictionary<string, PublishedCultureInfo> Cultures { get; } = new Dictionary<string, PublishedCultureInfo>();

        public PublishedItemType ItemType => PublishedItemType.Content;

        public IPublishedContent? Parent => null;

        public IEnumerable<IPublishedContent> Children => [];

        public bool IsDraft(string? culture = null) => false;

        public bool IsPublished(string? culture = null) => true;
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
