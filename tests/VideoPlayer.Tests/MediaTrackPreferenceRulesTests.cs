using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class MediaTrackPreferenceRulesTests
{
    private sealed record Track(string Language, int Ordinal);

    [Fact]
    public void FindPreferredTrackIndex_PrefersExactLanguageAndOrdinal()
    {
        var tracks = new[]
        {
            new Track("eng", 1),
            new Track("eng", 2),
            new Track("jpn", 3)
        };

        var index = MediaTrackPreferenceRules.FindPreferredTrackIndex(
            tracks, "ENG", 2, track => track.Language, track => track.Ordinal);

        Assert.Equal(1, index);
    }

    [Fact]
    public void FindPreferredTrackIndex_FallsBackToLanguageWhenOrdinalIsUnavailable()
    {
        var tracks = new[] { new Track("jpn", 1), new Track("eng", 2) };

        var index = MediaTrackPreferenceRules.FindPreferredTrackIndex(
            tracks, "eng", 8, track => track.Language, track => track.Ordinal);

        Assert.Equal(1, index);
    }

    [Fact]
    public void FindPreferredTrackIndex_FallsBackToOrdinalWhenLanguageIsUnavailable()
    {
        var tracks = new[] { new Track("jpn", 1), new Track("eng", 2) };

        var index = MediaTrackPreferenceRules.FindPreferredTrackIndex(
            tracks, "fra", 2, track => track.Language, track => track.Ordinal);

        Assert.Equal(1, index);
    }

    [Fact]
    public void FindPreferredTrackIndex_ReturnsMinusOneWhenNoPreferenceMatches()
    {
        var tracks = new[] { new Track("jpn", 1), new Track("eng", 2) };

        var index = MediaTrackPreferenceRules.FindPreferredTrackIndex(
            tracks, null, 0, track => track.Language, track => track.Ordinal);

        Assert.Equal(-1, index);
    }
}
