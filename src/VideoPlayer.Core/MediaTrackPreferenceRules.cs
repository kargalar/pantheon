using System.Text.RegularExpressions;

namespace VideoPlayer.Core;

public static partial class MediaTrackPreferenceRules
{
    [GeneratedRegex(@"\b(eng|english)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishTitleRegex();

    [GeneratedRegex(@"\b(commentary|yorum)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommentaryTitleRegex();

    public static bool IsEnglishLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return false;
        var trimmed = language.Trim();
        return trimmed.Equals("en", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("eng", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("english", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("en-", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("en_", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsEnglishTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title) && EnglishTitleRegex().IsMatch(title);

    public static bool IsEnglishTrack(string? language, string? title) =>
        IsEnglishLanguage(language) || IsEnglishTitle(title);

    public static bool IsCommentary(string? title) =>
        !string.IsNullOrWhiteSpace(title) && CommentaryTitleRegex().IsMatch(title);

    public static int FindPreferredAudioTrackIndex<T>(
        IReadOnlyList<T> tracks,
        string? preferredLanguage,
        int preferredOrdinal,
        Func<T, string?> languageSelector,
        Func<T, string?> titleSelector,
        Func<T, int> ordinalSelector)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(languageSelector);
        ArgumentNullException.ThrowIfNull(titleSelector);
        ArgumentNullException.ThrowIfNull(ordinalSelector);

        if (tracks.Count > 1)
        {
            var nonCommentaryEnglishIndex = FindIndex(tracks, track =>
                IsEnglishTrack(languageSelector(track), titleSelector(track))
                && !IsCommentary(titleSelector(track)));
            if (nonCommentaryEnglishIndex >= 0) return nonCommentaryEnglishIndex;

            var anyEnglishIndex = FindIndex(tracks, track =>
                IsEnglishTrack(languageSelector(track), titleSelector(track)));
            if (anyEnglishIndex >= 0) return anyEnglishIndex;
        }

        return FindPreferredTrackIndex(tracks, preferredLanguage, preferredOrdinal, languageSelector, ordinalSelector);
    }

    public static int FindPreferredTrackIndex<T>(
        IReadOnlyList<T> tracks,
        string? preferredLanguage,
        int preferredOrdinal,
        Func<T, string?> languageSelector,
        Func<T, int> ordinalSelector)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(languageSelector);
        ArgumentNullException.ThrowIfNull(ordinalSelector);

        var hasPreferredLanguage = !string.IsNullOrWhiteSpace(preferredLanguage);
        if (hasPreferredLanguage && preferredOrdinal > 0)
        {
            var exactMatch = FindIndex(tracks, track =>
                ordinalSelector(track) == preferredOrdinal
                && string.Equals(languageSelector(track), preferredLanguage, StringComparison.OrdinalIgnoreCase));
            if (exactMatch >= 0) return exactMatch;
        }

        if (hasPreferredLanguage)
        {
            var languageMatch = FindIndex(tracks, track =>
                string.Equals(languageSelector(track), preferredLanguage, StringComparison.OrdinalIgnoreCase));
            if (languageMatch >= 0) return languageMatch;
        }

        return preferredOrdinal > 0
            ? FindIndex(tracks, track => ordinalSelector(track) == preferredOrdinal)
            : -1;
    }

    private static int FindIndex<T>(IReadOnlyList<T> items, Func<T, bool> predicate)
    {
        for (var index = 0; index < items.Count; index++)
            if (predicate(items[index])) return index;
        return -1;
    }
}
