namespace VideoPlayer.Core;

public static class MediaTrackPreferenceRules
{
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
