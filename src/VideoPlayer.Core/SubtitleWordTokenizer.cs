using System.Text.RegularExpressions;

namespace VideoPlayer.Core;

/// <summary>One whitespace-separated chunk of a subtitle line, split into punctuation and the word itself.</summary>
/// <param name="Prefix">Leading punctuation (quotes, dialogue dashes, ...).</param>
/// <param name="Word">The word as displayed, or null when the chunk holds no letters (e.g. "-" or "♪").</param>
/// <param name="Suffix">Trailing punctuation.</param>
public sealed record SubtitleToken(string Prefix, string? Word, string Suffix)
{
    public bool IsWord => Word is not null;
    public string? Key => Word is null ? null : SubtitleWordTokenizer.Normalize(Word);
}

/// <summary>Splits subtitle text into lines of tokens and normalizes words for vocabulary lookups.</summary>
public static partial class SubtitleWordTokenizer
{
    public static IReadOnlyList<IReadOnlyList<SubtitleToken>> Tokenize(string? text)
    {
        var lines = new List<IReadOnlyList<SubtitleToken>>();
        if (string.IsNullOrWhiteSpace(text)) return lines;

        var clean = MarkupPattern().Replace(text, "").Replace("\r", "");
        foreach (var rawLine in clean.Split('\n'))
        {
            var tokens = new List<SubtitleToken>();
            foreach (var chunk in rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                tokens.Add(Split(chunk));
            if (tokens.Count > 0) lines.Add(tokens);
        }
        return lines;
    }

    public static SubtitleToken Split(string chunk)
    {
        var start = 0;
        while (start < chunk.Length && !char.IsLetter(chunk[start])) start++;
        if (start == chunk.Length) return new SubtitleToken(chunk, null, "");

        var end = chunk.Length - 1;
        while (end > start && !char.IsLetter(chunk[end])) end--;
        return new SubtitleToken(chunk[..start], chunk[start..(end + 1)], chunk[(end + 1)..]);
    }

    /// <summary>Lower-cases the word, unifies apostrophes and drops a possessive "'s".</summary>
    public static string Normalize(string word)
    {
        var value = word.Trim().Replace('’', '\'').Replace('‘', '\'').ToLowerInvariant();
        if (value.EndsWith("'s", StringComparison.Ordinal) && value.Length > 3) value = value[..^2];
        return value;
    }

    [GeneratedRegex(@"<[^>]+>|\{\\[^}]*\}")]
    private static partial Regex MarkupPattern();
}
