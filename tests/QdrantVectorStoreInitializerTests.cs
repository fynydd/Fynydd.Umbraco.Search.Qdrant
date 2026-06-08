using Fynydd.Umbraco.Search.Qdrant.Indexers;
using Fynydd.Umbraco.Search.Qdrant.VectorStores;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Qdrant.Client;

namespace Umbraco.Search.Qdrant.Tests;

public sealed class QdrantVectorStoreInitializerTests
{
    [Fact]
    public async Task StartAsync_PassesCancellationTokenToVectorStoreInitialization()
    {
        var store = new QdrantVectorStore(
            new QdrantClient("127.0.0.1", 1),
            Options.Create(new AiSearchIndexFilterOptions { DisableDefaultIndex = false }),
            NullLogger<QdrantVectorStore>.Instance);
        var initializer = new QdrantVectorStoreInitializer(store, NullLogger<QdrantVectorStoreInitializer>.Instance);
        using var cancellationTokenSource = new CancellationTokenSource();

        await cancellationTokenSource.CancelAsync();

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            initializer.StartAsync(cancellationTokenSource.Token));

        Assert.Equal(StatusCode.Cancelled, exception.StatusCode);
    }

    [Fact]
    public async Task StopAsync_CompletesWithoutWork()
    {
        var store = new QdrantVectorStore(
            new QdrantClient("127.0.0.1", 1),
            Options.Create(new AiSearchIndexFilterOptions()),
            NullLogger<QdrantVectorStore>.Instance);
        var initializer = new QdrantVectorStoreInitializer(store, NullLogger<QdrantVectorStoreInitializer>.Instance);

        await initializer.StopAsync(CancellationToken.None);
    }
}
