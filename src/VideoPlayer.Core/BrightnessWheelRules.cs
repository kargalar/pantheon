namespace VideoPlayer.Core;

public static class BrightnessWheelRules
{
    public const int MinimumBrightness = 0;
    public const int MaximumBrightness = 100;
    public const int AdjustmentStep = 5;

    public static int Adjust(int currentBrightness, double wheelDelta)
    {
        if (!double.IsFinite(wheelDelta) || wheelDelta == 0)
            return Math.Clamp(currentBrightness, MinimumBrightness, MaximumBrightness);

        var direction = Math.Sign(wheelDelta);
        return Math.Clamp(currentBrightness + direction * AdjustmentStep, MinimumBrightness, MaximumBrightness);
    }
}

