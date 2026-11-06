using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace VideoPlayer.Core;

public sealed record WordTranslation(string Text, IReadOnlyList<string> Alternatives, string SourceLanguage);

/// <summary>Translates single words (with dictionary alternatives) and sentences through the public Google Translate web endpoint.</summary>
public sealed class WordTranslator
{
    public static IReadOnlyList<(string Code, string Name)> Languages { get; } =
    [
        ("tr", "Türkçe"), ("en", "English"), ("de", "Deutsch"), ("fr", "Français"), ("es", "Español"),
        ("it", "Italiano"), ("pt", "Português"), ("ru", "Русский"), ("uk", "Українська"), ("pl", "Polski"),
        ("nl", "Nederlands"), ("ar", "العربية"), ("fa", "فارسی"), ("ja", "日本語"), ("ko", "한국어"),
        ("zh-CN", "中文 (简体)"), ("hi", "हिन्दी"), ("sv", "Svenska"), ("el", "Ελληνικά"), ("az", "Azərbaycanca")
    ];

    private const string Endpoint = "https://translate.googleapis.com/translate_a/single";
    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, WordTranslation> _cache = new(StringComparer.Ordinal);

    public WordTranslator(HttpClient client) => _client = client;

    public async Task<WordTranslation> TranslateWordAsync(string word, string targetLanguage, CancellationToken token)
    {
        var key = $"w|{targetLanguage}|{word}";
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var result = await RequestAsync(word, targetLanguage, includeDictionary: true, token);
        _cache[key] = result;
        return result;
    }

    public async Task<string> TranslateSentenceAsync(string sentence, string targetLanguage, CancellationToken token)
    {
        var key = $"s|{targetLanguage}|{sentence}";
        if (_cache.TryGetValue(key, out var cached)) return cached.Text;
        var result = await RequestAsync(sentence, targetLanguage, includeDictionary: false, token);
        _cache[key] = result;
        return result.Text;
    }

    private async Task<WordTranslation> RequestAsync(string text, string target, bool includeDictionary, CancellationToken token)
    {
        var url = $"{Endpoint}?client=gtx&sl=auto&tl={Uri.EscapeDataString(target)}&dt=t"
            + (includeDictionary ? "&dt=bd" : "") + $"&q={Uri.EscapeDataString(text)}";
        using var response = await _client.GetAsync(url, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        return Parse(document.RootElement, text);
    }

    public static WordTranslation Parse(JsonElement root, string original)
    {
        var translated = new System.Text.StringBuilder();
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in root[0].EnumerateArray())
                if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0
                    && segment[0].ValueKind == JsonValueKind.String)
                    translated.Append(segment[0].GetString());
        }

        var alternatives = new List<string>();
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 1 && root[1].ValueKind == JsonValueKind.Array)
        {
            foreach (var group in root[1].EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Array || group.GetArrayLength() < 2
                    || group[1].ValueKind != JsonValueKind.Array) continue;
                var partOfSpeech = group[0].ValueKind == JsonValueKind.String ? group[0].GetString() : "";
                var terms = group[1].EnumerateArray().Where(term => term.ValueKind == JsonValueKind.String)
                    .Select(term => term.GetString()!).Take(4);
                var line = string.Join(", ", terms);
                if (line.Length > 0) alternatives.Add(string.IsNullOrEmpty(partOfSpeech) ? line : $"{partOfSpeech}: {line}");
                if (alternatives.Count >= 4) break;
            }
        }

        var source = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 2
            && root[2].ValueKind == JsonValueKind.String ? root[2].GetString() ?? "" : "";
        var text = translated.ToString().Trim();
        if (text.Length == 0) text = original;
        return new WordTranslation(text, alternatives, source);
    }

    public static string LanguageName(string code) =>
        Languages.FirstOrDefault(language => string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase)).Name
        ?? code.ToUpper(CultureInfo.InvariantCulture);
}
