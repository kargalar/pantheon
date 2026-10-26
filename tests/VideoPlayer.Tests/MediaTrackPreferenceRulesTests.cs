using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class MediaTrackPreferenceRulesTests
{
    private sealed record Track(string Language, int Ordinal, string Title = "");

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

    [Fact]
    public void FindPreferredAudioTrackIndex_PrefersEnglishWhenMultipleTracksExist()
    {
        var tracks = new[]
        {
            new Track("tur", 1, "Türkçe"),
            new Track("eng", 2, "English")
        };

        var index = MediaTrackPreferenceRules.FindPreferredAudioTrackIndex(
            tracks, null, 0, track => track.Language, track => track.Title, track => track.Ordinal);

        Assert.Equal(1, index);
    }

    [Fact]
    public void FindPreferredAudioTrackIndex_PrefersEnglishByTitleWhenLanguageIsUndefined()
    {
        var tracks = new[]
        {
            new Track("und", 1, "Russian"),
            new Track("und", 2, "English 5.1 Surround")
        };

        var index = MediaTrackPreferenceRules.FindPreferredAudioTrackIndex(
            tracks, null, 0, track => track.Language, track => track.Title, track => track.Ordinal);

        Assert.Equal(1, index);
    }

    [Fact]
    public void FindPreferredAudioTrackIndex_PrefersMainEnglishOverCommentary()
    {
        var tracks = new[]
        {
            new Track("tur", 1, "Türkçe"),
            new Track("eng", 2, "Director Commentary"),
            new Track("eng", 3, "English (Original)")
        };

        var index = MediaTrackPreferenceRules.FindPreferredAudioTrackIndex(
            tracks, null, 0, track => track.Language, track => track.Title, track => track.Ordinal);

        Assert.Equal(2, index);
    }

    [Fact]
    public void FindPreferredAudioTrackIndex_DoesNotForceEnglishWhenOnlySingleTrackExists()
    {
        var tracks = new[]
        {
            new Track("tur", 1, "Türkçe")
        };

        var index = MediaTrackPreferenceRules.FindPreferredAudioTrackIndex(
            tracks, null, 0, track => track.Language, track => track.Title, track => track.Ordinal);

        Assert.Equal(-1, index);
    }

    [Fact]
    public void FindPreferredAudioTrackIndex_FallsBackToUserPreferenceWhenNoEnglishFound()
    {
        var tracks = new[]
        {
            new Track("jpn", 1, "Japanese"),
            new Track("fra", 2, "French")
        };

        var index = MediaTrackPreferenceRules.FindPreferredAudioTrackIndex(
            tracks, "fra", 2, track => track.Language, track => track.Title, track => track.Ordinal);

        Assert.Equal(1, index);
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("eng", true)]
    [InlineData("english", true)]
    [InlineData("en-US", true)]
    [InlineData("en_GB", true)]
    [InlineData("tur", false)]
    [InlineData("de", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsEnglishLanguage_ValidatesCorrectly(string? language, bool expected)
    {
        var result = MediaTrackPreferenceRules.IsEnglishLanguage(language);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("English", true)]
    [InlineData("English [Original]", true)]
    [InlineData("ENG 5.1", true)]
    [InlineData("Audio (English)", true)]
    [InlineData("Türkçe Dublaj", false)]
    [InlineData("Russian", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsEnglishTitle_ValidatesCorrectly(string? title, bool expected)
    {
        var result = MediaTrackPreferenceRules.IsEnglishTitle(title);

        Assert.Equal(expected, result);
    }
}
