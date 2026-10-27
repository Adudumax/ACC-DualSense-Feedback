using ACCDualSenseFeedback.Hardware.DualSense;

namespace ACCDualSenseFeedback.Haptics;

// L2 only. Keep input mapping, event detection and the shared R2/lock renderer
// independent from this experimental position-dependent ABS pulse profile.
internal static class BrakePedalFeedback
{
    internal const string ProfileVersion = "1.2.0-preview.4-griploss.3-l2.2";
    private static ReadOnlySpan<byte> ResistanceZones => [3, 4, 4, 5, 6, 6, 7, 7, 8, 8];

    public static TriggerEffect CreateResistance(float scale)
        => FeedbackTuning.ScaleTrigger(TriggerEffect.FeedbackZones(ResistanceZones),
            float.IsFinite(scale) ? Math.Clamp(scale, 0f, 1.35f) : 0f);

    public static TriggerEffect BuildAbs(float baselineScale, float envelope, float eventScale)
    {
        if (!float.IsFinite(eventScale) || eventScale <= 0f ||
            !float.IsFinite(envelope) || envelope < 0.035f)
            return CreateResistance(baselineScale);

        // Preserve the original threshold, envelope-to-amplitude calibration,
        // start zone and hardware-driven 15 Hz cadence.
        float developed = Math.Clamp((envelope - 0.035f) / (0.75f - 0.035f), 0f, 1f);
        developed = developed * developed * (3f - 2f * developed);
        int calibratedAmplitude = Math.Clamp(2 + (int)MathF.Floor(developed * 4f), 2, 5);
        float eventAmplitude = calibratedAmplitude * eventScale;
        float supportGain = 0.60f * (float.IsFinite(baselineScale)
            ? Math.Clamp(baselineScale, 0f, 1.35f) : 0f);

        // Isolated ABS retains its original command, including low settings.
        if (supportGain == 0f)
            return TriggerEffect.Vibration(2,
                (byte)Math.Clamp((int)MathF.Round(eventAmplitude), 1, 8), 15);

        Span<byte> amplitudes = stackalloc byte[10];
        amplitudes.Clear();
        for (int index = 2; index < amplitudes.Length; index++)
        {
            float position = Math.Clamp((ResistanceZones[index] - 4f) / 4f, 0f, 1f);
            // Quantize only after applying both controls. A tiny ABS setting
            // must not be rounded up first and then amplified by base strength.
            amplitudes[index] = (byte)Math.Clamp(
                (int)MathF.Round(eventAmplitude * (1f + supportGain * position)), 1, 8);
        }

        // These are vibration amplitudes, not a second feedback-force layer.
        return TriggerEffect.VibrationZones(amplitudes, 15);
    }
}
