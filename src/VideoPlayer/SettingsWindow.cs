using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System.Diagnostics;
using VideoPlayer.Core;

namespace VideoPlayer;

internal sealed class SettingsWindow : Window
{
    private readonly Action<PlayerPreferences> _savePreferences;
    private readonly StackPanel _navigation;
    private readonly ContentControl _pageContent;
    private TextBlock? _shortcutStatus;
    private PlayerPreferences _preferences;
    private string? _capturingAction;
    private int? _capturingGestureIndex;
    private bool _addingGesture;

    public SettingsWindow(PlayerPreferences preferences, Action<PlayerPreferences> savePreferences)
    {
        _preferences = preferences;
        _savePreferences = savePreferences;
        Title = UiText.PantheonSettings;
        Width = 850;
        Height = 590;
        MinWidth = 760;
        MinHeight = 520;
        CanResize = true;
        WindowDecorations = WindowDecorations.None;
        ExtendClientAreaToDecorationsHint = true;
        ShowInTaskbar = false;
        Background = Brush.Parse("#101010");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid { RowDefinitions = new RowDefinitions("48,*"), Margin = new Thickness(9) };
        root.Children.Add(CreateTitleBar());

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("196,*"), Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        _navigation = new StackPanel { Spacing = 6, Margin = new Thickness(12) };
        var navBorder = new Border
        {
            Background = Brush.Parse("#181818"),
            BorderBrush = Brush.Parse("#2D2D2D"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = _navigation
        };
        body.Children.Add(navBorder);

        _pageContent = new ContentControl { Margin = new Thickness(25, 19, 12, 12) };
        body.Children.Add(_pageContent);
        Grid.SetColumn(_pageContent, 1);

        _navigation.Children.Add(new TextBlock
        {
            Text = UiText.PREFERENCES,
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            Foreground = Brush.Parse("#858585"),
            Margin = new Thickness(12, 5, 0, 8)
        });
        AddNavigation(UiText.Appearance, "appearance");
        AddNavigation(UiText.Playback, "playback");
        AddNavigation(UiText.Subtitles, "subtitles");
        AddNavigation(UiText.Keyboard, "keyboard");
        AddNavigation(UiText.Mouse, "mouse");
        AddNavigation(UiText.Screenshots, "screenshots");
        AddNavigation(UiText.Diagnostics, "diagnostics");

        Content = root;
        ShowCategory("appearance");
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private Control CreateTitleBar()
    {
        var title = new TextBlock
        {
            Text = UiText.Settings,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#F2F2F2"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(16, 0),
            Cursor = new Cursor(StandardCursorType.SizeAll)
        };
        var dragRegion = new Border
        {
            Background = Brush.Parse("#181818"),
            Child = title,
            Cursor = new Cursor(StandardCursorType.SizeAll)
        };
        dragRegion.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(dragRegion).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
        var close = new Button
        {
            Content = "×",
            Width = 38,
            Height = 34,
            Padding = new Thickness(0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Background = Brush.Parse("#181818"),
            Foreground = Brush.Parse("#F2F2F2"),
            BorderBrush = Brush.Parse("#2D2D2D"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9)
        };
        close.Click += (_, _) => Close();

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        bar.Children.Add(dragRegion);
        bar.Children.Add(close);
        Grid.SetColumn(close, 1);
        return new Border
        {
            Background = Brush.Parse("#181818"),
            BorderBrush = Brush.Parse("#2D2D2D"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = bar
        };
    }

    private void AddNavigation(string label, string category)
    {
        var button = new Button
        {
            Content = label,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Background = Brush.Parse("#181818"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(9),
            Tag = category,
            Foreground = Brush.Parse("#C9C9C9")
        };
        button.Click += (_, _) => ShowCategory(category);
        _navigation.Children.Add(button);
    }

    private void ShowCategory(string category)
    {
        try
        {
            _capturingAction = null;
            _pageContent.Content = null;
            foreach (var button in _navigation.Children.OfType<Button>())
            {
                var selected = Equals(button.Tag, category);
                button.Background = Brush.Parse(selected ? "#3A2020" : "#181818");
                button.Foreground = Brush.Parse(selected ? "#FF7770" : "#C9C9C9");
            }

            var layout = new StackPanel { Spacing = 15 };
            layout.Children.Add(CreateCategoryTitle(category));

            layout.Children.Add(new Border { Height = 1, Background = Brush.Parse("#2D2D2D"), Margin = new Thickness(0, 0, 0, 1) });

            if (category == "appearance") BuildAppearancePage(layout);
            else if (category == "playback") BuildPlaybackPage(layout);
            else if (category == "subtitles") BuildSubtitlesPage(layout);
            else if (category == "keyboard") BuildKeyboardPage(layout);
            else if (category == "mouse") BuildMousePage(layout);
            else if (category == "diagnostics") BuildDiagnosticsPage(layout);
            else BuildScreenshotsPage(layout);

            _pageContent.Content = new ScrollViewer { Content = layout, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        }
        catch (Exception exception)
        {
            AppLogger.Error($"Settings category '{category}' failed to load", exception);
            _pageContent.Content = new TextBlock
            {
                Text = UiText.ThisSettingsCategoryCouldNotBeOpenedError,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush.Parse("#FF7770")
            };
        }
    }

    internal static TextBlock CreateCategoryTitle(string category) => new()
    {
        Text = category switch
        {
            "appearance" => UiText.Appearance,
            "playback" => UiText.Playback,
            "subtitles" => UiText.SubtitleDiscoveryAndSynchronization,
            "keyboard" => UiText.KeyboardShortcuts,
            "mouse" => UiText.MouseGestures,
            "diagnostics" => UiText.DiagnosticLogs,
            _ => UiText.Screenshots
        },
        FontSize = 23,
        FontWeight = FontWeight.SemiBold,
        Foreground = Brush.Parse("#F2F2F2")
    };

    private void BuildAppearancePage(StackPanel layout)
    {
        layout.Children.Add(Description(UiText.CustomizeTheAppearanceOfSubtitlesAndTheInterface));
        AddSliderSetting(layout, UiText.SubtitleFontSize, _preferences.SubtitleFontSize,
            PlayerPreferences.MinimumSubtitleFontSize, PlayerPreferences.MaximumSubtitleFontSize,
            value => UpdatePreferences(_preferences with { SubtitleFontSize = (int)value }, applyAppearance: true));
        AddSliderSetting(layout, UiText.SubtitleBottomMargin, _preferences.SubtitleMargin,
            PlayerPreferences.MinimumSubtitleMargin, PlayerPreferences.MaximumSubtitleMargin,
            value => UpdatePreferences(_preferences with { SubtitleMargin = (int)value }, applyAppearance: true));

        layout.Children.Add(SectionTitle(UiText.PlayerControls));
        var autoHideHeader = new CheckBox
        {
            Content = UiText.ShowTheTopBarWhenThePointerReaches,
            IsChecked = _preferences.AutoHideHeader,
            Foreground = Brush.Parse("#EAEAEA")
        };
        autoHideHeader.Click += (_, _) => UpdatePreferences(
            _preferences with { AutoHideHeader = autoHideHeader.IsChecked == true }, applyAppearance: false);
        layout.Children.Add(autoHideHeader);

        var autoHideBottom = new CheckBox
        {
            Content = UiText.ShowTheBottomBarWhenThePointerReaches,
            IsChecked = _preferences.AutoHideBottom,
            Foreground = Brush.Parse("#EAEAEA")
        };
        autoHideBottom.Click += (_, _) => UpdatePreferences(
            _preferences with { AutoHideBottom = autoHideBottom.IsChecked == true }, applyAppearance: false);
        layout.Children.Add(autoHideBottom);
        layout.Children.Add(Description(UiText.AutoHideControlsHelp));

        layout.Children.Add(SectionTitle(UiText.AccentColor));
        var palette = new ComboBox
        {
            ItemsSource = new[]
            {
                new ComboBoxItem { Content = UiText.Red, Tag = "#E53935" },
                new ComboBoxItem { Content = UiText.Burgundy, Tag = "#C9364F" },
                new ComboBoxItem { Content = UiText.Gray, Tag = "#B0B0B0" }
            },
            SelectedIndex = AccentIndex(_preferences.AccentColor),
            Width = 250,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };
        palette.SelectionChanged += (_, _) =>
        {
            if (palette.SelectedItem is ComboBoxItem { Tag: string color })
                UpdatePreferences(_preferences with { AccentColor = color }, applyAppearance: true);
        };
        layout.Children.Add(palette);

        layout.Children.Add(new TextBlock
        {
            Text = UiText.AddExternalSubtitleHelp,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush.Parse("#A6A6A6"),
            MaxWidth = 500,
            Margin = new Thickness(0, 7, 0, 0)
        });
    }

    private void BuildPlaybackPage(StackPanel layout)
    {
        layout.Children.Add(SectionTitle(UiText.Video));
        layout.Children.Add(Description(UiText.VideoDisplayHelp));
        layout.Children.Add(SectionTitle(UiText.AspectRatio));
        var aspectRatio = CreateVideoDisplaySelector(
            VideoDisplayRules.AspectRatios,
            VideoDisplayRules.NormalizeAspectRatio(_preferences.VideoAspectRatio));
        aspectRatio.SelectionChanged += (_, _) =>
        {
            if (aspectRatio.SelectedItem is ComboBoxItem { Tag: string value })
                UpdatePreferences(_preferences with { VideoAspectRatio = value }, applyAppearance: false);
        };
        AutomationProperties.SetName(aspectRatio, UiText.VideoAspectRatio);
        layout.Children.Add(aspectRatio);

        layout.Children.Add(SectionTitle(UiText.ScaleMode));
        var scaleMode = CreateVideoDisplaySelector(
            VideoDisplayRules.ScaleModes,
            VideoDisplayRules.NormalizeScaleMode(_preferences.VideoScaleMode));
        scaleMode.SelectionChanged += (_, _) =>
        {
            if (scaleMode.SelectedItem is ComboBoxItem { Tag: string value })
                UpdatePreferences(_preferences with { VideoScaleMode = value }, applyAppearance: false);
        };
        AutomationProperties.SetName(scaleMode, UiText.VideoScaleMode);
        layout.Children.Add(scaleMode);

        layout.Children.Add(Description(UiText.IfSubtitleTextLooksIncorrectChooseTheFile));
        var subtitleCodepage = new ComboBox
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            MinWidth = 260
        };
        foreach (var option in SubtitleCodepageRules.Options)
        {
            var item = new ComboBoxItem { Content = option.Label, Tag = option.Codepage };
            subtitleCodepage.Items.Add(item);
            if (string.Equals(option.Codepage, _preferences.SubtitleCodepage, StringComparison.OrdinalIgnoreCase))
                subtitleCodepage.SelectedItem = item;
        }
        subtitleCodepage.SelectionChanged += (_, _) =>
        {
            if (subtitleCodepage.SelectedItem is ComboBoxItem { Tag: string codepage })
                UpdatePreferences(_preferences with { SubtitleCodepage = codepage }, applyAppearance: false);
        };
        AutomationProperties.SetName(subtitleCodepage, UiText.SubtitleCharacterEncoding);
        layout.Children.Add(subtitleCodepage);
        layout.Children.Add(new TextBlock
        {
            Text = UiText.SubtitleEncodingHelp,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush.Parse("#A6A6A6"),
            MaxWidth = 500,
            Margin = new Thickness(0, 5, 0, 10)
        });
        layout.Children.Add(Description(UiText.PlaybackSpeedStepHelp));
        AddSliderSetting(layout, UiText.PlaybackSpeedStep, _preferences.PlaybackSpeedStep, 0.05, 1.00,
            value => UpdatePreferences(_preferences with { PlaybackSpeedStep = Math.Round(value, 2) }, applyAppearance: false),
            value => $"{value:0.00}×");
        AddSliderSetting(layout, UiText.SubtitleTimingStep, _preferences.SubtitleDelayStep, 0.05, 1.00,
            value => UpdatePreferences(_preferences with { SubtitleDelayStep = Math.Round(value, 2) }, applyAppearance: false),
            value => UiText.Format(UiText.SecondsValueFormat, value));
        layout.Children.Add(new TextBlock
        {
            Text = UiText.PlaybackSpeedRangesFrom025To10,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush.Parse("#A6A6A6"),
            MaxWidth = 500
        });
    }

    private void BuildSubtitlesPage(StackPanel layout)
    {
        layout.Children.Add(Description(UiText.SubtitleAutomationHelp));
        var find = new CheckBox { Content = UiText.AutomaticallyFindAndDownloadOnlineSubtitlesWhenA, IsChecked = _preferences.AutoFindSubtitles };
        find.Click += (_, _) => UpdatePreferences(_preferences with { AutoFindSubtitles = find.IsChecked == true }, false);
        layout.Children.Add(find);
        var sync = new CheckBox { Content = UiText.AutomaticallySynchronizeExternalSubtitles, IsChecked = _preferences.AutoSynchronizeSubtitles };
        sync.Click += (_, _) => UpdatePreferences(_preferences with { AutoSynchronizeSubtitles = sync.IsChecked == true }, false);
        layout.Children.Add(sync);
        layout.Children.Add(Description(UiText.OnlineSubtitleSearchHelp));
        layout.Children.Add(SectionTitle(UiText.SearchLanguage));
        var language = CreateVideoDisplaySelector(SubtitleAutomationOptions.Languages, _preferences.SubtitleSearchLanguage);
        language.SelectionChanged += (_, _) =>
        {
            if (language.SelectedItem is ComboBoxItem { Tag: string code })
                UpdatePreferences(_preferences with { SubtitleSearchLanguage = code }, false);
        };
        AutomationProperties.SetName(language, UiText.SubtitleSearchLanguage);
        layout.Children.Add(language);
        layout.Children.Add(SectionTitle(UiText.OpenSubtitlesAPIKey));
        var key = new TextBox { Text = _preferences.OpenSubtitlesApiKey, PasswordChar = '●', PlaceholderText = UiText.APIKey, MaxWidth = 500 };
        key.TextChanged += (_, _) => UpdatePreferences(_preferences with { OpenSubtitlesApiKey = key.Text?.Trim() ?? "" }, false);
        AutomationProperties.SetName(key, UiText.OpenSubtitlesAPIKey);
        layout.Children.Add(key);
        layout.Children.Add(Description(UiText.OpenSubtitlesApiKeyHelp));
        layout.Children.Add(SectionTitle(UiText.SynchronizationTool));
        var executable = new TextBox { Text = _preferences.SubtitleSynchronizerPath, PlaceholderText = UiText.PathToFfsubsyncOrFfsubsyncExe, MaxWidth = 500 };
        executable.TextChanged += (_, _) => UpdatePreferences(_preferences with
        {
            SubtitleSynchronizerPath = string.IsNullOrWhiteSpace(executable.Text)
                ? SubtitleAutomationOptions.DefaultSynchronizer : executable.Text.Trim()
        }, false);
        AutomationProperties.SetName(executable, UiText.FFsubsyncExecutablePath);
        layout.Children.Add(executable);
        layout.Children.Add(Description(UiText.SubtitleToolsSetupHelp));
    }

    private static ComboBox CreateVideoDisplaySelector(
        IReadOnlyList<(string Label, string Value)> options,
        string selectedValue)
    {
        var selector = new ComboBox
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            MinWidth = 260,
            Margin = new Thickness(0, 5, 0, 10)
        };
        foreach (var option in options)
        {
            var item = new ComboBoxItem { Content = option.Label, Tag = option.Value };
            selector.Items.Add(item);
            if (option.Value == selectedValue) selector.SelectedItem = item;
        }
        return selector;
    }

    private void BuildKeyboardPage(StackPanel layout)
    {
        layout.Children.Add(Description(UiText.ShortcutEditingHelp));
        _shortcutStatus = new TextBlock { Foreground = Brush.Parse("#FF7770"), TextWrapping = TextWrapping.Wrap };
        layout.Children.Add(_shortcutStatus);

        var groups = new (string Title, (string Action, string Label)[] Items)[]
        {
            (UiText.Playback, [
                (ShortcutDefaults.TogglePlayback, UiText.PlayPause),
                (ShortcutDefaults.SeekBackward, UiText.SeekBackward5Seconds),
                (ShortcutDefaults.SeekForward, UiText.SeekForward5Seconds),
                (ShortcutDefaults.SpeedDown, UiText.DecreasePlaybackSpeed),
                (ShortcutDefaults.SpeedUp, UiText.IncreasePlaybackSpeed),
                (ShortcutDefaults.SpeedReset, UiText.ResetPlaybackSpeed),
                (ShortcutDefaults.PreviousFrame, UiText.PreviousFrame),
                (ShortcutDefaults.NextFrame, UiText.NextFrame),
                (ShortcutDefaults.PreviousChapter, UiText.PreviousChapter),
                (ShortcutDefaults.NextChapter, UiText.NextChapter)]),
            (UiText.Interface, [(ShortcutDefaults.ToggleSettings, UiText.OpenCloseSettings)]),
            (UiText.VideoAndSubtitles, [
                (ShortcutDefaults.OpenVideo, UiText.OpenVideo),
            (ShortcutDefaults.AddSubtitle, UiText.AddExternalSubtitle),
            (ShortcutDefaults.ToggleSubtitles, UiText.ShowHideSubtitles),
            (ShortcutDefaults.SubtitleDelayEarlier, UiText.MoveSubtitlesEarlier),
            (ShortcutDefaults.SubtitleDelayLater, UiText.MoveSubtitlesLater),
                (ShortcutDefaults.SubtitleDelayReset, UiText.ResetSubtitleTiming),
                (ShortcutDefaults.CaptureFrame, UiText.CaptureAFrameWithoutSubtitles),
                (ShortcutDefaults.ToggleFullscreen, UiText.ToggleFullscreen)]),
            (UiText.Audio, [
                (ShortcutDefaults.VolumeDown, UiText.DecreaseVolume),
                (ShortcutDefaults.VolumeUp, UiText.IncreaseVolume),
                (ShortcutDefaults.ToggleMute, UiText.MuteUnmute)])
        };

        foreach (var group in groups)
        {
            layout.Children.Add(SectionTitle(group.Title));
            foreach (var (action, label) in group.Items)
                layout.Children.Add(CreateShortcutRow(action, label, _shortcutStatus));
        }
    }

    private void BuildMousePage(StackPanel layout)
    {
        layout.Children.Add(Description(UiText.MouseRegionsHelp));

        var orientation = new ComboBox
        {
            ItemsSource = new[] { UiText.VerticalSplitLeftRight, UiText.HorizontalSplitTopBottom },
            SelectedIndex = _preferences.MouseRegionOrientation == "Horizontal" ? 1 : 0,
            Width = 250,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };
        var regionCount = new ComboBox
        {
            ItemsSource = new[] { UiText.Label2Regions, UiText.Label3Regions },
            SelectedIndex = _preferences.MouseRegionCount == 3 ? 1 : 0,
            Width = 150,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };

        layout.Children.Add(SectionTitle(UiText.RegionLayout));
        var layoutSettings = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
        layoutSettings.Children.Add(orientation);
        layoutSettings.Children.Add(regionCount);
        layout.Children.Add(layoutSettings);
        layout.Children.Add(CreateMouseRegionPreview(_preferences.MouseRegionOrientation, _preferences.MouseRegionCount));

        orientation.SelectionChanged += (_, _) =>
        {
            var value = orientation.SelectedIndex == 1 ? "Horizontal" : "Vertical";
            if (value == _preferences.MouseRegionOrientation) return;
            UpdatePreferences(_preferences with { MouseRegionOrientation = value }, applyAppearance: false);
            ShowCategory("mouse");
        };
        regionCount.SelectionChanged += (_, _) =>
        {
            var value = regionCount.SelectedIndex == 1 ? 3 : 2;
            if (value == _preferences.MouseRegionCount) return;
            UpdatePreferences(_preferences with { MouseRegionCount = value }, applyAppearance: false);
            ShowCategory("mouse");
        };

        var gestureOptions = new (MouseGesture Gesture, string Label)[]
        {
            (MouseGesture.LeftClick, UiText.LeftClick),
            (MouseGesture.RightClick, UiText.RightClick),
            (MouseGesture.MiddleClick, UiText.MiddleClick),
            (MouseGesture.WheelUp, UiText.ScrollUp),
            (MouseGesture.WheelDown, UiText.ScrollDown),
            (MouseGesture.AltWheelUp, UiText.AltScrollUp),
            (MouseGesture.AltWheelDown, UiText.AltScrollDown)
        };
        var actionOptions = GetMouseActionOptions();
        layout.Children.Add(SectionTitle(UiText.GlobalMouseShortcuts));
        layout.Children.Add(Description(UiText.ScrollingOverTheVideoSeeks5SecondsCtrl));
        AddMouseBindingSetting(layout, UiText.LeftClickVideo, MouseBindingDefaults.GlobalKey(nameof(MouseGesture.LeftClick)), actionOptions);
        AddMouseBindingSetting(layout, UiText.RightClickVideo, MouseBindingDefaults.GlobalKey(nameof(MouseGesture.RightClick)), actionOptions);
        AddMouseBindingSetting(layout, UiText.MiddleClickVideo, MouseBindingDefaults.GlobalKey(nameof(MouseGesture.MiddleClick)), actionOptions);
        AddMouseBindingSetting(layout, UiText.ScrollUpOverVideo, MouseBindingDefaults.GlobalKey(nameof(MouseGesture.WheelUp)), actionOptions);
        AddMouseBindingSetting(layout, UiText.ScrollDownOverVideo, MouseBindingDefaults.GlobalKey(nameof(MouseGesture.WheelDown)), actionOptions);
        AddMouseBindingSetting(layout, UiText.CtrlScrollUpNextFrame, MouseBindingDefaults.GlobalKey(nameof(MouseGesture.CtrlWheelUp)), actionOptions);
        AddMouseBindingSetting(layout, UiText.CtrlScrollDownPreviousFrame, MouseBindingDefaults.GlobalKey(nameof(MouseGesture.CtrlWheelDown)), actionOptions);
        AddMouseBindingSetting(layout, UiText.ShiftScrollUpSeekForward30Seconds, MouseBindingDefaults.GlobalKey(nameof(MouseGesture.ShiftWheelUp)), actionOptions);
        AddMouseBindingSetting(layout, UiText.ShiftScrollDownSeekBackward30Seconds, MouseBindingDefaults.GlobalKey(nameof(MouseGesture.ShiftWheelDown)), actionOptions);
        AddMouseBindingSetting(layout, UiText.AltScrollUpVideo, MouseBindingDefaults.GlobalKey(nameof(MouseGesture.AltWheelUp)), actionOptions);
        AddMouseBindingSetting(layout, UiText.AltScrollDownVideo, MouseBindingDefaults.GlobalKey(nameof(MouseGesture.AltWheelDown)), actionOptions);
        AddMouseBindingSetting(layout, UiText.ScrollUpInTheCenterRegion, MouseBindingDefaults.CenterKey(nameof(MouseGesture.WheelUp)), actionOptions);
        AddMouseBindingSetting(layout, UiText.ScrollDownInTheCenterRegion, MouseBindingDefaults.CenterKey(nameof(MouseGesture.WheelDown)), actionOptions);
        layout.Children.Add(SectionTitle(UiText.AdditionalGesturesByRegion));
        for (var zone = 0; zone < _preferences.MouseRegionCount; zone++)
        {
            layout.Children.Add(SectionTitle(GetZoneTitle(zone, _preferences.MouseRegionCount, _preferences.MouseRegionOrientation)));
            foreach (var (gesture, label) in gestureOptions)
            {
                var bindingKey = $"{zone}:{gesture}";
                _preferences.MouseBindings.TryGetValue(bindingKey, out var selectedAction);
                var choices = actionOptions.Select(option => new ComboBoxItem { Content = option.Label, Tag = option.Action }).ToArray();
                var combo = new ComboBox
                {
                    ItemsSource = choices,
                    SelectedItem = choices.FirstOrDefault(item => Equals(item.Tag, selectedAction)) ?? choices[0],
                    MinWidth = 210
                };
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is not ComboBoxItem { Tag: string action }) return;
                    var bindings = new Dictionary<string, string>(_preferences.MouseBindings, StringComparer.Ordinal);
                    if (action.Length == 0) bindings[bindingKey] = string.Empty;
                    else bindings[bindingKey] = action;
                    UpdatePreferences(_preferences with { MouseBindings = bindings }, applyAppearance: false);
                };

                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                row.Children.Add(new TextBlock
                {
                    Text = label,
                    Foreground = Brush.Parse("#EAEAEA"),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                });
                row.Children.Add(combo);
                Grid.SetColumn(combo, 1);
                layout.Children.Add(new Border
                {
                    Padding = new Thickness(12, 8),
                    Background = Brush.Parse("#181818"),
                    BorderBrush = Brush.Parse("#2D2D2D"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Child = row
                });
            }
        }

        layout.Children.Add(new TextBlock
        {
            Text = UiText.MouseGestureScopeHelp,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush.Parse("#A6A6A6"),
            MaxWidth = 540,
            Margin = new Thickness(0, 5, 0, 0)
        });
    }

    private void BuildScreenshotsPage(StackPanel layout)
    {
        layout.Children.Add(Description(UiText.ChooseWhereSubtitleFreeFramesAreSavedEach));
        var captureDirectory = CurrentScreenshotDirectory();
        var pathLabel = new TextBlock
        {
            Text = captureDirectory,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush.Parse("#EAEAEA"),
            MaxWidth = 540
        };
        layout.Children.Add(SectionTitle(UiText.SaveFolder));
        layout.Children.Add(new Border
        {
            Padding = new Thickness(13),
            Background = Brush.Parse("#181818"),
            BorderBrush = Brush.Parse("#2D2D2D"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = pathLabel
        });

        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 9 };
        var choose = CreateButton(UiText.ChooseFolder);
        choose.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = UiText.ChooseScreenshotFolder,
                AllowMultiple = false
            });
            if (folders.Count == 0) return;
            var selectedPath = folders[0].Path.LocalPath;
            UpdatePreferences(_preferences with { ScreenshotDirectory = selectedPath }, applyAppearance: false);
            pathLabel.Text = selectedPath;
        };
        var reset = CreateButton(UiText.ResetToDefault);
        reset.Click += (_, _) =>
        {
            UpdatePreferences(_preferences with { ScreenshotDirectory = null }, applyAppearance: false);
            pathLabel.Text = CurrentScreenshotDirectory();
        };
        actions.Children.Add(choose);
        actions.Children.Add(reset);
        layout.Children.Add(actions);
        layout.Children.Add(new TextBlock
        {
            Text = UiText.DefaultScreenshotFolder,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush.Parse("#A6A6A6"),
            MaxWidth = 540,
            Margin = new Thickness(0, 6, 0, 0)
        });
    }

    private void BuildDiagnosticsPage(StackPanel layout)
    {
        layout.Children.Add(Description(UiText.PantheonLogsErrorsAndUnexpectedApplicationEventsA));
        layout.Children.Add(SectionTitle(UiText.LogFolder));
        layout.Children.Add(new Border
        {
            Padding = new Thickness(13),
            Background = Brush.Parse("#181818"),
            BorderBrush = Brush.Parse("#2D2D2D"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = new TextBlock
            {
                Text = AppLogger.LogDirectory,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush.Parse("#EAEAEA"),
                MaxWidth = 540
            }
        });

        var openFolder = CreateButton(UiText.OpenLogFolder);
        openFolder.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        openFolder.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(AppLogger.LogDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = AppLogger.LogDirectory,
                    UseShellExecute = true
                });
            }
            catch (Exception exception)
            {
                AppLogger.Error("Could not open the diagnostics log folder", exception);
            }
        };
        layout.Children.Add(openFolder);
    }

    private static Border CreateMouseRegionPreview(string orientation, int count)
    {
        var preview = new Grid { Height = 82, ColumnSpacing = 7, RowSpacing = 7 };
        if (orientation == "Horizontal")
        {
            preview.RowDefinitions = new RowDefinitions(string.Join(',', Enumerable.Repeat("*", count)));
            preview.ColumnDefinitions = new ColumnDefinitions("*");
        }
        else
        {
            preview.ColumnDefinitions = new ColumnDefinitions(string.Join(',', Enumerable.Repeat("*", count)));
            preview.RowDefinitions = new RowDefinitions("*");
        }

        for (var index = 0; index < count; index++)
        {
            var zone = new Border
            {
                Background = Brush.Parse(index % 2 == 0 ? "#222222" : "#1B1B1B"),
                BorderBrush = Brush.Parse("#383838"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = new TextBlock
                {
                    Text = GetZoneTitle(index, count, orientation),
                    Foreground = Brush.Parse("#D9D9D9"),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                }
            };
            preview.Children.Add(zone);
            if (orientation == "Horizontal") Grid.SetRow(zone, index);
            else Grid.SetColumn(zone, index);
        }

        return new Border
        {
            Background = Brush.Parse("#090909"),
            BorderBrush = Brush.Parse("#2D2D2D"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8),
            Child = preview
        };
    }

    private static string GetZoneTitle(int zone, int count, string orientation) => orientation == "Horizontal"
        ? zone == 0 ? UiText.TopRegion : zone == count - 1 ? UiText.BottomRegion : UiText.CenterRegion
        : zone == 0 ? UiText.LeftRegion : zone == count - 1 ? UiText.RightRegion : UiText.CenterRegion;

    private string CurrentScreenshotDirectory() => string.IsNullOrWhiteSpace(_preferences.ScreenshotDirectory)
        ? AppStoragePaths.ScreenshotDirectory
        : _preferences.ScreenshotDirectory;

    private static (string Action, string Label)[] GetMouseActionOptions() =>
    [
        ("", UiText.Unassigned),
        (ShortcutDefaults.TogglePlayback, UiText.PlayPause),
        (ShortcutDefaults.SeekBackward, UiText.SeekBackward5Seconds),
        (ShortcutDefaults.SeekForward, UiText.SeekForward5Seconds),
        (ShortcutDefaults.SeekLongBackward, UiText.SeekBackward30Seconds),
        (ShortcutDefaults.SeekLongForward, UiText.SeekForward30Seconds),
        (ShortcutDefaults.SpeedDown, UiText.DecreaseSpeed),
        (ShortcutDefaults.SpeedUp, UiText.IncreaseSpeed),
        (ShortcutDefaults.SpeedReset, UiText.ResetSpeed),
        (ShortcutDefaults.ToggleSubtitles, UiText.ShowHideSubtitles),
        (ShortcutDefaults.SubtitleDelayEarlier, UiText.MoveSubtitlesEarlier),
        (ShortcutDefaults.SubtitleDelayLater, UiText.MoveSubtitlesLater),
        (ShortcutDefaults.SubtitleDelayReset, UiText.ResetSubtitleTiming),
        (ShortcutDefaults.ToggleFullscreen, UiText.ToggleFullscreen),
        (ShortcutDefaults.CaptureFrame, UiText.CaptureAFrameWithoutSubtitles),
        (ShortcutDefaults.OpenVideo, UiText.OpenVideo),
        (ShortcutDefaults.AddSubtitle, UiText.AddExternalSubtitle),
        (ShortcutDefaults.VolumeDown, UiText.DecreaseVolume),
        (ShortcutDefaults.VolumeUp, UiText.IncreaseVolume),
        (ShortcutDefaults.ToggleMute, UiText.MuteUnmute)
    ];

    private void AddMouseBindingSetting(StackPanel layout, string label, string bindingKey,
        (string Action, string Label)[] actionOptions)
    {
        _preferences.MouseBindings.TryGetValue(bindingKey, out var selectedAction);
        var choices = actionOptions.Select(option => new ComboBoxItem { Content = option.Label, Tag = option.Action }).ToArray();
        var combo = new ComboBox
        {
            ItemsSource = choices,
            SelectedItem = choices.FirstOrDefault(item => Equals(item.Tag, selectedAction)) ?? choices[0],
            MinWidth = 210
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not ComboBoxItem { Tag: string action }) return;
            var bindings = new Dictionary<string, string>(_preferences.MouseBindings, StringComparer.Ordinal);
            if (action.Length == 0) bindings[bindingKey] = string.Empty;
            else bindings[bindingKey] = action;
            UpdatePreferences(_preferences with { MouseBindings = bindings }, applyAppearance: false);
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 2) };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Brush.Parse("#EAEAEA"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });
        row.Children.Add(combo);
        Grid.SetColumn(combo, 1);
        layout.Children.Add(new Border
        {
            Padding = new Thickness(12, 8),
            Background = Brush.Parse("#181818"),
            BorderBrush = Brush.Parse("#2D2D2D"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = row
        });
    }

    private Control CreateShortcutRow(string action, string label, TextBlock? status)
    {
        var bindings = ShortcutBindingRules.Parse(_preferences.Shortcuts[action]);
        var bindingButtons = new WrapPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        for (var index = 0; index < bindings.Count; index++)
        {
            var bindingIndex = index;
            var gestureButton = CreateButton(FormatShortcutDisplay(bindings[index]));
            gestureButton.MinWidth = 72;
            gestureButton.Margin = new Thickness(0, 0, 5, 0);
            gestureButton.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            gestureButton.Click += (_, _) => BeginShortcutCapture(action, bindingIndex, false, gestureButton, status);
            bindingButtons.Children.Add(gestureButton);
            if (bindings.Count <= 1) continue;
            var remove = CreateButton("×");
            remove.Width = 34;
            remove.Margin = new Thickness(0, 0, 5, 0);
            ToolTip.SetTip(remove, UiText.RemoveThisShortcut);
            remove.Click += (_, _) => RemoveShortcut(action, bindingIndex);
            bindingButtons.Children.Add(remove);
        }
        var add = CreateButton("+");
        add.Width = 38;
        ToolTip.SetTip(add, UiText.AddAnotherShortcutToThisAction);
        add.Click += (_, _) => BeginShortcutCapture(action, bindings.Count, true, add, status);
        bindingButtons.Children.Add(add);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 2) };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Brush.Parse("#EAEAEA"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });
        row.Children.Add(bindingButtons);
        Grid.SetColumn(bindingButtons, 1);
        return new Border
        {
            Padding = new Thickness(12, 9),
            Background = Brush.Parse("#181818"),
            BorderBrush = Brush.Parse("#2D2D2D"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = row
        };
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_capturingAction is null)
        {
            if (ShortcutGesture.TryFromAvalonia(e.Key, e.KeyModifiers, out var toggleGesture)
                && ShortcutBindingRules.Parse(_preferences.Shortcuts[ShortcutDefaults.ToggleSettings])
                    .Contains(toggleGesture.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                e.Handled = true;
                Close();
            }
            return;
        }
        if (!ShortcutGesture.TryFromAvalonia(e.Key, e.KeyModifiers, out var gesture)) return;

        e.Handled = true;
        var action = _capturingAction;
        _capturingAction = null;
        var gestureString = gesture.ToString();
        if (ShortcutBindingRules.IsAssignedToAnotherAction(_preferences.Shortcuts, action, gestureString))
        {
            ShowCategory("keyboard");
            if (_shortcutStatus is not null)
                _shortcutStatus.Text = UiText.Format(UiText.ShortcutAssignedElsewhereFormat, gesture);
            return;
        }

        var actionBindings = ShortcutBindingRules.Parse(_preferences.Shortcuts[action]).ToList();
        if (actionBindings.Select((existing, index) => (existing, index)).Any(binding =>
            binding.existing.Equals(gestureString, StringComparison.OrdinalIgnoreCase)
            && (_addingGesture || binding.index != _capturingGestureIndex)))
        {
            ShowCategory("keyboard");
            if (_shortcutStatus is not null) _shortcutStatus.Text = UiText.Format(UiText.ShortcutAlreadyAssignedFormat, gesture);
            return;
        }
        if (_addingGesture) actionBindings.Add(gestureString);
        else if (_capturingGestureIndex is { } replaceIndex && replaceIndex < actionBindings.Count)
            actionBindings[replaceIndex] = gestureString;
        var shortcuts = new Dictionary<string, string>(_preferences.Shortcuts, StringComparer.Ordinal)
        {
            [action] = ShortcutBindingRules.Format(actionBindings)
        };
        _capturingGestureIndex = null;
        _addingGesture = false;
        UpdatePreferences(_preferences with { Shortcuts = shortcuts }, applyAppearance: false);
        ShowCategory("keyboard");
    }

    private void BeginShortcutCapture(string action, int index, bool add, Button button, TextBlock? status)
    {
        _capturingAction = action;
        _capturingGestureIndex = index;
        _addingGesture = add;
        if (status is not null)
            status.Text = UiText.ShortcutCaptureHelp;
        button.Content = UiText.WaitingForAKey;
        button.Focus();
    }

    private void RemoveShortcut(string action, int index)
    {
        var bindings = ShortcutBindingRules.Parse(_preferences.Shortcuts[action]).ToList();
        if (index < 0 || index >= bindings.Count || bindings.Count <= 1) return;
        bindings.RemoveAt(index);
        var shortcuts = new Dictionary<string, string>(_preferences.Shortcuts, StringComparer.Ordinal)
        {
            [action] = ShortcutBindingRules.Format(bindings)
        };
        UpdatePreferences(_preferences with { Shortcuts = shortcuts }, applyAppearance: false);
        ShowCategory("keyboard");
    }

    private static string FormatShortcutDisplay(string value) =>
        ShortcutGesture.TryParse(value, out var gesture) ? gesture.ToString() : value;

    private void AddSliderSetting(StackPanel parent, string title, double value, double minimum, double maximum,
        Action<double> onValueChanged, Func<double, string>? format = null)
    {
        var valueLabel = new TextBlock
        {
            Text = (format ?? (number => number.ToString("0", System.Globalization.CultureInfo.InvariantCulture)))(value),
            Foreground = Brush.Parse("#A6A6A6"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            TickFrequency = minimum < 1 ? 0.05 : 1,
            IsSnapToTickEnabled = true,
            MinWidth = 250,
            MaxWidth = 460
        };
        slider.ValueChanged += (_, e) =>
        {
            valueLabel.Text = (format ?? (number => number.ToString("0", System.Globalization.CultureInfo.InvariantCulture)))(e.NewValue);
            onValueChanged(e.NewValue);
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(SectionTitle(title));
        header.Children.Add(valueLabel);
        Grid.SetColumn(valueLabel, 1);
        parent.Children.Add(header);
        parent.Children.Add(slider);
    }

    private void UpdatePreferences(PlayerPreferences preferences, bool applyAppearance)
    {
        _preferences = preferences;
        if (applyAppearance && Application.Current is { } app)
            app.Resources["AccentBrush"] = new SolidColorBrush(Color.Parse(_preferences.AccentColor));
        _savePreferences(_preferences);
    }

    private static TextBlock Description(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush.Parse("#A6A6A6"),
        MaxWidth = 540
    };

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeight.SemiBold,
        Foreground = Brush.Parse("#F0F0F0"),
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
    };

    private static Button CreateButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(12, 9),
        Background = Brush.Parse("#272727"),
        Foreground = Brush.Parse("#F2F2F2"),
        BorderBrush = Brush.Parse("#383838"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9)
    };

    private static int AccentIndex(string color) => color.ToUpperInvariant() switch
    {
        "#C9364F" => 1,
        "#B0B0B0" => 2,
        _ => 0
    };
}
