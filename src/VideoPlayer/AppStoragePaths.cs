using System.Security.Cryptography;
using System.Text;

namespace VideoPlayer;

internal static class AppStoragePaths
{
    private const string DataDirectoryVariable = "PANTHEON_DATA_DIRECTORY";
    private const string LegacyDataDirectoryVariable = "PANTEON_DATA_DIRECTORY";
    private const string DefaultApplicationDirectoryName = "LumaPlayer";
    private const string DefaultInstanceName = "Panteon.VideoPlayer.SingleInstance";
    private const string InstanceNamePrefix = "Pantheon.VideoPlayer";

    private static string? CustomDirectory =>
        Environment.GetEnvironmentVariable(DataDirectoryVariable) ?? Environment.GetEnvironmentVariable(LegacyDataDirectoryVariable);

    public static string DataDirectory => GetDataDirectory(CustomDirectory);

    public static string LogDirectory => HasCustomDirectory
        ? Path.Combine(DataDirectory, "Logs")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pantheon", "Logs");

    public static string ScreenshotDirectory => HasCustomDirectory
        ? Path.Combine(DataDirectory, "Screenshots")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Pantheon");

    public static string InstanceName => GetProfileInstanceName(CustomDirectory);

    private static bool HasCustomDirectory =>
        !string.IsNullOrWhiteSpace(CustomDirectory);

    internal static string GetDataDirectory(string? customDirectory) => string.IsNullOrWhiteSpace(customDirectory)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DefaultApplicationDirectoryName)
        : Path.GetFullPath(customDirectory.Trim());

    internal static string GetProfileInstanceName(string? customDirectory) =>
        string.IsNullOrWhiteSpace(customDirectory) ? DefaultInstanceName : GetInstanceName(GetDataDirectory(customDirectory));

    internal static string GetInstanceName(string dataDirectory)
    {
        var canonicalDirectory = Path.GetFullPath(dataDirectory);
        var directoryRoot = Path.GetPathRoot(canonicalDirectory);
        if (!string.Equals(canonicalDirectory, directoryRoot, StringComparison.OrdinalIgnoreCase))
            canonicalDirectory = canonicalDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedDirectory = canonicalDirectory.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedDirectory)));
        return $"{InstanceNamePrefix}.{hash}";
    }
}
