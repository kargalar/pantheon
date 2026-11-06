using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VideoPlayer.Core;

namespace VideoPlayer;

/// <summary>Non-modal window that lists the unknown words and holds the word translation settings.</summary>
internal sealed class VocabularyWindow : Window
{
    private static readonly IBrush Yellow = new SolidColorBrush(Color.Parse("#FFD54A"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8C8C8C"));
    private static readonly IBrush Soft = new SolidColorBrush(Color.Parse("#C9C9C9"));
    private static readonly IBrush CardBrush = new SolidColorBrush(Color.Parse("#1A1A1A"));
    private static readonly IBrush CardBorder = new SolidColorBrush(Color.Parse("#2D2D2D"));

    private readonly VocabularyStore _store;
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly TextBlock _countLabel = new();
    private readonly TextBlock _emptyNotice = new();
    private readonly TextBox _search = new();
    private readonly ComboBox _sort = new();
    private readonly Button _clearButton = new();
    private readonly DispatcherTimer _clearConfirmTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _updatingSettings;
    private bool _clearArmed;

    private readonly CheckBox _enabledBox = new() { Content = "Etkileşimli altyazı (üzerine gel: çeviri, tıkla: listeye ekle)" };
    private readonly CheckBox _pauseBox = new() { Content = "Kelimenin üzerine gelince videoyu duraklat" };
    private readonly CheckBox _sentenceBox = new() { Content = "Çeviri kartında cümlenin tamamını da çevir" };
    private readonly ComboBox _language = new() { MinWidth = 170 };

    public VocabularyWindow(VocabularyStore store)
    {
        _store = store;
        Title = "Bilinmeyen Kelimeler";
        Width = 780;
        Height = 700;
        MinWidth = 560;
        MinHeight = 480;
        Background = new SolidColorBrush(Color.Parse("#101010"));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildLayout();

        foreach (var (code, name) in WordTranslator.Languages)
            _language.Items.Add(new ComboBoxItem { Content = $"{name}  ({code})", Tag = code });
        foreach (var label in new[] { "En yeni", "A → Z", "En çok karşılaşılan" })
            _sort.Items.Add(label);
        _sort.SelectedIndex = 0;

        _enabledBox.IsCheckedChanged += (_, _) => SaveSettings();
        _pauseBox.IsCheckedChanged += (_, _) => SaveSettings();
        _sentenceBox.IsCheckedChanged += (_, _) => SaveSettings();
        _language.SelectionChanged += (_, _) => SaveSettings();
        _search.TextChanged += (_, _) => Rebuild();
        _sort.SelectionChanged += (_, _) => Rebuild();
        _clearConfirmTimer.Tick += (_, _) => DisarmClear();

        _store.Changed += OnStoreChanged;
        Closed += (_, _) =>
        {
            _store.Changed -= OnStoreChanged;
            _clearConfirmTimer.Stop();
        };
        LoadSettings();
        Rebuild();
    }

    private Control BuildLayout()
    {
        var title = new TextBlock
        {
            Text = "Bilinmeyen Kelimeler", FontSize = 22, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#F5F5F5")), VerticalAlignment = VerticalAlignment.Center
        };
        _countLabel.FontSize = 12;
        _countLabel.FontWeight = FontWeight.SemiBold;
        _countLabel.Foreground = Brushes.Black;
        var countBadge = new Border
        {
            Background = Yellow, CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2),
            VerticalAlignment = VerticalAlignment.Center, Child = _countLabel
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { title, countBadge } };

        var settings = new Border
        {
            Background = CardBrush, BorderBrush = CardBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 12),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "AYARLAR", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Muted },
                    _enabledBox,
                    _pauseBox,
                    _sentenceBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 4, 0, 0),
                        Children =
                        {
                            new TextBlock { Text = "Çeviri dili", Foreground = Soft, VerticalAlignment = VerticalAlignment.Center },
                            _language
                        }
                    }
                }
            }
        };

        _search.PlaceholderText = "Kelime, çeviri veya cümle ara…";
        _search.MinHeight = 36;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _sort.MinHeight = 36;
        _sort.MinWidth = 170;
        var exportButton = new Button { Content = "⭳ Dışa aktar (TSV)", Height = 36 };
        exportButton.Click += async (_, _) => await ExportAsync();
        _clearButton.Content = "Tümünü temizle";
        _clearButton.Height = 36;
        _clearButton.Click += (_, _) => OnClearClicked();

        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 8 };
        toolbar.Children.Add(_search);
        Grid.SetColumn(_sort, 1);
        Grid.SetColumn(exportButton, 2);
        Grid.SetColumn(_clearButton, 3);
        toolbar.Children.Add(_sort);
        toolbar.Children.Add(exportButton);
        toolbar.Children.Add(_clearButton);

        _emptyNotice.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyNotice.TextAlignment = TextAlignment.Center;
        _emptyNotice.TextWrapping = TextWrapping.Wrap;
        _emptyNotice.Foreground = Muted;
        _emptyNotice.Margin = new Thickness(0, 60);

        var list = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel { Spacing = 8, Children = { _rows, _emptyNotice } }
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"), RowSpacing = 14, Margin = new Thickness(22, 18) };
        root.Children.Add(header);
        Grid.SetRow(settings, 1);
        Grid.SetRow(toolbar, 2);
        Grid.SetRow(list, 3);
        root.Children.Add(settings);
        root.Children.Add(toolbar);
        root.Children.Add(list);
        return root;
    }

    private void LoadSettings()
    {
        _updatingSettings = true;
        var settings = _store.Settings;
        _enabledBox.IsChecked = settings.Enabled;
        _pauseBox.IsChecked = settings.PauseOnHover;
        _sentenceBox.IsChecked = settings.ShowSentenceTranslation;
        var index = 0;
        for (var i = 0; i < _language.Items.Count; i++)
            if (_language.Items[i] is ComboBoxItem { Tag: string code }
                && string.Equals(code, settings.TargetLanguage, StringComparison.OrdinalIgnoreCase)) index = i;
        _language.SelectedIndex = index;
        _updatingSettings = false;
    }

    private void SaveSettings()
    {
        if (_updatingSettings) return;
        var code = (_language.SelectedItem as ComboBoxItem)?.Tag as string ?? _store.Settings.TargetLanguage;
        _store.UpdateSettings(new VocabularySettings
        {
            Enabled = _enabledBox.IsChecked == true,
            PauseOnHover = _pauseBox.IsChecked == true,
            ShowSentenceTranslation = _sentenceBox.IsChecked == true,
            TargetLanguage = code
        });
    }

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LoadSettings();
            Rebuild();
        });
    }

    private void Rebuild()
    {
        var query = _search.Text?.Trim() ?? "";
        IEnumerable<VocabularyEntry> entries = _store.Words;
        if (query.Length > 0)
            entries = entries.Where(entry => Contains(entry.Word, query) || Contains(entry.Display, query)
                || Contains(entry.Translation, query) || Contains(entry.Context, query)
                || entry.Alternatives.Any(alternative => Contains(alternative, query)));
        entries = _sort.SelectedIndex switch
        {
            1 => entries.OrderBy(entry => entry.Word, StringComparer.CurrentCultureIgnoreCase),
            2 => entries.OrderByDescending(entry => entry.SeenCount).ThenByDescending(entry => entry.AddedAt),
            _ => entries.OrderByDescending(entry => entry.AddedAt)
        };

        _rows.Children.Clear();
        var shown = 0;
        foreach (var entry in entries)
        {
            _rows.Children.Add(BuildRow(entry));
            shown++;
        }

        _countLabel.Text = query.Length > 0 ? $"{shown} / {_store.Count}" : _store.Count.ToString();
        _emptyNotice.IsVisible = shown == 0;
        _emptyNotice.Text = _store.Count == 0
            ? "Henüz kelime eklemedin.\n\nAltyazıdaki bir kelimenin üzerine gelince çevirisini görürsün;\ntıklarsan buraya eklenir ve bir daha sarı renkte görünür."
            : "Aramayla eşleşen kelime yok.";
        _clearButton.IsEnabled = _store.Count > 0;

        static bool Contains(string value, string query) =>
            value.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private Control BuildRow(VocabularyEntry entry)
    {
        var display = entry.Display.Length > 0 ? entry.Display : entry.Word;
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        head.Children.Add(new TextBlock
        {
            Text = display, FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Yellow, VerticalAlignment = VerticalAlignment.Center
        });
        if (entry.Translation.Length > 0)
        {
            head.Children.Add(new TextBlock { Text = "→", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock
            {
                Text = entry.Translation, FontSize = 16, Foreground = new SolidColorBrush(Color.Parse("#F5F5F5")),
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 380
            });
        }

        var body = new StackPanel { Spacing = 3 };
        body.Children.Add(head);
        if (entry.Alternatives.Count > 0)
            body.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", entry.Alternatives), FontSize = 11.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap
            });
        if (entry.Context.Length > 0)
            body.Children.Add(new TextBlock
            {
                Text = $"“{entry.Context}”", FontSize = 12, FontStyle = FontStyle.Italic, Foreground = Soft,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0)
            });
        var meta = new List<string>();
        if (entry.Source.Length > 0) meta.Add("🎞 " + entry.Source);
        meta.Add("🗓 " + entry.AddedAt.ToString("d MMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("tr-TR")));
        meta.Add($"👁 {entry.SeenCount}×");
        body.Children.Add(new TextBlock
        {
            Text = string.Join("   ", meta), FontSize = 10.5, Foreground = new SolidColorBrush(Color.Parse("#6E6E6E")),
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0)
        });

        var copy = new Button { Content = "⧉", Width = 34, Height = 34, Padding = new Thickness(0), FontSize = 14 };
        ToolTip.SetTip(copy, "Kopyala");
        copy.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard)
                await clipboard.SetTextAsync(entry.Translation.Length > 0 ? $"{display} — {entry.Translation}" : display);
        };
        var learned = new Button { Content = "✓ Öğrendim", Height = 34, Padding = new Thickness(12, 0), FontSize = 12 };
        ToolTip.SetTip(learned, "Kelimeyi listeden çıkar");
        learned.Click += (_, _) => _store.Remove(entry.Word);

        var actions = new StackPanel
        {
            Spacing = 6, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { learned, copy }
        };
        copy.HorizontalAlignment = HorizontalAlignment.Right;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 14 };
        grid.Children.Add(body);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        return new Border
        {
            Background = CardBrush, BorderBrush = CardBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12), Child = grid
        };
    }

    private void OnClearClicked()
    {
        if (!_clearArmed)
        {
            _clearArmed = true;
            _clearButton.Content = "Emin misin? Tekrar tıkla";
            _clearConfirmTimer.Start();
            return;
        }
        DisarmClear();
        _store.Clear();
    }

    private void DisarmClear()
    {
        _clearConfirmTimer.Stop();
        _clearArmed = false;
        _clearButton.Content = "Tümünü temizle";
    }

    private async Task ExportAsync()
    {
        if (_store.Count == 0) return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Kelime listesini dışa aktar",
                SuggestedFileName = "bilinmeyen-kelimeler",
                DefaultExtension = "tsv",
                FileTypeChoices = [new FilePickerFileType("Sekmeyle ayrılmış metin (Anki / Excel)") { Patterns = ["*.tsv"] }]
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(true));
            await writer.WriteAsync(_store.ExportTsv());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogger.Error("Vocabulary list could not be exported", exception);
        }
    }
}
