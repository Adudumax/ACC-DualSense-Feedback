using System.Diagnostics;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Hardware.DualSense;

namespace ACCDualSenseFeedback.Runtime;

// Owned by the existing output thread. This instance survives runtime retries
// so reconnecting or clicking Check again cannot replay the startup action.
// No fabricated telemetry, sleeping, device access or haptic-engine changes.
internal sealed class StartupTriggerConfirmation
{
    public static readonly StartupTriggerConfirmation ForProcess = new();
    internal static readonly long DurationTicks = (long)(0.120 * Stopwatch.Frequency);
    // L2 uses the gameplay profile; R2 retains its existing resting curve.
    private static readonly TriggerEffect ThrottleResistance =
        TriggerEffect.FeedbackZones(2, 2, 2, 3, 3, 3, 4, 4, 5, 5);
    private bool _consumed;
    private bool _active;
    private bool _leftAllowed = true;
    private bool _rightAllowed = true;
    private long _startedAt;

    public FeedbackFrame Apply(in FeedbackFrame normal, long now, bool deviceConnected,
        bool gameStateKnown, bool gameRunning, bool telemetryLive,
        Func<FeedbackTuning>? tuningProvider = null)
    {
        // Existing game feedback always wins. Seeing ACC already running also
        // skips this action, including the first-packet verification window.
        if (gameRunning || telemetryLive || normal != FeedbackFrame.Off)
        {
            _consumed = true;
            Cancel();
            return normal;
        }
        if (!deviceConnected) { Cancel(); return normal; }
        if (!gameStateKnown) return normal;
        if (!_consumed)
        {
            // Consume before the first write; a failed write is not retried as
            // another startup pulse when the device is opened again.
            _consumed = true;
            _active = true;
            _startedAt = now;
        }
        if (!_active) return normal;
        if (now - _startedAt >= DurationTicks)
        {
            Cancel();
            return normal;
        }
        FeedbackTuning tuning = tuningProvider?.Invoke() ?? FeedbackTuning.Neutral;
        TriggerEffect left = _leftAllowed ? BrakePedalFeedback.CreateResistance(tuning.BrakeResistanceScale) : TriggerEffect.Off;
        TriggerEffect right = _rightAllowed ? BuildTrigger(ThrottleResistance, tuning.ThrottleResistanceScale) : TriggerEffect.Off;
        // A side disabled before/during the cue stays disabled for its remainder,
        // even if settings change again inside this short window.
        _leftAllowed = left.Mode != 0x05;
        _rightAllowed = right.Mode != 0x05;
        if (!_leftAllowed && !_rightAllowed) { Cancel(); return normal; }
        return new(0, 0, left, right);
    }

    public void Cancel() => _active = false;

    private static TriggerEffect BuildTrigger(in TriggerEffect baseline, float scale)
        => float.IsFinite(scale)
            // The existing 0-100 base-resistance settings produce scales up to 1.35.
            ? FeedbackTuning.ScaleTrigger(baseline, Math.Clamp(scale, 0, 1.35f))
            : TriggerEffect.Off;
}
