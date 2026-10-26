using System.Text;
using System.Text.RegularExpressions;

namespace VideoPlayer.Core;

public static partial class SubtitleTextEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static SubtitleTextEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string ResolveSynchronizationEncoding(byte[] source, string requestedEncoding) =>
        TryReadUtf8(source, out _) ? "utf-8" : requestedEncoding;

    public static string DecodeSubtitleText(byte[] source, string requestedEncoding, out string resolvedEncoding)
    {
        if (source.Length >= 3 && source[0] == 0xEF && source[1] == 0xBB && source[2] == 0xBF)
        {
            resolvedEncoding = "utf-8";
            return Encoding.UTF8.GetString(source, 3, source.Length - 3);
        }

        if (source.Length >= 2 && source[0] == 0xFF && source[1] == 0xFE)
        {
            resolvedEncoding = "utf-16le";
            return Encoding.Unicode.GetString(source, 2, source.Length - 2);
        }

        if (source.Length >= 2 && source[0] == 0xFE && source[1] == 0xFF)
        {
            resolvedEncoding = "utf-16be";
            return Encoding.BigEndianUnicode.GetString(source, 2, source.Length - 2);
        }

        if (TryReadUtf8(source, out var utf8Text) && utf8Text is not null)
        {
            resolvedEncoding = "utf-8";
            return utf8Text;
        }

        string normalized = string.IsNullOrWhiteSpace(requestedEncoding)
            ? SubtitleCodepageRules.Auto
            : requestedEncoding.Trim().ToLowerInvariant();

        if (normalized is not "infer" and not SubtitleCodepageRules.Auto)
        {
            string encodingName = normalized.StartsWith("cp", StringComparison.Ordinal)
                ? "windows-" + normalized[2..]
                : normalized;

            try
            {
                var explicitEncoding = Encoding.GetEncoding(encodingName);
                resolvedEncoding = encodingName;
                return explicitEncoding.GetString(source);
            }
            catch
            {
                // Fall through to heuristic detection
            }
        }

        if (LooksLikeTurkishWindows1254(source))
        {
            resolvedEncoding = "windows-1254";
            return Encoding.GetEncoding(1254).GetString(source);
        }

        resolvedEncoding = "windows-1252";
        return Encoding.GetEncoding(1252).GetString(source);
    }

    private static bool LooksLikeTurkishWindows1254(byte[] source)
    {
        // Turkish-specific bytes in Windows-1254:
        // 0xFD (ı), 0xFE (ş), 0xF0 (ğ), 0xDD (İ), 0xDE (Ş), 0xD0 (Ğ), plus shared 0xE7/0xC7 (ç/Ç), 0xF6/0xD6 (ö/Ö), 0xFC/0xDC (ü/Ü)
        int turkishSpecificCount = 0;
        int sharedTurkishVowelCount = 0;

        foreach (byte b in source)
        {
            if (b is 0xFD or 0xFE or 0xF0 or 0xDD or 0xDE or 0xD0)
                turkishSpecificCount++;
            else if (b is 0xE7 or 0xC7 or 0xF6 or 0xD6 or 0xFC or 0xDC)
                sharedTurkishVowelCount++;
        }

        return turkishSpecificCount >= 2 || (turkishSpecificCount >= 1 && sharedTurkishVowelCount >= 2);
    }

    public static bool TryRepairSynchronizedText(
        byte[] source, string synchronizedText, string codepage, out string repairedText)
    {
        repairedText = synchronizedText;
        codepage = SubtitleCodepageRules.Normalize(codepage);
        if (codepage == SubtitleCodepageRules.Auto) return false;
        var encodingName = codepage.StartsWith("cp", StringComparison.Ordinal)
            ? "windows-" + codepage[2..] : codepage;

        var sourceEncoding = TryReadUtf8(source, out var sourceText)
            ? StrictUtf8
            : Encoding.GetEncoding(encodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        sourceText ??= sourceEncoding.GetString(source);
        var expectedDialogue = DialogueText(sourceText);
        var currentDialogue = DialogueText(synchronizedText);
        if (expectedDialogue.Length == 0 || currentDialogue == expectedDialogue
            || expectedDialogue.Length != currentDialogue.Length) return false;

        // A mistaken single-byte decoder preserves ASCII and maps each byte consistently.
        // Require that relationship across the complete original dialogue before repairing.
        var characters = new Dictionary<char, char>();
        for (var index = 0; index < expectedDialogue.Length; index++)
        {
            var expected = expectedDialogue[index];
            var current = currentDialogue[index];
            if (expected != current && (expected <= '\u007F' || current <= '\u007F'
                || expected == '\uFFFD' || current == '\uFFFD'
                || char.IsSurrogate(expected) || char.IsSurrogate(current))) return false;
            if (characters.TryGetValue(current, out var mapped) && mapped != expected) return false;
            characters[current] = expected;
        }

        var repaired = synchronizedText.ToCharArray();
        for (var index = 0; index < repaired.Length; index++)
            if (characters.TryGetValue(repaired[index], out var replacement)) repaired[index] = replacement;
        repairedText = new string(repaired);
        return true;
    }

    private static bool TryReadUtf8(byte[] source, out string? text)
    {
        try
        {
            text = StrictUtf8.GetString(source);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = null;
            return false;
        }
    }

    private static string DialogueText(string text) =>
        WhitespacePattern().Replace(CueStructurePattern().Replace(text.TrimStart('\uFEFF'), ""), " ").Trim();

    [GeneratedRegex(@"(?m)^\s*\d+\s*$|^\s*\d{2,}:\d{2}:\d{2}[,.]\d{3}\s*-->[^\r\n]*")]
    private static partial Regex CueStructurePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
