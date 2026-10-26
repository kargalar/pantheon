using System.Numerics;

namespace VideoPlayer.Core;

public sealed record SubtitleAlignmentOutcome(
    double OffsetSeconds,
    double ScaleFactor,
    double ConfidenceScore,
    int SpeechFrameCount,
    int SegmentCount,
    IReadOnlyList<SubtitleCue> AlignedCues,
    string Diagnostics);

public static class SubtitleTimelineAligner
{
    public const int EnvelopeSampleRateHz = AudioSpeechAnalyzer.EnvelopeSampleRateHz;
    public const double MaximumAbsOffsetSeconds = 60.0;
    public const double MinimumScaleFactor = 0.90;
    public const double MaximumScaleFactor = 1.10;

    private static readonly double[] StandardScaleCandidates =
    [
        1.0,
        1000.0 / 1001.0, // 23.976 / 24.0
        1001.0 / 1000.0, // 24.0 / 23.976
        24.0 / 25.0,     // 0.96 (PAL 25fps -> 24fps)
        25.0 / 24.0,     // 1.0416667 (24fps -> PAL 25fps)
        24000.0 / 25025.0, // 23.976 -> 25.0
        25025.0 / 24000.0, // 25.0 -> 23.976
        24.0 / 30.0,
        30.0 / 24.0,
        25.0 / 30.0,
        30.0 / 25.0
    ];

    public static SubtitleAlignmentOutcome AlignToReferenceSubtitles(
        IReadOnlyList<SubtitleCue> referenceCues,
        IReadOnlyList<SubtitleCue> targetCues)
    {
        var refDialogue = SubtitleParser.FilterDialogueCues(referenceCues);
        var targetDialogue = SubtitleParser.FilterDialogueCues(targetCues);

        if (refDialogue.Count < 5 || targetDialogue.Count < 5)
            throw new InvalidOperationException(UiText.UnreliableSubtitleSyncResult);

        double maxRefEnd = refDialogue.Max(static cue => cue.EndSeconds);
        double maxTargetEnd = targetDialogue.Max(static cue => cue.EndSeconds);
        double timelineSeconds = Math.Max(maxRefEnd, maxTargetEnd) + MaximumAbsOffsetSeconds + 30.0;
        int frameCount = Math.Max(512, (int)Math.Ceiling(timelineSeconds * EnvelopeSampleRateHz));

        BuildCueEnvelope(refDialogue, scaleFactor: 1.0, frameCount, out float[] refActivity, out float[] refOnset, out int refActiveFrames);
        if (refActiveFrames < 40)
            throw new InvalidOperationException(UiText.UnreliableSubtitleSyncResult);

        var globalFit = FindBestGlobalModel(
            refActivity,
            refOnset,
            targetDialogue,
            frameCount,
            allowFineScaleGrid: true);

        if (!IsReliableGlobalFit(globalFit, refActiveFrames))
            throw new InvalidOperationException(UiText.UnreliableSubtitleSyncResult);

        var refinedGlobal = RefineSubtitleToSubtitleModel(refDialogue, targetDialogue, globalFit.ScaleFactor, globalFit.OffsetSeconds);
        double bestScale = refinedGlobal.ScaleFactor;
        double bestOffset = refinedGlobal.OffsetSeconds;

        int segmentCount = 1;
        IReadOnlyList<SubtitleCue> alignedCues;

        bool canTryPiecewise = Math.Abs(bestScale - 1.0) <= 0.0025
            && maxRefEnd >= 180.0
            && targetDialogue.Count >= 40
            && refDialogue.Count >= 40;

        if (canTryPiecewise
            && TryAlignPiecewiseToSubtitles(
                refActivity,
                refOnset,
                refDialogue,
                targetCues,
                targetDialogue,
                bestScale,
                bestOffset,
                out var piecewiseCues,
                out int detectedSegments,
                out double representativeOffset))
        {
            alignedCues = piecewiseCues;
            segmentCount = detectedSegments;
            bestOffset = representativeOffset;
        }
        else
        {
            alignedCues = ApplyLinearTransform(targetCues, bestScale, bestOffset);
            alignedCues = SnapCuesToReferenceSubtitles(refDialogue, alignedCues);
        }

        string diagnostics = FormattableString.Invariant(
            $"mode=sub2sub scale={bestScale:F6} offset={bestOffset:F3}s z={globalFit.ZScore:F2} corr={globalFit.Correlation:F4} segments={segmentCount} speechFrames={refActiveFrames}");

        return new SubtitleAlignmentOutcome(
            bestOffset,
            bestScale,
            globalFit.ZScore,
            refActiveFrames,
            segmentCount,
            alignedCues,
            diagnostics);
    }

    public static SubtitleAlignmentOutcome AlignToAudioEnvelope(
        AudioSpeechEnvelope audioEnvelope,
        IReadOnlyList<SubtitleCue> targetCues)
    {
        var targetDialogue = SubtitleParser.FilterDialogueCues(targetCues);
        if (targetDialogue.Count < 5 || audioEnvelope.SpeechFrameCount < 100)
            throw new InvalidOperationException(UiText.UnreliableSubtitleSyncResult);

        double maxTargetEnd = targetDialogue.Max(static cue => cue.EndSeconds);
        int neededFrames = (int)Math.Ceiling((maxTargetEnd + MaximumAbsOffsetSeconds + 30.0) * EnvelopeSampleRateHz);
        int frameCount = Math.Max(audioEnvelope.Activity.Length, Math.Max(512, neededFrames));

        float[] refActivity = PadArray(audioEnvelope.Activity, frameCount);
        float[] refOnset = PadArray(audioEnvelope.Onset, frameCount);

        var globalFit = FindBestGlobalModel(
            refActivity,
            refOnset,
            targetDialogue,
            frameCount,
            allowFineScaleGrid: true);

        if (!IsReliableGlobalFit(globalFit, audioEnvelope.SpeechFrameCount))
            throw new InvalidOperationException(UiText.UnreliableSubtitleSyncResult);

        double bestScale = globalFit.ScaleFactor;
        double bestOffset = globalFit.OffsetSeconds;
        int segmentCount = 1;
        IReadOnlyList<SubtitleCue> alignedCues;

        bool canTryPiecewise = Math.Abs(bestScale - 1.0) <= 0.0025
            && audioEnvelope.DurationSeconds >= 240.0
            && targetDialogue.Count >= 50;

        if (canTryPiecewise
            && TryAlignPiecewiseToEnvelope(
                refActivity,
                refOnset,
                targetCues,
                targetDialogue,
                bestScale,
                bestOffset,
                out var piecewiseCues,
                out int detectedSegments,
                out double representativeOffset))
        {
            alignedCues = piecewiseCues;
            segmentCount = detectedSegments;
            bestOffset = representativeOffset;
        }
        else
        {
            alignedCues = ApplyLinearTransform(targetCues, bestScale, bestOffset);
        }

        string diagnostics = FormattableString.Invariant(
            $"mode=audio2sub scale={bestScale:F6} offset={bestOffset:F3}s z={globalFit.ZScore:F2} corr={globalFit.Correlation:F4} segments={segmentCount} speechFrames={audioEnvelope.SpeechFrameCount}");

        return new SubtitleAlignmentOutcome(
            bestOffset,
            bestScale,
            globalFit.ZScore,
            audioEnvelope.SpeechFrameCount,
            segmentCount,
            alignedCues,
            diagnostics);
    }

    public static IReadOnlyList<SubtitleCue> ApplyLinearTransform(
        IReadOnlyList<SubtitleCue> cues,
        double scaleFactor,
        double offsetSeconds)
    {
        var result = new List<SubtitleCue>(cues.Count);
        double previousStart = 0.0;

        foreach (var cue in cues)
        {
            double start = cue.StartSeconds * scaleFactor + offsetSeconds;
            double end = cue.EndSeconds * scaleFactor + offsetSeconds;
            double duration = Math.Max(0.20, cue.Duration * scaleFactor);

            if (end <= 0.05)
            {
                start = Math.Max(0.0, previousStart);
                end = start + Math.Min(duration, 0.50);
            }
            else
            {
                start = Math.Max(0.0, start);
                if (end <= start + 0.15)
                    end = start + duration;
            }

            previousStart = start;
            result.Add(cue with { StartSeconds = start, EndSeconds = end });
        }

        return result;
    }

    internal static void BuildCueEnvelope(
        IReadOnlyList<SubtitleCue> cues,
        double scaleFactor,
        int frameCount,
        out float[] activity,
        out float[] onset,
        out int activeFrames)
    {
        activity = new float[frameCount];
        onset = new float[frameCount];
        activeFrames = 0;

        foreach (var cue in cues)
        {
            double scaledStart = cue.StartSeconds * scaleFactor;
            double scaledRawEnd = cue.EndSeconds * scaleFactor;
            double effectiveDuration = cue.EstimatedSpeechDuration * scaleFactor;
            double scaledEnd = Math.Min(scaledRawEnd, scaledStart + effectiveDuration);

            int startIdx = (int)Math.Round(scaledStart * EnvelopeSampleRateHz);
            int endIdx = (int)Math.Round(scaledEnd * EnvelopeSampleRateHz);

            if (endIdx <= 0 || startIdx >= frameCount)
                continue;

            int clampedStart = Math.Clamp(startIdx, 0, frameCount - 1);
            int clampedEnd = Math.Clamp(Math.Max(clampedStart + 1, endIdx), 0, frameCount);

            float weight = cue.IsSpokenDialogue ? 1.0f : 0.15f;
            for (int i = clampedStart; i < clampedEnd; i++)
            {
                if (activity[i] == 0f)
                    activeFrames++;
                if (weight > activity[i])
                    activity[i] = weight;
            }

            if (startIdx >= 0 && startIdx < frameCount)
            {
                for (int k = -3; k <= 3; k++)
                {
                    int idx = startIdx + k;
                    if (idx >= 0 && idx < frameCount)
                    {
                        float kernel = (4 - Math.Abs(k)) * 0.25f * weight;
                        if (kernel > onset[idx])
                            onset[idx] = kernel;
                    }
                }
            }
        }
    }

    private sealed record GlobalFitCandidate(
        double ScaleFactor,
        double OffsetSeconds,
        double Score,
        double Correlation,
        double ZScore);

    private static GlobalFitCandidate FindBestGlobalModel(
        float[] refActivity,
        float[] refOnset,
        IReadOnlyList<SubtitleCue> targetDialogue,
        int baseFrameCount,
        bool allowFineScaleGrid)
    {
        int maxLagFrames = (int)Math.Round(MaximumAbsOffsetSeconds * EnvelopeSampleRateHz);
        double maxTargetEnd = targetDialogue.Max(static cue => cue.EndSeconds);
        int maxScaledFrames = (int)Math.Ceiling(maxTargetEnd * MaximumScaleFactor * EnvelopeSampleRateHz) + maxLagFrames + 128;
        int fftSize = NextPowerOfTwo(Math.Max(baseFrameCount, maxScaledFrames) + maxLagFrames + 64);

        Complex[] refActFft = PrepareForwardFft(refActivity, fftSize, out double refActNorm);
        Complex[] refOnsFft = PrepareForwardFft(refOnset, fftSize, out double refOnsNorm);

        GlobalFitCandidate EvalScale(double scale)
        {
            BuildCueEnvelope(targetDialogue, scale, baseFrameCount, out float[] tgtAct, out float[] tgtOns, out _);
            double[] actCorr = CrossCorrelateFft(refActFft, tgtAct, fftSize, maxLagFrames, refActNorm);
            double[] onsCorr = CrossCorrelateFft(refOnsFft, tgtOns, fftSize, maxLagFrames, refOnsNorm);

            int totalLags = 2 * maxLagFrames + 1;
            double bestCombined = double.NegativeInfinity;
            double bestActCorr = 0;
            int bestIndex = maxLagFrames;
            double sum = 0;
            double sumSq = 0;

            for (int i = 0; i < totalLags; i++)
            {
                int lagFrames = i - maxLagFrames;
                double lagSeconds = (double)lagFrames / EnvelopeSampleRateHz;
                // Mild prior favoring smaller offsets only to break ties among noise peaks
                double priorPenalty = 0.00015 * Math.Abs(lagSeconds);
                double val = (0.68 * actCorr[i] + 0.32 * onsCorr[i]) - priorPenalty;
                sum += val;
                sumSq += val * val;
                if (val > bestCombined)
                {
                    bestCombined = val;
                    bestActCorr = actCorr[i];
                    bestIndex = i;
                }
            }

            double subFrameShift = ParabolicPeakInterpolation(actCorr, onsCorr, bestIndex, totalLags);
            double bestLagFrames = (bestIndex - maxLagFrames) + subFrameShift;
            double bestOffsetSeconds = bestLagFrames / EnvelopeSampleRateHz;

            // Compute Z-score excluding immediate neighborhood (+/- 1.5s = 75 frames) of the main peak
            double bgSum = 0;
            double bgSumSq = 0;
            int bgCount = 0;
            for (int i = 0; i < totalLags; i++)
            {
                if (Math.Abs(i - bestIndex) <= 75)
                    continue;
                double val = 0.68 * actCorr[i] + 0.32 * onsCorr[i];
                bgSum += val;
                bgSumSq += val * val;
                bgCount++;
            }

            double mean = bgCount > 0 ? bgSum / bgCount : sum / totalLags;
            double variance = bgCount > 1
                ? Math.Max(1e-9, (bgSumSq / bgCount) - (mean * mean))
                : Math.Max(1e-9, (sumSq / totalLags) - (mean * mean));
            double stdDev = Math.Sqrt(variance);
            double zScore = (bestCombined - mean) / stdDev;

            return new GlobalFitCandidate(scale, bestOffsetSeconds, bestCombined, bestActCorr, zScore);
        }

        var unitCandidate = EvalScale(1.0);
        var bestCandidate = unitCandidate;

        var candidateScales = new SortedSet<double>();
        foreach (double standard in StandardScaleCandidates)
        {
            if (standard >= MinimumScaleFactor && standard <= MaximumScaleFactor)
                candidateScales.Add(Math.Round(standard, 7));
        }

        if (allowFineScaleGrid && maxTargetEnd >= 25.0)
        {
            for (double s = 0.92; s <= 1.08001; s += 0.005)
                candidateScales.Add(Math.Round(s, 7));
        }

        foreach (double scale in candidateScales)
        {
            if (Math.Abs(scale - 1.0) < 1e-7)
                continue;

            var cand = EvalScale(scale);
            if (cand.Score > bestCandidate.Score)
                bestCandidate = cand;
        }

        // Fine search around the winning scale if it differs from 1.0
        if (Math.Abs(bestCandidate.ScaleFactor - 1.0) > 1e-5)
        {
            double center = bestCandidate.ScaleFactor;
            for (double delta = -0.004; delta <= 0.004001; delta += 0.0005)
            {
                double scale = Math.Clamp(center + delta, MinimumScaleFactor, MaximumScaleFactor);
                var cand = EvalScale(scale);
                if (cand.Score > bestCandidate.Score)
                    bestCandidate = cand;
            }

            double fineCenter = bestCandidate.ScaleFactor;
            for (double delta = -0.0004; delta <= 0.000401; delta += 0.0001)
            {
                double scale = Math.Clamp(fineCenter + delta, MinimumScaleFactor, MaximumScaleFactor);
                var cand = EvalScale(scale);
                if (cand.Score > bestCandidate.Score)
                    bestCandidate = cand;
            }
        }

        // Snap to exact standard framerate ratio if very close
        foreach (double standard in StandardScaleCandidates)
        {
            if (Math.Abs(bestCandidate.ScaleFactor - standard) <= 0.00035)
            {
                var snapped = EvalScale(standard);
                if (snapped.Score >= bestCandidate.Score * 0.992)
                {
                    bestCandidate = snapped;
                    break;
                }
            }
        }

        // Occam's Razor: keep ScaleFactor = 1.0 unless the non-unit scale provides a meaningful improvement
        if (Math.Abs(bestCandidate.ScaleFactor - 1.0) > 1e-5)
        {
            double relativeGain = (bestCandidate.Score - unitCandidate.Score) / Math.Max(0.05, Math.Abs(unitCandidate.Score));
            double zGain = bestCandidate.ZScore - unitCandidate.ZScore;
            bool isStandardRatio = StandardScaleCandidates.Any(s => Math.Abs(s - 1.0) > 1e-5 && Math.Abs(bestCandidate.ScaleFactor - s) <= 0.0004);
            double requiredGain = isStandardRatio ? 0.035 : 0.075;

            if (relativeGain < requiredGain && zGain < 1.2)
                return unitCandidate;
        }

        return bestCandidate;
    }

    private static bool IsReliableGlobalFit(GlobalFitCandidate fit, int activeFrames)
    {
        if (double.IsNaN(fit.OffsetSeconds) || double.IsInfinity(fit.OffsetSeconds))
            return false;
        if (Math.Abs(fit.OffsetSeconds) >= MaximumAbsOffsetSeconds)
            return false;
        if (fit.ScaleFactor < MinimumScaleFactor || fit.ScaleFactor > MaximumScaleFactor)
            return false;
        if (activeFrames < 40)
            return false;

        return fit.Correlation >= 0.08 && fit.ZScore >= 3.2;
    }

    private static (double ScaleFactor, double OffsetSeconds) RefineSubtitleToSubtitleModel(
        IReadOnlyList<SubtitleCue> refDialogue,
        IReadOnlyList<SubtitleCue> targetDialogue,
        double initialScale,
        double initialOffset)
    {
        var pairs = MatchCuePairs(refDialogue, targetDialogue, initialScale, initialOffset, maxToleranceSeconds: 0.85);
        if (pairs.Count < 6)
            return (initialScale, initialOffset);

        // Compute median residual at initialScale
        var residuals = pairs
            .Select(p => p.RefTime - (p.TargetTime * initialScale))
            .OrderBy(static x => x)
            .ToArray();

        double medianOffset = GetPercentile(residuals, 0.50);

        // Check if a linear regression over matched inlier cues confirms a scale refinement
        var inliers = pairs
            .Where(p => Math.Abs((p.RefTime - (p.TargetTime * initialScale + medianOffset))) <= 0.35)
            .ToList();

        if (inliers.Count >= 12)
        {
            double minT = inliers.Min(static p => p.TargetTime);
            double maxT = inliers.Max(static p => p.TargetTime);
            if (maxT - minT >= 120.0 && Math.Abs(initialScale - 1.0) > 1e-4)
            {
                double meanX = inliers.Average(static p => p.TargetTime);
                double meanY = inliers.Average(static p => p.RefTime);
                double num = 0;
                double den = 0;
                foreach (var p in inliers)
                {
                    double dx = p.TargetTime - meanX;
                    num += dx * (p.RefTime - meanY);
                    den += dx * dx;
                }

                if (den > 1.0)
                {
                    double regScale = num / den;
                    if (Math.Abs(regScale - initialScale) <= 0.002)
                    {
                        double regOffset = meanY - regScale * meanX;
                        return (regScale, regOffset);
                    }
                }
            }
        }

        return (initialScale, medianOffset);
    }

    private readonly record struct MatchedCuePair(double RefTime, double TargetTime);

    private static List<MatchedCuePair> MatchCuePairs(
        IReadOnlyList<SubtitleCue> refDialogue,
        IReadOnlyList<SubtitleCue> targetDialogue,
        double scale,
        double offset,
        double maxToleranceSeconds)
    {
        var pairs = new List<MatchedCuePair>(Math.Min(refDialogue.Count, targetDialogue.Count));
        int refIdx = 0;

        foreach (var tgt in targetDialogue)
        {
            double predictedStart = tgt.StartSeconds * scale + offset;
            while (refIdx + 1 < refDialogue.Count && refDialogue[refIdx + 1].StartSeconds <= predictedStart)
                refIdx++;

            double bestDist = double.MaxValue;
            SubtitleCue? bestRef = null;

            for (int k = Math.Max(0, refIdx - 2); k <= Math.Min(refDialogue.Count - 1, refIdx + 2); k++)
            {
                double dist = Math.Abs(refDialogue[k].StartSeconds - predictedStart);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestRef = refDialogue[k];
                }
            }

            if (bestRef is not null && bestDist <= maxToleranceSeconds)
            {
                pairs.Add(new MatchedCuePair(bestRef.StartSeconds, tgt.StartSeconds));
            }
        }

        return pairs;
    }

    private sealed record DialogueBlock(
        int StartCueIndex,
        int EndCueIndex,
        double StartTimeSeconds,
        double EndTimeSeconds,
        IReadOnlyList<SubtitleCue> DialogueCues);

    private static List<DialogueBlock> PartitionIntoDialogueBlocks(IReadOnlyList<SubtitleCue> allCues)
    {
        const double BreakGapSeconds = 3.5;
        const double MinimumBlockSpanSeconds = 22.0;
        const int MinimumDialogueCuesPerBlock = 8;

        var rawBlocks = new List<DialogueBlock>();
        int blockStartIdx = 0;
        var currentDialogue = new List<SubtitleCue>();

        for (int i = 0; i < allCues.Count; i++)
        {
            var cue = allCues[i];
            if (cue.IsSpokenDialogue)
                currentDialogue.Add(cue);

            bool isLast = i == allCues.Count - 1;
            double gapToNext = isLast ? double.MaxValue : allCues[i + 1].StartSeconds - cue.EndSeconds;

            if (isLast || (gapToNext >= BreakGapSeconds && currentDialogue.Count >= MinimumDialogueCuesPerBlock))
            {
                double startSec = allCues[blockStartIdx].StartSeconds;
                double endSec = cue.EndSeconds;
                if (endSec - startSec >= MinimumBlockSpanSeconds || isLast)
                {
                    rawBlocks.Add(new DialogueBlock(
                        blockStartIdx,
                        i,
                        startSec,
                        endSec,
                        currentDialogue.ToList()));
                    blockStartIdx = i + 1;
                    currentDialogue.Clear();
                }
            }
        }

        if (rawBlocks.Count > 1 && rawBlocks[^1].DialogueCues.Count < MinimumDialogueCuesPerBlock)
        {
            var prev = rawBlocks[^2];
            var last = rawBlocks[^1];
            var combinedDialogue = prev.DialogueCues.Concat(last.DialogueCues).ToList();
            rawBlocks[^2] = new DialogueBlock(
                prev.StartCueIndex,
                last.EndCueIndex,
                prev.StartTimeSeconds,
                last.EndTimeSeconds,
                combinedDialogue);
            rawBlocks.RemoveAt(rawBlocks.Count - 1);
        }

        return rawBlocks;
    }

    private static bool TryAlignPiecewiseToSubtitles(
        float[] refActivity,
        float[] refOnset,
        IReadOnlyList<SubtitleCue> refDialogue,
        IReadOnlyList<SubtitleCue> allTargetCues,
        IReadOnlyList<SubtitleCue> targetDialogue,
        double globalScale,
        double globalOffset,
        out IReadOnlyList<SubtitleCue> alignedCues,
        out int distinctSegmentCount,
        out double representativeOffset)
    {
        if (!TrySolvePiecewiseBlockOffsets(
                refActivity,
                refOnset,
                allTargetCues,
                targetDialogue,
                globalScale,
                globalOffset,
                out var blocks,
                out var blockOffsets,
                out distinctSegmentCount,
                out representativeOffset))
        {
            alignedCues = [];
            return false;
        }

        // Refine each block's offset against reference subtitle cues
        for (int b = 0; b < blocks.Count; b++)
        {
            var block = blocks[b];
            var matched = MatchCuePairs(refDialogue, block.DialogueCues, globalScale, blockOffsets[b], maxToleranceSeconds: 0.75);
            if (matched.Count >= 4)
            {
                var residuals = matched
                    .Select(p => p.RefTime - (p.TargetTime * globalScale))
                    .OrderBy(static x => x)
                    .ToArray();
                blockOffsets[b] = GetPercentile(residuals, 0.50);
            }
        }

        var piecewiseTransformed = ApplyPiecewiseBlockOffsets(allTargetCues, blocks, globalScale, blockOffsets);
        alignedCues = SnapCuesToReferenceSubtitles(refDialogue, piecewiseTransformed);
        return true;
    }

    private static bool TryAlignPiecewiseToEnvelope(
        float[] refActivity,
        float[] refOnset,
        IReadOnlyList<SubtitleCue> allTargetCues,
        IReadOnlyList<SubtitleCue> targetDialogue,
        double globalScale,
        double globalOffset,
        out IReadOnlyList<SubtitleCue> alignedCues,
        out int distinctSegmentCount,
        out double representativeOffset)
    {
        if (!TrySolvePiecewiseBlockOffsets(
                refActivity,
                refOnset,
                allTargetCues,
                targetDialogue,
                globalScale,
                globalOffset,
                out var blocks,
                out var blockOffsets,
                out distinctSegmentCount,
                out representativeOffset))
        {
            alignedCues = [];
            return false;
        }

        alignedCues = ApplyPiecewiseBlockOffsets(allTargetCues, blocks, globalScale, blockOffsets);
        return true;
    }

    private static bool TrySolvePiecewiseBlockOffsets(
        float[] refActivity,
        float[] refOnset,
        IReadOnlyList<SubtitleCue> allTargetCues,
        IReadOnlyList<SubtitleCue> targetDialogue,
        double globalScale,
        double globalOffset,
        out List<DialogueBlock> blocks,
        out double[] solvedOffsets,
        out int distinctSegmentCount,
        out double representativeOffset)
    {
        blocks = PartitionIntoDialogueBlocks(allTargetCues);
        solvedOffsets = [];
        distinctSegmentCount = 1;
        representativeOffset = globalOffset;

        if (blocks.Count < 2)
            return false;

        // Offset grid from -45s to +45s in 0.10s steps (901 candidates)
        const double MinOffset = -45.0;
        const double MaxOffset = 45.0;
        const double StepSeconds = 0.10;
        int numCandidates = (int)Math.Round((MaxOffset - MinOffset) / StepSeconds) + 1;
        double[] candidateOffsets = new double[numCandidates];
        for (int c = 0; c < numCandidates; c++)
            candidateOffsets[c] = MinOffset + c * StepSeconds;

        int numBlocks = blocks.Count;
        double[,] blockScores = new double[numBlocks, numCandidates];

        for (int b = 0; b < numBlocks; b++)
        {
            var block = blocks[b];
            if (block.DialogueCues.Count == 0)
                continue;

            int minFrame = Math.Max(0, (int)Math.Floor((block.StartTimeSeconds * globalScale + MinOffset - 2.0) * EnvelopeSampleRateHz));
            int spanFrames = Math.Max(64, (int)Math.Ceiling((block.EndTimeSeconds - block.StartTimeSeconds) * globalScale * EnvelopeSampleRateHz) + 32);

            BuildLocalBlockEnvelope(block.DialogueCues, block.StartTimeSeconds, globalScale, spanFrames, out float[] localAct, out float[] localOns);

            double maxRaw = 1e-6;
            for (int c = 0; c < numCandidates; c++)
            {
                double offset = candidateOffsets[c];
                int refStartFrame = (int)Math.Round((block.StartTimeSeconds * globalScale + offset) * EnvelopeSampleRateHz);
                double raw = EvaluateLocalOverlap(refActivity, refOnset, localAct, localOns, refStartFrame);
                blockScores[b, c] = raw;
                if (raw > maxRaw)
                    maxRaw = raw;
            }

            double weight = Math.Sqrt(block.DialogueCues.Count);
            for (int c = 0; c < numCandidates; c++)
            {
                double proximityPrior = -0.02 * Math.Min(1.0, Math.Abs(candidateOffsets[c] - globalOffset) / 25.0);
                blockScores[b, c] = weight * (blockScores[b, c] / maxRaw + proximityPrior);
            }
        }

        // Dynamic Programming (Viterbi) with L0 jump penalty + L1 transition cost to find piecewise-constant act offsets
        const double JumpFixedPenalty = 1.65;
        const double JumpPerSecondPenalty = 0.08;

        double[,] dp = new double[numBlocks, numCandidates];
        int[,] parent = new int[numBlocks, numCandidates];

        for (int c = 0; c < numCandidates; c++)
        {
            dp[0, c] = blockScores[0, c];
            parent[0, c] = -1;
        }

        for (int b = 1; b < numBlocks; b++)
        {
            double gapBetweenBlocks = Math.Max(0.0, blocks[b].StartTimeSeconds - blocks[b - 1].EndTimeSeconds);
            // Larger silence gap (e.g. commercial break black screen / scene transition) reduces jump penalty
            double gapFactor = gapBetweenBlocks >= 5.0 ? 0.72 : 1.0;

            for (int c = 0; c < numCandidates; c++)
            {
                double bestPrev = dp[b - 1, c];
                int bestPrevIdx = c;

                for (int prev = 0; prev < numCandidates; prev++)
                {
                    if (prev == c)
                        continue;

                    double deltaSeconds = Math.Abs(candidateOffsets[c] - candidateOffsets[prev]);
                    // Prevent consecutive blocks from overlapping backwards in time
                    if (candidateOffsets[c] - candidateOffsets[prev] < -(gapBetweenBlocks - 0.25))
                        continue;

                    double penalty = gapFactor * (JumpFixedPenalty + JumpPerSecondPenalty * deltaSeconds);
                    double candidateVal = dp[b - 1, prev] - penalty;
                    if (candidateVal > bestPrev)
                    {
                        bestPrev = candidateVal;
                        bestPrevIdx = prev;
                    }
                }

                dp[b, c] = bestPrev + blockScores[b, c];
                parent[b, c] = bestPrevIdx;
            }
        }

        int bestFinalIdx = 0;
        double bestFinalScore = double.NegativeInfinity;
        for (int c = 0; c < numCandidates; c++)
        {
            if (dp[numBlocks - 1, c] > bestFinalScore)
            {
                bestFinalScore = dp[numBlocks - 1, c];
                bestFinalIdx = c;
            }
        }

        // Compare against constant offset score across all blocks
        int globalCandidateIdx = Math.Clamp((int)Math.Round((globalOffset - MinOffset) / StepSeconds), 0, numCandidates - 1);
        double constantScore = 0;
        for (int b = 0; b < numBlocks; b++)
            constantScore += blockScores[b, globalCandidateIdx];

        solvedOffsets = new double[numBlocks];
        int cur = bestFinalIdx;
        for (int b = numBlocks - 1; b >= 0; b--)
        {
            solvedOffsets[b] = candidateOffsets[cur];
            cur = parent[b, cur];
        }

        // Count distinct act segments (steps > 0.35s)
        int segments = 1;
        double minSolved = solvedOffsets[0];
        double maxSolved = solvedOffsets[0];
        for (int b = 1; b < numBlocks; b++)
        {
            if (Math.Abs(solvedOffsets[b] - solvedOffsets[b - 1]) >= 0.35)
                segments++;
            if (solvedOffsets[b] < minSolved) minSolved = solvedOffsets[b];
            if (solvedOffsets[b] > maxSolved) maxSolved = solvedOffsets[b];
        }

        if (segments <= 1 || (maxSolved - minSolved) < 0.45 || bestFinalScore <= constantScore + 0.45)
            return false;

        distinctSegmentCount = segments;

        // Weighted median offset across dialogue cues
        var weightedOffsets = new List<double>(targetDialogue.Count);
        for (int b = 0; b < numBlocks; b++)
        {
            for (int i = 0; i < blocks[b].DialogueCues.Count; i++)
                weightedOffsets.Add(solvedOffsets[b]);
        }

        weightedOffsets.Sort();
        representativeOffset = weightedOffsets.Count > 0
            ? weightedOffsets[weightedOffsets.Count / 2]
            : globalOffset;

        return true;
    }

    private static IReadOnlyList<SubtitleCue> ApplyPiecewiseBlockOffsets(
        IReadOnlyList<SubtitleCue> allCues,
        IReadOnlyList<DialogueBlock> blocks,
        double scaleFactor,
        double[] blockOffsets)
    {
        var result = new List<SubtitleCue>(allCues.Count);
        double previousEnd = 0.0;

        for (int b = 0; b < blocks.Count; b++)
        {
            var block = blocks[b];
            double offset = blockOffsets[b];

            for (int i = block.StartCueIndex; i <= block.EndCueIndex; i++)
            {
                var cue = allCues[i];
                double duration = Math.Max(0.20, cue.Duration * scaleFactor);
                double start = cue.StartSeconds * scaleFactor + offset;
                double end = cue.EndSeconds * scaleFactor + offset;

                start = Math.Max(previousEnd, Math.Max(0.0, start));
                if (end <= start + 0.15)
                    end = start + duration;

                previousEnd = start + 0.02;
                result.Add(cue with { StartSeconds = start, EndSeconds = end });
            }
        }

        return result;
    }

    private static IReadOnlyList<SubtitleCue> SnapCuesToReferenceSubtitles(
        IReadOnlyList<SubtitleCue> refDialogue,
        IReadOnlyList<SubtitleCue> alignedCues)
    {
        if (refDialogue.Count == 0 || alignedCues.Count == 0)
            return alignedCues;

        // First compute local smoothed residuals for gentle fine adjustment, then snap close boundaries
        const double SnapWindowSeconds = 0.38;
        var result = new List<SubtitleCue>(alignedCues.Count);
        int refIdx = 0;
        double prevStart = 0.0;

        for (int i = 0; i < alignedCues.Count; i++)
        {
            var cue = alignedCues[i];
            if (!cue.IsSpokenDialogue)
            {
                result.Add(cue);
                continue;
            }

            while (refIdx + 1 < refDialogue.Count && refDialogue[refIdx + 1].StartSeconds <= cue.StartSeconds)
                refIdx++;

            SubtitleCue? closestRef = null;
            double bestDist = double.MaxValue;
            for (int k = Math.Max(0, refIdx - 2); k <= Math.Min(refDialogue.Count - 1, refIdx + 2); k++)
            {
                double dist = Math.Abs(refDialogue[k].StartSeconds - cue.StartSeconds);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    closestRef = refDialogue[k];
                }
            }

            double newStart = cue.StartSeconds;
            double newEnd = cue.EndSeconds;

            if (closestRef is not null && bestDist <= SnapWindowSeconds)
            {
                double duration = cue.Duration;
                newStart = Math.Max(prevStart, closestRef.StartSeconds);
                double endDist = Math.Abs(closestRef.EndSeconds - (newStart + duration));
                newEnd = endDist <= 0.45
                    ? Math.Max(newStart + 0.25, closestRef.EndSeconds)
                    : newStart + duration;
            }

            prevStart = newStart;
            result.Add(cue with { StartSeconds = newStart, EndSeconds = newEnd });
        }

        return result;
    }

    private static void BuildLocalBlockEnvelope(
        IReadOnlyList<SubtitleCue> cues,
        double blockStartSeconds,
        double scaleFactor,
        int spanFrames,
        out float[] activity,
        out float[] onset)
    {
        activity = new float[spanFrames];
        onset = new float[spanFrames];

        foreach (var cue in cues)
        {
            double relStart = (cue.StartSeconds - blockStartSeconds) * scaleFactor;
            double relEnd = relStart + Math.Min(cue.Duration, cue.EstimatedSpeechDuration) * scaleFactor;

            int sIdx = Math.Clamp((int)Math.Round(relStart * EnvelopeSampleRateHz), 0, spanFrames - 1);
            int eIdx = Math.Clamp(Math.Max(sIdx + 1, (int)Math.Round(relEnd * EnvelopeSampleRateHz)), 0, spanFrames);

            for (int i = sIdx; i < eIdx; i++)
                activity[i] = 1.0f;

            for (int k = -2; k <= 2; k++)
            {
                int idx = sIdx + k;
                if (idx >= 0 && idx < spanFrames)
                {
                    float w = (3 - Math.Abs(k)) / 3.0f;
                    if (w > onset[idx])
                        onset[idx] = w;
                }
            }
        }
    }

    private static double EvaluateLocalOverlap(
        float[] refActivity,
        float[] refOnset,
        float[] localActivity,
        float[] localOnset,
        int refStartFrame)
    {
        double actSum = 0;
        double onsSum = 0;
        int len = localActivity.Length;
        int refLen = refActivity.Length;

        int iStart = Math.Max(0, -refStartFrame);
        int iEnd = Math.Min(len, refLen - refStartFrame);

        for (int i = iStart; i < iEnd; i++)
        {
            int rIdx = refStartFrame + i;
            actSum += localActivity[i] * refActivity[rIdx];
            onsSum += localOnset[i] * refOnset[rIdx];
        }

        return 0.70 * actSum + 0.30 * onsSum;
    }

    private static double ParabolicPeakInterpolation(double[] actCorr, double[] onsCorr, int peakIndex, int length)
    {
        if (peakIndex <= 0 || peakIndex >= length - 1)
            return 0.0;

        double yMinus = 0.68 * actCorr[peakIndex - 1] + 0.32 * onsCorr[peakIndex - 1];
        double yZero = 0.68 * actCorr[peakIndex] + 0.32 * onsCorr[peakIndex];
        double yPlus = 0.68 * actCorr[peakIndex + 1] + 0.32 * onsCorr[peakIndex + 1];

        double denom = yMinus - 2.0 * yZero + yPlus;
        if (Math.Abs(denom) < 1e-12)
            return 0.0;

        double delta = 0.5 * (yMinus - yPlus) / denom;
        return Math.Clamp(delta, -0.5, 0.5);
    }

    private static Complex[] PrepareForwardFft(float[] signal, int fftSize, out double norm)
    {
        var buffer = new Complex[fftSize];
        double sumSq = 0;
        int copyLen = Math.Min(signal.Length, fftSize);
        for (int i = 0; i < copyLen; i++)
        {
            double v = signal[i];
            buffer[i] = new Complex(v, 0.0);
            sumSq += v * v;
        }

        norm = Math.Sqrt(Math.Max(1e-12, sumSq));
        FftInPlace(buffer, inverse: false);
        return buffer;
    }

    private static double[] CrossCorrelateFft(
        Complex[] refFft,
        float[] target,
        int fftSize,
        int maxLagFrames,
        double refNorm)
    {
        var buffer = new Complex[fftSize];
        double targetSumSq = 0;
        int copyLen = Math.Min(target.Length, fftSize);
        for (int i = 0; i < copyLen; i++)
        {
            double v = target[i];
            buffer[i] = new Complex(v, 0.0);
            targetSumSq += v * v;
        }

        double denom = refNorm * Math.Sqrt(Math.Max(1e-12, targetSumSq));
        FftInPlace(buffer, inverse: false);

        for (int i = 0; i < fftSize; i++)
        {
            buffer[i] = refFft[i] * Complex.Conjugate(buffer[i]);
        }

        FftInPlace(buffer, inverse: true);

        int totalLags = 2 * maxLagFrames + 1;
        double[] correlations = new double[totalLags];

        for (int lag = -maxLagFrames; lag <= maxLagFrames; lag++)
        {
            int circularIndex = lag >= 0 ? lag : fftSize + lag;
            correlations[lag + maxLagFrames] = buffer[circularIndex].Real / denom;
        }

        return correlations;
    }

    private static void FftInPlace(Complex[] buffer, bool inverse)
    {
        int n = buffer.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;

            if (i < j)
                (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = 2.0 * Math.PI / len * (inverse ? 1.0 : -1.0);
            var wlen = new Complex(Math.Cos(angle), Math.Sin(angle));
            int half = len >> 1;

            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int j = 0; j < half; j++)
                {
                    Complex u = buffer[i + j];
                    Complex v = buffer[i + j + half] * w;
                    buffer[i + j] = u + v;
                    buffer[i + j + half] = u - v;
                    w *= wlen;
                }
            }
        }

        if (inverse)
        {
            double invN = 1.0 / n;
            for (int i = 0; i < n; i++)
                buffer[i] *= invN;
        }
    }

    private static int NextPowerOfTwo(int value)
    {
        int power = 1;
        while (power < value)
            power <<= 1;
        return power;
    }

    private static float[] PadArray(float[] source, int length)
    {
        if (source.Length >= length)
            return source;

        float[] padded = new float[length];
        Array.Copy(source, padded, source.Length);
        return padded;
    }

    private static double GetPercentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0)
            return 0.0;

        int index = Math.Clamp((int)Math.Round((sortedValues.Length - 1) * percentile), 0, sortedValues.Length - 1);
        return sortedValues[index];
    }
}
