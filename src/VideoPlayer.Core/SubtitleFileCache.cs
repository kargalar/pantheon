using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoPlayer.Core;

public sealed partial class SubtitleFileCache(string directory)
{
    private const int HashBlockBytes = 64 * 1024;
    private const int MaximumSourceMetadataBytes = 8192;

    public string DownloadPath(string videoPath, string language, int? fileId = null) => Path.Combine(directory,
        CreateKey(fileId.HasValue
            ? $"download-v1|{MediaStamp(videoPath)}|{language}|{fileId.Value}"
            : $"download-v1|{MediaStamp(videoPath)}|{language}") + ".srt");

    public bool IsDownloadedSubtitleForVideo(string? subtitlePath, string videoPath, string language)
    {
        if (string.IsNullOrWhiteSpace(subtitlePath)) return false;
        var downloadedPath = DownloadPath(videoPath, language);
        if (!IsValidSrt(downloadedPath)) return false;
        var sourcePath = FindSynchronizationSource(subtitlePath) ?? subtitlePath;
        return string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(downloadedPath), StringComparison.OrdinalIgnoreCase);
    }

    public string? FindSynchronizationSource(string path)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)) return null;
        var metadataPath = path + ".source.json";
        if (!File.Exists(metadataPath) || new FileInfo(metadataPath).Length > MaximumSourceMetadataBytes) return null;
        try
        {
            var source = JsonSerializer.Deserialize<string>(File.ReadAllText(metadataPath));
            return !string.IsNullOrWhiteSpace(source) && File.Exists(source) ? source : null;
        }
        catch (JsonException) { return null; }
    }

    public static Task SaveSynchronizationSourceAsync(string outputPath, string sourcePath, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(outputPath + ".source.json", JsonSerializer.Serialize(sourcePath), cancellationToken);

    public bool RepairSynchronizedEncoding(string path, string codepage)
    {
        var source = FindSynchronizationSource(path);
        if (source is null || !string.Equals(Path.GetExtension(source), ".srt", StringComparison.OrdinalIgnoreCase)
            || !IsValidSrt(path) || new FileInfo(source).Length > SubtitleAutomationOptions.MaximumSubtitleBytes)
            return false;
        if (!SubtitleTextEncoding.TryRepairSynchronizedText(
            File.ReadAllBytes(source), File.ReadAllText(path), codepage, out var repairedText)) return false;

        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, repairedText, new UTF8Encoding(false));
            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public async Task<string> SynchronizationPathAsync(
        string videoPath, string subtitlePath, string referenceStream, string encoding, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(subtitlePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var contentHash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
        return Path.Combine(directory, CreateKey(
            $"sync-v2|{MediaStamp(videoPath)}|{contentHash}|{referenceStream}|{encoding}") + ".synced.srt");
    }

    public static string MediaStamp(string path)
    {
        var file = new FileInfo(path);
        return $"{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
    }

    public static async Task<string?> ComputeMovieHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < HashBlockBytes * 2) return null;
        var buffer = new byte[HashBlockBytes];
        var hash = (ulong)stream.Length;
        foreach (var offset in new[] { 0L, stream.Length - HashBlockBytes })
        {
            stream.Position = offset;
            await stream.ReadExactlyAsync(buffer, cancellationToken);
            for (var index = 0; index < buffer.Length; index += sizeof(ulong))
                hash = unchecked(hash + BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(index, sizeof(ulong))));
        }
        return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static bool IsValidSrt(string path)
    {
        if (!File.Exists(path)) return false;
        var length = new FileInfo(path).Length;
        return length is > 0 and <= SubtitleAutomationOptions.MaximumSubtitleBytes
            && IsValidSrtContent(File.ReadAllText(path));
    }

    public static bool IsValidSrtContent(string content) => SrtCuePattern().IsMatch(content);

    public static async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length is 0 or > SubtitleAutomationOptions.MaximumSubtitleBytes
            || !IsValidSrtContent(Encoding.UTF8.GetString(bytes)))
            throw new InvalidDataException(UiText.TheProviderDidNotReturnAValidSRT);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string CreateKey(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [GeneratedRegex(@"(?m)^\s*\d{2,}:\d{2}:\d{2}[,.]\d{3}\s*-->\s*\d{2,}:\d{2}:\d{2}[,.]\d{3}[^\r\n]*\r?\n\S")]
    private static partial Regex SrtCuePattern();
}
