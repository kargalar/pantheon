using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
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
        string videoPath,
        string subtitlePath,
        string executable,
        string referenceStream,
        string encoding,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(subtitlePath).ToLowerInvariant();
        if (extension is not (".srt" or ".ass" or ".ssa" or ".vtt" or ".sub"))
            throw new NotSupportedException(UiText.UnsupportedSubtitleSyncFormat);

        var subtitleBytes = await ReadAllBytesSharedAsync(subtitlePath, cancellationToken);
        var subtitleText = SubtitleTextEncoding.DecodeSubtitleText(subtitleBytes, encoding, out var resolvedEncoding);
        var outputPath = await cache.SynchronizationPathAsync(videoPath, subtitlePath, referenceStream, resolvedEncoding, cancellationToken);
        if (SubtitleFileCache.IsValidSrt(outputPath))
            return new(outputPath, 0, 1, true);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var workDirectory = Path.Combine(Path.GetDirectoryName(outputPath)!, $"work-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        var temporaryOutput = Path.Combine(workDirectory, "aligned.srt");

        try
        {
            if (IsCustomExternalSynchronizerExecutable(executable))
            {
                return await SynchronizeWithExternalProcessAsync(
                    videoPath,
                    subtitlePath,
                    executable,
                    referenceStream,
                    resolvedEncoding,
                    workDirectory,
                    temporaryOutput,
                    outputPath,
                    cancellationToken);
            }

            return await SynchronizeNativeAsync(
                videoPath,
                subtitlePath,
                subtitleText,
                extension,
                executable,
                referenceStream,
                workDirectory,
                temporaryOutput,
                outputPath,
                cancellationToken);
        }
        finally
        {
            if (Directory.Exists(workDirectory))
            {
                try { Directory.Delete(workDirectory, recursive: true); }
                catch { /* Ignore cleanup races */ }
            }
        }
    }

    private static async Task<SubtitleSynchronizationResult> SynchronizeNativeAsync(
        string videoPath,
        string subtitlePath,
        string subtitleText,
        string extension,
        string executableHint,
        string referenceStream,
        string workDirectory,
        string temporaryOutput,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var targetCues = SubtitleParser.Parse(subtitleText, extension);
        if (targetCues.Count == 0)
            throw new InvalidOperationException(UiText.UnreliableSubtitleSyncResult);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SynchronizationTimeout);

        var ffmpegExecutable = AudioSpeechAnalyzer.ResolveFfmpegExecutable(executableHint);
        var diagnosticsBuilder = new StringBuilder();
        SubtitleAlignmentOutcome? outcome = null;

        try
        {
            if (referenceStream.StartsWith("0:s:", StringComparison.Ordinal))
            {
                var embeddedReferencePath = Path.Combine(workDirectory, "reference.srt");
                if (await TryExtractEmbeddedSubtitleAsync(videoPath, referenceStream, ffmpegExecutable, embeddedReferencePath, timeout.Token))
                {
                    try
                    {
                        var refBytes = await ReadAllBytesSharedAsync(embeddedReferencePath, timeout.Token);
                        var refText = SubtitleTextEncoding.DecodeSubtitleText(refBytes, "utf-8", out _);
                        var refCues = SubtitleParser.Parse(refText, ".srt");
                        var refDialogue = SubtitleParser.FilterDialogueCues(refCues);

                        if (refDialogue.Count >= 12)
                        {
                            outcome = SubtitleTimelineAligner.AlignToReferenceSubtitles(refCues, targetCues);
                        }
                        else
                        {
                            diagnosticsBuilder.AppendLine($"Embedded subtitle {referenceStream} had only {refDialogue.Count} dialogue cues; falling back to audio.");
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        diagnosticsBuilder.AppendLine($"Embedded subtitle {referenceStream} alignment fallback: {ex.Message}");
                    }
                }
                else
                {
                    diagnosticsBuilder.AppendLine($"Could not extract embedded subtitle {referenceStream}; falling back to audio.");
                }
            }

            if (outcome is null)
            {
                var audioStream = referenceStream.StartsWith("0:a:", StringComparison.Ordinal)
                    ? referenceStream
                    : "0:a:0";

                var maxCueEnd = targetCues.Max(static cue => cue.EndSeconds);
                var envelope = await AudioSpeechAnalyzer.ExtractAndAnalyzeAsync(
                    videoPath,
                    audioStream,
                    ffmpegExecutable,
                    maxCueEnd * 1.15 + MaximumOffsetSeconds + 60.0,
                    timeout.Token)
                    ?? throw new InvalidOperationException(UiText.UnreliableSubtitleSyncResult);

                outcome = SubtitleTimelineAligner.AlignToAudioEnvelope(envelope, targetCues);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException(UiText.SubtitleSynchronizationTimedOut);
        }
        catch (Exception ex) when (ex is InvalidOperationException && ex.Message == UiText.UnreliableSubtitleSyncResult)
        {
            string details = diagnosticsBuilder.ToString().Trim();
            throw string.IsNullOrEmpty(details)
                ? ex
                : new InvalidOperationException(UiText.UnreliableSubtitleSyncResult, new InvalidOperationException(details));
        }

        var formattedSrt = SubtitleParser.FormatSrt(outcome.AlignedCues);
        if (!SubtitleFileCache.IsValidSrtContent(formattedSrt))
            throw new InvalidOperationException(UiText.UnreliableSubtitleSyncResult);

        await File.WriteAllTextAsync(temporaryOutput, formattedSrt, new UTF8Encoding(false), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await SubtitleFileCache.SaveSynchronizationSourceAsync(temporaryOutput, subtitlePath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(temporaryOutput + ".source.json", outputPath + ".source.json", overwrite: true);
        File.Move(temporaryOutput, outputPath, overwrite: true);

        return new(outputPath, outcome.OffsetSeconds, outcome.ScaleFactor, false);
    }

    private static bool IsCustomExternalSynchronizerExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return false;

        var fileName = Path.GetFileNameWithoutExtension(executable);
        if (string.Equals(fileName, "ffsubsync", StringComparison.OrdinalIgnoreCase))
            return false;

        return File.Exists(executable);
    }

    private static async Task<SubtitleSynchronizationResult> SynchronizeWithExternalProcessAsync(
        string videoPath,
        string subtitlePath,
        string executable,
        string referenceStream,
        string encoding,
        string workDirectory,
        string temporaryOutput,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var referencePath = videoPath;
        var embeddedReference = Path.Combine(workDirectory, "reference.srt");
        var ffmpegExecutable = AudioSpeechAnalyzer.ResolveFfmpegExecutable(executable);
        var useStreamReference = !referenceStream.StartsWith("0:s:", StringComparison.Ordinal)
            || !await TryExtractEmbeddedSubtitleAsync(videoPath, referenceStream, ffmpegExecutable, embeddedReference, cancellationToken);
        if (!useStreamReference)
            referencePath = embeddedReference;

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
        if (!string.IsNullOrEmpty(ffmpegDirectory) && File.Exists(Path.Combine(ffmpegDirectory, "ffmpeg.exe")))
        {
            start.ArgumentList.Add("--ffmpeg-path");
            start.ArgumentList.Add(ffmpegDirectory);
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

    private static async Task<bool> TryExtractEmbeddedSubtitleAsync(
        string videoPath,
        string referenceStream,
        string ffmpegExecutable,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ffmpegExecutable)
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

    private static async Task<byte[]> ReadAllBytesSharedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[stream.Length];
        await stream.ReadExactlyAsync(buffer, cancellationToken);
        return buffer;
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
