using System.Text;

namespace VideoPlayer.Core;

public static class M3uPlaylistFile
{
    public const int MaximumEntryCount = 100_000;
    private const string PlaylistHeader = "#EXTM3U";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    public static IReadOnlyList<string> FilePickerPatterns { get; } = Array.AsReadOnly<string>(["*.m3u", "*.m3u8"]);

    public static bool IsPlaylistPath(string path) =>
        string.Equals(Path.GetExtension(path), ".m3u", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetExtension(path), ".m3u8", StringComparison.OrdinalIgnoreCase);

    public static async Task<IReadOnlyList<string>> LoadAsync(string playlistPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistPath);
        var fullPlaylistPath = Path.GetFullPath(playlistPath);
        var playlistDirectory = Path.GetDirectoryName(fullPlaylistPath)
            ?? throw new ArgumentException("Playlist path must include a directory.", nameof(playlistPath));
        var entries = new List<string>();

        await using var stream = new FileStream(
            fullPlaylistPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveEntry(line, playlistDirectory, out var videoPath)) continue;
            if (entries.Count == MaximumEntryCount)
                throw new InvalidDataException($"A playlist can contain at most {MaximumEntryCount:N0} videos.");
            entries.Add(videoPath);
        }

        return entries;
    }

    public static async Task SaveAsync(
        string playlistPath,
        IEnumerable<string> videoPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistPath);
        ArgumentNullException.ThrowIfNull(videoPaths);

        var fullPlaylistPath = Path.GetFullPath(playlistPath);
        var directory = Path.GetDirectoryName(fullPlaylistPath)
            ?? throw new ArgumentException("Playlist path must include a directory.", nameof(playlistPath));
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{fullPlaylistPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var writer = new StreamWriter(stream, StrictUtf8))
            {
                await writer.WriteLineAsync(PlaylistHeader.AsMemory(), cancellationToken).ConfigureAwait(false);
                var entryCount = 0;
                foreach (var videoPath in videoPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryGetPathForPlaylist(videoPath, directory, out var playlistEntry)) continue;
                    if (entryCount == MaximumEntryCount)
                        throw new InvalidDataException($"A playlist can contain at most {MaximumEntryCount:N0} videos.");
                    entryCount++;
                    await writer.WriteLineAsync(playlistEntry.AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPlaylistPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool TryResolveEntry(string line, string playlistDirectory, out string videoPath)
    {
        videoPath = string.Empty;
        var entry = line.Trim();
        if (entry.Length == 0 || entry[0] == '#') return false;

        try
        {
            string candidatePath;
            if (Path.IsPathFullyQualified(entry)) candidatePath = entry;
            else if (Uri.TryCreate(entry, UriKind.Absolute, out var uri))
            {
                if (!uri.IsFile) return false;
                candidatePath = uri.LocalPath;
            }
            else candidatePath = Path.GetFullPath(entry, playlistDirectory);

            var fullPath = Path.GetFullPath(candidatePath);
            if (!MediaPathRules.IsSupportedVideo(fullPath) || !File.Exists(fullPath)) return false;
            videoPath = fullPath;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool TryGetPathForPlaylist(string videoPath, string playlistDirectory, out string playlistEntry)
    {
        playlistEntry = string.Empty;
        if (string.IsNullOrWhiteSpace(videoPath) || videoPath.Contains('\r') || videoPath.Contains('\n')) return false;

        try
        {
            var fullPath = Path.GetFullPath(videoPath);
            if (!MediaPathRules.IsSupportedVideo(fullPath)) return false;
            playlistEntry = Path.GetRelativePath(playlistDirectory, fullPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
                playlistEntry = playlistEntry.Replace(Path.AltDirectorySeparatorChar, '/');
            if (playlistEntry.StartsWith('#') || !string.Equals(playlistEntry, playlistEntry.Trim(), StringComparison.Ordinal))
                playlistEntry = $"./{playlistEntry}";
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
