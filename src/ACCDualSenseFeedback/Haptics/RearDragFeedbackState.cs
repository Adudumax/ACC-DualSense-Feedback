using System.Diagnostics;
using ACCDualSenseFeedback.Telemetry;

namespace ACCDualSenseFeedback.Haptics;

internal readonly record struct RearDragCueFrame(
    float Strength, float Left, float Right, bool Active, bool Recovery);

// A selective transplant of griploss.1's REAR voice onto tcgrip.4.
// Qualification is independent of tone: it can open/close an event, but never
// multiplies a confirmed voice by another low blend weight. No front cue and
// no writes to the legacy slip/ownership/engine/TC/pedal state.
internal struct RearDragFeedbackState
{
    private static readonly long StaleTicks = Ticks(.120);
    private static readonly long RecoveryTicks = Ticks(.180);
    private bool _primed, _motionPrimed, _eventActive, _recovering;
    private int _packet;
    private long _observed, _freshAt, _updatedAt, _startedAt, _confirmedAt;
    private float _target, _strength, _evidenceTime;
    private float _previousBody, _previousRear, _previousYaw, _previousSteer;

    public RearDragCueFrame Update(in TelemetrySnapshot sample, float gripScale, float deltaSeconds, long now)
    {
        bool timestamped = sample.ObservedTimestamp != 0;
        bool fresh = !_primed || sample.PacketId != _packet;
        bool discontinuity = _updatedAt != 0 && (now < _updatedAt || now - _updatedAt > StaleTicks);
        if (_primed && fresh && timestamped)
            discontinuity |= sample.ObservedTimestamp < _observed || unchecked(sample.PacketId - _packet) < 0;
        if (discontinuity) ClearDynamics();
        _updatedAt = now;

        if (!float.IsFinite(gripScale) || gripScale <= 0f)
        {
            ClearDynamics();
            _primed = true; _packet = sample.PacketId;
            _observed = sample.ObservedTimestamp; _freshAt = now;
            return default;
        }

        float dt = float.IsFinite(deltaSeconds) ? Math.Clamp(deltaSeconds, .001f, .050f) : .004f;
        if (fresh)
        {
            float sampleDt = _primed && !discontinuity
                ? ((timestamped && _observed != 0 ? sample.ObservedTimestamp - _observed : now - _freshAt)
                    / (float)Stopwatch.Frequency)
                : .004f;
            sampleDt = Math.Clamp(sampleDt, .001f, .050f);
            _primed = true; _packet = sample.PacketId;
            _observed = sample.ObservedTimestamp; _freshAt = now;
            if (!ValidMotion(sample))
            {
                ClearDynamics();
                return default;
            }
            UpdateTarget(sample, sampleDt, now);
        }
        else if (now < _freshAt || now - _freshAt > StaleTicks)
        {
            // Retain packet identity: a frozen positive packet cannot restart.
            ClearDynamics();
            return default;
        }

        // Preserve griploss.1's rear envelope and complete rear waveform.
        _strength = Follow(_strength, _target, dt, .012f, .100f);
        if (_strength < .003f && _target == 0f) _strength = 0f;
        float age = MathF.Max(0f, (now - _startedAt) / (float)Stopwatch.Frequency);
        float rearGrain = (.64f + .36f * Grain(age, 36f)) * (.88f + .12f * Grain(age, 4.5f));
        float rear = _strength * rearGrain;
        float scale = Math.Clamp(gripScale, 0f, 1.5f);
        // The preserved rear-left emphasis is an EVENT label, not road-side
        // information or a physical direction of oversteer. A real slide may
        // dominate a simultaneous kerb; ordinary kerbs cannot open this gate.
        return new(_strength, Math.Clamp(rear * .52f * scale, 0f, 1f),
            Math.Clamp(rear * .31f * scale, 0f, 1f), _eventActive || _strength > 0f,
            _eventActive && _recovering);
    }

    private void UpdateTarget(in TelemetrySnapshot t, float dt, long now)
    {
        float rl = MathF.Abs(t.SlipAngleRl), rr = MathF.Abs(t.SlipAngleRr);
        float frontAngle = (MathF.Abs(t.SlipAngleFl) + MathF.Abs(t.SlipAngleFr)) * .5f;
        float rearAngle = (rl + rr) * .5f;
        float body = MathF.Abs(t.LocalVelocityX) / MathF.Max(t.LocalVelocityZ, 4f);
        float steer = MathF.Abs(t.SteerAngle), yaw = t.LocalYawRate;
        float speed = SmoothStep(14f, 35f, MathF.Abs(t.SpeedKmh));
        float turn = MathF.Max(SmoothStep(.035f, .14f, steer),
            MathF.Max(SmoothStep(.06f, .28f, MathF.Abs(yaw)), SmoothStep(.025f, .08f, body)));

        // Retain griploss.2's conservative rear EVENT qualification. A lone
        // wheel spike needs independent body-motion support; paired rear slip
        // may lead body motion. This is eligibility, not a second gain knob.
        float qualification = SmoothStep(.12f, .29f, rearAngle) * turn * speed
            * MathF.Max(SmoothStep(.025f, .075f, rearAngle - frontAngle),
                MathF.Max(SmoothStep(.070f, .17f, body), SmoothStep(.20f, .34f, rearAngle)));
        bool motionSupported = MathF.Min(rl, rr) >= .075f
            || (body >= .075f && rearAngle >= .14f);
        bool confirmed = qualification > .055f && motionSupported;
        _evidenceTime = confirmed ? _evidenceTime + dt : 0f;
        if (!_eventActive && _evidenceTime >= .012f)
        {
            _eventActive = true; _recovering = false;
            _startedAt = _confirmedAt = now;
        }

        if (_eventActive)
        {
            float bodyRate = (body - _previousBody) / dt;
            float rearRate = (rearAngle - _previousRear) / dt;
            float steerRate = (steer - _previousSteer) / dt;
            if (_motionPrimed && ((bodyRate < -.12f && rearRate < 0f)
                || (yaw * _previousYaw < 0f && body > .045f)
                || (steerRate < -2f && body > .045f)))
                _recovering = true;
            if (_motionPrimed && bodyRate > 1.5f && rearRate > 2f)
                _recovering = false;

            if (confirmed) _confirmedAt = now;
            float unconfirmedAge = MathF.Max(0f, (now - _confirmedAt) / (float)Stopwatch.Frequency);
            // Preserve .1's rear strength mapping while the gate is open;
            // don't substitute the smaller .2 qualification as voice strength.
            float rear = SmoothStep(.10f, .27f, rearAngle)
                * MathF.Max(SmoothStep(.018f, .065f, rearAngle - frontAngle),
                    MathF.Max(SmoothStep(.045f, .12f, body), SmoothStep(.18f, .32f, rearAngle)))
                * turn * speed;
            float recovery = SmoothStep(.055f, .17f, rearAngle)
                * SmoothStep(.025f, .075f, body) * .68f * speed
                * (1f - SmoothStep(.040f, .180f, unconfirmedAge));
            _target = MathF.Max(rear, recovery);
            if (!confirmed && now - _confirmedAt >= RecoveryTicks)
            {
                _eventActive = _recovering = false;
                _target = 0f;
            }
        }
        else _target = 0f;

        _previousBody = body; _previousRear = rearAngle;
        _previousYaw = yaw; _previousSteer = steer; _motionPrimed = true;
    }

    private static bool ValidMotion(in TelemetrySnapshot t)
        => float.IsFinite(t.SpeedKmh) && MathF.Abs(t.SpeedKmh) >= 14f
        && float.IsFinite(t.LocalVelocityX) && float.IsFinite(t.LocalVelocityZ) && t.LocalVelocityZ > 2f
        && float.IsFinite(t.SlipAngleFl) && float.IsFinite(t.SlipAngleFr)
        && float.IsFinite(t.SlipAngleRl) && float.IsFinite(t.SlipAngleRr)
        && float.IsFinite(t.SteerAngle) && float.IsFinite(t.LocalYawRate);

    private void ClearDynamics()
    {
        _motionPrimed = _eventActive = _recovering = false;
        _target = _strength = _evidenceTime = 0f;
        _startedAt = _confirmedAt = 0;
        _previousBody = _previousRear = _previousYaw = _previousSteer = 0f;
    }

    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);
    private static float Grain(float age, float hz) => .5f + .5f * MathF.Cos(age * hz * MathF.Tau);
    private static float Follow(float current, float target, float dt, float attack, float release)
        => current + (target - current) * (1f - MathF.Exp(-dt / (target > current ? attack : release)));
    private static float SmoothStep(float low, float high, float value)
    {
        float x = Math.Clamp((value - low) / (high - low), 0f, 1f);
        return x * x * (3f - 2f * x);
    }
}
