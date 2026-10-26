using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;

namespace VideoPlayer.Core;

public sealed record AudioSpeechEnvelope(
    float[] Activity,
    float[] Onset,
    int SpeechFrameCount,
    double FrameRateHz)
{
    public double DurationSeconds => FrameRateHz > 0 ? Activity.Length / FrameRateHz : 0.0;
}

public static class AudioSpeechAnalyzer
{
    public const int DefaultSampleRate = 8000;
    public const int FrameSamples = 160; // 20 ms at 8000 Hz -> 50 Hz frame rate
    public const int EnvelopeSampleRateHz = DefaultSampleRate / FrameSamples;
    public const double FrameRateHz = (double)DefaultSampleRate / FrameSamples;

    private const float SilenceRmsThreshold = 0.00035f;
    private const float SpeechProbabilityThreshold = 0.24f;

    public static string ResolveFfmpegExecutable(string? executableHint)
    {
        if (!string.IsNullOrWhiteSpace(executableHint))
        {
            try
            {
                var full = Path.GetFullPath(executableHint);
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir))
                {
                    var sibling = Path.Combine(dir, "ffmpeg.exe");
                    if (File.Exists(sibling))
                        return sibling;
                }
            }
            catch
            {
                // Ignore invalid path hints
            }
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "SubtitleTools", "ffmpeg.exe");
        if (File.Exists(bundled))
            return bundled;

        return "ffmpeg";
    }

    public static async Task<AudioSpeechEnvelope?> ExtractAndAnalyzeAsync(
        string videoPath,
        string referenceStream,
        string ffmpegExecutable,
        double maxDurationSeconds,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ffmpegExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        start.ArgumentList.Add("-hide_banner");
        start.ArgumentList.Add("-loglevel");
        start.ArgumentList.Add("error");
        start.ArgumentList.Add("-nostdin");
        if (double.IsFinite(maxDurationSeconds) && maxDurationSeconds > 30)
        {
            start.ArgumentList.Add("-t");
            start.ArgumentList.Add(Math.Ceiling(maxDurationSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(videoPath);
        start.ArgumentList.Add("-vn");
        start.ArgumentList.Add("-sn");
        start.ArgumentList.Add("-dn");
        start.ArgumentList.Add("-map");
        start.ArgumentList.Add(referenceStream);
        start.ArgumentList.Add("-ac");
        start.ArgumentList.Add("1");
        start.ArgumentList.Add("-ar");
        start.ArgumentList.Add(DefaultSampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add("s16le");
        start.ArgumentList.Add("-");

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return null;
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var state = new StreamingFrameAccumulator();
        var byteBuffer = new byte[FrameSamples * sizeof(short) * 64];
        var leftoverBytes = 0;

        try
        {
            var stream = process.StandardOutput.BaseStream;
            while (true)
            {
                var bytesRead = await stream.ReadAsync(
                    byteBuffer.AsMemory(leftoverBytes, byteBuffer.Length - leftoverBytes),
                    cancellationToken);
                if (bytesRead == 0) break;

                var totalBytes = leftoverBytes + bytesRead;
                var usableBytes = totalBytes & ~1;
                state.AddPcmBytes(byteBuffer.AsSpan(0, usableBytes));

                leftoverBytes = totalBytes - usableBytes;
                if (leftoverBytes == 1)
                    byteBuffer[0] = byteBuffer[usableBytes];
            }

            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }

        _ = await stderrTask;
        if (process.ExitCode != 0 && state.FrameCount < 50)
            return null;

        return state.BuildEnvelope();
    }

    public static AudioSpeechEnvelope AnalyzePcmSamples(ReadOnlySpan<short> samples)
    {
        var accumulator = new StreamingFrameAccumulator();
        accumulator.AddSamples(samples);
        return accumulator.BuildEnvelope();
    }

    private sealed class StreamingFrameAccumulator
    {
        private readonly List<float> _vocalRms = new(4096);
        private readonly List<float> _totalRms = new(4096);
        private readonly List<float> _zeroCrossing = new(4096);

        // 2nd-order Butterworth high-pass at ~210 Hz (fs = 8000 Hz)
        private float _hpX1, _hpX2, _hpY1, _hpY2;
        // 2nd-order Butterworth low-pass at ~3100 Hz (fs = 8000 Hz)
        private float _lpX1, _lpX2, _lpY1, _lpY2;

        private int _sampleInFrame;
        private float _frameVocalSumSq;
        private float _frameTotalSumSq;
        private int _frameZeroCrossings;
        private float _prevVocalSample;

        public int FrameCount => _vocalRms.Count;

        public void AddPcmBytes(ReadOnlySpan<byte> pcmBytes)
        {
            for (var offset = 0; offset + 1 < pcmBytes.Length; offset += 2)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(pcmBytes.Slice(offset, 2));
                ProcessSample(sample * (1.0f / 32768.0f));
            }
        }

        public void AddSamples(ReadOnlySpan<short> samples)
        {
            for (var i = 0; i < samples.Length; i++)
                ProcessSample(samples[i] * (1.0f / 32768.0f));
        }

        private void ProcessSample(float x)
        {
            // High-pass filter (fc = 210 Hz, fs = 8000 Hz): b = [0.8903, -1.7806, 0.8903], a = [1, -1.7688, 0.7924]
            var hp = 0.8903f * x - 1.7806f * _hpX1 + 0.8903f * _hpX2 + 1.7688f * _hpY1 - 0.7924f * _hpY2;
            _hpX2 = _hpX1;
            _hpX1 = x;
            _hpY2 = _hpY1;
            _hpY1 = hp;

            // Low-pass filter (fc = 3100 Hz, fs = 8000 Hz): b = [0.5914, 1.1828, 0.5914], a = [1, 0.9969, 0.3687]
            var vocal = 0.5914f * hp + 1.1828f * _lpX1 + 0.5914f * _lpX2 - 0.9969f * _lpY1 - 0.3687f * _lpY2;
            _lpX2 = _lpX1;
            _lpX1 = hp;
            _lpY2 = _lpY1;
            _lpY1 = vocal;

            _frameTotalSumSq += x * x;
            _frameVocalSumSq += vocal * vocal;
            if ((vocal >= 0 && _prevVocalSample < 0) || (vocal < 0 && _prevVocalSample >= 0))
                _frameZeroCrossings++;
            _prevVocalSample = vocal;

            _sampleInFrame++;
            if (_sampleInFrame >= FrameSamples)
            {
                _vocalRms.Add(MathF.Sqrt(_frameVocalSumSq / FrameSamples));
                _totalRms.Add(MathF.Sqrt(_frameTotalSumSq / FrameSamples));
                _zeroCrossing.Add((float)_frameZeroCrossings / FrameSamples);
                _sampleInFrame = 0;
                _frameVocalSumSq = 0;
                _frameTotalSumSq = 0;
                _frameZeroCrossings = 0;
            }
        }

        public AudioSpeechEnvelope BuildEnvelope()
        {
            var count = _vocalRms.Count;
            if (count == 0)
                return new AudioSpeechEnvelope([], [], 0, FrameRateHz);

            var maxVocal = 0f;
            for (var i = 0; i < count; i++)
                if (_vocalRms[i] > maxVocal) maxVocal = _vocalRms[i];

            if (maxVocal < SilenceRmsThreshold)
                return new AudioSpeechEnvelope(new float[count], new float[count], 0, FrameRateHz);

            var db = new float[count];
            var vocalRatio = new float[count];
            for (var i = 0; i < count; i++)
            {
                db[i] = 20.0f * MathF.Log10(_vocalRms[i] + 1e-7f);
                var ratio = _vocalRms[i] / (_totalRms[i] + 1e-6f);
                vocalRatio[i] = Math.Clamp(ratio, 0.1f, 1.2f);
            }

            var speechProb = ComputeSpeechProbabilities(db, vocalRatio, _zeroCrossing);
            ApplyMorphologicalCleanup(speechProb);

            var activity = new float[count];
            var onset = new float[count];
            var speechFrames = 0;

            for (var i = 0; i < count; i++)
            {
                var p = speechProb[i];
                if (p >= SpeechProbabilityThreshold)
                {
                    speechFrames++;
                    activity[i] = 0.35f + 0.65f * p;
                }
                else
                {
                    activity[i] = -0.25f * (1.0f - p / SpeechProbabilityThreshold);
                }
            }

            // Compute smoothed positive onset derivative
            for (var i = 2; i < count; i++)
            {
                var delta = speechProb[i] - speechProb[i - 2];
                if (delta > 0.08f)
                    onset[i] = delta;
            }

            SmoothInPlace(onset, radius: 3);
            NormalizeZeroMean(activity);
            return new AudioSpeechEnvelope(activity, onset, speechFrames, FrameRateHz);
        }

        private static float[] ComputeSpeechProbabilities(
            float[] db, float[] vocalRatio, List<float> zeroCrossing)
        {
            var count = db.Length;
            var probabilities = new float[count];
            const int BlockSize = 250; // 5-second blocks (250 frames at 50 Hz)
            var numBlocks = (count + BlockSize - 1) / BlockSize;
            var blockNoise = new float[numBlocks];
            var blockPeak = new float[numBlocks];
            var scratch = new float[BlockSize];

            var globalMaxDb = -120f;
            for (var i = 0; i < count; i++)
                if (db[i] > globalMaxDb) globalMaxDb = db[i];

            for (var b = 0; b < numBlocks; b++)
            {
                var start = Math.Max(0, b * BlockSize - BlockSize);
                var end = Math.Min(count, (b + 2) * BlockSize);
                var len = end - start;
                var sampleStep = Math.Max(1, len / BlockSize);
                var n = 0;
                for (var i = start; i < end && n < scratch.Length; i += sampleStep)
                    scratch[n++] = db[i];
                Array.Sort(scratch, 0, n);
                blockNoise[b] = Math.Max(globalMaxDb - 48.0f, scratch[(int)(n * 0.15)]);
                blockPeak[b] = Math.Max(blockNoise[b] + 9.0f, scratch[(int)(n * 0.92)]);
            }

            for (var i = 0; i < count; i++)
            {
                var b = Math.Min(numBlocks - 1, i / BlockSize);
                var noiseFloor = blockNoise[b];
                var speechCeil = blockPeak[b];
                var snr = (db[i] - (noiseFloor + 3.2f)) / Math.Max(6.0f, speechCeil - noiseFloor - 3.2f);
                if (snr <= 0)
                {
                    probabilities[i] = 0;
                    continue;
                }

                var zcr = zeroCrossing[i];
                // Normal speech ZCR at 8 kHz is roughly 0.04..0.48
                var zcrWeight = zcr is >= 0.02f and <= 0.55f ? 1.0f : 0.65f;
                var formantWeight = Math.Clamp((vocalRatio[i] - 0.25f) / 0.45f, 0.35f, 1.15f);
                probabilities[i] = Math.Clamp(snr * zcrWeight * formantWeight, 0f, 1f);
            }

            AttenuateUnmodulatedDrones(probabilities);
            return probabilities;
        }

        private static void AttenuateUnmodulatedDrones(float[] probabilities)
        {
            var count = probabilities.Length;
            if (count < 150) return;

            // Human speech modulates at 3-8 Hz (syllables every 125-330ms), whereas music pads/hum stay flat.
            const int Window = 100; // 2.0 seconds
            for (var start = 0; start + Window <= count; start += Window / 2)
            {
                var activeCount = 0;
                var diffSum = 0f;
                for (var i = start + 1; i < start + Window; i++)
                {
                    if (probabilities[i] > 0.35f) activeCount++;
                    diffSum += MathF.Abs(probabilities[i] - probabilities[i - 1]);
                }

                var avgDiff = diffSum / Window;
                if (activeCount > Window * 0.92 && avgDiff < 0.025f)
                {
                    for (var i = start; i < start + Window; i++)
                        probabilities[i] *= 0.45f;
                }
            }
        }

        private static void ApplyMorphologicalCleanup(float[] probabilities)
        {
            var count = probabilities.Length;
            // Bridge short intra-word consonant stops (<= 4 frames = 80 ms)
            for (var i = 1; i + 4 < count; i++)
            {
                if (probabilities[i - 1] < SpeechProbabilityThreshold) continue;
                for (var gap = 1; gap <= 4; gap++)
                {
                    if (probabilities[i + gap] >= SpeechProbabilityThreshold)
                    {
                        var fill = 0.5f * (probabilities[i - 1] + probabilities[i + gap]);
                        for (var k = 0; k < gap; k++)
                            probabilities[i + k] = Math.Max(probabilities[i + k], fill);
                        break;
                    }
                }
            }

            // Suppress isolated 1-frame impulse clicks
            for (var i = 1; i + 1 < count; i++)
            {
                if (probabilities[i - 1] < 0.1f && probabilities[i + 1] < 0.1f && probabilities[i] > 0.3f)
                    probabilities[i] *= 0.25f;
            }
        }

        private static void SmoothInPlace(float[] values, int radius)
        {
            var count = values.Length;
            if (count == 0 || radius <= 0) return;
            var copy = (float[])values.Clone();
            for (var i = 0; i < count; i++)
            {
                var sum = 0f;
                var weightSum = 0f;
                var start = Math.Max(0, i - radius);
                var end = Math.Min(count - 1, i + radius);
                for (var j = start; j <= end; j++)
                {
                    var w = (radius + 1) - Math.Abs(i - j);
                    sum += copy[j] * w;
                    weightSum += w;
                }
                values[i] = sum / weightSum;
            }
        }

        private static void NormalizeZeroMean(float[] values)
        {
            if (values.Length == 0) return;
            double sum = 0;
            for (var i = 0; i < values.Length; i++) sum += values[i];
            var mean = (float)(sum / values.Length);
            double sqSum = 0;
            for (var i = 0; i < values.Length; i++)
            {
                values[i] -= mean;
                sqSum += (double)values[i] * values[i];
            }
            var rms = (float)Math.Sqrt(sqSum / values.Length);
            if (rms > 1e-5f)
            {
                var inv = 1.0f / rms;
                for (var i = 0; i < values.Length; i++) values[i] *= inv;
            }
        }
    }
}
