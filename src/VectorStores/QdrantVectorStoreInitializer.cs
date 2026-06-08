namespace Argentini.Umbraco.Search.Qdrant.VectorStores;

/// <summary>
/// Initializes configured Qdrant collections when the application host starts.
/// </summary>
public sealed class QdrantVectorStoreInitializer(QdrantVectorStore vectorStore, ILogger<QdrantVectorStoreInitializer> logger) : IHostedService
{
    /// <summary>
    /// Creates or repairs configured Qdrant collections before application traffic is handled.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await vectorStore.InitializeAsync(cancellationToken);
        logger.LogInformation("Initialized Qdrant vector collections");
    }

    /// <summary>
    /// Stops the initializer.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
