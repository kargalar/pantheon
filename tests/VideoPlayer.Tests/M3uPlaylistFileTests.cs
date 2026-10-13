using System.Text;
using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class M3uPlaylistFileTests
{
    [Fact]
    public async Task LoadAsync_ResolvesRelativePathsSkipsMetadataAndPreservesDuplicates()
    {
        using var directory = new TemporaryDirectory();
        var mediaDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "Media"));
        var firstVideo = Path.Combine(mediaDirectory.FullName, "Birinci video.mkv");
        var secondVideo = Path.Combine(mediaDirectory.FullName, "İkinci-video.mp4");
        await File.WriteAllBytesAsync(firstVideo, []);
        await File.WriteAllBytesAsync(secondVideo, []);
        var playlistPath = Path.Combine(directory.Path, "watch.m3u8");
        await File.WriteAllTextAsync(playlistPath,
            "#EXTM3U\r\n#EXTINF:120,First title\r\nMedia/Birinci video.mkv\r\n"
            + "Media/İkinci-video.mp4\nMedia/Birinci video.mkv\nmissing.mkv\nnotes.txt\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var entries = await M3uPlaylistFile.LoadAsync(playlistPath);

        Assert.Equal([firstVideo, secondVideo, firstVideo], entries);
    }

    [Fact]
    public async Task SaveAsync_WritesUtf8RelativeEntriesThatLoadBackInOrder()
    {
        using var directory = new TemporaryDirectory();
        var mediaDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "Media"));
        var playlistDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "Lists"));
        var firstVideo = Path.Combine(mediaDirectory.FullName, "Film ğ.mkv");
        var secondVideo = Path.Combine(mediaDirectory.FullName, "Film two.mp4");
        var hashVideo = Path.Combine(playlistDirectory.FullName, "#special.mkv");
        await File.WriteAllBytesAsync(firstVideo, []);
        await File.WriteAllBytesAsync(secondVideo, []);
        await File.WriteAllBytesAsync(hashVideo, []);
        var playlistPath = Path.Combine(playlistDirectory.FullName, "saved.m3u");

        await M3uPlaylistFile.SaveAsync(playlistPath, [firstVideo, secondVideo, firstVideo, hashVideo]);
        var contents = await File.ReadAllTextAsync(playlistPath, Encoding.UTF8);
        var entries = await M3uPlaylistFile.LoadAsync(playlistPath);

        Assert.StartsWith("#EXTM3U", contents, StringComparison.Ordinal);
        Assert.Contains($"..{Path.DirectorySeparatorChar}Media{Path.DirectorySeparatorChar}Film ğ.mkv"
            .Replace(Path.DirectorySeparatorChar, '/'), contents, StringComparison.Ordinal);
        Assert.Contains("./#special.mkv", contents, StringComparison.Ordinal);
        Assert.Equal([firstVideo, secondVideo, firstVideo, hashVideo], entries);
        Assert.Empty(Directory.EnumerateFiles(playlistDirectory.FullName, "saved.m3u.*.tmp"));
    }

    [Fact]
    public async Task LoadAsync_ResolvesLocalFileUris()
    {
        using var directory = new TemporaryDirectory();
        var videoPath = Path.Combine(directory.Path, "clip.mkv");
        var playlistPath = Path.Combine(directory.Path, "uris.m3u");
        await File.WriteAllBytesAsync(videoPath, []);
        await File.WriteAllTextAsync(playlistPath, $"#EXTM3U\n{new Uri(videoPath).AbsoluteUri}\n", Encoding.UTF8);

        var entries = await M3uPlaylistFile.LoadAsync(playlistPath);

        Assert.Equal([videoPath], entries);
    }

    [Fact]
    public async Task LoadAsync_RejectsPlaylistsAboveTheEntryLimit()
    {
        using var directory = new TemporaryDirectory();
        var videoPath = Path.Combine(directory.Path, "clip.mkv");
        var playlistPath = Path.Combine(directory.Path, "oversized.m3u8");
        await File.WriteAllBytesAsync(videoPath, []);
        await using (var stream = File.Create(playlistPath))
        await using (var writer = new StreamWriter(stream, Encoding.UTF8))
        {
            await writer.WriteLineAsync("#EXTM3U");
            for (var index = 0; index <= M3uPlaylistFile.MaximumEntryCount; index++)
                await writer.WriteLineAsync("clip.mkv");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => M3uPlaylistFile.LoadAsync(playlistPath));
    }

    [Fact]
    public async Task LoadAsync_StopsReadingWhenCancelled()
    {
        using var directory = new TemporaryDirectory();
        var playlistPath = Path.Combine(directory.Path, "cancelled.m3u8");
        await File.WriteAllTextAsync(playlistPath, "#EXTM3U\n", Encoding.UTF8);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => M3uPlaylistFile.LoadAsync(playlistPath, cancellation.Token));
    }

    [Theory]
    [InlineData("mix.M3U")]
    [InlineData("mix.m3u8")]
    [InlineData("mix.txt")]
    public void IsPlaylistPath_RecognizesM3uExtensionsCaseInsensitively(string path)
    {
        var expected = !path.EndsWith(".txt", StringComparison.Ordinal);

        Assert.Equal(expected, M3uPlaylistFile.IsPlaylistPath(path));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"m3u-playlist-tests-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
