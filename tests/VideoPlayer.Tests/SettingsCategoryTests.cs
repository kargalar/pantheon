using VideoPlayer;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class SettingsCategoryTests
{
    [Theory]
    [InlineData("appearance", "Appearance")]
    [InlineData("playback", "Playback")]
    [InlineData("subtitles", "Subtitle discovery and synchronization")]
    [InlineData("keyboard", "Keyboard shortcuts")]
    [InlineData("mouse", "Mouse gestures")]
    [InlineData("screenshots", "Screenshots")]
    [InlineData("diagnostics", "Diagnostic logs")]
    public void CategoryTitleCreatesIndependentControlForEveryPage(string category, string expectedTitle)
    {
        var first = SettingsWindow.CreateCategoryTitle(category);
        var second = SettingsWindow.CreateCategoryTitle(category);

        Assert.Equal(expectedTitle, first.Text);
        Assert.NotSame(first, second);
    }
}
