using Microsoft.Extensions.Options;
using Qdrant.Client;
using Umbraco.AI.Search.Core.VectorStore;
using Umbraco.AI.Search.Startup;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Configuration;
using Umbraco.Cms.Search.Core.Services.ContentIndexing;
using Fynydd.Umbraco.Search.Qdrant.Indexers;
using Fynydd.Umbraco.Search.Qdrant.Searchers;
using Fynydd.Umbraco.Search.Qdrant.Services;
using Fynydd.Umbraco.Search.Qdrant.VectorStores;
// ReSharper disable UnusedType.Global

namespace Fynydd.Umbraco.Search.Qdrant.Composers;

/// <summary>
/// Registers Qdrant as the AI vector store and wires configured semantic-search indexes into Umbraco Search.
/// </summary>
[ComposeAfter(typeof(UmbracoAISearchComposer))]
public class QdrantVectorStoreComposer : IComposer
{
    /// <summary>
    /// Adds Qdrant services, binds configuration, and registers each configured index alias for content and media changes.
    /// </summary>
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddOptions<AiSearchIndexFilterOptions>().BindConfiguration("Umbraco:AI:Search:Qdrant");
        builder.Services.AddSingleton(services =>
        {
            var connectionOptions = services.GetRequiredService<IOptions<AiSearchIndexFilterOptions>>().Value.Connection;

            return new QdrantClient(
                connectionOptions.ServerAddress,
                port: connectionOptions.ServerPort,
                https: connectionOptions.UseHttps,
                apiKey: connectionOptions.ServerApiKey);
        });
        builder.Services.AddSingleton<QdrantVectorStore>();
        builder.Services.AddSingleton<IAIVectorStore>(services => services.GetRequiredService<QdrantVectorStore>());
        builder.Services.AddHostedService<QdrantVectorStoreInitializer>();
        builder.Services.TryAddSingleton<ITextReplacementProvider, EmptyTextReplacementProvider>();
        builder.Services.AddTransient<FilteringAiVectorIndexer>();
        builder.Services.AddOptions<IndexOptions>().Configure<IOptions<AiSearchIndexFilterOptions>>((options, aiSearchOptions) =>
        {
            var indexAliases = aiSearchOptions.Value.Categories.Values
                .Select(category => category.IndexAlias)
                .Where(alias => string.IsNullOrWhiteSpace(alias) == false)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .DefaultIfEmpty("UmbAI_Search");

            foreach (var indexAlias in indexAliases)
            {
                options.RegisterContentIndex<FilteringAiVectorIndexer, FilteringAiVectorSearcher, IPublishedContentChangeStrategy>(
                    indexAlias,
                    UmbracoObjectTypes.Document,
                    UmbracoObjectTypes.Media);
            }
        });
    }
}
