using System.Diagnostics;
using ACCDualSenseFeedback.Telemetry;

namespace ACCDualSenseFeedback.Haptics;

internal readonly record struct TractionCueFrame(float TcStrength, float WheelspinStrength, float TcGripPulse);

// Only owns the two traction cues. Legacy tyre learning and background mixing
// deliberately keep their preview.4 state and inputs in the feedback engine.
internal struct TractionFeedbackState
{
    private static readonly long StaleTicks = (long)(0.120 * Stopwatch.Frequency);
    private static readonly long MinimumTcTicks = (long)(0.100 * Stopwatch.Frequency);
    private static readonly long GapTicks = (long)(0.024 * Stopwatch.Frequency);
    private static readonly long GripTicks = (long)(0.080 * Stopwatch.Frequency);
    private bool _primed;
    private int _packet;
    private long _observed, _freshAt, _updatedAt;
    private float _rawTc, _rawSpin;
    private bool _tcActive;
    private long _tcStartedAt, _tcQuietAt;
    private float _tcHeldStrength, _tcEnvelope, _spinEnvelope;

    public TractionCueFrame Update(in TelemetrySnapshot sample, float wheelspinTarget,
        float tcScale, float spinScale, float deltaSeconds, long now, bool shiftActive)
    {
        bool timestamped = sample.ObservedTimestamp != 0;
        bool fresh = !_primed || !timestamped || sample.PacketId != _packet;
        bool reset = _updatedAt != 0 && (now < _updatedAt || now - _updatedAt > StaleTicks);
        if (_primed && fresh && timestamped)
            reset |= sample.ObservedTimestamp < _observed || unchecked(sample.PacketId - _packet) < 0;
        if (reset) ClearCues();
        _updatedAt = now;

        if (fresh)
        {
            _primed = true;
            _packet = sample.PacketId;
            _observed = sample.ObservedTimestamp;
            _freshAt = now;
            float tc = float.IsFinite(sample.TcIntervention) ? Math.Clamp(sample.TcIntervention, 0f, 1f) : 0f;
            _rawTc = tc > 0.01f ? tc : 0f;
            _rawSpin = float.IsFinite(wheelspinTarget) ? Math.Clamp(wheelspinTarget, 0f, 1f) : 0f;
        }
        else if (now < _freshAt || now - _freshAt > StaleTicks)
        {
            // Keep packet identity: clearing it would accept the same frozen
            // positive packet again on the next output tick.
            ClearCues();
            return default;
        }

        if (shiftActive)
        {
            ClearCues();
            return default; // No stored cue may play after the actual gear kick.
        }

        float dt = float.IsFinite(deltaSeconds) ? Math.Clamp(deltaSeconds, .001f, .050f) : .004f;
        if (!float.IsFinite(spinScale) || spinScale <= 0f) _spinEnvelope = 0f;
        else _spinEnvelope = Follow(_spinEnvelope, _rawSpin, dt, .018f, .100f);

        if (!float.IsFinite(tcScale) || tcScale <= 0f)
        {
            ClearTc();
            return new(0f, _spinEnvelope, 0f);
        }
        if (_rawTc > 0f)
        {
            if (!_tcActive)
            {
                _tcActive = true;
                _tcStartedAt = now;
            }
            _tcQuietAt = 0;
            // Perceptual floor, not a claim about ECU torque-cut percentage.
            _tcHeldStrength = MathF.Max(.32f, _rawTc);
        }
        else if (_tcActive && _tcQuietAt == 0) _tcQuietAt = now;

        if (_tcActive && _rawTc == 0f && now - _tcStartedAt >= MinimumTcTicks &&
            now - _tcQuietAt >= GapTicks)
            ClearTc();

        float grip = 0f;
        if (_tcActive)
        {
            _tcEnvelope = Follow(_tcEnvelope, _tcHeldStrength, dt, .008f, .028f);
            long age = now - _tcStartedAt;
            if (age >= 0 && age < GripTicks)
            {
                float remaining = 1f - age / (float)GripTicks;
                grip = Math.Clamp(_tcHeldStrength * MathF.Min(tcScale, 1.5f), 0f, 1f)
                    * remaining * remaining;
            }
        }
        return new(_tcEnvelope, _spinEnvelope, grip);
    }

    private void ClearTc()
    {
        _tcActive = false;
        _tcStartedAt = _tcQuietAt = 0;
        _tcHeldStrength = _tcEnvelope = 0f;
    }

    private void ClearCues()
    {
        ClearTc();
        _spinEnvelope = 0f;
    }

    private static float Follow(float current, float target, float dt, float attack, float release)
        => current + (target - current) * (1f - MathF.Exp(-dt / (target > current ? attack : release)));
}
