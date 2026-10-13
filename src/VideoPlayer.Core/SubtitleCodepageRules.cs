namespace VideoPlayer.Core;

public sealed record SubtitleCodepageOption(string Codepage, string Label);

public static class SubtitleCodepageRules
{
    public const string Auto = "auto";

    public static IReadOnlyList<SubtitleCodepageOption> Options { get; } = Array.AsReadOnly(
    new SubtitleCodepageOption[]
    {
        new(Auto, UiText.DetectAutomatically),
        new("utf-8", "Unicode (UTF-8)"),
        new("cp1254", UiText.TurkishWindows1254),
        new("cp1250", UiText.CentralEuropeanWindows1250),
        new("cp1251", UiText.CyrillicWindows1251),
        new("cp1252", UiText.WesternEuropeanWindows1252)
    });

    public static string Normalize(string? codepage)
    {
        if (string.IsNullOrWhiteSpace(codepage)) return Auto;
        return Options.FirstOrDefault(option => string.Equals(
            option.Codepage, codepage.Trim(), StringComparison.OrdinalIgnoreCase))?.Codepage ?? Auto;
    }
}
