namespace VideoPlayer.Core;

public static class MediaPathRules
{
    private static readonly string[] VideoExtensionValues =
    [
        ".mkv", ".mp4", ".mov", ".avi", ".webm", ".m4v", ".ts", ".m2ts", ".mts", ".m2t",
        ".vob", ".m2v", ".wmv", ".mpg", ".mpeg", ".mpe", ".ogv", ".flv", ".3gp", ".3g2"
    ];

    private static readonly HashSet<string> VideoExtensions = new(VideoExtensionValues, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx"
    };

    private static readonly string[] SubtitleExtensionPriority = [".srt", ".ass", ".vtt", ".ssa", ".sub", ".idx"];
    private const double MinimumSubtitleNameSimilarity = 0.85;

    public static IReadOnlyList<string> VideoFilePickerPatterns { get; } = Array.AsReadOnly(
        VideoExtensionValues.Select(extension => $"*{extension}").ToArray());

    public static string? FindMatchingSubtitleBesideVideo(string videoPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        var directory = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrEmpty(directory)) return null;

        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        foreach (var extension in SubtitleExtensionPriority)
        {
            var candidate = Path.Combine(directory, baseName + extension);
            if (File.Exists(candidate)) return candidate;
        }

        return FindMatchingSubtitle(videoPath, Directory.EnumerateFiles(directory));
    }

    public static bool IsSupportedVideo(string? path) => HasSupportedExtension(path, VideoExtensions);

    public static bool IsSupportedSubtitle(string? path) => HasSupportedExtension(path, SubtitleExtensions);

    public static IReadOnlyList<string> FindAllSubtitlesBesideVideo(string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath)) return Array.Empty<string>();
        var directory = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return Array.Empty<string>();

        try
        {
            return Directory.EnumerateFiles(directory)
                .Where(IsSupportedSubtitle)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    public static IReadOnlyList<string> ResolveExistingVideoPaths(IEnumerable<string?> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var resolvedPaths = new List<string>();
        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!IsSupportedVideo(path)) continue;
            try
            {
                var fullPath = Path.GetFullPath(path!);
                if (File.Exists(fullPath) && knownPaths.Add(fullPath))
                    resolvedPaths.Add(fullPath);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                // Ignore invalid command-line or pipe paths and continue with the valid files.
            }
        }

        return resolvedPaths;
    }

    public static IReadOnlyList<string> OrderVideosByFileName(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths
            .Where(IsSupportedVideo)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string? FindMatchingSubtitle(string videoPath, IEnumerable<string> candidatePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentNullException.ThrowIfNull(candidatePaths);

        var videoName = Path.GetFileNameWithoutExtension(videoPath);
        var identity = SubtitleMediaIdentity.FromPath(videoPath);
        var subtitles = candidatePaths
            .Where(path => IsSupportedSubtitle(path)
                && !string.IsNullOrWhiteSpace(path)
                && IsCompatibleSubtitle(identity, SubtitleMediaIdentity.FromPath(path)))
            .ToArray();

        var exactMatch = subtitles
            .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), videoName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Array.IndexOf(SubtitleExtensionPriority, Path.GetExtension(path)))
            .FirstOrDefault();
        if (exactMatch is not null) return exactMatch;

        var normalizedVideoName = NormalizeMediaName(videoName);
        return subtitles
            .Select(path => new
            {
                Path = path,
                Similarity = CalculateNameSimilarity(
                    normalizedVideoName,
                    NormalizeMediaName(Path.GetFileNameWithoutExtension(path)))
            })
            .Where(candidate => candidate.Similarity >= MinimumSubtitleNameSimilarity)
            .OrderByDescending(candidate => candidate.Similarity)
            .ThenBy(candidate => Array.IndexOf(SubtitleExtensionPriority, Path.GetExtension(candidate.Path)))
            .ThenBy(candidate => Path.GetFileName(candidate.Path), StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    public static string CreateCapturePath(string videoPath, string captureDirectory, DateTimeOffset timestamp,
        string? saveMode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(captureDirectory);

        var videoName = Path.GetFileNameWithoutExtension(videoPath);
        var extension = ScreenshotSaveModeRules.GetExtension(saveMode);
        var fileName = $"{videoName}-{timestamp:yyyyMMdd-HHmmss-fff}{extension}";
        return Path.Combine(captureDirectory, videoName, fileName);
    }

    private static bool HasSupportedExtension(string? path, HashSet<string> extensions) =>
        !string.IsNullOrWhiteSpace(path) && extensions.Contains(Path.GetExtension(path));

    private static bool IsCompatibleSubtitle(SubtitleMediaIdentity video, SubtitleMediaIdentity subtitle) =>
        (!video.IsEpisode || video.MatchesEpisode(subtitle))
        && (!video.Year.HasValue || !subtitle.Year.HasValue || video.Year == subtitle.Year);

    private static string NormalizeMediaName(string fileName) =>
        new(fileName.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static double CalculateNameSimilarity(string first, string second)
    {
        var longestLength = Math.Max(first.Length, second.Length);
        if (longestLength == 0) return 1;
        if (first.Length == 0 || second.Length == 0) return 0;

        var previousRow = Enumerable.Range(0, second.Length + 1).ToArray();
        var currentRow = new int[second.Length + 1];
        for (var firstIndex = 1; firstIndex <= first.Length; firstIndex++)
        {
            currentRow[0] = firstIndex;
            for (var secondIndex = 1; secondIndex <= second.Length; secondIndex++)
            {
                var substitutionCost = first[firstIndex - 1] == second[secondIndex - 1] ? 0 : 1;
                currentRow[secondIndex] = Math.Min(
                    Math.Min(currentRow[secondIndex - 1] + 1, previousRow[secondIndex] + 1),
                    previousRow[secondIndex - 1] + substitutionCost);
            }

            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return 1 - (double)previousRow[second.Length] / longestLength;
    }
}

public static class MediaDropRules
{
    public static bool CanAccept(bool containsSupportedVideo, bool containsSupportedSubtitle, bool hasActiveVideo) =>
        containsSupportedVideo || containsSupportedSubtitle && hasActiveVideo;
}
