using Microsoft.Extensions.Options;
using Fynydd.Umbraco.Search.Qdrant.Indexers;

namespace Fynydd.Umbraco.Search.Qdrant.VectorStores;

/// <summary>
/// Initializes configured Qdrant collections when the application host starts.
/// </summary>
public sealed class QdrantVectorStoreInitializer(
    IQdrantCollectionInitializer vectorStore,
    IOptions<AiSearchIndexFilterOptions> options,
    ILogger<QdrantVectorStoreInitializer> logger) : IHostedService
{
    /// <summary>
    /// Creates or repairs configured Qdrant collections before application traffic is handled.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeoutSeconds = Math.Max(1, options.Value.Connection.InitializationTimeoutSeconds);

        timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await vectorStore.InitializeAsync(timeoutSource.Token);
            logger.LogInformation("Initialized Qdrant vector collections");
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested == false && QdrantTransientFailure.IsTransient(exception))
        {
            logger.LogWarning(
                exception,
                "Qdrant is unavailable; vector collection initialization was skipped after {TimeoutSeconds} seconds and Umbraco startup will continue",
                timeoutSeconds);
        }
    }

    /// <summary>
    /// Stops the initializer.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
