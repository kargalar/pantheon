using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoPlayer.Core;

public sealed record SubtitleSynchronizationResult(string Path, double OffsetSeconds, double ScaleFactor, bool FromCache);

public sealed partial class SubtitleSynchronizer(SubtitleFileCache cache)
{
    private const double MaximumOffsetSeconds = 60;
    private const double MaximumScaleDeviation = 0.10;
    private const double MinimumSpeechFrames = 100;
    private const int MaximumDiagnosticCharacters = 6000;
    private static readonly TimeSpan SynchronizationTimeout = TimeSpan.FromMinutes(10);

    public async Task<SubtitleSynchronizationResult> SynchronizeAsync(
        string videoPath, string subtitlePath, string executable, string referenceStream, string encoding,
        CancellationToken cancellationToken)
    {
        if (Path.GetExtension(subtitlePath).ToLowerInvariant() is not (".srt" or ".ass" or ".ssa" or ".vtt"))
            throw new NotSupportedException(UiText.UnsupportedSubtitleSyncFormat);
        encoding = SubtitleTextEncoding.ResolveSynchronizationEncoding(
            await File.ReadAllBytesAsync(subtitlePath, cancellationToken), encoding);
        var outputPath = await cache.SynchronizationPathAsync(videoPath, subtitlePath, referenceStream, encoding, cancellationToken);
        if (SubtitleFileCache.IsValidSrt(outputPath)) return new(outputPath, 0, 1, true);

        var workDirectory = Path.Combine(Path.GetDirectoryName(outputPath)!, $"work-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        var temporaryOutput = Path.Combine(workDirectory, "aligned.srt");
        try
        {
            var referencePath = videoPath;
            var embeddedReference = Path.Combine(workDirectory, "reference.srt");
            var useStreamReference = !referenceStream.StartsWith("0:s:", StringComparison.Ordinal)
                || !await TryExtractEmbeddedSubtitleAsync(videoPath, referenceStream, executable, embeddedReference, cancellationToken);
            if (!useStreamReference) referencePath = embeddedReference;

            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDirectory
            };
            start.Environment["NO_COLOR"] = "1";
            start.Environment["COLUMNS"] = "1000";
            foreach (var argument in new[]
            {
                referencePath, "-i", subtitlePath, "-o", temporaryOutput,
                "--encoding", encoding, "--output-encoding", "utf-8",
                "--max-offset-seconds", MaximumOffsetSeconds.ToString(CultureInfo.InvariantCulture),
                "--log-dir-path", workDirectory
            }) start.ArgumentList.Add(argument);
            if (useStreamReference)
            {
                start.ArgumentList.Add("--vad");
                start.ArgumentList.Add("subs_then_webrtc");
                start.ArgumentList.Add("--reference-stream");
                start.ArgumentList.Add(referenceStream);
            }
            var ffmpegDirectory = Path.GetDirectoryName(Path.GetFullPath(executable));
            if (File.Exists(Path.Combine(ffmpegDirectory!, "ffmpeg.exe")))
            {
                start.ArgumentList.Add("--ffmpeg-path");
                start.ArgumentList.Add(ffmpegDirectory!);
            }
            using var process = new Process { StartInfo = start };
            try { process.Start(); }
            catch (Win32Exception exception)
            {
                throw new InvalidOperationException(UiText.SubtitleSynchronizerMissing, exception);
            }
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SynchronizationTimeout);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdout, stderr);
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException(UiText.SubtitleSynchronizationTimedOut);
            }
            await Task.WhenAll(stdout, stderr);
            var logPath = Path.Combine(workDirectory, "ffsubsync.log");
            var log = (File.Exists(logPath) ? await File.ReadAllTextAsync(logPath, cancellationToken) : string.Empty)
                + "\n" + await stderr;
            var alignment = ParseAlignment(log);
            if (process.ExitCode != 0 || alignment is null || !SubtitleFileCache.IsValidSrt(temporaryOutput))
                throw new InvalidOperationException(UiText.UnreliableSubtitleSyncResult,
                    new InvalidOperationException(log.Length > MaximumDiagnosticCharacters ? log[^MaximumDiagnosticCharacters..] : log));
            cancellationToken.ThrowIfCancellationRequested();
            await SubtitleFileCache.SaveSynchronizationSourceAsync(temporaryOutput, subtitlePath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryOutput + ".source.json", outputPath + ".source.json", overwrite: true);
            File.Move(temporaryOutput, outputPath, overwrite: true);
            return new(outputPath, alignment.Value.Offset, alignment.Value.Scale, false);
        }
        finally
        {
            if (Directory.Exists(workDirectory)) Directory.Delete(workDirectory, recursive: true);
        }
    }

    private static async Task<bool> TryExtractEmbeddedSubtitleAsync(
        string videoPath, string referenceStream, string executable, string outputPath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(executable));
        var bundled = Path.Combine(directory!, "ffmpeg.exe");
        var ffmpeg = File.Exists(bundled) ? bundled : "ffmpeg";
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-i", videoPath,
            "-map", referenceStream, "-f", "srt", "-y", outputPath
        }) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception) { return false; }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await Task.WhenAll(stdout, stderr);
            throw;
        }
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode == 0 && SubtitleFileCache.IsValidSrt(outputPath);
    }

    public static (double Offset, double Scale)? ParseAlignment(string log)
    {
        var score = ReadNumber(log, "score");
        var offset = ReadNumber(log, "offset seconds");
        var scale = ReadNumber(log, "framerate scale factor");
        var speech = ReadNumber(log, "total of speech segments");
        if (score is null or <= 0 || offset is null || scale is null || speech is null or < MinimumSpeechFrames
            || Math.Abs(offset.Value) >= MaximumOffsetSeconds
            || Math.Abs(scale.Value - 1) > MaximumScaleDeviation
            || log.Contains("failed to sync", StringComparison.OrdinalIgnoreCase)) return null;
        return (offset.Value, scale.Value);
    }

    private static double? ReadNumber(string log, string label)
    {
        var match = Regex.Match(log, Regex.Escape(label) + @":\s*([-+]?\d+(?:\.\d+)?)",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    }
}
