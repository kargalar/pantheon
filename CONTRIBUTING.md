# Contributing to Pantheon

Use Windows x64 and the .NET 10 SDK. Start with the [build instructions](README.md#build-from-source).

Keep changes focused, preserve existing preferences and media files, and use clear names and small functions. Playback rules and persistence belong in `VideoPlayer.Core`; UI and platform integration belong in `VideoPlayer`.

Before submitting a pull request:

```powershell
dotnet format .\Pantheon.slnx --verify-no-changes
dotnet test .\Pantheon.slnx -c Release
dotnet publish .\src\VideoPlayer\VideoPlayer.csproj -c Release -r win-x64 --self-contained true -o .\dist\Pantheon
```

Run the relevant Windows runtime check for changes to rendering or subtitle automation; see [the guide](docs/usage.md#build-and-tests). Real speech synchronization tests need the optional subtitle bundle. Describe what changed and the checks you ran. Do not commit generated builds, profiles, logs, API keys or copyrighted media.

For bug reports, provide reproducible steps, expected and actual behavior, Windows version and relevant media/codec details. Sanitize logs and paths before attaching them.

Contributions to Pantheon's original code are accepted under the [MIT license](LICENSE).
