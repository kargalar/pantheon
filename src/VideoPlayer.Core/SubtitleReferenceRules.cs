namespace VideoPlayer.Core;

public readonly record struct EmbeddedSubtitleTrack(string Codec, bool Forced, bool Selected);

public static class SubtitleReferenceRules
{
    private static readonly HashSet<string> TextSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ass", "ssa", "subrip", "srt", "webvtt", "mov_text", "text",
        "microdvd", "subviewer", "subviewer1", "sami", "realtext", "mpl2", "vplayer", "pjs", "jacosub"
    };

    public static int? SelectEmbeddedTextSubtitle(IReadOnlyList<EmbeddedSubtitleTrack> tracks)
    {
        int? firstTextTrack = null;
        for (var index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            if (track.Forced || !TextSubtitleCodecs.Contains(track.Codec)) continue;
            if (track.Selected) return index;
            firstTextTrack ??= index;
        }
        return firstTextTrack;
    }
}
