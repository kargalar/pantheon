namespace VideoPlayer.Core;

public readonly record struct EmbeddedSubtitleTrack(
    string Codec,
    bool Forced,
    bool Selected,
    string Language = "",
    string Title = "");

public static class SubtitleReferenceRules
{
    private static readonly HashSet<string> TextSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ass", "ssa", "subrip", "srt", "webvtt", "mov_text", "text",
        "microdvd", "subviewer", "subviewer1", "sami", "realtext", "mpl2", "vplayer", "pjs", "jacosub"
    };

    private static readonly string[] ExcludedTitleKeywords =
    [
        "forced", "forzati", "forzado", "forzados", "forcé",
        "signs", "songs", "song", "commentary", "karaoke", "chapters"
    ];

    public static int? SelectEmbeddedTextSubtitle(
        IReadOnlyList<EmbeddedSubtitleTrack> tracks,
        string? preferredAudioLanguage = null)
    {
        int? bestIndex = null;
        int bestScore = int.MinValue;

        for (var index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            if (track.Forced || !TextSubtitleCodecs.Contains(track.Codec))
                continue;

            if (HasExcludedTitle(track.Title))
                continue;

            int score = ScoreCandidateTrack(track, preferredAudioLanguage, index);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    private static bool HasExcludedTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        foreach (string keyword in ExcludedTitleKeywords)
        {
            if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static int ScoreCandidateTrack(EmbeddedSubtitleTrack track, string? preferredAudioLanguage, int index)
    {
        int score = 100 - index;
        if (track.Selected)
            score += 1000;

        string normLang = NormalizeLanguagePrefix(track.Language);
        string normAudio = NormalizeLanguagePrefix(preferredAudioLanguage);

        if (!string.IsNullOrEmpty(normLang))
        {
            if (!string.IsNullOrEmpty(normAudio) && string.Equals(normLang, normAudio, StringComparison.OrdinalIgnoreCase))
                score += 300;
            else if (normLang is "en" or "eng")
                score += 200;
            else if (normLang is "tr" or "tur")
                score += 150;
        }

        if (!string.IsNullOrWhiteSpace(track.Title)
            && (track.Title.Contains("sdh", StringComparison.OrdinalIgnoreCase)
                || track.Title.Contains("cc", StringComparison.OrdinalIgnoreCase)
                || track.Title.Contains("hearing", StringComparison.OrdinalIgnoreCase)))
        {
            score -= 25;
        }

        return score;
    }

    private static string NormalizeLanguagePrefix(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return string.Empty;

        string trimmed = language.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "eng" or "en-us" or "en-gb" => "en",
            "tur" or "tr-tr" => "tr",
            "ita" => "it",
            "fra" or "fre" => "fr",
            "deu" or "ger" => "de",
            "spa" => "es",
            "jpn" => "ja",
            "kor" => "ko",
            "zho" or "chi" => "zh",
            _ => trimmed.Length >= 2 ? trimmed[..2] : trimmed
        };
    }
}
