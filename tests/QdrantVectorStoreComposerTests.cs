using Fynydd.Umbraco.Search.Qdrant.Composers;
using Fynydd.Umbraco.Search.Qdrant.Indexers;
using Fynydd.Umbraco.Search.Qdrant.Searchers;
using Fynydd.Umbraco.Search.Qdrant.Services;
using Fynydd.Umbraco.Search.Qdrant.VectorStores;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.AI.Search.Core.VectorStore;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Search.Core.Configuration;

namespace Umbraco.Search.Qdrant.Tests;

public sealed class QdrantVectorStoreComposerTests
{
    [Fact]
    public void Compose_RegistersQdrantVectorStoreServices()
    {
        var services = new ServiceCollection();
        var builder = CreateBuilder(services);

        new QdrantVectorStoreComposer().Compose(builder);

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(QdrantVectorStore));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IAIVectorStore));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(QdrantVectorStoreInitializer));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ITextReplacementProvider));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(FilteringAiVectorIndexer));
    }

    [Fact]
    public void Compose_RegistersDefaultIndexWhenNoCategoriesAreConfigured()
    {
        var services = new ServiceCollection();
        var builder = CreateBuilder(
            services,
            new Dictionary<string, string?>
            {
                ["Umbraco:AI:Search:Qdrant:DisableDefaultIndex"] = "false"
            });

        new QdrantVectorStoreComposer().Compose(builder);

        using var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<IOptions<IndexOptions>>().Value.GetContentIndexRegistrations();

        var registration = Assert.Single(registrations);
        Assert.Equal("UmbAI_Search", registration.IndexAlias);
        Assert.Equal(typeof(FilteringAiVectorIndexer), registration.Indexer);
        Assert.Equal(typeof(FilteringAiVectorSearcher), registration.Searcher);
    }

    [Fact]
    public void Compose_HonorsDefaultIndexDisablingWithConfiguredCategories()
    {
        var services = new ServiceCollection();
        var builder = CreateBuilder(
            services,
            new Dictionary<string, string?>
            {
                ["Umbraco:AI:Search:Qdrant:DisableDefaultIndex"] = "true",
                ["Umbraco:AI:Search:Qdrant:Categories:docs:IndexAlias"] = "Docs",
                ["Umbraco:AI:Search:Qdrant:Categories:news:IndexAlias"] = "News"
            });

        new QdrantVectorStoreComposer().Compose(builder);

        using var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<IOptions<IndexOptions>>().Value.GetContentIndexRegistrations();

        Assert.Equal(["Docs", "News"], registrations.Select(registration => registration.IndexAlias).Order());
        Assert.DoesNotContain(registrations, registration => registration.IndexAlias == "UmbAI_Search");
    }

    private static IUmbracoBuilder CreateBuilder(
        IServiceCollection services,
        Dictionary<string, string?>? configurationValues = null)
    {
        var builder = Substitute.For<IUmbracoBuilder>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues ?? new Dictionary<string, string?>())
            .Build();

        services.AddSingleton<IConfiguration>(configuration);
        builder.Services.Returns(services);
        builder.Config.Returns(configuration);

        return builder;
    }
}
