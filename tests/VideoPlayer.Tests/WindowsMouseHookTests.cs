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
}
