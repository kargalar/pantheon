# Third-party notices

Pantheon's original source code is MIT licensed; see `LICENSE`. This notice does not relicense upstream components. The Windows distribution includes the following software and their dependencies, whose original license and attribution requirements continue to apply.

| Component | Version / source | License and distribution notes |
| --- | --- | --- |
| .NET runtime | [.NET 10](https://github.com/dotnet/runtime) | MIT and upstream notices; see `Licenses/dotnet-LICENSE.txt` and `Licenses/dotnet-THIRD-PARTY-NOTICES.txt`. |
| Avalonia UI / Inter fonts | [Avalonia 12.0.3](https://github.com/AvaloniaUI/Avalonia/tree/12.0.3) | Avalonia is MIT; bundled Inter fonts are SIL OFL 1.1. See `Licenses/Avalonia-LICENSE.txt` and `Licenses/Inter-OFL.txt`. Native rendering dependencies retain their own licenses. |
| HanumanInstitute.LibMpv / Avalonia integration | [LibMpv 0.10.1](https://github.com/mysteryx93/LibMpv-OpenGL) | MIT; see `Licenses/HanumanInstitute-LICENSE.txt`. |
| libmpv (dynamically linked) | [Endpne.LibMPV.Windows 0.41.0](https://www.nuget.org/packages/Endpne.LibMPV.Windows/0.41.0); actual binary reports **mpv v0.41.0-85-g468d34c9b** and FFmpeg **N-122476-g685ceebd4** | The NuGet package declares LGPL-2.1-or-later, but this binary's configuration enables GPL features and does not specify `-Dgpl=false`. Treat the bundled binary as GPL, including its dependencies' terms; the Windows binary distribution must satisfy GPL requirements. See `Licenses/GPL-3.0.txt`, `Licenses/mpv-Copyright.txt`, and the build configuration in `Licenses/libmpv-build.txt`. |
| FFsubsync | [0.4.31 source](https://github.com/smacke/ffsubsync/tree/0.4.31) | MIT. Runs as a separate process. Full Python, NumPy, WebRTC VAD, parser and other dependency metadata/license files are included under `SubtitleTools/Licenses` and `_internal`. |
| FFmpeg / ffprobe | [Gyan FFmpeg 8.1.1 full build](https://www.gyan.dev/ffmpeg/builds/) | GPL-3.0-or-later; separate executables. See `SubtitleTools/Licenses/FFmpeg-LICENSE.txt` and `Licenses/GPL-3.0.txt`. |

## Upstream source and build information

The unmodified native DLL and tool executables can be replaced independently of Pantheon. Their licenses permit modification and debugging of such replacements; Pantheon does not restrict reverse engineering necessary to debug changes to LGPL components.

- libmpv matching source: [mpv commit 468d34c9b](https://github.com/mpv-player/mpv/tree/468d34c9b); its linked FFmpeg source: [FFmpeg commit 685ceebd4](https://github.com/FFmpeg/FFmpeg/tree/685ceebd4).
- Windows build machinery and dependency recipes: [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake). The binary embeds this build tree path; the package itself does not identify an exact build-recipe commit. The recorded configuration is provided in `Licenses/libmpv-build.txt`.
- Standalone FFmpeg matching source: [FFmpeg n8.1.1](https://github.com/FFmpeg/FFmpeg/tree/n8.1.1), [source archive](https://ffmpeg.org/releases/ffmpeg-8.1.1.tar.xz), and [Gyan's build information](https://www.gyan.dev/ffmpeg/builds/).
- Python, FFsubsync and dependency metadata are packaged under `SubtitleTools/Licenses`. `tools/Build-SubtitleTools.ps1` documents how the synchronization bundle is assembled.

The MIT grant for Pantheon's own code permits commercial use and closed-source derivatives, but does not remove GPL/LGPL requirements from bundled third-party binaries. For a different licensing profile, use compatible upstream binaries and comply with their licenses.

## Documentation images

The screenshots show **Swing Girls (2004)**, directed by Shinobu Yaguchi, playing inside Pantheon. Film imagery belongs to its respective rights holders and is not covered by the MIT license. The movie is not included in the source repository or Windows package.
