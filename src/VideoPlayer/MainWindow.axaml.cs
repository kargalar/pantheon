using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HanumanInstitute.LibMpv;
using HanumanInstitute.LibMpv.Core;
using VideoPlayer.Core;
using System.Globalization;
using System.Text;

namespace VideoPlayer;

public readonly record struct QueueListItem(string Path, int Index)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

public sealed partial class MainWindow : Window
{
    private const double HeaderRevealZoneHeight = 48;
    private const double BottomRevealZoneHeight = 160;
    private const double QueueRevealZoneWidth = 146;
    private const double QueueDragThreshold = 5;
    private const ulong TrackListObservationId = 1;
    private const ulong ChapterListObservationId = 2;
    private const ulong ChapterObservationId = 3;
    private const int MaximumChapterCount = 10_000;
    private static readonly TimeSpan CursorHideDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BottomActivityVisibilityDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PanelFadeDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan PlaybackStatusFailureLogDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PreferenceUpdateDebounce = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan QueuePersistenceDebounce = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan DiscreteSeekBurstDuration = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan SubtitleSyncNotificationDuration = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan VolumeNotificationDuration = TimeSpan.FromSeconds(1.5);

    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _preferencesUpdateTimer;
    private readonly DispatcherTimer _queuePersistenceTimer;
    private readonly DispatcherTimer _captureNotificationTimer;
    private readonly DispatcherTimer _subtitleSyncNotificationTimer;
    private readonly DispatcherTimer _statusNotificationTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };
    private readonly DispatcherTimer _volumeNotificationTimer = new() { Interval = VolumeNotificationDuration };
    private readonly DispatcherTimer _cursorHideTimer = new() { Interval = CursorHideDelay };
    private readonly DispatcherTimer _bottomActivityVisibilityTimer = new() { Interval = BottomActivityVisibilityDuration };
    private readonly DispatcherTimer _panelFadeTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Dictionary<Control, PanelFadeState> _panelFadeStates = [];
    private readonly PlayerPreferencesStore _preferencesStore;
    private readonly PlaybackProgressStore _playbackProgressStore;
    private readonly string _queuePlaylistPath;
    private readonly SemaphoreSlim _queuePersistenceLock = new(1, 1);
    private readonly Task _playbackProgressLoadTask;
    private Task _queuePersistenceTask = Task.CompletedTask;
    private Task _captureTask = Task.CompletedTask;
    private Bitmap? _captureThumbnail;
    private IReadOnlyList<string> _startupVideoPaths = Array.Empty<string>();
    private readonly List<string> _queuedVideos = [];
    private bool _queueItemsDirty = true;
    private long _queueScanGeneration;
    private long _queuePersistenceGeneration;
    private CancellationTokenSource? _queueScanCancellation;
    private CancellationTokenSource? _playlistLoadCancellation;
    private long _loadRequestGeneration;
    private long _mediaLoadGeneration;
    private long _subtitleSelectionGeneration;
    private ActivePlaylistEntry? _activePlaylistEntry;
    private readonly HashSet<string> _loadedSubtitles = new(StringComparer.OrdinalIgnoreCase);
    private List<TrackMetadata>? _trackMetadataCache;
    private int _trackMetadataCacheCount = -1;
    private readonly CoalescingRefreshGate _trackMetadataRefreshGate = new(initiallyPending: true);
    private readonly CoalescingRefreshGate _chapterListRefreshGate = new();
    private readonly DispatcherTimer _subtitlePickerCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly List<(MenuItem Item, bool IsHeader)> _autoHideMenuItems = [];
    private string? _trackListSignature;
    private PlayerPreferences _preferences;
    private bool _isDraggingSeek;
    private bool _isPlaying;
    private bool _isMuted;
    private bool _preferencesDirty;
    private string? _appliedSubtitleCodepage;
    private string? _currentVideo;
    private double _playbackSpeed = 1.0;
    private double _videoDuration;
    private double? _discreteSeekTarget;
    private DateTime _lastDiscreteSeek = DateTime.MinValue;
    private double _subtitleDelaySeconds;
    private string? _selectedExternalSubtitlePath;
    private int? _selectedSubtitleTrackId;
    private long _lastDisplayedPositionSecond = long.MinValue;
    private long _lastDisplayedDurationSecond = long.MinValue;
    private double _lastDisplayedPlaybackSpeed = double.NaN;
    private PlaybackProgress? _pendingResume;
    private double? _pendingResumeStartPositionSeconds;
    private double? _pendingResumeTargetPositionSeconds;
    private DateTime _lastProgressSave = DateTime.MinValue;
    private bool _endOfFileHandled;
    private bool _playbackStatusUnavailableLogged;
    private bool _activeVideoDecoderLogged;
    private DateTime _playbackStatusUnavailableSince = DateTime.MinValue;
    private bool _headerHoverActive;
    private bool _bottomHoverActive;
    private bool _bottomActivityActive;
    private bool _queuePanelPinned;
    private bool _queuePanelHoverActive;
    private WindowsKeyboardHook? _keyboardHook;
    private WindowsMouseHook? _mouseHook;
    private SettingsWindow? _settingsWindow;
    private IPointer? _queueDragPointer;
    private Point _queueDragStart;
    private int _queueDragIndex = -1;
    private int _queueDragDestination = -1;
    private bool _queueDragActive;
    private bool _cursorHidden;
    private Cursor? _cursorBeforeHide;
    private bool _updatingTrackSelectors;
    private int _chapterCount;
    private bool _hasAppliedTrackPreferences;
    private MpvContext Player => VideoSurface.MpvContext!;

    private sealed class PanelFadeState(Control control)
    {
        public Control Control { get; } = control;
        public bool IsAnimating { get; set; }
        public DateTime StartedAt { get; set; }
        public double StartOpacity { get; set; }
        public double TargetOpacity { get; set; }
    }

    private sealed record ActivePlaylistEntry(long EntryId, long LoadGeneration);

    public MainWindow() : this(Array.Empty<string>())
    {
    }

    public MainWindow(string? startupVideoPath)
        : this(string.IsNullOrWhiteSpace(startupVideoPath) ? Array.Empty<string>() : [startupVideoPath])
    {
    }

    public MainWindow(IReadOnlyList<string> startupVideoPaths)
        : this(startupVideoPaths, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) })
    {
    }

    internal MainWindow(IReadOnlyList<string> startupVideoPaths, HttpClient subtitleHttpClient)
    {
        _subtitleHttpClient = subtitleHttpClient;
        _startupVideoPaths = MediaPathRules.ResolveExistingVideoPaths(startupVideoPaths);
        InitializeComponent();
        Player.StartFile += OnPlayerStartFile;
        Player.EndFile += OnPlayerEndFile;
        Player.PropertyChanged += OnPlayerPropertyChanged;
        Player.ObserveProperty(TrackListObservationId, "track-list", MpvFormat.Node);
        Player.ObserveProperty(ChapterListObservationId, "chapter-list", MpvFormat.Node);
        Player.ObserveProperty(ChapterObservationId, "chapter", MpvFormat.Int64);
        _panelFadeStates.Add(HeaderBar, new PanelFadeState(HeaderBar));
        _panelFadeStates.Add(BottomBar, new PanelFadeState(BottomBar));
        _panelFadeStates.Add(QueuePanel, new PanelFadeState(QueuePanel));
        _preferencesStore = new PlayerPreferencesStore(Path.Combine(AppStoragePaths.DataDirectory, "settings.json"));
        _playbackProgressStore = new PlaybackProgressStore(Path.Combine(AppStoragePaths.DataDirectory, "playback.json"),
            loadImmediately: false);
        _queuePlaylistPath = Path.Combine(AppStoragePaths.DataDirectory, "queue.m3u8");
        _playbackProgressLoadTask = _playbackProgressStore.LoadAsync();
        _preferences = _preferencesStore.Load();
        _preferencesUpdateTimer = new DispatcherTimer { Interval = PreferenceUpdateDebounce };
        _preferencesUpdateTimer.Tick += (_, _) => FlushPreferences();
        _queuePersistenceTimer = new DispatcherTimer { Interval = QueuePersistenceDebounce };
        _queuePersistenceTimer.Tick += (_, _) =>
        {
            _queuePersistenceTimer.Stop();
            _queuePersistenceTask = PersistQueueAsync(_queuePersistenceGeneration);
        };
        ApplyPreferences();

        _progressTimer = new DispatcherTimer { Interval = PlaybackPollingRules.ActiveInterval };
        _progressTimer.Tick += (_, _) => UpdatePlaybackStatus();
        _captureNotificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _captureNotificationTimer.Tick += (_, _) =>
        {
            _captureNotificationTimer.Stop();
            CaptureNotification.IsVisible = false;
        };
        _subtitleSyncNotificationTimer = new DispatcherTimer { Interval = SubtitleSyncNotificationDuration };
        _subtitleSyncNotificationTimer.Tick += (_, _) =>
        {
            _subtitleSyncNotificationTimer.Stop();
            SubtitleSyncNotification.IsVisible = false;
        };
        _statusNotificationTimer.Tick += (_, _) =>
        {
            _statusNotificationTimer.Stop();
            StatusNotification.IsVisible = false;
        };
        _volumeNotificationTimer.Tick += (_, _) =>
        {
            _volumeNotificationTimer.Stop();
            VolumeNotification.IsVisible = false;
        };
        _cursorHideTimer.Tick += (_, _) => HideCursor();
        _bottomActivityVisibilityTimer.Tick += (_, _) =>
        {
            _bottomActivityVisibilityTimer.Stop();
            _bottomActivityActive = false;
            UpdateControlVisibility();
        };
        _panelFadeTimer.Tick += (_, _) => UpdatePanelFades();

        QueuePanelPinButton.Click += (_, _) => ToggleQueuePanelPin();
        OpenPlaylistButton.Click += async (_, _) => await OpenPlaylistAsync();
        SavePlaylistButton.Click += async (_, _) => await SavePlaylistAsync();
        QueueItems.AddHandler(PointerPressedEvent, OnQueuePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        QueueItems.AddHandler(PointerMovedEvent, OnQueuePointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        QueueItems.AddHandler(PointerReleasedEvent, OnQueuePointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        QueueItems.PointerCaptureLost += (_, _) => ClearQueueDrag();
        QueueItems.DoubleTapped += (_, e) =>
        {
            var index = GetQueueItemIndex(e.Source);
            if (index >= 0) PlayQueuedVideo(index);
        };
        PlayButton.Click += (_, _) => TogglePlayback();
        SubtitleButton.Click += (_, _) => ToggleSubtitlePicker();
        AudioTrackButton.Click += (_, _) => ToggleAudioTrackPicker();
        InitializeSubtitleAutomation();
        AudioTrackList.SelectionChanged += (_, _) => OnAudioTrackSelectionChanged();
        SubtitleTrackList.SelectionChanged += (_, _) => OnSubtitleTrackSelectionChanged();
        AudioTrackPickerPopup.Closed += (_, _) => UpdateControlVisibility();
        SubtitlePickerPopup.Closed += (_, _) =>
        {
            _bottomHoverActive = BottomBar.IsPointerOver;
            UpdateControlVisibility();
        };
        HardwareAccelerationButton.Click += (_, _) => ToggleHardwareAcceleration();
        SettingsButton.Click += (_, _) => ToggleSettings();
        HeaderBar.ContextMenu = CreateAutoHideContextMenu(isHeader: true);
        BottomBar.ContextMenu = CreateAutoHideContextMenu(isHeader: false);
        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaximizeButton.Click += (_, _) => ToggleMaximize();
        CloseButton.Click += (_, _) => Close();
        VolumeSlider.ValueChanged += (_, e) => SetVolume((int)e.NewValue);
        VolumeSlider.AddHandler(PointerWheelChangedEvent, OnVolumeSliderWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
        InitializeTimeline();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        RootPanel.AddHandler(PointerMovedEvent, OnRootPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        RootPanel.AddHandler(PointerPressedEvent, OnRootPointerActivity, RoutingStrategies.Tunnel, handledEventsToo: true);
        RootPanel.AddHandler(PointerWheelChangedEvent, OnRootPointerActivity, RoutingStrategies.Tunnel, handledEventsToo: true);
        RootPanel.PointerExited += OnRootPointerExited;
        TitleDragRegion.PointerPressed += OnTitleBarPointerPressed;
        AddHandler(PointerPressedEvent, OnResizePointerPressed, RoutingStrategies.Tunnel);
        Opened += OnOpened;
        PropertyChanged += OnWindowPropertyChanged;
        Closed += async (_, _) =>
        {
            _timelineClosing = true;
            CancelSubtitleAutomation();
            _subtitleHttpClient.Dispose();
            _queueScanGeneration++;
            _queueScanCancellation?.Cancel();
            _loadRequestGeneration++;
            CancelPendingPlaylistLoad();
            EndTimelineDrag();
            _seekTimer.Stop();
            SaveCurrentProgress(force: true);
            _keyboardHook?.Dispose();
            _mouseHook?.Dispose();
            _progressTimer.Stop();
            _preferencesUpdateTimer.Stop();
            _queuePersistenceTimer.Stop();
            FlushPreferences();
            Player.StartFile -= OnPlayerStartFile;
            Player.EndFile -= OnPlayerEndFile;
            Player.PropertyChanged -= OnPlayerPropertyChanged;
            Player.UnobserveProperty(TrackListObservationId);
            Player.UnobserveProperty(ChapterListObservationId);
            Player.UnobserveProperty(ChapterObservationId);
            _captureNotificationTimer.Stop();
            _volumeNotificationTimer.Stop();
            _captureThumbnail?.Dispose();
            _captureThumbnail = null;
            _subtitleSyncNotificationTimer.Stop();
            _cursorHideTimer.Stop();
            _bottomActivityVisibilityTimer.Stop();
            _panelFadeTimer.Stop();
            await _previewTask;
            await _captureTask;
            try
            {
                await _playbackProgressStore.FlushAsync();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLogger.Error("Playback progress could not be saved during shutdown", exception);
            }
            _queuePersistenceTask = PersistQueueAsync(++_queuePersistenceGeneration);
            await _queuePersistenceTask;
            ClearSeekPreview();
            ResetPreviewCache();
            Player.Dispose();
        };
    }

    private void ApplyPreferences()
    {
        if (Application.Current is { } app)
            app.Resources["AccentBrush"] = new SolidColorBrush(Color.Parse(_preferences.AccentColor));
        VolumeSlider.Value = _preferences.Volume;
        _isMuted = _preferences.Muted || _preferences.Volume == 0;
        VolumeLabel.Text = _isMuted ? UiText.Muted : $"{_preferences.Volume}%";
        _playbackSpeed = PlaybackSpeedRules.Clamp(_preferences.PlaybackSpeed);
        UpdateSpeedLabel();
        UpdateSubtitleDelayTooltip();
        Player.ScreenshotTagColorspace.Set(true);
        Player.ScreenshotHighBitDepth.Set(true);
        Player.SetProperty("volume-max", (long)PlayerPreferences.MaximumVolume);
        Player.SetProperty("hwdec", _preferences.HardwareAcceleration ? "auto" : "no");
        ApplyVideoDisplayPreferences();
        ApplySubtitleCodepage();
        ApplySubtitleAppearancePreferences();
        Player.SetProperty("sub-visibility", _preferences.SubtitlesVisible);
        Player.SetProperty("volume", (long)_preferences.Volume);
        Player.SetProperty("mute", _isMuted);
        Player.SetProperty("speed", _playbackSpeed);
        UpdateHardwareAccelerationButton();
        UpdateControlVisibility();
        UpdateQueuePanel();
        if (AutoFindSubtitlesCheckBox is not null && AutoFindSubtitlesCheckBox.IsChecked != _preferences.AutoFindSubtitles)
            AutoFindSubtitlesCheckBox.IsChecked = _preferences.AutoFindSubtitles;
        foreach (var (menuItem, isHeader) in _autoHideMenuItems)
            menuItem.IsChecked = isHeader ? _preferences.AutoHideHeader : _preferences.AutoHideBottom;
    }

    private void ApplySubtitleAppearancePreferences()
    {
        Player.SetPropertyString("sub-font", SubtitleAppearanceRules.NormalizeFontFamily(_preferences.SubtitleFontFamily));
        Player.SetProperty("sub-font-size", (long)_preferences.SubtitleFontSize);
        Player.SetProperty("sub-bold", _preferences.SubtitleBold);
        Player.SetProperty("sub-italic", _preferences.SubtitleItalic);
        Player.SetPropertyString("sub-color", SubtitleAppearanceRules.NormalizeTextColor(_preferences.SubtitleTextColor));
        Player.SetProperty("sub-border-style", _preferences.SubtitleBackgroundEnabled ? "background-box" : "outline-and-shadow");
        Player.SetProperty("sub-back-color", $"#{(int)Math.Round(_preferences.SubtitleBackgroundOpacity * 255.0 / 100):X2}000000");
        Player.SetProperty("sub-outline-size", _preferences.SubtitleOutlineSize);
        Player.SetProperty("sub-shadow-offset", _preferences.SubtitleShadowOffset);
        Player.SetProperty("sub-pos", (long)_preferences.SubtitlePosition);
        Player.SetProperty("sub-align-x", SubtitleAppearanceRules.NormalizeHorizontalAlignment(_preferences.SubtitleHorizontalAlignment));
        Player.SetProperty("sub-margin-y", (long)_preferences.SubtitleMargin);
        Player.SetProperty("sub-ass-override", "force");
    }

    private void ApplySubtitleCodepage()
    {
        var codepage = SubtitleCodepageRules.Normalize(_preferences.SubtitleCodepage);
        if (string.Equals(_appliedSubtitleCodepage, codepage, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            Player.SetPropertyString("sub-codepage", codepage);
            _appliedSubtitleCodepage = codepage;
            if (_currentVideo is null) return;

            var trackCount = Math.Clamp(Player.GetProperty<int>("track-list/count"), 0, 256);
            for (var index = 0; index < trackCount; index++)
            {
                if (!string.Equals(ReadTrackProperty(index, "type", string.Empty), "sub", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(ReadTrackProperty(index, "external-filename", string.Empty)))
                    continue;
                var trackId = ReadTrackProperty(index, "id", -1);
                if (trackId >= 0) Player.SubReload(trackId).Invoke();
            }
        }
        catch (MpvException exception)
        {
            AppLogger.Error("Subtitle character encoding could not be applied", exception);
        }
    }

    private async Task OpenVideoAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiText.OpenVideoOrPlaylist,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(UiText.VideoFiles)
            {
                Patterns = [.. MediaPathRules.VideoFilePickerPatterns, .. M3uPlaylistFile.FilePickerPatterns]
            }]
        });
        if (files.Count == 0) return;
        var path = files[0].Path.LocalPath;
        if (M3uPlaylistFile.IsPlaylistPath(path)) await LoadPlaylistAsync(path);
        else LoadVideo(path);
    }

    private async Task OpenPlaylistAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiText.OpenPlaylist,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(UiText.M3UPlaylists)
            {
                Patterns = M3uPlaylistFile.FilePickerPatterns
            }]
        });
        if (files.Count > 0) await LoadPlaylistAsync(files[0].Path.LocalPath);
    }

    private Task LoadPlaylistAsync(string playlistPath) => LoadPlaylistAsync([playlistPath], [], []);

    private async Task LoadPlaylistAsync(
        IReadOnlyList<string> playlistPaths,
        IReadOnlyList<string> additionalVideoPaths,
        IReadOnlyList<string> subtitlePaths)
    {
        var requestGeneration = Interlocked.Increment(ref _loadRequestGeneration);
        var cancellation = new CancellationTokenSource();
        var previousCancellation = Interlocked.Exchange(ref _playlistLoadCancellation, cancellation);
        previousCancellation?.Cancel();

        try
        {
            await LoadPlaylistCoreAsync(
                playlistPaths, additionalVideoPaths, subtitlePaths, requestGeneration, cancellation.Token);
        }
        finally
        {
            Interlocked.CompareExchange(ref _playlistLoadCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private async Task LoadPlaylistCoreAsync(
        IReadOnlyList<string> playlistPaths,
        IReadOnlyList<string> additionalVideoPaths,
        IReadOnlyList<string> subtitlePaths,
        long requestGeneration,
        CancellationToken cancellationToken)
    {
        var playlistEntries = new List<string>();
        try
        {
            foreach (var playlistPath in playlistPaths)
            {
                var entries = await M3uPlaylistFile.LoadAsync(playlistPath, cancellationToken);
                if (entries.Count > M3uPlaylistFile.MaximumEntryCount - playlistEntries.Count)
                    throw new InvalidDataException(
                        $"A combined playlist can contain at most {M3uPlaylistFile.MaximumEntryCount:N0} videos.");
                playlistEntries.AddRange(entries);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or DecoderFallbackException or System.Security.SecurityException)
        {
            if (_timelineClosing || requestGeneration != Volatile.Read(ref _loadRequestGeneration)) return;
            AppLogger.Error("Playlist could not be opened", exception);
            await ShowMessageAsync(UiText.CouldNotOpenPlaylist, exception.Message);
            return;
        }

        if (_timelineClosing || requestGeneration != Volatile.Read(ref _loadRequestGeneration)) return;

        var validAdditionalVideos = additionalVideoPaths
            .Where(MediaPathRules.IsSupportedVideo)
            .Where(File.Exists)
            .ToArray();
        IReadOnlyList<string> videoPaths;
        try
        {
            videoPaths = QueueOrderRules.CombinePlaylistEntriesAndDroppedVideos(
                playlistEntries, validAdditionalVideos, M3uPlaylistFile.MaximumEntryCount);
        }
        catch (InvalidDataException exception)
        {
            AppLogger.Error("Dropped playlist exceeds the queue limit", exception);
            await ShowMessageAsync(UiText.PlaylistIsTooLong, exception.Message);
            return;
        }

        if (videoPaths.Count == 0)
        {
            await ShowMessageAsync(UiText.NoVideosInPlaylist, UiText.NoPlayableLocalVideosWereFoundInThe);
            return;
        }

        LoadVideo(videoPaths[0], afterLoad: () =>
        {
            _queuedVideos.Clear();
            _queuedVideos.AddRange(videoPaths.Skip(1));
            _queueItemsDirty = true;
            UpdateQueuePanel();
            ScheduleQueuePersistence();
            foreach (var subtitlePath in subtitlePaths) AddSubtitle(subtitlePath);
        }, scanSiblingVideos: false);
    }

    private async Task SavePlaylistAsync()
    {
        var files = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = UiText.SavePlaybackQueue,
            DefaultExtension = "m3u8",
            SuggestedFileName = UiText.PlaybackQueueM3u8,
            FileTypeChoices = [new FilePickerFileType(UiText.M3UPlaylist)
            {
                Patterns = ["*.m3u8", "*.m3u"]
            }]
        });
        if (files is null) return;

        var videoPaths = _currentVideo is null
            ? _queuedVideos.ToArray()
            : new[] { _currentVideo }.Concat(_queuedVideos).ToArray();
        try
        {
            await M3uPlaylistFile.SaveAsync(files.Path.LocalPath, videoPaths);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            AppLogger.Error("Playlist could not be saved", exception);
            await ShowMessageAsync(UiText.CouldNotSavePlaylist, exception.Message);
        }
    }

    private async Task OpenSubtitleAsync()
    {
        if (_currentVideo is null)
        {
            await ShowMessageAsync(UiText.OpenAVideoFirst, UiText.OpenAVideoOrDropOneOntoThe);
            return;
        }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiText.AddExternalSubtitle,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(UiText.SubtitleFiles)
            {
                Patterns = ["*.srt", "*.ass", "*.ssa", "*.vtt", "*.sub", "*.idx"]
            }]
        });
        if (files.Count > 0) AddSubtitle(files[0].Path.LocalPath);
    }

    internal void OpenVideosFromAnotherInstance(IReadOnlyList<string> paths)
    {
        Dispatcher.UIThread.Post(() => OpenVideoPaths(paths, bringToFront: true));
    }

    private void OpenVideoPaths(IReadOnlyList<string> paths, bool bringToFront)
    {
        if (_timelineClosing) return;
        var validPaths = MediaPathRules.ResolveExistingVideoPaths(paths);
        if (validPaths.Count > 0)
        {
            LoadVideo(validPaths[0], afterLoad: () =>
            {
                var addedCount = QueueOrderRules.AppendUniquePaths(_queuedVideos, validPaths.Skip(1));
                _queueItemsDirty |= addedCount > 0;
                UpdateQueuePanel();
                if (addedCount > 0) ScheduleQueuePersistence();
            });
        }

        if (!bringToFront) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void LoadVideo(
        string path,
        bool preserveQueue = false,
        Action? afterLoad = null,
        bool scanSiblingVideos = true)
    {
        if (!File.Exists(path) || !MediaPathRules.IsSupportedVideo(path)) return;
        var requestGeneration = ++_loadRequestGeneration;
        CancelPendingPlaylistLoad();
        if (!_previewTask.IsCompleted || !_playbackProgressLoadTask.IsCompleted || !_captureTask.IsCompleted)
        {
            if (!_previewTask.IsCompleted) EndTimelineDrag();
            _ = LoadVideoWhenReadyAsync(path, preserveQueue, requestGeneration, afterLoad, scanSiblingVideos);
            return;
        }

        LoadVideoCore(path, preserveQueue, scanSiblingVideos);
        afterLoad?.Invoke();
    }

    private async Task LoadVideoWhenReadyAsync(
        string path,
        bool preserveQueue,
        long requestGeneration,
        Action? afterLoad,
        bool scanSiblingVideos)
    {
        try { await _playbackProgressLoadTask; }
        catch (Exception exception) { AppLogger.Error("Playback history could not be loaded", exception); }
        try { await _previewTask; }
        catch (Exception exception) { AppLogger.Error("Timeline preview did not stop cleanly before loading another video", exception); }
        try { await _captureTask; }
        catch (Exception exception) { AppLogger.Error("Frame capture did not stop cleanly before loading another video", exception); }

        if (_timelineClosing || requestGeneration != _loadRequestGeneration) return;
        LoadVideoCore(path, preserveQueue, scanSiblingVideos);
        afterLoad?.Invoke();
    }

    private void LoadVideoCore(string path, bool preserveQueue, bool scanSiblingVideos)
    {
        EndTimelineDrag();
        ResetPreviewCache();
        SaveCurrentProgress(force: true);
        if (!preserveQueue && _queuedVideos.Count > 0)
        {
            _queuedVideos.Clear();
            _queueItemsDirty = true;
        }
        UpdateQueuePanel();
        Interlocked.Increment(ref _mediaLoadGeneration);
        Interlocked.Increment(ref _subtitleSelectionGeneration);
        CancelSubtitleAutomation();
        _subtitleAutomationStarted = false;
        _subtitleMediaReady = false;
        _onlineSubtitleSearchPending = _preferences.AutoFindSubtitles;
        FindSubtitleButton.IsEnabled = false;
        SynchronizeSubtitleButton.IsEnabled = false;
        Volatile.Write(ref _activePlaylistEntry, null);
        _currentVideo = path;
        _videoDuration = 0;
        _queueScanCancellation?.Cancel();
        var queueScanGeneration = ++_queueScanGeneration;
        CancellationTokenSource? queueScanCancellation = null;
        if (!preserveQueue && scanSiblingVideos)
        {
            queueScanCancellation = new CancellationTokenSource();
            _queueScanCancellation = queueScanCancellation;
        }
        _trackListSignature = null;
        _trackMetadataCache = null;
        _trackMetadataCacheCount = -1;
        _trackMetadataRefreshGate.MarkPendingForFallback();
        _chapterListRefreshGate.MarkPendingForFallback();
        _chapterCount = 0;
        SetTimelineChapters(Array.Empty<ChapterTimelineMarker>());
        _hasAppliedTrackPreferences = false;
        _loadedSubtitles.Clear();
        _pendingResume = _playbackProgressStore.Get(path);
        _selectedExternalSubtitlePath = null;
        _selectedSubtitleTrackId = _pendingResume?.SubtitleTrackId;
        _pendingResumeStartPositionSeconds = null;
        _pendingResumeTargetPositionSeconds = null;
        _subtitleDelaySeconds = 0;
        _endOfFileHandled = false;
        _playbackStatusUnavailableLogged = false;
        _activeVideoDecoderLogged = false;
        _playbackStatusUnavailableSince = DateTime.MinValue;
        Player.SetProperty("sub-delay", 0.0);
        Player.LoadFile(path).Invoke();
        RequestChapterListRefresh();
        Player.SetProperty("pause", false);
        _isPlaying = true;
        UpdatePlaybackPollingInterval();
        _progressTimer.Start();
        var fileName = Path.GetFileName(path);
        WindowFileLabel.Text = fileName;
        ToolTip.SetTip(WindowFileLabel, fileName);
        Title = $"{fileName} - Pantheon";
        if (queueScanCancellation is not null)
            _ = AddSiblingVideosToQueueAsync(path, queueScanGeneration, queueScanCancellation);
        UpdateQueuePanel();
        DropHint.IsVisible = false;
        UpdatePlaybackButton();
        SeekSlider.IsEnabled = true;
        _playbackSpeed = PlaybackSpeedRules.Clamp(_pendingResume?.PlaybackSpeed ?? _preferences.PlaybackSpeed);
        Player.SetProperty("speed", _playbackSpeed);
        Player.SetProperty("volume", (long)_preferences.Volume);
        Player.SetProperty("mute", _preferences.Muted || _preferences.Volume == 0);
        Player.SetProperty("sub-visibility", _preferences.SubtitlesVisible);
        UpdateSpeedLabel();
        var matchingLocalSubtitle = MediaPathRules.FindMatchingSubtitleBesideVideo(path);
        if (matchingLocalSubtitle is not null)
        {
            AddSubtitle(matchingLocalSubtitle, showInPlayer: true, isAutomatic: true);
            ShowStatusNotification($"Klasördeki '{Path.GetFileName(matchingLocalSubtitle)}' altyazısı ile eşleşti");
        }
        ScheduleQueuePersistence();
    }

    private void OnPlayerStartFile(object? sender, MpvStartFileEventArgs eventArgs)
    {
        if (_timelineClosing) return;
        var activeEntry = new ActivePlaylistEntry(
            eventArgs.PlayListEntryId,
            Volatile.Read(ref _mediaLoadGeneration));
        Volatile.Write(ref _activePlaylistEntry, activeEntry);
    }

    private void OnPlayerEndFile(object? sender, MpvEndFileEventArgs eventArgs)
    {
        if (eventArgs.Reason is not (HanumanInstitute.LibMpv.Core.MpvEndFileReason.EndOfFile
            or HanumanInstitute.LibMpv.Core.MpvEndFileReason.Error)) return;
        var endedEntry = Volatile.Read(ref _activePlaylistEntry);
        if (endedEntry is null || endedEntry.EntryId != eventArgs.PlayListEntryId) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_timelineClosing
                || _currentVideo is null
                || Volatile.Read(ref _mediaLoadGeneration) != endedEntry.LoadGeneration
                || !ReferenceEquals(Volatile.Read(ref _activePlaylistEntry), endedEntry)) return;

            if (eventArgs.Reason == HanumanInstitute.LibMpv.Core.MpvEndFileReason.EndOfFile)
            {
                if (_isPlaying && !_isDraggingSeek && !_endOfFileHandled)
                    PlayNextQueuedVideo();
                else
                {
                    _endOfFileHandled = true;
                    _isPlaying = false;
                    UpdatePlaybackButton();
                    _progressTimer.Stop();
                    UpdatePlaybackPollingInterval();
                }
                return;
            }

            _endOfFileHandled = true;
            _isPlaying = false;
            UpdatePlaybackButton();
            SeekSlider.IsEnabled = false;
            _progressTimer.Stop();
            UpdatePlaybackPollingInterval();
            AppLogger.Error("Media playback failed", new InvalidOperationException(
                $"libmpv error {eventArgs.Error} for '{_currentVideo}'."));
            _ = ShowMessageAsync(UiText.CouldNotOpenVideo, UiText.UnsupportedMediaHelp);
        });
    }

    private void AddSubtitle(string path, bool showInPlayer = true, bool isAutomatic = false)
    {
        if (_currentVideo is null || !MediaPathRules.IsSupportedSubtitle(path)) return;
        if (!File.Exists(path))
        {
            if (!isAutomatic)
                _ = ShowMessageAsync(UiText.CouldNotOpenSubtitle, UiText.TheFileCouldNotBeFoundSelectIt);
            return;
        }

        if (!isAutomatic)
        {
            CancelSubtitleAutomation();
            Interlocked.Increment(ref _subtitleSelectionGeneration);
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var existingTrackId = FindLoadedSubtitleId(fullPath);
            if (existingTrackId is { } trackId)
            {
                Player.SetProperty("sid", (long)trackId);
            }
            else if (_loadedSubtitles.Contains(fullPath))
            {
                if (!isAutomatic)
                {
                    Interlocked.Increment(ref _subtitleSelectionGeneration);
                }
                return;
            }
            else
            {
                Player.SubAdd(fullPath, LoadOption.Select).Invoke();
            }

            _loadedSubtitles.Add(fullPath);
            _selectedExternalSubtitlePath = fullPath;
            _selectedSubtitleTrackId = null;
            if (!isAutomatic)
            {
                Interlocked.Increment(ref _subtitleSelectionGeneration);
            }
            _preferences = _preferences with { SubtitlesVisible = showInPlayer };
            Player.SetProperty("sub-visibility", showInPlayer);
            UpdateSubtitleDelayTooltip();
            SavePreferences(_preferences);
            if (!isAutomatic)
            {
                SaveCurrentProgress(force: true);
                _subtitleAutomationStarted = false;
            }
        }
        catch (Exception exception) when (exception is MpvException or IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            AppLogger.Error("External subtitle could not be loaded", exception);
            if (!isAutomatic)
                _ = ShowMessageAsync(UiText.CouldNotOpenSubtitle, UiText.UnsupportedMediaHelp);
        }
    }

    private int? FindLoadedSubtitleId(string fullPath)
    {
        var trackCount = Math.Clamp(Player.GetProperty<int>("track-list/count"), 0, 256);
        for (var index = 0; index < trackCount; index++)
        {
            if (!string.Equals(ReadTrackProperty(index, "type", string.Empty), "sub", StringComparison.Ordinal)) continue;
            var loadedPath = ReadTrackProperty(index, "external-filename", string.Empty);
            if (!PathsEqual(loadedPath, fullPath)) continue;
            var id = ReadTrackProperty(index, "id", 0);
            if (id > 0) return id;
        }
        return null;
    }

    private void ToggleSubtitles()
    {
        CancelSubtitleAutomation();
        _preferences = _preferences with { SubtitlesVisible = !_preferences.SubtitlesVisible };
        Player.SetProperty("sub-visibility", _preferences.SubtitlesVisible);
        UpdateSubtitleDelayTooltip();
        SavePreferences(_preferences);
    }

    private void TogglePlayback()
    {
        if (_currentVideo is null) return;
        _isPlaying = !_isPlaying;
        Player.SetProperty("pause", !_isPlaying);
        UpdatePlaybackButton();
        UpdatePlaybackPollingInterval();
    }

    private void UpdatePlaybackButton()
    {
        var action = _isPlaying ? UiText.Pause : UiText.Play;
        PlayButton.Content = _isPlaying ? "Ⅱ" : "▶";
        AutomationProperties.SetName(PlayButton, action);
        ToolTip.SetTip(PlayButton, action);
    }

    private void StepFrame(bool forward)
    {
        if (_currentVideo is null) return;
        _pendingResume = null;
        _pendingResumeStartPositionSeconds = null;
        _pendingResumeTargetPositionSeconds = null;
        Player.SetProperty("pause", true);
        _isPlaying = false;
        UpdatePlaybackButton();
        UpdatePlaybackPollingInterval();
        if (forward) Player.FrameStep().Invoke();
        else Player.FrameBackStep().Invoke();
        ShowTransientNotification("↪", forward ? "+1 frame" : "−1 frame");
        UpdatePlaybackStatus();
    }

    private void Seek(double seconds)
    {
        if (_currentVideo is null) return;
        _pendingResume = null;
        _pendingResumeStartPositionSeconds = null;
        _pendingResumeTargetPositionSeconds = null;
        var now = DateTime.UtcNow;
        var currentPosition = Player.GetProperty<double>("time-pos");
        var basePosition = _discreteSeekTarget is { } previousTarget
            && now - _lastDiscreteSeek <= DiscreteSeekBurstDuration
                ? previousTarget
                : currentPosition;
        var duration = Player.GetProperty<double>("duration");
        var targetPosition = basePosition + seconds;
        if (double.IsFinite(duration) && duration > 0)
            targetPosition = Math.Clamp(targetPosition, 0, duration);
        _discreteSeekTarget = targetPosition;
        _lastDiscreteSeek = now;
        Player.Seek(targetPosition, SeekOption.Absolute | SeekOption.Exact).Invoke();
        ShowSeekNotification(targetPosition - currentPosition);
    }

    private void ResetDiscreteSeekBurst()
    {
        _discreteSeekTarget = null;
        _lastDiscreteSeek = DateTime.MinValue;
    }

    private void ShowBottomTemporarily()
    {
        if (!_preferences.AutoHideBottom) return;
        _bottomActivityActive = true;
        _bottomActivityVisibilityTimer.Stop();
        _bottomActivityVisibilityTimer.Start();
        UpdateControlVisibility();
    }

    private Task CaptureFrameAsync()
    {
        if (_timelineClosing || _currentVideo is null || !_captureTask.IsCompleted) return Task.CompletedTask;
        if (_isDraggingSeek) EndTimelineDrag();
        SetCaptureThumbnail(null);
        ShowCaptureNotification(UiText.CapturingFrame, keepVisible: true);
        _captureTask = CaptureFrameCoreAsync(_currentVideo);
        return _captureTask;
    }

    private async Task CaptureFrameCoreAsync(string videoPath)
    {
        try
        {
            var saveMode = ScreenshotSaveModeRules.Normalize(_preferences.ScreenshotSaveMode);
            var output = MediaPathRules.CreateCapturePath(
                videoPath,
                GetCaptureDirectory(),
                DateTimeOffset.Now,
                saveMode);
            await _previewTask;
            if (_timelineClosing || !PathsEqual(_currentVideo, videoPath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await VideoSurface.CaptureFrameAsync(output, optimized: saveMode == ScreenshotSaveModeRules.Optimized);
            if (_timelineClosing) return;
            var thumbnail = await Task.Run(() =>
            {
                using var stream = File.OpenRead(output);
                return Bitmap.DecodeToWidth(stream, 160);
            });
            if (_timelineClosing)
            {
                thumbnail.Dispose();
                return;
            }
            SetCaptureThumbnail(thumbnail);
            ShowCaptureNotification(UiText.FrameSaved);
            CaptureNotificationIndicator.IsVisible = false;
            CaptureNotificationText.IsVisible = false;
        }
        catch (Exception exception)
        {
            AppLogger.Error("Screenshot capture failed", exception);
            if (!_timelineClosing) CaptureNotification.IsVisible = false;
            if (!_timelineClosing) await ShowMessageAsync(UiText.CouldNotCaptureFrame, exception.Message);
        }
    }

    private void ShowCaptureNotification(string message, bool keepVisible = false)
    {
        CaptureNotificationText.Text = message;
        CaptureNotificationIndicator.IsVisible = true;
        CaptureNotificationText.IsVisible = true;
        CaptureNotification.IsVisible = true;
        _captureNotificationTimer.Stop();
        if (!keepVisible) _captureNotificationTimer.Start();
    }

    private void SetCaptureThumbnail(Bitmap? thumbnail)
    {
        _captureThumbnail?.Dispose();
        _captureThumbnail = thumbnail;
        CaptureThumbnail.Source = thumbnail;
        CaptureThumbnailBorder.IsVisible = thumbnail is not null;
    }

    private void ShowStatusNotification(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusNotificationText.Text = message;
            StatusNotification.IsVisible = true;
            _statusNotificationTimer.Stop();
            _statusNotificationTimer.Start();
        });
    }

    private void SetVolume(int volume)
    {
        volume = Math.Clamp(volume, 0, PlayerPreferences.MaximumVolume);
        var volumeChanged = _preferences.Volume != volume;
        _preferences = _preferences with { Volume = volume };
        _isMuted = volume == 0;
        _preferences = _preferences with { Muted = _isMuted };
        VolumeLabel.Text = _isMuted ? UiText.Muted : $"{volume}%";
        if ((int)VolumeSlider.Value != volume) VolumeSlider.Value = volume;
        Player.SetProperty("volume", (long)volume);
        Player.SetProperty("mute", volume == 0);
        SavePreferences(_preferences);
        if (volumeChanged && _currentVideo is not null)
            ShowVolumeNotification();
    }

    private void ShowVolumeNotification()
    {
        ShowTransientNotification(_isMuted ? "🔇" : "🔊", _isMuted ? UiText.Muted : $"{_preferences.Volume}%");
    }

    private void ShowSeekNotification(double offsetSeconds)
    {
        if (Math.Abs(offsetSeconds) < 0.5) return;

        var roundedOffset = (int)Math.Round(Math.Abs(offsetSeconds), MidpointRounding.AwayFromZero);
        var magnitude = roundedOffset < 60
            ? $"{roundedOffset}s"
            : MediaTimeFormatter.FormatPreviewTime(roundedOffset);
        var direction = offsetSeconds > 0 ? "+" : "−";
        ShowTransientNotification("↪", $"{direction}{magnitude}");
    }

    private void ShowTransientNotification(string icon, string text)
    {
        VolumeNotificationText.Text = text;
        VolumeNotificationIcon.Text = icon;
        VolumeNotification.IsVisible = true;
        _volumeNotificationTimer.Stop();
        _volumeNotificationTimer.Start();
    }

    private void ToggleMute()
    {
        _isMuted = !_isMuted;
        _preferences = _preferences with { Muted = _isMuted };
        Player.SetProperty("mute", _isMuted);
        VolumeLabel.Text = _isMuted ? UiText.Muted : $"{_preferences.Volume}%";
        SavePreferences(_preferences);
        if (_currentVideo is not null)
            ShowVolumeNotification();
    }

    private void OnVolumeSliderWheel(object? sender, PointerWheelEventArgs e)
    {
        var nextVolume = VolumeWheelRules.Adjust((int)VolumeSlider.Value, e.Delta.Y);
        if (nextVolume == (int)VolumeSlider.Value) return;
        VolumeSlider.Value = nextVolume;
        e.Handled = true;
    }

    private void ToggleHardwareAcceleration() => SavePreferences(_preferences with
    {
        HardwareAcceleration = !_preferences.HardwareAcceleration
    });

    private void SavePreferences(PlayerPreferences preferences)
    {
        if (_preferences == preferences) return;
        var autoFindChanged = _preferences.AutoFindSubtitles != preferences.AutoFindSubtitles;
        _preferences = preferences;
        _preferencesDirty = true;
        _preferencesUpdateTimer.Stop();
        _preferencesUpdateTimer.Start();
        if (autoFindChanged)
        {
            if (AutoFindSubtitlesCheckBox is not null && AutoFindSubtitlesCheckBox.IsChecked != _preferences.AutoFindSubtitles)
                AutoFindSubtitlesCheckBox.IsChecked = _preferences.AutoFindSubtitles;

            if (!_preferences.AutoFindSubtitles)
            {
                _onlineSubtitleSearchPending = false;
                CancelSubtitleAutomation();
            }
            else if (_preferences.AutoFindSubtitles && _selectedExternalSubtitlePath is null && _selectedSubtitleTrackId is null or not 0)
            {
                _onlineSubtitleSearchPending = true;
                _subtitleAutomationStarted = false;
                TryStartSubtitleAutomation();
            }
        }
    }

    private void FlushPreferences()
    {
        _preferencesUpdateTimer.Stop();
        if (!_preferencesDirty) return;
        ApplyPreferences();
        try
        {
            _preferencesStore.Save(_preferences);
            _preferencesDirty = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogger.Error("Player preferences could not be saved", exception);
        }
    }

    private async Task ShowSettingsAsync()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        try
        {
            _settingsWindow = new SettingsWindow(_preferences, SavePreferences);
            await _settingsWindow.ShowDialog(this);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Settings window failed", exception);
            await ShowMessageAsync(UiText.CouldNotOpenSettings, UiText.ErrorDetailsWereSavedToTheDiagnosticLog);
        }
        finally
        {
            _settingsWindow = null;
        }
    }

    private void ToggleSettings()
    {
        if (_settingsWindow is { } settingsWindow)
        {
            settingsWindow.Close();
            return;
        }
        _ = ShowSettingsAsync();
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var close = new Button { Content = UiText.Close, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var dialog = new Window
        {
            Title = title,
            Content = new StackPanel
            {
                Spacing = 14,
                Margin = new Thickness(18),
                Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, close }
            },
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private void UpdatePlaybackStatus()
    {
        if (_currentVideo is null) return;
        try
        {
            var position = Player.GetProperty<double>("time-pos");
            if (!double.IsFinite(position)) return;
            position = Math.Max(0, position);
            _playbackStatusUnavailableSince = DateTime.MinValue;
            _playbackStatusUnavailableLogged = false;
            if (!double.IsFinite(_videoDuration) || _videoDuration <= 0)
            {
                var duration = Player.GetProperty<double>("duration");
                if (double.IsFinite(duration) && duration > 0)
                {
                    _videoDuration = duration;
                    RenderChapterMarkers();
                }
            }
            var videoDuration = _videoDuration;
            LogActiveVideoDecoder();
            RestorePendingProgress(videoDuration);
            if (BottomBar.IsVisible && !_isDraggingSeek && videoDuration > 0)
            {
                _updatingTimeline = true;
                SeekSlider.Value = Math.Clamp(position / videoDuration * 100, 0, 100);
                _updatingTimeline = false;
            }
            if (BottomBar.IsVisible) UpdatePlaybackTimeDisplay(position, videoDuration);
            if (_trackMetadataRefreshGate.IsPending)
                RefreshTrackSelectors();
            TryStartSubtitleAutomation();
            if (_chapterListRefreshGate.IsPending)
                RequestChapterListRefresh();
            if ((DateTime.UtcNow - _lastProgressSave).TotalSeconds >= 8)
                SaveCurrentProgress(positionSeconds: position, durationSeconds: videoDuration);
        }
        catch (MpvException exception)
        {
            // libmpv briefly reports unavailable properties while replacing or initializing a file.
            if (_playbackStatusUnavailableSince == DateTime.MinValue)
                _playbackStatusUnavailableSince = DateTime.UtcNow;
            else if (!_playbackStatusUnavailableLogged
                && DateTime.UtcNow - _playbackStatusUnavailableSince >= PlaybackStatusFailureLogDelay)
            {
                AppLogger.Error("Playback status is temporarily unavailable", exception);
                _playbackStatusUnavailableLogged = true;
            }
        }
    }

    private void UpdatePlaybackPollingInterval() =>
        _progressTimer.Interval = PlaybackPollingRules.GetInterval(_isPlaying, _isDraggingSeek);

    private void UpdatePlaybackTimeDisplay(double position, double duration)
    {
        var positionSecond = MediaTimeFormatter.GetPlaybackSecond(position);
        var durationSecond = MediaTimeFormatter.GetPlaybackSecond(duration);
        if (positionSecond == _lastDisplayedPositionSecond
            && durationSecond == _lastDisplayedDurationSecond
            && _playbackSpeed == _lastDisplayedPlaybackSpeed) return;

        TimeLabel.Text = $"{MediaTimeFormatter.FormatPlaybackTime(position)} / {MediaTimeFormatter.FormatPlaybackTime(duration)}";
        ToolTip.SetTip(TimeLabel, UiText.CurrentTimeTotalDuration);
        _lastDisplayedPositionSecond = positionSecond;
        _lastDisplayedDurationSecond = durationSecond;
        _lastDisplayedPlaybackSpeed = _playbackSpeed;
    }

    private void LogActiveVideoDecoder()
    {
        if (_activeVideoDecoderLogged) return;
        try
        {
            var decoder = Player.GetProperty<string>("hwdec-current");
            if (string.IsNullOrWhiteSpace(decoder)) return;
            AppLogger.Information($"Active video decoder: {decoder}");
            _activeVideoDecoderLogged = true;
        }
        catch (MpvException)
        {
            // The property may be unavailable until libmpv finishes opening the video.
        }
    }

    private void RefreshTrackSelectors()
    {
        var trackCount = Math.Clamp(Player.GetProperty<int>("track-list/count"), 0, 256);
        var trackMetadata = _trackMetadataCache;
        var rebuildRequested = _trackMetadataRefreshGate.ConsumePending();
        var rebuiltMetadata = rebuildRequested || trackMetadata is null || _trackMetadataCacheCount != trackCount;
        if (rebuiltMetadata)
        {
            trackMetadata = BuildTrackMetadata(trackCount);
            _trackMetadataCache = trackMetadata;
            _trackMetadataCacheCount = trackCount;
        }

        var audioTracks = new List<MediaTrackOption>();
        var subtitleTracks = new List<MediaTrackOption>();
        foreach (var metadata in trackMetadata!)
        {
            var option = metadata.Option with
            {
                IsSelected = rebuiltMetadata
                    ? metadata.Option.IsSelected
                    : ReadTrackProperty(metadata.Index, "selected", false)
            };
            if (metadata.Type == "audio") audioTracks.Add(option);
            else subtitleTracks.Add(option);
        }

        if (!_hasAppliedTrackPreferences && _subtitleMediaReady && trackCount > 0)
        {
            ApplyPreferredTrack(audioTracks, _preferences.PreferredAudioLanguage,
                _preferences.PreferredAudioTrackIndex, "aid");
            if (_preferences.AudioTrackDisabled) Player.SetPropertyString("aid", "no");
            if (_selectedExternalSubtitlePath is null && _selectedSubtitleTrackId is { } selectedTrackId)
            {
                if (selectedTrackId == 0)
                    Player.SetPropertyString("sid", "no");
                else if (subtitleTracks.Any(track => track.Id == selectedTrackId))
                    Player.SetProperty("sid", (long)selectedTrackId);
                else
                    ApplyPreferredTrack(subtitleTracks, _preferences.PreferredSubtitleLanguage,
                        _preferences.PreferredSubtitleTrackIndex, "sid");
            }
            else if (_selectedExternalSubtitlePath is null)
                ApplyPreferredTrack(subtitleTracks, _preferences.PreferredSubtitleLanguage,
                    _preferences.PreferredSubtitleTrackIndex, "sid");
            Player.SetProperty("sub-visibility", _preferences.SubtitlesVisible);
            _hasAppliedTrackPreferences = true;
        }

        var signature = string.Join('|', audioTracks.Select(track => $"{track.Id}:{track.Label}:{track.IsSelected}"))
            + "||" + string.Join('|', subtitleTracks.Select(track => $"{track.Id}:{track.Label}:{track.IsSelected}"));
        if (string.Equals(_trackListSignature, signature, StringComparison.Ordinal)) return;
        _trackListSignature = signature;
        AppLogger.Information(
            $"Media tracks changed: audio=[{string.Join(", ", audioTracks.Select(track => track.Label))}], "
            + $"subtitles=[{string.Join(", ", subtitleTracks.Select(track => track.Label))}].");

        _updatingTrackSelectors = true;
        try
        {
            PopulateAudioTrackList(audioTracks);
            PopulateSubtitleTrackList(subtitleTracks);
            AudioTrackButton.IsVisible = audioTracks.Count > 1;
            if (AudioTrackPickerPopup.IsOpen) AudioTrackPickerPopup.IsOpen = audioTracks.Count > 1;
            if (SubtitlePickerPopup.IsOpen) RefreshSubtitlePickerContent();
        }
        finally
        {
            _updatingTrackSelectors = false;
        }
    }

    private void OnPlayerPropertyChanged(object? sender, MpvPropertyEventArgs eventArgs)
    {
        if (string.Equals(eventArgs.Name, "track-list", StringComparison.Ordinal))
        {
            if (_trackMetadataRefreshGate.Request()) PostTrackMetadataRefresh();
        }
        else if (string.Equals(eventArgs.Name, "chapter-list", StringComparison.Ordinal))
        {
            RequestChapterListRefresh();
        }
    }

    private void RequestChapterListRefresh()
    {
        if (_chapterListRefreshGate.Request()) PostChapterListRefresh();
    }

    private void PostChapterListRefresh()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var refreshFailed = false;
            try
            {
                if (!_timelineClosing && _chapterListRefreshGate.IsPending)
                    RefreshChapters();
            }
            catch (MpvException)
            {
                _chapterListRefreshGate.MarkPendingForFallback();
                refreshFailed = true;
            }
            finally
            {
                if (_chapterListRefreshGate.Complete(allowReschedule: !refreshFailed && !_timelineClosing))
                    PostChapterListRefresh();
            }
        }, DispatcherPriority.Background);
    }

    private void RefreshChapters()
    {
        if (!_chapterListRefreshGate.ConsumePending()) return;

        var chapterCount = Math.Clamp(Player.GetProperty<int>("chapter-list/count"), 0, MaximumChapterCount);
        var timelineChapters = new List<ChapterTimelineMarker>(chapterCount);
        for (var index = 0; index < chapterCount; index++)
        {
            var startTime = Player.GetProperty<double>($"chapter-list/{index}/time");
            if (!double.IsFinite(startTime) || startTime < 0) startTime = 0;
            string title;
            try { title = Player.GetProperty<string>($"chapter-list/{index}/title") ?? string.Empty; }
            catch (MpvException) { title = string.Empty; }
            var label = string.IsNullOrWhiteSpace(title) ? UiText.Format(UiText.ChapterNumberFormat, index + 1) : title.Trim();
            timelineChapters.Add(new ChapterTimelineMarker(startTime, label));
        }
        _chapterCount = chapterCount;
        SetTimelineChapters(timelineChapters);
    }

    private void NavigateChapter(int direction)
    {
        if (_chapterCount == 0) return;
        try
        {
            var currentIndex = Player.GetProperty<int>("chapter");
            var targetIndex = ChapterNavigationRules.GetAdjacentChapterIndex(
                currentIndex, _chapterCount, direction);
            if (targetIndex is not { } chapterIndex) return;
            _pendingResume = null;
            _pendingResumeStartPositionSeconds = null;
            _pendingResumeTargetPositionSeconds = null;
            Player.SetProperty("chapter", (long)chapterIndex);
        }
        catch (MpvException exception)
        {
            AppLogger.Error("Chapter navigation failed", exception);
        }
    }

    private void PostTrackMetadataRefresh()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var refreshFailed = false;
            try
            {
                if (!_timelineClosing && _trackMetadataRefreshGate.IsPending)
                    RefreshTrackSelectors();
            }
            catch (MpvException)
            {
                // Keep the request pending; the playback-status timer retries after media loading settles.
                _trackMetadataRefreshGate.MarkPendingForFallback();
                refreshFailed = true;
            }
            finally
            {
                if (_trackMetadataRefreshGate.Complete(allowReschedule: !refreshFailed && !_timelineClosing))
                    PostTrackMetadataRefresh();
            }
        }, DispatcherPriority.Background);
    }

    private List<TrackMetadata> BuildTrackMetadata(int trackCount)
    {
        var metadata = new List<TrackMetadata>();
        var audioOrdinal = 0;
        var subtitleOrdinal = 0;
        for (var index = 0; index < trackCount; index++)
        {
            var type = ReadTrackProperty(index, "type", string.Empty);
            if (type is not ("audio" or "sub")) continue;
            var id = ReadTrackProperty(index, "id", 0);
            if (id <= 0) continue;

            var ordinal = type == "audio" ? ++audioOrdinal : ++subtitleOrdinal;
            var title = ReadTrackProperty(index, "title", string.Empty);
            var language = ReadTrackProperty(index, "lang", string.Empty);
            var codec = ReadTrackProperty(index, "codec", string.Empty);
            var isDefault = ReadTrackProperty(index, "default", false);
            var isForced = ReadTrackProperty(index, "forced", false);
            var isExternal = ReadTrackProperty(index, "external", false);
            var isSelected = ReadTrackProperty(index, "selected", false);
            string? sourceTag = null;
            if (type == "sub")
            {
                if (!isExternal)
                {
                    sourceTag = "[Dahili]";
                }
                else
                {
                    var externalPath = ReadTrackProperty(index, "external-filename", string.Empty);
                    if (_subtitleCache.IsDownloadedSubtitleForVideo(externalPath, _currentVideo ?? string.Empty, _preferences.SubtitleSearchLanguage))
                        sourceTag = "[Online]";
                    else
                        sourceTag = "[Klasör]";
                }
            }
            var label = BuildTrackLabel(type, ordinal, title, language, codec, isDefault, isForced, isExternal, sourceTag);
            metadata.Add(new TrackMetadata(index, type,
                new MediaTrackOption(id, label, isSelected, language, ordinal)));
        }

        return metadata;
    }

    private static string BuildTrackLabel(string type, int ordinal, string title, string language, string codec,
        bool isDefault, bool isForced, bool isExternal, string? sourceTag = null)
    {
        var labelParts = new List<string>();
        if (!string.IsNullOrEmpty(sourceTag)) labelParts.Add(sourceTag);
        labelParts.Add(string.IsNullOrWhiteSpace(title) ? UiText.Format(UiText.TrackNumberFormat, type == "audio" ? UiText.Audio : UiText.Subtitles, ordinal) : title.Trim());
        if (!string.IsNullOrWhiteSpace(language) && !string.Equals(language, "und", StringComparison.OrdinalIgnoreCase))
            labelParts.Add(language.Trim());
        if (!string.IsNullOrWhiteSpace(codec)) labelParts.Add(codec.Trim().ToUpperInvariant());
        if (isDefault) labelParts.Add(UiText.Default);
        if (isForced) labelParts.Add(UiText.Forced);
        return string.Join(" · ", labelParts);
    }

    private void PopulateAudioTrackList(IReadOnlyList<MediaTrackOption> tracks)
    {
        AudioTrackList.Items.Clear();
        var offItem = new ListBoxItem { Content = UiText.Muted };
        AudioTrackList.Items.Add(offItem);
        ListBoxItem? selectedItem = tracks.Any(track => track.IsSelected) ? null : offItem;
        foreach (var track in tracks)
        {
            var item = new ListBoxItem { Content = track.Label, Tag = track };
            AudioTrackList.Items.Add(item);
            if (track.IsSelected) selectedItem = item;
        }
        AudioTrackList.SelectedItem = selectedItem;
    }

    private void PopulateSubtitleTrackList(IReadOnlyList<MediaTrackOption> tracks)
    {
        SubtitleTrackList.Items.Clear();
        var offItem = new ListBoxItem { Content = UiText.Off };
        SubtitleTrackList.Items.Add(offItem);
        ListBoxItem? selectedItem = tracks.Any(track => track.IsSelected) ? null : offItem;
        foreach (var track in tracks)
        {
            var item = new ListBoxItem { Content = track.Label, Tag = track };
            SubtitleTrackList.Items.Add(item);
            if (track.IsSelected) selectedItem = item;
        }
        SubtitleTrackList.SelectedItem = selectedItem;
    }

    private void OpenSubtitlePicker()
    {
        _subtitlePickerCloseTimer.Stop();
        SubtitlePickerPopup.IsOpen = _currentVideo is not null;
    }

    private void ScheduleSubtitlePickerClose()
    {
        _subtitlePickerCloseTimer.Stop();
        _subtitlePickerCloseTimer.Start();
    }

    private string ReadTrackProperty(int index, string property, string fallback)
    {
        try { return Player.GetProperty<string>($"track-list/{index}/{property}") ?? fallback; }
        catch (MpvException) { return fallback; }
    }

    private int ReadTrackProperty(int index, string property, int fallback)
    {
        try { return Player.GetProperty<int>($"track-list/{index}/{property}"); }
        catch (MpvException) { return fallback; }
    }

    private bool ReadTrackProperty(int index, string property, bool fallback)
    {
        try { return Player.GetProperty<bool>($"track-list/{index}/{property}"); }
        catch (MpvException) { return fallback; }
    }

    private void OnAudioTrackSelectionChanged()
    {
        if (_updatingTrackSelectors || AudioTrackList.SelectedItem is not ListBoxItem item) return;
        CancelSubtitleAutomation();
        _subtitleAutomationStarted = false;
        try
        {
            if (item.Tag is MediaTrackOption track)
            {
                Player.SetProperty("aid", (long)track.Id);
                _preferences = _preferences with
                {
                    AudioTrackDisabled = false,
                    PreferredAudioLanguage = track.Language,
                    PreferredAudioTrackIndex = track.Ordinal
                };
            }
            else
            {
                Player.SetPropertyString("aid", "no");
                _preferences = _preferences with
                {
                    AudioTrackDisabled = true,
                    PreferredAudioLanguage = null,
                    PreferredAudioTrackIndex = 0
                };
            }
            SavePreferences(_preferences);
            AudioTrackPickerPopup.IsOpen = false;
            UpdateControlVisibility();
        }
        catch (MpvException exception) { AppLogger.Error("Audio track selection failed", exception); }
    }

    private void OnSubtitleTrackSelectionChanged()
    {
        if (_updatingTrackSelectors || SubtitleTrackList.SelectedItem is not ListBoxItem item) return;
        ApplySubtitleTrackSelection(item.Tag as MediaTrackOption);
    }

    private void ApplySubtitleTrackSelection(MediaTrackOption? track)
    {
        Interlocked.Increment(ref _subtitleSelectionGeneration);
        CancelSubtitleAutomation();
        try
        {
            var hasTrack = false;
            _selectedExternalSubtitlePath = null;
            if (track is not null)
            {
                Player.SetProperty("sid", (long)track.Id);
                _selectedSubtitleTrackId = track.Id;
                var selectedMetadata = _trackMetadataCache?.FirstOrDefault(metadata => metadata.Type == "sub" && metadata.Option.Id == track.Id);
                if (selectedMetadata is not null && ReadTrackProperty(selectedMetadata.Index, "external", false))
                {
                    _selectedExternalSubtitlePath = ReadTrackProperty(selectedMetadata.Index, "external-filename", "");
                    _selectedSubtitleTrackId = null;
                }
                hasTrack = true;
                _preferences = _preferences with
                {
                    PreferredSubtitleLanguage = track.Language,
                    PreferredSubtitleTrackIndex = track.Ordinal
                };
            }
            else
            {
                Player.SetPropertyString("sid", "no");
                _selectedSubtitleTrackId = 0;
                _preferences = _preferences with
                {
                    PreferredSubtitleLanguage = null,
                    PreferredSubtitleTrackIndex = 0
                };
            }
            _preferences = _preferences with { SubtitlesVisible = hasTrack };
            Player.SetProperty("sub-visibility", hasTrack);
            UpdateSubtitleDelayTooltip();
            SavePreferences(_preferences);
            SaveCurrentProgress(force: true);
            _subtitleAutomationStarted = false;

            _updatingTrackSelectors = true;
            try
            {
                SetListBoxSelection(SubtitleTrackList, track?.Id);
            }
            finally
            {
                _updatingTrackSelectors = false;
            }
        }
        catch (MpvException exception) { AppLogger.Error("Subtitle track selection failed", exception); }
    }

    private void ToggleSubtitlePicker()
    {
        SubtitlePickerPopup.IsOpen = !SubtitlePickerPopup.IsOpen;
        if (SubtitlePickerPopup.IsOpen)
        {
            RefreshSubtitlePickerContent();
        }
        UpdateControlVisibility();
    }

    private void ToggleAudioTrackPicker()
    {
        AudioTrackPickerPopup.IsOpen = _currentVideo is not null && !AudioTrackPickerPopup.IsOpen;
        UpdateControlVisibility();
    }

    private static void SetListBoxSelection(ListBox listBox, int? trackId)
    {
        foreach (var obj in listBox.Items)
        {
            if (obj is ListBoxItem lbi)
            {
                if (trackId is null && lbi.Tag is null) { listBox.SelectedItem = lbi; return; }
                if (lbi.Tag is MediaTrackOption opt && opt.Id == trackId) { listBox.SelectedItem = lbi; return; }
            }
        }
    }

    private void ApplyPreferredTrack(IReadOnlyList<MediaTrackOption> tracks, string? preferredLanguage, int preferredIndex, string property)
    {
        if (tracks.Count == 0) return;
        var preferredTrackIndex = MediaTrackPreferenceRules.FindPreferredTrackIndex(
            tracks,
            preferredLanguage,
            preferredIndex,
            track => track.Language,
            track => track.Ordinal);
        if (preferredTrackIndex < 0) return;
        var preferred = tracks[preferredTrackIndex];
        if (preferred is not null && !preferred.IsSelected) Player.SetProperty(property, (long)preferred.Id);
    }

    private sealed record MediaTrackOption(int Id, string Label, bool IsSelected, string Language, int Ordinal);
    private sealed record TrackMetadata(int Index, string Type, MediaTrackOption Option);

    private void RestorePendingProgress(double duration)
    {
        if (_currentVideo is null || _pendingResume is null || !_subtitleMediaReady) return;
        var loadedPath = Player.GetProperty<string>("path");
        if (!PathsEqual(loadedPath, _currentVideo)) return;

        var savedSubtitlePath = _pendingResume?.SubtitlePath;
        if (!string.IsNullOrWhiteSpace(savedSubtitlePath) && File.Exists(savedSubtitlePath))
        {
            AddSubtitle(savedSubtitlePath, _preferences.SubtitlesVisible, isAutomatic: true);
        }

        if (_pendingResume is not { } progress) return;
        if (duration <= 0) return;
        if (progress.PositionSeconds > 2 && progress.PositionSeconds < duration - 10)
        {
            _pendingResumeStartPositionSeconds = Player.GetProperty<double>("time-pos");
            Player.Seek(progress.PositionSeconds, SeekOption.Absolute | SeekOption.Exact).Invoke();
            _pendingResumeTargetPositionSeconds = progress.PositionSeconds;
        }
        if (double.IsFinite(progress.SubtitleDelaySeconds))
        {
            _subtitleDelaySeconds = progress.SubtitleDelaySeconds;
            Player.SetProperty("sub-delay", _subtitleDelaySeconds);
            UpdateSubtitleDelayTooltip();
        }
        _pendingResume = null;
        SaveCurrentProgress(force: true);
    }

    private void SaveCurrentProgress(
        bool force = false,
        double? positionSeconds = null,
        double? durationSeconds = null)
    {
        if (_currentVideo is null || (!force && (DateTime.UtcNow - _lastProgressSave).TotalSeconds < 8)) return;
        try
        {
            if (!PathsEqual(Player.GetProperty<string>("path"), _currentVideo)) return;
            var position = positionSeconds ?? Player.GetProperty<double>("time-pos");
            var duration = durationSeconds ?? Player.GetProperty<double>("duration");
            if (_pendingResumeTargetPositionSeconds is { } resumeTarget)
            {
                var isSeeking = Player.GetProperty<bool>("seeking");
                var seekStart = _pendingResumeStartPositionSeconds;
                if (seekStart is { } startPosition
                    && PlaybackSeekRules.HasReachedOrPassedTarget(isSeeking, position, startPosition, resumeTarget))
                {
                    _pendingResumeStartPositionSeconds = null;
                    _pendingResumeTargetPositionSeconds = null;
                }
                position = PlaybackSeekRules.GetPositionForPersistence(
                    position, _pendingResumeStartPositionSeconds,
                    _pendingResumeTargetPositionSeconds, isSeeking);
            }
            if (!double.IsFinite(position) || position < 2 || (duration > 0 && position >= duration - 10))
                position = 0;
            _ = ObserveProgressPersistence(_playbackProgressStore.ScheduleSave(
                _currentVideo,
                new PlaybackProgress(position, _subtitleDelaySeconds, _playbackSpeed,
                    _selectedExternalSubtitlePath, _selectedSubtitleTrackId)));
            _lastProgressSave = DateTime.UtcNow;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _lastProgressSave = DateTime.UtcNow;
            AppLogger.Error("Playback progress could not be saved", exception);
        }
        catch (Exception exception) when (exception is InvalidOperationException or MpvException)
        {
            // libmpv properties can be temporarily unavailable while changing media.
            _lastProgressSave = DateTime.UtcNow;
        }
    }

    private static async Task ObserveProgressPersistence(Task persistenceTask)
    {
        try
        {
            await persistenceTask;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogger.Error("Playback progress could not be saved", exception);
        }
    }

    private static bool PathsEqual(string? first, string second)
    {
        if (string.IsNullOrWhiteSpace(first)) return false;
        try { return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    private void PlayNextQueuedVideo()
    {
        _endOfFileHandled = true;
        if (_currentVideo is not null)
            _ = ObserveProgressPersistence(_playbackProgressStore.ScheduleRemove(_currentVideo));
        var next = QueueOrderRules.TakeNextAvailablePath(_queuedVideos,
            path => File.Exists(path) && MediaPathRules.IsSupportedVideo(path));
        _queueItemsDirty = true;
        UpdateQueuePanel();
        ScheduleQueuePersistence();
        if (next is null)
        {
            _isPlaying = false;
            UpdatePlaybackButton();
            _progressTimer.Stop();
            UpdatePlaybackPollingInterval();
            return;
        }

        LoadVideo(next, preserveQueue: true);
    }

    private void PlayQueuedVideo(int index)
    {
        if (index < 0 || index >= _queuedVideos.Count) return;
        var path = _queuedVideos[index];
        if (!File.Exists(path) || !MediaPathRules.IsSupportedVideo(path)) return;

        RemoveQueuedVideo(index);
        LoadVideo(path, preserveQueue: true);
    }

    private void RemoveQueuedVideo(int index)
    {
        if (index < 0 || index >= _queuedVideos.Count) return;
        _queuedVideos.RemoveAt(index);
        _queueItemsDirty = true;
        UpdateQueuePanel();
        ScheduleQueuePersistence();
    }

    private void ReorderQueuedVideo(int index, int destination)
    {
        if (!QueueOrderRules.Move(_queuedVideos, index, destination)) return;
        _queueItemsDirty = true;
        UpdateQueuePanel();
        ScheduleQueuePersistence();
        QueueItems.SelectedIndex = destination;
    }

    private void OnQueueRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index }) RemoveQueuedVideo(index);
        e.Handled = true;
    }

    private void OnQueuePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(QueueItems).Properties.IsLeftButtonPressed) return;
        var index = GetQueueItemIndex(e.Source);
        if (index < 0) return;
        _queueDragPointer = e.Pointer;
        _queueDragIndex = index;
        _queueDragDestination = index;
        _queueDragStart = e.GetPosition(QueueItems);
        _queueDragActive = false;
        e.Pointer.Capture(QueueItems);
    }

    private void OnQueuePointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.Pointer != _queueDragPointer || !e.GetCurrentPoint(QueueItems).Properties.IsLeftButtonPressed) return;
        var position = e.GetPosition(QueueItems);
        if (!_queueDragActive
            && Math.Abs(position.X - _queueDragStart.X) < QueueDragThreshold
            && Math.Abs(position.Y - _queueDragStart.Y) < QueueDragThreshold) return;
        _queueDragActive = true;
        var destination = GetQueueItemIndexAt(position.Y);
        if (destination >= 0 && destination != _queueDragDestination)
        {
            _queueDragDestination = destination;
            QueueItems.SelectedIndex = destination;
        }
        e.Handled = true;
    }

    private void OnQueuePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer != _queueDragPointer || e.InitialPressMouseButton != MouseButton.Left) return;
        var source = _queueDragIndex;
        var destination = _queueDragDestination;
        var wasDragging = _queueDragActive;
        ClearQueueDrag();
        if (wasDragging)
        {
            ReorderQueuedVideo(source, destination);
        }
        else if (source >= 0)
        {
            PlayQueuedVideo(source);
        }
        e.Handled = true;
    }

    private int GetQueueItemIndex(object? source)
    {
        if (source is not Visual visual) return -1;
        if ((visual as Control)?.DataContext is QueueListItem directItem)
            return directItem.Index;
        for (Visual? current = visual; current is not null && current != QueueItems; current = current.GetVisualParent())
        {
            if (current is Button) return -1;
            if ((current as Control)?.DataContext is QueueListItem dataItem)
                return dataItem.Index;
            if (current is ListBoxItem item) return QueueItems.IndexFromContainer(item);
        }
        return -1;
    }

    private int GetQueueItemIndexAt(double y)
    {
        if (_queuedVideos.Count == 0) return -1;
        if (y <= 0) return 0;
        if (y >= QueueItems.Bounds.Height) return _queuedVideos.Count - 1;

        var hitTarget = QueueItems.InputHitTest(new Point(QueueItems.Bounds.Width / 2, y)) as Visual;
        for (var current = hitTarget; current is not null && current != QueueItems; current = current.GetVisualParent())
        {
            if (current is not ListBoxItem item) continue;
            var index = QueueItems.IndexFromContainer(item);
            if (index < 0) return -1;
            var top = item.TranslatePoint(new Point(0, 0), QueueItems);
            if (top is not { } point) return index;
            return y < point.Y + item.Bounds.Height / 2
                ? index
                : Math.Min(index + 1, _queuedVideos.Count - 1);
        }

        return -1;
    }

    private void ClearQueueDrag()
    {
        var pointer = _queueDragPointer;
        _queueDragPointer = null;
        _queueDragIndex = -1;
        _queueDragDestination = -1;
        _queueDragActive = false;
        pointer?.Capture(null);
    }

    private async Task AddSiblingVideosToQueueAsync(
        string videoPath,
        long generation,
        CancellationTokenSource scanCancellation)
    {
        var cancellationToken = scanCancellation.Token;
        try
        {
            var directory = Path.GetDirectoryName(videoPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
            var fullVideoPath = Path.GetFullPath(videoPath);
            var siblings = await Task.Run(() =>
            {
                var videoPaths = new List<string>();
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (MediaPathRules.IsSupportedVideo(path)) videoPaths.Add(Path.GetFullPath(path));
                }

                cancellationToken.ThrowIfCancellationRequested();
                return MediaPathRules.OrderVideosByFileName(videoPaths)
                    .Where(path => !string.Equals(path, fullVideoPath, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _queueScanGeneration || !PathsEqual(_currentVideo, videoPath)) return;
            var addedCount = QueueOrderRules.AppendUniquePaths(_queuedVideos, siblings);
            _queueItemsDirty |= addedCount > 0;
            UpdateQueuePanel();
            if (addedCount > 0) ScheduleQueuePersistence();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (generation != _queueScanGeneration) return;
            AppLogger.Error("Video folder scan failed", exception);
            _ = ShowMessageAsync(UiText.CouldNotScanFolder, exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_queueScanCancellation, scanCancellation)) _queueScanCancellation = null;
            scanCancellation.Dispose();
        }
    }

    private void ToggleQueuePanelPin()
    {
        _queuePanelPinned = !_queuePanelPinned;
        if (_queuePanelPinned) _queuePanelHoverActive = false;
        UpdateQueuePanel();
    }

    private void ApplyVideoDisplayPreferences()
    {
        var aspectRatio = VideoDisplayRules.NormalizeAspectRatio(_preferences.VideoAspectRatio);
        Player.SetProperty("video-aspect-override", aspectRatio == VideoDisplayRules.AutomaticAspectRatio ? "no" : aspectRatio);
        var scaleMode = VideoDisplayRules.NormalizeScaleMode(_preferences.VideoScaleMode);
        Player.SetProperty("keepaspect", scaleMode != VideoDisplayRules.StretchScaleMode);
        Player.SetProperty("panscan", scaleMode == VideoDisplayRules.FillScaleMode ? 1.0 : 0.0);
    }

    private void UpdateQueuePanel()
    {
        QueuePanelPinButton.Content = _queuePanelPinned ? "●" : "◇";
        var pinAction = _queuePanelPinned ? UiText.UnpinPanel : UiText.PinPanel;
        AutomationProperties.SetName(QueuePanelPinButton, pinAction);
        ToolTip.SetTip(QueuePanelPinButton, pinAction);
        QueueCurrentLabel.Text = _currentVideo is null ? UiText.NowPlaying : UiText.Format(UiText.NowPlayingFormat, Path.GetFileName(_currentVideo));
        if (_queueItemsDirty)
        {
            QueueItems.ItemsSource = _queuedVideos.Select((path, index) => new QueueListItem(path, index)).ToArray();
            _queueItemsDirty = false;
        }
        RootPanel.ColumnDefinitions[1].Width = _queuePanelPinned ? GridLength.Auto : new GridLength(0);
        VideoFrame.SetValue(Grid.ColumnSpanProperty, _queuePanelPinned ? 1 : 2);
        QueuePanel.SetValue(Grid.ColumnProperty, _queuePanelPinned ? 1 : 0);
        QueuePanel.SetValue(Grid.ColumnSpanProperty, _queuePanelPinned ? 1 : 2);
        var showQueuePanel = _queuePanelPinned || _queuedVideos.Count > 0 && _queuePanelHoverActive;
        TransitionPanelVisibility(QueuePanel, showQueuePanel, !_queuePanelPinned);
    }

    private void ScheduleQueuePersistence()
    {
        if (_timelineClosing) return;
        Interlocked.Increment(ref _queuePersistenceGeneration);
        _queuePersistenceTimer.Stop();
        _queuePersistenceTimer.Start();
    }

    private void CancelPendingPlaylistLoad()
    {
        var cancellation = Interlocked.Exchange(ref _playlistLoadCancellation, null);
        if (cancellation is null) return;
        cancellation.Cancel();
    }

    private async Task RestoreQueueAsync()
    {
        if (!File.Exists(_queuePlaylistPath)) return;
        var loadRequestGeneration = _loadRequestGeneration;
        IReadOnlyList<string> videoPaths;
        try
        {
            videoPaths = await M3uPlaylistFile.LoadAsync(_queuePlaylistPath);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or UnauthorizedAccessException or DecoderFallbackException or System.Security.SecurityException)
        {
            AppLogger.Error("Saved playback queue could not be restored", exception);
            return;
        }

        if (_timelineClosing || _currentVideo is not null
            || loadRequestGeneration != _loadRequestGeneration || videoPaths.Count == 0) return;

        LoadVideo(videoPaths[0], afterLoad: () =>
        {
            _queuedVideos.Clear();
            _queuedVideos.AddRange(videoPaths.Skip(1));
            _queueItemsDirty = true;
            UpdateQueuePanel();
            ScheduleQueuePersistence();
        }, scanSiblingVideos: false);
    }

    private async Task PersistQueueAsync(long generation)
    {
        var videoPaths = QueueOrderRules.CreatePlaybackSnapshot(
            _currentVideo, _queuedVideos, _endOfFileHandled, M3uPlaylistFile.MaximumEntryCount);

        try
        {
            await _queuePersistenceLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (generation != Interlocked.Read(ref _queuePersistenceGeneration)) return;
                await M3uPlaylistFile.SaveAsync(_queuePlaylistPath, videoPaths).ConfigureAwait(false);
            }
            finally
            {
                _queuePersistenceLock.Release();
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException)
        {
            AppLogger.Error("Playback queue could not be saved", exception);
        }
    }

    private void AdjustSubtitleDelay(double direction)
    {
        CancelSubtitleAutomation();
        if (_currentVideo is null) return;
        var previousDelay = _subtitleDelaySeconds;
        _subtitleDelaySeconds = Math.Round(_subtitleDelaySeconds + direction * _preferences.SubtitleDelayStep, 2);
        Player.SetProperty("sub-delay", _subtitleDelaySeconds);
        ShowSubtitleSyncNotification(_subtitleDelaySeconds - previousDelay);
        UpdateSubtitleDelayTooltip();
        SaveCurrentProgress(force: true);
    }

    private void ResetSubtitleDelay()
    {
        CancelSubtitleAutomation();
        if (_currentVideo is null) return;
        var change = -_subtitleDelaySeconds;
        _subtitleDelaySeconds = 0;
        Player.SetProperty("sub-delay", 0.0);
        ShowSubtitleSyncNotification(change);
        UpdateSubtitleDelayTooltip();
        SaveCurrentProgress(force: true);
    }

    private void ShowSubtitleSyncNotification(double changeSeconds)
    {
        var culture = CultureInfo.InvariantCulture;
        var current = _subtitleDelaySeconds.ToString("+0.00;-0.00;0.00", culture);
        var change = changeSeconds.ToString("+0.00;-0.00;0.00", culture);
        SubtitleSyncCurrentLabel.Text = UiText.Format(UiText.CurrentSubtitleTimingFormat, current);
        SubtitleSyncChangeLabel.Text = UiText.Format(UiText.SubtitleTimingChangeFormat, change);
        SubtitleSyncNotification.IsVisible = true;
        _subtitleSyncNotificationTimer.Stop();
        _subtitleSyncNotificationTimer.Start();
    }

    private void UpdateSubtitleDelayTooltip()
    {
        SubtitleButton.Content = "CC";
        SubtitleButton.Foreground = _preferences.SubtitlesVisible
            ? new SolidColorBrush(Color.Parse(_preferences.AccentColor))
            : Brush.Parse("#A7A7A7");
        var action = _preferences.SubtitlesVisible ? UiText.HideSubtitles : UiText.ShowSubtitles;
        AutomationProperties.SetName(SubtitleButton, action);
        var delay = _subtitleDelaySeconds.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        ToolTip.SetTip(SubtitleButton, UiText.Format(UiText.SubtitleTimingTooltipFormat, action, delay)
            + (string.IsNullOrEmpty(_subtitleAutomationStatus) ? "" : $"\n{_subtitleAutomationStatus}"));
    }

    private void UpdateHardwareAccelerationButton()
    {
        HardwareAccelerationButton.Content = "▦";
        HardwareAccelerationButton.Foreground = _preferences.HardwareAcceleration
            ? new SolidColorBrush(Color.Parse(_preferences.AccentColor))
            : Brush.Parse("#A7A7A7");
        var action = _preferences.HardwareAcceleration
            ? UiText.DisableHardwareAcceleration
            : UiText.EnableHardwareAcceleration;
        AutomationProperties.SetName(HardwareAccelerationButton, action);
        ToolTip.SetTip(HardwareAccelerationButton, _preferences.HardwareAcceleration
            ? UiText.HardwareAccelerationEnabledDisable
            : UiText.HardwareAccelerationDisabledHelp);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var containsPlaylist = false;
        var containsVideo = false;
        var containsSubtitle = false;
        foreach (var file in e.DataTransfer.TryGetFiles() ?? [])
        {
            var path = file.Path.LocalPath;
            containsPlaylist |= M3uPlaylistFile.IsPlaylistPath(path);
            containsVideo |= MediaPathRules.IsSupportedVideo(path);
            containsSubtitle |= MediaPathRules.IsSupportedSubtitle(path);
            if (containsPlaylist && containsVideo && containsSubtitle) break;
        }

        var canAccept = containsPlaylist || MediaDropRules.CanAccept(containsVideo, containsSubtitle, _currentVideo is not null);
        e.DragEffects = canAccept ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.IsVisible = canAccept;
        if (canAccept)
            DropOverlayText.Text = containsPlaylist ? UiText.OpenPlaylist
                : containsVideo ? UiText.OpenVideo : UiText.AddSubtitlesToVideo;
    }

    private void OnDragLeave(object? sender, RoutedEventArgs e) => DropOverlay.IsVisible = false;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        var paths = e.DataTransfer.TryGetFiles()?.Select(file => file.Path.LocalPath).ToArray() ?? [];
        var playlistPaths = paths.Where(M3uPlaylistFile.IsPlaylistPath).Where(File.Exists).ToArray();
        if (playlistPaths.Length > 0)
        {
            var playlistDroppedVideos = paths.Where(MediaPathRules.IsSupportedVideo).Where(File.Exists).ToArray();
            var playlistDroppedSubtitles = paths.Where(MediaPathRules.IsSupportedSubtitle).ToArray();
            _ = LoadPlaylistAsync(playlistPaths, playlistDroppedVideos, playlistDroppedSubtitles);
            return;
        }
        var videos = paths.Where(MediaPathRules.IsSupportedVideo).Where(File.Exists).ToArray();
        var subtitles = paths.Where(MediaPathRules.IsSupportedSubtitle).ToArray();
        if (videos.Length > 0)
        {
            LoadVideo(videos[0], afterLoad: () =>
            {
                var addedCount = QueueOrderRules.AppendUniquePaths(_queuedVideos, videos.Skip(1));
                _queueItemsDirty |= addedCount > 0;
                UpdateQueuePanel();
                if (addedCount > 0) ScheduleQueuePersistence();
                foreach (var subtitle in subtitles) AddSubtitle(subtitle);
            });
            return;
        }

        if (_currentVideo is null) return;
        foreach (var subtitle in subtitles) AddSubtitle(subtitle);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ShortcutGesture.TryFromAvalonia(e.Key, e.KeyModifiers, out var gesture) && HandleShortcut(gesture))
            e.Handled = true;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (_startupVideoPaths.Count > 0)
        {
            var startupVideoPaths = _startupVideoPaths;
            _startupVideoPaths = Array.Empty<string>();
            Dispatcher.UIThread.Post(() => OpenVideoPaths(startupVideoPaths, bringToFront: false),
                DispatcherPriority.Background);
        }
        else
        {
            _ = RestoreQueueAsync();
        }

        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle != IntPtr.Zero)
            {
                _keyboardHook = new WindowsKeyboardHook(handle, HandleShortcut);
                _mouseHook = new WindowsMouseHook(handle, HandleMouseGesture);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("Windows keyboard or mouse hooks could not start", exception);
            _ = ShowMessageAsync(UiText.ShortcutsAreUnavailable, exception.Message);
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty) return;

        var maximizeAction = WindowState == WindowState.Maximized ? UiText.RestoreWindowSize : UiText.Maximize;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        AutomationProperties.SetName(MaximizeButton, maximizeAction);
        ToolTip.SetTip(MaximizeButton, maximizeAction);

    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState == WindowState.FullScreen || !e.GetCurrentPoint(TitleDragRegion).Properties.IsLeftButtonPressed) return;
        BeginMoveDrag(e);
    }

    private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal || !CanResize) return;
        var position = e.GetPosition(this);
        const double edge = 7;
        var left = position.X <= edge;
        var right = position.X >= Bounds.Width - edge;
        var top = position.Y <= edge;
        var bottom = position.Y >= Bounds.Height - edge;
        var resizeEdge = (left, right, top, bottom) switch
        {
            (true, _, true, _) => WindowEdge.NorthWest,
            (_, true, true, _) => WindowEdge.NorthEast,
            (true, _, _, true) => WindowEdge.SouthWest,
            (_, true, _, true) => WindowEdge.SouthEast,
            (true, _, _, _) => WindowEdge.West,
            (_, true, _, _) => WindowEdge.East,
            (_, _, true, _) => WindowEdge.North,
            (_, _, _, true) => WindowEdge.South,
            _ => (WindowEdge?)null
        };
        if (resizeEdge is null) return;
        BeginResizeDrag(resizeEdge.Value, e);
        e.Handled = true;
    }

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized
        ? WindowState.Normal
        : WindowState.Maximized;

    private void ToggleFullscreen()
    {
        var isFullscreen = WindowState == WindowState.FullScreen;
        WindowState = isFullscreen ? WindowState.Normal : WindowState.FullScreen;
        UpdateControlVisibility();
    }

    private ContextMenu CreateAutoHideContextMenu(bool isHeader)
    {
        var toggle = new MenuItem
        {
            Header = isHeader ? UiText.AutoHideTopBar : UiText.AutoHideBottomBar,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = isHeader ? _preferences.AutoHideHeader : _preferences.AutoHideBottom
        };
        toggle.Click += (_, _) => SavePreferences(isHeader
            ? _preferences with { AutoHideHeader = !_preferences.AutoHideHeader }
            : _preferences with { AutoHideBottom = !_preferences.AutoHideBottom });
        _autoHideMenuItems.Add((toggle, isHeader));
        return new ContextMenu { ItemsSource = new[] { toggle } };
    }

    private void OnRootPointerMoved(object? sender, PointerEventArgs e)
    {
        ResetCursorHideTimer();
        var position = e.GetPosition(RootPanel);
        var pointerWithinWindow = position.X >= 0 && position.X <= RootPanel.Bounds.Width
            && position.Y >= 0 && position.Y <= RootPanel.Bounds.Height;
        UpdatePanelHoverState(position.X, position.Y, pointerWithinWindow);
    }

    private void OnRootPointerExited(object? sender, PointerEventArgs e)
    {
        _cursorHideTimer.Stop();
        ShowCursor();
        UpdatePanelHoverState(0, 0, pointerWithinWindow: false);
    }

    private void OnRootPointerActivity(object? sender, PointerEventArgs e)
    {
        ResetCursorHideTimer();
        if (e is PointerPressedEventArgs && SubtitlePickerPopup.IsOpen && !SubtitleButton.IsPointerOver)
            SubtitlePickerPopup.IsOpen = false;
    }

    private void ResetCursorHideTimer()
    {
        ShowCursor();
        _cursorHideTimer.Stop();
        _cursorHideTimer.Start();
    }

    private void HideCursor()
    {
        _cursorHideTimer.Stop();
        _cursorBeforeHide = Cursor;
        Cursor = new Cursor(StandardCursorType.None);
        _cursorHidden = true;
    }

    private void ShowCursor()
    {
        if (!_cursorHidden) return;
        Cursor = _cursorBeforeHide;
        _cursorBeforeHide = null;
        _cursorHidden = false;
    }

    private void UpdatePanelHoverState(double pointerX, double pointerY, bool pointerWithinWindow)
    {
        var headerContextMenuOpen = HeaderBar.ContextMenu?.IsOpen == true;
        var bottomContextMenuOpen = BottomBar.ContextMenu?.IsOpen == true;
        var headerHovered = pointerWithinWindow && IsPointerInside(HeaderBar, pointerX, pointerY);
        var bottomHovered = pointerWithinWindow && IsPointerInside(BottomBar, pointerX, pointerY);
        var queueHovered = pointerWithinWindow && IsPointerInside(QueuePanel, pointerX, pointerY);
        var pointerOverVideo = pointerWithinWindow && IsPointerInside(VideoFrame, pointerX, pointerY);
        var headerActive = _preferences.AutoHideHeader
            && (headerContextMenuOpen || pointerWithinWindow && (headerHovered || pointerY <= HeaderRevealZoneHeight));
        var audioTrackPickerOpen = AudioTrackPickerPopup.IsOpen;
        var bottomActive = _preferences.AutoHideBottom
            && (bottomContextMenuOpen || SubtitlePickerPopup.IsOpen || audioTrackPickerOpen || pointerWithinWindow
                && (bottomHovered || pointerY >= RootPanel.Bounds.Height - BottomRevealZoneHeight));
        var queueActive = !_queuePanelPinned
            && _queuedVideos.Count > 0
            && pointerWithinWindow
            && !bottomHovered
            && (queueHovered || pointerOverVideo && pointerX >= RootPanel.Bounds.Width - QueueRevealZoneWidth);
        if (headerActive == _headerHoverActive && bottomActive == _bottomHoverActive && queueActive == _queuePanelHoverActive) return;

        _headerHoverActive = headerActive;
        _bottomHoverActive = bottomActive;
        _queuePanelHoverActive = queueActive;
        UpdateControlVisibility();
        UpdateQueuePanel();
    }

    private static bool IsPointerInside(Control control, double pointerX, double pointerY) =>
        control.IsVisible
        && pointerX >= control.Bounds.X && pointerX <= control.Bounds.Right
        && pointerY >= control.Bounds.Y && pointerY <= control.Bounds.Bottom;

    private void TransitionPanelVisibility(Control panel, bool visible, bool shouldAutoHide)
    {
        var state = _panelFadeStates[panel];
        if (!shouldAutoHide)
        {
            state.IsAnimating = false;
            panel.IsVisible = true;
            panel.IsHitTestVisible = true;
            panel.Opacity = 1;
            return;
        }

        var targetOpacity = visible ? 1.0 : 0.0;
        if (visible)
        {
            if (!panel.IsVisible)
            {
                panel.Opacity = 0;
                panel.IsVisible = true;
            }
            panel.IsHitTestVisible = true;
        }
        else if (!panel.IsVisible)
        {
            return;
        }

        if (Math.Abs(panel.Opacity - targetOpacity) < 0.001)
        {
            state.IsAnimating = false;
            panel.Opacity = targetOpacity;
            if (!visible)
                panel.IsVisible = false;
            if (!_panelFadeStates.Values.Any(panelState => panelState.IsAnimating))
                _panelFadeTimer.Stop();
            return;
        }

        panel.IsHitTestVisible = visible;
        state.StartOpacity = panel.Opacity;
        state.TargetOpacity = targetOpacity;
        state.StartedAt = DateTime.UtcNow;
        state.IsAnimating = true;
        _panelFadeTimer.Start();
    }

    private void UpdatePanelFades()
    {
        var hasAnimatingPanels = false;
        foreach (var state in _panelFadeStates.Values)
        {
            if (!state.IsAnimating) continue;
            var elapsed = DateTime.UtcNow - state.StartedAt;
            var progress = Math.Clamp(elapsed.TotalMilliseconds / PanelFadeDuration.TotalMilliseconds, 0, 1);
            var easedProgress = progress * progress * (3 - 2 * progress);
            state.Control.Opacity = state.StartOpacity
                + (state.TargetOpacity - state.StartOpacity) * easedProgress;
            if (progress < 1)
            {
                hasAnimatingPanels = true;
                continue;
            }

            state.IsAnimating = false;
            state.Control.Opacity = state.TargetOpacity;
            if (state.TargetOpacity == 0)
                state.Control.IsVisible = false;
        }

        if (!hasAnimatingPanels) _panelFadeTimer.Stop();
    }

    private void UpdateControlVisibility()
    {
        var isFullscreen = WindowState == WindowState.FullScreen;
        var autoHideHeader = _preferences.AutoHideHeader;
        var autoHideBottom = _preferences.AutoHideBottom;
        var showHeader = !autoHideHeader || _headerHoverActive;
        var showBottom = _isDraggingSeek || !autoHideBottom || _bottomHoverActive || _bottomActivityActive
            || SubtitlePickerPopup.IsOpen || AudioTrackPickerPopup.IsOpen;

        RootPanel.RowDefinitions[0].Height = autoHideHeader ? new GridLength(0) : new GridLength(48);
        RootPanel.RowDefinitions[2].Height = autoHideBottom ? new GridLength(0) : GridLength.Auto;
        HeaderBar.SetValue(Grid.RowProperty, autoHideHeader ? 1 : 0);
        HeaderBar.Height = 48;
        HeaderBar.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        HeaderBar.ZIndex = 5;
        BottomBar.SetValue(Grid.RowProperty, autoHideBottom ? 1 : 2);
        TransitionPanelVisibility(HeaderBar, showHeader, autoHideHeader);
        TransitionPanelVisibility(BottomBar, showBottom, autoHideBottom);
        RootPanel.Margin = new Thickness(0);
        VideoFrame.Margin = !autoHideHeader && !isFullscreen ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        VideoFrame.CornerRadius = new CornerRadius(0);
        VideoFrame.BorderThickness = new Thickness(0);
    }

    private bool HandleShortcut(ShortcutGesture gesture)
    {
        var action = ShortcutBindingRules.FindAction(_preferences.Shortcuts, gesture.ToString());
        return !string.IsNullOrEmpty(action) && ExecuteAction(action);
    }

    private bool HandleMouseGesture(MouseGesture gesture, int screenX, int screenY)
    {
        var rootTopLeft = RootPanel.PointToScreen(new Point(0, 0));
        var rootBottomRight = RootPanel.PointToScreen(new Point(RootPanel.Bounds.Width, RootPanel.Bounds.Height));
        var inWindow = screenX >= rootTopLeft.X && screenX < rootBottomRight.X
            && screenY >= rootTopLeft.Y && screenY < rootBottomRight.Y;
        if (!inWindow) return false;

        var topLeft = VideoFrame.PointToScreen(new Point(0, 0));
        var bottomRight = VideoFrame.PointToScreen(new Point(VideoFrame.Bounds.Width, VideoFrame.Bounds.Height));
        var width = bottomRight.X - topLeft.X;
        var height = bottomRight.Y - topLeft.Y;
        var insideVideo = width > 0 && height > 0 && screenX >= topLeft.X && screenX < bottomRight.X
            && screenY >= topLeft.Y && screenY < bottomRight.Y;
        if (!insideVideo
            || IsPointerInsideScreen(HeaderBar, screenX, screenY)
            || IsPointerInsideScreen(BottomBar, screenX, screenY)
            || IsPointerInsideScreen(QueuePanel, screenX, screenY))
            return false;

        var relativeX = width > 0 ? (screenX - topLeft.X) / width : 0;
        var relativeY = height > 0 ? (screenY - topLeft.Y) / height : 0;
        var action = MouseBindingRules.FindAction(gesture.ToString(), relativeX, relativeY, insideVideo,
            _preferences.MouseRegionOrientation, _preferences.MouseRegionCount, _preferences.MouseBindings);
        return action is not null && ExecuteAction(action);
    }

    private static bool IsPointerInsideScreen(Control control, int screenX, int screenY)
    {
        if (!control.IsVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) return false;
        var topLeft = control.PointToScreen(new Point(0, 0));
        var bottomRight = control.PointToScreen(new Point(control.Bounds.Width, control.Bounds.Height));
        return screenX >= topLeft.X && screenX < bottomRight.X
            && screenY >= topLeft.Y && screenY < bottomRight.Y;
    }

    private bool ExecuteAction(string action)
    {
        switch (action)
        {
            case ShortcutDefaults.TogglePlayback: TogglePlayback(); break;
            case ShortcutDefaults.ToggleSettings: ToggleSettings(); break;
            case ShortcutDefaults.ToggleMute: ToggleMute(); break;
            case ShortcutDefaults.PreviousFrame: StepFrame(forward: false); break;
            case ShortcutDefaults.NextFrame: StepFrame(forward: true); break;
            case ShortcutDefaults.PreviousChapter: NavigateChapter(-1); break;
            case ShortcutDefaults.NextChapter: NavigateChapter(1); break;
            case ShortcutDefaults.SeekBackward: Seek(-5); break;
            case ShortcutDefaults.SeekForward: Seek(5); break;
            case ShortcutDefaults.SeekLongBackward: Seek(-30); break;
            case ShortcutDefaults.SeekLongForward: Seek(30); break;
            case ShortcutDefaults.SpeedDown: AdjustPlaybackSpeed(-_preferences.PlaybackSpeedStep); break;
            case ShortcutDefaults.SpeedUp: AdjustPlaybackSpeed(_preferences.PlaybackSpeedStep); break;
            case ShortcutDefaults.SpeedReset: SetPlaybackSpeed(1.0); break;
            case ShortcutDefaults.CaptureFrame: _ = CaptureFrameAsync(); break;
            case ShortcutDefaults.OpenVideo: _ = OpenVideoAsync(); break;
            case ShortcutDefaults.ToggleFullscreen: ToggleFullscreen(); break;
            case ShortcutDefaults.ToggleSubtitles: ToggleSubtitles(); break;
            case ShortcutDefaults.AddSubtitle: _ = OpenSubtitleAsync(); break;
            case ShortcutDefaults.VolumeDown:
                SetVolume(_preferences.Volume - PlayerPreferences.VolumeAdjustmentStep);
                break;
            case ShortcutDefaults.VolumeUp:
                SetVolume(_preferences.Volume + PlayerPreferences.VolumeAdjustmentStep);
                break;
            case ShortcutDefaults.SubtitleDelayEarlier: AdjustSubtitleDelay(-1); break;
            case ShortcutDefaults.SubtitleDelayLater: AdjustSubtitleDelay(1); break;
            case ShortcutDefaults.SubtitleDelayReset: ResetSubtitleDelay(); break;
            case ShortcutDefaults.CloseWindow: Close(); break;
            default: return false;
        }
        return true;
    }

    private string GetCaptureDirectory() => string.IsNullOrWhiteSpace(_preferences.ScreenshotDirectory)
        ? AppStoragePaths.ScreenshotDirectory
        : _preferences.ScreenshotDirectory;

    private void AdjustPlaybackSpeed(double amount) => SetPlaybackSpeed(_playbackSpeed + amount);

    private void SetPlaybackSpeed(double speed)
    {
        _playbackSpeed = PlaybackSpeedRules.Clamp(speed);
        _preferences = _preferences with { PlaybackSpeed = _playbackSpeed };
        Player.SetProperty("speed", _playbackSpeed);
        UpdateSpeedLabel();
        SavePreferences(_preferences);
    }

    private void UpdateSpeedLabel() => SpeedLabel.Text = _playbackSpeed.ToString("0.00", CultureInfo.InvariantCulture) + "×";
}

