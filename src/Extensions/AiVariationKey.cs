using System.Text.RegularExpressions;
using Umbraco.Cms.Search.Core.Models.Indexing;

namespace Fynydd.Umbraco.Search.Qdrant.Extensions;

/// <summary>
/// Creates and parses compact keys that combine an Umbraco culture and segment for variation-aware vector storage.
/// </summary>
public static partial class AiVariationKey
{
    private const string InvariantCultureKey = "__invariant";
    private const string SegmentSeparator = "__segment__";

    /// <summary>
    /// Creates a variation key from a culture and segment, using a sentinel culture when only a segment is present.
    /// </summary>
    public static string? Create(string? culture, string? segment)
    {
        culture = Normalize(culture);
        segment = Normalize(segment);

        if (culture is null && segment is null)
            return null;

        return segment is null ? culture : $"{culture ?? InvariantCultureKey}{SegmentSeparator}{segment}";
    }

    /// <summary>
    /// Creates a variation key from an Umbraco indexing variation.
    /// </summary>
    public static string? Create(Variation variation) => Create(variation.Culture, variation.Segment);

    /// <summary>
    /// Parses a variation key back into separate culture and segment values.
    /// </summary>
    public static (string? Culture, string? Segment) Parse(string? key)
    {
        key = Normalize(key);

        if (key is null)
            return (null, null);

        var separatorIndex = key.IndexOf(SegmentSeparator, StringComparison.Ordinal);

        if (separatorIndex < 0)
            return (key, null);

        var culture = key[..separatorIndex];
        var segment = key[(separatorIndex + SegmentSeparator.Length)..];

        return (culture == InvariantCultureKey ? null : culture, Normalize(segment));
    }

    /// <summary>
    /// Gets fallback variation keys in most-specific to least-specific order for a requested culture and segment.
    /// </summary>
    public static IReadOnlyList<string?> SearchKeys(string? key)
    {
        var (culture, segment) = Parse(key);
        var keys = new List<string?>();

        Add(Create(culture, segment));

        if (segment is not null)
        {
            Add(Create(culture, null));
            Add(Create(null, segment));
        }

        Add(null);

        return keys;

        void Add(string? value)
        {
            if (keys.Contains(value, StringComparer.OrdinalIgnoreCase) == false)
                keys.Add(value);
        }
    }

    /// <summary>
    /// Converts a variation key into a safe Qdrant collection suffix by lowercasing and replacing unsupported characters.
    /// </summary>
    public static string ToCollectionSuffix(string? key)
    {
        key = Normalize(key);

        if (key is null)
            return string.Empty;

        return "-" + CollectionNameUnsafeCharacters().Replace(key.Trim().ToLowerInvariant(), "-");
    }

    /// <summary>
    /// Normalizes blank culture, segment, or variation-key values to null.
    /// </summary>
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Matches characters that are unsafe in a Qdrant collection name.
    /// </summary>
    [GeneratedRegex("[^a-zA-Z0-9_-]+")]
    private static partial Regex CollectionNameUnsafeCharacters();
}
