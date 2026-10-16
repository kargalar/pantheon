using System.Text;
using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class SubtitleTextEncodingTests
{
    private const string Dialogue = "Hayır değil, küçük sapık seni. İı Şş Ğğ Üü Öö Çç";
    private const string SourceCue = "1\n00:00:01,000 --> 00:00:03,000\n" + Dialogue + "\n";

    static SubtitleTextEncodingTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [Theory]
    [InlineData(1252)]
    [InlineData(28591)]
    public void Repair_RestoresDialogueAndPreservesSynchronizedTimings(int mistakenCodepage)
    {
        var source = Encoding.GetEncoding(1254).GetBytes(SourceCue);
        var corrupted = Encoding.GetEncoding(mistakenCodepage).GetString(source)
            .Replace("00:00:01,000 --> 00:00:03,000", "00:00:02,500 --> 00:00:04,500");

        Assert.True(SubtitleTextEncoding.TryRepairSynchronizedText(source, corrupted, "cp1254", out var repaired));
        Assert.Contains(Dialogue, repaired);
        Assert.Contains("00:00:02,500 --> 00:00:04,500", repaired);
        Assert.False(SubtitleTextEncoding.TryRepairSynchronizedText(source, repaired, "cp1254", out _));
    }

    [Fact]
    public void Repair_LeavesUnrelatedOrEditedDialogueUntouched()
    {
        var source = Encoding.GetEncoding(1254).GetBytes(SourceCue);
        var edited = SourceCue.Replace(Dialogue, "Different subtitle release. ý ð þ");

        Assert.False(SubtitleTextEncoding.TryRepairSynchronizedText(source, edited, "cp1254", out var result));
        Assert.Equal(edited, result);
    }

    [Fact]
    public void Repair_HandlesNordicDecoderMistakenForTurkish()
    {
        var source = Encoding.GetEncoding(1254).GetBytes(SourceCue);
        var corrupted = Encoding.GetEncoding(1252).GetString(source).Replace('ç', 'į').Replace('Ç', 'Į');

        Assert.True(SubtitleTextEncoding.TryRepairSynchronizedText(source, corrupted, "cp1254", out var repaired));
        Assert.Equal(SourceCue, repaired);
    }

    [Fact]
    public void Repair_RejectsAsciiEditsEvenWhenTextLengthMatches()
    {
        var source = Encoding.GetEncoding(1254).GetBytes(SourceCue);
        var corrupted = Encoding.GetEncoding(1252).GetString(source).Replace("seni", "bunu");

        Assert.False(SubtitleTextEncoding.TryRepairSynchronizedText(source, corrupted, "cp1254", out _));
    }

    [Fact]
    public void Synchronization_PreservesUtf8EvenWhenLegacyCodepageIsSelected()
    {
        Assert.Equal("utf-8", SubtitleTextEncoding.ResolveSynchronizationEncoding(
            Encoding.UTF8.GetBytes(SourceCue), "windows-1254"));
        Assert.Equal("windows-1254", SubtitleTextEncoding.ResolveSynchronizationEncoding(
            Encoding.GetEncoding(1254).GetBytes(SourceCue), "windows-1254"));
    }

    [Fact]
    public async Task CacheRepair_PreservesOriginalFileAndSynchronizationMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pantheon-encoding-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var sourcePath = Path.Combine(directory, "original.srt");
            var synchronizedPath = Path.Combine(directory, "aligned.synced.srt");
            var originalBytes = Encoding.GetEncoding(1254).GetBytes(SourceCue);
            await File.WriteAllBytesAsync(sourcePath, originalBytes);
            await File.WriteAllTextAsync(synchronizedPath, Encoding.GetEncoding(1252).GetString(originalBytes));
            await SubtitleFileCache.SaveSynchronizationSourceAsync(synchronizedPath, sourcePath, default);
            var cache = new SubtitleFileCache(directory);

            Assert.True(cache.RepairSynchronizedEncoding(synchronizedPath, "cp1254"));
            Assert.Contains(Dialogue, await File.ReadAllTextAsync(synchronizedPath));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sourcePath));
            Assert.Equal(sourcePath, cache.FindSynchronizationSource(synchronizedPath));
            Assert.False(cache.RepairSynchronizedEncoding(synchronizedPath, "cp1254"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
