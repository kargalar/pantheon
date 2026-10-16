using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HanumanInstitute.LibMpv;
using System.Diagnostics;
using System.Collections.ObjectModel;
using VideoPlayer.Core;

namespace VideoPlayer;

public sealed record TimelinePreviewFrame(Bitmap? Bitmap, string Label);

public sealed partial class MainWindow
{
    private sealed record ChapterTimelineMarker(double StartTime, string Title);

    private static readonly SolidColorBrush ChapterMarkerBrush = new(Color.Parse("#FFF0B3"));
    private const string SeekSliderHelpText = UiText.DragToSeekHoldAndPullUpwardFor;
    private const double ChapterMarkerTooltipDistance = 10;
    private static readonly TimeSpan SeekInterval = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(150);
    private const double PreviewRevealDistance = 32;
    private const double PreviewHideDistance = 12;
    private const int PreviewThumbnailWidth = 146;
    private const double PreviewPanelWidth = 1380;
    private const int PreviewCacheRadius = 20;
    private const int PreviewSeekPollIntervalMilliseconds = 15;
    private static readonly TimeSpan PreviewSeekTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PreviewFrameSettleDelay = TimeSpan.FromMilliseconds(20);
    private readonly DispatcherTimer _seekTimer = new() { Interval = SeekInterval };
    private readonly Dictionary<long, Bitmap> _previewCache = [];
    private readonly ObservableCollection<TimelinePreviewFrame> _previewFrames = [];
    private IReadOnlyList<ChapterTimelineMarker> _chapterTimelineMarkers = Array.Empty<ChapterTimelineMarker>();
    private Task _previewTask = Task.CompletedTask;
    private CancellationTokenSource? _previewCancellation;
    private long? _previewCaptureCenterIndex;
    private IPointer? _seekPointer;
    private bool _updatingTimeline;
    private bool _timelineClosing;
    private bool _resumeAfterSeek;
    private bool _seekPending;
    private bool _previewFailed;
    private double _seekDuration;
    private double _seekTarget;
    private long _seekRevision;
    private long _previewCacheGeneration;
    private long? _lastPreviewCenterIndex;
    private DateTime _lastPreview = DateTime.MinValue;
    private DateTime _lastSeek = DateTime.MinValue;
    private double _lastSeekTarget;
    private double _lastPreviewTarget = double.NaN;

    private void InitializeTimeline()
    {
        SeekPreviewItems.ItemsSource = _previewFrames;
        ChapterMarkers.SizeChanged += (_, _) => RenderChapterMarkers();
        // Slider's Thumb handles pointer events. Own the gesture in the tunnel,
        // including capture, so dragging outside the bar still seeks and releases.
        SeekSlider.AddHandler(PointerPressedEvent, OnTimelinePressed, RoutingStrategies.Tunnel, true);
        SeekSlider.AddHandler(PointerMovedEvent, OnTimelineMoved, RoutingStrategies.Tunnel, true);
        SeekSlider.AddHandler(PointerReleasedEvent, OnTimelineReleased, RoutingStrategies.Tunnel, true);
        SeekSlider.PointerCaptureLost += (_, _) => EndTimelineDrag();
        SeekSlider.ValueChanged += (_, e) =>
        {
            if (_updatingTimeline || _isDraggingSeek || _currentVideo is null) return;
            ResetDiscreteSeekBurst();
            _pendingResume = null;
            _pendingResumeStartPositionSeconds = null;
            _pendingResumeTargetPositionSeconds = null;
            var duration = Player.GetProperty<double>("duration");
            if (duration > 0)
            {
                var targetPosition = TimelinePosition.SecondsFromPercent(e.NewValue, duration);
                Player.Seek(targetPosition, SeekOption.Absolute | SeekOption.Keyframes).Invoke();
                ShowBottomTemporarily();
            }
        };
        _seekTimer.Tick += (_, _) => RefreshTimelineDrag();
    }

    private void SetTimelineChapters(IReadOnlyList<ChapterTimelineMarker> chapters)
    {
        _chapterTimelineMarkers = chapters;
        RenderChapterMarkers();
    }

    private void RenderChapterMarkers()
    {
        ChapterMarkers.Children.Clear();
        var duration = _videoDuration;
        var width = ChapterMarkers.Bounds.Width;
        var height = ChapterMarkers.Bounds.Height;
        if (!double.IsFinite(duration) || duration <= 0 || width <= 0 || height <= 0) return;

        foreach (var chapter in _chapterTimelineMarkers)
        {
            if (!double.IsFinite(chapter.StartTime) || chapter.StartTime < 0 || chapter.StartTime > duration) continue;
            var markerLine = new Border
            {
                Width = 2,
                Height = Math.Min(12, height),
                CornerRadius = new CornerRadius(1),
                Background = ChapterMarkerBrush,
                Opacity = 0.9
            };
            var marker = new Border
            {
                Width = 12,
                Height = height,
                Background = Brushes.Transparent,
                Child = markerLine
            };
            Canvas.SetLeft(marker, Math.Clamp(chapter.StartTime / duration * width - marker.Width / 2, 0, width - marker.Width));
            Canvas.SetTop(marker, 0);
            ChapterMarkers.Children.Add(marker);
        }
    }

    private void OnTimelinePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || _currentVideo is null || !e.GetCurrentPoint(SeekSlider).Properties.IsLeftButtonPressed) return;
        ResetDiscreteSeekBurst();
        _seekDuration = Player.GetProperty<double>("duration");
        if (!double.IsFinite(_seekDuration) || _seekDuration <= 0) return;
        _pendingResume = null;
        _pendingResumeStartPositionSeconds = null;
        _pendingResumeTargetPositionSeconds = null;
        _resumeAfterSeek = !Player.GetProperty<bool>("pause");
        _isDraggingSeek = true;
        UpdatePlaybackPollingInterval();
        _seekRevision++;
        _lastPreviewCenterIndex = null;
        _lastPreviewTarget = double.NaN;
        _lastSeek = DateTime.MinValue;
        _previewFailed = false;
        _seekPointer = e.Pointer;
        Player.SetProperty("pause", true);
        e.Pointer.Capture(SeekSlider);
        UpdateControlVisibility();
        UpdateTimelineTarget(e.GetPosition(SeekSlider));
        RefreshTimelineDrag();
        _seekTimer.Start();
        e.Handled = true;
    }

    private void OnTimelineMoved(object? sender, PointerEventArgs e)
    {
        UpdateChapterTooltip(e.GetPosition(SeekSlider));
        if (!_isDraggingSeek || e.Pointer != _seekPointer) return;
        UpdateTimelineTarget(e.GetPosition(SeekSlider));
        e.Handled = true;
    }

    private void UpdateChapterTooltip(Point position)
    {
        var duration = _videoDuration;
        var width = SeekSlider.Bounds.Width;
        var nearestChapter = duration > 0 && width > 0
            ? _chapterTimelineMarkers
                .Select(chapter => new
                {
                    chapter.Title,
                    Distance = Math.Abs(chapter.StartTime / duration * width - position.X)
                })
                .MinBy(chapter => chapter.Distance)
            : null;

        var tooltip = nearestChapter is not null && nearestChapter.Distance <= ChapterMarkerTooltipDistance
            ? nearestChapter.Title
            : SeekSliderHelpText;
        ToolTip.SetTip(SeekSlider, tooltip);
    }

    private void OnTimelineReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingSeek || e.Pointer != _seekPointer) return;
        UpdateTimelineTarget(e.GetPosition(SeekSlider));
        EndTimelineDrag();
        e.Handled = true;
    }

    private void UpdateTimelineTarget(Point position)
    {
        var percent = TimelinePosition.PercentFromPointer(position.X, SeekSlider.Bounds.Width);
        _seekTarget = TimelinePosition.SecondsFromPercent(percent, _seekDuration);
        if (SeekPreviewPanel.IsVisible && _previewCaptureCenterIndex is not null
            && double.IsFinite(_lastPreviewTarget) && Math.Abs(_seekTarget - _lastPreviewTarget) >= 0.1)
            CancelPreviewCapture();
        _updatingTimeline = true;
        SeekSlider.Value = percent;
        _updatingTimeline = false;
        _seekPending = true;
        var showPreview = position.Y < -(SeekPreviewPanel.IsVisible ? PreviewHideDistance : PreviewRevealDistance);
        SeekPreviewPanel.IsVisible = showPreview;
        var panelWidth = Math.Min(PreviewPanelWidth, Math.Max(900, BottomBar.Bounds.Width - 24));
        var panelOffsetLimit = SeekSlider.Bounds.Width - panelWidth;
        var minLeft = Math.Min(panelOffsetLimit, 0);
        var maxLeft = Math.Max(panelOffsetLimit, 0);
        var left = Math.Clamp(position.X - panelWidth / 2.0, minLeft, maxLeft);
        SeekPreviewPanel.Width = panelWidth;
        SeekPreviewPanel.Margin = new Thickness(left, 0, 0, 8);
        Span<TimelinePreviewSample> sampleBuffer = stackalloc TimelinePreviewSample[TimelinePreviewSampling.VisibleSampleCount];
        var sampleCount = TimelinePreviewSampling.FillSamples(_seekTarget, _seekDuration, sampleBuffer);
        if (sampleCount == 0) return;
        var samples = sampleBuffer[..sampleCount];
        var centerIndex = TimelinePreviewSampling.GetClosestSample(samples, _seekTarget).Index;
        if (!showPreview) ClearSeekPreview();
        else if (_lastPreviewCenterIndex != centerIndex) ShowCachedPreviewFrames(samples);
        TrimPreviewCache(centerIndex);
        if (!showPreview)
        {
            CancelPreviewCapture();
            _lastPreviewCenterIndex = null;
            return;
        }

        if (_previewCaptureCenterIndex is { } activeCenter && activeCenter != centerIndex)
            CancelPreviewCapture();
    }

    private void RefreshTimelineDrag()
    {
        if (!_isDraggingSeek || _timelineClosing) return;
        if (!_previewTask.IsCompleted) return;
        if (_seekPending && DateTime.UtcNow - _lastSeek >= SeekInterval)
        {
            Player.Seek(_seekTarget, SeekOption.Absolute | SeekOption.Keyframes).Invoke();
            _lastSeekTarget = _seekTarget;
            _lastSeek = DateTime.UtcNow;
            _seekPending = false;
            return;
        }
        if (!SeekPreviewPanel.IsVisible || _previewFailed
            || DateTime.UtcNow - _lastPreview < PreviewInterval || Player.GetProperty<bool>("seeking")) return;
        if (Math.Abs(Player.GetProperty<double>("time-pos") - _lastSeekTarget) > 0.25) return;
        var samples = TimelinePreviewSampling.GetSamples(_seekTarget, _seekDuration);
        if (samples.Count == 0) return;
        var centerSample = TimelinePreviewSampling.GetClosestSample(samples, _seekTarget);
        if (_lastPreviewCenterIndex == centerSample.Index
            && double.IsFinite(_lastPreviewTarget) && Math.Abs(_lastPreviewTarget - _seekTarget) < 0.1) return;
        if (_previewCache.Remove(centerSample.Index, out var staleCenter)) staleCenter.Dispose();
        _lastPreview = DateTime.UtcNow;
        _lastPreviewCenterIndex = centerSample.Index;
        _lastPreviewTarget = _seekTarget;
        ShowCachedPreviewFrames(samples);
        _previewCaptureCenterIndex = centerSample.Index;
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        _previewTask = RefreshSeekPreviewAsync(
            _seekRevision, _previewCacheGeneration, samples, centerSample.Index, _currentVideo!, cancellation);
    }

    private async Task RefreshSeekPreviewAsync(long revision, long cacheGeneration,
        IReadOnlyList<TimelinePreviewSample> samples, long centerIndex, string videoPath,
        CancellationTokenSource cancellation)
    {
        var player = Player;
        var cancellationToken = cancellation.Token;
        try
        {
            var captureOrder = samples.OrderBy(sample => Math.Abs(sample.Index - centerIndex)).ToArray();
            foreach (var sample in captureOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_timelineClosing || revision != _seekRevision || cacheGeneration != _previewCacheGeneration) return;
                if (_previewCache.ContainsKey(sample.Index)) continue;
                var bitmap = sample.Index == centerIndex
                    ? await VideoSurface.CapturePreviewFrameAsync(PreviewThumbnailWidth)
                    : await CapturePreviewFrameAsync(player, sample.PositionSeconds, cancellationToken);
                if (cancellationToken.IsCancellationRequested || cacheGeneration != _previewCacheGeneration)
                {
                    bitmap.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                    return;
                }
                _previewCache[sample.Index] = bitmap;

                if (CanPublishPreview(revision, cacheGeneration, videoPath))
                    ShowCachedPreviewFrames(TimelinePreviewSampling.GetSamples(_seekTarget, _seekDuration), showPlaceholders: true);
            }

            if (_timelineClosing || !_isDraggingSeek || revision != _seekRevision || cacheGeneration != _previewCacheGeneration) return;
            var currentSamples = TimelinePreviewSampling.GetSamples(_seekTarget, _seekDuration);
            var currentCenter = TimelinePreviewSampling.GetClosestSample(currentSamples, _seekTarget).Index;
            if (currentCenter == centerIndex && string.Equals(videoPath, _currentVideo, StringComparison.OrdinalIgnoreCase))
                ShowCachedPreviewFrames(currentSamples);
            TrimPreviewCache(centerIndex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (revision == _seekRevision && cacheGeneration == _previewCacheGeneration)
            {
                _previewFailed = true;
                SeekPreviewTime.Text = UiText.FramePreviewIsUnavailable;
                AppLogger.Error("Timeline frame strip preview failed", exception);
            }
        }
        finally
        {
            if (!_timelineClosing && _isDraggingSeek && revision == _seekRevision
                && cacheGeneration == _previewCacheGeneration
                && string.Equals(videoPath, _currentVideo, StringComparison.OrdinalIgnoreCase))
                player.Seek(_seekTarget, SeekOption.Absolute | SeekOption.Keyframes).Invoke();
            if (ReferenceEquals(_previewCancellation, cancellation))
                _previewCancellation = null;
            if (_previewCaptureCenterIndex == centerIndex)
                _previewCaptureCenterIndex = null;
            cancellation.Dispose();
        }
    }

    private bool CanPublishPreview(long revision, long cacheGeneration, string videoPath)
    {
        if (_timelineClosing || !_isDraggingSeek || revision != _seekRevision
            || cacheGeneration != _previewCacheGeneration
            || !string.Equals(videoPath, _currentVideo, StringComparison.OrdinalIgnoreCase))
            return false;

        var samples = TimelinePreviewSampling.GetSamples(_seekTarget, _seekDuration);
        if (samples.Count == 0) return false;
        var centerIndex = TimelinePreviewSampling.GetClosestSample(samples, _seekTarget).Index;
        return centerIndex == _previewCaptureCenterIndex && _previewCache.ContainsKey(centerIndex);
    }

    private static async Task<Bitmap> CapturePreviewFrameAsync(MpvContext player, double position,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(Path.GetTempPath(), $"Pantheon-preview-{Guid.NewGuid():N}.jpg");
            try
            {
                var positionBeforeSeek = player.GetProperty<double>("time-pos");
                player.Seek(position, SeekOption.Absolute | SeekOption.Keyframes).Invoke();
                var seekTimer = Stopwatch.StartNew();
                var reachedPosition = false;
                while (seekTimer.Elapsed < PreviewSeekTimeout)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var isSeeking = player.GetProperty<bool>("seeking");
                    var currentPosition = player.GetProperty<double>("time-pos");
                    if (!isSeeking && (PlaybackSeekRules.HasReachedTarget(false, currentPosition, position)
                        || Math.Abs(currentPosition - positionBeforeSeek) > PlaybackSeekRules.TargetToleranceSeconds
                        || seekTimer.Elapsed >= PreviewFrameSettleDelay))
                    {
                        reachedPosition = true;
                        break;
                    }
                    cancellationToken.WaitHandle.WaitOne(PreviewSeekPollIntervalMilliseconds);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (!reachedPosition)
                    throw new TimeoutException("The player did not reach the requested timeline preview position in time.");
                cancellationToken.ThrowIfCancellationRequested();
                cancellationToken.WaitHandle.WaitOne(PreviewFrameSettleDelay);
                cancellationToken.ThrowIfCancellationRequested();
                // Window mode preserves the displayed tone mapping for HDR previews.
                player.ScreenshotToFile(path, ScreenshotOptions.Window).Invoke();
                using var stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, PreviewThumbnailWidth);
            }
            finally
            {
                try { File.Delete(path); }
                catch (IOException exception) { AppLogger.Error("Timeline preview cleanup failed", exception); }
                catch (UnauthorizedAccessException exception) { AppLogger.Error("Timeline preview cleanup failed", exception); }
            }
        });
    }

    private void ShowCachedPreviewFrames(IReadOnlyList<TimelinePreviewSample> samples, bool showPlaceholders = false)
    {
        _previewFrames.Clear();
        var centerIndex = samples.Count == 0
            ? -1
            : samples.MinBy(sample => Math.Abs(sample.PositionSeconds - _seekTarget)).Index;
        var visibleSamples = TimelinePreviewSampling.GetVisibleSamples(
            samples, _previewCache.Keys, centerIndex, showPlaceholders);
        foreach (var sample in visibleSamples)
        {
            if (_previewCache.TryGetValue(sample.Index, out var bitmap))
                _previewFrames.Add(new TimelinePreviewFrame(bitmap,
                    FormatPreviewTimestamp(sample.Index == centerIndex ? _seekTarget : sample.PositionSeconds)));
            else if (showPlaceholders)
                _previewFrames.Add(new TimelinePreviewFrame(null, FormatPreviewTimestamp(sample.PositionSeconds)));
        }
        SeekPreviewTime.Text = _previewFrames.Count == samples.Count
            ? UiText.PreviousAndNextFramesSpacingAdaptsToThe
            : UiText.LoadingNearbyFrames;
    }

    private void ShowCachedPreviewFrames(ReadOnlySpan<TimelinePreviewSample> samples)
    {
        _previewFrames.Clear();
        var centerIndex = samples.IsEmpty ? -1 : TimelinePreviewSampling.GetClosestSample(samples, _seekTarget).Index;
        if (samples.IsEmpty || !_previewCache.ContainsKey(centerIndex))
        {
            SeekPreviewTime.Text = UiText.LoadingNearbyFrames;
            return;
        }

        var visibleCount = 0;
        for (var index = 0; index < samples.Length; index++)
            if (_previewCache.ContainsKey(samples[index].Index)) visibleCount++;

        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            if (_previewCache.TryGetValue(sample.Index, out var bitmap))
                _previewFrames.Add(new TimelinePreviewFrame(bitmap,
                    FormatPreviewTimestamp(sample.Index == centerIndex ? _seekTarget : sample.PositionSeconds)));
        }

        SeekPreviewTime.Text = visibleCount == samples.Length
            ? UiText.PreviousAndNextFramesSpacingAdaptsToThe
            : UiText.LoadingNearbyFrames;
    }

    private static string FormatPreviewTimestamp(double seconds)
        => MediaTimeFormatter.FormatPreviewTime(seconds);

    private void TrimPreviewCache(long centerIndex)
    {
        foreach (var key in TimelinePreviewCacheRules.GetIndexesOutsideRadius(
            _previewCache.Keys, centerIndex, PreviewCacheRadius))
        {
            _previewCache[key].Dispose();
            _previewCache.Remove(key);
        }
    }

    private void ResetPreviewCache()
    {
        _previewCacheGeneration++;
        CancelPreviewCapture();
        _lastPreviewCenterIndex = null;
        foreach (var bitmap in _previewCache.Values) bitmap.Dispose();
        _previewCache.Clear();
        _previewFrames.Clear();
    }

    private void EndTimelineDrag()
    {
        if (!_isDraggingSeek) return;
        ResetDiscreteSeekBurst();
        _isDraggingSeek = false;
        _seekTimer.Stop();
        _seekRevision++;
        CancelPreviewCapture();
        _seekPointer?.Capture(null);
        _seekPointer = null;
        SeekPreviewPanel.IsVisible = false;
        ClearSeekPreview();
        TrimPreviewCacheAroundPosition(_seekTarget);
        if (!_timelineClosing)
        {
            Player.Seek(_seekTarget, SeekOption.Absolute | SeekOption.Exact).Invoke();
            ShowBottomTemporarily();
            Player.SetProperty("pause", !_resumeAfterSeek);
            _isPlaying = _resumeAfterSeek;
            PlayButton.Content = _isPlaying ? "Ⅱ" : "▶";
            UpdatePlaybackPollingInterval();
            UpdateControlVisibility();
        }
    }

    private void CancelPreviewCapture()
    {
        _previewCaptureCenterIndex = null;
        _previewCancellation?.Cancel();
    }

    private void TrimPreviewCacheAroundPosition(double positionSeconds)
    {
        var samples = TimelinePreviewSampling.GetSamples(positionSeconds, _seekDuration);
        if (samples.Count == 0) return;
        var centerIndex = TimelinePreviewSampling.GetClosestSample(samples, positionSeconds).Index;
        TrimPreviewCache(centerIndex);
    }

    private void ClearSeekPreview()
    {
        _previewFrames.Clear();
        SeekPreviewTime.Text = UiText.LoadingNearbyFrames;
    }
}
