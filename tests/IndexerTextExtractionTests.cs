using System.Collections;
using System.Reflection;
using Fynydd.Umbraco.Search.Qdrant.Indexers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Blocks;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
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
    public void GetPropertyText_DoesNotStringifyBlockListValues()
    {
        var block = new BlockListItem(Guid.NewGuid(), new FakePublishedElement(("body", "Umbraco.TinyMCE", "<p>Nested</p>")), null, null!);
        var element = new FakePublishedElement(("blocks", "Umbraco.BlockList", new[] { block }));

        var result = InvokeGetPropertyText(element, "blocks");

        Assert.Empty(result);
        Assert.DoesNotContain("BlockList", result);
    }

    [Fact]
    public void ExtractTextFromFields_StripsHtmlFromIndexValues()
    {
        var indexer = CreateIndexer();
        var searchDocument = new SearchIndexDocument();
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
    public void RenderMarkdownTemplate_AppliesTokenWeight()
    {
        var indexer = CreateIndexer();
        var element = new FakePublishedElement("body", "Umbraco.TinyMCE", "<p>weighted syntax</p>");
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{body:3}", element, searchDocument, null, 0);

        Assert.Equal(3, result.Split("weighted syntax").Length - 1);
        Assert.DoesNotContain("<p>", result);
    }

    [Fact]
    public void RenderMarkdownTemplate_ResolvesAndWeightsDotNotationToken()
    {
        var indexer = CreateIndexer();
        var technology = new FakePublishedElement(("description", "Umbraco.TinyMCE", "<p>Weighted path field</p>"));
        var content = new FakePublishedContent("Current", ("technology", "Umbraco.MultiNodeTreePicker", technology));
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{technology.description:2}", content, searchDocument, content, 0);

        Assert.Equal(2, result.Split("Weighted path field").Length - 1);
        Assert.DoesNotContain("<p>", result);
    }

    [Fact]
    public void RenderMarkdownTemplate_PreservesColonAliasesBeforeTrailingWeight()
    {
        var indexer = CreateIndexer();
        var element = new FakePublishedElement("name:headline", "Umbraco.TinyMCE", "<p>Colon alias text</p>");
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{name:headline:3}", element, searchDocument, null, 0);

        Assert.Equal(3, result.Split("Colon alias text").Length - 1);
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
    public void RenderMarkdownTemplate_DoesNotUseScalarPickerValueAsNestedProperty()
    {
        var indexer = CreateIndexer();
        var element = new FakePublishedElement(("author", "Umbraco.MultiNodeTreePicker", 0));
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{author.Name}", element, searchDocument, null, 0);

        Assert.Empty(result);
    }

    [Fact]
    public void RenderMarkdownTemplate_ResolvesUserPickerNameWithDotNotation()
    {
        var userService = Substitute.For<IUserService>();
        var user = Substitute.For<IUser>();
        user.Name.Returns("Jane Editor");
        userService.GetUserById(2).Returns(user);
        var indexer = CreateIndexer(userService: userService);
        var element = new FakePublishedElement(("author", "Umbraco.UserPicker", 2));
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{author.Name}", element, searchDocument, null, 0);

        Assert.Equal("Jane Editor", result);
    }

    [Fact]
    public void RenderMarkdownTemplate_DoesNotStringifyComplexFallbackValue()
    {
        var indexer = CreateIndexer();
        var block = new BlockListItem(Guid.NewGuid(), new FakePublishedElement(("body", "Umbraco.TinyMCE", "<p>Nested</p>")), null, null!);
        var element = new FakePublishedElement(("blockContent", "Umbraco.BlockList", new[] { block }));
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{blockContent|body}", element, searchDocument, null, 0);

        Assert.Empty(result);
        Assert.DoesNotContain("BlockList", result);
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
    public void RenderMarkdownTemplate_ResolvesPublishedContentPickerPropertiesWithDotNotation()
    {
        var indexer = CreateIndexer();
        var clientProjects = new FakePublishedContent("Client projects", ("segments", "Umbraco.Tags", "Client projects"));
        var caseStudies = new FakePublishedContent("Case studies", ("segments", "Umbraco.Tags", "Case studies"));
        var element = new FakePublishedElement(("categories", "Umbraco.MultiNodeTreePicker", new[] { clientProjects, caseStudies }));
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{categories.segments}", element, searchDocument, null, 0);

        Assert.Contains("Client projects", result);
        Assert.Contains("Case studies", result);
    }

    [Fact]
    public void RenderMarkdownTemplate_ResolvesPublishedContentPickerMultipleTextStringWithDotNotation()
    {
        var indexer = CreateIndexer();
        var category = new FakePublishedContent("Client projects", ("segments", "Umbraco.MultipleTextString", new[] { "Client projects", "Government" }));
        var element = new FakePublishedElement(("categories", "Umbraco.MultiNodeTreePicker", new[] { category }));
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{categories.segments}", element, searchDocument, null, 0);

        Assert.Contains("Client projects", result);
        Assert.Contains("Government", result);
        Assert.DoesNotContain("System.String", result);
        Assert.Equal(1, result.Split("Client projects").Length - 1);
    }

    [Fact]
    public void RenderMarkdownTemplate_FormatsMultipleTextStringAsMarkdownList()
    {
        var indexer = CreateIndexer();
        var category = new FakePublishedContent("Client projects", ("segments", "Umbraco.MultipleTextString", new[] { "Client projects", "Group\nWebsites" }));
        var element = new FakePublishedElement(("categories", "Umbraco.MultiNodeTreePicker", new[] { category }));
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{categories.segments}", element, searchDocument, null, 0);

        Assert.Equal("- Client projects\n- Websites", result);
    }

    [Fact]
    public void RenderMarkdownTemplate_UsesMultipleTextStringSourceWhenConvertedValueIsEmpty()
    {
        var indexer = CreateIndexer();
        var category = new FakePublishedElement(("segments", "Umbraco.MultipleTextString", Array.Empty<string>(), """["Client projects","Government"]"""));
        var element = new FakePublishedElement(("categories", "Umbraco.MultiNodeTreePicker", new[] { category }));
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{categories.segments}", element, searchDocument, null, 0);

        Assert.Contains("Client projects", result);
        Assert.Contains("Government", result);
    }

    [Fact]
    public void RenderMarkdownTemplate_UsesContentServiceWhenPublishedPickerPropertyIsEmpty()
    {
        var key = Guid.NewGuid();
        var category = new FakePublishedContent("Client projects", key, ("segments", "Umbraco.MultipleTextString", Array.Empty<string>()));
        var serviceCategory = Substitute.For<IContent>();
        serviceCategory.HasProperty("segments").Returns(true);
        serviceCategory.GetValue("segments").Returns(new[] { "Client projects", "Government" });
        var contentService = Substitute.For<IContentService>();
        contentService.GetById(key).Returns(serviceCategory);
        var indexer = CreateIndexer(contentService: contentService);
        var element = new FakePublishedElement(("categories", "Umbraco.MultiNodeTreePicker", new[] { category }));
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{categories.segments}", element, searchDocument, null, 0);

        Assert.Contains("Client projects", result);
        Assert.Contains("Government", result);
    }

    [Fact]
    public void RenderMarkdownTemplate_FallsBackWhenContentServicePickerRootIsEmpty()
    {
        var rootKey = Guid.NewGuid();
        var category = new FakePublishedContent("Client projects", ("segments", "Umbraco.MultipleTextString", new[] { "Client projects" }));
        var root = new FakePublishedContent("Root", rootKey, ("categories", "Umbraco.MultiNodeTreePicker", new[] { category }));
        var serviceRoot = Substitute.For<IContent>();
        serviceRoot.HasProperty("categories").Returns(true);
        serviceRoot.GetValue("categories", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>()).Returns((object?)null);
        serviceRoot.GetValue("categories", published: Arg.Any<bool>()).Returns((object?)null);
        serviceRoot.GetValue("categories").Returns((object?)null);
        var contentService = Substitute.For<IContentService>();
        contentService.GetById(rootKey).Returns(serviceRoot);
        var indexer = CreateIndexer(contentService: contentService);
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{categories.segments}", root, searchDocument, root, 0);

        Assert.Equal("- Client projects", result);
    }

    [Fact]
    public void RenderMarkdownTemplate_FallsBackWhenContentServiceNestedPropertyIsEmpty()
    {
        var categoryKey = Guid.NewGuid();
        var category = new FakePublishedContent("Client projects", categoryKey, ("segments", "Umbraco.MultipleTextString", new[] { "Client projects" }));
        var serviceCategory = Substitute.For<IContent>();
        serviceCategory.HasProperty("segments").Returns(true);
        serviceCategory.GetValue("segments", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>()).Returns((object?)null);
        serviceCategory.GetValue("segments", published: Arg.Any<bool>()).Returns((object?)null);
        serviceCategory.GetValue("segments").Returns((object?)null);
        var contentService = Substitute.For<IContentService>();
        contentService.GetById(categoryKey).Returns(serviceCategory);
        var indexer = CreateIndexer(contentService: contentService);
        var element = new FakePublishedElement(("categories", "Umbraco.MultiNodeTreePicker", new[] { category }));
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{categories.segments}", element, searchDocument, null, 0);

        Assert.Equal("- Client projects", result);
    }

    [Fact]
    public void RenderMarkdownTemplate_UsesStoredContentServiceValuesWhenGetValueIsEmpty()
    {
        var categoryKey = Guid.NewGuid();
        var category = new FakePublishedContent("Client projects", categoryKey, ("segments", "Umbraco.MultipleTextString", Array.Empty<string>()));
        var propertyValue = Substitute.For<IPropertyValue>();
        propertyValue.PublishedValue.Returns("""["Client projects","Government"]""");
        var property = Substitute.For<IProperty>();
        property.Alias.Returns("segments");
        property.Values.Returns([propertyValue]);
        var properties = Substitute.For<IPropertyCollection>();
        properties.GetEnumerator().Returns(_ => new List<IProperty> { property }.GetEnumerator());
        var serviceCategory = Substitute.For<IContent>();
        serviceCategory.HasProperty("segments").Returns(true);
        serviceCategory.GetValue("segments", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>()).Returns((object?)null);
        serviceCategory.GetValue("segments", published: Arg.Any<bool>()).Returns((object?)null);
        serviceCategory.GetValue("segments").Returns((object?)null);
        serviceCategory.Properties.Returns(properties);
        var contentService = Substitute.For<IContentService>();
        contentService.GetById(categoryKey).Returns(serviceCategory);
        var indexer = CreateIndexer(contentService: contentService);
        var element = new FakePublishedElement(("categories", "Umbraco.MultiNodeTreePicker", new[] { category }));
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{categories.segments}", element, searchDocument, null, 0);

        Assert.Contains("Client projects", result);
        Assert.Contains("Government", result);
    }

    [Fact]
    public void RenderMarkdownTemplate_FallsBackToPublishedCacheWhenContentServiceNestedValueIsEmpty()
    {
        var categoryKey = Guid.NewGuid();
        var pickerCategory = Substitute.For<IContent>();
        pickerCategory.Key.Returns(categoryKey);
        pickerCategory.HasProperty("segments").Returns(true);
        pickerCategory.GetValue("segments", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>()).Returns((object?)null);
        pickerCategory.GetValue("segments", published: Arg.Any<bool>()).Returns((object?)null);
        pickerCategory.GetValue("segments").Returns((object?)null);
        var pickerProperty = Substitute.For<IProperty>();
        pickerProperty.Alias.Returns("segments");
        var pickerProperties = Substitute.For<IPropertyCollection>();
        pickerProperties.GetEnumerator().Returns(_ => new List<IProperty> { pickerProperty }.GetEnumerator());
        pickerCategory.Properties.Returns(pickerProperties);
        var contentService = Substitute.For<IContentService>();
        contentService.GetById(categoryKey).Returns(pickerCategory);
        var category = new FakePublishedContent("Client projects", categoryKey, ("segments", "Umbraco.MultipleTextString", new[] { "Client projects" }));
        var contentCache = Substitute.For<IPublishedContentCache>();
        contentCache.GetById(categoryKey).Returns(category);
        var context = Substitute.For<IUmbracoContext>();
        context.Content.Returns(contentCache);
        var contextAccessor = Substitute.For<IUmbracoContextAccessor>();
        var contextFactory = Substitute.For<IUmbracoContextFactory>();
        contextFactory.EnsureUmbracoContext().Returns(_ => new UmbracoContextReference(context, true, contextAccessor));
        var indexer = CreateIndexer(contentService: contentService, contextFactory: contextFactory);
        var element = new FakePublishedElement(("categories", "Umbraco.MultiNodeTreePicker", new[] { pickerCategory }));
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{categories.segments}", element, searchDocument, null, 0);

        Assert.Equal("- Client projects", result);
    }

    [Fact]
    public void RenderMarkdownTemplate_ResolvesRawUdiMultiplePickerPropertiesWithDotNotation()
    {
        var key = Guid.Parse("8a976dc3-7897-4015-b559-f9c0e7ee1f5a");
        var category = new FakePublishedContent("Client projects", ("segments", "Umbraco.Tags", "Client projects"));
        var contentCache = Substitute.For<IPublishedContentCache>();
        contentCache.GetById(key).Returns(category);
        var context = Substitute.For<IUmbracoContext>();
        context.Content.Returns(contentCache);
        var contextAccessor = Substitute.For<IUmbracoContextAccessor>();
        var contextFactory = Substitute.For<IUmbracoContextFactory>();
        contextFactory.EnsureUmbracoContext().Returns(_ => new UmbracoContextReference(context, true, contextAccessor));
        var indexer = CreateIndexer(contextFactory: contextFactory);
        var element = new FakePublishedElement(("categories", "Umbraco.MultiNodeTreePicker", "umb://document/8a976dc378974015b559f9c0e7ee1f5a"));
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{categories.segments}", element, searchDocument, null, 0);

        Assert.Equal("Client projects", result);
    }

    [Fact]
    public void RenderMarkdownTemplate_ResolvesRawUdiPickerPropertiesFromContentService()
    {
        var key = Guid.Parse("8a976dc3-7897-4015-b559-f9c0e7ee1f5a");
        var contentCache = Substitute.For<IPublishedContentCache>();
        contentCache.GetById(key).Returns((IPublishedContent?)null);
        var context = Substitute.For<IUmbracoContext>();
        context.Content.Returns(contentCache);
        var contextAccessor = Substitute.For<IUmbracoContextAccessor>();
        var contextFactory = Substitute.For<IUmbracoContextFactory>();
        contextFactory.EnsureUmbracoContext().Returns(_ => new UmbracoContextReference(context, true, contextAccessor));
        var category = Substitute.For<IContent>();
        category.Name.Returns("Client projects");
        category.HasProperty("segments").Returns(true);
        category.GetValue("segments").Returns("Client projects");
        var contentService = Substitute.For<IContentService>();
        contentService.GetById(key).Returns(category);
        var indexer = CreateIndexer(contentService: contentService, contextFactory: contextFactory);
        var element = new FakePublishedElement(("categories", "Umbraco.MultiNodeTreePicker", "umb://document/8a976dc378974015b559f9c0e7ee1f5a"));
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{categories.segments}", element, searchDocument, null, 0);

        Assert.Equal("Client projects", result);
    }

    [Fact]
    public void RenderMarkdownTemplate_ResolvesContentServiceMultipleTextStringWithDotNotation()
    {
        var key = Guid.Parse("8a976dc3-7897-4015-b559-f9c0e7ee1f5a");
        var contentCache = Substitute.For<IPublishedContentCache>();
        contentCache.GetById(key).Returns((IPublishedContent?)null);
        var context = Substitute.For<IUmbracoContext>();
        context.Content.Returns(contentCache);
        var contextAccessor = Substitute.For<IUmbracoContextAccessor>();
        var contextFactory = Substitute.For<IUmbracoContextFactory>();
        contextFactory.EnsureUmbracoContext().Returns(_ => new UmbracoContextReference(context, true, contextAccessor));
        var category = Substitute.For<IContent>();
        category.HasProperty("segments").Returns(true);
        category.GetValue("segments").Returns(new[] { "Client projects", "Government" });
        var contentService = Substitute.For<IContentService>();
        contentService.GetById(key).Returns(category);
        var indexer = CreateIndexer(contentService: contentService, contextFactory: contextFactory);
        var element = new FakePublishedElement(("categories", "Umbraco.MultiNodeTreePicker", "umb://document/8a976dc378974015b559f9c0e7ee1f5a"));
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{categories.segments}", element, searchDocument, null, 0);

        Assert.Contains("Client projects", result);
        Assert.Contains("Government", result);
        Assert.DoesNotContain("System.String", result);
    }

    [Fact]
    public void RenderMarkdownTemplate_ResolvesUdiObjectPickerPropertiesFromContentService()
    {
        var key = Guid.Parse("8a976dc3-7897-4015-b559-f9c0e7ee1f5a");
        var contentCache = Substitute.For<IPublishedContentCache>();
        contentCache.GetById(key).Returns((IPublishedContent?)null);
        var context = Substitute.For<IUmbracoContext>();
        context.Content.Returns(contentCache);
        var contextAccessor = Substitute.For<IUmbracoContextAccessor>();
        var contextFactory = Substitute.For<IUmbracoContextFactory>();
        contextFactory.EnsureUmbracoContext().Returns(_ => new UmbracoContextReference(context, true, contextAccessor));
        var category = Substitute.For<IContent>();
        category.HasProperty("segments").Returns(true);
        category.GetValue("segments").Returns("Client projects");
        var contentService = Substitute.For<IContentService>();
        contentService.GetById(key).Returns(category);
        var indexer = CreateIndexer(contentService: contentService, contextFactory: contextFactory);
        var element = new FakePublishedElement(("categories", "Umbraco.MultiNodeTreePicker", new[] { new FakeUdi("umb://document/8a976dc378974015b559f9c0e7ee1f5a") }));
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{categories.segments}", element, searchDocument, null, 0);

        Assert.Equal("Client projects", result);
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

        var result = InvokeRenderMarkdownTemplate(indexer, "{technology.description:3}", element, searchDocument, null, 0);

        Assert.Equal(3, result.Split("Weighted dot text").Length - 1);
    }

    [Fact]
    public void RenderMarkdownTemplate_WeightsMultipleTextStringListsWithoutBlankLinesBetweenGroups()
    {
        var indexer = CreateIndexer();
        var firstCategory = new FakePublishedContent("Client projects", ("segments", "Umbraco.MultipleTextString", new[] { "Client projects" }));
        var secondCategory = new FakePublishedContent("Websites", ("segments", "Umbraco.MultipleTextString", new[] { "Websites" }));
        var element = new FakePublishedElement(("categories", "Umbraco.MultiNodeTreePicker", new[] { firstCategory, secondCategory }));
        var searchDocument = new SearchIndexDocument();

        var result = InvokeRenderMarkdownTemplate(indexer, "{categories.segments:2}", element, searchDocument, null, 0);

        Assert.Equal("- Client projects\n- Websites\n- Client projects\n- Websites", result);
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

    [Fact]
    public void CreateChunkContext_DoesNotDuplicateAliasesAlreadyInMarkdownTemplate()
    {
        var indexer = CreateIndexer();
        var category = new FakePublishedElement(("label", "Umbraco.TextBox", "Context category"));
        var content = new FakePublishedContent(
            "Current",
            ("headline", "Umbraco.TextBox", "Context title"),
            ("category", "Umbraco.MultiNodeTreePicker", category),
            ("summary", "Umbraco.TextBox", "Context summary"));
        var searchDocument = new SearchIndexDocument();
        searchDocument.SearchText.MarkdownTemplate = "# {headline}\n\n> {Breadcrumb}\n\n{category.label}\n\n{summary}";
        searchDocument.Chunking.Context.TitlePropertyAliases.Add("headline");
        searchDocument.Chunking.Context.CategoryPropertyAliases.Add("category.label");
        searchDocument.Chunking.Context.AdditionalPropertyAliases.Add("summary");

        var result = InvokeCreateChunkContext(indexer, content, searchDocument);

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

    private static string InvokeCreateChunkContext(FilteringAiVectorIndexer indexer, IPublishedContent content, SearchIndexDocument searchDocument)
    {
        var method = typeof(FilteringAiVectorIndexer).GetMethod("CreateChunkContext", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        return Assert.IsType<string>(method.Invoke(indexer, [content, searchDocument, new Dictionary<string, string>()]));
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

    private static FilteringAiVectorIndexer CreateIndexer(IContentService? contentService = null, IUserService? userService = null, IUmbracoContextFactory? contextFactory = null) => new(
        null!,
        null!,
        null!,
        null!,
        null!,
        null!,
        contentService,
        userService,
        contextFactory!,
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

        public FakePublishedElement(params (string Alias, string EditorAlias, object? Value, object? SourceValue)[] properties)
        {
            _properties = properties
                .Select(property => new FakePublishedProperty(property.Alias, property.EditorAlias, property.Value, property.SourceValue))
                .ToList();
            ContentType = new FakePublishedContentType("testElement", _properties.Select(property => property.PropertyType));
        }

        public IPublishedContentType ContentType { get; }

        public Guid Key { get; protected set; } = Guid.NewGuid();

        public IEnumerable<IPublishedProperty> Properties => _properties;

        public IPublishedProperty? GetProperty(string alias) =>
            _properties.FirstOrDefault(property => alias.Equals(property.Alias, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakePublishedContent(string name, params (string Alias, string EditorAlias, object? Value)[] properties)
        : FakePublishedElement(properties), IPublishedContent
    {
        public int Id => 1;

        public string Name { get; } = name;

        public FakePublishedContent(string name, Guid key, params (string Alias, string EditorAlias, object? Value)[] properties)
            : this(name, properties)
        {
            Key = key;
        }

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

    private sealed class FakePublishedProperty(string alias, string editorAlias, object? value, object? sourceValue = null) : IPublishedProperty
    {
        public IPublishedPropertyType PropertyType { get; } = new FakePublishedPropertyType(alias, editorAlias);

        public string Alias { get; } = alias;

        public bool HasValue(string? culture = null, string? segment = null) => value is not null;

        public object? GetSourceValue(string? culture = null, string? segment = null) => sourceValue ?? value;

        public object? GetValue(string? culture = null, string? segment = null) => value;

        public object? GetDeliveryApiValue(bool expanding, string? culture = null, string? segment = null) => value;
    }

    private sealed class FakeUdi(string value)
    {
        public override string ToString() => value;
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
