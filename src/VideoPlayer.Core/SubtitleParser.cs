using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoPlayer.Core;

public sealed record SubtitleCue(
    int Index,
    double StartSeconds,
    double EndSeconds,
    string Text,
    string CleanDialogueText,
    bool IsSpokenDialogue,
    double EstimatedSpeechDuration)
{
    public double Duration => Math.Max(0, EndSeconds - StartSeconds);
}

public static partial class SubtitleParser
{
    private const double MinimumCueDurationSeconds = 0.12;
    private const double BaseSpeechReactionSeconds = 0.32;
    private const double SecondsPerDialogueCharacter = 0.065;
    private const double SecondsPerDialogueWord = 0.08;

    private static readonly string[] CreditKeywords =
    [
        "çeviri", "ceviri", "çeviren", "ceviren", "altyazı:", "altyazi:", "senkron",
        "iyi seyirler", "subtitles by", "subtitle by", "synced by", "synched by",
        "corrected by", "resync", "opensubtitles", "turkcealtyazi", "divxplanet",
        "subscene", "podnapisi", "addic7ed", "yify", "subdl", "encoded by", "ripped by"
    ];

    private static readonly HashSet<string> NonDialogueAssStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        "sign", "signs", "title", "titles", "logo", "op", "ed", "opening", "ending",
        "karaoke", "romaji", "kanji", "fx", "effect", " effects", "screen", "typeset", "credit", "credits"
    };

    public static IReadOnlyList<SubtitleCue> Parse(string content, string extension)
    {
        if (string.IsNullOrWhiteSpace(content)) return [];
        var normalized = NormalizeLineEndings(content);
        var ext = extension.Trim().ToLowerInvariant();
        var rawCues = ext switch
        {
            ".ass" or ".ssa" => ParseAssOrSsa(normalized),
            ".vtt" => ParseWebVtt(normalized),
            ".sub" => ParseSub(normalized),
            _ => ParseSrt(normalized)
        };
        if (rawCues.Count == 0 && ext != ".srt")
            rawCues = ParseSrt(normalized);

        return FinalizeCues(rawCues);
    }

    public static IReadOnlyList<SubtitleCue> FilterDialogueCues(IReadOnlyList<SubtitleCue> cues)
    {
        var dialogue = cues.Where(static cue => cue.IsSpokenDialogue).ToList();
        return dialogue.Count >= 4 ? dialogue : cues;
    }

    public static string FormatSrt(IReadOnlyList<SubtitleCue> cues) =>
        FormatSrt(cues, static cue => (cue.StartSeconds, cue.EndSeconds));

    public static string FormatSrt(IReadOnlyList<SubtitleCue> cues, Func<SubtitleCue, (double Start, double End)> transform)
    {
        var builder = new StringBuilder(cues.Count * 64);
        var outputIndex = 0;
        var previousEnd = 0.0;

        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            var (start, end) = transform(cue);
            if (!double.IsFinite(start) || !double.IsFinite(end)) continue;
            if (end <= 0.05) continue;

            start = Math.Max(0, start);
            if (start < previousEnd && start > previousEnd - 0.15)
                start = previousEnd;

            var minDuration = Math.Min(MinimumCueDurationSeconds, Math.Max(0.08, cue.Duration));
            if (end < start + minDuration)
                end = start + minDuration;

            if (i + 1 < cues.Count)
            {
                var (nextStart, _) = transform(cues[i + 1]);
                if (double.IsFinite(nextStart) && nextStart > start + 0.08 && end > nextStart)
                    end = Math.Max(start + 0.08, nextStart - 0.005);
            }

            var text = CleanOutputText(cue.Text);
            if (string.IsNullOrWhiteSpace(text)) continue;

            outputIndex++;
            previousEnd = Math.Max(previousEnd, end);
            if (outputIndex > 1) builder.Append('\n');
            builder.Append(outputIndex.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append(FormatSrtTimestamp(start)).Append(" --> ").Append(FormatSrtTimestamp(end)).Append('\n');
            builder.Append(text).Append('\n');
        }

        return builder.ToString();
    }

    public static string FormatSrtTimestamp(double seconds)
    {
        var clampedMillis = (long)Math.Round(Math.Max(0, seconds) * 1000.0, MidpointRounding.AwayFromZero);
        var span = TimeSpan.FromMilliseconds(clampedMillis);
        var totalHours = (int)span.TotalHours;
        return string.Create(CultureInfo.InvariantCulture,
            $"{totalHours:00}:{span.Minutes:00}:{span.Seconds:00},{span.Milliseconds:000}");
    }

    private static List<(double Start, double End, string Text, bool ForceNonDialogue)> ParseSrt(string content)
    {
        var results = new List<(double Start, double End, string Text, bool ForceNonDialogue)>();
        var lines = content.Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
            if (i >= lines.Length) break;

            var timeLineIndex = i;
            if (!TryParseTimingLine(lines[timeLineIndex], out var start, out var end))
            {
                if (i + 1 < lines.Length && IsCueNumberLine(lines[i]) && TryParseTimingLine(lines[i + 1], out start, out end))
                {
                    timeLineIndex = i + 1;
                }
                else
                {
                    i++;
                    continue;
                }
            }

            i = timeLineIndex + 1;
            var textLines = new List<string>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                if (TryParseTimingLine(lines[i], out _, out _)) break;
                if (i + 1 < lines.Length && IsCueNumberLine(lines[i]) && TryParseTimingLine(lines[i + 1], out _, out _))
                    break;
                textLines.Add(lines[i].TrimEnd());
                i++;
            }

            if (textLines.Count > 0 && end > start)
                results.Add((start, end, string.Join('\n', textLines), false));
        }

        return results;
    }

    private static List<(double Start, double End, string Text, bool ForceNonDialogue)> ParseWebVtt(string content)
    {
        var results = new List<(double Start, double End, string Text, bool ForceNonDialogue)>();
        var lines = content.Split('\n');
        var i = 0;

        if (i < lines.Length && lines[i].TrimStart().StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
            i++;

        while (i < lines.Length)
        {
            while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
            if (i >= lines.Length) break;

            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("NOTE", StringComparison.Ordinal)
                || trimmed.StartsWith("STYLE", StringComparison.Ordinal)
                || trimmed.StartsWith("REGION", StringComparison.Ordinal))
            {
                i++;
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])) i++;
                continue;
            }

            var timeLineIndex = i;
            if (!TryParseTimingLine(lines[timeLineIndex], out var start, out var end))
            {
                if (i + 1 < lines.Length && TryParseTimingLine(lines[i + 1], out start, out end))
                {
                    timeLineIndex = i + 1;
                }
                else
                {
                    i++;
                    continue;
                }
            }

            i = timeLineIndex + 1;
            var textLines = new List<string>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                if (TryParseTimingLine(lines[i], out _, out _)) break;
                var cleanedLine = VttInlineTagPattern().Replace(lines[i].TrimEnd(), string.Empty);
                if (!string.IsNullOrWhiteSpace(cleanedLine))
                    textLines.Add(cleanedLine);
                i++;
            }

            if (textLines.Count > 0 && end > start)
                results.Add((start, end, string.Join('\n', textLines), false));
        }

        return results;
    }

    private static List<(double Start, double End, string Text, bool ForceNonDialogue)> ParseAssOrSsa(string content)
    {
        var results = new List<(double Start, double End, string Text, bool ForceNonDialogue)>();
        var lines = content.Split('\n');
        var inEvents = false;
        var startCol = 1;
        var endCol = 2;
        var styleCol = 3;
        var nameCol = 4;
        var effectCol = 8;
        var textCol = 9;
        var columnCount = 10;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inEvents) continue;

            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                var columns = line[7..].Split(',').Select(c => c.Trim()).ToArray();
                columnCount = Math.Max(1, columns.Length);
                for (var idx = 0; idx < columns.Length; idx++)
                {
                    if (columns[idx].Equals("Start", StringComparison.OrdinalIgnoreCase)) startCol = idx;
                    else if (columns[idx].Equals("End", StringComparison.OrdinalIgnoreCase)) endCol = idx;
                    else if (columns[idx].Equals("Style", StringComparison.OrdinalIgnoreCase)) styleCol = idx;
                    else if (columns[idx].Equals("Name", StringComparison.OrdinalIgnoreCase)) nameCol = idx;
                    else if (columns[idx].Equals("Effect", StringComparison.OrdinalIgnoreCase)) effectCol = idx;
                    else if (columns[idx].Equals("Text", StringComparison.OrdinalIgnoreCase)) textCol = idx;
                }
                continue;
            }

            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
            var payload = line[9..].TrimStart();
            var parts = payload.Split(',', columnCount);
            if (parts.Length <= Math.Max(startCol, Math.Max(endCol, textCol))) continue;
            if (!TryParseTimestamp(parts[startCol].Trim(), out var start)
                || !TryParseTimestamp(parts[endCol].Trim(), out var end)
                || end <= start) continue;

            var style = styleCol < parts.Length ? parts[styleCol].Trim() : string.Empty;
            var actor = nameCol < parts.Length ? parts[nameCol].Trim() : string.Empty;
            var effect = effectCol < parts.Length ? parts[effectCol].Trim() : string.Empty;
            var rawText = parts[textCol];

            if (AssDrawingPattern().IsMatch(rawText)) continue;
            var isNonDialogueStyle = IsNonDialogueAssMetadata(style, actor, effect)
                || rawText.Contains(@"\pos(", StringComparison.OrdinalIgnoreCase) && ! style.Contains("Default", StringComparison.OrdinalIgnoreCase) && !style.Contains("Main", StringComparison.OrdinalIgnoreCase);

            var cleanAssText = AssOverridePattern().Replace(rawText, string.Empty)
                .Replace(@"\N", "\n", StringComparison.Ordinal)
                .Replace(@"\n", "\n", StringComparison.Ordinal)
                .Replace(@"\h", " ", StringComparison.Ordinal)
                .Trim();

            if (!string.IsNullOrWhiteSpace(cleanAssText))
                results.Add((start, end, cleanAssText, isNonDialogueStyle));
        }

        return results;
    }

    private static List<(double Start, double End, string Text, bool ForceNonDialogue)> ParseSub(string content)
    {
        var results = new List<(double Start, double End, string Text, bool ForceNonDialogue)>();
        var frameRate = 23.976;
        foreach (var rawLine in content.Split('\n'))
        {
            var match = MicroDvdPattern().Match(rawLine.Trim());
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var startFrame)
                || !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var endFrame))
                continue;
            var text = match.Groups[3].Value.Trim();
            if (startFrame <= 1 && endFrame <= 1 && double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var detectedFps) && detectedFps is >= 10 and <= 120)
            {
                frameRate = detectedFps;
                continue;
            }
            if (endFrame <= startFrame) continue;
            var formattedText = text.Replace('|', '\n').Trim();
            if (!string.IsNullOrWhiteSpace(formattedText))
                results.Add((startFrame / frameRate, endFrame / frameRate, formattedText, false));
        }
        return results;
    }

    private static IReadOnlyList<SubtitleCue> FinalizeCues(
        List<(double Start, double End, string Text, bool ForceNonDialogue)> rawCues)
    {
        if (rawCues.Count == 0) return [];
        rawCues.Sort((a, b) =>
        {
            var cmp = a.Start.CompareTo(b.Start);
            return cmp != 0 ? cmp : a.End.CompareTo(b.End);
        });

        var finalized = new List<SubtitleCue>(rawCues.Count);
        for (var i = 0; i < rawCues.Count; i++)
        {
            var (start, end, text, forceNonDialogue) = rawCues[i];
            if (finalized.Count > 0)
            {
                var prev = finalized[^1];
                if (Math.Abs(prev.StartSeconds - start) < 0.02
                    && Math.Abs(prev.EndSeconds - end) < 0.02
                    && string.Equals(prev.Text, text, StringComparison.Ordinal))
                    continue;
            }

            var (cleanDialogue, isSpoken, speechDuration) = AnalyzeDialogue(text, end - start, forceNonDialogue);
            finalized.Add(new SubtitleCue(
                finalized.Count + 1,
                start,
                end,
                text,
                cleanDialogue,
                isSpoken,
                speechDuration));
        }

        return finalized;
    }

    private static (string CleanDialogue, bool IsSpoken, double SpeechDuration) AnalyzeDialogue(
        string rawText, double cueDuration, bool forceNonDialogue)
    {
        var withoutTags = HtmlTagPattern().Replace(AssOverridePattern().Replace(rawText, " "), " ");
        var normalized = WhitespacePattern().Replace(withoutTags, " ").Trim();
        if (forceNonDialogue || string.IsNullOrWhiteSpace(normalized))
            return (string.Empty, false, 0);

        if (IsMusicOrCreditText(normalized))
            return (normalized, false, 0);

        var withoutSdh = SdhBracketPattern().Replace(normalized, " ");
        withoutSdh = SpeakerLabelPattern().Replace(withoutSdh, " ");
        withoutSdh = WhitespacePattern().Replace(withoutSdh, " ").Trim(' ', '-', '–', '—', ':');

        if (string.IsNullOrWhiteSpace(withoutSdh) || IsMusicOrCreditText(withoutSdh))
            return (string.Empty, false, 0);

        var letterCount = 0;
        var digitCount = 0;
        foreach (var ch in withoutSdh)
        {
            if (char.IsLetter(ch)) letterCount++;
            else if (char.IsDigit(ch)) digitCount++;
        }

        if (letterCount == 0 && digitCount == 0)
            return (string.Empty, false, 0);

        var wordCount = withoutSdh.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var estimatedDuration = BaseSpeechReactionSeconds
            + (letterCount + digitCount) * SecondsPerDialogueCharacter
            + Math.Max(0, wordCount - 1) * SecondsPerDialogueWord;
        var clampedDuration = Math.Clamp(estimatedDuration, Math.Min(0.35, cueDuration), Math.Max(0.35, cueDuration));
        return (withoutSdh, true, clampedDuration);
    }

    private static bool IsMusicOrCreditText(string text)
    {
        if (text.Contains('♪') || text.Contains('♫') || text.Contains('¶'))
            return true;
        if (text.StartsWith('#') && text.EndsWith('#') && text.Length > 2)
            return true;

        var lower = text.ToLowerInvariant();
        foreach (var keyword in CreditKeywords)
            if (lower.Contains(keyword, StringComparison.Ordinal)) return true;

        if (SocialHandleOrUrlPattern().IsMatch(text))
            return true;

        return false;
    }

    private static bool IsNonDialogueAssMetadata(string style, string actor, string effect)
    {
        if (!string.IsNullOrWhiteSpace(effect)) return true;
        foreach (var token in NonDialogueAssStyles)
        {
            if (style.Contains(token, StringComparison.OrdinalIgnoreCase)
                || actor.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsCueNumberLine(string line)
    {
        var span = line.AsSpan().Trim();
        if (span.IsEmpty) return false;
        foreach (var ch in span)
            if (!char.IsAsciiDigit(ch)) return false;
        return true;
    }

    private static bool TryParseTimingLine(string line, out double startSeconds, out double endSeconds)
    {
        startSeconds = 0;
        endSeconds = 0;
        var arrowIndex = line.IndexOf("-->", StringComparison.Ordinal);
        if (arrowIndex <= 0) return false;

        var left = line.AsSpan(0, arrowIndex).Trim();
        var rightSpan = line.AsSpan(arrowIndex + 3).Trim();
        var spaceIndex = rightSpan.IndexOfAny(' ', '\t');
        var right = spaceIndex > 0 ? rightSpan[..spaceIndex] : rightSpan;

        return TryParseTimestamp(left, out startSeconds)
            && TryParseTimestamp(right, out endSeconds)
            && endSeconds > startSeconds;
    }

    public static bool TryParseTimestamp(ReadOnlySpan<char> text, out double seconds)
    {
        seconds = 0;
        text = text.Trim();
        if (text.IsEmpty) return false;

        Span<Range> parts = stackalloc Range[4];
        var count = text.Split(parts, ':');
        if (count is not (2 or 3)) return false;

        var hours = 0;
        ReadOnlySpan<char> minutesSpan;
        ReadOnlySpan<char> secondsSpan;
        if (count == 3)
        {
            if (!int.TryParse(text[parts[0]], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours) || hours < 0)
                return false;
            minutesSpan = text[parts[1]];
            secondsSpan = text[parts[2]];
        }
        else
        {
            minutesSpan = text[parts[0]];
            secondsSpan = text[parts[1]];
        }

        if (!int.TryParse(minutesSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            || minutes is < 0 or >= 60)
            return false;

        var separatorIndex = secondsSpan.IndexOfAny(',', '.');
        int wholeSeconds;
        var fractionalSeconds = 0.0;
        if (separatorIndex >= 0)
        {
            var wholePart = secondsSpan[..separatorIndex];
            var fracPart = secondsSpan[(separatorIndex + 1)..];
            if (!int.TryParse(wholePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out wholeSeconds)
                || wholeSeconds is < 0 or >= 60)
                return false;
            if (fracPart.Length == 0 || fracPart.Length > 4) return false;
            if (!int.TryParse(fracPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fracValue) || fracValue < 0)
                return false;
            fractionalSeconds = fracValue / Math.Pow(10, fracPart.Length);
        }
        else
        {
            if (!int.TryParse(secondsSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out wholeSeconds)
                || wholeSeconds is < 0 or >= 60)
                return false;
        }

        seconds = hours * 3600.0 + minutes * 60.0 + wholeSeconds + fractionalSeconds;
        return double.IsFinite(seconds) && seconds >= 0;
    }

    private static string NormalizeLineEndings(string text) =>
        text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string CleanOutputText(string text)
    {
        var lines = text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);
        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex AssOverridePattern();

    [GeneratedRegex(@"\{\\p[1-9]\d*\}")]
    private static partial Regex AssDrawingPattern();

    [GeneratedRegex(@"</?(?:c(?:\.[^>]*)?|v(?:\s[^>]*)?|lang(?:\s[^>]*)?|ruby|rt|\d{2}:\d{2}(?::\d{2})?[.,]\d{3})>", RegexOptions.IgnoreCase)]
    private static partial Regex VttInlineTagPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagPattern();

    [GeneratedRegex(@"\[[^\]]*\]|\([^\)]*\)")]
    private static partial Regex SdhBracketPattern();

    [GeneratedRegex(@"(?m)^(?:\s*[-–—]?\s*[A-ZÇĞİÖŞÜ0-9][A-Za-zÇĞİÖŞÜçğıöşü0-9\s]{0,18}:\s*)")]
    private static partial Regex SpeakerLabelPattern();

    [GeneratedRegex(@"(?:https?://|www\.)\S+|@[A-Za-z0-9_]{3,}", RegexOptions.IgnoreCase)]
    private static partial Regex SocialHandleOrUrlPattern();

    [GeneratedRegex(@"^\{(\d+)\}\{(\d+)\}(.+)$")]
    private static partial Regex MicroDvdPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
