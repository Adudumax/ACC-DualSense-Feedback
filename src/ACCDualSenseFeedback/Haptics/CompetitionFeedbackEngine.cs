using System.Diagnostics;
using ACCDualSenseFeedback.Hardware.DualSense;
using ACCDualSenseFeedback.Telemetry;

namespace ACCDualSenseFeedback.Haptics;

// Telemetry owns spatial information. ACC's unlabeled XInput rumble is retained
// only as a quiet, centered detail layer so it cannot force every kerb into the
// low-frequency/left actuator.
internal sealed class CompetitionFeedbackEngine
{
    private static readonly byte[] BrakeZones = [3, 4, 4, 5, 5, 6, 6, 7, 7, 8];
    private static readonly byte[] ThrottleZones = [2, 2, 2, 3, 3, 3, 4, 4, 5, 5];

    // Crosstalk is intentionally tiny. The heavy/left legacy rumble channel is
    // much easier to feel than the light/right channel, so even an apparently
    // small numeric leak can make a right-side event feel left-biased.
    private const float SurfaceCrossfeed = 0.018f;
    private const float SlipCrossfeed = 0.018f;
    private const float OppositeGripLimit = 0.015f;
    private const float RightGripCompensation = 1.42f;

    private long _lastUpdate;
    private float _absEnvelope;
    private float _lockEnvelope;
    private float _tcEnvelope;
    private float _wheelspinEnvelope;
    private float _redlineEnvelope;
    private float _shiftEnvelope;
    private int _previousGear = int.MinValue;
    private long _shiftUntil;

    private readonly float[] _loadReference = new float[4];
    private readonly float[] _previousSuspension = new float[4];
    private readonly float[] _wheelShockEnergy = new float[4];
    private readonly float[] _slipEnvelope = new float[4];
    private readonly float[] _slipRatioBaseline = [0.030f, 0.030f, 0.040f, 0.040f];
    private readonly float[] _slipAngleBaseline = [0.100f, 0.100f, 0.060f, 0.060f];
    private bool _adaptiveSlipPrimed;
    private bool _wheelDynamicsInitialized;
    private float _surfaceLeftEnvelope;
    private float _surfaceRightEnvelope;
    private float _surfaceSidePan;
    private float _surfaceAxle;

    private bool _nativeBaselineInitialized;
    private float _nativeLargeBaseline;
    private float _nativeSmallBaseline;

    private const int ImpactVelocityHistorySize = 96;
    private bool _impactInitialized;
    private int _impactLastPacketId;
    private readonly float[] _previousCarDamage = new float[5];
    private readonly long[] _impactVelocityTimestamps = new long[ImpactVelocityHistorySize];
    private readonly float[] _impactVelocityX = new float[ImpactVelocityHistorySize];
    private readonly float[] _impactVelocityY = new float[ImpactVelocityHistorySize];
    private readonly float[] _impactVelocityZ = new float[ImpactVelocityHistorySize];
    private int _impactVelocityWriteIndex;
    private int _impactVelocityCount;
    private long _impactCandidateUntil;
    private float _impactCandidatePeakG;
    private float _impactCandidateGx;
    private float _impactCandidateGz;
    private float _impactCandidateRoughRoad;
    private long _impactDamagePendingUntil;
    private float _impactPendingDamageDelta;
    private long _impactStartedAt;
    private long _impactCooldownUntil;
    private float _impactStrength;
    private float _impactPan;
    private long _scrapeLastAt;
    private float _scrapeStrength;
    private float _scrapePan;

    public FeedbackFrame Compute(
        in TelemetrySnapshot t,
        long now,
        byte originalLargeMotor = 0,
        byte originalSmallMotor = 0)
        => Compute(t, now, originalLargeMotor, originalSmallMotor, FeedbackTuning.Neutral);

    public FeedbackFrame Compute(
        in TelemetrySnapshot t,
        long now,
        byte originalLargeMotor,
        byte originalSmallMotor,
        in FeedbackTuning tuning)
    {
        float deltaSeconds = _lastUpdate == 0
            ? 0.004f
            : Math.Clamp((now - _lastUpdate) / (float)Stopwatch.Frequency, 0.001f, 0.050f);
        _lastUpdate = now;

        float speedFade = SmoothStep(4f, 28f, MathF.Abs(t.SpeedKmh));
        float gas = Clamp01(t.Gas);
        float brake = Clamp01(t.Brake);
        bool absInterventionReported = MathF.Abs(t.Abs) > 0.01f || MathF.Abs(t.AbsVibration) > 0.035f;
        bool tcInterventionReported = MathF.Abs(t.Tc) > 0.01f;
        bool absEnabled = t.AbsLevel > 0.01f || absInterventionReported;
        bool tcEnabled = t.TcLevel > 0.01f || tcInterventionReported;

        UpdateAdaptiveSlipBaseline(t, deltaSeconds, speedFade);

        float frontLongitudinalSlip = MathF.Max(
            SmoothStep(RatioStart(0), RatioEnd(0), MathF.Abs(t.SlipRatioFl)),
            SmoothStep(RatioStart(1), RatioEnd(1), MathF.Abs(t.SlipRatioFr)));
        float frontLateralSlip = MathF.Max(
            SmoothStep(AngleStart(0), AngleEnd(0), MathF.Abs(t.SlipAngleFl)),
            SmoothStep(AngleStart(1), AngleEnd(1), MathF.Abs(t.SlipAngleFr)));
        float rearLongitudinalSlip = MathF.Max(
            SmoothStep(RatioStart(2), RatioEnd(2), MathF.Abs(t.SlipRatioRl)),
            SmoothStep(RatioStart(3), RatioEnd(3), MathF.Abs(t.SlipRatioRr)));
        float rearLateralSlip = MathF.Max(
            SmoothStep(AngleStart(2), AngleEnd(2), MathF.Abs(t.SlipAngleRl)),
            SmoothStep(AngleStart(3), AngleEnd(3), MathF.Abs(t.SlipAngleRr)));

        float frontSlip = MathF.Max(frontLongitudinalSlip, frontLateralSlip * 0.72f) * speedFade;
        float rearSlip = MathF.Max(rearLongitudinalSlip, rearLateralSlip * 0.78f)
            * SmoothStep(0.24f, 0.70f, gas)
            * speedFade;

        float brakeGate = SmoothStep(0.16f, 0.48f, brake) * speedFade;
        float electronicAbs = MathF.Max(
            Clamp01(MathF.Abs(t.Abs)),
            SmoothStep(0.045f, 0.32f, MathF.Abs(t.AbsVibration))) * brakeGate;
        float frontLock = MathF.Max(
            SmoothStep(RatioStart(0) + 0.035f, RatioEnd(0), MathF.Abs(t.SlipRatioFl)),
            SmoothStep(RatioStart(1) + 0.035f, RatioEnd(1), MathF.Abs(t.SlipRatioFr)));
        float rearLock = MathF.Max(
            SmoothStep(RatioStart(2) + 0.035f, RatioEnd(2), MathF.Abs(t.SlipRatioRl)),
            SmoothStep(RatioStart(3) + 0.035f, RatioEnd(3), MathF.Abs(t.SlipRatioRr)));
        float mechanicalLock = MathF.Max(frontLock, rearLock * 0.82f) * brakeGate;

        float electronicTc = Clamp01(MathF.Abs(t.Tc))
            * SmoothStep(0.18f, 0.50f, gas)
            * speedFade;
        float mechanicalWheelspin = rearSlip;

        float absTarget = absEnabled ? MathF.Max(electronicAbs, mechanicalLock * 0.28f) : 0f;
        float lockTarget = absEnabled ? 0f : mechanicalLock;
        float tcTarget = tcEnabled ? MathF.Max(electronicTc, mechanicalWheelspin * 0.22f) : 0f;
        float wheelspinTarget = tcEnabled ? 0f : mechanicalWheelspin;

        float rpmRatio = t.CurrentMaxRpm > 1000
            ? Clamp01(t.Rpm / (float)t.CurrentMaxRpm)
            : 0f;
        float redlineStart = Math.Clamp(tuning.RedlineStartRatio, 0.94f, 0.99f);
        float redlineEnd = MathF.Min(0.999f, MathF.Max(0.997f, redlineStart + 0.015f));
        float redline = SmoothStep(redlineStart, redlineEnd, rpmRatio)
            * SmoothStep(0.78f, 0.97f, gas);

        if (_previousGear != int.MinValue &&
            t.Gear > _previousGear &&
            t.SpeedKmh > 8f &&
            gas > 0.18f)
            _shiftUntil = now + (long)(0.115 * Stopwatch.Frequency);
        _previousGear = t.Gear;
        float shiftTarget = now < _shiftUntil ? 1f : 0f;

        _absEnvelope = FollowEnvelope(_absEnvelope, absTarget, deltaSeconds, 0.032f, 0.105f);
        _lockEnvelope = FollowEnvelope(_lockEnvelope, lockTarget, deltaSeconds, 0.050f, 0.135f);
        _tcEnvelope = FollowEnvelope(_tcEnvelope, tcTarget, deltaSeconds, 0.060f, 0.135f);
        _wheelspinEnvelope = FollowEnvelope(
            _wheelspinEnvelope, wheelspinTarget, deltaSeconds, 0.085f, 0.175f);
        _redlineEnvelope = FollowEnvelope(_redlineEnvelope, redline, deltaSeconds, 0.052f, 0.110f);
        _shiftEnvelope = FollowEnvelope(_shiftEnvelope, shiftTarget, deltaSeconds, 0.010f, 0.052f);

        UpdateWheelDynamics(t, deltaSeconds, speedFade);
        UpdateSlipEnvelopes(t, deltaSeconds, speedFade);
        UpdateImpactDetector(t, now);

        float absCue = tuning.AbsPulseScale > 0f ? _absEnvelope : 0f;
        float lockCue = tuning.LockPulseScale > 0f ? _lockEnvelope : 0f;
        TriggerEffect leftTrigger = absCue >= lockCue
            ? BuildEventPedal(
                BrakeZones,
                tuning.BrakeResistanceScale,
                absCue,
                tuning.AbsPulseScale,
                2, 2, 5, 15)
            : BuildEventPedal(
                BrakeZones,
                tuning.BrakeResistanceScale,
                lockCue,
                tuning.LockPulseScale,
                2, 2, 6, 8);

        float tcCue = tuning.TcPulseScale > 0f ? _tcEnvelope : 0f;
        float wheelspinCue = tuning.WheelspinPulseScale > 0f ? _wheelspinEnvelope : 0f;
        float redlineCue = tuning.RedlinePulseScale > 0f ? _redlineEnvelope : 0f;
        TriggerEffect rightTrigger;
        if (_shiftEnvelope > 0.06f && tuning.ShiftKickScale > 0f)
            rightTrigger = BuildEventPedal(
                ThrottleZones,
                tuning.ThrottleResistanceScale,
                _shiftEnvelope,
                tuning.ShiftKickScale,
                1, 2, 5, 28);
        else if (tcCue >= wheelspinCue && tcCue >= redlineCue * 0.85f)
            rightTrigger = BuildEventPedal(
                ThrottleZones,
                tuning.ThrottleResistanceScale,
                tcCue,
                tuning.TcPulseScale,
                2, 1, 4, 12);
        else if (wheelspinCue >= redlineCue * 0.85f)
            rightTrigger = BuildEventPedal(
                ThrottleZones,
                tuning.ThrottleResistanceScale,
                wheelspinCue,
                tuning.WheelspinPulseScale,
                2, 2, 5, 7);
        else
            rightTrigger = BuildEventPedal(
                ThrottleZones,
                tuning.ThrottleResistanceScale,
                redlineCue,
                tuning.RedlinePulseScale,
                1, 2, 4, 18);

        double seconds = now / (double)Stopwatch.Frequency;

        // Front/rear information is encoded as texture because the controller
        // has two actuators, not four. A small frequency change was too easy to
        // miss in hand, so the signatures now differ in both cadence and pulse
        // shape: front = quick/sharp buzz, rear = slow/heavy grouped thumps.
        float frontSurfaceWave = PulseWave(seconds, 48f, 0.18f, 1.45f);
        float rearSurfaceWave = PulseWave(seconds, 13f, 0.08f, 3.20f);
        float frontShare = 0.5f + 0.5f * _surfaceAxle;
        float surfaceWave = Lerp(rearSurfaceWave, frontSurfaceWave, frontShare);
        float surfaceLeftDirect = _surfaceLeftEnvelope * surfaceWave * 0.54f;
        float surfaceRightDirect = _surfaceRightEnvelope * surfaceWave * 0.54f;
        float surfaceLeft = MathF.Max(surfaceLeftDirect, surfaceRightDirect * SurfaceCrossfeed)
            * tuning.RoadSurfaceScale;
        float surfaceRight = MathF.Max(surfaceRightDirect, surfaceLeftDirect * SurfaceCrossfeed)
            * tuning.RoadSurfaceScale;

        float frontSlipWave = PulseWave(seconds, 76f, 0.14f, 1.35f);
        float rearSlipWave = PulseWave(seconds, 16f, 0.06f, 3.60f);
        float slipFl = _slipEnvelope[0] * frontSlipWave * 0.58f;
        float slipFr = _slipEnvelope[1] * frontSlipWave * 0.58f;
        float slipRl = _slipEnvelope[2] * rearSlipWave * 0.68f;
        float slipRr = _slipEnvelope[3] * rearSlipWave * 0.68f;
        float slipLeftDirect = MathF.Max(slipFl, slipRl);
        float slipRightDirect = MathF.Max(slipFr, slipRr);
        float slipLeft = MathF.Max(slipLeftDirect, slipRightDirect * SlipCrossfeed)
            * tuning.GripLossScale;
        float slipRight = MathF.Max(slipRightDirect, slipLeftDirect * SlipCrossfeed)
            * tuning.GripLossScale;

        float strongestSurface = MathF.Max(_surfaceLeftEnvelope, _surfaceRightEnvelope);
        float strongestSlip = Max4(_slipEnvelope[0], _slipEnvelope[1], _slipEnvelope[2], _slipEnvelope[3]);
        float informationCue = MathF.Max(strongestSurface, strongestSlip);

        // Track ACC's slow native component so an unlabeled transient can be
        // removed from the wrong side and re-routed during a spatial event.
        // Outside a confirmed information event the raw native channels pass
        // through unchanged; this is what preserves the natural continuity and
        // small road/chassis details of the native-first v0.3 behavior.
        float largeNormalized = originalLargeMotor / 255f;
        float smallNormalized = originalSmallMotor / 255f;
        if (!_nativeBaselineInitialized)
        {
            _nativeLargeBaseline = largeNormalized;
            _nativeSmallBaseline = smallNormalized;
            _nativeBaselineInitialized = true;
        }

        float largeTransient = MathF.Abs(largeNormalized - _nativeLargeBaseline);
        float smallTransient = MathF.Abs(smallNormalized - _nativeSmallBaseline);
        if (informationCue < 0.08f)
        {
            _nativeLargeBaseline = FollowEnvelope(
                _nativeLargeBaseline, largeNormalized, deltaSeconds, 0.120f, 0.070f);
            _nativeSmallBaseline = FollowEnvelope(
                _nativeSmallBaseline, smallNormalized, deltaSeconds, 0.100f, 0.060f);
        }
        else
        {
            _nativeLargeBaseline = MathF.Min(_nativeLargeBaseline, largeNormalized);
            _nativeSmallBaseline = MathF.Min(_nativeSmallBaseline, smallNormalized);
        }

        // Ownership comes from the slow envelopes, not the pulsed carrier.
        // Otherwise the wrong grip reappears every time the intended texture
        // reaches a trough, which feels like a strong left buzz under a right
        // kerb even though the average numbers look correctly panned.
        float leftSpatial = MathF.Max(
            _surfaceLeftEnvelope,
            MathF.Max(_slipEnvelope[0], _slipEnvelope[2]));
        float rightSpatial = MathF.Max(
            _surfaceRightEnvelope,
            MathF.Max(_slipEnvelope[1], _slipEnvelope[3]));
        float spatialTotal = leftSpatial + rightSpatial;
        float spatialBalance = spatialTotal > 0.001f
            ? (rightSpatial - leftSpatial) / spatialTotal
            : 0f;
        float ownershipStrength = SmoothStep(
            0.035f, 0.30f, MathF.Max(leftSpatial, rightSpatial));
        float rightOwnership = MathF.Max(0f, spatialBalance)
            * SmoothStep(0.08f, 0.34f, MathF.Abs(spatialBalance))
            * ownershipStrength;
        float leftOwnership = MathF.Max(0f, -spatialBalance)
            * SmoothStep(0.08f, 0.34f, MathF.Abs(spatialBalance))
            * ownershipStrength;
        float rightIsolation = SmoothStep(0.12f, 0.70f, rightOwnership);
        float leftIsolation = SmoothStep(0.12f, 0.70f, leftOwnership);

        // Native-first mixer:
        //  * calm/unclassified driving keeps ACC's raw waveform bit-for-bit;
        //  * centered grip events reserve only modest headroom for telemetry;
        //  * one-sided events strip the unlabeled transient toward its slow
        //    baseline, then route useful transient energy to the owning grip;
        //  * only the conflicting side is deeply suppressed.
        float oneSidedIsolation = MathF.Max(leftIsolation, rightIsolation);
        float centeredPriority = SmoothStep(0.08f, 0.62f, informationCue)
            * (1f - oneSidedIsolation);
        float centeredHeadroom = 0.18f * centeredPriority;
        float stripToBaseline = 0.88f * oneSidedIsolation;

        float nativeLeft = largeNormalized * (1f - centeredHeadroom);
        float nativeRight = smallNormalized * (1f - centeredHeadroom);
        // A symmetric grip event must not inherit the arbitrary low/high motor
        // imbalance of XInput. Preserve total native energy and texture, but
        // center it before the symmetric telemetry cue is added.
        float centeredNative = (nativeLeft + nativeRight) * 0.5f;
        float centerBlend = 0.92f * centeredPriority;
        nativeLeft = Lerp(nativeLeft, centeredNative, centerBlend);
        nativeRight = Lerp(nativeRight, centeredNative, centerBlend);
        nativeLeft = Lerp(nativeLeft, _nativeLargeBaseline, stripToBaseline);
        nativeRight = Lerp(nativeRight, _nativeSmallBaseline, stripToBaseline);

        nativeLeft *= 1f - rightIsolation * 0.985f;
        nativeRight *= 1f - leftIsolation * 0.985f;
        // Keep most of the native texture on the event-owning side while still
        // making room for the explicitly localized wheel cue.
        nativeLeft *= 1f - leftIsolation * 0.14f;
        nativeRight *= 1f - rightIsolation * 0.14f;
        nativeLeft *= tuning.NativeAccScale;
        nativeRight *= tuning.NativeAccScale;

        float nativeMicroDetail = MathF.Max(
            largeTransient * 0.58f,
            smallTransient * 0.68f);
        float nativeImpactDetail = SmoothStep(
            0.20f, 0.82f, MathF.Max(largeTransient, smallTransient)) * 0.48f;
        float routedNativeDetail = MathF.Max(nativeMicroDetail, nativeImpactDetail)
            * oneSidedIsolation;
        float nativeDetailLeft = routedNativeDetail * leftIsolation * tuning.NativeAccScale;
        float nativeDetailRight = routedNativeDetail * rightIsolation * tuning.NativeAccScale;

        float absBody = _absEnvelope * Wave(seconds, 16f, 0.24f) * 0.13f * tuning.AbsPulseScale;
        float lockBody = _lockEnvelope * Wave(seconds, 8f, 0.34f) * 0.16f * tuning.LockPulseScale;
        float shiftBody = _shiftEnvelope * Wave(seconds, 32f, 0.42f) * 0.26f * tuning.ShiftKickScale;

        // Build the directional layer first, then impose a final isolation cap.
        // This is deliberately after native-detail merging: otherwise ACC's
        // large/low-frequency motor can leak back into the wrong grip.
        float leftOutput = nativeLeft;
        leftOutput = SoftCombine(leftOutput, nativeDetailLeft);
        leftOutput = SoftCombine(leftOutput, surfaceLeft);
        leftOutput = SoftCombine(leftOutput, slipLeft);

        float rightOutput = nativeRight;
        rightOutput = SoftCombine(rightOutput, nativeDetailRight);
        float rightCueGain = Lerp(1f, RightGripCompensation, rightIsolation);
        rightOutput = SoftCombine(rightOutput, Clamp01(surfaceRight * rightCueGain));
        rightOutput = SoftCombine(rightOutput, Clamp01(slipRight * rightCueGain));

        if (rightIsolation > 0.01f)
        {
            float isolated = MathF.Min(leftOutput, rightOutput * OppositeGripLimit);
            leftOutput = Lerp(leftOutput, isolated, rightIsolation);
        }
        else if (leftIsolation > 0.01f)
        {
            float isolated = MathF.Min(rightOutput, leftOutput * OppositeGripLimit);
            rightOutput = Lerp(rightOutput, isolated, leftIsolation);
        }

        // ABS/lock and shift are centered vehicle events rather than wheel-side
        // information. Add them after spatial isolation so they retain their
        // intended whole-car character without contaminating kerb localization.
        leftOutput = SoftCombine(leftOutput, MathF.Max(absBody * 0.72f, lockBody * 0.82f));
        leftOutput = SoftCombine(leftOutput, shiftBody);
        rightOutput = SoftCombine(rightOutput, MathF.Max(absBody, lockBody));
        rightOutput = SoftCombine(rightOutput, shiftBody * 0.88f);

        // Engine character remains the quietest layer. The calibrated preview6
        // texture was retained unchanged, and still ducks under every handling
        // cue so it cannot mask tyre, kerb, ABS or shift information.
        float drivingPriority = Max4(
            informationCue,
            MathF.Max(_absEnvelope, _lockEnvelope),
            MathF.Max(_tcEnvelope, _wheelspinEnvelope),
            _shiftEnvelope);
        float quietOutput = 1f - SmoothStep(
            0.035f,
            0.16f,
            MathF.Max(leftOutput, rightOutput));
        float engineRoom = (1f - SmoothStep(0.035f, 0.30f, drivingPriority)) * quietOutput;
        BuildEngineTexture(
            t,
            seconds,
            engineRoom,
            tuning.EngineTextureScale,
            out float engineLeft,
            out float engineRight);
        leftOutput = SoftCombine(leftOutput, engineLeft);
        rightOutput = SoftCombine(rightOutput, engineRight);

        // A collision must read as a distinct hit instead of another continuous
        // rumble layer. Briefly reserve headroom, then add the directionally
        // routed impulse. Existing trigger and four-wheel estimators are not
        // changed by this late mixer stage.
        BuildImpactFeedback(
            now,
            tuning.CollisionFeedbackScale,
            out float impactLeft,
            out float impactRight,
            out float impactDuck);
        leftOutput = Clamp01(leftOutput * (1f - impactDuck) + impactLeft);
        rightOutput = Clamp01(rightOutput * (1f - impactDuck) + impactRight);

        return new FeedbackFrame(
            ToByte(leftOutput),
            ToByte(rightOutput),
            leftTrigger,
            rightTrigger);
    }

    private static TriggerEffect BuildEventPedal(
        ReadOnlySpan<byte> baseline,
        float baselineScale,
        float envelope,
        float eventScale,
        byte startPosition,
        byte minimumAmplitude,
        byte maximumAmplitude,
        byte frequency)
    {
        TriggerEffect restingEffect = FeedbackTuning.ScaleTrigger(
            TriggerEffect.FeedbackZones(baseline),
            baselineScale);
        if (eventScale <= 0f || envelope < 0.035f)
            return restingEffect;

        float developed = SmoothStep(0.035f, 0.75f, envelope);
        int calibratedAmplitude = Math.Clamp(
            minimumAmplitude + (int)MathF.Floor(
                developed * (maximumAmplitude - minimumAmplitude + 1)),
            minimumAmplitude,
            maximumAmplitude);
        byte amplitude = (byte)Math.Clamp(
            (int)MathF.Round(calibratedAmplitude * eventScale),
            1,
            8);
        return TriggerEffect.Vibration(startPosition, amplitude, frequency);
    }

    private void UpdateImpactDetector(in TelemetrySnapshot t, long now)
    {
        if (_impactInitialized && t.PacketId == _impactLastPacketId)
            return;

        Span<float> damage = stackalloc float[5]
        {
            PositiveFinite(t.CarDamageFront),
            PositiveFinite(t.CarDamageRear),
            PositiveFinite(t.CarDamageLeft),
            PositiveFinite(t.CarDamageRight),
            PositiveFinite(t.CarDamageCenter)
        };

        bool discontinuity = _impactInitialized &&
            (t.PacketId < _impactLastPacketId || t.PacketId - _impactLastPacketId > 2000);
        if (!_impactInitialized || discontinuity)
        {
            ResetImpactTracking(t, damage, now);
            _impactInitialized = true;
            _impactLastPacketId = t.PacketId;
            return;
        }

        float damageDelta = 0f;
        for (int i = 0; i < damage.Length; i++)
        {
            damageDelta = MathF.Max(damageDelta, MathF.Max(0f, damage[i] - _previousCarDamage[i]));
            _previousCarDamage[i] = damage[i];
        }

        float gx = FiniteOrZero(t.GForceX);
        float gy = FiniteOrZero(t.GForceY);
        float gz = FiniteOrZero(t.GForceZ);
        float gMagnitude = MathF.Sqrt(gx * gx + gy * gy + gz * gz);
        float roughRoad = MathF.Max(
            SmoothStep(0.010f, 0.090f, MathF.Abs(t.KerbVibration)),
            SmoothStep(0.22f, 1.20f, MathF.Abs(t.GVibration)));

        if (now > _impactCandidateUntil)
            ClearImpactCandidate();
        if (gMagnitude >= 3f)
        {
            if (gMagnitude > _impactCandidatePeakG)
            {
                _impactCandidatePeakG = gMagnitude;
                _impactCandidateGx = gx;
                _impactCandidateGz = gz;
            }
            _impactCandidateRoughRoad = MathF.Max(_impactCandidateRoughRoad, roughRoad);
            _impactCandidateUntil = now + (long)(0.125 * Stopwatch.Frequency);
        }

        if (damageDelta > 0.02f)
        {
            if (_impactDamagePendingUntil == 0)
                _impactDamagePendingUntil = now + (long)(0.012 * Stopwatch.Frequency);
            _impactPendingDamageDelta = MathF.Max(_impactPendingDamageDelta, damageDelta);
            UpdateScrapeContact(damageDelta, gx, gz, now);
        }

        float velocityDelta = TryGetDelayedVelocity(now, out float oldVx, out float oldVy, out float oldVz)
            ? VectorDistance(
                t.LocalVelocityX, t.LocalVelocityY, t.LocalVelocityZ,
                oldVx, oldVy, oldVz)
            : 0f;

        bool damageConfirmed = _impactDamagePendingUntil != 0 && now >= _impactDamagePendingUntil;
        bool extremeKinematicImpact = _impactCandidatePeakG >= 20f;
        float requiredVelocityDelta = Lerp(5f, 6.5f, _impactCandidateRoughRoad);
        bool corroboratedKinematicImpact = _impactCandidatePeakG >= 6f &&
            velocityDelta >= requiredVelocityDelta;
        bool kinematicConfirmed = MathF.Abs(t.SpeedKmh) > 3f &&
            (extremeKinematicImpact || corroboratedKinematicImpact);

        if (damageConfirmed && now < _impactCooldownUntil)
            ClearPendingDamage();
        else if (now >= _impactCooldownUntil && (damageConfirmed || kinematicConfirmed))
        {
            float peakG = MathF.Max(gMagnitude, _impactCandidatePeakG);
            float damageSignal = SmoothStep(0.02f, 2.5f, _impactPendingDamageDelta);
            float gSignal = SmoothStep(3f, 18f, peakG);
            float velocitySignal = SmoothStep(2f, 10f, velocityDelta);
            _impactStrength = Clamp01(damageConfirmed
                ? 0.58f + damageSignal * 0.25f + gSignal * 0.17f
                : 0.62f + MathF.Max(gSignal, velocitySignal) * 0.38f);

            float directionGx = _impactCandidatePeakG > 0f ? _impactCandidateGx : gx;
            float directionGz = _impactCandidatePeakG > 0f ? _impactCandidateGz : gz;
            float rawPan = directionGx /
                (MathF.Abs(directionGx) + MathF.Abs(directionGz) * 0.50f + 0.001f);
            _impactPan = MathF.Sign(rawPan) * SmoothStep(0.12f, 0.62f, MathF.Abs(rawPan));
            _impactStartedAt = now;
            _impactCooldownUntil = now + (long)(0.220 * Stopwatch.Frequency);
            ClearImpactCandidate();
            ClearPendingDamage();
        }

        StoreImpactVelocity(t, now);
        _impactLastPacketId = t.PacketId;
    }

    private void ResetImpactTracking(in TelemetrySnapshot t, ReadOnlySpan<float> damage, long now)
    {
        for (int i = 0; i < damage.Length; i++)
            _previousCarDamage[i] = damage[i];
        Array.Clear(_impactVelocityTimestamps);
        _impactVelocityWriteIndex = 0;
        _impactVelocityCount = 0;
        ClearImpactCandidate();
        ClearPendingDamage();
        _scrapeLastAt = 0;
        _scrapeStrength = 0f;
        _scrapePan = 0f;
        StoreImpactVelocity(t, now);
    }

    private void ClearImpactCandidate()
    {
        _impactCandidateUntil = 0;
        _impactCandidatePeakG = 0f;
        _impactCandidateGx = 0f;
        _impactCandidateGz = 0f;
        _impactCandidateRoughRoad = 0f;
    }

    private void ClearPendingDamage()
    {
        _impactDamagePendingUntil = 0;
        _impactPendingDamageDelta = 0f;
    }

    private void UpdateScrapeContact(float damageDelta, float gx, float gz, long now)
    {
        float lateralG = MathF.Abs(gx);
        float directionalRatio = lateralG /
            (lateralG + MathF.Abs(gz) * 0.50f + 0.001f);
        if (lateralG < 1.5f || directionalRatio < 0.25f)
            return;

        float target = 0.30f +
            SmoothStep(1.5f, 7f, lateralG) * 0.24f +
            SmoothStep(0.02f, 2.5f, damageDelta) * 0.12f;
        _scrapeStrength = MathF.Max(_scrapeStrength * 0.82f, Clamp01(target));
        _scrapePan = MathF.Sign(gx) *
            SmoothStep(0.12f, 0.62f, directionalRatio);
        _scrapeLastAt = now;
    }

    private void StoreImpactVelocity(in TelemetrySnapshot t, long now)
    {
        int index = _impactVelocityWriteIndex;
        _impactVelocityTimestamps[index] = now;
        _impactVelocityX[index] = FiniteOrZero(t.LocalVelocityX);
        _impactVelocityY[index] = FiniteOrZero(t.LocalVelocityY);
        _impactVelocityZ[index] = FiniteOrZero(t.LocalVelocityZ);
        _impactVelocityWriteIndex = (index + 1) % ImpactVelocityHistorySize;
        _impactVelocityCount = Math.Min(_impactVelocityCount + 1, ImpactVelocityHistorySize);
    }

    private bool TryGetDelayedVelocity(long now, out float x, out float y, out float z)
    {
        int bestIndex = -1;
        long targetAge = (long)(0.100 * Stopwatch.Frequency);
        long minimumAge = (long)(0.075 * Stopwatch.Frequency);
        long maximumAge = (long)(0.150 * Stopwatch.Frequency);
        long bestDistance = long.MaxValue;

        for (int offset = 0; offset < _impactVelocityCount; offset++)
        {
            int index = (_impactVelocityWriteIndex - 1 - offset + ImpactVelocityHistorySize)
                % ImpactVelocityHistorySize;
            long age = now - _impactVelocityTimestamps[index];
            if (age < minimumAge)
                continue;
            if (age > maximumAge)
                break;
            long distance = Math.Abs(age - targetAge);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = index;
            }
        }

        if (bestIndex < 0)
        {
            x = y = z = 0f;
            return false;
        }

        x = _impactVelocityX[bestIndex];
        y = _impactVelocityY[bestIndex];
        z = _impactVelocityZ[bestIndex];
        return true;
    }

    private static void BuildEngineTexture(
        in TelemetrySnapshot t,
        double seconds,
        float availableRoom,
        float scale,
        out float left,
        out float right)
    {
        left = 0f;
        right = 0f;
        if (scale <= 0f || availableRoom <= 0.001f || t.IsEngineRunning == 0 ||
            t.CurrentMaxRpm <= 1000 || t.Rpm <= 350)
            return;

        float rpmRatio = Clamp01(t.Rpm / (float)t.CurrentMaxRpm);
        float rpmProgress = SmoothStep(0.36f, 0.995f, rpmRatio);
        float shapedRpm = MathF.Pow(rpmProgress, 2.05f);
        float redlineRise = SmoothStep(0.88f, 0.995f, rpmRatio);
        float redlineUnderLoad = redlineRise *
            SmoothStep(0.55f, 0.92f, Clamp01(t.Gas));
        float amplitude = (0.0032f + shapedRpm * 0.016f +
            redlineUnderLoad * 0.090f)
            * availableRoom
            * scale;

        float frequency = Lerp(17f, 31f, rpmRatio);
        float carrier = 0.84f
            + 0.10f * (float)Math.Sin(seconds * frequency * Math.Tau)
            + 0.06f * (float)Math.Sin(seconds * frequency * 0.6180339 * Math.Tau + 1.7);
        carrier = Math.Clamp(carrier, 0.68f, 1f);
        right = amplitude * carrier;
        float bilateralShare = 0.45f + rpmProgress * 0.15f + redlineUnderLoad * 0.20f;
        left = right * bilateralShare;
    }

    private void BuildImpactFeedback(
        long now,
        float scale,
        out float left,
        out float right,
        out float duck)
    {
        left = 0f;
        right = 0f;
        duck = 0f;
        if (scale <= 0f)
            return;

        float age = (now - _impactStartedAt) / (float)Stopwatch.Frequency;
        bool impactActive = _impactStartedAt != 0 && _impactStrength > 0f &&
            age >= 0f && age < 0.180f;
        if (_impactStartedAt != 0 && age >= 0.180f)
            _impactStrength = 0f;

        float impactCue = 0f;
        if (impactActive)
        {
            float crackDecay = MathF.Exp(-age / 0.018f);
            float thudDecay = MathF.Exp(-age / 0.072f);
            float crackTexture = 0.84f + 0.16f * Wave(age, 52f, 0f);
            float thudTexture = 0.70f + 0.30f * Wave(age, 12f, 0f);
            float reboundPosition = (age - 0.068f) / 0.026f;
            float rebound = MathF.Exp(-(reboundPosition * reboundPosition)) * 0.18f;
            impactCue = Clamp01(_impactStrength * (
                crackDecay * crackTexture * 0.34f +
                thudDecay * thudTexture * 0.58f +
                rebound) * scale);
        }

        RouteDirectionalImpact(impactCue, _impactPan, out float impactLeft, out float impactRight);

        float scrapeAge = (now - _scrapeLastAt) / (float)Stopwatch.Frequency;
        bool scrapeActive = _scrapeLastAt != 0 && _scrapeStrength > 0f &&
            scrapeAge >= 0f && scrapeAge < 0.140f;
        float scrapeCue = 0f;
        if (scrapeActive)
        {
            float scrapeDecay = MathF.Exp(-scrapeAge / 0.085f);
            float scrapeTexture = 0.76f + 0.24f * Wave(scrapeAge, 34f, 0.21f);
            scrapeCue = Clamp01(
                _scrapeStrength * scrapeDecay * scrapeTexture * 0.70f * scale);
        }
        else if (_scrapeLastAt != 0 && scrapeAge >= 0.140f)
        {
            _scrapeStrength = 0f;
        }

        RouteDirectionalImpact(scrapeCue, _scrapePan, out float scrapeLeft, out float scrapeRight);
        left = MathF.Max(impactLeft, scrapeLeft);
        right = MathF.Max(impactRight, scrapeRight);

        float strongestCue = MathF.Max(impactCue, scrapeCue);
        float directionality = MathF.Max(
            impactActive ? MathF.Abs(_impactPan) : 0f,
            scrapeActive ? MathF.Abs(_scrapePan) : 0f);
        duck = SmoothStep(0.10f, 0.55f, strongestCue) *
            Lerp(0.34f, 0.44f, directionality);
    }

    private static void RouteDirectionalImpact(
        float cue,
        float pan,
        out float left,
        out float right)
    {
        if (cue <= 0f)
        {
            left = right = 0f;
            return;
        }

        float directionality = MathF.Abs(pan);
        float oppositeShare = Lerp(1f, 0.42f, directionality);
        float energyGain = 2f / (1f + oppositeShare);
        float routed = Clamp01(cue * energyGain);
        left = routed * (pan > 0f ? oppositeShare : 1f);
        right = routed * (pan < 0f ? oppositeShare : 1f);
    }

    private static float VectorDistance(
        float x,
        float y,
        float z,
        float previousX,
        float previousY,
        float previousZ)
    {
        float dx = FiniteOrZero(x) - previousX;
        float dy = FiniteOrZero(y) - previousY;
        float dz = FiniteOrZero(z) - previousZ;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private void UpdateWheelDynamics(in TelemetrySnapshot t, float deltaSeconds, float speedFade)
    {
        Span<float> loads = stackalloc float[4]
        {
            PositiveFinite(t.WheelLoadFl),
            PositiveFinite(t.WheelLoadFr),
            PositiveFinite(t.WheelLoadRl),
            PositiveFinite(t.WheelLoadRr)
        };
        Span<float> suspension = stackalloc float[4]
        {
            FiniteOrZero(t.SuspensionFl),
            FiniteOrZero(t.SuspensionFr),
            FiniteOrZero(t.SuspensionRl),
            FiniteOrZero(t.SuspensionRr)
        };
        Span<float> dirt = stackalloc float[4]
        {
            Clamp01(t.TyreDirtyFl),
            Clamp01(t.TyreDirtyFr),
            Clamp01(t.TyreDirtyRl),
            Clamp01(t.TyreDirtyRr)
        };
        Span<float> loadK = stackalloc float[4];
        Span<float> evidence = stackalloc float[4];

        float loadSum = loads[0] + loads[1] + loads[2] + loads[3];
        float averageLoad = loadSum > 1f ? loadSum * 0.25f : 0f;
        for (int i = 0; i < 4; i++)
            loadK[i] = averageLoad > 1f ? Math.Clamp(loads[i] / averageLoad, 0f, 2.5f) : 1f;

        if (!_wheelDynamicsInitialized)
        {
            for (int i = 0; i < 4; i++)
            {
                _loadReference[i] = loadK[i];
                _previousSuspension[i] = suspension[i];
            }
            _wheelDynamicsInitialized = true;
        }

        float loadAlpha = Math.Clamp(deltaSeconds * 36f, 0f, 1f);
        for (int i = 0; i < 4; i++)
        {
            float loadDelta = MathF.Abs(loadK[i] - _loadReference[i]);
            float loadShock = SmoothStep(0.018f, 0.20f, loadDelta);
            float suspensionDelta = MathF.Abs(suspension[i] - _previousSuspension[i]);
            float suspensionShock = SmoothStep(0.00035f, 0.0045f, suspensionDelta);
            float instantaneousShock = MathF.Max(loadShock, suspensionShock * 0.84f) * speedFade;
            // Kerbs shake the whole chassis, so the largest instantaneous wheel
            // can alternate sides from one physics packet to the next. Integrate
            // a short shock-energy window per wheel before deciding direction.
            _wheelShockEnergy[i] = FollowEnvelope(
                _wheelShockEnergy[i], instantaneousShock, deltaSeconds, 0.004f, 0.070f);
            evidence[i] = _wheelShockEnergy[i];
            _loadReference[i] += (loadK[i] - _loadReference[i]) * loadAlpha;
            _previousSuspension[i] = suspension[i];
        }

        float leftEvidence = MathF.Max(evidence[0], evidence[2])
            + MathF.Min(evidence[0], evidence[2]) * 0.38f;
        float rightEvidence = MathF.Max(evidence[1], evidence[3])
            + MathF.Min(evidence[1], evidence[3]) * 0.38f;
        float evidenceTotal = leftEvidence + rightEvidence;
        float shockSide = evidenceTotal > 0.001f
            ? (rightEvidence - leftEvidence) / evidenceTotal
            : 0f;

        float offTrackGate = t.NumberOfTyresOut > 0 ? 1f : 0f;
        float dirtFl = SmoothStep(0.015f, 0.28f, dirt[0]) * offTrackGate;
        float dirtFr = SmoothStep(0.015f, 0.28f, dirt[1]) * offTrackGate;
        float dirtRl = SmoothStep(0.015f, 0.28f, dirt[2]) * offTrackGate;
        float dirtRr = SmoothStep(0.015f, 0.28f, dirt[3]) * offTrackGate;
        float dirtLeft = MathF.Max(dirtFl, dirtRl);
        float dirtRight = MathF.Max(dirtFr, dirtRr);
        float dirtTotal = dirtLeft + dirtRight;
        float dirtSide = dirtTotal > 0.001f ? (dirtRight - dirtLeft) / dirtTotal : 0f;

        bool dirtOwnsDirection = MathF.Max(dirtLeft, dirtRight) > 0.08f;
        float rawSide = dirtOwnsDirection ? dirtSide : shockSide;
        float sideEvidence = MathF.Max(
            MathF.Max(leftEvidence, rightEvidence),
            MathF.Max(dirtLeft, dirtRight));
        float requestedSidePan = MathF.Sign(rawSide)
            * SmoothStep(0.025f, 0.18f, MathF.Abs(rawSide))
            * SmoothStep(0.035f, 0.28f, sideEvidence);
        if (MathF.Abs(requestedSidePan) > 0.08f)
            _surfaceSidePan = FollowSigned(_surfaceSidePan, requestedSidePan, deltaSeconds, 0.006f);
        else
            _surfaceSidePan = FollowSigned(_surfaceSidePan, 0f, deltaSeconds, 0.090f);
        float sidePan = _surfaceSidePan;

        float frontEvidence;
        float rearEvidence;
        if (sidePan > 0.20f)
        {
            frontEvidence = MathF.Max(evidence[1], dirtFr);
            rearEvidence = MathF.Max(evidence[3], dirtRr);
        }
        else if (sidePan < -0.20f)
        {
            frontEvidence = MathF.Max(evidence[0], dirtFl);
            rearEvidence = MathF.Max(evidence[2], dirtRl);
        }
        else
        {
            frontEvidence = MathF.Max(MathF.Max(evidence[0], evidence[1]), MathF.Max(dirtFl, dirtFr));
            rearEvidence = MathF.Max(MathF.Max(evidence[2], evidence[3]), MathF.Max(dirtRl, dirtRr));
        }

        float axleTotal = frontEvidence + rearEvidence;
        float rawAxle = axleTotal > 0.001f
            ? (frontEvidence - rearEvidence) / axleTotal
            : 0f;
        float axlePan = MathF.Sign(rawAxle) * SmoothStep(0.06f, 0.32f, MathF.Abs(rawAxle));

        // Real ACC capture peaks around 0.10 for a strong kerb, not 1.0.
        float kerb = SmoothStep(0.010f, 0.090f, MathF.Abs(t.KerbVibration)) * speedFade;
        // WheelLoad/tyre-dirt/tyres-out are zero in current ACC builds. The
        // native G-vibration channel reliably rises on grass and rough ground;
        // suspension movement supplies its otherwise missing side information.
        float roughSurface = SmoothStep(0.22f, 1.20f, MathF.Abs(t.GVibration)) * speedFade;
        float ordinaryRoad = MathF.Max(leftEvidence, rightEvidence) * 0.08f;
        float surfaceStrength = MathF.Max(
            MathF.Max(kerb, roughSurface),
            MathF.Max(ordinaryRoad, MathF.Max(dirtLeft, dirtRight)));

        // pan=0 is a centered event; pan=+1 is an isolated right event. Do not
        // let secondary suspension movement on the body re-open the wrong side
        // once one wheel/side has confidently taken ownership.
        float leftShare = sidePan > 0f
            ? Lerp(1f, OppositeGripLimit, sidePan)
            : 1f;
        float rightShare = sidePan < 0f
            ? Lerp(1f, OppositeGripLimit, -sidePan)
            : 1f;
        float leftTarget = MathF.Max(surfaceStrength, MathF.Max(dirtLeft, leftEvidence * 0.20f))
            * leftShare;
        float rightTarget = MathF.Max(surfaceStrength, MathF.Max(dirtRight, rightEvidence * 0.20f))
            * rightShare;

        _surfaceLeftEnvelope = FollowEnvelope(
            _surfaceLeftEnvelope, leftTarget, deltaSeconds, 0.006f, 0.060f);
        _surfaceRightEnvelope = FollowEnvelope(
            _surfaceRightEnvelope, rightTarget, deltaSeconds, 0.006f, 0.060f);
        _surfaceAxle = MathF.Abs(axlePan) > 0.03f
            ? FollowSigned(_surfaceAxle, axlePan, deltaSeconds, 0.012f)
            : FollowSigned(_surfaceAxle, 0f, deltaSeconds, 0.120f);
    }

    private void UpdateSlipEnvelopes(in TelemetrySnapshot t, float deltaSeconds, float speedFade)
    {
        Span<float> wheelSlip = stackalloc float[4]
        {
            MathF.Abs(FiniteOrZero(t.WheelSlipFl)),
            MathF.Abs(FiniteOrZero(t.WheelSlipFr)),
            MathF.Abs(FiniteOrZero(t.WheelSlipRl)),
            MathF.Abs(FiniteOrZero(t.WheelSlipRr))
        };
        Span<float> slipRatio = stackalloc float[4]
        {
            MathF.Abs(FiniteOrZero(t.SlipRatioFl)),
            MathF.Abs(FiniteOrZero(t.SlipRatioFr)),
            MathF.Abs(FiniteOrZero(t.SlipRatioRl)),
            MathF.Abs(FiniteOrZero(t.SlipRatioRr))
        };
        Span<float> slipAngle = stackalloc float[4]
        {
            MathF.Abs(FiniteOrZero(t.SlipAngleFl)),
            MathF.Abs(FiniteOrZero(t.SlipAngleFr)),
            MathF.Abs(FiniteOrZero(t.SlipAngleRl)),
            MathF.Abs(FiniteOrZero(t.SlipAngleRr))
        };
        Span<float> loads = stackalloc float[4]
        {
            PositiveFinite(t.WheelLoadFl),
            PositiveFinite(t.WheelLoadFr),
            PositiveFinite(t.WheelLoadRl),
            PositiveFinite(t.WheelLoadRr)
        };

        float loadSum = loads[0] + loads[1] + loads[2] + loads[3];
        float averageLoad = loadSum > 1f ? loadSum * 0.25f : 0f;
        for (int i = 0; i < 4; i++)
        {
            float normalizedLoad = averageLoad > 1f
                ? Clamp01(loads[i] / averageLoad)
                : 0.75f;
            float loadMultiplier = Lerp(0.52f, 1f, normalizedLoad);
            bool frontWheel = i < 2;
            float longitudinalSlip = SmoothStep(RatioStart(i), RatioEnd(i), slipRatio[i])
                * (frontWheel ? 0.88f : 0.92f);
            float lateralSlip = SmoothStep(AngleStart(i), AngleEnd(i), slipAngle[i])
                * (frontWheel ? 0.86f : 0.92f);
            // ACC's WheelSlip channel can stay numerically high during normal
            // rolling and must never create a cue by itself. It is only a mild
            // confidence boost after ratio/angle already confirms real slip.
            float globalSlipGate = SmoothStep(0.16f, 0.85f, MathF.Abs(t.SlipVibration));
            float confirmedSlip = MathF.Max(longitudinalSlip, lateralSlip * globalSlipGate);
            float reportedConfidence = SmoothStep(0.35f, 2.50f, wheelSlip[i]);
            float target = confirmedSlip * Lerp(0.88f, 1f, reportedConfidence)
                * loadMultiplier
                * speedFade;
            _slipEnvelope[i] = FollowEnvelope(
                _slipEnvelope[i], target, deltaSeconds, 0.008f, 0.065f);
        }
    }

    private void UpdateAdaptiveSlipBaseline(
        in TelemetrySnapshot t,
        float deltaSeconds,
        float speedFade)
    {
        Span<float> ratio = stackalloc float[4]
        {
            MathF.Abs(FiniteOrZero(t.SlipRatioFl)),
            MathF.Abs(FiniteOrZero(t.SlipRatioFr)),
            MathF.Abs(FiniteOrZero(t.SlipRatioRl)),
            MathF.Abs(FiniteOrZero(t.SlipRatioRr))
        };
        Span<float> angle = stackalloc float[4]
        {
            MathF.Abs(FiniteOrZero(t.SlipAngleFl)),
            MathF.Abs(FiniteOrZero(t.SlipAngleFr)),
            MathF.Abs(FiniteOrZero(t.SlipAngleRl)),
            MathF.Abs(FiniteOrZero(t.SlipAngleRr))
        };

        bool trustedGrip = speedFade > 0.70f &&
            t.Brake < 0.20f &&
            t.Gas < 0.82f &&
            MathF.Abs(t.Tc) < 0.01f &&
            MathF.Abs(t.Abs) < 0.01f &&
            MathF.Abs(t.AbsVibration) < 0.035f &&
            MathF.Abs(t.SlipVibration) < 0.14f &&
            MathF.Abs(t.KerbVibration) < 0.012f &&
            t.NumberOfTyresOut == 0 &&
            Max4(ratio[0], ratio[1], ratio[2], ratio[3]) < 0.16f &&
            Max4(angle[0], angle[1], angle[2], angle[3]) < 0.24f;
        if (!trustedGrip)
            return;

        for (int i = 0; i < 4; i++)
        {
            bool frontWheel = i < 2;
            float ratioFloor = frontWheel ? 0.030f : 0.040f;
            float angleFloor = frontWheel ? 0.100f : 0.060f;
            float ratioTarget = Math.Clamp(ratio[i], ratioFloor, frontWheel ? 0.120f : 0.140f);
            float angleTarget = Math.Clamp(angle[i], angleFloor, frontWheel ? 0.220f : 0.180f);

            if (!_adaptiveSlipPrimed)
            {
                // Prime from the first clearly normal rolling/cornering frame
                // so a car with naturally high slip values does not buzz for
                // several seconds before the estimator catches up.
                _slipRatioBaseline[i] = ratioTarget;
                _slipAngleBaseline[i] = angleTarget;
                continue;
            }

            _slipRatioBaseline[i] = FollowValue(
                _slipRatioBaseline[i], ratioTarget, deltaSeconds,
                ratioTarget > _slipRatioBaseline[i] ? 5.0f : 18.0f);
            _slipAngleBaseline[i] = FollowValue(
                _slipAngleBaseline[i], angleTarget, deltaSeconds,
                angleTarget > _slipAngleBaseline[i] ? 5.0f : 18.0f);
        }

        _adaptiveSlipPrimed = true;
    }

    private float RatioStart(int wheel)
    {
        bool frontWheel = wheel < 2;
        float floor = frontWheel ? 0.085f : 0.110f;
        float adaptive = frontWheel
            ? _slipRatioBaseline[wheel] * 1.35f + 0.045f
            : _slipRatioBaseline[wheel] * 1.30f + 0.055f;
        return MathF.Max(floor, adaptive);
    }

    private float RatioEnd(int wheel)
        => RatioStart(wheel) + (wheel < 2 ? 0.295f : 0.290f);

    private float AngleStart(int wheel)
    {
        bool frontWheel = wheel < 2;
        float floor = frontWheel ? 0.160f : 0.100f;
        float adaptive = frontWheel
            ? _slipAngleBaseline[wheel] * 1.12f + 0.035f
            : _slipAngleBaseline[wheel] * 1.20f + 0.030f;
        return MathF.Max(floor, adaptive);
    }

    private float AngleEnd(int wheel)
        => AngleStart(wheel) + (wheel < 2 ? 0.180f : 0.170f);

    private static float FollowSigned(float current, float target, float deltaSeconds, float timeConstant)
    {
        float blend = 1f - MathF.Exp(-deltaSeconds / MathF.Max(0.001f, timeConstant));
        float next = current + (target - current) * blend;
        return MathF.Abs(next) < 0.002f ? 0f : Math.Clamp(next, -1f, 1f);
    }

    private static float FollowValue(float current, float target, float deltaSeconds, float timeConstant)
    {
        float blend = 1f - MathF.Exp(-deltaSeconds / MathF.Max(0.001f, timeConstant));
        return current + (target - current) * blend;
    }

    private static float FollowEnvelope(
        float current,
        float target,
        float deltaSeconds,
        float attackSeconds,
        float releaseSeconds)
    {
        float timeConstant = target > current ? attackSeconds : releaseSeconds;
        float blend = 1f - MathF.Exp(-deltaSeconds / MathF.Max(0.001f, timeConstant));
        float next = current + (target - current) * blend;
        return next < 0.002f ? 0f : Clamp01(next);
    }

    private static float SoftCombine(float baseline, float cue)
        => 1f - (1f - Clamp01(baseline)) * (1f - Clamp01(cue));

    private static float Wave(double seconds, float frequency, float floor)
    {
        float pulse = 0.5f + 0.5f * (float)Math.Sin(seconds * frequency * Math.Tau);
        return floor + (1f - floor) * pulse;
    }

    private static float PulseWave(double seconds, float frequency, float floor, float sharpness)
    {
        float pulse = 0.5f + 0.5f * (float)Math.Sin(seconds * frequency * Math.Tau);
        float shaped = MathF.Pow(pulse, sharpness);
        return floor + (1f - floor) * shaped;
    }

    private static byte ToByte(float normalized)
        => (byte)Math.Clamp((int)MathF.Round(Clamp01(normalized) * 255f), 0, 255);

    private static float SmoothStep(float from, float to, float value)
    {
        if (to <= from)
            return value >= to ? 1f : 0f;
        float x = Clamp01((value - from) / (to - from));
        return x * x * (3f - 2f * x);
    }

    private static float Max4(float a, float b, float c, float d)
        => MathF.Max(MathF.Max(a, b), MathF.Max(c, d));

    private static float Lerp(float from, float to, float amount)
        => from + (to - from) * Clamp01(amount);

    private static float PositiveFinite(float value)
        => float.IsFinite(value) && value > 0f ? value : 0f;

    private static float FiniteOrZero(float value)
        => float.IsFinite(value) ? value : 0f;

    private static float Clamp01(float value)
        => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
}
