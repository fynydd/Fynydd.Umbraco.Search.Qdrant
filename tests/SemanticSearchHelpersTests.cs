using Fynydd.Umbraco.Search.Qdrant.Extensions;

namespace Umbraco.Search.Qdrant.Tests;

public sealed class SemanticSearchHelpersTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(8, 5)]
    public void ApplyFieldWeight_ClampsWeightAndRepeatsText(int weight, int expectedCount)
    {
        var result = "syntax".ApplyFieldWeight(weight);

        Assert.Equal(expectedCount, result.Split("syntax").Length - 1);
    }

    [Fact]
    public void ApplyFieldWeight_RepeatsMarkdownListsWithSingleLineBreaks()
    {
        var result = "- one\n- two".ApplyFieldWeight(2);

        Assert.Equal("- one\n- two\n- one\n- two", result);
    }

    [Fact]
    public void SplitMarkdownSections_KeepsHeadingsWithTheirBody()
    {
        const string markdown = """
                                Intro

                                ## Filters
                                Blur syntax

                                ## Layout
                                Grid syntax
                                """;

        var sections = markdown.SplitMarkdownSections();

        Assert.Equal(3, sections.Count);
        Assert.Equal("Intro", sections[0]);
        Assert.StartsWith("## Filters", sections[1]);
        Assert.Contains("Blur syntax", sections[1]);
        Assert.StartsWith("## Layout", sections[2]);
    }

    [Fact]
    public void HtmlToSearchText_RemovesHtmlTagsAndKeepsText()
    {
        var result = "<p>Hello <strong>syntax</strong></p><ul><li>Fast</li></ul>".HtmlToSearchText();

        Assert.DoesNotContain('<', result);
        Assert.DoesNotContain('>', result);
        Assert.Contains("Hello", result);
        Assert.Contains("syntax", result);
        Assert.Contains("Fast", result);
    }

    [Fact]
    public void HtmlToSearchText_RemovesCommentsAndCompactsBlankLines()
    {
        var result = """
            <div>
                <!-- hide me -->
                <p>First</p>


                <p>Second</p>
            </div>
            """.HtmlToSearchText();

        Assert.DoesNotContain("hide me", result);
        Assert.DoesNotContain("\n\n\n", result);
        Assert.Contains("First", result);
        Assert.Contains("Second", result);
    }

    [Theory]
    [InlineData("running", "run")]
    [InlineData("filters", "filter")]
    public void Stem_ReturnsEnglishStem(string value, string expected)
    {
        Assert.Equal(expected, value.Stem());
    }
}
