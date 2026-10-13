namespace VideoPlayer.Core;

public static class QueueOrderRules
{
    public static IReadOnlyList<string> CreatePlaybackSnapshot(
        string? currentPath,
        IReadOnlyList<string> queuedPaths,
        bool currentPathEnded,
        int maximumEntryCount)
    {
        ArgumentNullException.ThrowIfNull(queuedPaths);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumEntryCount);

        var snapshot = new List<string>(Math.Min(maximumEntryCount, queuedPaths.Count + 1));
        if (maximumEntryCount > 0 && !currentPathEnded && !string.IsNullOrWhiteSpace(currentPath))
            snapshot.Add(currentPath);
        foreach (var queuedPath in queuedPaths)
        {
            if (snapshot.Count == maximumEntryCount) break;
            snapshot.Add(queuedPath);
        }

        return snapshot;
    }

    public static string? TakeNextAvailablePath(List<string> queue, Predicate<string> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(isAvailable);

        for (var index = 0; index < queue.Count; index++)
        {
            var path = queue[index];
            if (!isAvailable(path)) continue;
            queue.RemoveRange(0, index + 1);
            return path;
        }

        queue.Clear();
        return null;
    }

    public static int AppendUniquePaths(IList<string> queue, IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(candidates);

        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in queue)
            knownPaths.Add(GetFullPathWhenNeeded(path));

        var addedCount = 0;
        foreach (var path in candidates)
        {
            var fullPath = GetFullPathWhenNeeded(path);
            if (!knownPaths.Add(fullPath)) continue;
            queue.Add(fullPath);
            addedCount++;
        }

        return addedCount;
    }

    public static IReadOnlyList<string> CombinePlaylistEntriesAndDroppedVideos(
        IReadOnlyList<string> playlistEntries,
        IEnumerable<string> droppedVideoPaths,
        int maximumEntryCount)
    {
        ArgumentNullException.ThrowIfNull(playlistEntries);
        ArgumentNullException.ThrowIfNull(droppedVideoPaths);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumEntryCount);
        if (playlistEntries.Count > maximumEntryCount)
            throw new InvalidDataException($"A combined playlist can contain at most {maximumEntryCount:N0} videos.");

        var combinedEntries = new List<string>(Math.Min(maximumEntryCount, playlistEntries.Count));
        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var playlistEntry in playlistEntries)
        {
            combinedEntries.Add(playlistEntry);
            knownPaths.Add(GetFullPathWhenNeeded(playlistEntry));
        }

        foreach (var droppedVideoPath in droppedVideoPaths)
        {
            var fullPath = GetFullPathWhenNeeded(droppedVideoPath);
            if (!knownPaths.Add(fullPath)) continue;
            if (combinedEntries.Count == maximumEntryCount)
                throw new InvalidDataException($"A combined playlist can contain at most {maximumEntryCount:N0} videos.");
            combinedEntries.Add(fullPath);
        }

        return combinedEntries;
    }

    private static string GetFullPathWhenNeeded(string path) =>
        Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path);

    public static bool Move<T>(IList<T> items, int source, int destination)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (source < 0 || source >= items.Count || destination < 0 || destination >= items.Count || source == destination)
            return false;
        var item = items[source];
        items.RemoveAt(source);
        items.Insert(destination, item);
        return true;
    }
}
