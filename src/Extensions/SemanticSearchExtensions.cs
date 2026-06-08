using System.Text.RegularExpressions;
using Lucene.Net.Analysis.En;
using Lucene.Net.Analysis.TokenAttributes;
using Lucene.Net.Util;
using ReverseMarkdown;

namespace Fynydd.Umbraco.Search.Qdrant.Extensions;

/// <summary>
/// Provides text normalization helpers used by semantic search indexing, chunking, stemming, and highlighting.
/// </summary>
public static partial class SemanticSearchExtensions
{
    private static readonly Config MarkdownConverterConfig = new()
    {
        GithubFlavored = true,
        RemoveComments = true,
    };

    /// <summary>
    /// Common English stop words ignored when highlighting search terms.
    /// </summary>
    public static readonly string[] SearchNoiseWordsAbridged =
    [
        "a", "about", "after", "all", "also", "an", "another", "any", "are", "as", "and", "at",
        "b", "be", "because", "been", "before", "being", "between", "but", "both", "by",
        "c", "came", "can", "come", "could",
        "d", "did", "do",
        "e", "each", "even",
        "f", "for", "from", "further", "furthermore",
        "g", "get", "got",
        "h", "has", "had", "he", "have", "her", "here", "him", "himself", "his", "how", "hi", "however",
        "i", "if", "in", "into", "is", "it", "its", "indeed",
        "j", "just",
        "k",
        "l", "like",
        "m", "made", "many", "me", "might", "more", "moreover", "most", "much", "must", "my",
        "n", "never", "not", "now",
        "o", "of", "on", "only", "other", "our", "out", "or", "over",
        "p",
        "q",
        "r",
        "s", "said", "same", "see", "should", "since", "she", "some", "still", "such",
        "t", "take", "than", "that", "the", "their", "them", "then", "there", "these", "therefore", "they", "this", "those", "through", "to", "too", "thus",
        "u", "under", "up",
        "v", "very",
        "w", "was", "way", "we", "well", "were", "what", "when", "where", "which", "while", "who", "will", "with", "would",
        "x",
        "y", "you", "your",
        "z"
    ];

    /// <summary>
    /// Provides extension methods for text values that are transformed before indexing or highlighting.
    /// </summary>
    /// <param name="text">The text value to transform.</param>
    extension(string text)
    {
        /// <summary>
        /// Repeats text to increase the influence of an indexed field in the generated embedding.
        /// </summary>
        public string ApplyFieldWeight(int weight)
        {
            weight = Math.Clamp(weight, 1, 5);

            return weight == 1
                ? text
                : string.Join("\n\n", Enumerable.Repeat(text, weight));
        }

        /// <summary>
        /// Splits Markdown into sections so heading context can stay attached to nearby body text.
        /// </summary>
        public List<string> SplitMarkdownSections()
        {
            var sections = new List<string>();
            var current = new List<string>();

            foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
            {
                if (line.StartsWith('#') && current.Count > 0)
                {
                    sections.Add(string.Join('\n', current).Trim());
                    current.Clear();
                }

                current.Add(line);
            }

            if (current.Count > 0)
                sections.Add(string.Join('\n', current).Trim());

            return sections.Where(section => string.IsNullOrWhiteSpace(section) == false).ToList();
        }

        /// <summary>
        /// Converts HTML to clean GitHub-flavored Markdown for vector-search chunking.
        /// </summary>
        /// <returns>The cleaned Markdown text.</returns>
        public string HtmlToSearchText()
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;
        
            var converter = new Converter(MarkdownConverterConfig);
            var markdown = CleanMarkdown(converter.Convert(MinifyHtml(text)));

            return markdown;
        }

        /// <summary>
        /// Removes blank blockquote lines, blank list items, trailing whitespace, and excess blank lines from Markdown.
        /// </summary>
        private string CleanMarkdown()
        {
            var markdown = MarkdownBlankBlockquoteLineRegex().Replace(text.ReplaceLineEndings("\n"), string.Empty);
        
            markdown = MarkdownBlankListItemLineRegex().Replace(markdown, string.Empty);
            markdown = MarkdownTrailingWhitespaceRegex().Replace(markdown, string.Empty);
            markdown = MarkdownExcessBlankLinesRegex().Replace(markdown, "\n\n");

            return markdown.Trim();
        }

        /// <summary>
        /// Gets the Lucene EnglishAnalyzer stem for a single word.
        /// </summary>
        /// <returns>The stemmed token.</returns>
        public string Stem()
        {
            using var analyzer = new EnglishAnalyzer(LuceneVersion.LUCENE_48);
            using var reader = new StringReader(text);
            using var stream = analyzer.GetTokenStream("text", reader);

            var term = stream.AddAttribute<ICharTermAttribute>();

            stream.Reset();

            var result = stream.IncrementToken()
                ? term.ToString()
                : text;

            stream.End();

            return result;
        }

        /// <summary>
        /// Compacts HTML whitespace while preserving tag boundaries before Markdown conversion.
        /// </summary>
        private string MinifyHtml()
        {
            var singleLine = WhitespaceRegex().Replace(text, " ");

            return HtmlStructuralWhitespaceRegex().Replace(singleLine, "><").Trim();
        }
    }

    /// <summary>
    /// Matches consecutive whitespace.
    /// </summary>
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
    
    /// <summary>
    /// Matches whitespace between HTML tags.
    /// </summary>
    [GeneratedRegex(@">\s+<")]
    private static partial Regex HtmlStructuralWhitespaceRegex();

    /// <summary>
    /// Matches empty Markdown blockquote lines.
    /// </summary>
    [GeneratedRegex(@"(?m)^> ?\n")]
    private static partial Regex MarkdownBlankBlockquoteLineRegex();

    /// <summary>
    /// Matches empty Markdown list item lines.
    /// </summary>
    [GeneratedRegex(@"(?m)^\s*(?:[-*+]|\d+\.)\s*\n")]
    private static partial Regex MarkdownBlankListItemLineRegex();

    /// <summary>
    /// Matches trailing whitespace before Markdown line endings.
    /// </summary>
    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex MarkdownTrailingWhitespaceRegex();

    /// <summary>
    /// Matches excess blank lines in Markdown.
    /// </summary>
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MarkdownExcessBlankLinesRegex();
}
