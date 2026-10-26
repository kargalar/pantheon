using System.Text;
using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class SubtitleSynchronizationEngineTests
{
    [Fact]
    public void Parser_ExtractsCuesAcrossFormats_AndFiltersCreditsSdhAndMusic()
    {
        const string srtContent = """
            1
            00:00:01,000 --> 00:00:04,000
            Çeviri: Ümit Bulut / @HeyUmitBulut

            2
            00:00:05,200 --> 00:00:07,800
            [cell phone vibrates]

            3
            00:00:08,500 --> 00:00:10,900
            ♪ La la la ♪

            4
            00:00:12,000 --> 00:00:15,200
            [sighs] Merhaba, beni duyabiliyor musun?

            5
            00:00:16,000 --> 00:00:18,500
            Evet, gayet net duyuyorum.
            """;

        var cues = SubtitleParser.Parse(srtContent, ".srt");
        Assert.Equal(5, cues.Count);
        Assert.False(cues[0].IsSpokenDialogue); // Translator credit
        Assert.False(cues[1].IsSpokenDialogue); // SDH only
        Assert.False(cues[2].IsSpokenDialogue); // Music only
        Assert.True(cues[3].IsSpokenDialogue);  // Mixed SDH + spoken dialogue
        Assert.Equal("Merhaba, beni duyabiliyor musun?", cues[3].CleanDialogueText);
        Assert.True(cues[4].IsSpokenDialogue);
    }

    [Fact]
    public void Parser_SupportsAssVttAndMicroDvdSubFormats()
    {
        const string assContent = """
            [Script Info]
            Title: Test

            [V4+ Styles]
            Format: Name, Fontname, Fontsize
            Style: Default,Arial,20
            Style: Signs,Arial,20

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:02.00,0:00:04.50,Signs,,0,0,0,,{\pos(120,200)}NEW YORK
            Dialogue: 0,0:00:05.00,0:00:07.50,Default,,0,0,0,,{\i1}Hello there!{\i0}\NHow are you?
            Dialogue: 0,0:00:08.00,0:00:10.00,Default,,0,0,0,,{\p1}m 0 0 l 100 0 100 100 0 100{\p0}
            """;

        var assCues = SubtitleParser.Parse(assContent, ".ass");
        Assert.Equal(2, assCues.Count);
        Assert.False(assCues[0].IsSpokenDialogue); // Signs style
        Assert.True(assCues[1].IsSpokenDialogue);
        Assert.Contains("Hello there!", assCues[1].CleanDialogueText);

        const string vttContent = """
            WEBVTT

            00:01.500 --> 00:03.800 align:start position:10%
            <v Speaker>Welcome to the broadcast.</v>
            """;

        var vttCues = SubtitleParser.Parse(vttContent, ".vtt");
        Assert.Single(vttCues);
        Assert.True(vttCues[0].IsSpokenDialogue);
        Assert.Equal(1.5, vttCues[0].StartSeconds, 2);

        const string subContent = """
            {240}{312}First line|Second line
            """;

        var subCues = SubtitleParser.Parse(subContent, ".sub");
        Assert.Single(subCues);
        Assert.True(subCues[0].IsSpokenDialogue);
        Assert.InRange(subCues[0].StartSeconds, 9.5, 10.5);
    }

    [Theory]
    [InlineData(7.4, 1.0)]
    [InlineData(-11.2, 1.0)]
    [InlineData(2.5, 25.0 / 24.0)]
    [InlineData(-1.8, 24.0 / 25.0)]
    public void TimelineAligner_SubtitleToSubtitle_RecoversConstantOffsetAndFramerateDrift(double delay, double scale)
    {
        var referenceCues = CreateSyntheticEpisodeCues(cueCount: 90, startSeconds: 20.0);

        // Create shifted/stretched target cues with translator credits injected at start/end
        var targetBuilder = new StringBuilder();
        int idx = 1;
        targetBuilder.AppendLine($"{idx++}\n00:00:01,000 --> 00:00:04,500\nÇeviren: TestUser / @TestHandle\n");

        foreach (var cue in referenceCues)
        {
            double s = cue.StartSeconds * scale + delay;
            double e = cue.EndSeconds * scale + delay;
            targetBuilder.AppendLine($"{idx++}\n{SubtitleParser.FormatSrtTimestamp(s)} --> {SubtitleParser.FormatSrtTimestamp(e)}\n{cue.Text}\n");
        }

        targetBuilder.AppendLine($"{idx}\n00:25:00,000 --> 00:25:05,000\nwww.turkcealtyazi.org\n");

        var targetCues = SubtitleParser.Parse(targetBuilder.ToString(), ".srt");
        var outcome = SubtitleTimelineAligner.AlignToReferenceSubtitles(referenceCues, targetCues);

        double expectedScale = 1.0 / scale;
        double expectedOffset = -delay / scale;

        Assert.InRange(outcome.ScaleFactor, expectedScale - 0.003, expectedScale + 0.003);
        Assert.InRange(outcome.OffsetSeconds, expectedOffset - 0.25, expectedOffset + 0.25);

        // Check that all dialogue cues align closely with reference cues
        var alignedDialogue = outcome.AlignedCues.Where(static c => c.IsSpokenDialogue).ToList();
        Assert.Equal(referenceCues.Count, alignedDialogue.Count);
        for (int i = 0; i < referenceCues.Count; i++)
        {
            Assert.InRange(Math.Abs(alignedDialogue[i].StartSeconds - referenceCues[i].StartSeconds), 0.0, 0.25);
        }
    }

    [Fact]
    public void TimelineAligner_PiecewiseMultiAct_CorrectsCommercialBreakOffsetJumps()
    {
        // Simulate a 3-act TV episode (like What We Do in the Shadows) where commercial breaks shift each act differently:
        // Act 1 (20s..280s): target is +1.2s late (needs -1.2s offset)
        // Act 2 (320s..620s): target is +5.4s late (needs -5.4s offset)
        // Act 3 (660s..960s): target is +8.2s late (needs -8.2s offset)
        var refBuilder = new StringBuilder();
        var tgtBuilder = new StringBuilder();
        var random = new Random(42);
        int idx = 1;

        var actSpecs = new (double ActStart, int CueCount, double TargetDelay)[]
        {
            (25.0, 35, 1.2),
            (340.0, 35, 5.4),
            (680.0, 35, 8.2)
        };

        double currentRefTime = 0;
        foreach (var (actStart, count, delay) in actSpecs)
        {
            currentRefTime = Math.Max(currentRefTime, actStart);
            for (int i = 0; i < count; i++)
            {
                double duration = 1.6 + random.NextDouble() * 1.8;
                double gap = 0.6 + random.NextDouble() * 1.9;
                double rStart = currentRefTime;
                double rEnd = rStart + duration;
                currentRefTime = rEnd + gap;

                string text = $"Diyalog satırı numara {idx} konuşma metni.";
                refBuilder.AppendLine($"{idx}\n{SubtitleParser.FormatSrtTimestamp(rStart)} --> {SubtitleParser.FormatSrtTimestamp(rEnd)}\n{text}\n");
                tgtBuilder.AppendLine($"{idx}\n{SubtitleParser.FormatSrtTimestamp(rStart + delay)} --> {SubtitleParser.FormatSrtTimestamp(rEnd + delay)}\n{text}\n");
                idx++;
            }
        }

        var refCues = SubtitleParser.Parse(refBuilder.ToString(), ".srt");
        var tgtCues = SubtitleParser.Parse(tgtBuilder.ToString(), ".srt");

        var outcome = SubtitleTimelineAligner.AlignToReferenceSubtitles(refCues, tgtCues);

        Assert.True(outcome.SegmentCount >= 3, $"Expected >= 3 piecewise segments, got {outcome.SegmentCount}. Diagnostics: {outcome.Diagnostics}");
        Assert.Equal(refCues.Count, outcome.AlignedCues.Count);

        for (int i = 0; i < refCues.Count; i++)
        {
            double error = Math.Abs(outcome.AlignedCues[i].StartSeconds - refCues[i].StartSeconds);
            Assert.InRange(error, 0.0, 0.25);
        }
    }

    [Fact]
    public void AudioSpeechAnalyzer_SynthesizedSpeechWithNoise_RecoversOffsetViaAudioEnvelope()
    {
        var cues = CreateSyntheticEpisodeCues(cueCount: 30, startSeconds: 8.0);
        double totalDurationSeconds = cues[^1].EndSeconds + 25.0;
        int totalSamples = (int)(totalDurationSeconds * AudioSpeechAnalyzer.DefaultSampleRate);
        short[] pcm = new short[totalSamples];
        var rng = new Random(1234);

        // Background low-level noise
        for (int n = 0; n < totalSamples; n++)
            pcm[n] = (short)rng.Next(-45, 46);

        // Synthesize syllabic-modulated speech formants at exact reference cue intervals
        foreach (var cue in cues)
        {
            int sIdx = (int)(cue.StartSeconds * AudioSpeechAnalyzer.DefaultSampleRate);
            int eIdx = Math.Min(totalSamples, (int)((cue.StartSeconds + cue.EstimatedSpeechDuration) * AudioSpeechAnalyzer.DefaultSampleRate));
            for (int n = sIdx; n < eIdx; n++)
            {
                double t = (double)n / AudioSpeechAnalyzer.DefaultSampleRate;
                double syllabicEnv = 0.55 + 0.45 * Math.Sin(2.0 * Math.PI * 4.5 * t);
                double formant1 = Math.Sin(2.0 * Math.PI * 520.0 * t);
                double formant2 = 0.6 * Math.Sin(2.0 * Math.PI * 1480.0 * t);
                double sample = syllabicEnv * (formant1 + formant2) * 9500.0;
                pcm[n] = (short)Math.Clamp(pcm[n] + sample, short.MinValue, short.MaxValue);
            }
        }

        var envelope = AudioSpeechAnalyzer.AnalyzePcmSamples(pcm);
        Assert.True(envelope.SpeechFrameCount >= 100);

        // Shift target subtitles by +4.35s
        const double delay = 4.35;
        var shiftedSrt = SubtitleParser.FormatSrt(cues, c => (c.StartSeconds + delay, c.EndSeconds + delay));
        var shiftedCues = SubtitleParser.Parse(shiftedSrt, ".srt");

        var outcome = SubtitleTimelineAligner.AlignToAudioEnvelope(envelope, shiftedCues);
        Assert.InRange(outcome.OffsetSeconds, -delay - 0.30, -delay + 0.30);
        Assert.InRange(outcome.ScaleFactor, 0.998, 1.002);
    }

    [Fact]
    public void SubtitleReferenceRules_SkipsForcedAndSignsTitles_AndPrefersAudioLanguageOrEnglish()
    {
        // Reproduces What We Do in the Shadows S04 track layout:
        // 0: subrip, Forced=false, Title="Forced", Lang="it"
        // 1: subrip, Forced=false, Title="", Lang="it"
        // 2: subrip, Forced=false, Title="", Lang="eng"
        var tracks = new EmbeddedSubtitleTrack[]
        {
            new("subrip", Forced: false, Selected: false, Language: "it", Title: "Forced"),
            new("subrip", Forced: false, Selected: false, Language: "it", Title: ""),
            new("subrip", Forced: false, Selected: false, Language: "eng", Title: "")
        };

        int? selected = SubtitleReferenceRules.SelectEmbeddedTextSubtitle(tracks, preferredAudioLanguage: "eng");
        Assert.Equal(2, selected);

        // Even when preferredAudioLanguage is not specified, English is preferred over Italian and Forced is rejected
        int? selectedWithoutAudioHint = SubtitleReferenceRules.SelectEmbeddedTextSubtitle(tracks);
        Assert.Equal(2, selectedWithoutAudioHint);
    }

    [Fact]
    public void SubtitleTextEncoding_AutoDetectsTurkishWindows1254_WhenInferOrAutoRequested()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string turkishSrt = """
            1
            00:00:02,000 --> 00:00:05,000
            Şu ağacın altında ışıklar çok güzel görünüyor, değil mi?
            """;

        byte[] cp1254Bytes = Encoding.GetEncoding(1254).GetBytes(turkishSrt);
        string decoded = SubtitleTextEncoding.DecodeSubtitleText(cp1254Bytes, "infer", out string resolvedEncoding);

        Assert.Equal("windows-1254", resolvedEncoding);
        Assert.Contains("Şu ağacın altında ışıklar", decoded);
    }

    private static IReadOnlyList<SubtitleCue> CreateSyntheticEpisodeCues(int cueCount, double startSeconds)
    {
        var rng = new Random(99);
        var builder = new StringBuilder();
        double t = startSeconds;

        for (int i = 1; i <= cueCount; i++)
        {
            double dur = 1.4 + rng.NextDouble() * 2.1;
            double gap = (i % 12 == 0)
                ? 5.5 + rng.NextDouble() * 4.0
                : 0.45 + rng.NextDouble() * 1.8;

            builder.AppendLine($"{i}");
            builder.AppendLine($"{SubtitleParser.FormatSrtTimestamp(t)} --> {SubtitleParser.FormatSrtTimestamp(t + dur)}");
            builder.AppendLine($"Dialogue line number {i} with natural cadence.");
            builder.AppendLine();

            t += dur + gap;
        }

        return SubtitleParser.Parse(builder.ToString(), ".srt");
    }
}

