using System.Collections.Concurrent;
using System.Collections;
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
    IUmbracoContextFactory umbracoContextFactory,
    IVariationContextAccessor variationContextAccessor,
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

        var previousVariationContext = variationContextAccessor.VariationContext;
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

                variationContextAccessor.VariationContext = new VariationContext(culture, segment);

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
                    .Select(part => part with { Text = ApplyTextReplacements(part.Text, textReplacements) })
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
                    var chunks = CreateChunks(text, prefix, searchIndexDocument, chunkingOptions);

                    if (chunks.Count == 0)
                    {
                        logger.LogDebug("No chunks for document {DocumentId} culture {Culture} segment {Segment} in {IndexAlias}", id, culture ?? "invariant", segment ?? "invariant", indexAlias);
                        continue;
                    }

                    var chunkTexts = chunks.Select(chunk => chunk.Text).ToList();
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
                            ["totalChunks"] = chunkTexts.Count
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
            variationContextAccessor.VariationContext = previousVariationContext;
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
            var breadcrumb = CreateBreadcrumb(content);

            if (string.IsNullOrWhiteSpace(breadcrumb) == false)
                prefix += $"> {ApplyTextReplacements(breadcrumb, textReplacements)}\n\n";
        }

        if (content is not null)
        {
            var category = CreateDocumentCategory(content, searchIndexDocument);

            if (string.IsNullOrWhiteSpace(category) == false)
                prefix += $"Category: {ApplyTextReplacements(category, textReplacements)}\n\n";
        }

        foreach (var propertyAlias in searchIndexDocument?.Chunking.Context.TitlePropertyAliases ?? [])
        {
            var title = content?.Value(propertyAlias, fallback: Fallback.ToDefaultValue, defaultValue: string.Empty) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(title))
                continue;

            prefix += $"# {ApplyTextReplacements(title, textReplacements)}\n\n";

            break;
        }

        foreach (var propertyAlias in searchIndexDocument?.Chunking.Context.SectionTitlePropertyAliases ?? [])
        {
            var title = content?.Value(propertyAlias, fallback: Fallback.ToDefaultValue, defaultValue: string.Empty) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(title))
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

                var value = GetPropertyText(content, propertyAlias);

                if (string.IsNullOrWhiteSpace(value) == false)
                    prefix += $"{ApplyTextReplacements(value, textReplacements)}\n\n";
            }
        }

        return prefix;
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
    /// Gets property text as Markdown, preserving Markdown-editor values and converting other HTML values.
    /// </summary>
    private static string GetPropertyText(IPublishedElement element, string propertyAlias) =>
        IsMarkdownEditor(element, propertyAlias)
            ? GetPropertyValue(element, propertyAlias)
            : GetPropertyValue(element, propertyAlias).HtmlToSearchText();

    /// <summary>
    /// Gets the converted published value without relying on the global Umbraco fallback service.
    /// </summary>
    private static string GetPropertyValue(IPublishedElement element, string propertyAlias) =>
        element.GetProperty(propertyAlias)?.GetValue()?.ToString() ?? string.Empty;

    /// <summary>
    /// Resolves the first configured category or taxonomy property value for metadata and chunk context.
    /// </summary>
    private static string CreateDocumentCategory(IPublishedContent? content, SearchIndexDocument? searchIndexDocument)
    {
        foreach (var propertyAlias in searchIndexDocument?.Chunking.Context.CategoryPropertyAliases ?? [])
        {
            var category = content?.Value(propertyAlias, fallback: Fallback.ToDefaultValue, defaultValue: string.Empty) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(category) == false)
                return category;
        }

        return string.Empty;
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

        return chunks;
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
    /// Extracts Markdown search text from a template, configured fields, boosted Examine text ranks, or nested block lists.
    /// </summary>
    private List<SearchTextPart> ExtractTextFromFields(IEnumerable<IndexField>? fields, SearchIndexDocument? searchIndexDocument, IPublishedContent? content)
    {
        if (content is not null && string.IsNullOrWhiteSpace(searchIndexDocument?.SearchText.MarkdownTemplate) == false)
        {
            var templatedText = RenderMarkdownTemplate(searchIndexDocument.SearchText.MarkdownTemplate, content, searchIndexDocument, content, 0);

            return string.IsNullOrWhiteSpace(templatedText)
                ? []
                : [new SearchTextPart("__template", templatedText)];
        }

        List<SearchTextPart> boostedTexts = [];
        List<SearchTextPart> texts = [];

        foreach (var propertyAlias in searchIndexDocument?.SearchText.Fields.Keys ?? Enumerable.Empty<string>())
        {
            // ReSharper disable UnusedVariable
            foreach (var (fieldName, value, culture, segment) in fields ?? [])
            {
                if (fieldName.Equals(propertyAlias, StringComparison.OrdinalIgnoreCase) == false)
                    continue;

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
                    var valueTexts = value.TextsR1.Select(text => new SearchTextPart(fieldName, isMarkdown ? text : text.HtmlToSearchText()));
                    boostedTexts.AddRange(valueTexts);
                }

                if (value.TextsR2 is not null)
                {
                    var valueTexts = value.TextsR2.Select(text => new SearchTextPart(fieldName, isMarkdown ? text : text.HtmlToSearchText()));
                    texts.AddRange(valueTexts);
                }

                if (value.TextsR3 is not null)
                {
                    var valueTexts = value.TextsR3.Select(text => new SearchTextPart(fieldName, isMarkdown ? text : text.HtmlToSearchText()));
                    texts.AddRange(valueTexts);
                }

                if (value.Texts is not null)
                {
                    var valueTexts = value.Texts.Select(text => new SearchTextPart(fieldName, isMarkdown ? text : text.HtmlToSearchText()));
                    texts.AddRange(valueTexts);
                }
            }
        }
        
        List<SearchTextPart> parts = new(boostedTexts.Count + texts.Count);
        parts.AddRange(boostedTexts);
        parts.AddRange(texts);

        return parts.Select(part => ApplyFieldWeight(part, searchIndexDocument)).ToList();
    }

    /// <summary>
    /// Applies configured field weight by repeating text before embedding, clamped to supported bounds.
    /// </summary>
    private SearchTextPart ApplyFieldWeight(SearchTextPart part, SearchIndexDocument? searchIndexDocument)
    {
        var weight = 1;

        if (searchIndexDocument?.SearchText.Fields.TryGetValue(part.FieldName, out var fieldOptions) == true)
            weight = fieldOptions.Weight;

        weight = Math.Clamp(weight, 1, 5);

        return weight == 1
            ? part
            : part with { Text = part.Text.ApplyFieldWeight(weight) };
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
        foreach (var alias in token.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = ResolveTemplateAlias(alias, element, searchIndexDocument, rootContent, depth);

            if (string.IsNullOrWhiteSpace(value) == false)
                return value;
        }

        return string.Empty;
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

        var blockParts = GetBlockSearchText(element, searchIndexDocument, alias, depth + 1);

        if (blockParts.Count > 0)
            return string.Join("\n\n", blockParts.Select(part => ApplyFieldWeight(part, searchIndexDocument).Text));

        return ApplyFieldWeight(new SearchTextPart(alias, GetPropertyText(element, alias)), searchIndexDocument).Text;
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
            current = ResolveTemplatePathPart(current, parts[i], searchIndexDocument, depth, i == parts.Length - 1);

            if (current.Count == 0)
                return string.Empty;
        }

        return string.Join("\n\n", current.Select(value => value.Text).Where(text => string.IsNullOrWhiteSpace(text) == false));
    }

    /// <summary>
    /// Resolves the first segment in a dotted template path.
    /// </summary>
    private static List<TemplatePathValue> ResolveTemplatePathRoot(string alias, IPublishedElement element, IPublishedContent? rootContent)
    {
        if (rootContent is not null && alias.Equals("Name", StringComparison.OrdinalIgnoreCase))
            return [new TemplatePathValue(rootContent.Name, alias)];

        if (rootContent is not null && alias.Equals("Url", StringComparison.OrdinalIgnoreCase))
            return [new TemplatePathValue(rootContent.Url(), alias)];

        if (alias.Equals("ContentType", StringComparison.OrdinalIgnoreCase))
            return [new TemplatePathValue(element.ContentType.Alias, alias)];

        if (element.HasProperty(alias) == false)
            return [];

        return ExpandTemplatePathValue(element.GetProperty(alias)?.GetValue(), alias);
    }

    /// <summary>
    /// Resolves one segment in a dotted template path against all current values.
    /// </summary>
    private List<TemplatePathValue> ResolveTemplatePathPart(IEnumerable<TemplatePathValue> values, string alias, SearchIndexDocument searchIndexDocument, int depth, bool isFinalPart)
    {
        var results = new List<TemplatePathValue>();

        foreach (var value in values)
        {
            if (value.Value is IPublishedElement element)
            {
                if (alias.Equals("ContentType", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new TemplatePathValue(element.ContentType.Alias, alias));
                    continue;
                }

                if (element is IPublishedContent content)
                {
                    if (alias.Equals("Name", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new TemplatePathValue(content.Name, alias));
                        continue;
                    }

                    if (alias.Equals("Url", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new TemplatePathValue(content.Url(), alias));
                        continue;
                    }
                }

                if (element.HasProperty(alias) == false)
                    continue;

                var blockParts = GetBlockSearchText(element, searchIndexDocument, alias, depth + 1);

                if (blockParts.Count > 0)
                {
                    results.AddRange(blockParts.Select(part => new TemplatePathValue(ApplyFieldWeight(part, searchIndexDocument).Text, part.FieldName)));
                    continue;
                }

                results.AddRange(ExpandTemplatePathValue(element.GetProperty(alias)?.GetValue(), alias, element));
                continue;
            }

            if (isFinalPart && value.Value is not null)
                results.Add(value);
        }

        return results;
    }

    /// <summary>
    /// Expands picker, block-list, and scalar values from a template path segment.
    /// </summary>
    private static List<TemplatePathValue> ExpandTemplatePathValue(object? value, string fieldName, IPublishedElement? owner = null)
    {
        return value switch
        {
            null => [],
            string text => [new TemplatePathValue(owner is null ? text : GetPropertyText(owner, fieldName), fieldName)],
            BlockListItem item => [new TemplatePathValue(item.Content, fieldName)],
            IEnumerable<BlockListItem> items => items.Select(item => new TemplatePathValue(item.Content, fieldName)).ToList(),
            IPublishedElement element => [new TemplatePathValue(element, fieldName)],
            IEnumerable<IPublishedElement> elements => elements.Select(element => new TemplatePathValue(element, fieldName)).ToList(),
            IEnumerable enumerable when value is not string => enumerable
                .Cast<object?>()
                .SelectMany(item => ExpandTemplatePathValue(item, fieldName))
                .ToList(),
            _ => [new TemplatePathValue(value.ToString() ?? string.Empty, fieldName)]
        };
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
            if (searchIndexDocument is null || depth > 8)
                return [];

            var parts = new List<SearchTextPart>();

            foreach (var item in content.Value<IEnumerable<BlockListItem>>(fieldName, fallback: Fallback.ToDefaultValue, defaultValue: []) ?? [])
            {
                var propertyAliases = searchIndexDocument.SearchText.Fields.Keys
                    .Concat(item.Content.Properties
                        .Select(property => property.Alias)
                        .Where(alias => (item.Content.Value<IEnumerable<BlockListItem>>(alias, fallback: Fallback.ToDefaultValue, defaultValue: []) ?? []).Any()))                        
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var propertyAlias in propertyAliases)
                {
                    if (item.Content.HasProperty(propertyAlias) == false)
                        continue;

                    var blockListText = GetBlockSearchText(item.Content, searchIndexDocument, propertyAlias, depth + 1);

                    if (blockListText.Count > 0)
                    {
                        parts.AddRange(blockListText);
                        continue;
                    }
                    
                    var propertyText = GetPropertyText(item.Content, propertyAlias);

                    if (string.IsNullOrWhiteSpace(propertyText) == false)
                        parts.Add(new SearchTextPart(propertyAlias, propertyText));
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
    /// Matches single-brace Markdown template tokens, such as {Name}, {summary|bodyText}, or {author.Name}.
    /// </summary>
    [GeneratedRegex(@"\{(?<token>[A-Za-z0-9_|.]+)\}")]
    private static partial Regex TemplateTokenRegex();

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
    /// Markdown template rendered once per document. Tokens use property aliases, dotted picker or block-list paths, pipe-delimited fallbacks, and built-ins like Name, Breadcrumb, Url, and ContentType.
    /// </summary>
    public string MarkdownTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Property aliases the template or block traversal can use, with optional per-field embedding weight.
    /// </summary>
    public Dictionary<string, SearchTextFieldOptions> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Controls how strongly a field influences generated embeddings.
/// </summary>
public sealed class SearchTextFieldOptions
{
    /// <summary>
    /// Text repeat count before embedding. Values are clamped from 1 to 5 during indexing.
    /// </summary>
    public int Weight { get; set; } = 1;
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
    /// Category or taxonomy property aliases repeated as "Category: ..." and stored as result metadata.
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
internal sealed record SearchTextPart(string FieldName, string Text);

/// <summary>
/// Represents one intermediate value while walking a dotted Markdown-template path.
/// </summary>
internal sealed record TemplatePathValue(object? Value, string FieldName)
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
