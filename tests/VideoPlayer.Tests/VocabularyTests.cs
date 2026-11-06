using System.Text.Json;
using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class VocabularyTests
{
    [Fact]
    public void TokenizerSeparatesPunctuationAndLines()
    {
        var lines = SubtitleWordTokenizer.Tokenize("- \"Hello, world!\"\n<i>It’s Tom’s</i> ♪");
        Assert.Equal(2, lines.Count);
        Assert.Null(lines[0][0].Word);
        Assert.Equal("\"", lines[0][1].Prefix);
        Assert.Equal("Hello", lines[0][1].Word);
        Assert.Equal(",", lines[0][1].Suffix);
        Assert.Equal("world", lines[0][2].Word);
        Assert.Equal("tom", lines[1][1].Key);
        Assert.Null(lines[1][2].Word);
    }

    [Fact]
    public void StorePersistsWordsAndSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vocab-{Guid.NewGuid():N}.json");
        try
        {
            var store = new VocabularyStore(path);
            store.Add(new VocabularyEntry { Word = "bank", Display = "Bank", Translation = "banka" });
            store.UpdateSettings(store.Settings with { Enabled = true, TargetLanguage = "de" });

            var reloaded = new VocabularyStore(path);
            Assert.True(reloaded.Contains("bank"));
            Assert.Equal("banka", reloaded.Get("bank")!.Translation);
            Assert.True(reloaded.Settings.Enabled);
            Assert.Equal("de", reloaded.Settings.TargetLanguage);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TranslatorParsesTranslationAndDictionary()
    {
        using var document = JsonDocument.Parse(
            """[[["banka","bank",null,null,10]],[["noun",["banka","kıyı"],[["banka",["bank"],null,0.5]],"bank",1]],"en"]""");
        var result = WordTranslator.Parse(document.RootElement, "bank");
        Assert.Equal("banka", result.Text);
        Assert.Equal("en", result.SourceLanguage);
        Assert.Equal("noun: banka, kıyı", Assert.Single(result.Alternatives));
    }
}
