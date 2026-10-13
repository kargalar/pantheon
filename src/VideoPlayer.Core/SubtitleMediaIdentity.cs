using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoPlayer.Core;

public sealed partial record SubtitleMediaIdentity(string Title, int? Year, int? Season, int? Episode, bool IsMultiEpisode)
{
    public bool IsEpisode => Season.HasValue && Episode.HasValue;

    public static SubtitleMediaIdentity FromPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Replace('.', ' ');
        var episode = EpisodePattern().Match(name);
        var titleEnd = episode.Success ? episode.Index : name.Length;
        var yearMatch = YearPattern().Matches(name[..titleEnd]).Cast<Match>().LastOrDefault()
            ?? Match.Empty;
        int? year = yearMatch.Success && yearMatch.Index > 0 ? int.Parse(yearMatch.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        if (yearMatch.Success && yearMatch.Index > 0) titleEnd = yearMatch.Index;
        var release = ReleasePattern().Match(name[..titleEnd]);
        if (release.Success) titleEnd = release.Index;
        var title = WhitespacePattern().Replace(name[..titleEnd], " ").Trim(' ', '-', '(', ')', '[', ']');
        return new(title, year,
            episode.Success ? int.Parse(episode.Groups["season"].Value, CultureInfo.InvariantCulture) : null,
            episode.Success ? int.Parse(episode.Groups["episode"].Value, CultureInfo.InvariantCulture) : null,
            episode.Success && MultiEpisodePattern().IsMatch(name[(episode.Index + episode.Length)..]));
    }

    public static string NormalizeTitle(string? title) => new((title ?? string.Empty)
        .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public bool MatchesEpisode(SubtitleMediaIdentity other) => !IsMultiEpisode && !other.IsMultiEpisode
        && Season == other.Season && Episode == other.Episode;

    [GeneratedRegex(@"\b(?:S(?<season>\d{1,2})\s*E(?<episode>\d{1,3})|(?<season>\d{1,2})x(?<episode>\d{1,3}))(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodePattern();

    [GeneratedRegex(@"^\s*(?:E\d|[-+]\s*(?:E)?\d|x\d)", RegexOptions.IgnoreCase)]
    private static partial Regex MultiEpisodePattern();

    [GeneratedRegex(@"\b((?:19|20)\d{2})\b")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"\b(?:\d{3,4}p|2160p|WEB[- ]?(?:DL|Rip)|BluRay|BDRip|BRRip|HDTV|DVDRip|x26[45]|HEVC|REMUX)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReleasePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}

public static class SubtitleAutomationOptions
{
    public const string DefaultLanguage = "en";
    public const string DefaultSynchronizer = "ffsubsync";
    public const int MaximumSubtitleBytes = 5 * 1024 * 1024;
    public static IReadOnlyList<(string Label, string Code)> Languages { get; } =
    [
        (UiText.Turkish, "tr"), ("English", "en"), (UiText.German, "de"), (UiText.French, "fr"),
        (UiText.Spanish, "es"), (UiText.Italian, "it"), (UiText.Portuguese, "pt"), (UiText.Russian, "ru"),
        (UiText.Arabic, "ar"), (UiText.Japanese, "ja"), (UiText.Korean, "ko"), (UiText.Chinese, "zh-CN")
    ];

    public static string NormalizeLanguage(string? value) => Languages.FirstOrDefault(
        language => string.Equals(language.Code, value?.Trim(), StringComparison.OrdinalIgnoreCase)).Code ?? DefaultLanguage;

}
