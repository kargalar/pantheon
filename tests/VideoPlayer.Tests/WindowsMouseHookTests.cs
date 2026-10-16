using VideoPlayer;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class WindowsMouseHookTests
{
    [Theory]
    [InlineData(0x0200, false)]
    [InlineData(0x0201, true)]
    [InlineData(0x020A, true)]
    public void IsRelevantMessage_SkipsPointerMoveAndKeepsConfiguredGestures(int message, bool expected) =>
        Assert.Equal(expected, WindowsMouseHook.IsRelevantMessage(message));

    [Fact]
    public void AltWheelKeepsDirectionAndUsesDedicatedGestures()
    {
        Assert.Equal(MouseGesture.AltWheelUp, WindowsMouseHook.WheelGesture(120u << 16, altPressed: true));
        Assert.Equal(MouseGesture.AltWheelDown, WindowsMouseHook.WheelGesture(0xFF880000, altPressed: true));
        Assert.Equal(MouseGesture.WheelUp, WindowsMouseHook.WheelGesture(120u << 16, altPressed: false));
    }

    [Fact]
    public void WheelGesture_ReturnsNullWhenWindowsKeyOrConflictingModifiersPressed()
    {
        const uint wheelUp = 120u << 16;
        Assert.Null(WindowsMouseHook.WheelGesture(wheelUp, controlPressed: false, shiftPressed: false, altPressed: false, winPressed: true));
        Assert.Null(WindowsMouseHook.WheelGesture(wheelUp, controlPressed: true, shiftPressed: false, altPressed: true, winPressed: false));
        Assert.Null(WindowsMouseHook.WheelGesture(wheelUp, controlPressed: true, shiftPressed: true, altPressed: false, winPressed: false));
    }

    [Theory]
    [InlineData(0x0201, "LeftClick")]
    [InlineData(0x0204, "RightClick")]
    [InlineData(0x0207, "MiddleClick")]
    public void ResolveGesture_ReturnsPlainClicksWhenNoModifiersActive(int messageId, string expected)
    {
        var gesture = WindowsMouseHook.ResolveGesture(
            messageId, 0, controlPressed: false, shiftPressed: false, altPressed: false, winPressed: false);
        Assert.Equal(expected, gesture?.ToString());
    }

    [Theory]
    [InlineData(0x0201)]
    [InlineData(0x0204)]
    [InlineData(0x0207)]
    [InlineData(0x020A)]
    public void ResolveGesture_ReturnsNullWhenWindowsKeyIsActive(int messageId)
    {
        const uint wheelUp = 120u << 16;
        var gesture = WindowsMouseHook.ResolveGesture(
            messageId, wheelUp, controlPressed: false, shiftPressed: false, altPressed: false, winPressed: true);
        Assert.Null(gesture);
    }

    [Theory]
    [InlineData(0x0201)]
    [InlineData(0x0204)]
    [InlineData(0x0207)]
    public void ResolveGesture_ReturnsNullWhenModifierKeyIsActiveOnClicks(int messageId)
    {
        Assert.Null(WindowsMouseHook.ResolveGesture(messageId, 0, controlPressed: true, shiftPressed: false, altPressed: false));
        Assert.Null(WindowsMouseHook.ResolveGesture(messageId, 0, controlPressed: false, shiftPressed: true, altPressed: false));
        Assert.Null(WindowsMouseHook.ResolveGesture(messageId, 0, controlPressed: false, shiftPressed: false, altPressed: true));
    }
}
