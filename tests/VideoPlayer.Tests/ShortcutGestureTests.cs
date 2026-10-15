using Avalonia.Input;
using VideoPlayer.Core;
using VideoPlayer;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class ShortcutGestureTests
{
    [Theory]
    [InlineData("Space", "Space")]
    [InlineData("Ctrl+Shift+s", "Ctrl+Shift+S")]
    [InlineData("Control+Alt+Left", "Ctrl+Alt+Left")]
    [InlineData("plus", "Plus")]
    [InlineData("minus", "Minus")]
    [InlineData("Ctrl+Shift+Back", "Ctrl+Shift+Backspace")]
    [InlineData("OemComma", ",")]
    [InlineData("OemPeriod", ".")]
    [InlineData(",", ",")]
    [InlineData(".", ".")]
    public void TryParse_NormalizesValidGestures(string input, string expected)
    {
        Assert.True(ShortcutGesture.TryParse(input, out var gesture));
        Assert.Equal(expected, gesture.ToString());
    }

    [Theory]
    [InlineData(0x20, "Space")]
    [InlineData(0x25, "Left")]
    [InlineData(0x27, "Right")]
    [InlineData(0xBB, "Plus")]
    [InlineData(0xBD, "Minus")]
    [InlineData(0xBC, ",")]
    [InlineData(0xBE, ".")]
    public void TryFromVirtualKey_UsesSameNamesAsSettings(int virtualKey, string expected)
    {
        Assert.True(ShortcutGesture.TryFromVirtualKey(virtualKey, KeyModifiers.None, out var gesture));
        Assert.Equal(expected, gesture.ToString());
    }

    [Theory]
    [InlineData(Key.Space, "Space")]
    [InlineData(Key.Left, "Left")]
    [InlineData(Key.Right, "Right")]
    [InlineData(Key.S, "S")]
    [InlineData(Key.OemPlus, "Plus")]
    public void TryFromAvalonia_UsesSameNamesAsWindowsHook(Key key, string expected)
    {
        Assert.True(ShortcutGesture.TryFromAvalonia(key, KeyModifiers.None, out var gesture));
        Assert.Equal(expected, gesture.ToString());
    }

    [Fact]
    public void TryParse_RejectsUnknownKeysAndRepeatedModifiers()
    {
        Assert.False(ShortcutGesture.TryParse("Ctrl+NotAKey", out _));
        Assert.False(ShortcutGesture.TryParse("Ctrl+Control+S", out _));
        Assert.False(ShortcutGesture.TryParse("", out _));
    }

    [Fact]
    public void TryParse_RecognizesTheKeyBelowEscapeAsTheScreenshotShortcut()
    {
        Assert.True(ShortcutGesture.TryParse("\"", out var gesture));
        Assert.Equal("\"", gesture.ToString());
        Assert.True(ShortcutGesture.TryFromVirtualKey(0xC0, KeyModifiers.None, out var physicalKey));
        Assert.Equal(gesture, physicalKey);
    }

    [Fact]
    public void EnterKeyUsesTheFullscreenShortcutName()
    {
        Assert.True(ShortcutGesture.TryFromVirtualKey(0x0D, KeyModifiers.None, out var gesture));
        Assert.Equal("Enter", gesture.ToString());
        Assert.True(ShortcutGesture.TryParse("Enter", out var parsed));
        Assert.Equal(gesture, parsed);
    }

    [Theory]
    [InlineData(Key.Left, ShortcutDefaults.PreviousFrame)]
    [InlineData(Key.Right, ShortcutDefaults.NextFrame)]
    public void CtrlArrowShortcutsStepFrames(Key key, string expectedAction)
    {
        Assert.True(ShortcutGesture.TryFromAvalonia(key, KeyModifiers.Control, out var gesture));

        Assert.Equal(expectedAction,
            ShortcutBindingRules.FindAction(ShortcutDefaults.Create(), gesture.ToString()));
    }

    [Theory]
    [InlineData("Ö", 0xBC, ",")]
    [InlineData("Ç", 0xBE, ".")]
    public void LegacyFrameStepKeysKeepTheirPhysicalBindingAndUseEnglishDisplay(string key, int virtualKey, string expectedDisplay)
    {
        Assert.True(ShortcutGesture.TryParse(key, out var parsed));
        Assert.True(ShortcutGesture.TryFromVirtualKey(virtualKey, KeyModifiers.None, out var physical));
        Assert.Equal(expectedDisplay, parsed.ToString());
        Assert.Equal(parsed, physical);
    }
}
