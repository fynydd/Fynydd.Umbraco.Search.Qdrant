using System.Collections.Concurrent;
using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Umbraco.AI.Core.Embeddings;
using Umbraco.AI.Core.Models;
using Umbraco.AI.Core.Profiles;
using Umbraco.AI.Search.Core.Chunking;
using Umbraco.AI.Search.Core.Configuration;
using Umbraco.AI.Search.Core.VectorStore;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Blocks;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Search.Core.Models.Indexing;
using Umbraco.Cms.Search.Core.Services;
using Umbraco.Extensions;
using Fynydd.Umbraco.Search.Qdrant.Extensions;
using Fynydd.Umbraco.Search.Qdrant.Services;
using Fynydd.Umbraco.Search.Qdrant.VectorStores;
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable CollectionNeverUpdated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace Fynydd.Umbraco.Search.Qdrant.Indexers;

/// <summary>
/// Builds AI vector-search chunks from configured Umbraco content fields and writes their embeddings to the vector store.
/// </summary>
public partial class FilteringAiVectorIndexer(
    IAIVectorStore vectorStore,
    IAIProfileService profileService,
    IAIEmbeddingService embeddingService,
    IAITextChunker textChunker,
    IAITokenCounter tokenCounter,
    IContentTypeService contentTypeService,
    IContentService? contentService,
    IUserService? userService,
    IUmbracoContextFactory umbracoContextFactory,
    IVariationContextAccessor? variationContextAccessor,
    ITextReplacementProvider textReplacementProvider,
    IOptions<AIVectorSearchOptions> options,
    IOptions<AiSearchIndexFilterOptions> filterOptions,
    ILogger<FilteringAiVectorIndexer> logger) : IIndexer
{
    private readonly ConcurrentDictionary<Guid, string> _aliasCache = new();

    /// <summary>
    /// Rebuilds vector entries for an Umbraco content or media item across all supplied culture and segment variations.
    /// </summary>
    public async Task AddOrUpdateAsync(string indexAlias, Guid id, UmbracoObjectTypes objectType, IEnumerable<Variation> variations, IEnumerable<IndexField> fields, ContentProtection? protection)
    {
        if (await profileService.HasDefaultProfileAsync((AICapability)1) == false)
        {
            logger.LogDebug("No default embedding profile configured, skipping vector indexing for {IndexAlias}", indexAlias);
            return;
        }

        var fieldList = fields.ToList();
        var contentTypeIdKeyword = fieldList
            .FirstOrDefault(f => f.FieldName == "Umb_ContentTypeId")
            ?.Value.Keywords
            ?.FirstOrDefault();

        var searchIndexDocument = (SearchIndexDocument?)null;
        var searchCategory = (SearchCategory?)null;

        if (contentTypeIdKeyword is not null && Guid.TryParse(contentTypeIdKeyword, out var contentTypeKey))
        {
            var documentTypeAlias = _aliasCache.GetOrAdd(contentTypeKey, key => contentTypeService.Get(key)?.Alias ?? string.Empty);

            if (string.IsNullOrWhiteSpace(documentTypeAlias))
                return;

            foreach (var category in filterOptions.Value.Categories.Values)
            {
                var match = category.Indexing.FirstOrDefault(indexing => indexing.DocumentTypeAliases.Contains(documentTypeAlias, StringComparer.OrdinalIgnoreCase));

                if (match is null)
                    continue;

                searchIndexDocument = match;
                searchCategory = category;
                break;
            }

            if (searchIndexDocument is null)
                return;
        }

        using var contextRef = umbracoContextFactory.EnsureUmbracoContext();
        var content = objectType == UmbracoObjectTypes.Media
            ? contextRef.UmbracoContext.Media.GetById(id)
            : contextRef.UmbracoContext.Content.GetById(id);
        var documentId = id.ToString("D");
        var variants = GetVariants(variations, fieldList).ToList();

        if (variants.Count == 0)
        {
            logger.LogDebug("No fields to index for document {DocumentId} in {IndexAlias}", id, indexAlias);
            return;
        }

        var previousVariationContext = variationContextAccessor?.VariationContext;
        var pendingUpserts = new List<SearchVectorUpsert>();
        var failed = false;

        try
        {
            foreach (var variation in variants)
            {
                var culture = variation.Culture;
                var segment = variation.Segment;
                var variationKey = AiVariationKey.Create(variation);
                var variationFields = fieldList.Where(field => FieldAppliesToVariation(field, variation)).ToList();

                variationContextAccessor?.VariationContext = new VariationContext(culture, segment);

                var textReplacements = textReplacementProvider.GetReplacements();
                var prefix = CreateChunkContext(content, searchIndexDocument, textReplacements);
                var prefixTokens = tokenCounter.CountTokens(prefix);
                var configuredChunkSize = searchIndexDocument?.Chunking.ChunkSize ?? searchCategory?.ChunkSize ?? options.Value.ChunkSize;
                var configuredChunkOverlap = searchIndexDocument?.Chunking.ChunkOverlap ?? searchCategory?.ChunkOverlap ?? options.Value.ChunkOverlap;
                var maxChunkSize = Math.Max(1, configuredChunkSize - prefixTokens);
                var chunkOverlap = Math.Min(configuredChunkOverlap, Math.Max(0, maxChunkSize - 1));
                var chunkingOptions = new AITextChunkingOptions
                {
                    MaxChunkSize = maxChunkSize,
                    ChunkOverlap = chunkOverlap
                };

                var textParts = ExtractTextFromFields(variationFields, searchIndexDocument, content)
                    .Select(part => new SearchTextPart(Text: ApplyTextReplacements(part.Text, textReplacements)))
                    .Where(part => string.IsNullOrWhiteSpace(part.Text) == false)
                    .ToList();
                var text = string.Join("\n\n", textParts.Select(part => part.Text));

                if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(prefix))
                {
                    logger.LogDebug("No text content for document {DocumentId} culture {Culture} segment {Segment} in {IndexAlias}", id, culture ?? "invariant", segment ?? "invariant", indexAlias);
                    continue;
                }

                try
                {
                    var sourceText = $"{prefix}{text}".Trim();
                    var chunks = CreateChunks(text, prefix, searchIndexDocument, chunkingOptions);

                    if (chunks.Count == 0)
                    {
                        logger.LogDebug("No chunks for document {DocumentId} culture {Culture} segment {Segment} in {IndexAlias}", id, culture ?? "invariant", segment ?? "invariant", indexAlias);
                        continue;
                    }

                    var chunkTexts = chunks
                        .Select(chunk => chunk.Text.Trim())
                        .Where(i => string.IsNullOrWhiteSpace(i) == false)
                        .ToList();

                    if (chunkTexts.Count == 0)
                    {
                        logger.LogDebug("No non-empty chunks for document {DocumentId} culture {Culture} segment {Segment} in {IndexAlias}", id, culture ?? "invariant", segment ?? "invariant", indexAlias);
                        continue;
                    }

                    var embeddings = await embeddingService.GenerateEmbeddingsAsync(
                        builder => builder.WithAlias("ai-search-indexer").AsPassThrough(),
                        chunkTexts,
                        CancellationToken.None);

                    if (embeddings.Count != chunkTexts.Count)
                    {
                        logger.LogError("Embedding service returned {EmbeddingCount} embeddings for {ChunkCount} chunks for document {DocumentId} culture {Culture} segment {Segment} in {IndexAlias}", embeddings.Count, chunkTexts.Count, id, culture ?? "invariant", segment ?? "invariant", indexAlias);
                        failed = true;
                        continue;
                    }

                    for (var i = 0; i < chunkTexts.Count; i++)
                    {
                        var metadata = new Dictionary<string, object>
                        {
                            ["objectType"] = objectType.ToString(),
                            ["chunkIndex"] = i,
                            ["totalChunks"] = chunkTexts.Count,
                            ["chunkText"] = chunkTexts[i],
                            ["sourceText"] = sourceText
                        };

                        if (content is not null)
                        {
                            var breadcrumb = CreateBreadcrumb(content);
                            var url = content.Url();

                            if (string.IsNullOrWhiteSpace(breadcrumb) == false)
                                metadata["breadcrumb"] = breadcrumb;

                            if (string.IsNullOrWhiteSpace(url) == false)
                                metadata["url"] = url;

                            metadata["documentTypeAlias"] = content.ContentType.Alias;

                            var category = CreateDocumentCategory(content, searchIndexDocument);

                            if (string.IsNullOrWhiteSpace(category) == false)
                                metadata["category"] = category;
                        }

                        if (culture is not null)
                            metadata["culture"] = culture;

                        if (segment is not null)
                            metadata["segment"] = segment;

                        if (protection is not null && protection.AccessIds.Any())
                            metadata["accessIds"] = string.Join(",", protection.AccessIds);

                        var snippet = chunkTexts[i].StartsWith(prefix, StringComparison.Ordinal)
                            ? chunkTexts[i][prefix.Length..].Trim()
                            : chunkTexts[i].Trim();

                        if (string.IsNullOrWhiteSpace(snippet) == false)
                            metadata["snippet"] = snippet;

                        pendingUpserts.Add(new SearchVectorUpsert(variationKey, i, embeddings[i].Vector, metadata));
                    }

                    logger.LogDebug("Indexed document {DocumentId} culture {Culture} segment {Segment} in {IndexAlias} ({ChunkCount} chunks)", id, culture ?? "invariant", segment ?? "invariant", indexAlias, chunkTexts.Count);
                }
                catch (Exception exception)
                {
                    failed = true;
                    logger.LogError(exception, "Failed to generate embeddings for document {DocumentId} culture {Culture} segment {Segment} in {IndexAlias}", id, culture ?? "invariant", segment ?? "invariant", indexAlias);
                }
            }
        }
        finally
        {
            variationContextAccessor?.VariationContext = previousVariationContext;
        }

        if (failed)
        {
            logger.LogWarning("Skipped vector replacement for document {DocumentId} in {IndexAlias} because one or more embeddings failed", id, indexAlias);
            return;
        }

        if (pendingUpserts.Count == 0)
        {
            logger.LogDebug("Skipped vector replacement for document {DocumentId} in {IndexAlias} because no vector upserts were prepared", id, indexAlias);
            return;
        }

        await vectorStore.DeleteDocumentAsync(indexAlias, documentId);

        if (vectorStore is QdrantVectorStore qdrantVectorStore)
        {
            await qdrantVectorStore.UpsertManyAsync(
                indexAlias,
                documentId,
                pendingUpserts.Select(pendingUpsert => new AIVectorEntry(documentId, pendingUpsert.VariationKey, pendingUpsert.ChunkIndex, pendingUpsert.Vector, pendingUpsert.Metadata)));
        }
        else
        {
            foreach (var pendingUpsert in pendingUpserts)
                await vectorStore.UpsertAsync(indexAlias, documentId, pendingUpsert.VariationKey, pendingUpsert.ChunkIndex, pendingUpsert.Vector, pendingUpsert.Metadata);
        }
    }

    /// <summary>
    /// Deletes vector entries for the specified content identifiers from every variation collection in the index.
    /// </summary>
    public async Task DeleteAsync(string indexAlias, IEnumerable<Guid> ids)
    {
        foreach (var id in ids)
            await vectorStore.DeleteDocumentAsync(indexAlias, id.ToString("D"));
    }

    /// <summary>
    /// Clears all vector collections for the specified Umbraco search index alias.
    /// </summary>
    public async Task ResetAsync(string indexAlias)
    {
        await vectorStore.ResetAsync(indexAlias);
        logger.LogInformation("Reset vector index {IndexAlias}", indexAlias);
    }

    /// <summary>
    /// Gets health and document-count metadata for the vector index.
    /// </summary>
    public async Task<IndexMetadata> GetMetadataAsync(string indexAlias) => new(
        await vectorStore.GetDocumentCountAsync(indexAlias),
        HealthStatus.Healthy,
        "ai-vector-search-provider");

    /// <summary>
    /// Creates repeated breadcrumb, category, title, and configured-property context prepended to every chunk.
    /// </summary>
    private string CreateChunkContext(IPublishedContent? content, SearchIndexDocument? searchIndexDocument, IReadOnlyDictionary<string, string> textReplacements)
    {
        var prefix = string.Empty;

        if (content is not null)
        {
            if (TemplateContainsAlias(searchIndexDocument?.SearchText.MarkdownTemplate, "Breadcrumb") == false)
            {
                var breadcrumb = CreateBreadcrumb(content);

                if (string.IsNullOrWhiteSpace(breadcrumb) == false)
                    prefix += $"> {ApplyTextReplacements(breadcrumb, textReplacements)}\n\n";
            }
        }

        if (content is not null)
        {
            var category = CreateDocumentCategory(content, searchIndexDocument);

            if (string.IsNullOrWhiteSpace(category) == false &&
                TemplateContainsAnyAlias(searchIndexDocument?.SearchText.MarkdownTemplate, searchIndexDocument?.Chunking.Context.CategoryPropertyAliases) == false)
            {
                prefix += $"{ApplyTextReplacements(category, textReplacements)}\n\n";
            }
        }

        foreach (var propertyAlias in searchIndexDocument?.Chunking.Context.TitlePropertyAliases ?? [])
        {
            var title = content is null
                ? string.Empty
                : ResolveContextPropertyText(propertyAlias, content, searchIndexDocument, content);

            if (string.IsNullOrWhiteSpace(title) ||
                TemplateContainsAlias(searchIndexDocument?.SearchText.MarkdownTemplate, propertyAlias))
                continue;

            prefix += $"# {ApplyTextReplacements(title, textReplacements)}\n\n";

            break;
        }

        foreach (var propertyAlias in searchIndexDocument?.Chunking.Context.SectionTitlePropertyAliases ?? [])
        {
            var title = content is null
                ? string.Empty
                : ResolveContextPropertyText(propertyAlias, content, searchIndexDocument, content);

            if (string.IsNullOrWhiteSpace(title) ||
                TemplateContainsAlias(searchIndexDocument?.SearchText.MarkdownTemplate, propertyAlias))
                continue;

            prefix += $"## {ApplyTextReplacements(title, textReplacements)}\n\n";

            break;
        }

        if (content is not null)
        {
            foreach (var propertyAlias in searchIndexDocument?.Chunking.Context.AdditionalPropertyAliases ?? [])
            {
                if (searchIndexDocument?.Chunking.Context.CategoryPropertyAliases.Contains(propertyAlias, StringComparer.OrdinalIgnoreCase) == true ||
                    searchIndexDocument?.Chunking.Context.TitlePropertyAliases.Contains(propertyAlias, StringComparer.OrdinalIgnoreCase) == true ||
                    searchIndexDocument?.Chunking.Context.SectionTitlePropertyAliases.Contains(propertyAlias, StringComparer.OrdinalIgnoreCase) == true)
                {
                    continue;
                }

                var value = ResolveContextPropertyText(propertyAlias, content, searchIndexDocument, content);

                if (string.IsNullOrWhiteSpace(value) == false &&
                    TemplateContainsAlias(searchIndexDocument?.SearchText.MarkdownTemplate, propertyAlias) == false)
                {
                    prefix += $"{ApplyTextReplacements(value, textReplacements)}\n\n";
                }
            }
        }

        return prefix;
    }

    /// <summary>
    /// Determines whether a Markdown template already contains any configured aliases.
    /// </summary>
    private static bool TemplateContainsAnyAlias(string? template, IEnumerable<string>? aliases) =>
        aliases?.Any(alias => TemplateContainsAlias(template, alias)) == true;

    /// <summary>
    /// Determines whether a Markdown template already contains an alias token.
    /// </summary>
    private static bool TemplateContainsAlias(string? template, string alias)
    {
        if (string.IsNullOrWhiteSpace(template))
            return false;

        return TemplateTokenRegex()
            .Matches(template)
            .SelectMany(match => ParseTemplateTokenWeight(match.Groups["token"].Value).Token.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Any(token => token.Equals(alias, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Applies the current code-supplied replacement map to searchable text.
    /// </summary>
    private static string ApplyTextReplacements(string text, IReadOnlyDictionary<string, string> textReplacements)
    {
        if (string.IsNullOrEmpty(text) || textReplacements.Count == 0)
            return text;

        foreach (var (find, replace) in textReplacements
                     .Where(replacement => string.IsNullOrEmpty(replacement.Key) == false)
                     .OrderByDescending(replacement => replacement.Key.Length))
        {
            text = text.Replace(find, replace, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>
    /// Determines whether a property is backed by the Umbraco Markdown editor and can be indexed without HTML conversion.
    /// </summary>
    private static bool IsMarkdownEditor(IPublishedElement element, string propertyAlias) =>
        element.GetProperty(propertyAlias)?.PropertyType.EditorAlias
            .Equals("Umbraco.MarkdownEditor", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Determines whether a property is backed by an Umbraco block-list editor.
    /// </summary>
    private static bool IsBlockListEditor(IPublishedElement element, string propertyAlias) =>
        element.GetProperty(propertyAlias)?.PropertyType.EditorAlias
            .Equals("Umbraco.BlockList", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Gets property text as Markdown, preserving Markdown-editor values and converting other HTML values.
    /// </summary>
    private static string GetPropertyText(IPublishedElement element, string propertyAlias) =>
        IsMarkdownEditor(element, propertyAlias)
            ? GetPropertyValue(element, propertyAlias)
            : GetPropertyTextValue(element, propertyAlias).HtmlToSearchText();

    /// <summary>
    /// Gets the converted published value without relying on the global Umbraco fallback service.
    /// </summary>
    private static string GetPropertyValue(IPublishedElement element, string propertyAlias) =>
        GetScalarText(element.GetProperty(propertyAlias)?.GetValue());

    /// <summary>
    /// Gets scalar property text while ignoring complex picker and block-list values.
    /// </summary>
    private static string GetPropertyTextValue(IPublishedElement element, string propertyAlias)
    {
        var value = element.GetProperty(propertyAlias)?.GetValue();

        return IsComplexSearchValue(value)
            ? string.Empty
            : GetScalarText(value);
    }

    /// <summary>
    /// Determines whether a value must be traversed explicitly instead of stringified.
    /// </summary>
    private static bool IsComplexSearchValue(object? value) =>
        value is IPublishedElement or BlockListItem or IEnumerable<BlockListItem> or IEnumerable<IPublishedElement> ||
        value is IEnumerable enumerable and not string && enumerable
            .Cast<object?>()
            .Any(item => item is IPublishedElement or BlockListItem);

    /// <summary>
    /// Converts scalar or scalar-list values into searchable text.
    /// </summary>
    private static string GetScalarText(object? value)
    {
        if (value is null)
            return string.Empty;

        if (value is string text)
            return GetStringText(text);

        if (value is IEnumerable enumerable)
        {
            return string.Join(
                "\n",
                GetScalarListItems(enumerable));
        }

        return value.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Converts string source values, including JSON string arrays, into searchable text.
    /// </summary>
    private static string GetStringText(string value)
    {
        var text = value.Trim();

        if (text.StartsWith("[", StringComparison.Ordinal) && text.EndsWith("]", StringComparison.Ordinal))
        {
            try
            {
                var values = JsonSerializer.Deserialize<string[]>(text);

                if (values?.Length > 0)
                    return string.Join("\n", values.Where(item => string.IsNullOrWhiteSpace(item) == false));
            }
            catch
            {
                return value;
            }
        }

        return value;
    }

    /// <summary>
    /// Converts an enumerable collection of scalar values into de-duplicated display strings.
    /// </summary>
    private static IEnumerable<string> GetScalarListItems(IEnumerable values)
    {
        return values
            .Cast<object?>()
            .Where(item => item is not null)
            .SelectMany(item => GetScalarListItemLines(item!))
            .Where(text => string.IsNullOrWhiteSpace(text) == false)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Converts a scalar-list item into searchable lines, dropping label-only first lines from grouped values.
    /// </summary>
    private static IEnumerable<string> GetScalarListItemLines(object value)
    {
        var rawText = value.ToString() ?? string.Empty;
        var text = GetStringText(rawText);
        var lines = text
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return rawText.TrimStart().StartsWith("[", StringComparison.Ordinal) == false &&
               lines.Length > 1 &&
               lines[0].StartsWith("- ", StringComparison.Ordinal) == false
            ? lines.Skip(1)
            : lines;
    }

    /// <summary>
    /// Formats scalar values as a Markdown list.
    /// </summary>
    private static string FormatMarkdownList(IEnumerable values)
    {
        var items = GetScalarListItems(values).ToList();

        return items.Count == 0
            ? string.Empty
            : string.Join("\n", items.Select(item => $"- {item}"));
    }

    /// <summary>
    /// Resolves the first configured category or taxonomy property value for metadata and chunk context.
    /// </summary>
    private string CreateDocumentCategory(IPublishedContent? content, SearchIndexDocument? searchIndexDocument)
    {
        foreach (var propertyAlias in searchIndexDocument?.Chunking.Context.CategoryPropertyAliases ?? [])
        {
            var category = content is null
                ? string.Empty
                : ResolveContextPropertyText(propertyAlias, content, searchIndexDocument, content);

            if (string.IsNullOrWhiteSpace(category) == false)
                return category;
        }

        return string.Empty;
    }

    /// <summary>
    /// Resolves direct and dotted context property aliases.
    /// </summary>
    private string ResolveContextPropertyText(string propertyAlias, IPublishedElement element, SearchIndexDocument? searchIndexDocument, IPublishedContent? rootContent)
    {
        if (propertyAlias.Contains('.', StringComparison.Ordinal))
            return ResolveTemplatePath(propertyAlias, element, searchIndexDocument ?? new SearchIndexDocument(), rootContent, 0);

        return GetPropertyText(element, propertyAlias);
    }

    /// <summary>
    /// Creates a root-to-current breadcrumb path from the content tree.
    /// </summary>
    private static string CreateBreadcrumb(IPublishedContent content)
    {
        var parts = content.AncestorsOrSelf()
            .OrderBy(item => item.Level)
            .Select(item => item.Name)
            .Where(name => string.IsNullOrWhiteSpace(name) == false)
            .ToList();

        return string.Join(" > ", parts);
    }

    /// <summary>
    /// Splits rendered search text into token-limited chunks and adds stable document or section context to each chunk.
    /// </summary>
    private List<SearchChunk> CreateChunks(string text, string prefix, SearchIndexDocument? searchIndexDocument, AITextChunkingOptions chunkingOptions)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return textChunker.ChunkText(prefix.Trim(), chunkingOptions)
                .Select(chunk => new SearchChunk(chunk.Text.Trim()))
                .Where(chunk => string.IsNullOrWhiteSpace(chunk.Text) == false)
                .DistinctBy(chunk => chunk.Text, StringComparer.Ordinal)
                .ToList();
        }

        var sections = searchIndexDocument?.Chunking.UseHeadingAwareChunking ?? true
            ? text.SplitMarkdownSections()
            : [text];

        var chunks = new List<SearchChunk>();

        foreach (var section in sections)
        {
            var sectionContext = CreateSectionContext(section);

            foreach (var chunk in textChunker.ChunkText(section, chunkingOptions))
            {
                var originalText = chunk.Text.Trim();
                var contextualPrefix = string.IsNullOrWhiteSpace(sectionContext) || originalText.StartsWith(sectionContext, StringComparison.OrdinalIgnoreCase)
                    ? prefix
                    : $"{prefix}{sectionContext}\n\n";
                var chunkText = $"{contextualPrefix}{originalText}".Trim();

                if (string.IsNullOrWhiteSpace(chunkText) == false)
                    chunks.Add(new SearchChunk(chunkText));
            }
        }

        return chunks
            .DistinctBy(chunk => chunk.Text, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Extracts the first Markdown heading from a section for repeated section context.
    /// </summary>
    private static string CreateSectionContext(string section)
    {
        return section
            .ReplaceLineEndings("\n")
            .Split('\n')
            .FirstOrDefault(line => line.StartsWith('#'))?
            .Trim() ?? string.Empty;
    }

    /// <summary>
    /// Resolves distinct culture and segment variations from the index event, falling back to field variations.
    /// </summary>
    private static IEnumerable<Variation> GetVariants(IEnumerable<Variation> variations, IReadOnlyCollection<IndexField> fields)
    {
        var variationList = variations.ToList();
        var variantSource = variationList.Count > 0
            ? variationList
            : fields.Select(field => new Variation(field.Culture, field.Segment));

        return variantSource
            .DefaultIfEmpty(new Variation(null, null))
            .GroupBy(AiVariationKey.Create, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
    }

    /// <summary>
    /// Determines whether an index field applies to the requested culture and segment variation.
    /// </summary>
    private static bool FieldAppliesToVariation(IndexField field, Variation variation)
    {
        var cultureMatches = field.Culture is null || variation.Culture is not null && field.Culture.Equals(variation.Culture, StringComparison.OrdinalIgnoreCase);
        var segmentMatches = field.Segment is null || variation.Segment is not null && field.Segment.Equals(variation.Segment, StringComparison.OrdinalIgnoreCase);

        return cultureMatches && segmentMatches;
    }

    /// <summary>
    /// Extracts Markdown search text from a template, boosted Examine text ranks, or nested block lists.
    /// </summary>
    private List<SearchTextPart> ExtractTextFromFields(IEnumerable<IndexField>? fields, SearchIndexDocument? searchIndexDocument, IPublishedContent? content)
    {
        if (content is not null && string.IsNullOrWhiteSpace(searchIndexDocument?.SearchText.MarkdownTemplate) == false)
        {
            var templatedText = RenderMarkdownTemplate(searchIndexDocument.SearchText.MarkdownTemplate, content, searchIndexDocument, content, 0);

            return string.IsNullOrWhiteSpace(templatedText)
                ? []
                : [new SearchTextPart(templatedText)];
        }

        List<SearchTextPart> boostedTexts = [];
        List<SearchTextPart> texts = [];

        // ReSharper disable UnusedVariable
        foreach (var (fieldName, value, culture, segment) in fields ?? [])
        {
            var blockListText = content is null
                ? []
                : GetBlockSearchText(content, searchIndexDocument, fieldName, 0);

            if (blockListText.Count > 0)
            {
                boostedTexts.AddRange(blockListText);
                continue;
            }

            var isMarkdown = content is not null && IsMarkdownEditor(content, fieldName);

            if (value.TextsR1 is not null)
            {
                var valueTexts = value.TextsR1.Select(text => new SearchTextPart(isMarkdown ? text : text.HtmlToSearchText()));
                boostedTexts.AddRange(valueTexts);
            }

            if (value.TextsR2 is not null)
            {
                var valueTexts = value.TextsR2.Select(text => new SearchTextPart(isMarkdown ? text : text.HtmlToSearchText()));
                texts.AddRange(valueTexts);
            }

            if (value.TextsR3 is not null)
            {
                var valueTexts = value.TextsR3.Select(text => new SearchTextPart(isMarkdown ? text : text.HtmlToSearchText()));
                texts.AddRange(valueTexts);
            }

            if (value.Texts is not null)
            {
                var valueTexts = value.Texts.Select(text => new SearchTextPart(isMarkdown ? text : text.HtmlToSearchText()));
                texts.AddRange(valueTexts);
            }
        }
        
        List<SearchTextPart> parts = new(boostedTexts.Count + texts.Count);
        parts.AddRange(boostedTexts);
        parts.AddRange(texts);

        return parts;
    }

    /// <summary>
    /// Renders a Markdown template against a published element while protecting escaped double-brace literals.
    /// </summary>
    private string RenderMarkdownTemplate(string template, IPublishedElement element, SearchIndexDocument searchIndexDocument, IPublishedContent? rootContent, int depth)
    {
        var protectedTemplate = template
            .Replace("{{", "\uE000", StringComparison.Ordinal)
            .Replace("}}", "\uE001", StringComparison.Ordinal);

        var rendered = TemplateTokenRegex().Replace(
            protectedTemplate,
            match => ResolveTemplateToken(match.Groups["token"].Value, element, searchIndexDocument, rootContent, depth));

        return CleanRenderedTemplate(rendered
            .Replace("\uE000", "{", StringComparison.Ordinal)
            .Replace("\uE001", "}", StringComparison.Ordinal));
    }

    /// <summary>
    /// Resolves one template token, trying pipe-delimited fallback aliases until text is found.
    /// </summary>
    private string ResolveTemplateToken(string token, IPublishedElement element, SearchIndexDocument searchIndexDocument, IPublishedContent? rootContent, int depth)
    {
        var (unweightedToken, weight) = ParseTemplateTokenWeight(token);

        foreach (var alias in unweightedToken.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = ResolveTemplateAlias(alias, element, searchIndexDocument, rootContent, depth);

            if (string.IsNullOrWhiteSpace(value) == false)
                return value.ApplyFieldWeight(weight);
        }

        return string.Empty;
    }

    /// <summary>
    /// Parses a trailing :weight suffix from a template token, preserving earlier colons as alias text.
    /// </summary>
    private static (string Token, int Weight) ParseTemplateTokenWeight(string token)
    {
        var match = TemplateWeightRegex().Match(token);

        return match.Success && int.TryParse(match.Groups["weight"].Value, out var weight)
            ? (token[..match.Index], weight)
            : (token, 1);
    }

    /// <summary>
    /// Resolves one template alias to built-in content metadata, a property value, or nested block-list text.
    /// </summary>
    private string ResolveTemplateAlias(string alias, IPublishedElement element, SearchIndexDocument searchIndexDocument, IPublishedContent? rootContent, int depth)
    {
        if (alias.Contains('.', StringComparison.Ordinal))
            return ResolveTemplatePath(alias, element, searchIndexDocument, rootContent, depth);

        if (rootContent is not null)
        {
            if (alias.Equals("Breadcrumb", StringComparison.OrdinalIgnoreCase))
                return CreateBreadcrumb(rootContent);

            if (alias.Equals("Url", StringComparison.OrdinalIgnoreCase))
                return rootContent.Url();

            if (alias.Equals("Name", StringComparison.OrdinalIgnoreCase))
                return rootContent.Name;
        }

        if (alias.Equals("ContentType", StringComparison.OrdinalIgnoreCase))
            return element.ContentType.Alias;

        if (element.HasProperty(alias) == false)
            return string.Empty;

        var blockParts = IsBlockListEditor(element, alias)
            ? GetBlockSearchText(element, searchIndexDocument, alias, depth + 1)
            : [];

        if (blockParts.Count > 0)
            return string.Join("\n\n", blockParts.Select(part => part.Text));

        return GetPropertyText(element, alias);
    }

    /// <summary>
    /// Resolves dotted template paths through single or multiple picker and block-list values.
    /// </summary>
    private string ResolveTemplatePath(string path, IPublishedElement element, SearchIndexDocument searchIndexDocument, IPublishedContent? rootContent, int depth)
    {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 2)
            return string.Empty;

        var current = ResolveTemplatePathRoot(parts[0], element, rootContent);

        for (var i = 1; i < parts.Length; i++)
        {
            current = ResolveTemplatePathPart(current, parts[i], searchIndexDocument, depth);

            if (current.Count == 0)
                return string.Empty;
        }

        var values = current
            .Select(value => value.Text)
            .Where(text => string.IsNullOrWhiteSpace(text) == false)
            .ToList();

        var separator = values.Count > 0 && values.All(IsMarkdownListText)
            ? "\n"
            : "\n\n";

        return string.Join(separator, values);
    }

    /// <summary>
    /// Determines whether text contains only Markdown list items.
    /// </summary>
    private static bool IsMarkdownListText(string value)
    {
        var lines = value.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return lines.Length > 0 && lines.All(line => line.StartsWith("- ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Resolves the first segment in a dotted template path.
    /// </summary>
    private List<TemplatePathValue> ResolveTemplatePathRoot(string alias, IPublishedElement element, IPublishedContent? rootContent)
    {
        if (rootContent is not null && alias.Equals("Name", StringComparison.OrdinalIgnoreCase))
            return [new TemplatePathValue(rootContent.Name)];

        if (rootContent is not null && alias.Equals("Url", StringComparison.OrdinalIgnoreCase))
            return [new TemplatePathValue(rootContent.Url())];

        if (alias.Equals("ContentType", StringComparison.OrdinalIgnoreCase))
            return [new TemplatePathValue(element.ContentType.Alias)];

        if (element.HasProperty(alias) == false)
            return [];

        if (element is IPublishedContent publishedContent &&
            contentService?.GetById(publishedContent.Key) is { } serviceContent &&
            serviceContent.HasProperty(alias))
        {
            var serviceValue = GetContentPropertyValue(serviceContent, alias);

            if (IsEmptySearchValue(serviceValue) == false)
                return ExpandTemplatePathValue(serviceValue, alias);
        }

        return ExpandTemplatePathValue(GetSearchPropertyValue(element, alias), alias);
    }

    /// <summary>
    /// Resolves one segment in a dotted template path against all current values.
    /// </summary>
    private List<TemplatePathValue> ResolveTemplatePathPart(IEnumerable<TemplatePathValue> values, string alias, SearchIndexDocument searchIndexDocument, int depth)
    {
        var results = new List<TemplatePathValue>();

        foreach (var value in values)
        {
            if (value.Value is IPublishedElement element)
            {
                if (alias.Equals("ContentType", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new TemplatePathValue(element.ContentType.Alias));
                    continue;
                }

                if (element is IPublishedContent content)
                {
                    if (alias.Equals("Name", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new TemplatePathValue(content.Name));
                        continue;
                    }

                    if (alias.Equals("Url", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new TemplatePathValue(content.Url()));
                        continue;
                    }
                }

                if (element.HasProperty(alias) == false)
                    continue;

                var blockParts = IsBlockListEditor(element, alias)
                    ? GetBlockSearchText(element, searchIndexDocument, alias, depth + 1)
                    : [];

                if (blockParts.Count > 0)
                {
                    results.AddRange(blockParts.Select(part => new TemplatePathValue(part.Text)));
                    continue;
                }

                if (element is IPublishedContent publishedContent &&
                    contentService?.GetById(publishedContent.Key) is { } fallbackContent &&
                    fallbackContent.HasProperty(alias))
                {
                    var serviceValue = GetContentPropertyValue(fallbackContent, alias);

                    if (IsEmptySearchValue(serviceValue) == false)
                    {
                        results.AddRange(ExpandTemplatePathValue(serviceValue, alias));
                        continue;
                    }
                }

                results.AddRange(ExpandTemplatePathValue(GetSearchPropertyValue(element, alias), alias, element));
                continue;
            }

            if (value.Value is IContent serviceContent)
            {
                results.AddRange(ResolveContentServicePathPart(serviceContent, alias));
                continue;
            }

            if (value.Value is int userId && ResolveUserProperty(userId, alias) is { } userValue)
                results.Add(new TemplatePathValue(userValue));
        }

        return results;
    }

    /// <summary>
    /// Resolves common properties from Umbraco user picker values.
    /// </summary>
    private string? ResolveUserProperty(int userId, string alias)
    {
        var user = userService?.GetUserById(userId);

        if (user is null)
            return null;

        return alias switch
        {
            _ when alias.Equals("Name", StringComparison.OrdinalIgnoreCase) => user.Name,
            _ when alias.Equals("Email", StringComparison.OrdinalIgnoreCase) => user.Email,
            _ when alias.Equals("UserName", StringComparison.OrdinalIgnoreCase) => user.Username,
            _ when alias.Equals("Id", StringComparison.OrdinalIgnoreCase) => user.Id.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
    }

    /// <summary>
    /// Gets a property value using the published value converter when available.
    /// </summary>
    private static object? GetSearchPropertyValue(IPublishedElement element, string propertyAlias)
    {
        var multipleContent = TryGetSearchPropertyValue<IEnumerable<IPublishedContent>>(element, propertyAlias)?.ToList();

        if (multipleContent?.Count > 0)
            return multipleContent;

        var singleContent = TryGetSearchPropertyValue<IPublishedContent>(element, propertyAlias);

        if (singleContent is not null)
            return singleContent;

        var blockList = TryGetSearchPropertyValue<IEnumerable<BlockListItem>>(element, propertyAlias)?.ToList();

        if (blockList?.Count > 0)
            return blockList;

        var block = TryGetSearchPropertyValue<BlockListItem>(element, propertyAlias);

        if (block is not null)
            return block;

        var multipleText = TryGetSearchPropertyValue<IEnumerable<string>>(element, propertyAlias)?.ToList();

        if (multipleText?.Count > 0)
            return multipleText;

        var convertedValue = TryGetSearchPropertyValue<object>(element, propertyAlias);

        return IsEmptyEnumerable(convertedValue)
            ? GetPropertyFallbackValue(element, propertyAlias)
            : convertedValue ?? GetPropertyFallbackValue(element, propertyAlias);
    }

    /// <summary>
    /// Gets a typed published value, returning null when a converter cannot supply that shape.
    /// </summary>
    private static T? TryGetSearchPropertyValue<T>(IPublishedElement element, string propertyAlias)
    {
        try
        {
            return element.Value<T>(propertyAlias, fallback: Fallback.ToDefaultValue, defaultValue: default);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// Gets a scalar or enumerable content service property value.
    /// </summary>
    private object? GetContentPropertyValue(IContent content, string propertyAlias)
    {
        var culture = variationContextAccessor?.VariationContext?.Culture;
        var segment = variationContextAccessor?.VariationContext?.Segment;
        var property = content.Properties.FirstOrDefault(property => property.Alias.Equals(propertyAlias, StringComparison.OrdinalIgnoreCase));
        var cultures = GetContentValueCultures(content, property, culture).ToList();
        var segments = GetContentValueSegments(property, segment).ToList();

        try
        {
            foreach (var valueCulture in cultures)
            {
                foreach (var valueSegment in segments)
                {
                    var value =
                        property?.GetValue(valueCulture, valueSegment, published: true) ??
                        property?.GetValue(valueCulture, valueSegment) ??
                        content.GetValue(propertyAlias, valueCulture, valueSegment, published: true) ??
                        content.GetValue(propertyAlias, valueCulture, valueSegment);

                    if (IsEmptySearchValue(value) == false)
                        return value;
                }
            }

            return property?.GetValue(published: true) ??
                   property?.GetValue() ??
                   GetStoredContentPropertyValue(content, propertyAlias, culture, segment);
        }
        catch
        {
            return GetStoredContentPropertyValue(content, propertyAlias, culture, segment);
        }
    }

    /// <summary>
    /// Resolves one dotted path segment directly from ContentService content.
    /// </summary>
    private List<TemplatePathValue> ResolveContentServicePathPart(IContent content, string alias)
    {
        if (alias.Equals("Name", StringComparison.OrdinalIgnoreCase))
            return [new TemplatePathValue(content.Name)];

        var property = content.Properties.FirstOrDefault(property => property.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));

        if (property is null && content.HasProperty(alias) == false)
            return [];

        if (property is null)
        {
            var contentValue = GetContentPropertyValue(content, alias);

            return IsEmptySearchValue(contentValue)
                ? []
                : ExpandTemplatePathValue(contentValue, alias);
        }

        var editorAlias = property.PropertyType.PropertyEditorAlias;
        var values = GetContentPropertyValues(content, property)
            .Where(IsStoredValuePresent)
            .DistinctBy(GetSearchValueKey, StringComparer.Ordinal)
            .ToList();

        if (IsContentPickerEditor(editorAlias))
            return values.SelectMany(value => ExpandTemplatePathValue(value, alias)).ToList();

        if (IsMultipleTextStringEditor(editorAlias))
        {
            var text = FormatMarkdownList(values);

            return string.IsNullOrWhiteSpace(text)
                ? []
                : [new TemplatePathValue(text)];
        }

        var value = values.FirstOrDefault(IsStoredValuePresent);

        if (IsEmptySearchValue(value) == false)
            return ExpandTemplatePathValue(value, alias);

        var publishedValue = GetPublishedContentPropertyValue(content.Key, alias);

        return IsEmptySearchValue(publishedValue.Value)
            ? []
            : ExpandTemplatePathValue(publishedValue.Value, alias, publishedValue.Owner);
    }

    /// <summary>
    /// Gets all stored and API-visible values for a content-service property.
    /// </summary>
    private IEnumerable<object?> GetContentPropertyValues(IContent content, IProperty property)
    {
        var culture = variationContextAccessor?.VariationContext?.Culture;
        var segment = variationContextAccessor?.VariationContext?.Segment;
        var cultures = GetContentValueCultures(content, property, culture).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var segments = GetContentValueSegments(property, segment).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var valueCulture in cultures)
        {
            foreach (var valueSegment in segments)
            {
                yield return property.GetValue(valueCulture, valueSegment, published: true);
                yield return property.GetValue(valueCulture, valueSegment);
                yield return content.GetValue(property.Alias, valueCulture, valueSegment, published: true);
                yield return content.GetValue(property.Alias, valueCulture, valueSegment);
            }
        }

        yield return property.GetValue(published: true);
        yield return property.GetValue();
        yield return content.GetValue(property.Alias, published: true);
        yield return content.GetValue(property.Alias);

        foreach (var value in property.Values.SelectMany(GetStoredPropertyValues))
            yield return value;
    }

    /// <summary>
    /// Creates a stable key for de-duplicating raw content-service values.
    /// </summary>
    private static string GetSearchValueKey(object? value) =>
        value switch
        {
            null => string.Empty,
            string text => text,
            IEnumerable enumerable => string.Join("\u001F", enumerable.Cast<object?>().Select(item => item?.ToString() ?? string.Empty)),
            _ => value.ToString() ?? string.Empty
        };

    /// <summary>
    /// Determines whether an editor stores content picker values.
    /// </summary>
    private static bool IsContentPickerEditor(string? editorAlias) =>
        editorAlias is not null &&
        (editorAlias.Equals("Umbraco.MultiNodeTreePicker", StringComparison.OrdinalIgnoreCase) ||
         editorAlias.Equals("Umbraco.ContentPicker", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Determines whether an editor stores multiple text string values.
    /// </summary>
    private static bool IsMultipleTextStringEditor(string? editorAlias) =>
        editorAlias?.Equals("Umbraco.MultipleTextString", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Gets a property from the published cache when content-service values are empty.
    /// </summary>
    private (object? Value, IPublishedElement? Owner) GetPublishedContentPropertyValue(Guid key, string propertyAlias)
    {
        try
        {
            using var contextRef = umbracoContextFactory.EnsureUmbracoContext();
            var content = contextRef.UmbracoContext.Content.GetById(key);

            if (content?.HasProperty(propertyAlias) != true)
                return (null, null);

            return (GetSearchPropertyValue(content, propertyAlias), content);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed to resolve published content property {PropertyAlias} for {ContentKey}", propertyAlias, key);
            return (null, null);
        }
    }

    /// <summary>
    /// Gets candidate cultures for content-service value reads.
    /// </summary>
    private static IEnumerable<string?> GetContentValueCultures(IContent content, IProperty? property, string? culture)
    {
        yield return culture;
        yield return null;

        foreach (var publishedCulture in content.PublishedCultures)
            yield return publishedCulture;

        foreach (var editedCulture in content.EditedCultures ?? [])
            yield return editedCulture;

        foreach (var valueCulture in property?.Values.Select(value => value.Culture) ?? [])
            yield return valueCulture;
    }

    /// <summary>
    /// Gets candidate segments for content-service value reads.
    /// </summary>
    private static IEnumerable<string?> GetContentValueSegments(IProperty? property, string? segment)
    {
        yield return segment;
        yield return null;

        foreach (var valueSegment in property?.Values.Select(value => value.Segment) ?? [])
            yield return valueSegment;
    }

    /// <summary>
    /// Gets a raw property value directly from stored content values.
    /// </summary>
    private static object? GetStoredContentPropertyValue(IContent content, string propertyAlias, string? culture, string? segment)
    {
        var property = content.Properties.FirstOrDefault(property => property.Alias.Equals(propertyAlias, StringComparison.OrdinalIgnoreCase));

        if (property is null)
            return null;

        var values = property.Values.ToList();

        return values
                   .Where(value => CultureMatches(value.Culture, culture) && SegmentMatches(value.Segment, segment))
                   .SelectMany(GetStoredPropertyValues)
                   .FirstOrDefault(IsStoredValuePresent) ??
               values
                   .SelectMany(GetStoredPropertyValues)
                   .FirstOrDefault(IsStoredValuePresent);
    }

    /// <summary>
    /// Yields published and edited stored values.
    /// </summary>
    private static IEnumerable<object?> GetStoredPropertyValues(IPropertyValue value)
    {
        yield return value.PublishedValue;
        yield return value.EditedValue;
    }

    /// <summary>
    /// Matches invariant values when no culture is active, or exact culture values when it is.
    /// </summary>
    private static bool CultureMatches(string? valueCulture, string? culture) =>
        string.IsNullOrWhiteSpace(culture)
            ? string.IsNullOrWhiteSpace(valueCulture)
            : culture.Equals(valueCulture, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Matches neutral values when no segment is active, or exact segment values when it is.
    /// </summary>
    private static bool SegmentMatches(string? valueSegment, string? segment) =>
        string.IsNullOrWhiteSpace(segment)
            ? string.IsNullOrWhiteSpace(valueSegment)
            : segment.Equals(valueSegment, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Determines whether a stored content value has searchable content.
    /// </summary>
    private static bool IsStoredValuePresent(object? value) =>
        IsEmptySearchValue(value) == false;

    /// <summary>
    /// Determines whether a converted picker value is empty and should fall back to source value.
    /// </summary>
    private static bool IsEmptyEnumerable(object? value) =>
        value is IEnumerable enumerable and not string && enumerable.Cast<object?>().Any() == false;

    /// <summary>
    /// Determines whether a resolved value has no searchable content.
    /// </summary>
    private static bool IsEmptySearchValue(object? value) =>
        value is null || IsEmptyEnumerable(value) || value is string text && string.IsNullOrWhiteSpace(text);

    /// <summary>
    /// Gets the raw published property value, falling back to source value when converted value is empty.
    /// </summary>
    private static object? GetPropertyFallbackValue(IPublishedElement element, string propertyAlias)
    {
        var property = element.GetProperty(propertyAlias);
        var value = property?.GetValue();

        return IsEmptyEnumerable(value)
            ? property?.GetSourceValue()
            : value;
    }

    /// <summary>
    /// Expands picker, block-list, and scalar values from a template path segment.
    /// </summary>
    private List<TemplatePathValue> ExpandTemplatePathValue(object? value, string fieldName, IPublishedElement? owner = null)
    {
        return value switch
        {
            null => [],
            string text => owner is null
                ? ExpandPickerSourceText(text, fieldName)
                : [new TemplatePathValue(GetOwnedPropertyTemplateText(owner, fieldName, text))], BlockListItem item => [new TemplatePathValue(item.Content)],
            IEnumerable<BlockListItem> items => items.Select(item => new TemplatePathValue(item.Content)).ToList(), IContent content => [new TemplatePathValue(content)],
            IEnumerable<IContent> contents => contents.Select(content => new TemplatePathValue(content)).ToList(), IPublishedContent content => [new TemplatePathValue(content)],
            IEnumerable<IPublishedContent> contents => contents.Select(content => new TemplatePathValue(content)).ToList(), IPublishedElement element => [new TemplatePathValue(element)],
            IEnumerable<IPublishedElement> elements => elements.Select(element => new TemplatePathValue(element)).ToList(), IEnumerable enumerable when owner is not null && IsComplexSearchValue(value) == false =>
            [
                new TemplatePathValue(
                    GetOwnedPropertyTemplateText(owner, fieldName, enumerable))
            ],
            IEnumerable enumerable when value is not string => enumerable
                .Cast<object?>()
                .SelectMany(item => ExpandTemplatePathValue(item, fieldName))
                .ToList(),
            _ => ExpandScalarTemplatePathValue(value, fieldName)
        };
    }

    /// <summary>
    /// Formats a final scalar property value according to its editor type.
    /// </summary>
    private static string GetOwnedPropertyTemplateText(IPublishedElement owner, string fieldName, object value)
    {
        if (IsMultipleTextStringEditor(owner.GetProperty(fieldName)?.PropertyType.EditorAlias))
            return FormatMarkdownList(value is IEnumerable enumerable and not string ? enumerable : new[] { value });

        var text = GetScalarText(value);

        return IsMarkdownEditor(owner, fieldName)
            ? text
            : text.HtmlToSearchText();
    }

    /// <summary>
    /// Expands scalar values, including UDI instances whose string form points at content.
    /// </summary>
    private List<TemplatePathValue> ExpandScalarTemplatePathValue(object value, string fieldName)
    {
        if (IsComplexSearchValue(value))
            return [];

        var text = value.ToString() ?? string.Empty;

        return text.Contains("umb://", StringComparison.OrdinalIgnoreCase)
            ? ExpandPickerSourceText(text, fieldName)
            : [new TemplatePathValue(value)];
    }

    /// <summary>
    /// Expands raw UDI picker source text into published content values, falling back to scalar text.
    /// </summary>
    private List<TemplatePathValue> ExpandPickerSourceText(string text, string fieldName)
    {
        var matches = UdiPickerValueRegex().Matches(text);

        if (matches.Count == 0)
            return [new TemplatePathValue(text)];

        var results = new List<TemplatePathValue>();

        try
        {
            using var contextRef = umbracoContextFactory.EnsureUmbracoContext();

            foreach (Match match in matches)
            {
                var objectType = match.Groups["type"].Value;
                var key = Guid.ParseExact(match.Groups["key"].Value, "N");
                if (objectType.Equals("document", StringComparison.OrdinalIgnoreCase) &&
                    contentService?.GetById(key) is { } serviceContent)
                {
                    results.Add(new TemplatePathValue(serviceContent));
                    continue;
                }

                var content = objectType.Equals("media", StringComparison.OrdinalIgnoreCase)
                    ? contextRef.UmbracoContext.Media.GetById(key)
                    : contextRef.UmbracoContext.Content.GetById(key);

                if (content is not null)
                {
                    results.Add(new TemplatePathValue(content));
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed to resolve picker UDI values for field {FieldName}", fieldName);
        }

        return results;
    }

    /// <summary>
    /// Removes empty Markdown headings and excess blank lines left after missing template values.
    /// </summary>
    private static string CleanRenderedTemplate(string value)
    {
        var text = RenderedEmptyHeadingRegex().Replace(value.ReplaceLineEndings("\n"), string.Empty);
        text = RenderedExcessBlankLinesRegex().Replace(text, "\n\n");

        return text.Trim();
    }

    /// <summary>
    /// Recursively extracts searchable text from nested Umbraco block-list elements, bounded by maximum depth.
    /// </summary>
    private List<SearchTextPart> GetBlockSearchText(IPublishedElement content, SearchIndexDocument? searchIndexDocument, string fieldName, int depth)
    {
        try
        {
            if (searchIndexDocument is null || depth > 8 || IsBlockListEditor(content, fieldName) == false)
                return [];

            var parts = new List<SearchTextPart>();

            foreach (var item in content.Value<IEnumerable<BlockListItem>>(fieldName, fallback: Fallback.ToDefaultValue, defaultValue: []) ?? [])
            {
                var propertyAliases = item.Content.Properties
                    .Select(property => property.Alias)
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var propertyAlias in propertyAliases)
                {
                    if (item.Content.HasProperty(propertyAlias) == false)
                        continue;

                    var blockListText = IsBlockListEditor(item.Content, propertyAlias)
                        ? GetBlockSearchText(item.Content, searchIndexDocument, propertyAlias, depth + 1)
                        : [];

                    if (blockListText.Count > 0)
                    {
                        parts.AddRange(blockListText);
                        continue;
                    }
                    
                    var propertyText = GetPropertyText(item.Content, propertyAlias);

                    if (string.IsNullOrWhiteSpace(propertyText) == false)
                        parts.Add(new SearchTextPart(propertyText));
                }
            }

            return parts;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed to extract block search text for property {PropertyAlias}", fieldName);
            return [];
        }
    }

    /// <summary>
    /// Matches single-brace Markdown template tokens, such as {Name}, {summary|bodyText}, {author.Name}, or {headline:2}.
    /// </summary>
    [GeneratedRegex(@"\{(?<token>[A-Za-z0-9_|.:]+)\}")]
    private static partial Regex TemplateTokenRegex();

    /// <summary>
    /// Matches the optional trailing weight suffix on a template token.
    /// </summary>
    [GeneratedRegex(@":(?<weight>\d+)$")]
    private static partial Regex TemplateWeightRegex();

    /// <summary>
    /// Matches raw UDI picker values stored by source-value indexes.
    /// </summary>
    [GeneratedRegex(@"umb://(?<type>document|media)/(?<key>[0-9a-fA-F]{32})")]
    private static partial Regex UdiPickerValueRegex();

    /// <summary>
    /// Matches empty rendered Markdown headings.
    /// </summary>
    [GeneratedRegex(@"(?m)^\s{0,3}#{1,6}\s*$\n?")]
    private static partial Regex RenderedEmptyHeadingRegex();

    /// <summary>
    /// Matches excess blank lines in rendered Markdown.
    /// </summary>
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex RenderedExcessBlankLinesRegex();
}

#region Helper Classes/Records

/// <summary>
/// Semantic-search settings bound from the Umbraco:AI:Search:Qdrant configuration section.
/// </summary>
public sealed class AiSearchIndexFilterOptions
{
    /// <summary>
    /// Qdrant connection and collection-maintenance settings.
    /// </summary>
    public QdrantConnectionOptions Connection { get; set; } = new();

    /// <summary>
    /// Disables the default AI search index so only explicitly configured category indexes are registered.
    /// </summary>
    public bool DisableDefaultIndex { get; set; } = true;

    /// <summary>
    /// Named semantic-search categories, such as Documentation, each with its own query and indexing settings.
    /// </summary>
    public Dictionary<string, SearchCategory> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Qdrant collection settings used by the vector store.
/// </summary>
public sealed class QdrantConnectionOptions
{
    /// <summary>
    /// Hostname or IP address of the Qdrant server.
    /// </summary>
    public string ServerAddress { get; set; } = "localhost";

    /// <summary>
    /// gRPC port of the Qdrant server.
    /// </summary>
    public int ServerPort { get; set; } = 6334;

    /// <summary>
    /// Connects to Qdrant over HTTPS when true.
    /// </summary>
    public bool UseHttps { get; set; }

    /// <summary>
    /// API key for authenticating with the Qdrant server.
    /// </summary>
    public string ServerApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Expected embedding vector dimension, which must match the configured embedding model output size.
    /// </summary>
    public ulong EmbeddingSize { get; set; } = 1024;

    /// <summary>
    /// Removes old prefixed collections that no configured index alias references.
    /// </summary>
    public bool RemoveOrphanedCollections { get; set; }
}

/// <summary>
/// Combines semantic-search query behavior with the content type indexing rules searched by that category.
/// </summary>
public sealed class SearchCategory
{
    /// <summary>
    /// Umbraco search index alias backed by Qdrant collections.
    /// </summary>
    public string IndexAlias { get; set; } = "UmbAI_Search";

    /// <summary>
    /// Embedding profile alias intended for embedding search queries.
    /// </summary>
    public string EmbeddingsProfileAlias { get; set; } = "embeddings";

    /// <summary>
    /// Maximum vector chunk matches requested before score filtering and document grouping.
    /// </summary>
    public int TopK { get; set; } = 10;

    /// <summary>
    /// Maximum result documents rendered for this category after grouping.
    /// </summary>
    public int Take { get; set; } = 10;

    /// <summary>
    /// Minimum cosine similarity score allowed for vector results in this category.
    /// </summary>
    public double? MinScore { get; set; }

    /// <summary>
    /// Optional token chunk-size override for all indexing rules in this category.
    /// </summary>
    public int? ChunkSize { get; set; }

    /// <summary>
    /// Optional token overlap between adjacent chunks for all indexing rules in this category.
    /// </summary>
    public int? ChunkOverlap { get; set; }

    /// <summary>
    /// Per-document-type indexing configurations included in this search category.
    /// </summary>
    public List<SearchIndexDocument> Indexing { get; set; } = [];
}

// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable CollectionNeverUpdated.Global
// ReSharper disable UnusedMember.Global
/// <summary>
/// Controls how one or more Umbraco document types are rendered into semantic-search text.
/// </summary>
public sealed class SearchIndexDocument
{
    /// <summary>
    /// Umbraco document type aliases handled by this indexing configuration.
    /// </summary>
    public List<string> DocumentTypeAliases { get; set; } = [];

    /// <summary>
    /// Markdown template and weighted field settings used to build searchable text.
    /// </summary>
    public SearchTextOptions SearchText { get; set; } = new();

    /// <summary>
    /// Chunk splitting settings and context repeated on each chunk.
    /// </summary>
    public SearchChunkingOptions Chunking { get; set; } = new();
}

/// <summary>
/// Controls which source text is rendered and embedded for semantic search.
/// </summary>
public sealed class SearchTextOptions
{
    /// <summary>
    /// Markdown template rendered once per document. Tokens use property aliases, dotted picker or block-list paths, pipe-delimited fallbacks, optional trailing weights like :2, and built-ins like Name, Breadcrumb, Url, and ContentType.
    /// </summary>
    public string MarkdownTemplate { get; set; } = string.Empty;
}

/// <summary>
/// Controls how rendered Markdown is split into token-limited chunks.
/// </summary>
public sealed class SearchChunkingOptions
{
    /// <summary>
    /// Optional per-document-type token chunk-size override.
    /// </summary>
    public int? ChunkSize { get; set; }

    /// <summary>
    /// Optional per-document-type token overlap between adjacent chunks.
    /// </summary>
    public int? ChunkOverlap { get; set; }

    /// <summary>
    /// When true, splits Markdown at headings before token chunking so heading context is preserved.
    /// </summary>
    public bool? UseHeadingAwareChunking { get; set; }

    /// <summary>
    /// Stable document context repeated on every generated chunk.
    /// </summary>
    public SearchChunkContextOptions Context { get; set; } = new();
}

/// <summary>
/// Property aliases used as repeated chunk context without requiring those properties in the Markdown template.
/// </summary>
public sealed class SearchChunkContextOptions
{
    /// <summary>
    /// Category or taxonomy property aliases repeated as chunk context and stored as result metadata.
    /// </summary>
    public List<string> CategoryPropertyAliases { get; set; } = [];

    /// <summary>
    /// Title property aliases repeated as H1 Markdown context on each chunk.
    /// </summary>
    public List<string> TitlePropertyAliases { get; set; } = [];

    /// <summary>
    /// Section-title property aliases repeated as H2 Markdown context on each chunk.
    /// </summary>
    public List<string> SectionTitlePropertyAliases { get; set; } = [];

    /// <summary>
    /// Additional property aliases repeated as plain Markdown context on each chunk.
    /// </summary>
    public List<string> AdditionalPropertyAliases { get; set; } = [];
}

/// <summary>
/// Represents one extracted property or template text part before chunking.
/// </summary>
internal sealed record SearchTextPart(string Text);

/// <summary>
/// Represents one intermediate value while walking a dotted Markdown-template path.
/// </summary>
internal sealed record TemplatePathValue(object? Value)
{
    public string Text => Value?.ToString() ?? string.Empty;
}

/// <summary>
/// Represents one final context-enriched chunk of text to embed.
/// </summary>
internal sealed record SearchChunk(string Text);

/// <summary>
/// Represents one vector-store write prepared before replacing existing document vectors.
/// </summary>
internal sealed record SearchVectorUpsert(string? VariationKey, int ChunkIndex, ReadOnlyMemory<float> Vector, IDictionary<string, object> Metadata);

#endregion
