using Argentini.Umbraco.Search.Qdrant.Services;

namespace Umbraco.Search.Qdrant.Tests;

public sealed class TextReplacementProviderTests
{
    [Fact]
    public void EmptyTextReplacementProvider_ReturnsEmptyMap()
    {
        var provider = new EmptyTextReplacementProvider();

        Assert.Empty(provider.GetReplacements());
    }
}
