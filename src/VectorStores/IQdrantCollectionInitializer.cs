namespace Fynydd.Umbraco.Search.Qdrant.VectorStores;

/// <summary>
/// Prepares Qdrant collections required by configured search indexes.
/// </summary>
public interface IQdrantCollectionInitializer
{
    /// <summary>
    /// Creates or validates configured Qdrant collections.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = new());
}
