using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HanumanInstitute.LibMpv;
using VideoPlayer.Core;

namespace VideoPlayer;

/// <summary>
/// Interactive subtitles: the current subtitle line is redrawn as hoverable words. Hovering shows a translation card,
/// clicking adds the word to the unknown-word list, and listed words are drawn in yellow from then on.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly IBrush UnknownWordBrush = new SolidColorBrush(Color.Parse("#FFD54A"));
    private static readonly IBrush UnknownWordHoverBrush = new SolidColorBrush(Color.Parse("#44FFD54A"));
    private static readonly IBrush KnownWordHoverBrush = new SolidColorBrush(Color.Parse("#3CFFFFFF"));

    private readonly VocabularyStore _vocabulary = new(Path.Combine(AppStoragePaths.DataDirectory, "vocabulary.json"));
    private readonly DispatcherTimer _subtitleWordTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly DispatcherTimer _wordHoverLeaveTimer = new() { Interval = TimeSpan.FromMilliseconds(260) };
    private readonly List<WordChip> _wordChips = [];
    private HttpClient? _translationHttpClient;
    private WordTranslator? _wordTranslator;
    private VocabularyWindow? _vocabularyWindow;
    private WordChip? _hoverChip;
    private CancellationTokenSource? _hoverCancellation;
    private WordTranslation? _hoverTranslation;
    private CancellationTokenSource? _sentenceCancellation;
    private string _overlayText = "";
    private bool _hoverPaused;
    private bool _appliedInteractive;
    private int _vocabularyFlushTicks;

    private sealed class WordChip(Border border, OutlinedText text, string key, string display)
    {
        public Border Border { get; } = border;
        public OutlinedText Text { get; } = text;
        public string Key { get; } = key;
        public string Display { get; } = display;
    }

    private void InitializeVocabulary()
    {
        _translationHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _translationHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        _wordTranslator = new WordTranslator(_translationHttpClient);

        _subtitleWordTimer.Tick += (_, _) => PollSubtitleText();
        _wordHoverLeaveTimer.Tick += (_, _) => EndWordHover();
        VocabularyButton.Click += (_, _) => ToggleVocabularyWindow();
        OpenVocabularyButton.Click += (_, _) =>
        {
            SubtitlePickerPopup.IsOpen = false;
            ToggleVocabularyWindow();
        };
        InteractiveSubtitlesCheckBox.IsCheckedChanged += (_, _) =>
        {
            var enabled = InteractiveSubtitlesCheckBox.IsChecked == true;
            if (_vocabulary.Settings.Enabled == enabled) return;
            _vocabulary.UpdateSettings(_vocabulary.Settings with { Enabled = enabled });
            if (enabled) ShowStatusNotification("Etkileşimli altyazı açık: bir kelimenin üzerine gel, tıklayıp listeye ekle");
        };
        VideoFrame.SizeChanged += (_, _) =>
        {
            if (_overlayText.Length > 0) RenderSubtitleOverlay();
        };
        _vocabulary.Changed += (_, _) => OnVocabularyChanged();

        Closed += (_, _) =>
        {
            _subtitleWordTimer.Stop();
            _wordHoverLeaveTimer.Stop();
            _hoverCancellation?.Cancel();
            CloseSentenceCard();
            _vocabularyWindow?.Close();
            _vocabulary.FlushIfDirty();
            _translationHttpClient?.Dispose();
        };
        ApplyInteractiveSubtitleState();
        OnVocabularyChanged();
    }

    private void OnVocabularyChanged()
    {
        if (_appliedInteractive != _vocabulary.Settings.Enabled) ApplyInteractiveSubtitleState();
        if (InteractiveSubtitlesCheckBox.IsChecked != _vocabulary.Settings.Enabled)
            InteractiveSubtitlesCheckBox.IsChecked = _vocabulary.Settings.Enabled;
        OpenVocabularyButton.Content = $"Bilinmeyen Kelimeler Listesi ({_vocabulary.Count})";
        ToolTip.SetTip(VocabularyButton, $"Bilinmeyen kelimeler listesi ({_vocabulary.Count})");
        RefreshWordChips();
    }

    private void ApplyInteractiveSubtitleState()
    {
        _appliedInteractive = _vocabulary.Settings.Enabled;
        ApplySubtitleAppearancePreferences();
        if (_appliedInteractive)
        {
            _subtitleWordTimer.Start();
            return;
        }
        _subtitleWordTimer.Stop();
        ClearSubtitleOverlay();
    }

    /// <summary>Makes mpv's own text subtitle drawing invisible while the interactive overlay replaces it.</summary>
    private void ApplyNativeSubtitleOverride()
    {
        var hide = _vocabulary.Settings.Enabled;
        try
        {
            Player.SetPropertyString("sub-border-color", hide ? "#00000000" : "#FF000000");
            Player.SetPropertyString("sub-shadow-color", hide ? "#00000000" : "#A0000000");
            if (hide)
            {
                Player.SetPropertyString("sub-color", "#00FFFFFF");
                Player.SetPropertyString("sub-back-color", "#00000000");
                Player.SetProperty("sub-outline-size", 0.0);
                Player.SetProperty("sub-shadow-offset", 0.0);
            }
        }
        catch (MpvException exception)
        {
            AppLogger.Error("Native subtitle drawing could not be switched", exception);
        }
        if (_overlayText.Length > 0) RenderSubtitleOverlay();
    }

    private void PollSubtitleText()
    {
        if (++_vocabularyFlushTicks >= 60)
        {
            _vocabularyFlushTicks = 0;
            _vocabulary.FlushIfDirty();
        }

        var text = "";
        if (!_timelineClosing && _currentVideo is not null && _preferences.SubtitlesVisible)
        {
            try { text = Player.GetProperty<string>("sub-text") ?? ""; }
            catch (Exception exception) when (exception is MpvException or InvalidOperationException) { }
        }
        text = text.Trim();
        if (text == _overlayText) return;

        EndWordHover();
        CloseSentenceCard();
        _overlayText = text;
        RenderSubtitleOverlay();
        foreach (var key in _wordChips.Select(chip => chip.Key).Distinct())
            _vocabulary.RecordSeen(key);
    }

    private void ClearSubtitleOverlay()
    {
        EndWordHover();
        CloseSentenceCard();
        _overlayText = "";
        _wordChips.Clear();
        InteractiveSubtitleHost.Child = null;
        InteractiveSubtitleHost.IsVisible = false;
    }

    private void RenderSubtitleOverlay()
    {
        EndWordHover();
        _wordChips.Clear();
        var lines = SubtitleWordTokenizer.Tokenize(_overlayText);
        if (!_vocabulary.Settings.Enabled || lines.Count == 0)
        {
            InteractiveSubtitleHost.Child = null;
            InteractiveSubtitleHost.IsVisible = false;
            return;
        }

        var frameWidth = Math.Max(VideoFrame.Bounds.Width, 320);
        var frameHeight = Math.Max(VideoFrame.Bounds.Height, 180);
        var scale = Math.Max(0.35, frameHeight / 720.0);
        var family = new FontFamily(SubtitleAppearanceRules.NormalizeFontFamily(_preferences.SubtitleFontFamily));
        var weight = _preferences.SubtitleBold ? FontWeight.Bold : FontWeight.Normal;
        var style = _preferences.SubtitleItalic ? FontStyle.Italic : FontStyle.Normal;
        var typeface = new Typeface(family, style, weight);
        // libass sizes text so that ascent + descent equals the configured size, not the em square.
        var emRatio = 0.9;
        if (FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphs) && glyphs.Metrics is var metrics
            && metrics.DesignEmHeight > 0)
        {
            var cell = Math.Abs(metrics.Ascent) + Math.Abs(metrics.Descent);
            var ratio = (double)metrics.DesignEmHeight / cell;
            if (cell > 0 && ratio is >= 0.6 and <= 1.2) emRatio = ratio;
        }
        var fontSize = Math.Max(8, _preferences.SubtitleFontSize * scale * emRatio);
        var outline = _preferences.SubtitleBackgroundEnabled ? 0 : _preferences.SubtitleOutlineSize * scale;
        var shadow = _preferences.SubtitleBackgroundEnabled ? 0 : _preferences.SubtitleShadowOffset * scale;
        var normalBrush = new SolidColorBrush(Color.Parse(SubtitleAppearanceRules.NormalizeTextColor(_preferences.SubtitleTextColor)));
        var alignment = SubtitleAppearanceRules.NormalizeHorizontalAlignment(_preferences.SubtitleHorizontalAlignment) switch
        {
            SubtitleAppearanceRules.LeftAlignment => HorizontalAlignment.Left,
            SubtitleAppearanceRules.RightAlignment => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center
        };

        OutlinedText CreateText(string value, IBrush brush) => new()
        {
            Text = value, Typeface = typeface, FontSize = fontSize, OutlineWidth = outline, ShadowOffset = shadow,
            Foreground = brush, IsHitTestVisible = false, Margin = new Thickness(-Math.Ceiling(outline + shadow), 0)
        };

        var stack = new StackPanel { HorizontalAlignment = alignment };
        foreach (var line in lines)
        {
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = alignment };
            foreach (var token in line)
            {
                var group = new StackPanel { Orientation = Orientation.Horizontal };
                if (token.Prefix.Length > 0) group.Children.Add(CreateText(token.Prefix, normalBrush));
                if (token.Word is { } word)
                {
                    var text = CreateText(word, normalBrush);
                    var border = new Border
                    {
                        Background = Brushes.Transparent, CornerRadius = new CornerRadius(6), Child = text
                    };
                    var chip = new WordChip(border, text, token.Key!, word);
                    _wordChips.Add(chip);
                    ApplyChipVisual(chip, hovered: false, normalBrush);
                    group.Children.Add(border);
                }
                group.Children.Add(CreateText(token.Suffix + " ", normalBrush));
                wrap.Children.Add(group);
            }
            stack.Children.Add(wrap);
        }

        var host = InteractiveSubtitleHost;
        host.Tag = normalBrush;
        host.Child = stack;
        host.HorizontalAlignment = alignment;
        host.MaxWidth = frameWidth * 0.92;
        var bottom = (100 - _preferences.SubtitlePosition) / 100.0 * frameHeight + _preferences.SubtitleMargin * scale;
        host.Margin = new Thickness(12, 0, 12, Math.Min(bottom, frameHeight * 0.85));
        host.Background = _preferences.SubtitleBackgroundEnabled
            ? new SolidColorBrush(Color.FromArgb((byte)Math.Round(_preferences.SubtitleBackgroundOpacity * 255.0 / 100), 0, 0, 0))
            : null;
        host.Effect = null;
        host.IsVisible = true;
    }

    private bool HitTestSubtitle(Point rootPoint)
    {
        if (InteractiveSubtitleHost.TranslatePoint(new Point(0, 0), RootPanel) is not { } origin) return false;
        return new Rect(origin, InteractiveSubtitleHost.Bounds.Size).Contains(rootPoint);
    }

    private WordChip? HitTestWordChip(Point rootPoint)
    {
        foreach (var chip in _wordChips)
        {
            if (chip.Border.TranslatePoint(new Point(0, 0), RootPanel) is not { } origin) continue;
            var rect = new Rect(origin, chip.Border.Bounds.Size).Inflate(new Thickness(2, 3));
            if (rect.Contains(rootPoint)) return chip;
        }
        return null;
    }

    private void UpdateWordHoverFromPointer(PointerEventArgs e)
    {
        if (!_vocabulary.Settings.Enabled || !InteractiveSubtitleHost.IsVisible || SentenceCard.IsVisible) return;
        var chip = HitTestWordChip(e.GetPosition(RootPanel));
        if (chip is not null)
        {
            if (!ReferenceEquals(chip, _hoverChip)) BeginWordHover(chip);
            else _wordHoverLeaveTimer.Stop();
        }
        else if (_hoverChip is not null && !_wordHoverLeaveTimer.IsEnabled)
        {
            _wordHoverLeaveTimer.Start();
        }
    }

    private bool TryClickWord(PointerPressedEventArgs e)
    {
        if (!_vocabulary.Settings.Enabled || !InteractiveSubtitleHost.IsVisible) return false;
        if (!e.GetCurrentPoint(RootPanel).Properties.IsLeftButtonPressed) return false;
        var chip = HitTestWordChip(e.GetPosition(RootPanel));
        if (chip is null) return false;
        if (!ReferenceEquals(chip, _hoverChip)) BeginWordHover(chip);
        ToggleWordKnown(chip);
        return true;
    }

    private void ApplyChipVisual(WordChip chip, bool hovered, IBrush? normalBrush = null)
    {
        normalBrush ??= InteractiveSubtitleHost.Tag as IBrush ?? Brushes.White;
        var listed = _vocabulary.Contains(chip.Key);
        chip.Text.Foreground = listed ? UnknownWordBrush : normalBrush;
        chip.Border.Background = hovered ? (listed ? UnknownWordHoverBrush : KnownWordHoverBrush) : Brushes.Transparent;
    }

    private void RefreshWordChips()
    {
        foreach (var chip in _wordChips) ApplyChipVisual(chip, ReferenceEquals(chip, _hoverChip));
    }

    private void BeginWordHover(WordChip chip)
    {
        _wordHoverLeaveTimer.Stop();
        if (_hoverChip is not null && !ReferenceEquals(_hoverChip, chip)) ApplyChipVisual(_hoverChip, hovered: false);
        _hoverChip = chip;
        ApplyChipVisual(chip, hovered: true);

        if (_vocabulary.Settings.PauseOnHover && _isPlaying && !_hoverPaused && _currentVideo is not null)
        {
            _hoverPaused = true;
            SetPlayback(play: false);
        }

        _hoverCancellation?.Cancel();
        _hoverCancellation = new CancellationTokenSource();
        _ = LoadWordCardAsync(chip, _hoverCancellation.Token);
    }

    private void EndWordHover()
    {
        _wordHoverLeaveTimer.Stop();
        _hoverCancellation?.Cancel();
        if (_hoverChip is not null) ApplyChipVisual(_hoverChip, hovered: false);
        _hoverChip = null;
        _hoverTranslation = null;
        WordCard.Opacity = 0;
        WordCard.IsVisible = false;
        if (!_hoverPaused) return;
        _hoverPaused = false;
        if (_currentVideo is not null && !_isPlaying && !_timelineClosing) SetPlayback(play: true);
    }

    private void SetPlayback(bool play)
    {
        _isPlaying = play;
        Player.SetProperty("pause", !play);
        UpdatePlaybackButton();
        UpdatePlaybackPollingInterval();
    }

    private async Task LoadWordCardAsync(WordChip chip, CancellationToken token)
    {
        var settings = _vocabulary.Settings;
        _hoverTranslation = null;
        if (_vocabulary.Get(chip.Key) is { Translation.Length: > 0 } saved)
        {
            _hoverTranslation = new WordTranslation(saved.Translation, saved.Alternatives, "");
            UpdateWordCard(chip, loading: false, failed: false);
        }
        else
        {
            UpdateWordCard(chip, loading: true, failed: false);
        }

        try
        {
            if (_hoverTranslation is null)
            {
                await Task.Delay(110, token);
                var translation = await _wordTranslator!.TranslateWordAsync(chip.Key, settings.TargetLanguage, token);
                if (token.IsCancellationRequested) return;
                _hoverTranslation = translation;
                if (_vocabulary.Get(chip.Key) is { Translation.Length: 0 } pending)
                    _vocabulary.Add(pending with { Translation = translation.Text, Alternatives = [.. translation.Alternatives] });
                UpdateWordCard(chip, loading: false, failed: false);
            }

        }
        catch (Exception exception) when (!token.IsCancellationRequested
            && exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (_hoverTranslation is null) UpdateWordCard(chip, loading: false, failed: true);
        }
        catch (OperationCanceledException) { }
    }

    private void CloseSentenceCard()
    {
        _sentenceCancellation?.Cancel();
        _sentenceCancellation?.Dispose();
        _sentenceCancellation = null;
        SentenceCard.IsVisible = false;
    }

    private void ShowSentenceCard(Point rootPoint)
    {
        CloseSentenceCard();
        var sentence = string.Join(' ', _overlayText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (sentence.Length == 0) return;
        EndWordHover();
        SentenceCardText.Text = sentence;
        SentenceCardTranslation.IsVisible = true;
        SentenceCardTranslation.Text = "Çevriliyor…";
        var point = RootPanel.TranslatePoint(rootPoint, WordCardLayer) ?? rootPoint;
        SentenceCard.IsVisible = true;
        PositionSentenceCard(point);
        _sentenceCancellation = new CancellationTokenSource();
        _ = TranslateSentenceCardAsync(sentence, point, _sentenceCancellation.Token);
    }

    private void PositionSentenceCard(Point point)
    {
        var width = Math.Max(0, Math.Min(380, WordCardLayer.Bounds.Width - 16));
        SentenceCard.Width = width;
        SentenceCard.Measure(new Size(width, double.PositiveInfinity));
        Canvas.SetLeft(SentenceCard, Math.Clamp(point.X - width / 2, 8, Math.Max(8, WordCardLayer.Bounds.Width - width - 8)));
        Canvas.SetTop(SentenceCard, Math.Max(8, point.Y - SentenceCard.DesiredSize.Height - 12));
    }

    private async Task TranslateSentenceCardAsync(string sentence, Point point, CancellationToken token)
    {
        try
        {
            var translated = await _wordTranslator!.TranslateSentenceAsync(sentence, _vocabulary.Settings.TargetLanguage, token);
            if (!token.IsCancellationRequested)
            {
                SentenceCardTranslation.Text = translated;
                PositionSentenceCard(point);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            if (!token.IsCancellationRequested)
            {
                SentenceCardTranslation.Text = "Çeviri alınamadı";
                PositionSentenceCard(point);
            }
        }
    }

    private void UpdateWordCard(WordChip chip, bool loading, bool failed)
    {
        if (!ReferenceEquals(chip, _hoverChip)) return;
        var listed = _vocabulary.Contains(chip.Key);
        var pair = _hoverTranslation is { SourceLanguage.Length: > 0 } t
            ? $"   ·   {t.SourceLanguage.ToUpperInvariant()} → {_vocabulary.Settings.TargetLanguage.ToUpperInvariant()}" : "";
        WordCardWord.Text = chip.Display + pair;
        WordCardTranslation.Text = failed ? "Çeviri alınamadı"
            : loading ? "Çevriliyor…" : _hoverTranslation?.Text ?? "";
        WordCardTranslation.Foreground = new SolidColorBrush(Color.Parse(failed ? "#FF8A80" : loading ? "#8C8C8C" : "#F5F5F5"));
        var alternatives = _hoverTranslation?.Alternatives ?? [];
        WordCardAlternatives.IsVisible = alternatives.Count > 0;
        WordCardAlternatives.Text = string.Join("\n", alternatives);
        WordCardBadge.IsVisible = listed;
        WordCardBadgeText.Text = "★ LİSTEDE";
        WordCardHint.Text = failed ? "İnternet bağlantını kontrol et. Yine de tıklayarak listeye ekleyebilirsin."
            : listed ? "Tıkla: listeden çıkar" : "Tıkla: bilinmeyen kelimelere ekle";
        PositionWordCard(chip);
        WordCard.IsVisible = true;
        WordCard.Opacity = 1;
    }

    private void PositionWordCard(WordChip chip)
    {
        var layerWidth = WordCardLayer.Bounds.Width;
        var layerHeight = WordCardLayer.Bounds.Height;
        if (chip.Border.TranslatePoint(new Point(0, 0), WordCardLayer) is not { } origin || layerWidth <= 0) return;
        WordCard.Measure(new Size(Math.Max(190, layerWidth - 16), double.PositiveInfinity));
        var size = WordCard.DesiredSize;
        var x = Math.Clamp(origin.X + chip.Border.Bounds.Width / 2 - size.Width / 2, 8, Math.Max(8, layerWidth - size.Width - 8));
        var y = origin.Y - size.Height - 10;
        if (y < 8) y = Math.Min(origin.Y + chip.Border.Bounds.Height + 10, Math.Max(8, layerHeight - size.Height - 8));
        Canvas.SetLeft(WordCard, x);
        Canvas.SetTop(WordCard, y);
    }

    private void ToggleWordKnown(WordChip chip)
    {
        if (_vocabulary.Contains(chip.Key))
        {
            _vocabulary.Remove(chip.Key);
            ShowStatusNotification($"“{chip.Display}” listeden çıkarıldı");
        }
        else
        {
            _vocabulary.Add(new VocabularyEntry
            {
                Word = chip.Key,
                Display = chip.Display,
                Translation = _hoverTranslation?.Text ?? "",
                Alternatives = [.. _hoverTranslation?.Alternatives ?? []],
                Context = string.Join(' ', _overlayText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                Source = _currentVideo is { } video ? Path.GetFileNameWithoutExtension(video) : "",
                AddedAt = DateTime.Now
            });
            ShowStatusNotification($"“{chip.Display}” bilinmeyen kelimelere eklendi");
        }
        if (ReferenceEquals(chip, _hoverChip)) UpdateWordCard(chip, loading: false, failed: false);
    }

    private void ToggleVocabularyWindow()
    {
        if (_vocabularyWindow is { } open)
        {
            open.Close();
            return;
        }
        _vocabularyWindow = new VocabularyWindow(_vocabulary);
        _vocabularyWindow.Closed += (_, _) => _vocabularyWindow = null;
        _vocabularyWindow.Show(this);
    }
}
