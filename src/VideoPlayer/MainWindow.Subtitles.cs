using System.Globalization;
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
    private bool _updatingSubtitlePickerControls;

    private void RepairSynchronizedSubtitleEncoding(string path, string codepage)
    {
        try { _subtitleCache.RepairSynchronizedEncoding(path, codepage); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLogger.Error("Synchronized subtitle encoding could not be repaired", exception);
        }
    }

    private void InitializeSubtitleAutomation()
    {
        CloseSubtitlePopupButton.Click += (_, _) => SubtitlePickerPopup.IsOpen = false;
        ToggleSubtitlesVisibilityButton.Click += (_, _) =>
        {
            ToggleSubtitles();
            RefreshSubtitlePickerContent();
        };
        SubtitleDelayEarlierButton.Click += (_, _) =>
        {
            AdjustSubtitleDelay(-1);
            RefreshSubtitlePickerContent();
        };
        SubtitleDelayLaterButton.Click += (_, _) =>
        {
            AdjustSubtitleDelay(1);
            RefreshSubtitlePickerContent();
        };
        SubtitleDelayResetButton.Click += (_, _) =>
        {
            ResetSubtitleDelay();
            RefreshSubtitlePickerContent();
        };
        SubtitleFontSmallerButton.Click += (_, _) => AdjustSubtitleFontSize(-2);
        SubtitleFontLargerButton.Click += (_, _) => AdjustSubtitleFontSize(2);

        foreach (var option in SubtitleCodepageRules.Options)
        {
            SubtitleCodepageSelector.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Codepage });
        }
        SubtitleCodepageSelector.SelectionChanged += (_, _) =>
        {
            if (_updatingSubtitlePickerControls || SubtitleCodepageSelector.SelectedItem is not ComboBoxItem { Tag: string codepage })
                return;
            if (string.Equals(_preferences.SubtitleCodepage, codepage, StringComparison.OrdinalIgnoreCase)) return;
            SavePreferences(_preferences with { SubtitleCodepage = codepage });
            ApplySubtitleCodepage();
        };

        foreach (var (label, code) in SubtitleAutomationOptions.Languages)
        {
            SubtitleSearchLanguageSelector.Items.Add(new ComboBoxItem { Content = label, Tag = code });
        }
        SubtitleSearchLanguageSelector.SelectionChanged += (_, _) =>
        {
            if (_updatingSubtitlePickerControls || SubtitleSearchLanguageSelector.SelectedItem is not ComboBoxItem { Tag: string language })
                return;
            if (string.Equals(_preferences.SubtitleSearchLanguage, language, StringComparison.OrdinalIgnoreCase)) return;
            SavePreferences(_preferences with { SubtitleSearchLanguage = language });
        };

        AutoFindSubtitlesCheckBox.IsChecked = _preferences.AutoFindSubtitles;
        AutoFindSubtitlesCheckBox.IsCheckedChanged += (_, _) =>
        {
            if (_updatingSubtitlePickerControls) return;
            var isChecked = AutoFindSubtitlesCheckBox.IsChecked == true;
            if (_preferences.AutoFindSubtitles != isChecked)
                SavePreferences(_preferences with { AutoFindSubtitles = isChecked });
        };
        AutoSynchronizeSubtitlesCheckBox.IsChecked = _preferences.AutoSynchronizeSubtitles;
        AutoSynchronizeSubtitlesCheckBox.IsCheckedChanged += (_, _) =>
        {
            if (_updatingSubtitlePickerControls) return;
            var isChecked = AutoSynchronizeSubtitlesCheckBox.IsChecked == true;
            if (_preferences.AutoSynchronizeSubtitles != isChecked)
                SavePreferences(_preferences with { AutoSynchronizeSubtitles = isChecked });
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

    private void AdjustSubtitleFontSize(int delta)
    {
        var nextSize = Math.Clamp(
            _preferences.SubtitleFontSize + delta,
            PlayerPreferences.MinimumSubtitleFontSize,
            PlayerPreferences.MaximumSubtitleFontSize);
        if (nextSize == _preferences.SubtitleFontSize) return;
        SavePreferences(_preferences with { SubtitleFontSize = nextSize });
        ApplySubtitleAppearancePreferences();
        RefreshSubtitlePickerContent();
    }

    private void UpdateSubtitlePickerSize()
    {
        if (SubtitlePickerContent is null) return;
        var width = Bounds.Width > 0 ? Math.Clamp(Bounds.Width - 36, 360, 720) : 720;
        var maxHeight = Bounds.Height > 0 ? Math.Clamp(Bounds.Height - 105, 340, 740) : 740;
        SubtitlePickerContent.Width = width;
        SubtitlePickerContent.MaxHeight = maxHeight;
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
        var referenceStream = GetSynchronizationReferenceStream();
        if (referenceStream is null)
        {
            const string noAudioMsg = "Senkronizasyon için geçerli bir ses akışı seçin.";
            SetSubtitleAutomationStatus(noAudioMsg);
            ShowStatusNotification(noAudioMsg);
            return;
        }
        SetSubtitleAutomationStatus("Altyazı otomatik senkronize ediliyor...");
        ShowStatusNotification("Altyazı senkronizasyonu başlatıldı...");
        var encoding = SubtitleCodepageRules.Normalize(preferences.SubtitleCodepage);
        if (encoding == SubtitleCodepageRules.Auto) encoding = "infer";
        else if (encoding == "cp1254") encoding = "windows-1254";
        var result = await new SubtitleSynchronizer(_subtitleCache).SynchronizeAsync(videoPath, sourceSubtitlePath,
            ResolveSubtitleSynchronizer(preferences.SubtitleSynchronizerPath), referenceStream, encoding, token);
        if (!isCurrent() || !PathsEqual(_selectedExternalSubtitlePath, subtitlePath)) return;
        if (FindLoadedSubtitleId(result.Path) is { } loadedSyncedId)
            Player.SubReload(loadedSyncedId).Invoke();
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

    private string? GetSynchronizationReferenceStream()
    {
        var count = Math.Clamp(Player.GetProperty<int>("track-list/count"), 0, 256);
        var embeddedSubtitles = new List<EmbeddedSubtitleTrack>();
        for (var index = 0; index < count; index++)
        {
            if (ReadTrackProperty(index, "type", "") != "sub") continue;
            if (ReadTrackProperty(index, "external", false)) continue;
            embeddedSubtitles.Add(new EmbeddedSubtitleTrack(
                ReadTrackProperty(index, "codec", ""),
                ReadTrackProperty(index, "forced", false),
                ReadTrackProperty(index, "selected", false),
                ReadTrackProperty(index, "lang", ""),
                ReadTrackProperty(index, "title", "")));
        }
        if (SubtitleReferenceRules.SelectEmbeddedTextSubtitle(embeddedSubtitles, GetSelectedAudioLanguage()) is { } subtitleOrdinal)
            return $"0:s:{subtitleOrdinal}";
        return GetSelectedAudioReferenceStream();
    }

    private string? GetSelectedAudioLanguage()
    {
        var count = Math.Clamp(Player.GetProperty<int>("track-list/count"), 0, 256);
        for (var index = 0; index < count; index++)
        {
            if (ReadTrackProperty(index, "type", "") != "audio") continue;
            if (ReadTrackProperty(index, "selected", false))
                return ReadTrackProperty(index, "lang", "");
        }
        return null;
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
        var hasStatus = !string.IsNullOrEmpty(status);
        SubtitleAutomationStatus.IsVisible = hasStatus;
        if (SubtitleAutomationStatusBanner is not null)
            SubtitleAutomationStatusBanner.IsVisible = hasStatus;
        UpdateSubtitleDelayTooltip();
    }

    private string? GetOpenSubtitlesApiKey() =>
        string.IsNullOrWhiteSpace(_preferences.OpenSubtitlesApiKey)
            ? Environment.GetEnvironmentVariable("PANTHEON_OPENSUBTITLES_API_KEY")
                ?? Environment.GetEnvironmentVariable("PANTEON_OPENSUBTITLES_API_KEY")
            : _preferences.OpenSubtitlesApiKey;

    private static void SelectComboBoxByTag(ComboBox comboBox, string value)
    {
        foreach (var obj in comboBox.Items)
        {
            if (obj is ComboBoxItem { Tag: string tag } item && string.Equals(tag, value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }

    private string BuildActiveSubtitleSummary()
    {
        if (_currentVideo is null) return "Video açık değil";
        if (!_preferences.SubtitlesVisible) return "Altyazı gizli";
        if (_selectedExternalSubtitlePath is { } externalPath)
            return $"Aktif: {Path.GetFileName(externalPath)}";
        if (SubtitleTrackList.SelectedItem is ListBoxItem { Tag: MediaTrackOption track })
            return $"Aktif: {track.Label}";
        return "Aktif altyazı yok";
    }

    private void RefreshSubtitlePickerContent()
    {
        UpdateSubtitlePickerSize();

        _updatingSubtitlePickerControls = true;
        try
        {
            if (AutoFindSubtitlesCheckBox is not null && AutoFindSubtitlesCheckBox.IsChecked != _preferences.AutoFindSubtitles)
                AutoFindSubtitlesCheckBox.IsChecked = _preferences.AutoFindSubtitles;
            if (AutoSynchronizeSubtitlesCheckBox is not null && AutoSynchronizeSubtitlesCheckBox.IsChecked != _preferences.AutoSynchronizeSubtitles)
                AutoSynchronizeSubtitlesCheckBox.IsChecked = _preferences.AutoSynchronizeSubtitles;
            if (SubtitleCodepageSelector is not null)
                SelectComboBoxByTag(SubtitleCodepageSelector, _preferences.SubtitleCodepage);
            if (SubtitleSearchLanguageSelector is not null)
                SelectComboBoxByTag(SubtitleSearchLanguageSelector, _preferences.SubtitleSearchLanguage);
        }
        finally
        {
            _updatingSubtitlePickerControls = false;
        }

        if (ActiveSubtitleSummaryLabel is not null)
            ActiveSubtitleSummaryLabel.Text = BuildActiveSubtitleSummary();
        if (ToggleSubtitlesVisibilityButton is not null)
        {
            ToggleSubtitlesVisibilityButton.Content = _preferences.SubtitlesVisible ? "Altyazı: Açık" : "Altyazı: Gizli";
            ToggleSubtitlesVisibilityButton.Foreground = _preferences.SubtitlesVisible
                ? Brush.Parse("#00BC7D")
                : Brush.Parse("#A7A7A7");
        }
        if (SubtitleDelayValueLabel is not null)
            SubtitleDelayValueLabel.Text = $"{_subtitleDelaySeconds.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)} sn";
        if (SubtitleFontSizeLabel is not null)
            SubtitleFontSizeLabel.Text = $"{_preferences.SubtitleFontSize} px";
        if (SubtitleFontSmallerButton is not null)
            SubtitleFontSmallerButton.IsEnabled = _preferences.SubtitleFontSize > PlayerPreferences.MinimumSubtitleFontSize;
        if (SubtitleFontLargerButton is not null)
            SubtitleFontLargerButton.IsEnabled = _preferences.SubtitleFontSize < PlayerPreferences.MaximumSubtitleFontSize;

        var hasVideo = _currentVideo is { };
        FindSubtitleButton.IsEnabled = hasVideo;
        ManualSubtitleSearchButton.IsEnabled = hasVideo;
        SubtitleSearchQueryBox.IsEnabled = hasVideo;
        SynchronizeSubtitleButton.IsEnabled = hasVideo;
        AddSubtitleButton.IsEnabled = hasVideo;
        SubtitleDelayEarlierButton.IsEnabled = hasVideo;
        SubtitleDelayLaterButton.IsEnabled = hasVideo;
        SubtitleDelayResetButton.IsEnabled = hasVideo && Math.Abs(_subtitleDelaySeconds) > 0.001;

        LocalFolderSubtitleList.Items.Clear();
        if (_currentVideo is { } videoPath)
        {
            var localFiles = MediaPathRules.FindAllSubtitlesBesideVideo(videoPath);
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
                ToolTip.SetTip(item, fileName);
                LocalFolderSubtitleList.Items.Add(item);
            }
            LocalFolderSubtitleList.IsVisible = localFiles.Count > 0;
            NoLocalSubtitlesNotice.IsVisible = localFiles.Count == 0;
        }
        else
        {
            LocalFolderSubtitleList.IsVisible = false;
            NoLocalSubtitlesNotice.IsVisible = true;
        }
        NoSubtitlesNotice.IsVisible = SubtitleTrackList.Items.Count <= 1;
    }

    private void OnLocalFolderSubtitleSelected()
    {
        if (LocalFolderSubtitleList.SelectedItem is not ListBoxItem { Tag: string path }) return;
        LocalFolderSubtitleList.SelectedItem = null;
        if (FindLoadedSubtitleId(path) is { } loadedId)
            Player.SubReload(loadedId).Invoke();
        AddSubtitle(path, showInPlayer: true, isAutomatic: false);
        SaveAutomaticSubtitleSelection();
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
                ToolTip.SetTip(item, label);
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
            var downloadPath = _subtitleCache.DownloadPath(videoPath, _preferences.SubtitleSearchLanguage);
            await client.DownloadAsync(candidate, downloadPath, apiKey, CancellationToken.None);
            if (FindLoadedSubtitleId(downloadPath) is { } loadedId)
                Player.SubReload(loadedId).Invoke();
            AddSubtitle(downloadPath, showInPlayer: true, isAutomatic: false);
            SaveAutomaticSubtitleSelection();
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
