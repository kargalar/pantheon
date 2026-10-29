using VideoPlayer.Core;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class ShortcutBindingRulesTests
{
    [Fact]
    public void ParseSupportsSeveralBindingsForOneActionAndNormalizesQuoteKey()
    {
        Assert.Equal(new[] { "\"", "S" }, ShortcutBindingRules.Parse("Oem3; S; Oem3"));
    }

    [Fact]
    public void FindActionResolvesEveryBindingForAnAction()
    {
        var bindings = ShortcutDefaults.Create();

        Assert.Equal(ShortcutDefaults.CaptureFrame, ShortcutBindingRules.FindAction(bindings, "\""));
        Assert.Equal(ShortcutDefaults.CaptureFrame, ShortcutBindingRules.FindAction(bindings, "S"));
        Assert.Equal(ShortcutDefaults.ToggleSettings, ShortcutBindingRules.FindAction(bindings, "Tab"));
        Assert.Equal(ShortcutDefaults.SubtitleDelayReset, ShortcutBindingRules.FindAction(bindings, "Ctrl+Shift+Backspace"));
        Assert.Equal(ShortcutDefaults.SubtitleDelayReset, ShortcutBindingRules.FindAction(bindings, "<"));
        Assert.Equal(ShortcutDefaults.SubtitleDelayReset, ShortcutBindingRules.FindAction(bindings, "Oem102"));
        Assert.Equal(ShortcutDefaults.ToggleSubtitles, ShortcutBindingRules.FindAction(bindings, "C"));
        Assert.Equal(ShortcutDefaults.ToggleMute, ShortcutBindingRules.FindAction(bindings, "M"));
        Assert.Equal(ShortcutDefaults.PreviousFrame, ShortcutBindingRules.FindAction(bindings, "Ctrl+Left"));
        Assert.Equal(ShortcutDefaults.NextFrame, ShortcutBindingRules.FindAction(bindings, "Ctrl+Right"));
        Assert.Equal(ShortcutDefaults.PreviousChapter, ShortcutBindingRules.FindAction(bindings, "Ctrl+PageUp"));
        Assert.Equal(ShortcutDefaults.NextChapter, ShortcutBindingRules.FindAction(bindings, "Ctrl+PageDown"));
        Assert.Equal(ShortcutDefaults.ToggleFullscreen, ShortcutBindingRules.FindAction(bindings, "Enter"));
        var commaBindings = new Dictionary<string, string>(bindings)
        {
            [ShortcutDefaults.PreviousFrame] = "OemComma",
            [ShortcutDefaults.NextFrame] = "OemPeriod"
        };
        Assert.Equal(ShortcutDefaults.PreviousFrame, ShortcutBindingRules.FindAction(commaBindings, "Ö"));
        Assert.Equal(ShortcutDefaults.NextFrame, ShortcutBindingRules.FindAction(commaBindings, "Ç"));
    }

    [Fact]
    public void DefaultsMapRequestedMouseGesturesToPlayerActions()
    {
        var bindings = MouseBindingDefaults.Create();

        Assert.Equal(ShortcutDefaults.TogglePlayback, bindings[MouseBindingDefaults.GlobalKey("LeftClick")]);
        Assert.Equal(ShortcutDefaults.CaptureFrame, bindings[MouseBindingDefaults.GlobalKey("RightClick")]);
        Assert.Equal(ShortcutDefaults.ToggleFullscreen, bindings[MouseBindingDefaults.GlobalKey("MiddleClick")]);
        Assert.Equal(ShortcutDefaults.SeekForward, bindings[MouseBindingDefaults.CenterKey("WheelUp")]);
        Assert.Equal(ShortcutDefaults.SeekBackward, bindings[MouseBindingDefaults.CenterKey("WheelDown")]);
        Assert.Equal(ShortcutDefaults.VolumeUp, bindings[MouseBindingDefaults.GlobalKey("AltWheelUp")]);
        Assert.Equal(ShortcutDefaults.VolumeDown, bindings[MouseBindingDefaults.GlobalKey("AltWheelDown")]);
    }

    [Fact]
    public void MouseRoutingUsesWindowRightClickAndCenteredWheelBeforeRegionMappings()
    {
        var bindings = MouseBindingDefaults.Create();
        bindings["0:WheelUp"] = ShortcutDefaults.SpeedUp;

        Assert.Equal(ShortcutDefaults.CaptureFrame,
            MouseBindingRules.FindAction("RightClick", 0.02, 0.98, false, "Vertical", 2, bindings));
        Assert.Equal(ShortcutDefaults.SeekForward,
            MouseBindingRules.FindAction("WheelUp", 0.5, 0.5, true, "Vertical", 2, bindings));
        Assert.Equal(ShortcutDefaults.SpeedUp,
            MouseBindingRules.FindAction("WheelUp", 0.1, 0.5, true, "Vertical", 2, bindings));
        Assert.Null(MouseBindingRules.FindAction("MiddleClick", 0.5, 0.5, false, "Vertical", 2, bindings));
    }

    [Fact]
    public void SameGestureCanRepeatForOneActionButConflictsAcrossActions()
    {
        var bindings = ShortcutDefaults.Create();
        bindings[ShortcutDefaults.CaptureFrame] += ";Space";

        Assert.False(ShortcutBindingRules.IsAssignedToAnotherAction(bindings, ShortcutDefaults.CaptureFrame, "S"));
        Assert.True(ShortcutBindingRules.IsAssignedToAnotherAction(bindings, ShortcutDefaults.TogglePlayback, "Space"));
    }
}
