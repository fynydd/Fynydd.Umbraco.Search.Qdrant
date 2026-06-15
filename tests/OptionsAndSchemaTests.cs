using System.Text.Json;
using Fynydd.Umbraco.Search.Qdrant.Indexers;

namespace Umbraco.Search.Qdrant.Tests;

public sealed class OptionsAndSchemaTests
{
    [Fact]
    public void AiSearchIndexFilterOptions_DefaultsMatchExpectedConfiguration()
    {
        var options = new AiSearchIndexFilterOptions();

        Assert.True(options.DisableDefaultIndex);
        Assert.Equal("localhost", options.Connection.ServerAddress);
        Assert.Equal(6334, options.Connection.ServerPort);
        Assert.False(options.Connection.UseHttps);
        Assert.Equal(1024UL, options.Connection.EmbeddingSize);
        Assert.Empty(options.Categories);
    }

    [Fact]
    public void SearchCategory_DefaultsMatchExpectedQueryBehavior()
    {
        var category = new SearchCategory();

        Assert.Equal("UmbAI_Search", category.IndexAlias);
        Assert.Equal("embeddings", category.EmbeddingsProfileAlias);
        Assert.Equal(10, category.TopK);
        Assert.Equal(10, category.Take);
        Assert.Null(category.MinScore);
        Assert.Empty(category.Indexing);
    }

    [Fact]
    public void SearchIndexDocument_DefaultCollectionsAreUsable()
    {
        var document = new SearchIndexDocument();

        document.DocumentTypeAliases.Add("article");
        document.SearchText.MarkdownTemplate = "{body:2}";
        document.Chunking.Context.TitlePropertyAliases.Add("title");

        Assert.Equal("article", Assert.Single(document.DocumentTypeAliases));
        Assert.Equal("{body:2}", document.SearchText.MarkdownTemplate);
        Assert.Equal("title", Assert.Single(document.Chunking.Context.TitlePropertyAliases));
    }

    [Fact]
    public void AppsettingsSchema_IsValidJsonAndDefinesQdrantSection()
    {
        using var document = JsonDocument.Parse(File.ReadAllText("appsettings-schema.Umbraco.Search.Qdrant.json"));

        var qdrant = document.RootElement
            .GetProperty("properties")
            .GetProperty("Qdrant");

        Assert.Equal("object", qdrant.GetProperty("type").GetString());
        Assert.True(qdrant.GetProperty("properties").TryGetProperty("Connection", out _));
        Assert.True(qdrant.GetProperty("properties").TryGetProperty("Categories", out _));
    }

    [Fact]
    public void AppsettingsSchema_DefaultsMatchOptionsDefaults()
    {
        using var document = JsonDocument.Parse(File.ReadAllText("appsettings-schema.Umbraco.Search.Qdrant.json"));

        var properties = document.RootElement
            .GetProperty("properties")
            .GetProperty("Qdrant")
            .GetProperty("properties");
        var connection = properties.GetProperty("Connection").GetProperty("properties");

        Assert.True(properties.GetProperty("DisableDefaultIndex").GetProperty("default").GetBoolean());
        Assert.Equal("localhost", connection.GetProperty("ServerAddress").GetProperty("default").GetString());
        Assert.Equal(6334, connection.GetProperty("ServerPort").GetProperty("default").GetInt32());
        Assert.False(connection.GetProperty("UseHttps").GetProperty("default").GetBoolean());
        Assert.Equal(1024, connection.GetProperty("EmbeddingSize").GetProperty("default").GetInt32());
        Assert.False(connection.TryGetProperty("RemoveOrphanedCollections", out _));
    }

    [Fact]
    public void AppsettingsSchema_DefinesExpectedSearchTextAndChunkingDefaults()
    {
        using var document = JsonDocument.Parse(File.ReadAllText("appsettings-schema.Umbraco.Search.Qdrant.json"));

        var indexingProperties = document.RootElement
            .GetProperty("properties")
            .GetProperty("Qdrant")
            .GetProperty("properties")
            .GetProperty("Categories")
            .GetProperty("additionalProperties")
            .GetProperty("properties")
            .GetProperty("Indexing")
            .GetProperty("items")
            .GetProperty("properties");
        var chunkingProperties = indexingProperties
            .GetProperty("Chunking")
            .GetProperty("properties");
        var searchTextProperties = indexingProperties
            .GetProperty("SearchText")
            .GetProperty("properties");

        Assert.Equal("array", indexingProperties.GetProperty("DocumentTypeAliases").GetProperty("type").GetString());
        Assert.True(searchTextProperties.TryGetProperty("MarkdownTemplate", out _));
        Assert.False(searchTextProperties.TryGetProperty("Fields", out _));
        Assert.False(chunkingProperties.GetProperty("UseHeadingAwareChunking").GetProperty("default").GetBoolean());
    }
}
