using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class PlayerPreferencesStoreTests
{
    [Fact]
    public void NewPreferences_EnableAutomaticHardwareDecodingByDefault() =>
        Assert.True(new PlayerPreferences().HardwareAcceleration);

    [Fact]
    public void Load_ReturnsDefaultsWhenSettingsDoNotExist()
    {
        var store = new PlayerPreferencesStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json"));

        AssertDefaults(store.Load());
    }

    [Fact]
    public void SaveAndLoad_PreserveAllPreferences()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var store = new PlayerPreferencesStore(path);
        var preferences = new PlayerPreferences
        {
            SubtitleFontSize = 50,
            SubtitleMargin = 48,
            Volume = 76,
            Muted = true,
            PlaybackSpeed = 2.35,
            AudioTrackDisabled = true,
            PreferredAudioLanguage = "eng",
            PreferredAudioTrackIndex = 2,
            PreferredSubtitleLanguage = "tur",
            PreferredSubtitleTrackIndex = 3,
            SubtitleCodepage = "cp1254",
            AutoFindSubtitles = false,
            AutoSynchronizeSubtitles = false,
            SubtitleSearchLanguage = "en",
            OpenSubtitlesApiKey = "test-key",
            SubtitleSynchronizerPath = "C:\\Subtitle Tools\\ffsubsync.exe",
            VideoAspectRatio = "21:9",
            VideoScaleMode = VideoDisplayRules.FillScaleMode,
            SubtitlesVisible = false,
            AutoHideHeader = false,
            AutoHideBottom = true,
            AccentColor = "#C9364F",
            SubtitleDelayStep = 0.25,
            ScreenshotDirectory = "C:\\Captures",
            ScreenshotSaveMode = ScreenshotSaveModeRules.Optimized,
            MouseRegionOrientation = "Horizontal",
            MouseRegionCount = 3,
            MouseBindings = new Dictionary<string, string> { ["2:WheelUp"] = ShortcutDefaults.SpeedUp }
        };

        store.Save(preferences);

        var loaded = store.Load();
        Assert.Equal(preferences.SubtitleFontSize, loaded.SubtitleFontSize);
        Assert.Equal(preferences.SubtitleMargin, loaded.SubtitleMargin);
        Assert.Equal(preferences.Volume, loaded.Volume);
        Assert.Equal(preferences.Muted, loaded.Muted);
        Assert.Equal(preferences.PlaybackSpeed, loaded.PlaybackSpeed);
        Assert.Equal(preferences.AudioTrackDisabled, loaded.AudioTrackDisabled);
        Assert.Equal(preferences.PreferredAudioLanguage, loaded.PreferredAudioLanguage);
        Assert.Equal(preferences.PreferredAudioTrackIndex, loaded.PreferredAudioTrackIndex);
        Assert.Equal(preferences.PreferredSubtitleLanguage, loaded.PreferredSubtitleLanguage);
        Assert.Equal(preferences.PreferredSubtitleTrackIndex, loaded.PreferredSubtitleTrackIndex);
        Assert.Equal(preferences.SubtitleCodepage, loaded.SubtitleCodepage);
        Assert.Equal(preferences.AutoFindSubtitles, loaded.AutoFindSubtitles);
        Assert.Equal(preferences.AutoSynchronizeSubtitles, loaded.AutoSynchronizeSubtitles);
        Assert.Equal(preferences.SubtitleSearchLanguage, loaded.SubtitleSearchLanguage);
        Assert.Equal(preferences.OpenSubtitlesApiKey, loaded.OpenSubtitlesApiKey);
        Assert.Equal(preferences.SubtitleSynchronizerPath, loaded.SubtitleSynchronizerPath);
        Assert.Equal(preferences.VideoAspectRatio, loaded.VideoAspectRatio);
        Assert.Equal(preferences.VideoScaleMode, loaded.VideoScaleMode);
        Assert.Equal(preferences.SubtitlesVisible, loaded.SubtitlesVisible);
        Assert.Equal(preferences.AutoHideHeader, loaded.AutoHideHeader);
        Assert.Equal(preferences.AutoHideBottom, loaded.AutoHideBottom);
        Assert.Equal(preferences.AccentColor, loaded.AccentColor);
        Assert.Equal(preferences.SubtitleDelayStep, loaded.SubtitleDelayStep);
        Assert.Equal(preferences.PlaybackSpeedStep, loaded.PlaybackSpeedStep);
        Assert.Equal(preferences.Shortcuts.OrderBy(pair => pair.Key), loaded.Shortcuts.OrderBy(pair => pair.Key));
        Assert.Equal(preferences.ScreenshotDirectory, loaded.ScreenshotDirectory);
        Assert.Equal(preferences.ScreenshotSaveMode, loaded.ScreenshotSaveMode);
        Assert.Equal(preferences.MouseRegionOrientation, loaded.MouseRegionOrientation);
        Assert.Equal(preferences.MouseRegionCount, loaded.MouseRegionCount);
        Assert.Equal(ShortcutDefaults.SpeedUp, loaded.MouseBindings["2:WheelUp"]);
        Assert.Equal(ShortcutDefaults.TogglePlayback, loaded.MouseBindings[MouseBindingDefaults.GlobalKey("LeftClick")]);
        Assert.False(File.Exists(path + ".tmp"));
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void Save_ClampsPlaybackSpeedAndTrackIndexes()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var store = new PlayerPreferencesStore(path);
        store.Save(new PlayerPreferences
        {
            PlaybackSpeed = 50,
            PreferredAudioTrackIndex = 500,
            PreferredSubtitleTrackIndex = -2
        });

        var loaded = store.Load();

        Assert.Equal(PlayerPreferences.MaximumPlaybackSpeed, loaded.PlaybackSpeed);
        Assert.Equal(256, loaded.PreferredAudioTrackIndex);
        Assert.Equal(0, loaded.PreferredSubtitleTrackIndex);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void Save_NormalizesInvalidVideoDisplayPreferences()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var store = new PlayerPreferencesStore(path);
        store.Save(new PlayerPreferences { VideoAspectRatio = "freeform", VideoScaleMode = "zoom" });

        var loaded = store.Load();

        Assert.Equal(VideoDisplayRules.AutomaticAspectRatio, loaded.VideoAspectRatio);
        Assert.Equal(VideoDisplayRules.FitScaleMode, loaded.VideoScaleMode);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Theory]
    [InlineData(null, "auto")]
    [InlineData("CP1254", "cp1254")]
    [InlineData("unsupported", "auto")]
    public void Save_NormalizesSubtitleCodepage(string? codepage, string expected)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var store = new PlayerPreferencesStore(path);
        store.Save(new PlayerPreferences { SubtitleCodepage = codepage! });

        Assert.Equal(expected, store.Load().SubtitleCodepage);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Theory]
    [InlineData(-100, 999, 18, 100)]
    [InlineData(999, -100, 64, 0)]
    public void Load_ClampsSubtitleAppearanceToSettingsSliderRanges(
        int subtitleFontSize,
        int subtitleMargin,
        int expectedFontSize,
        int expectedMargin)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            $"{{\"SubtitleFontSize\":{subtitleFontSize},\"SubtitleMargin\":{subtitleMargin}}}");

        var loaded = new PlayerPreferencesStore(path).Load();

        Assert.Equal(expectedFontSize, loaded.SubtitleFontSize);
        Assert.Equal(expectedMargin, loaded.SubtitleMargin);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void Save_PreservesAnExplicitHardwareDecodingOptOut()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var store = new PlayerPreferencesStore(path);

        store.Save(new PlayerPreferences { HardwareAcceleration = false });

        Assert.False(store.Load().HardwareAcceleration);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void SaveAndLoad_PreserveAnUnboundDefaultMouseGesture()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var store = new PlayerPreferencesStore(path);
        var bindingKey = MouseBindingDefaults.GlobalKey("LeftClick");
        var bindings = MouseBindingDefaults.Create();
        bindings[bindingKey] = string.Empty;

        store.Save(new PlayerPreferences { MouseBindings = bindings });

        var loaded = store.Load();
        Assert.Equal(string.Empty, loaded.MouseBindings[bindingKey]);
        Assert.Null(MouseBindingRules.FindAction(
            "LeftClick", 0.5, 0.5, insideVideo: true,
            orientation: "Vertical", regionCount: 2, bindings: loaded.MouseBindings));
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void Load_FillsNewShortcutDefaultsForExistingSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"Volume\":73,\"Shortcuts\":{\"TogglePlayback\":\"P\"}}");
        var store = new PlayerPreferencesStore(path);

        var loaded = store.Load();

        Assert.Equal(73, loaded.Volume);
        Assert.Equal("P", loaded.Shortcuts[ShortcutDefaults.TogglePlayback]);
        Assert.Equal("Left", loaded.Shortcuts[ShortcutDefaults.SeekBackward]);
        Assert.Equal("Tab", loaded.Shortcuts[ShortcutDefaults.ToggleSettings]);
        Assert.Equal(ShortcutDefaults.TogglePlayback, loaded.MouseBindings[MouseBindingDefaults.GlobalKey("LeftClick")]);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Load_MigratesLegacyAutoHideSettingToBothPanels(bool autoHide)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""{"AutoHideControls":{{autoHide.ToString().ToLowerInvariant()}}}""");

        var loaded = new PlayerPreferencesStore(path).Load();

        Assert.Equal(autoHide, loaded.AutoHideHeader);
        Assert.Equal(autoHide, loaded.AutoHideBottom);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void Load_PreservesIndependentPanelAutoHideSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"AutoHideControls\":true,\"AutoHideHeader\":false,\"AutoHideBottom\":true}");

        var loaded = new PlayerPreferencesStore(path).Load();

        Assert.False(loaded.AutoHideHeader);
        Assert.True(loaded.AutoHideBottom);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void Load_MigratesPreviousDefaultsButKeepsCustomizedShortcuts()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"Shortcuts\":{\"CaptureFrame\":\"S\",\"ToggleFullscreen\":\"F\",\"ToggleSubtitles\":\"T\",\"SubtitleDelayEarlier\":\"Ctrl+Shift+Left\",\"SubtitleDelayLater\":\"Y\",\"TogglePlayback\":\"P\"}}");

        var loaded = new PlayerPreferencesStore(path).Load();

        Assert.Equal("\";S", loaded.Shortcuts[ShortcutDefaults.CaptureFrame]);
        Assert.Equal("Z", loaded.Shortcuts[ShortcutDefaults.SubtitleDelayEarlier]);
        Assert.Equal("Y", loaded.Shortcuts[ShortcutDefaults.SubtitleDelayLater]);
        Assert.Equal("Enter", loaded.Shortcuts[ShortcutDefaults.ToggleFullscreen]);
        Assert.Equal("C", loaded.Shortcuts[ShortcutDefaults.ToggleSubtitles]);
        Assert.Equal("P", loaded.Shortcuts[ShortcutDefaults.TogglePlayback]);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void Load_FallsBackToDefaultsForInvalidJson()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{");
        var store = new PlayerPreferencesStore(path);

        AssertDefaults(store.Load());
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    private static void AssertDefaults(PlayerPreferences preferences)
    {
        var defaults = new PlayerPreferences();
        Assert.Equal(defaults.SubtitleFontSize, preferences.SubtitleFontSize);
        Assert.Equal(defaults.SubtitleMargin, preferences.SubtitleMargin);
        Assert.Equal(defaults.Volume, preferences.Volume);
        Assert.Equal(defaults.Muted, preferences.Muted);
        Assert.Equal(defaults.PlaybackSpeed, preferences.PlaybackSpeed);
        Assert.Equal(defaults.AudioTrackDisabled, preferences.AudioTrackDisabled);
        Assert.Equal(defaults.PreferredAudioLanguage, preferences.PreferredAudioLanguage);
        Assert.Equal(defaults.PreferredAudioTrackIndex, preferences.PreferredAudioTrackIndex);
        Assert.Equal(defaults.PreferredSubtitleLanguage, preferences.PreferredSubtitleLanguage);
        Assert.Equal(defaults.PreferredSubtitleTrackIndex, preferences.PreferredSubtitleTrackIndex);
        Assert.Equal(defaults.SubtitleCodepage, preferences.SubtitleCodepage);
        Assert.Equal(defaults.SubtitlesVisible, preferences.SubtitlesVisible);
        Assert.Equal(defaults.AutoHideHeader, preferences.AutoHideHeader);
        Assert.Equal(defaults.AutoHideBottom, preferences.AutoHideBottom);
        Assert.Equal(defaults.AccentColor, preferences.AccentColor);
        Assert.Equal(defaults.PlaybackSpeedStep, preferences.PlaybackSpeedStep);
        Assert.Equal(defaults.SubtitleDelayStep, preferences.SubtitleDelayStep);
        Assert.Equal(defaults.Shortcuts.OrderBy(pair => pair.Key), preferences.Shortcuts.OrderBy(pair => pair.Key));
        Assert.Null(preferences.ScreenshotDirectory);
        Assert.Equal(ScreenshotSaveModeRules.Original, preferences.ScreenshotSaveMode);
        Assert.Equal("Vertical", preferences.MouseRegionOrientation);
        Assert.Equal(2, preferences.MouseRegionCount);
        Assert.Equal(MouseBindingDefaults.Create(), preferences.MouseBindings);
    }
}
