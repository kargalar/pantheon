namespace VideoPlayer.Core;

public enum OnlineSubtitleStage { Searching, Downloading }

public sealed record OnlineSubtitleResult(string Path, bool FromCache);

public sealed class OnlineSubtitleLookup(OpenSubtitlesClient repository, SubtitleFileCache cache)
{
    public async Task<OnlineSubtitleResult?> FindAsync(
        string videoPath, string language, string apiKey, bool refresh,
        Action<OnlineSubtitleStage>? reportStage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var downloadedPath = cache.DownloadPath(videoPath, language);
        if (!refresh && SubtitleFileCache.IsValidSrt(downloadedPath)) return new(downloadedPath, true);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(UiText.OpenSubtitlesKeyRequired);

        reportStage?.Invoke(OnlineSubtitleStage.Searching);
        var candidate = await repository.FindAsync(videoPath, language, apiKey, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate is null) return null;
        reportStage?.Invoke(OnlineSubtitleStage.Downloading);
        await repository.DownloadAsync(candidate, downloadedPath, apiKey, cancellationToken);
        return new(downloadedPath, false);
    }
}
