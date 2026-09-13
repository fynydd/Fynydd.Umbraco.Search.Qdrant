using Fynydd.Umbraco.Search.Qdrant.Indexers;
using Fynydd.Umbraco.Search.Qdrant.VectorStores;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Umbraco.Search.Qdrant.Tests;

public sealed class QdrantVectorStoreInitializerTests
{
    [Fact]
    public async Task StartAsync_QdrantUnavailable_CompletesWithoutStoppingHost()
    {
        var store = new RecordingCollectionInitializer(_ =>
            Task.FromException(new RpcException(new Status(StatusCode.Unavailable, "Connection refused."))));
        var initializer = CreateInitializer(store);

        await initializer.StartAsync(CancellationToken.None);

        Assert.Equal(1, store.InitializeCalls);
    }

    [Fact]
    public async Task StartAsync_QdrantDeadlineExceeded_CompletesWithoutStoppingHost()
    {
        var store = new RecordingCollectionInitializer(_ =>
            Task.FromException(new RpcException(new Status(StatusCode.DeadlineExceeded, "Deadline exceeded."))));
        var initializer = CreateInitializer(store);

        await initializer.StartAsync(CancellationToken.None);

        Assert.Equal(1, store.InitializeCalls);
    }

    [Fact]
    public async Task StartAsync_QdrantCancelledWithoutHostCancellation_CompletesWithoutStoppingHost()
    {
        var store = new RecordingCollectionInitializer(_ =>
            Task.FromException(new RpcException(new Status(StatusCode.Cancelled, "Qdrant cancelled the request."))));
        var initializer = CreateInitializer(store);

        await initializer.StartAsync(CancellationToken.None);

        Assert.Equal(1, store.InitializeCalls);
    }

    [Fact]
    public async Task StartAsync_InitializationTimeout_CompletesWithoutStoppingHost()
    {
        var store = new RecordingCollectionInitializer(token =>
            Task.Delay(Timeout.InfiniteTimeSpan, token));
        var initializer = CreateInitializer(store, initializationTimeoutSeconds: 1);

        await initializer.StartAsync(CancellationToken.None);

        Assert.Equal(1, store.InitializeCalls);
        Assert.True(store.LastCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task StartAsync_HostCancellation_PropagatesCancellation()
    {
        var store = new RecordingCollectionInitializer(token =>
        {
            token.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        });
        var initializer = CreateInitializer(store);
        using var cancellationTokenSource = new CancellationTokenSource();

        await cancellationTokenSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            initializer.StartAsync(cancellationTokenSource.Token));

        Assert.Equal(1, store.InitializeCalls);
        Assert.True(store.LastCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task StartAsync_HealthyStore_CompletesInitialization()
    {
        var store = new RecordingCollectionInitializer();
        var initializer = CreateInitializer(store);

        await initializer.StartAsync(CancellationToken.None);

        Assert.Equal(1, store.InitializeCalls);
        Assert.False(store.LastCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task StartAsync_NonTransientFailure_Propagates()
    {
        var store = new RecordingCollectionInitializer(_ =>
            Task.FromException(new InvalidOperationException("Programming error.")));
        var initializer = CreateInitializer(store);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            initializer.StartAsync(CancellationToken.None));

        Assert.Equal("Programming error.", exception.Message);
        Assert.Equal(1, store.InitializeCalls);
    }

    [Fact]
    public async Task StopAsync_CompletesWithoutWork()
    {
        var store = new RecordingCollectionInitializer();
        var initializer = CreateInitializer(store);

        await initializer.StopAsync(CancellationToken.None);

        Assert.Equal(0, store.InitializeCalls);
    }

    private static QdrantVectorStoreInitializer CreateInitializer(
        IQdrantCollectionInitializer store,
        int initializationTimeoutSeconds = 5) =>
        new(
            store,
            Options.Create(new AiSearchIndexFilterOptions
            {
                Connection =
                {
                    InitializationTimeoutSeconds = initializationTimeoutSeconds
                }
            }),
            NullLogger<QdrantVectorStoreInitializer>.Instance);

    private sealed class RecordingCollectionInitializer(Func<CancellationToken, Task>? initialize = null) : IQdrantCollectionInitializer
    {
        public int InitializeCalls { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = new())
        {
            InitializeCalls++;
            LastCancellationToken = cancellationToken;

            return initialize?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
    }
}
