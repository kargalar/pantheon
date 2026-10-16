using System.Text;
using System.Text.RegularExpressions;

namespace VideoPlayer.Core;

public static partial class SubtitleTextEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static SubtitleTextEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string ResolveSynchronizationEncoding(byte[] source, string requestedEncoding) =>
        TryReadUtf8(source, out _) ? "utf-8" : requestedEncoding;

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
