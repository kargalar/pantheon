using Avalonia.Controls;
using HanumanInstitute.LibMpv;
using VideoPlayer.Core;

namespace VideoPlayer;

public sealed partial class MainWindow
{
    private readonly HttpClient _subtitleHttpClient;
    private readonly SubtitleFileCache _subtitleCache = new(Path.Combine(AppStoragePaths.DataDirectory, "Subtitles"));
    private CancellationTokenSource? _subtitleAutomationCancellation;
    private bool _subtitleAutomationStarted;
    private bool _onlineSubtitleSearchPending;
    private bool _subtitleMediaReady;
    private long _subtitleOperationRevision;
    private string _subtitleAutomationStatus = "";

    private void InitializeSubtitleAutomation()
    {
        FindSubtitleButton.Click += (_, _) => _ = RunSubtitleAutomationAsync(findOnline: true, synchronize: _preferences.AutoSynchronizeSubtitles, isManual: true);
        SynchronizeSubtitleButton.Click += (_, _) => _ = RunSubtitleAutomationAsync(findOnline: false, synchronize: true, isManual: true);
        AddSubtitleButton.Click += (_, _) => _ = OpenSubtitleAsync();
        Player.FileLoaded += (_, _) =>
        {
            var generation = Volatile.Read(ref _mediaLoadGeneration);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_timelineClosing || generation != _mediaLoadGeneration || _currentVideo is not { } currentVideo) return;
                try
                {
                    if (!PathsEqual(Player.GetProperty<string>("path"), currentVideo)) return;
                    _subtitleMediaReady = true;
                    FindSubtitleButton.IsEnabled = true;
                    SynchronizeSubtitleButton.IsEnabled = true;
                    _trackMetadataRefreshGate.MarkPendingForFallback();
                }
                catch (MpvException) { /* The next load can replace the file before this UI callback runs. */ }
            });
        };
    }

    private void CancelSubtitleAutomation()
    {
        _subtitleOperationRevision++;
        _subtitleAutomationStarted = true;
        _onlineSubtitleSearchPending = false;
        _subtitleAutomationCancellation?.Cancel();
        SetSubtitleAutomationStatus("");
    }

    private void TryStartSubtitleAutomation()
    {
        if (_subtitleAutomationStarted || !_subtitleMediaReady || !_hasAppliedTrackPreferences || _pendingResume is not null) return;
        _subtitleAutomationStarted = true;
        _ = RunSubtitleAutomationAsync(findOnline: _onlineSubtitleSearchPending, synchronize: _preferences.AutoSynchronizeSubtitles);
    }

    private async Task RunSubtitleAutomationAsync(bool findOnline, bool synchronize, bool isManual = false)
    {
        if (_currentVideo is not { } videoPath || !_subtitleMediaReady) return;
        CancelSubtitleAutomation();
        var revision = _subtitleOperationRevision;
        var generation = _mediaLoadGeneration;
        var selection = _subtitleSelectionGeneration;
        var preferences = _preferences;
        using var cancellation = new CancellationTokenSource();
        _subtitleAutomationCancellation = cancellation;
        var token = cancellation.Token;
        bool IsCurrent() => !_timelineClosing && !token.IsCancellationRequested
            && revision == _subtitleOperationRevision && generation == _mediaLoadGeneration
            && selection == _subtitleSelectionGeneration && PathsEqual(videoPath, _currentVideo);

        try
        {
            if (!isManual && _selectedSubtitleTrackId == 0) return;
            var subtitlePath = _selectedExternalSubtitlePath;
            if (findOnline && (isManual || !_subtitleCache.IsDownloadedSubtitleForVideo(
                subtitlePath, videoPath, preferences.SubtitleSearchLanguage)))
            {
                var online = await FindOnlineSubtitleForPlaybackAsync(videoPath, preferences, isManual, IsCurrent, token);
                subtitlePath = online?.Path;
                if (!IsCurrent() || subtitlePath is null) return;
                if (!online!.FromCache && FindLoadedSubtitleId(subtitlePath) is { } loadedId)
                    Player.SubReload(loadedId).Invoke();
                AddSubtitle(subtitlePath, showInPlayer: isManual || preferences.SubtitlesVisible, isAutomatic: true);
                if (!PathsEqual(_selectedExternalSubtitlePath, subtitlePath)) return;
                SaveAutomaticSubtitleSelection();
                SetSubtitleAutomationStatus(online!.FromCache ? UiText.LoadedAPreviouslyDownloadedOnlineSubtitle
                    : UiText.SubtitleDownloadedFromOpenSubtitlesAndLoaded);
            }
            if (!synchronize) return;
            if (subtitlePath is null)
            {
                if (isManual) SetSubtitleAutomationStatus(UiText.AddAnExternalSubtitleFromTheCCMenu);
                return;
            }
            if (!isManual && _subtitleDelaySeconds != 0)
            {
                SetSubtitleAutomationStatus(UiText.PreservingSavedManualSubtitleTiming);
                return;
            }
            await SynchronizeSubtitleForPlaybackAsync(videoPath, subtitlePath, preferences, IsCurrent, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            if (IsCurrent()) SetSubtitleAutomationStatus(UiText.TheSubtitleProviderDidNotRespondInTime);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException
            or System.Text.Json.JsonException or InvalidOperationException or NotSupportedException or TimeoutException
            or MpvException or ArgumentException)
        {
            if (!IsCurrent()) return;
            AppLogger.Error("Subtitle automation failed", exception);
            SetSubtitleAutomationStatus(exception is System.Text.Json.JsonException
                ? UiText.TheSubtitleProviderReturnedAnInvalidResponse : exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_subtitleAutomationCancellation, cancellation)) _subtitleAutomationCancellation = null;
        }
    }

    private async Task<OnlineSubtitleResult?> FindOnlineSubtitleForPlaybackAsync(
        string videoPath, PlayerPreferences preferences, bool refresh, Func<bool> isCurrent, CancellationToken token)
    {
        var apiKey = string.IsNullOrWhiteSpace(preferences.OpenSubtitlesApiKey)
            ? Environment.GetEnvironmentVariable("PANTHEON_OPENSUBTITLES_API_KEY")
                ?? Environment.GetEnvironmentVariable("PANTEON_OPENSUBTITLES_API_KEY")
            : preferences.OpenSubtitlesApiKey;
        var lookup = new OnlineSubtitleLookup(new OpenSubtitlesClient(_subtitleHttpClient), _subtitleCache);
        var result = await lookup.FindAsync(videoPath, preferences.SubtitleSearchLanguage, apiKey ?? "", refresh, stage =>
        {
            if (isCurrent()) SetSubtitleAutomationStatus(stage == OnlineSubtitleStage.Searching
                ? UiText.SearchingOpenSubtitlesForMovieOrEpisodeSubtitles : UiText.DownloadingSubtitlesFromOpenSubtitles);
        }, token);
        if (!isCurrent()) return null;
        if (result is null)
        {
            SetSubtitleAutomationStatus(UiText.OnlineSubtitleNotFound);
        }
        return result;
    }

    private async Task SynchronizeSubtitleForPlaybackAsync(
        string videoPath, string subtitlePath, PlayerPreferences preferences, Func<bool> isCurrent, CancellationToken token)
    {
        var sourceSubtitlePath = _subtitleCache.FindSynchronizationSource(subtitlePath) ?? subtitlePath;
        var audioStream = GetSelectedAudioReferenceStream();
        if (audioStream is null)
        {
            SetSubtitleAutomationStatus(UiText.SelectAnAudioTrackForSynchronization);
            return;
        }
        SetSubtitleAutomationStatus(UiText.SynchronizingSubtitlesWithAudio);
        var encoding = SubtitleCodepageRules.Normalize(preferences.SubtitleCodepage);
        if (encoding == SubtitleCodepageRules.Auto) encoding = "infer";
        else if (encoding == "cp1254") encoding = "windows-1254";
        var result = await new SubtitleSynchronizer(_subtitleCache).SynchronizeAsync(videoPath, sourceSubtitlePath,
            ResolveSubtitleSynchronizer(preferences.SubtitleSynchronizerPath), audioStream, encoding, token);
        if (!isCurrent() || !PathsEqual(_selectedExternalSubtitlePath, subtitlePath)) return;
        AddSubtitle(result.Path, preferences.SubtitlesVisible, isAutomatic: true);
        if (!PathsEqual(_selectedExternalSubtitlePath, result.Path)) return;
        SaveAutomaticSubtitleSelection();
        SetSubtitleAutomationStatus(result.FromCache ? UiText.LoadedCachedSynchronizedSubtitles
            : UiText.Format(UiText.SubtitleSynchronizedFormat, result.OffsetSeconds, result.ScaleFactor));
    }

    private void SaveAutomaticSubtitleSelection()
    {
        _subtitleDelaySeconds = 0;
        Player.SetProperty("sub-delay", 0.0);
        UpdateSubtitleDelayTooltip();
        SaveCurrentProgress(force: true);
    }

    private string? GetSelectedAudioReferenceStream()
    {
        var count = Math.Clamp(Player.GetProperty<int>("track-list/count"), 0, 256);
        var ordinal = 0;
        for (var index = 0; index < count; index++)
        {
            if (ReadTrackProperty(index, "type", "") != "audio") continue;
            if (ReadTrackProperty(index, "selected", false))
            {
                if (ReadTrackProperty(index, "external", false)) return null;
                return $"0:a:{ordinal}";
            }
            ordinal++;
        }
        return null;
    }

    internal static string ResolveSubtitleSynchronizer(string configuredPath)
    {
        if (configuredPath != SubtitleAutomationOptions.DefaultSynchronizer) return configuredPath;
        var bundled = Path.Combine(AppContext.BaseDirectory, "SubtitleTools", "ffsubsync.exe");
        return File.Exists(bundled) ? bundled : configuredPath;
    }

    private void SetSubtitleAutomationStatus(string status)
    {
        _subtitleAutomationStatus = status;
        SubtitleAutomationStatus.Text = status;
        SubtitleAutomationStatus.IsVisible = !string.IsNullOrEmpty(status);
        UpdateSubtitleDelayTooltip();
    }
}
