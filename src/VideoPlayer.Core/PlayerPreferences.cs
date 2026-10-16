using System.Text.Json;

namespace VideoPlayer.Core;

public sealed record PlayerPreferences
{
    public const int MaximumVolume = 150;
    public const int VolumeAdjustmentStep = 5;
    public const int MinimumSubtitleFontSize = 18;
    public const int MaximumSubtitleFontSize = 64;
    public const int MinimumSubtitleMargin = 0;
    public const int MaximumSubtitleMargin = 100;
    public const int MinimumSubtitlePosition = 0;
    public const int MaximumSubtitlePosition = 100;
    public const int MinimumSubtitleBackgroundOpacity = 0;
    public const int MaximumSubtitleBackgroundOpacity = 100;
    public const double MinimumPlaybackSpeed = 0.25;
    public const double MaximumPlaybackSpeed = 10.0;

    public int SubtitleFontSize { get; init; } = 42;
    public int SubtitleMargin { get; init; } = 30;
    public string SubtitleFontFamily { get; init; } = SubtitleAppearanceRules.DefaultFontFamily;
    public bool SubtitleBold { get; init; }
    public bool SubtitleItalic { get; init; }
    public string SubtitleTextColor { get; init; } = "#FFFFFF";
    public bool SubtitleBackgroundEnabled { get; init; }
    public int SubtitleBackgroundOpacity { get; init; } = 75;
    public double SubtitleOutlineSize { get; init; } = 1.65;
    public double SubtitleShadowOffset { get; init; }
    public int SubtitlePosition { get; init; } = 100;
    public string SubtitleHorizontalAlignment { get; init; } = SubtitleAppearanceRules.CenterAlignment;
    public int Volume { get; init; } = 100;
    public bool Muted { get; init; }
    public double PlaybackSpeed { get; init; } = 1.0;
    public bool AudioTrackDisabled { get; init; }
    public bool HardwareAcceleration { get; init; } = true;
    public bool SubtitlesVisible { get; init; } = true;
    public string SubtitleCodepage { get; init; } = SubtitleCodepageRules.Auto;
    public bool AutoFindSubtitles { get; init; } = true;
    public bool AutoSynchronizeSubtitles { get; init; } = true;
    public string SubtitleSearchLanguage { get; init; } = SubtitleAutomationOptions.DefaultLanguage;
    public string OpenSubtitlesApiKey { get; init; } = string.Empty;
    public string SubtitleSynchronizerPath { get; init; } = SubtitleAutomationOptions.DefaultSynchronizer;
    public string VideoAspectRatio { get; init; } = VideoDisplayRules.AutomaticAspectRatio;
    public string VideoScaleMode { get; init; } = VideoDisplayRules.FitScaleMode;
    public bool AutoHideHeader { get; init; } = true;
    public bool AutoHideBottom { get; init; } = true;
    public string AccentColor { get; init; } = "#E53935";
    public double PlaybackSpeedStep { get; init; } = 0.10;
    public double SubtitleDelayStep { get; init; } = 0.10;
    public string? PreferredAudioLanguage { get; init; }
    public int PreferredAudioTrackIndex { get; init; }
    public string? PreferredSubtitleLanguage { get; init; }
    public int PreferredSubtitleTrackIndex { get; init; }
    public Dictionary<string, string> Shortcuts { get; init; } = ShortcutDefaults.Create();
    public string? ScreenshotDirectory { get; init; }
    public string ScreenshotSaveMode { get; init; } = ScreenshotSaveModeRules.Original;
    public string MouseRegionOrientation { get; init; } = "Vertical";
    public int MouseRegionCount { get; init; } = 2;
    public Dictionary<string, string> MouseBindings { get; init; } = MouseBindingDefaults.Create();
}

public static class ScreenshotSaveModeRules
{
    public const string Original = "original";
    public const string Optimized = "optimized";
    public const int OptimizedJpegQuality = 95;

    public static string Normalize(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        Optimized => Optimized,
        _ => Original
    };

    public static string GetExtension(string? mode) => Normalize(mode) == Optimized ? ".jpg" : ".png";
}

public static class PlaybackSpeedRules
{
    public static double Clamp(double speed) => double.IsFinite(speed)
        ? Math.Clamp(Math.Round(speed, 2), PlayerPreferences.MinimumPlaybackSpeed, PlayerPreferences.MaximumPlaybackSpeed)
        : 1.0;
}

public static class SubtitleAppearanceRules
{
    public const string DefaultFontFamily = "Arial";
    public const string LeftAlignment = "left";
    public const string CenterAlignment = "center";
    public const string RightAlignment = "right";

    public static IReadOnlyList<string> FontFamilies { get; } =
    [
        "Arial",
        "Arial Black",
        "Bahnschrift",
        "Calibri",
        "Cambria",
        "Candara",
        "Comic Sans MS",
        "Consolas",
        "Constantia",
        "Corbel",
        "Courier New",
        "Franklin Gothic Medium",
        "Georgia",
        "Impact",
        "Lucida Sans Unicode",
        "Microsoft Sans Serif",
        "Palatino Linotype",
        "Segoe UI",
        "Sitka Text",
        "Sylfaen",
        "Tahoma",
        "Times New Roman",
        "Trebuchet MS",
        "Verdana"
    ];

    public static string NormalizeFontFamily(string? value) => FontFamilies.FirstOrDefault(font =>
        string.Equals(font, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? DefaultFontFamily;

    public static string NormalizeTextColor(string? value)
    {
        var color = value?.Trim() ?? string.Empty;
        return color.Length == 7 && color[0] == '#' && color.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0
            ? color.ToUpperInvariant() : "#FFFFFF";
    }

    public static string NormalizeHorizontalAlignment(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        LeftAlignment => LeftAlignment,
        RightAlignment => RightAlignment,
        _ => CenterAlignment
    };
}

public static class VideoDisplayRules
{
    public const string AutomaticAspectRatio = "auto";
    public const string FitScaleMode = "fit";
    public const string FillScaleMode = "fill";
    public const string StretchScaleMode = "stretch";

    public static IReadOnlyList<(string Label, string Value)> AspectRatios { get; } =
    [
        (UiText.Automatic, AutomaticAspectRatio),
        ("4:3", "4:3"),
        ("16:9", "16:9"),
        ("21:9", "21:9")
    ];

    public static IReadOnlyList<(string Label, string Value)> ScaleModes { get; } =
    [
        (UiText.Fit, FitScaleMode),
        (UiText.Fill, FillScaleMode),
        (UiText.Stretch, StretchScaleMode)
    ];

    public static string NormalizeAspectRatio(string? value) =>
        AspectRatios.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase)).Value
        ?? AutomaticAspectRatio;

    public static string NormalizeScaleMode(string? value) =>
        ScaleModes.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase)).Value
        ?? FitScaleMode;
}

public static class VolumeWheelRules
{
    public static int Adjust(int currentVolume, double wheelDelta)
    {
        if (!double.IsFinite(wheelDelta) || wheelDelta == 0) return Math.Clamp(currentVolume, 0, PlayerPreferences.MaximumVolume);
        var direction = Math.Sign(wheelDelta);
        return Math.Clamp(currentVolume + direction * PlayerPreferences.VolumeAdjustmentStep, 0, PlayerPreferences.MaximumVolume);
    }
}

public static class ShortcutDefaults
{
    public const string TogglePlayback = "TogglePlayback";
    public const string SeekBackward = "SeekBackward";
    public const string SeekForward = "SeekForward";
    public const string SeekLongBackward = "SeekLongBackward";
    public const string SeekLongForward = "SeekLongForward";
    public const string SpeedDown = "SpeedDown";
    public const string SpeedUp = "SpeedUp";
    public const string SpeedReset = "SpeedReset";
    public const string CaptureFrame = "CaptureFrame";
    public const string OpenVideo = "OpenVideo";
    public const string ToggleFullscreen = "ToggleFullscreen";
    public const string ToggleSubtitles = "ToggleSubtitles";
    public const string AddSubtitle = "AddSubtitle";
    public const string VolumeDown = "VolumeDown";
    public const string VolumeUp = "VolumeUp";
    public const string ToggleMute = "ToggleMute";
    public const string SubtitleDelayEarlier = "SubtitleDelayEarlier";
    public const string SubtitleDelayLater = "SubtitleDelayLater";
    public const string SubtitleDelayReset = "SubtitleDelayReset";
    public const string ToggleSettings = "ToggleSettings";
    public const string PreviousFrame = "PreviousFrame";
    public const string NextFrame = "NextFrame";
    public const string PreviousChapter = "PreviousChapter";
    public const string NextChapter = "NextChapter";
    public const string CloseWindow = "CloseWindow";

    public static Dictionary<string, string> Create() => new(StringComparer.Ordinal)
    {
        [TogglePlayback] = "Space",
        [SeekBackward] = "Left",
        [SeekForward] = "Right",
        [PreviousChapter] = "Ctrl+PageUp",
        [NextChapter] = "Ctrl+PageDown",
        [SpeedDown] = "Subtract",
        [SpeedUp] = "Add",
        [SpeedReset] = "Back",
        [CaptureFrame] = "\";S",
        [OpenVideo] = "O",
        [ToggleFullscreen] = "Enter",
        [ToggleSubtitles] = "C",
        [AddSubtitle] = "Ctrl+Shift+T",
        [VolumeDown] = "Ctrl+Down",
        [VolumeUp] = "Ctrl+Up",
        [SubtitleDelayEarlier] = "Z",
        [SubtitleDelayLater] = "X",
        [SubtitleDelayReset] = "Ctrl+Shift+Back",
        [ToggleSettings] = "Tab",
        [ToggleMute] = "M",
        [PreviousFrame] = "Ctrl+Left",
        [NextFrame] = "Ctrl+Right",
        [CloseWindow] = "Alt+F4;Ctrl+W"
    };
}

public static class MouseBindingDefaults
{
    public const string GlobalPrefix = "Global:";
    public const string CenterPrefix = "Center:";

    public static string GlobalKey(string gesture) => $"{GlobalPrefix}{gesture}";
    public static string CenterKey(string gesture) => $"{CenterPrefix}{gesture}";

    public static Dictionary<string, string> Create() => new(StringComparer.Ordinal)
    {
        [GlobalKey("LeftClick")] = ShortcutDefaults.TogglePlayback,
        [GlobalKey("RightClick")] = ShortcutDefaults.CaptureFrame,
        [GlobalKey("MiddleClick")] = ShortcutDefaults.ToggleFullscreen,
        [GlobalKey("WheelUp")] = ShortcutDefaults.SeekForward,
        [GlobalKey("WheelDown")] = ShortcutDefaults.SeekBackward,
        [GlobalKey("CtrlWheelUp")] = ShortcutDefaults.NextFrame,
        [GlobalKey("CtrlWheelDown")] = ShortcutDefaults.PreviousFrame,
        [GlobalKey("ShiftWheelUp")] = ShortcutDefaults.SeekLongForward,
        [GlobalKey("ShiftWheelDown")] = ShortcutDefaults.SeekLongBackward,
        [GlobalKey("AltWheelUp")] = ShortcutDefaults.VolumeUp,
        [GlobalKey("AltWheelDown")] = ShortcutDefaults.VolumeDown,
        [CenterKey("WheelUp")] = ShortcutDefaults.SeekForward,
        [CenterKey("WheelDown")] = ShortcutDefaults.SeekBackward
    };
}

public static class MouseBindingRules
{
    public static string? FindAction(string gesture, double relativeX, double relativeY, bool insideVideo,
        string orientation, int regionCount, IReadOnlyDictionary<string, string> bindings)
    {
        if (gesture is "WheelUp" or "WheelDown")
        {
            var defaultWheelAction = gesture == "WheelUp" ? ShortcutDefaults.SeekForward : ShortcutDefaults.SeekBackward;
            if (insideVideo && relativeX is >= 0.4 and <= 0.6 && relativeY is >= 0.4 and <= 0.6
                && bindings.TryGetValue(MouseBindingDefaults.CenterKey(gesture), out var wheelCenterAction)
                && !string.IsNullOrWhiteSpace(wheelCenterAction)
                && (!string.Equals(wheelCenterAction, defaultWheelAction, StringComparison.Ordinal)
                    || !bindings.ContainsKey(MouseBindingDefaults.GlobalKey(gesture))))
                return wheelCenterAction;

            if (insideVideo)
            {
                var wheelPosition = orientation == "Horizontal" ? relativeY : relativeX;
                var wheelRegion = MouseRegionRules.GetRegionIndex(wheelPosition, regionCount);
                if (bindings.TryGetValue($"{wheelRegion}:{gesture}", out var wheelRegionAction)
                    && !string.IsNullOrWhiteSpace(wheelRegionAction))
                    return wheelRegionAction;
            }

            return bindings.TryGetValue(MouseBindingDefaults.GlobalKey(gesture), out var wheelAction)
                && !string.IsNullOrWhiteSpace(wheelAction)
                    ? wheelAction
                    : null;
        }

        if (gesture is "RightClick" or "AltWheelUp" or "AltWheelDown"
            && bindings.TryGetValue(MouseBindingDefaults.GlobalKey(gesture), out var windowAction)
            && !string.IsNullOrWhiteSpace(windowAction))
            return windowAction;
        if (!insideVideo) return null;
        if (bindings.TryGetValue(MouseBindingDefaults.GlobalKey(gesture), out var globalAction)
            && !string.IsNullOrWhiteSpace(globalAction))
            return globalAction;

        var position = orientation == "Horizontal" ? relativeY : relativeX;
        var region = MouseRegionRules.GetRegionIndex(position, regionCount);
        return bindings.TryGetValue($"{region}:{gesture}", out var regionAction) && !string.IsNullOrWhiteSpace(regionAction)
            ? regionAction
            : null;
    }
}

public static class ShortcutBindingRules
{
    public static IReadOnlyList<string> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        return value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(ShortcutGestureRules.IsValid)
            .Select(ShortcutGestureRules.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string Format(IEnumerable<string> gestures) => string.Join(";", gestures
        .Where(ShortcutGestureRules.IsValid)
        .Select(ShortcutGestureRules.Normalize)
        .Distinct(StringComparer.OrdinalIgnoreCase));

    public static string? FindAction(IReadOnlyDictionary<string, string> bindings, string gesture)
    {
        var normalizedGesture = ShortcutGestureRules.Normalize(gesture);
        foreach (var (action, value) in bindings)
            if (Parse(value).Contains(normalizedGesture, StringComparer.OrdinalIgnoreCase)) return action;
        return null;
    }

    public static bool IsAssignedToAnotherAction(IReadOnlyDictionary<string, string> bindings, string action, string gesture) =>
        bindings.Any(pair => !string.Equals(pair.Key, action, StringComparison.Ordinal)
            && Parse(pair.Value).Contains(ShortcutGestureRules.Normalize(gesture), StringComparer.OrdinalIgnoreCase));
}

internal static class ShortcutGestureRules
{
    public static bool IsValid(string gesture)
    {
        var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        var key = parts[^1];
        if (parts.Length > 1 && parts[..^1].Any(part => part.ToLowerInvariant() is not ("ctrl" or "control" or "alt" or "shift" or "win" or "meta")))
            return false;
        return key is "\"" or "," or "." or "Ö" or "ö" or "Ç" or "ç"
            || key.Length > 0 && key.All(char.IsAsciiLetterOrDigit);
    }

    public static string Normalize(string gesture)
    {
        var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return gesture;
        var key = parts[^1].ToLowerInvariant() switch
        {
            "oem3" or "quote" or "\"" => "\"",
            "backspace" => "Back",
            "enter" => "Return",
            "," or "ö" => "OemComma",
            "." or "ç" => "OemPeriod",
            "plus" => "OemPlus",
            "minus" => "OemMinus",
            _ => parts[^1]
        };
        var modifiers = parts[..^1].Select(part => part.ToLowerInvariant() switch
        {
            "ctrl" or "control" => "Ctrl",
            "alt" => "Alt",
            "shift" => "Shift",
            "win" or "meta" => "Win",
            _ => part
        });
        return string.Join('+', modifiers.Append(key));
    }
}

public sealed class PlayerPreferencesStore(string settingsPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public PlayerPreferences Load()
    {
        if (!File.Exists(settingsPath)) return new PlayerPreferences();
        try
        {
            var json = File.ReadAllText(settingsPath);
            var preferences = JsonSerializer.Deserialize<PlayerPreferences>(json) ?? new PlayerPreferences();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("AutoHideControls", out var legacyAutoHide)
                && legacyAutoHide.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var legacyValue = legacyAutoHide.GetBoolean();
                preferences = preferences with
                {
                    AutoHideHeader = root.TryGetProperty(nameof(PlayerPreferences.AutoHideHeader), out _)
                        ? preferences.AutoHideHeader : legacyValue,
                    AutoHideBottom = root.TryGetProperty(nameof(PlayerPreferences.AutoHideBottom), out _)
                        ? preferences.AutoHideBottom : legacyValue
                };
            }
            return NormalizeShortcuts(preferences);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new PlayerPreferences();
        }
    }

    public void Save(PlayerPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporaryPath = settingsPath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(NormalizeShortcuts(preferences), JsonOptions));
            File.Move(temporaryPath, settingsPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static PlayerPreferences NormalizeShortcuts(PlayerPreferences preferences)
    {
        var shortcuts = ShortcutDefaults.Create();
        if (preferences.Shortcuts is not null)
            foreach (var (action, gesture) in preferences.Shortcuts)
                if (shortcuts.ContainsKey(action) && !string.IsNullOrWhiteSpace(gesture)
                    && !IsLegacyDefault(action, gesture))
                    shortcuts[action] = gesture;
        return preferences with
        {
            SubtitleFontSize = Math.Clamp(
                preferences.SubtitleFontSize,
                PlayerPreferences.MinimumSubtitleFontSize,
                PlayerPreferences.MaximumSubtitleFontSize),
            SubtitleMargin = Math.Clamp(
                preferences.SubtitleMargin,
                PlayerPreferences.MinimumSubtitleMargin,
                PlayerPreferences.MaximumSubtitleMargin),
            SubtitleFontFamily = SubtitleAppearanceRules.NormalizeFontFamily(preferences.SubtitleFontFamily),
            SubtitleTextColor = SubtitleAppearanceRules.NormalizeTextColor(preferences.SubtitleTextColor),
            SubtitleBackgroundOpacity = Math.Clamp(preferences.SubtitleBackgroundOpacity,
                PlayerPreferences.MinimumSubtitleBackgroundOpacity, PlayerPreferences.MaximumSubtitleBackgroundOpacity),
            SubtitleOutlineSize = double.IsFinite(preferences.SubtitleOutlineSize)
                ? Math.Clamp(preferences.SubtitleOutlineSize, 0, 10) : 1.65,
            SubtitleShadowOffset = double.IsFinite(preferences.SubtitleShadowOffset)
                ? Math.Clamp(preferences.SubtitleShadowOffset, 0, 20) : 0,
            SubtitlePosition = Math.Clamp(preferences.SubtitlePosition,
                PlayerPreferences.MinimumSubtitlePosition, PlayerPreferences.MaximumSubtitlePosition),
            SubtitleHorizontalAlignment = SubtitleAppearanceRules.NormalizeHorizontalAlignment(preferences.SubtitleHorizontalAlignment),
            Volume = Math.Clamp(preferences.Volume, 0, PlayerPreferences.MaximumVolume),
            PlaybackSpeed = PlaybackSpeedRules.Clamp(preferences.PlaybackSpeed),
            PreferredAudioLanguage = NormalizeTrackLanguage(preferences.PreferredAudioLanguage),
            PreferredAudioTrackIndex = Math.Clamp(preferences.PreferredAudioTrackIndex, 0, 256),
            PreferredSubtitleLanguage = NormalizeTrackLanguage(preferences.PreferredSubtitleLanguage),
            PreferredSubtitleTrackIndex = Math.Clamp(preferences.PreferredSubtitleTrackIndex, 0, 256),
            SubtitleCodepage = SubtitleCodepageRules.Normalize(preferences.SubtitleCodepage),
            SubtitleSearchLanguage = SubtitleAutomationOptions.NormalizeLanguage(preferences.SubtitleSearchLanguage),
            OpenSubtitlesApiKey = preferences.OpenSubtitlesApiKey?.Trim() ?? string.Empty,
            SubtitleSynchronizerPath = string.IsNullOrWhiteSpace(preferences.SubtitleSynchronizerPath)
                ? SubtitleAutomationOptions.DefaultSynchronizer : preferences.SubtitleSynchronizerPath.Trim(),
            VideoAspectRatio = VideoDisplayRules.NormalizeAspectRatio(preferences.VideoAspectRatio),
            VideoScaleMode = VideoDisplayRules.NormalizeScaleMode(preferences.VideoScaleMode),
            AccentColor = NormalizeAccentColor(preferences.AccentColor),
            ScreenshotSaveMode = ScreenshotSaveModeRules.Normalize(preferences.ScreenshotSaveMode),
            Shortcuts = shortcuts,
            PlaybackSpeedStep = double.IsFinite(preferences.PlaybackSpeedStep)
                ? Math.Clamp(preferences.PlaybackSpeedStep, 0.05, 1.0) : 0.10,
            SubtitleDelayStep = double.IsFinite(preferences.SubtitleDelayStep)
                ? Math.Clamp(preferences.SubtitleDelayStep, 0.05, 1.0) : 0.10,
            MouseBindings = MergeMouseBindingDefaults(preferences.MouseBindings),
            MouseRegionOrientation = preferences.MouseRegionOrientation == "Horizontal" ? "Horizontal" : "Vertical",
            MouseRegionCount = preferences.MouseRegionCount == 3 ? 3 : 2
        };
    }

    private static string? NormalizeTrackLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) ? null : language.Trim();

    private static bool IsLegacyDefault(string action, string gesture) => action switch
    {
        ShortcutDefaults.CaptureFrame => gesture.Equals("S", StringComparison.OrdinalIgnoreCase),
        ShortcutDefaults.ToggleFullscreen => gesture.Equals("F", StringComparison.OrdinalIgnoreCase),
        ShortcutDefaults.ToggleSubtitles => gesture.Equals("T", StringComparison.OrdinalIgnoreCase),
        ShortcutDefaults.PreviousChapter => gesture.Equals("Ctrl+Left", StringComparison.OrdinalIgnoreCase),
        ShortcutDefaults.NextChapter => gesture.Equals("Ctrl+Right", StringComparison.OrdinalIgnoreCase),
        ShortcutDefaults.PreviousFrame => gesture.Equals("OemComma", StringComparison.OrdinalIgnoreCase)
            || gesture.Equals("Ö", StringComparison.OrdinalIgnoreCase),
        ShortcutDefaults.NextFrame => gesture.Equals("OemPeriod", StringComparison.OrdinalIgnoreCase)
            || gesture.Equals("Ç", StringComparison.OrdinalIgnoreCase),
        ShortcutDefaults.SubtitleDelayEarlier => gesture.Equals("Ctrl+Shift+Left", StringComparison.OrdinalIgnoreCase),
        ShortcutDefaults.SubtitleDelayLater => gesture.Equals("Ctrl+Shift+Right", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static string NormalizeAccentColor(string? color) => color?.ToUpperInvariant() switch
    {
        "#BCA7FF" or "#68D7C1" or "#FF9A88" => "#E53935",
        "#E53935" or "#C9364F" or "#B0B0B0" => color!.ToUpperInvariant(),
        _ => "#E53935"
    };

    private static Dictionary<string, string> MergeMouseBindingDefaults(Dictionary<string, string>? existing)
    {
        var bindings = MouseBindingDefaults.Create();
        if (existing is not null)
            foreach (var binding in existing) bindings[binding.Key] = binding.Value;
        return bindings;
    }
}
