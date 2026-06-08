namespace Argentini.Umbraco.Search.Qdrant.Services;

/// <summary>
/// Default text replacement provider used when the host application does not register one.
/// </summary>
public sealed class EmptyTextReplacementProvider : ITextReplacementProvider
{
    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetReplacements() => new Dictionary<string, string>();
}
