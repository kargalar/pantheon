using Avalonia.Controls;
using Avalonia.Media;
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
        CloseSubtitlePopupButton.Click += (_, _) => SubtitlePickerPopup.IsOpen = false;
        AutoFindSubtitlesCheckBox.IsChecked = _preferences.AutoFindSubtitles;
        AutoFindSubtitlesCheckBox.IsCheckedChanged += (_, _) =>
        {
            var isChecked = AutoFindSubtitlesCheckBox.IsChecked == true;
            if (_preferences.AutoFindSubtitles != isChecked)
                SavePreferences(_preferences with { AutoFindSubtitles = isChecked });
        };
        FindSubtitleButton.Click += (_, _) =>
        {
            SubtitleSearchQueryBox.Text = "";
            _ = SearchOnlineSubtitlesManuallyAsync();
        };
        ManualSubtitleSearchButton.Click += (_, _) => _ = SearchOnlineSubtitlesManuallyAsync();
        SubtitleSearchQueryBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) _ = SearchOnlineSubtitlesManuallyAsync();
        };
        LocalFolderSubtitleList.SelectionChanged += (_, _) => OnLocalFolderSubtitleSelected();
        OnlineSearchResultsList.SelectionChanged += (_, _) => _ = OnOnlineSearchResultSelectedAsync();
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
        if (!_onlineSubtitleSearchPending && (!_preferences.AutoSynchronizeSubtitles || _selectedExternalSubtitlePath is null)) return;
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
                OnlineSubtitleResult? online;
                try
                {
                    online = await FindOnlineSubtitleForPlaybackAsync(videoPath, preferences, isManual, IsCurrent, token);
                }
                catch (Exception exception) when (subtitlePath is not null && exception is IOException
                    or UnauthorizedAccessException or HttpRequestException or System.Text.Json.JsonException
                    or InvalidOperationException or ArgumentException)
                {
                    online = null;
                }
                if (!IsCurrent()) return;
                if (online is not null)
                {
                    subtitlePath = online.Path;
                    if (!online.FromCache && FindLoadedSubtitleId(subtitlePath) is { } loadedId)
                        Player.SubReload(loadedId).Invoke();
                    AddSubtitle(subtitlePath, showInPlayer: isManual || preferences.SubtitlesVisible, isAutomatic: true);
                    if (!PathsEqual(_selectedExternalSubtitlePath, subtitlePath)) return;
                    SaveAutomaticSubtitleSelection();
                    SetSubtitleAutomationStatus(online.FromCache ? UiText.LoadedAPreviouslyDownloadedOnlineSubtitle
                        : UiText.SubtitleDownloadedFromOpenSubtitlesAndLoaded);
                }
            }
            if (!synchronize) return;
            if (subtitlePath is null)
            {
                if (isManual)
                {
                    const string msg = "Senkronize etmek için önce harici bir altyazı seçin veya ekleyin.";
                    SetSubtitleAutomationStatus(msg);
                    ShowStatusNotification(msg);
                }
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
            const string noAudioMsg = "Senkronizasyon için geçerli bir ses akışı seçin.";
            SetSubtitleAutomationStatus(noAudioMsg);
            ShowStatusNotification(noAudioMsg);
            return;
        }
        SetSubtitleAutomationStatus("Altyazı ses ile senkronize ediliyor (FFsubsync)...");
        ShowStatusNotification("Altyazı senkronizasyonu başlatıldı...");
        var encoding = SubtitleCodepageRules.Normalize(preferences.SubtitleCodepage);
        if (encoding == SubtitleCodepageRules.Auto) encoding = "infer";
        else if (encoding == "cp1254") encoding = "windows-1254";
        var result = await new SubtitleSynchronizer(_subtitleCache).SynchronizeAsync(videoPath, sourceSubtitlePath,
            ResolveSubtitleSynchronizer(preferences.SubtitleSynchronizerPath), audioStream, encoding, token);
        if (!isCurrent() || !PathsEqual(_selectedExternalSubtitlePath, subtitlePath)) return;
        AddSubtitle(result.Path, preferences.SubtitlesVisible, isAutomatic: true);
        if (!PathsEqual(_selectedExternalSubtitlePath, result.Path)) return;
        SaveAutomaticSubtitleSelection();
        var syncMsg = result.FromCache
            ? "Önbellekteki senkronize altyazı yüklendi."
            : $"Altyazı senkronize edildi (Öteleme: {result.OffsetSeconds:+0.00;-0.00;0.00}s).";
        SetSubtitleAutomationStatus(syncMsg);
        ShowStatusNotification(syncMsg);
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
        if (SubtitleSyncStatus is not null)
        {
            SubtitleSyncStatus.Text = status;
            SubtitleSyncStatus.IsVisible = !string.IsNullOrEmpty(status);
        }
        UpdateSubtitleDelayTooltip();
    }

    private string? GetOpenSubtitlesApiKey() =>
        string.IsNullOrWhiteSpace(_preferences.OpenSubtitlesApiKey)
            ? Environment.GetEnvironmentVariable("PANTHEON_OPENSUBTITLES_API_KEY")
                ?? Environment.GetEnvironmentVariable("PANTEON_OPENSUBTITLES_API_KEY")
            : _preferences.OpenSubtitlesApiKey;

    private void RefreshSubtitlePickerContent()
    {
        if (_currentVideo is not { } videoPath) return;

        if (AutoFindSubtitlesCheckBox is not null && AutoFindSubtitlesCheckBox.IsChecked != _preferences.AutoFindSubtitles)
            AutoFindSubtitlesCheckBox.IsChecked = _preferences.AutoFindSubtitles;

        FindSubtitleButton.IsEnabled = true;
        SynchronizeSubtitleButton.IsEnabled = true;
        var localFiles = MediaPathRules.FindAllSubtitlesBesideVideo(videoPath);
        LocalFolderSubtitleList.Items.Clear();
        foreach (var file in localFiles)
        {
            var fileName = Path.GetFileName(file);
            var isLoaded = _loadedSubtitles.Contains(Path.GetFullPath(file))
                || PathsEqual(_selectedExternalSubtitlePath, file);
            var item = new ListBoxItem
            {
                Content = isLoaded ? $"✓ {fileName} (Aktif)" : fileName,
                Tag = file,
                Foreground = isLoaded ? Brush.Parse("#00BC7D") : Brush.Parse("#F2F2F2")
            };
            LocalFolderSubtitleList.Items.Add(item);
        }
        NoLocalSubtitlesNotice.IsVisible = localFiles.Count == 0;
        NoSubtitlesNotice.IsVisible = SubtitleTrackList.Items.Count <= 1;
    }

    private void OnLocalFolderSubtitleSelected()
    {
        if (LocalFolderSubtitleList.SelectedItem is not ListBoxItem { Tag: string path }) return;
        LocalFolderSubtitleList.SelectedItem = null;
        AddSubtitle(path, showInPlayer: true, isAutomatic: false);
        ShowStatusNotification($"Klasördeki '{Path.GetFileName(path)}' altyazısı yüklendi");
        RefreshSubtitlePickerContent();
    }

    private async Task SearchOnlineSubtitlesManuallyAsync()
    {
        if (_currentVideo is not { } videoPath) return;
        var query = SubtitleSearchQueryBox.Text?.Trim();
        var apiKey = GetOpenSubtitlesApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            SetSubtitleAutomationStatus(UiText.OpenSubtitlesKeyRequired);
            return;
        }

        SetSubtitleAutomationStatus("OpenSubtitles üzerinde aranıyor...");
        OnlineSearchResultsList.Items.Clear();
        OnlineSearchResultsList.IsVisible = true;

        try
        {
            var client = new OpenSubtitlesClient(_subtitleHttpClient);
            IReadOnlyList<SubtitleCandidate> candidates;
            if (string.IsNullOrWhiteSpace(query))
            {
                candidates = await client.SearchCandidatesForVideoAsync(videoPath, _preferences.SubtitleSearchLanguage, apiKey, CancellationToken.None);
            }
            else
            {
                candidates = await client.SearchByQueryAsync(query, _preferences.SubtitleSearchLanguage, apiKey, CancellationToken.None);
            }

            if (candidates.Count == 0)
            {
                SetSubtitleAutomationStatus("Sonuç bulunamadı.");
                return;
            }

            SetSubtitleAutomationStatus($"{candidates.Count} altyazı bulundu. Yüklemek için seçin:");
            foreach (var candidate in candidates)
            {
                var label = $"{candidate.Release} [{candidate.Language}] · ⬇ {candidate.Downloads}";
                if (candidate.HashMatch) label = "★ " + label;
                var item = new ListBoxItem { Content = label, Tag = candidate };
                OnlineSearchResultsList.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            SetSubtitleAutomationStatus("Arama hatası: " + ex.Message);
        }
    }

    private async Task OnOnlineSearchResultSelectedAsync()
    {
        if (_currentVideo is not { } videoPath || OnlineSearchResultsList.SelectedItem is not ListBoxItem { Tag: SubtitleCandidate candidate }) return;
        OnlineSearchResultsList.SelectedItem = null;
        var apiKey = GetOpenSubtitlesApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) return;

        SetSubtitleAutomationStatus($"'{candidate.Release}' indiriliyor...");
        try
        {
            var client = new OpenSubtitlesClient(_subtitleHttpClient);
            var downloadPath = _subtitleCache.DownloadPath(videoPath, _preferences.SubtitleSearchLanguage, candidate.FileId);
            await client.DownloadAsync(candidate, downloadPath, apiKey, CancellationToken.None);
            AddSubtitle(downloadPath, showInPlayer: true, isAutomatic: false);
            SetSubtitleAutomationStatus("Altyazı indirildi ve yüklendi.");
            ShowStatusNotification($"'{candidate.Release}' indirildi ve yüklendi");
            RefreshSubtitlePickerContent();
        }
        catch (Exception ex)
        {
            SetSubtitleAutomationStatus("İndirme hatası: " + ex.Message);
        }
    }
}
