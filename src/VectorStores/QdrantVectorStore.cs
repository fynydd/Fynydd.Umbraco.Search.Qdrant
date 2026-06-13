using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Umbraco.AI.Search.Core.VectorStore;
using Fynydd.Umbraco.Search.Qdrant.Extensions;
using Fynydd.Umbraco.Search.Qdrant.Indexers;
// ReSharper disable UnusedVariable

namespace Fynydd.Umbraco.Search.Qdrant.VectorStores;

/// <summary>
/// Stores AI embedding vectors in Qdrant collections partitioned by Umbraco search index and variation key.
/// </summary>
public class QdrantVectorStore(QdrantClient client, IOptions<AiSearchIndexFilterOptions> filterOptions, ILogger<QdrantVectorStore> logger) : IAIVectorStore
{
    private const string CollectionPrefix = "umbraco-qdrant-";
    private readonly ConcurrentDictionary<string, ulong> _ensuredCollections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _collectionLocks = new(StringComparer.OrdinalIgnoreCase);

    #region Helpers

    /// <summary>
    /// Deletes prefixed Qdrant collections that no configured semantic-search index alias references.
    /// This cleanup runs at most once for the current vector-store instance.
    /// </summary>
    private async Task RemoveOrphanedCollectionsAsync(CancellationToken cancellationToken)
    {
        if (filterOptions.Value.Connection.RemoveOrphanedCollections == false)
            return;

        var validPrefixes = GetConfiguredIndexAliases()
            .Select(alias => GetCollectionName(alias, null))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var collections = await client.ListCollectionsAsync(cancellationToken);

        foreach (var collectionName in collections)
        {
            if (collectionName.StartsWith(CollectionPrefix) == false)
                continue;

            var isValid = validPrefixes.Any(prefix =>
                IsCollectionForIndex(collectionName, prefix));

            if (isValid == false)
                await client.DeleteCollectionAsync(collectionName, TimeSpan.FromSeconds(300), cancellationToken);
        }
    }
    
    /// <summary>
    /// Ensures a Qdrant collection exists without deleting existing vectors during normal startup or search traffic.
    /// </summary>
    /// <param name="collectionName">The Qdrant collection name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when the collection is available.</returns>
    private async Task<bool> EnsureCollectionAsync(string collectionName, CancellationToken cancellationToken = new())
    {
        var collections = await client.ListCollectionsAsync(cancellationToken);

        if (collections.Contains(collectionName, StringComparer.OrdinalIgnoreCase))
        {
            var collectionInfo = await client.GetCollectionInfoAsync(collectionName, cancellationToken);
            var vectorSize = collectionInfo.Config?.Params?.VectorsConfig?.Params?.Size;

            if (vectorSize is not null && vectorSize != filterOptions.Value.Connection.EmbeddingSize)
            {
                logger.LogWarning(
                    "Qdrant collection {CollectionName} has vector size {ActualVectorSize}, but configuration expects {ConfiguredVectorSize}. The collection was preserved; use reset only when you intentionally want to rebuild it.",
                    collectionName,
                    vectorSize,
                    filterOptions.Value.Connection.EmbeddingSize);
            }

            _ensuredCollections[collectionName] = vectorSize ?? filterOptions.Value.Connection.EmbeddingSize;
            return true;
        }
        
        await client.CreateCollectionAsync(
            collectionName, 
            new VectorParams
            {
                Size = filterOptions.Value.Connection.EmbeddingSize,
                Distance = Distance.Cosine
            }, cancellationToken: cancellationToken);
            
        collections = await client.ListCollectionsAsync(cancellationToken);

        var created = collections.Contains(collectionName, StringComparer.OrdinalIgnoreCase);

        if (created)
            _ensuredCollections[collectionName] = filterOptions.Value.Connection.EmbeddingSize;

        return created;
    }

    /// <summary>
    /// Ensures a Qdrant collection once per application lifetime unless it has not yet been seen.
    /// </summary>
    private async Task<bool> EnsureCollectionCachedAsync(string collectionName, CancellationToken cancellationToken = new())
    {
        if (_ensuredCollections.ContainsKey(collectionName))
            return true;

        var collectionLock = _collectionLocks.GetOrAdd(collectionName, _ => new SemaphoreSlim(1, 1));

        await collectionLock.WaitAsync(cancellationToken);

        try
        {
            if (_ensuredCollections.ContainsKey(collectionName))
                return true;

            return await EnsureCollectionAsync(collectionName, cancellationToken);
        }
        finally
        {
            collectionLock.Release();
        }
    }

    /// <summary>
    /// Runs a collection write, recreating the collection once when Qdrant reports it disappeared after local cache validation.
    /// </summary>
    private async Task ExecuteCollectionWriteAsync(string collectionName, Func<Task> write, CancellationToken cancellationToken)
    {
        try
        {
            await write();
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.NotFound)
        {
            _ensuredCollections.TryRemove(collectionName, out _);

            if (await EnsureCollectionCachedAsync(collectionName, cancellationToken) == false)
                throw;

            logger.LogWarning(exception, "Qdrant collection {CollectionName} was missing during write and has been recreated", collectionName);
            await write();
        }
    }

    /// <summary>
    /// Generates a collection name from an index name and variation key.
    /// </summary>
    /// <param name="indexName">The vector index name.</param>
    /// <param name="culture">The culture or combined culture-segment variation key.</param>
    /// <returns>The Qdrant collection name.</returns>
    private static string GetCollectionName(string indexName, string? culture) => CollectionPrefix + indexName.Trim().ToLowerInvariant() + AiVariationKey.ToCollectionSuffix(culture);

    /// <summary>
    /// Determines whether a collection name belongs to an index collection prefix.
    /// </summary>
    private static bool IsCollectionForIndex(string collectionName, string collectionNamePrefix) =>
        collectionName.Equals(collectionNamePrefix, StringComparison.OrdinalIgnoreCase) ||
        collectionName.StartsWith(collectionNamePrefix + "-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes points from a collection and ignores the deletion when the collection disappeared during the operation.
    /// </summary>
    private async Task DeletePointsIfCollectionExistsAsync(string collectionName, Filter filter, CancellationToken cancellationToken)
    {
        try
        {
            await client.DeleteAsync(collectionName, filter, cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            if ((await client.ListCollectionsAsync(cancellationToken)).Contains(collectionName, StringComparer.OrdinalIgnoreCase))
                throw;

            logger.LogDebug(exception, "Skipped deleting points from missing Qdrant collection {CollectionName}", collectionName);
        }
    }
    
    /// <summary>
    /// Converts a CLR metadata value into a Qdrant payload value.
    /// </summary>
    /// <param name="value">The source value.</param>
    /// <returns>The Qdrant payload value.</returns>
    private static Value ToQdrantValue(object? value)
    {
        return value switch
        {
            null => new Value { NullValue = NullValue.NullValue },
            string s => s,
            bool b => b,
            int i => i,
            long l => l,
            float f => f,
            double d => d,
            decimal d => (double)d,
            DateTime dt => dt.ToString("O"),
            DateTimeOffset dto => dto.ToString("O"),
            _ => value.ToString() ?? string.Empty
        };
    }

    /// <summary>
    /// Converts a Qdrant payload value into a CLR metadata value.
    /// </summary>
    /// <param name="value">The Qdrant payload value.</param>
    /// <returns>The converted CLR value.</returns>
    private static object FromQdrantValue(Value value)
    {
        return value.KindCase switch
        {
            Value.KindOneofCase.NullValue => null!,
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.BoolValue => value.BoolValue,
            Value.KindOneofCase.IntegerValue => value.IntegerValue,
            Value.KindOneofCase.DoubleValue => value.DoubleValue,
            Value.KindOneofCase.StructValue => value.StructValue,
            Value.KindOneofCase.ListValue => value.ListValue.Values
                .Select(FromQdrantValue)
                .ToList(),
            _ => value.StringValue
        };
    }
    
    /// <summary>
    /// Creates a deterministic point identifier so the same document chunk replaces the same Qdrant point.
    /// </summary>
    private static Guid CreateDeterministic(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input.ToLowerInvariant()));

        return new Guid(hash);
    }

    /// <summary>
    /// Determines whether the index is permitted by semantic-search configuration.
    /// </summary>
    private bool IsKnownIndex(string indexName) =>
        filterOptions.Value.DisableDefaultIndex == false ||
        filterOptions.Value.Categories.Values
            .Any(c => c.IndexAlias.Equals(indexName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Creates a Qdrant point for one document chunk.
    /// </summary>
    private static PointStruct CreatePoint(string indexName, string documentId, string? culture, int chunkIndex, ReadOnlyMemory<float> vector, IDictionary<string, object>? metadata)
    {
        var variation = AiVariationKey.Parse(culture);
        var point = new PointStruct
        {
            Id = CreateDeterministic($"{indexName.Trim()}:{culture?.Trim()}:{documentId.Trim()}:{chunkIndex}"),
            Vectors = vector.ToArray(),
            Payload =
            {
                ["documentId"] = documentId,
                ["chunkIndex"] = chunkIndex
            }
        };

        if (variation.Culture is not null)
            point.Payload["culture"] = variation.Culture;

        if (variation.Segment is not null)
            point.Payload["segment"] = variation.Segment;

        if (metadata is not null)
        {
            foreach (var kvp in metadata)
                point.Payload[kvp.Key] = ToQdrantValue(kvp.Value);
        }

        return point;
    }

    /// <summary>
    /// Gets the configured index aliases that should have Qdrant collections prepared at startup.
    /// </summary>
    private IEnumerable<string> GetConfiguredIndexAliases()
    {
        var aliases = filterOptions.Value.Categories.Values
            .Select(category => category.IndexAlias)
            .Where(alias => string.IsNullOrWhiteSpace(alias) == false);

        if (filterOptions.Value.DisableDefaultIndex == false)
            aliases = aliases.Append("UmbAI_Search");

        return aliases
            .DefaultIfEmpty("UmbAI_Search")
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    #endregion

    /// <summary>
    /// Prepares configured and existing Qdrant collections before indexing starts.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = new())
    {
        try
        {
            await RemoveOrphanedCollectionsAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to remove orphaned Qdrant collections");
        }

        var collections = await client.ListCollectionsAsync(cancellationToken);

        foreach (var indexName in GetConfiguredIndexAliases())
        {
            var collectionNamePrefix = GetCollectionName(indexName, null);
            var collectionNames = collections
                .Where(collectionName =>
                    IsCollectionForIndex(collectionName, collectionNamePrefix))
                .Append(collectionNamePrefix)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var collectionName in collectionNames)
            {
                if (await EnsureCollectionCachedAsync(collectionName, cancellationToken) == false)
                    throw new InvalidOperationException($"Failed to create or access collection `{collectionName}` in Qdrant.");
            }
        }
    }

    /// <summary>
    /// Inserts or updates one embedded document chunk and its metadata in the matching variation collection.
    /// </summary>
    public async Task UpsertAsync(string indexName, string documentId, string? culture, int chunkIndex, ReadOnlyMemory<float> vector, IDictionary<string, object>? metadata = null, CancellationToken cancellationToken = new ())
    {
        if (IsKnownIndex(indexName) == false)
            return;

        var collectionName = GetCollectionName(indexName, culture);

        if (vector.Length != (int)filterOptions.Value.Connection.EmbeddingSize)
            throw new ArgumentException($"Vector must have {filterOptions.Value.Connection.EmbeddingSize} dimensions.", nameof(vector));

        if (await EnsureCollectionCachedAsync(collectionName, cancellationToken) == false)
            throw new InvalidOperationException($"Failed to create or access collection `{collectionName}` in Qdrant.");

        var point = CreatePoint(indexName, documentId, culture, chunkIndex, vector, metadata);

        await ExecuteCollectionWriteAsync(
            collectionName,
            async () => _ = await client.UpsertAsync(collectionName, [point], cancellationToken: cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Inserts or updates embedded document chunks grouped by their matching variation collection.
    /// </summary>
    public async Task UpsertManyAsync(string indexName, string documentId, IEnumerable<AIVectorEntry> entries, CancellationToken cancellationToken = new())
    {
        if (IsKnownIndex(indexName) == false)
            return;

        var entryList = entries.ToList();
        var pointsByCollection = new Dictionary<string, List<PointStruct>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entryList)
        {
            if (entry.Vector.Length != (int)filterOptions.Value.Connection.EmbeddingSize)
                throw new ArgumentException($"Vector must have {filterOptions.Value.Connection.EmbeddingSize} dimensions.", nameof(entries));

            var collectionName = GetCollectionName(indexName, entry.Culture);

            if (pointsByCollection.TryGetValue(collectionName, out var points) == false)
            {
                points = [];
                pointsByCollection[collectionName] = points;
            }

            points.Add(CreatePoint(indexName, documentId, entry.Culture, entry.ChunkIndex, entry.Vector, entry.Metadata));
        }

        foreach (var (collectionName, points) in pointsByCollection)
        {
            if (await EnsureCollectionCachedAsync(collectionName, cancellationToken) == false)
                throw new InvalidOperationException($"Failed to create or access collection `{collectionName}` in Qdrant.");

            await ExecuteCollectionWriteAsync(
                collectionName,
                async () => _ = await client.UpsertAsync(collectionName, points, cancellationToken: cancellationToken),
                cancellationToken);
        }
    }

    /// <summary>
    /// Deletes vector points for one document from one culture or culture-segment collection.
    /// </summary>
    public async Task DeleteAsync(string indexName, string documentId, string? culture, CancellationToken cancellationToken = new ())
    {
        var collectionName = GetCollectionName(indexName, culture);

        if ((await client.ListCollectionsAsync(cancellationToken)).Contains(collectionName, StringComparer.OrdinalIgnoreCase) == false)
            return;

        await DeletePointsIfCollectionExistsAsync(
            collectionName,
            new Filter
            {
                Must =
                {
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "documentId",
                            Match = new Match
                            {
                                Text = documentId
                            }
                        }
                    }
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Deletes vector points for one document across all culture and segment variation collections for an index.
    /// </summary>
    public async Task DeleteDocumentAsync(string indexName, string documentId, CancellationToken cancellationToken = new ())
    {
        var collectionNamePrefix = GetCollectionName(indexName, null);
        var collections = await client.ListCollectionsAsync(cancellationToken);

        foreach (var collectionName in collections)
        {
            if (IsCollectionForIndex(collectionName, collectionNamePrefix) == false)
                continue;
            
            await DeletePointsIfCollectionExistsAsync(
                collectionName,
                new Filter
                {
                    Must =
                    {
                        new Condition
                        {
                            Field = new FieldCondition
                            {
                                Key = "documentId",
                                Match = new Match
                                {
                                    Text = documentId
                                }
                            }
                        }
                    }
                },
                cancellationToken);
        }
    }

    /// <summary>
    /// Searches the requested variation and its fallback collections, then returns the highest-scoring vector matches.
    /// </summary>
    public async Task<IReadOnlyList<AIVectorSearchResult>> SearchAsync(string indexName, ReadOnlyMemory<float> queryVector, string? culture = null, int topK = 10, CancellationToken cancellationToken = new ())
        => await SearchAsync(indexName, queryVector, culture, topK, payloadFilters: null, cancellationToken);

    /// <summary>
    /// Searches the requested variation and its fallback collections using exact-match payload filters.
    /// </summary>
    public async Task<IReadOnlyList<AIVectorSearchResult>> SearchAsync(
        string indexName,
        ReadOnlyMemory<float> queryVector,
        string? culture,
        int topK,
        IReadOnlyDictionary<string, IReadOnlyCollection<object?>?>? payloadFilters,
        CancellationToken cancellationToken = new())
    {
        var collections = await client.ListCollectionsAsync(cancellationToken);
        var scoredPoints = new List<ScoredPoint>();
        var filter = CreatePayloadFilter(payloadFilters);

        foreach (var variationKey in AiVariationKey.SearchKeys(culture))
        {
            var collectionName = GetCollectionName(indexName, variationKey);

            if (collections.Contains(collectionName, StringComparer.OrdinalIgnoreCase) == false)
                continue;

            scoredPoints.AddRange(await client.SearchAsync(
                collectionName,
                queryVector.ToArray(),
                filter: filter,
                limit: (ulong)topK,
                payloadSelector: true,
                cancellationToken: cancellationToken));
        }

        var results = new List<AIVectorSearchResult>();

        foreach (var item in scoredPoints.OrderByDescending(point => point.Score).Take(topK))
        {
            var documentId = item.Payload.TryGetValue("documentId", out var docIdValue) ? docIdValue.StringValue : string.Empty;

            if (string.IsNullOrWhiteSpace(documentId))
                continue;

            IDictionary<string, object> metadata = new Dictionary<string, object>();

            foreach (var kvp in item.Payload)
                metadata.TryAdd(kvp.Key, FromQdrantValue(kvp.Value));

            results.Add(new AIVectorSearchResult(documentId, item.Score, metadata));
        }

        return results;
    }

    /// <summary>
    /// Creates a Qdrant filter that requires one value from each supplied metadata field.
    /// </summary>
    private static Filter? CreatePayloadFilter(IReadOnlyDictionary<string, IReadOnlyCollection<object?>?>? payloadFilters)
    {
        if (payloadFilters is null || payloadFilters.Count == 0)
            return null;

        var filter = new Filter();

        foreach (var (fieldName, values) in payloadFilters)
        {
            if (values is null)
                continue;

            var normalizedValues = values
                .OfType<object>()
                .Distinct()
                .ToList();

            if (string.IsNullOrWhiteSpace(fieldName) || normalizedValues.Count == 0)
                continue;

            if (normalizedValues.Count == 1)
            {
                filter.Must.Add(CreatePayloadMatch(fieldName, normalizedValues[0]));
                continue;
            }

            var should = new Filter();

            foreach (var value in normalizedValues)
                should.Should.Add(CreatePayloadMatch(fieldName, value));

            filter.Must.Add(new Condition { Filter = should });
        }

        return filter.Must.Count == 0 ? null : filter;
    }

    /// <summary>
    /// Creates an exact-match Qdrant payload condition.
    /// </summary>
    private static Condition CreatePayloadMatch(string fieldName, object value) => new()
    {
        Field = new FieldCondition
        {
            Key = fieldName,
            Match = value switch
            {
                bool b => new Match { Boolean = b },
                int i => new Match { Integer = i },
                long l => new Match { Integer = l },
                float f => null,
                double d => null,
                decimal d => null,
                _ => new Match { Keyword = value.ToString() ?? string.Empty }
            },
            Range = value switch
            {
                float f => new global::Qdrant.Client.Grpc.Range { Gte = f, Lte = f },
                double d => new global::Qdrant.Client.Grpc.Range { Gte = d, Lte = d },
                decimal d => new global::Qdrant.Client.Grpc.Range { Gte = (double)d, Lte = (double)d },
                _ => null
            }
        }
    };

    /// <summary>
    /// Gets all stored vector chunks for a document, optionally constrained to one variation collection.
    /// </summary>
    public async Task<IReadOnlyList<AIVectorEntry>> GetVectorsByDocumentAsync(string indexName, string documentId, string? culture = null, CancellationToken cancellationToken = new ())
    {
        var collectionNamePrefix = GetCollectionName(indexName, null);
        var collections = await client.ListCollectionsAsync(cancellationToken);
        var collectionNames = string.IsNullOrWhiteSpace(culture)
            ? collections.Where(collectionName => IsCollectionForIndex(collectionName, collectionNamePrefix)).ToList()
            : collections.Where(collectionName => collectionName.Equals(GetCollectionName(indexName, culture), StringComparison.OrdinalIgnoreCase)).ToList();

        var results = new List<AIVectorEntry>();

        foreach (var collectionName in collectionNames)
        {
            var response = await client.ScrollAsync(
                collectionName,
                filter: new Filter
                {
                    Must =
                    {
                        new Condition
                        {
                            Field = new FieldCondition
                            {
                                Key = "documentId",
                                Match = new Match
                                {
                                    Text = documentId
                                }
                            }
                        }
                    }
                },
                vectorsSelector: true,
                limit: 9999,
                cancellationToken: cancellationToken);

            foreach (var item in response.Result)
            {
                var chunkIndex = item.Payload.TryGetValue("chunkIndex", out var chunkIndexValue) ? (int)chunkIndexValue.IntegerValue : 0;
                var vector = item.Vectors?.Vector.GetDenseVector()?.Data.ToArray() ?? [];
                var metadata = new Dictionary<string, object>();

                foreach (var kvp in item.Payload)
                    metadata.TryAdd(kvp.Key, FromQdrantValue(kvp.Value));
            
                results.Add(new AIVectorEntry(documentId, culture, chunkIndex, vector, metadata));
            }
        }

        return results.OrderBy(entry => entry.ChunkIndex).ToList();
    }

    /// <summary>
    /// Recreates every collection for an index, clearing all vectors while preserving collection names.
    /// </summary>
    public async Task ResetAsync(string indexName, CancellationToken cancellationToken = new ())
    {
        var collectionNamePrefix = GetCollectionName(indexName, null);
        var collections = await client.ListCollectionsAsync(cancellationToken);

        foreach (var collectionName in collections)
        {
            if (IsCollectionForIndex(collectionName, collectionNamePrefix) == false)
                continue;

            _ensuredCollections.TryRemove(collectionName, out _);
            await client.DeleteCollectionAsync(collectionName, TimeSpan.FromSeconds(300), cancellationToken);
            await client.CreateCollectionAsync(
                collectionName,
                new VectorParams
                {
                    Size = filterOptions.Value.Connection.EmbeddingSize,
                    Distance = Distance.Cosine
                },
                cancellationToken: cancellationToken);
            _ensuredCollections[collectionName] = filterOptions.Value.Connection.EmbeddingSize;
        }
    }

    /// <summary>
    /// Counts distinct document identifiers stored across every variation collection for an index.
    /// </summary>
    public async Task<long> GetDocumentCountAsync(string indexName, CancellationToken cancellationToken = new ())
    {
        var collectionNamePrefix = GetCollectionName(indexName, null);
        var collections = await client.ListCollectionsAsync(cancellationToken);
        var documentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var collectionName in collections)
        {
            if (IsCollectionForIndex(collectionName, collectionNamePrefix) == false)
                continue;
            
            PointId? offset = null;

            do
            {
                var response = await client.ScrollAsync(
                    collectionName,
                    limit: 1000,
                    offset: offset,
                    vectorsSelector: false,
                    cancellationToken: cancellationToken);

                foreach (var item in response.Result)
                {
                    if (item.Payload.TryGetValue("documentId", out var value) == false)
                        continue;

                    var documentId = value.StringValue;

                    if (string.IsNullOrWhiteSpace(documentId) == false)
                        documentIds.Add(documentId);
                }

                offset = response.NextPageOffset;
            }
            while (offset is not null);
        }

        return documentIds.Count;
    }
}
