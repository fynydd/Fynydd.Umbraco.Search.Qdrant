using Microsoft.Extensions.Options;
using Umbraco.AI.Core.Embeddings;
using Umbraco.AI.Search.Core.Configuration;
using Umbraco.AI.Search.Core.VectorStore;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Models.Searching;
using Umbraco.Cms.Search.Core.Models.Searching.Faceting;
using Umbraco.Cms.Search.Core.Models.Searching.Filtering;
using Umbraco.Cms.Search.Core.Models.Searching.Sorting;
using Umbraco.Cms.Search.Core.Services;
using Fynydd.Umbraco.Search.Qdrant.Extensions;
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable RedundantArgumentDefaultValue

namespace Fynydd.Umbraco.Search.Qdrant.Searchers;

/// <summary>
/// Searches Qdrant vector entries and returns Umbraco search documents after score and access filtering.
/// </summary>
public sealed class FilteringAiVectorSearcher(
    IAIVectorStore vectorStore,
    IAIEmbeddingService embeddingService,
    IOptions<AIVectorSearchOptions> options,
    ILogger<FilteringAiVectorSearcher> logger) : ISearcher
{
    /// <summary>
    /// Embeds the search query, retrieves matching vector chunks, groups them by document, and returns paged Umbraco documents.
    /// </summary>
    public async Task<SearchResult> SearchAsync(
        string indexAlias,
        string? query,
        IEnumerable<Filter>? filters,
        IEnumerable<Facet>? facets,
        IEnumerable<Sorter>? sorters,
        string? culture,
        string? segment,
        AccessContext? accessContext,
        int skip,
        int take,
        int maxSuggestions)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new SearchResult(0, [], [], null);

        try
        {
            var variationKey = AiVariationKey.Create(culture, segment);
            var embedding = await embeddingService.GenerateEmbeddingAsync(
                builder => builder.WithAlias("ai-search-query").AsPassThrough(),
                query,
                CancellationToken.None);

            var results = (await vectorStore.SearchAsync(indexAlias, embedding.Vector, variationKey, options.Value.DefaultTopK))
                .Where(result => result.Score >= options.Value.MinScore)
                .Where(result => IsAccessible(result, accessContext))
                .GroupBy(result => result.DocumentId)
                .Select(group => group.OrderByDescending(result => result.Score).First())
                .OrderByDescending(result => result.Score)
                .ToList();

            var documents = results
                .Select(result =>
                {
                    if (Guid.TryParse(result.DocumentId, out var id) == false)
                        return null;

                    var objectType = UmbracoObjectTypes.Document;

                    if (result.Metadata?.TryGetValue("objectType", out var value) == true && Enum.TryParse(value.ToString(), out UmbracoObjectTypes parsedObjectType))
                        objectType = parsedObjectType;

                    return new Document(id, objectType);
                })
                .OfType<Document>()
                .ToList();

            return new SearchResult(documents.Count, documents.Skip(skip).Take(take), [], null);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Vector search failed for index {IndexAlias}", indexAlias);
            return new SearchResult(0, [], [], null);
        }
    }

    /// <summary>
    /// Determines whether a vector result is public or matches the current member or member-group access identifiers.
    /// </summary>
    private static bool IsAccessible(AIVectorSearchResult result, AccessContext? accessContext)
    {
        if (result.Metadata?.TryGetValue("accessIds", out var value) != true || value is not string accessIdsValue || string.IsNullOrEmpty(accessIdsValue))
            return true;

        if (accessContext is null)
            return false;

        if (accessContext.Bypass)
            return true;

        var accessIds = accessIdsValue.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (accessIds.Contains(accessContext.PrincipalId.ToString("D")))
            return true;

        return accessContext.GroupIds?.Any(groupId => accessIds.Contains(groupId.ToString("D"))) == true;
    }
}
