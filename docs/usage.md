# Pantheon

A minimalist Windows video player built with .NET 10, Avalonia 12 and libmpv 0.41.

## Run

Use the ready-to-run x64 build in `dist\Pantheon\Pantheon.exe`, or launch from source:

```powershell
dotnet run --project .\src\VideoPlayer\VideoPlayer.csproj
```

The published x64 app is self-contained and does not need a separate .NET runtime installation.

## File Explorer icons

Pantheon includes separate dark, red-accented icons for each supported video extension. After publishing, register Pantheon with Windows:

```powershell
powershell -ExecutionPolicy Bypass -File .\dist\Pantheon\Register-PantheonFileAssociations.ps1
```

This adds Pantheon and its format-specific icons to the Windows app list without changing your current default players. Choose Pantheon under **Settings > Apps > Default apps** for the extensions whose icons and double-click behavior you want to change.

## Controls

- Press `O` to open a video, or drag a video onto the window
- Drag a video onto the window to open it
- Videos in the opened video's folder are automatically queued by filename; move the pointer to the right edge to view and reorder them
- Use **Queue** to pin the queue panel. Click a queued video to play it immediately; the other queued videos keep their order. Drag queued videos to reorder them, or press × on a row to remove it.
- Reopening a video resumes its last saved position and subtitle delay
- Drag `.srt`, `.ass`, `.ssa`, `.vtt`, `.sub`, or `.idx` onto the open video to attach it
- A subtitle with the same filename as the video is loaded automatically from that video’s folder
- **Space**: play/pause; **Left / Right**: seek 5 seconds; **numpad + / −**: adjust playback speed
- **Enter**: fullscreen; **C**: toggle subtitles; **M**: mute/unmute
- **Comma / Period**: step one frame backward/forward; frame stepping pauses playback
- **Z / X**: adjust subtitle delay by 0.1 seconds earlier/later; **Ctrl+Shift+Backspace**: reset it
- **Tab**: open/close settings; **"** (the key below Esc) or **S**: save a source-frame PNG without subtitles
- **Mouse**: left click video to play/pause; right click the app window to save a frame; middle click video to toggle fullscreen; wheel in the center of the video to seek 5 seconds; **Alt + wheel** to adjust volume
- Assign multiple keyboard shortcuts to an action in Settings > Keyboard. Mouse defaults and region bindings are editable in Settings > Mouse.
- Use the volume slider to adjust playback volume
- Drag the timeline to seek continuously. Playback pauses during the gesture and resumes on release if it was playing. While holding the timeline, pull upward to reveal a frame preview above the bar; move left or right to choose another frame. Preview images are temporary and are removed after use.

Use **Settings** to customize appearance, playback, keyboard shortcuts, mouse zones, and screenshots. In Settings > Screenshots, choose **Original (PNG, lossless)** or **Optimized (high-quality JPEG)**. Original is selected by default. Screenshots are saved in a subfolder named after the video inside the selected folder; by default, that folder is `Pictures\Pantheon`. Mouse actions can be mapped separately for up to three vertical or horizontal video regions. The top and bottom control bars can hide until the pointer reaches a window edge; toggle this in Appearance or by right-clicking either bar. Settings are saved under `%APPDATA%\LumaPlayer\settings.json` to preserve existing user preferences.

Playback positions, subtitle selections and per-video subtitle delays are saved under `%APPDATA%\LumaPlayer\playback.json`.

## Automatic subtitles

For incorrect Turkish characters, choose **Settings > Subtitles > Subtitle character encoding > Turkish (Windows-1254)**. Encoding changes reload external subtitles. A synchronized copy with incorrectly decoded text can be repaired using its original subtitle while preserving its synchronized timestamps and manual delay.

Settings > **Subtitles** controls automatic internet discovery, synchronization and the search language (English by default). Opening a video searches the OpenSubtitles repository and downloads a matching subtitle. Local files and matching embedded tracks do not suppress the online search. A subtitle previously downloaded for the same video/language can be reused from the application's download cache, preserving its saved manual delay. An explicitly saved subtitle-off choice is respected. Online matching prefers the video's OpenSubtitles hash, then checks the title, year and episode metadata. Episode matching understands `S02E03` and `2x03`; another season or episode is rejected. Ambiguous multi-episode files and multi-file download packages are left for manual selection.

Create an [OpenSubtitles account and API key](https://opensubtitles.tawk.help/article/getting-started), then enter the key in Settings > **Subtitles**. No account password is stored. The key is stored in this computer's `settings.json`; alternatively, set `PANTHEON_OPENSUBTITLES_API_KEY` in the environment. Downloads are subject to the provider's quota. Errors and missing configuration appear in the CC popup and its tooltip, without interrupting playback. Download requests do not send the API key to the file host.

External SRT/ASS/SSA/VTT subtitles are synchronized using [FFsubsync](https://github.com/smacke/ffsubsync): an embedded text subtitle track is used as the timing reference when available, otherwise the selected audio track is. It corrects a global time offset and common framerate differences. The original file is preserved; the aligned UTF-8 SRT copy is cached under `%APPDATA%\LumaPlayer\Subtitles`. Silence, failed alignment and extreme offset/scale estimates are rejected. A different edit with inserted/deleted scenes may still need another subtitle release or manual correction; this is not a transcription system. Embedded and bitmap subtitle timing is preserved, and converting styled ASS/SSA to SRT does not retain its original layout.

Use the CC popup's **Find subtitles online** button to run a fresh repository search and download, including when a local/embedded subtitle is selected. **Synchronize automatically** aligns the selected external subtitle, and **Add subtitle file** lets you explicitly attach your own file. Changing videos, audio/subtitle selection, visibility, or manual subtitle timing cancels pending work. Reopening a previously downloaded subtitle preserves manual timing; a manual synchronization request can replace that adjustment. Changing audio tracks uses the original external subtitle for a new alignment, avoiding repeated shifts of the already aligned copy.

The ready-to-run distribution includes `SubtitleTools`; end users do not need Python or FFmpeg installed separately. To prepare the same portable tools when building from source, install Python and FFmpeg (including `ffprobe`), then run:

```powershell
.\tools\Build-SubtitleTools.ps1
```

The script builds a private FFsubsync 0.4.31 runtime under `artifacts`; the next build/publish copies it beside Pantheon. Use `-Python <python.exe>` or `-FfmpegDirectory <bin-directory>` for custom installations. An existing FFsubsync executable can also be selected in settings. A source build without these tools can still find/download subtitles, and explains the missing synchronization setup in the CC popup.

Application errors are logged daily under `%LOCALAPPDATA%\Pantheon\Logs`. Open the **Diagnostics** category in Settings to open the log folder.

## Build and tests

```powershell
dotnet test .\Pantheon.slnx
dotnet publish .\src\VideoPlayer\VideoPlayer.csproj -c Release -r win-x64 --self-contained true -o .\dist\Pantheon
```

Real speech synchronization tests require `Build-SubtitleTools.ps1`; they are explicitly skipped if the bundle is absent. The fixture contains synthesized speech on the second audio stream and silence on the first. Tests measure positive/negative offsets, framerate correction, subtitle playback, cache reuse, original-file preservation and silence rejection. Provider tests use controlled HTTP responses, so they do not consume a download quota; testing a live OpenSubtitles download additionally requires a valid API key.

To verify the actual Avalonia player flow on Windows with an isolated profile:

```powershell
dotnet run --project .\tools\SubtitleAutomationCheck -- .\tests\VideoPlayer.Tests\TestData .\artifacts\subtitle-ui-check
```

This checks automatic repository search/download for both films and episodes through controlled HTTP responses, including when a local subtitle is already loaded or a matching Turkish embedded track exists. It also checks real speech synchronization, reopening/manual-delay persistence, download cache reuse, continued playback, and cancellation during manual timing changes, episode switching and audio switching. The cancellation checks use a deliberately slow local worker. These controlled repository checks do not claim a live authenticated OpenSubtitles download.

The check also routes pointer presses, movement and scrolling through the subtitle popup and verifies that clicking the main window still dismisses it. Append a video path and an external subtitle path to additionally verify synchronization of those files with an isolated profile; the original files are preserved.

The screenshot command saves the video at its original resolution without subtitles or player UI. Screenshots use the embedded GPU renderer's HDR tone mapping, so HDR videos produce standard 8-bit SDR images with the same colors as playback. Original mode writes PNG; optimized mode writes high-quality JPEG with minimal visible loss.

To verify HDR screenshots with the actual Avalonia/OpenGL renderer on Windows, use an HDR video longer than 61 seconds:

```powershell
dotnet run --project .\tools\HdrScreenshotCheck -- "C:\Videos\hdr.mkv" .\artifacts\hdr-screenshots
```

This checks source resolution, sRGB PNG output at up to three positions, subtitle exclusion, captures during paused and active playback, and stop/reload/resize behavior. It saves the captures and uses an isolated settings profile. Run it on the Windows desktop; ordinary libmpv playback tests do not exercise the embedded renderer.
