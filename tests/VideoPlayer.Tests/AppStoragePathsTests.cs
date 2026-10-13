using VideoPlayer;
using Xunit;

namespace VideoPlayer.Tests;

public sealed class AppStoragePathsTests
{
    [Fact]
    public void GetDataDirectory_UsesCustomAbsoluteDirectory()
    {
        var customDirectory = Path.Combine(Path.GetTempPath(), "pantheon-profile", "..");

        var dataDirectory = AppStoragePaths.GetDataDirectory(customDirectory);

        Assert.Equal(Path.GetFullPath(customDirectory), dataDirectory);
    }

    [Fact]
    public void GetDataDirectory_PreservesExistingDefaultLocation()
    {
        var expectedDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LumaPlayer");

        Assert.Equal(expectedDirectory, AppStoragePaths.GetDataDirectory(null));
        Assert.Equal(expectedDirectory, AppStoragePaths.GetDataDirectory(" "));
    }

    [Fact]
    public void GetInstanceName_IsStableForOneProfileAndDifferentAcrossProfiles()
    {
        var firstName = AppStoragePaths.GetInstanceName(Path.Combine(Path.GetTempPath(), "profile-one"));
        var sameProfileName = AppStoragePaths.GetInstanceName(Path.Combine(Path.GetTempPath(), "PROFILE-ONE"));
        var secondName = AppStoragePaths.GetInstanceName(Path.Combine(Path.GetTempPath(), "profile-two"));

        Assert.Equal(firstName, sameProfileName);
        Assert.NotEqual(firstName, secondName);
    }

    [Fact]
    public void DefaultInstanceName_PreservesExistingSingleInstanceIdentity()
    {
        Assert.Equal("Panteon.VideoPlayer.SingleInstance", AppStoragePaths.GetProfileInstanceName(null));
    }
}
