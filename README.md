# Umbraco.Search.Qdrant

This package adds Qdrant support to the Umbraco.AI.Search package. It not only adds a Qdrant vector store provider, it also adds a configuration pattern that allows you to use Markdown to structure the content that gets scanned and chunked. And you can configure multiple indexes, each used for searching specific document types, and with their own settings.

This provides a streamlined and performant way to add semantic search to your Umbraco CMS web application. If you're new to that feature, check out the [Umbraco semantic search documentation](https://docs.umbraco.com/ai-in-umbraco/add-ons/search).

**Key features:**

- High-performance Qdrant vector store that leverages filters
- Qdrant collections are completely managed; orphans get deleted, new ones get created, and vector size mismatch with your embedding model will recreate the collection
- Provides an optional `ITextReplacementsProvider` interface, so you can perform find/replace on content before it gets indexed (e.g. if your web app uses short codes, etc.)
- Completely configurable in *appsettings.json*; includes a schema file with descriptions and default values for the Qdrant features
- Can disable the default search index "UmbAI_Search" to avoid excess embeddings generation
- Search results include a snippet for the content chunk that matched the search, so you can show it to visitors

**Requirements:**

- Umbraco CMS 17 or later
- Umbraco.AI.Search package
- An AI API service that provides embeddings generation (e.g. AWS Bedrock, OpenAI API, etc.)
- A Qdrant vector database

## Prepare Umbraco

**If you already have Umbraco.AI.Search installed and have configured a connection and embeddings profile, skip ahead to the "Configure" section.**

**First**, assuming you already have a Qdrant database server, as well as an embeddings provider, add the `Umbraco.AI.Search` nuget package to your Umbraco CMS project. If the current version is still in beta, you will need to enable preview versions of packages to see it.

**Second**, sign in to your back office and configure the AI service connection, and add an embeddings profile, following Umbraco documentation steps.

### A Note On Microsoft Data Protection...

Much of the AI infrastructure in Umbraco relies on core Microsoft APIs and frameworks. As such, the AI service connection credentials are encrypted for security using *DataProtectionKeys*.

By default, IIS doesn't keep persistent *DataProtectionKeys*. In local development environments they are typically persisted in your user account (e.g. on macOS) so you wouldn't see an issue until you deployed. Therefore it's recommended that you set a persistent location for the keys, otherwise you'll lose the connection every time the app starts and new keys are generated.

The simplest way to persist keys is to tell the app to use local storage. To do that you would add this code to your *Program.cs* file:

```csharp
var keysPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtectionKeys");

Directory.CreateDirectory(keysPath);

builder.Services
    .AddDataProtection()
    .SetApplicationName("MyWebApp")
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
```

If you use this method, don't commit that key folder to your repository! There are ways to configure the key store location to use the server environment, etc. Choose the method that best matches your security needs.

## Configure

**Add the package/code:** Assuming you have all the requirements established, add the `Fynydd.Umbraco.Search.Qdrant` nuget package (or this repo code) to your Umbraco CMS project.

**Next, add your Qdrant configuration.** Below shows the configuration for a web app that has a category named "Documentation" for a docs search. In this example, there are two primary types of documents to be included in the index: 2 document types with similar properties, and a utility document type with its own properties.

Here is where the new settings will go in your *appsettings.json* file:

```json
...
"Umbraco": {
    "AI": {
        "Search": {
            // Qdrant settings go here
        }
    }
},
...
```

**Below is an example of the Qdrant settings.** If using the sample code below via copy/paste, remove the comments as they are not valid JSON.

```json
...
"Qdrant": {
    // Skip scanning for the default UmbAI_Search index
    "DisableDefaultIndex": true,
    // Qdrant server connection
    "Connection": {
        "ServerAddress": "localhost",
        "ServerPort": 6334,
        "UseHttps": false,
        "ServerApiKey": "opensaysme",
        // Should match the embedding profile alias in the back office
        "EmbeddingSize": 1024,
        // Deletes Qdrant collections which no longer exist as categories (below)
        "RemoveOrphanedCollections": true
    },
    "Categories": {
        "Documentation": {
            // Shown in Umbraco back office
            "IndexAlias": "Documentation",
            // Should match the embedding profile alias in the back office
            "EmbeddingsProfileAlias": "embeddings",
            "ChunkSize": 150,
            "ChunkOverlap": 25,
            "TopK": 150,
            "Take": 15,
            "MinScore": 0.1,
            "Indexing": [
                {
                    "DocumentTypeAliases": ["docPage", "docsPage"],
                    // MarkdownTemplate also defines which properties are indexed;
                    // by Qdrant; Breadcrumb is a dynamic value you can use to
                    // include the Umbraco content tree path
                    "SearchText": {
                        "MarkdownTemplate": "# {headline|heroHeadline|Name}\n\n> {Breadcrumb}\n\n## Summary\n\n{heroIntroductionText}\n\n## Body\n\n{blockContent}\n\n{Url}",
                        "Fields": {
                            // Weights are 1-5, with higher values providing more
                            // influence over matches (used as a multiplier for 
                            // repeating content); default is 1
                            "headline": { "Weight": 3 },
                            "heroHeadline": { "Weight": 3 },
                            "heroIntroductionText": { "Weight": 2 },
                            "richText": {},
                            "body": {},
                            "blockContent": {}
                        }
                    },
                    "Chunking": {
                        // Include document and other relevant headings in each chunk
                        "UseHeadingAwareChunking": true,
                        // Helps identify document categories, titles, and sections 
                        // by their property aliases
                        "Context": {
                            "CategoryPropertyAliases": ["heroCategory"],
                            "TitlePropertyAliases": ["headline", "heroHeadline"],
                            "SectionTitlePropertyAliases": ["heroIntroductionText"],
                            // Additional property values to prepend to chunks
                            "AdditionalPropertyAliases": []
                        }
                    }
                },
                {
                    "DocumentTypeAliases": ["utilityPage"],
                    "SearchText": {
                        "MarkdownTemplate": "# {utilityGroup|Name}\n\n> {Breadcrumb}\n\n## Summary\n\n{utilityGroupDescription}\n\n## Body\n\n{blockContent}\n\n{Url}",
                        "Fields": {
                            "utilityGroup": { "Weight": 3 },
                            "utilityGroupDescription": { "Weight": 2 },
                            "richText": {},
                            "body": {},
                            "blockContent": {}
                        }
                    },
                    "Chunking": {
                        "UseHeadingAwareChunking": true,
                        "Context": {
                            "CategoryPropertyAliases": ["utilityCategory"],
                            "TitlePropertyAliases": ["utilityGroup"],
                            "SectionTitlePropertyAliases": ["utilityGroupDescription"],
                            "AdditionalPropertyAliases": []
                        }
                    }
                }
            ]
        }
    }
}
...
```

## Optional Text Replacement Provider

If your web app uses short codes or something similar, and your pages dynamically perform find/replace before rendering, you can implement the `ITextReplacementProvider`, which gives the pipeline your dictionary of key/value text replacements so it can do them for you prior to chunking and indexing. It even supports nested replacements!

The example below is super simple. As you can imagine, you could use code that reads the text replacements from Umbraco data and caches it, etc.

**Note:** if your text replacements are wrapped in special characters, like braces (e.g. "the year is {{year}}") you must include them in the keys in your provider.

```csharp
using Fynydd.Umbraco.Search.Qdrant.Services;

namespace UmbracoCms.Services;

public class TextReplacementProvider() : ITextReplacementProvider
{
    public IReadOnlyDictionary<string, string> GetReplacements()
    {
        return new Dictionary<string, string>()
        {
            { "{{copyright}}", "(c) {{year}}, ABC Corp." },
            { "{{year}}", DateTime.Now.YUear.ToString() },
        };
    }
}
```

You can then add your text replacement provider at startup. It will be found and used automatically if it exists.

```csharp

builder.Services.AddSingleton<ITextReplacementProvider, TextReplacementProvider>();

```

## Populate The Index

Once everything is set up, go to the back office "Settings" area, and at the bottom choose the "Search" option. You should see your configured search indexes. Click the refresh icon next to each one to clear and repopulate them. This can take a minute or longer, depending on how much data you have to index.

## How To Search

Below is some example Razor code for performing a search on the example configuration above.

```csharp
@using Umbraco.AI.Core.Embeddings
@using Umbraco.AI.Search.Core.VectorStore
@using Umbraco.Cms.Core.Cache
@using Umbraco.Cms.Core.PublishedCache
@using Fynydd.Umbraco.Search.Qdrant.Indexers
@using Fynydd.Umbraco.Search.Qdrant.VectorStores
@using Fynydd.Umbraco.Search.Qdrant.Extensions

@inject IPublishedContentCache PublishedContentCache
@inject QdrantVectorStore AiVectorStore
@inject IAIEmbeddingService EmbeddingService
@inject IOptions<AiSearchIndexFilterOptions> AiSearchOptions
@inject IAppPolicyCache MemoryCache

@{
    var searchResults = new List<SearchResult>();

    if (string.IsNullOrWhiteSpace(search) == false)
    {
        AiSearchOptions.Value.Categories.TryGetValue("Documentation", out var documentationSearchCategory);

        var documentationPageDocumentTypeAliases = documentationSearchCategory?.Indexing
            .SelectMany(indexing => indexing.DocumentTypeAliases)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        if (documentationSearchCategory is not null && documentationPageDocumentTypeAliases.Contains(Model.ContentType.Alias, StringComparer.OrdinalIgnoreCase))
        {
            const int skipItems = 0;

            var takeItems = documentationSearchCategory.Take;
            var embeddingsProfileAlias = documentationSearchCategory.EmbeddingsProfileAlias;
            var indexAlias = documentationSearchCategory.IndexAlias;
            
            var variationContext = GlobalStateService.VariationContextAccessor.VariationContext;
            var culture = variationContext?.Culture ?? System.Globalization.CultureInfo.CurrentUICulture.Name;
            var segment = variationContext?.Segment;

            var allowedDocTypeAliases = documentationPageDocumentTypeAliases;
            var cacheKey = "vs::" + search.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            
            Embedding<float>? embeddings;

            try
            {
                // Caching searches can save embeddings generation cost, and keep things fast

                embeddings = await MemoryCache.GetCacheItem(
                    cacheKey,
                    async () =>
                    {
                        return await EmbeddingService.GenerateEmbeddingAsync(
                            builder => builder.WithAlias(embeddingsProfileAlias),
                            search,
                            CancellationToken.None
                        );
                    },
                    TimeSpan.FromHours(1), isSliding: true
                )!;
            }
            catch
            {
                embeddings = null;
            }

            if (embeddings is not null)
            {
                var vectorResults = await AiVectorStore.SearchAsync(
                    indexName: indexAlias,
                    queryVector: embeddings.Vector,
                    culture: AiVariationKey.Create(culture, segment),
                    topK: documentationSearchCategory.TopK,
                    payloadFilters: new Dictionary<string, IReadOnlyCollection<object?>?>
                    {
                        ["documentTypeAlias"] = allowedDocTypeAliases.Cast<object>().ToList()
                    });

                searchResults = vectorResults
                    .Where(x => x.Score >= (documentationSearchCategory.MinScore ?? 0.2))
                    .OrderByDescending(x => x.Score)
                    .Select(x => new
                    {
                        Result = x,
                        Content = PublishedContentCache.GetById(Guid.Parse(x.DocumentId))
                    })
                    .Where(x =>
                        x.Content is not null &&
                        x.Content.IsVisible() && x.Content.IsTrue("allowSearchIndexing"))
                    .GroupBy(x => x.Content!.Key)
                    .Select(g => g.First()) // Highest score because already ordered
                    .Skip(skipItems)
                    .Take(takeItems)
                    .Select(x => new SearchResult
                    {
                        Content = x.Content,
                        Result = x.Result
                    })
                    .ToList();
            }
        }
    }
}
```

Then further down on the page you can iterate the results. You can even highlight matching terms using word stems, so search words like "use" also highlight "using":

```csharp
<div class="space-y-8">
    @foreach (var result in searchResults)
    {
        if (result.Content is null)
            continue;

        var title = result.Content.ContentType.Alias.InvariantEquals("utilityPage") ? result.Content.SafeValue("utilityGroup") : string.Empty;
        var excerpt = result.Content.ContentType.Alias.InvariantEquals("utilityPage") ? result.Content.SafeValue("utilityGroupDescription") : string.Empty;
        
        if (string.IsNullOrEmpty(title))
            title = result.Content.SafeValue("heroHeadline", result.Content.Name);

        if (string.IsNullOrWhiteSpace(excerpt))
            excerpt = result.Content.ContentType.Alias.InvariantEquals("utilityPage") ? result.Content.SafeValue("utilityGroupDescription") : string.Empty;

        if (string.IsNullOrEmpty(excerpt))
            excerpt = result.Content.SafeValue("heroIntroductionText", result.Content.SafeValue("metaDescription", result.Content.SafeValue("ogDescription")));
        
        var snippet = result.Result?.Metadata?.ContainsKey("snippet") ?? false ? result.Result.Metadata["snippet"].ToString() : string.Empty;
        var searchTerms = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var searchStems = searchTerms
                .Where(i => SemanticSearchExtensions.SearchNoiseWordsAbridged.NotContains(i, StringComparer.OrdinalIgnoreCase))
                .Select(SemanticSearchExtensions.Stem)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(snippet) == false && searchStems.Count > 0)
        {
            snippet = Regex.Replace(
                snippet,
                @"[\p{L}\p{N}.#+'-]+",
                m =>
                {
                    var sourceText = m.Value;

                    // Handles terms like .NET inside ASP.NET
                    if (searchTerms.Any(t => t.Any(char.IsPunctuation) && sourceText.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    {
                        return Regex.Replace(
                            sourceText,
                            string.Join("|",
                                searchTerms
                                    .Where(t => t.Any(char.IsPunctuation))
                                    .Select(Regex.Escape)),
                            mm => $"""<strong class="font-bold text-black dark:text-white">{mm.Value}</strong>""",
                            RegexOptions.IgnoreCase);
                    }

                    var sourceStem = sourceText.Stem();

                    return searchStems.Contains(sourceStem)
                        ? $"""<strong class="font-bold text-black dark:text-white">{sourceText}</strong>"""
                        : sourceText;
                },
                RegexOptions.IgnoreCase);
        }
        
        <div class="space-y-2">
            <div class="space-y-1">
                <div><a href="/@(result.Content.Url(null, UrlMode.Relative).Trim('/'))/" class="cursor-pointer font-semibold text-primary dark:text-secondary hover:underline">@(title)</a></div>
                @if (string.IsNullOrWhiteSpace(excerpt) == false)
                {
                    <div class="text-sm">@Html.Raw(excerpt)</div>
                }
            </div>
            @if (string.IsNullOrWhiteSpace(snippet) == false)
            {
                <div class="text-xs text-gray-600 dark:text-gray-400">...@Html.Raw(snippet.Trim('.')))...</div>
            }
        </div>
    }
</div>
@functions {
    private sealed class SearchResult
    {
        public IPublishedContent? Content { get; init; }
        public AIVectorSearchResult? Result { get; init; }
    }
}
```
