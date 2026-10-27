using System.Numerics;

namespace ACCDualSenseFeedback.Gyro;

/// <summary>Single-input-thread steering state machine. IMU integration uses the device clock, never the GUI or ACC rate.</summary>
internal sealed class GyroSteeringProcessor : IDisposable
{
    private readonly Func<GyroSteeringSettings> _settingsProvider;
    private readonly IGyroFusion _fusion;
    private readonly bool _persistBias;
    private string _identity = "";
    private string _anonymousIdentity = "";
    private uint _timestamp;
    private bool _hasTimestamp, _hasCenter, _stationaryCalibrated, _wasSuspended, _resumeRequired, _wasEffective;
    private double _lastHost, _lastValidHost;
    private Quaternion _neutral = Quaternion.Identity;
    private GyroFusionPose _pose;
    private Vector3 _bias;
    private float _gravityOffset, _angle, _lastWrappedAngle, _filteredX, _lastOutput;
    private bool _hasAngle, _hadGyro, _manualEnabled = true;
    private GyroReferenceMode _referenceMode;
    private GyroActivationMode _activationMode;
    private GyroButton _previousButtons;
    private bool _stickTakeover;
    private double _stickReleasedAt = double.NaN, _handoffStarted = double.NaN;
    private float _handoffStart;
    private bool _calibrating;
    private double _calibrationSeconds, _calibrationStarted;
    private Vector3 _calibrationSum, _calibrationAccelMean;
    private int _calibrationSamples;
    private Task<GyroBiasSaveResult>? _biasSave;
    private string? _biasSaveError;
    private long _recenterSequence, _calibrationSequence, _learnSequence, _learnedSequence;
    private GyroBindingTarget _learning, _learnedTarget;
    private GyroButton _learnedButton;
    private GyroButton _learningHeldButton;
    private double _learnStarted;
    private bool _learnArmed;
    private string? _operationMessage;
    private double _operationMessageUntil;

    public GyroSteeringProcessor(Func<GyroSteeringSettings>? settingsProvider = null)
        : this(new NativeGyroFusion(), settingsProvider, true) { }

    // Injection is limited to deterministic tests; production always uses the versioned native implementation.
    internal GyroSteeringProcessor(IGyroFusion fusion, Func<GyroSteeringSettings>? settingsProvider = null, bool persistBias = false)
    {
        _fusion = fusion;
        _settingsProvider = settingsProvider ?? (() => GyroSettingsState.Current);
        _persistBias = persistBias;
    }

    public void ResetConnection(string deviceIdentity)
    {
        _identity = deviceIdentity;
        _anonymousIdentity = string.IsNullOrEmpty(deviceIdentity) ? "" : PortableGyroSettingsStore.AnonymousDeviceIdentity(deviceIdentity);
        _hasTimestamp = _hasCenter = _hasAngle = _hadGyro = _stickTakeover = false;
        _resumeRequired = _wasSuspended = _calibrating = _wasEffective = false;
        _manualEnabled = _activationMode != GyroActivationMode.Toggle;
        _pose = default;
        _learning = GyroBindingTarget.None;
        _learningHeldButton = GyroButton.None;
        _biasSave = null; _biasSaveError = null;
        _previousButtons = GyroButton.None;
        _filteredX = _lastOutput = 0;
        _stickReleasedAt = _handoffStarted = double.NaN;
        _fusion.Reset();
        var saved = _persistBias ? PortableGyroSettingsStore.LoadBias(deviceIdentity) : null;
        _stationaryCalibrated = saved.HasValue;
        _bias = saved ?? Vector3.Zero;
        _fusion.SetBias(_bias);
    }

    public GyroProcessResult Process(GyroSensorSample sample, float physicalX, GyroInputContext context)
    {
        physicalX = float.IsFinite(physicalX) ? Math.Clamp(physicalX, -1, 1) : 0;
        var settings = _settingsProvider().Sanitize();
        if (!string.Equals(_identity, sample.DeviceIdentity, StringComparison.Ordinal)) ResetConnection(sample.DeviceIdentity);
        if (_biasSave is { IsCompletedSuccessfully: true })
        {
            _biasSaveError = _biasSave.Result.Error; // Completed-only read cannot stall the input thread.
            _biasSave = null;
            if (_biasSaveError is not null) SetMessage("Calibration complete; bias not saved", context.MonotonicSeconds);
        }
        if (_referenceMode != settings.ReferenceMode)
        { _referenceMode = settings.ReferenceMode; InvalidateCenter(); }
        if (_activationMode != settings.ActivationMode)
        {
            _activationMode = settings.ActivationMode;
            _manualEnabled = _activationMode != GyroActivationMode.Toggle;
            if (_activationMode == GyroActivationMode.Always) _resumeRequired = false;
        }
        bool effective = context.CustomPreset && settings.Enabled;
        // Turning gyro back on or returning to Custom is an explicit enable.
        // Environment changes must never undo an Always user's manual stop.
        if (effective && !_wasEffective && _activationMode == GyroActivationMode.Always)
        { _manualEnabled = true; _resumeRequired = false; }
        _wasEffective = effective;
        bool suspended = context.IsSuspended || !context.IsForeground;
        // Always follows the driving/foreground gate automatically. Hold and
        // Toggle keep their existing explicit-resume policy after suspension.
        if (effective && _activationMode != GyroActivationMode.Always && suspended && !_wasSuspended && _hasCenter)
            _resumeRequired = true;
        _wasSuspended = suspended;
        float dt = DeviceDelta(sample.DeviceTimestamp, context.MonotonicSeconds, out bool repeated, out bool discontinuity);
        bool valid = sample.IsValid && context.FactoryCalibrated && Finite(sample.AngularVelocityDegreesPerSecond)
            && Finite(sample.AccelerationG) && sample.AngularVelocityDegreesPerSecond.LengthSquared() < 4_000_000
            && sample.AccelerationG.LengthSquared() is > .01f and < 256;
        if (discontinuity) { _fusion.Reset(); _fusion.SetBias(_bias); InvalidateCenter(); }
        if (!valid) InvalidateCenter();
        else if (!repeated && _fusion.Available)
        {
            valid = _fusion.Process(sample.AngularVelocityDegreesPerSecond, sample.AccelerationG, dt, out _pose);
            valid &= ValidPose(_pose);
            if (valid) _lastValidHost = context.MonotonicSeconds;
            else InvalidateCenter();
        }
        else if (repeated && context.MonotonicSeconds - _lastValidHost > .05)
        { valid = false; InvalidateCenter(); }
        if (!_fusion.Available) valid = false;

        while (GyroControlState.TryDequeue(out var command))
        {
            if (!context.CustomPreset) continue; // Shortcut and GUI commands never bypass the default preset.
            switch (command.Kind)
            {
                case GyroCommandKind.LearnButton:
                    _learning = command.Target; _learnSequence = command.Sequence;
                    _learnArmed = false; _learnStarted = context.MonotonicSeconds; break;
                case GyroCommandKind.CancelLearning:
                    _learning = GyroBindingTarget.None; break;
                case GyroCommandKind.Recenter when effective && valid && !_calibrating:
                    SetCenter(settings, command.Sequence, context.MonotonicSeconds); break;
                case GyroCommandKind.Calibrate when effective:
                    if (context.IsDriving) SetMessage("Pause ACC before calibration", context.MonotonicSeconds);
                    else if (valid) StartCalibration(command.Sequence, context.MonotonicSeconds);
                    else SetMessage("Motion sensor unavailable", context.MonotonicSeconds);
                    break;
                case GyroCommandKind.Resume when effective:
                    _resumeRequired = false; _manualEnabled = true; StartHandoff(physicalX, context.MonotonicSeconds); break;
            }
        }

        GyroButton consumed = GyroButton.None;
        // Continue consuming a captured press until release, including the gap before the UI saves the binding.
        _learningHeldButton &= context.Buttons;
        if (context.CustomPreset) consumed |= _learningHeldButton;
        else _learningHeldButton = GyroButton.None;
        var rising = context.Buttons & ~_previousButtons;
        bool learningFrame = _learning != GyroBindingTarget.None;
        if (learningFrame)
        {
            if (!context.CustomPreset || context.MonotonicSeconds - _learnStarted > 15) _learning = GyroBindingTarget.None;
            else if (context.Buttons == GyroButton.None) _learnArmed = true;
            else if (_learnArmed && rising != GyroButton.None)
            {
                // Pick the lowest individual button for simultaneous presses. Do not run its shortcut this frame.
                _learnedButton = (GyroButton)((ulong)rising & (0UL - (ulong)rising));
                _learningHeldButton = _learnedButton;
                consumed |= _learnedButton;
                _learnedTarget = _learning; _learnedSequence = _learnSequence; _learning = GyroBindingTarget.None;
            }
        }
        // A bound analog trigger belongs to the tool for its entire travel,
        // including movement before/after the report's digital press flag.
        // Otherwise a shortcut can leak a brief throttle/brake input to ACC.
        if (effective) consumed |= settings.ActivationButton & (GyroButton.L2 | GyroButton.R2);
        if (effective && !learningFrame)
        {
            if (!settings.ForwardShortcutButtons) consumed |= context.Buttons & (settings.RecenterButton | settings.ActivationButton);
            if ((rising & settings.RecenterButton) != 0 && valid && !_calibrating)
                SetCenter(settings, ++_recenterSequence, context.MonotonicSeconds);
            if ((rising & settings.ActivationButton) != 0 && !suspended)
            {
                if (_resumeRequired) { _resumeRequired = false; _manualEnabled = true; }
                else if (settings.ActivationMode != GyroActivationMode.Hold) _manualEnabled = !_manualEnabled;
                StartHandoff(physicalX, context.MonotonicSeconds);
            }
        }
        _previousButtons = context.Buttons;
        if (_calibrating)
        {
            if (!effective || context.IsDriving || !valid)
            { _calibrating = false; ResetCalibrationWindow(); SetMessage("Calibration cancelled", context.MonotonicSeconds); }
            else if (!repeated) UpdateCalibration(sample, dt, context.MonotonicSeconds);
        }

        float angle = 0, mapped = 0;
        bool angleValid = valid && _hasCenter && TryGetAngle(_pose, settings.ReferenceMode, out angle);
        if (valid && _hasCenter && !angleValid) { valid = false; InvalidateCenter(); SetMessage("Hold controller upright", context.MonotonicSeconds); }
        if (angleValid)
        {
            _angle = angle;
            mapped = MapAngle(angle, settings);
            float tau = settings.SmoothingMilliseconds / 1000f;
            // Preserve quick steering: progressively reduce smoothing above 30 degrees/s.
            tau /= 1 + _pose.CalibratedGyro.Length() / 30;
            float alpha = tau <= 0 ? 1 : 1 - MathF.Exp(-dt / tau);
            _filteredX += (mapped - _filteredX) * alpha;
        }
        bool activated = settings.ActivationMode == GyroActivationMode.Hold
            ? settings.ActivationButton != GyroButton.None && (context.Buttons & settings.ActivationButton) != 0
            : _manualEnabled && (settings.ActivationMode != GyroActivationMode.Toggle || settings.ActivationButton != GyroButton.None);
        bool ready = valid && _stationaryCalibrated && _hasCenter && !_calibrating;
        bool useGyro = effective && ready && !suspended && !_resumeRequired && activated;
        float output;
        GyroInputSource source = GyroInputSource.PhysicalStick;
        if (useGyro)
        {
            if (!_hadGyro) StartHandoff(physicalX, context.MonotonicSeconds);
            output = Arbitrate(physicalX, _filteredX, settings, context.MonotonicSeconds, out source);
        }
        else
        {
            _stickTakeover = false; _stickReleasedAt = double.NaN;
            // Explicit stop/preset/pause hands control back over 80ms; invalid IMU falls back immediately.
            if (_hadGyro && valid) { _handoffStart = _lastOutput; _handoffStarted = context.MonotonicSeconds; }
            float amount = double.IsNaN(_handoffStarted) ? 1 : (float)Math.Clamp((context.MonotonicSeconds - _handoffStarted) / .08, 0, 1);
            output = valid && !double.IsNaN(_handoffStarted) ? Lerp(_handoffStart, physicalX, amount) : physicalX;
            if (valid && !double.IsNaN(_handoffStarted) && amount < 1) source = GyroInputSource.ReturningToStick;
            if (amount >= 1) _handoffStarted = double.NaN;
        }
        _hadGyro = useGyro;
        _lastOutput = output = Math.Clamp(output, -1, 1);
        var state = !effective ? GyroRuntimeState.Disabled
            : !_fusion.Available ? GyroRuntimeState.NativeUnavailable
            : _calibrating ? GyroRuntimeState.Calibrating
            : !valid ? GyroRuntimeState.SensorInvalid
            : !_stationaryCalibrated ? GyroRuntimeState.WaitingForCalibration
            : !_hasCenter ? GyroRuntimeState.WaitingForCenter
            : suspended || _resumeRequired || !activated ? GyroRuntimeState.Suspended
            : _stickTakeover ? GyroRuntimeState.StickTakeover : GyroRuntimeState.Active;
        string message = state switch
        {
            GyroRuntimeState.Disabled => "Gyro off", GyroRuntimeState.NativeUnavailable => "Motion fusion library unavailable",
            GyroRuntimeState.Calibrating => "Keep controller still", GyroRuntimeState.SensorInvalid => "Motion sensor unavailable",
            GyroRuntimeState.WaitingForCalibration => "Stationary calibration required", GyroRuntimeState.WaitingForCenter => "Set neutral pose",
            GyroRuntimeState.Suspended => "Gyro suspended", GyroRuntimeState.StickTakeover => "Physical stick override", _ => "Gyro active"
        };
        if (_operationMessage is not null && context.MonotonicSeconds < _operationMessageUntil) message = _operationMessage;
        // Describe the existing gates without changing activation, handoff, or protection behavior.
        GyroPauseReason pauseReasons = GyroPauseReason.None;
        if (state == GyroRuntimeState.Suspended)
        {
            if (context.IsSuspended) pauseReasons |= GyroPauseReason.NotDriving;
            if (!context.IsForeground) pauseReasons |= GyroPauseReason.GameNotForeground;
            if (_resumeRequired) pauseReasons |= GyroPauseReason.ResumeRequired;
            if (!activated) pauseReasons |= settings.ActivationMode == GyroActivationMode.Hold
                ? GyroPauseReason.HoldNotPressed : GyroPauseReason.ActivationOff;
        }
        var status = new GyroRuntimeStatus
        {
            State = state, Message = message, DeviceIdentity = _anonymousIdentity,
            AngleDegrees = _angle, PhysicalX = physicalX, GyroX = _filteredX, OutputX = output, InputSource = source,
            SteeringPreview = effective && ready && angleValid ? new(true, _angle, mapped, _filteredX) : default,
            DeviceDeltaSeconds = repeated ? 0 : dt, InputValid = true, ImuValid = valid,
            FactoryCalibrated = context.FactoryCalibrated, StationaryCalibrated = _stationaryCalibrated,
            NeedsRecenter = !_hasCenter, Ready = ready, IsCalibrating = _calibrating,
            CalibrationProgress = (float)Math.Clamp(_calibrationSeconds / 2.5, 0, 1),
            CalibrationPersistencePending = _biasSave is not null, CalibrationPersistenceError = _biasSaveError,
            RecenterSequence = _recenterSequence, CalibrationSequence = _calibrationSequence,
            PendingBindingTarget = _learning, LearnedSequence = _learnedSequence, LearnedButton = _learnedButton, LearnedBindingTarget = _learnedTarget,
            RawGyro = sample.RawGyro, RawAcceleration = sample.RawAcceleration,
            CalibratedGyro = _pose.CalibratedGyro, AccelerationG = sample.AccelerationG,
            PauseReasons = pauseReasons
        };
        GyroControlState.Publish(status);
        return new(output, consumed, status);
    }

    public static float MapAngle(float angleDegrees, GyroSteeringSettings settings)
    {
        if (!float.IsFinite(angleDegrees)) return 0;
        settings = settings.Sanitize();
        float x = Math.Clamp((Math.Abs(angleDegrees) - settings.DeadzoneDegrees) / (settings.FullLockDegrees - settings.DeadzoneDegrees), 0, 1);
        return MathF.CopySign(MathF.Pow(x, settings.EffectiveExponent), settings.Invert ? -angleDegrees : angleDegrees);
    }

    private float DeviceDelta(uint timestamp, double host, out bool repeated, out bool discontinuity)
    {
        repeated = discontinuity = false;
        if (!_hasTimestamp)
        { _hasTimestamp = true; _timestamp = timestamp; _lastHost = host; return .001f; }
        uint ticks = unchecked(timestamp - _timestamp);
        float seconds = ticks / 3_000_000f;
        if (ticks == 0) { repeated = true; return .001f; }
        discontinuity = seconds > .05f || host - _lastHost > .1 || host < _lastHost;
        _timestamp = timestamp; _lastHost = host;
        return discontinuity ? .001f : seconds;
    }
    private void SetCenter(GyroSteeringSettings settings, long sequence, double host)
    {
        if (!_stationaryCalibrated) { SetMessage("Stationary calibration required", host); return; }
        _neutral = Quaternion.Normalize(_pose.Orientation);
        _gravityOffset = GravityAngle(_pose.Gravity);
        _hasCenter = true; _hasAngle = false; _angle = _lastWrappedAngle = _filteredX = 0;
        _recenterSequence = Math.Max(_recenterSequence, sequence);
        StartHandoff(_lastOutput, host);
    }
    private bool TryGetAngle(GyroFusionPose pose, GyroReferenceMode reference, out float angle)
    {
        angle = 0;
        float wrapped;
        if (reference == GyroReferenceMode.Gravity)
        {
            float length = pose.Gravity.Length();
            if (length < .1f || new Vector2(pose.Gravity.X, pose.Gravity.Y).Length() / length < .25f) return false;
            wrapped = GravityAngle(pose.Gravity) - _gravityOffset;
        }
        else
        {
            var relative = Quaternion.Normalize(Quaternion.Conjugate(_neutral) * pose.Orientation);
            if (relative.W * relative.W + relative.Z * relative.Z < .0001f) return false;
            wrapped = -2 * MathF.Atan2(relative.Z, relative.W) * (180 / MathF.PI);
        }
        wrapped = WrapDegrees(wrapped);
        angle = _hasAngle ? _angle + WrapDegrees(wrapped - _lastWrappedAngle) : wrapped;
        _lastWrappedAngle = wrapped; _hasAngle = true;
        return float.IsFinite(angle);
    }
    private static float GravityAngle(Vector3 gravity) => MathF.Atan2(gravity.X, -gravity.Y) * (180 / MathF.PI);
    private static float WrapDegrees(float value) { value %= 360; return value > 180 ? value - 360 : value < -180 ? value + 360 : value; }
    private void InvalidateCenter() { _hasCenter = _hasAngle = false; _filteredX = _angle = 0; }
    private void StartCalibration(long sequence, double host)
    { _calibrating = true; _calibrationSequence = sequence; _calibrationStarted = host; ResetCalibrationWindow(); InvalidateCenter(); }
    private void ResetCalibrationWindow() { _calibrationSeconds = 0; _calibrationSum = _calibrationAccelMean = Vector3.Zero; _calibrationSamples = 0; }
    private void UpdateCalibration(GyroSensorSample sample, float dt, double host)
    {
        // Bias is estimated only following an explicit request, outside driving, with feedback muted by the unique writer.
        if (host - _calibrationStarted > 20) { _calibrating = false; SetMessage("Calibration timed out", host); return; }
        if (host - _calibrationStarted < .2) return; // Allow the unique HID writer to mute and the motors to settle.
        if (sample.AngularVelocityDegreesPerSecond.Length() > 3 || Math.Abs(sample.AccelerationG.Length() - 1) > .08f
            || (_calibrationSamples > 10 && Vector3.Distance(sample.AccelerationG, _calibrationAccelMean) > .05f))
        { ResetCalibrationWindow(); return; }
        _calibrationSamples++;
        _calibrationSum += sample.AngularVelocityDegreesPerSecond * dt;
        _calibrationAccelMean += (sample.AccelerationG - _calibrationAccelMean) / _calibrationSamples;
        _calibrationSeconds += dt;
        if (_calibrationSeconds < 2.5) return;
        _bias = _calibrationSum / (float)_calibrationSeconds;
        _fusion.SetBias(_bias); _fusion.Reset();
        _stationaryCalibrated = true; _calibrating = false; InvalidateCenter();
        _biasSaveError = null;
        if (_persistBias) _biasSave = PortableGyroSettingsStore.SaveBiasAsync(_identity, _bias);
        SetMessage("Calibration complete; set neutral pose", host);
    }
    private float Arbitrate(float physicalX, float gyroX, GyroSteeringSettings settings, double host, out GyroInputSource source)
    {
        float threshold = settings.TakeoverPercent / 100;
        if (settings.StickPriority && Math.Abs(physicalX) > threshold)
        { _stickTakeover = true; _stickReleasedAt = double.NaN; _handoffStarted = double.NaN; }
        if (_stickTakeover)
        {
            if (!settings.StickPriority || Math.Abs(physicalX) < threshold * .6f)
            {
                if (double.IsNaN(_stickReleasedAt)) _stickReleasedAt = host;
                if (!settings.StickPriority || host - _stickReleasedAt >= .12)
                { _stickTakeover = false; StartHandoff(physicalX, host); }
            }
            else _stickReleasedAt = double.NaN;
            if (_stickTakeover) { source = GyroInputSource.StickTakeover; return physicalX; }
        }
        float amount = double.IsNaN(_handoffStarted) ? 1 : (float)Math.Clamp((host - _handoffStarted) / .08, 0, 1);
        if (amount >= 1) { _handoffStarted = double.NaN; source = GyroInputSource.Gyro; return gyroX; }
        source = GyroInputSource.ReturningToGyro;
        return Lerp(_handoffStart, gyroX, amount);
    }
    private void StartHandoff(float start, double host) { _handoffStart = start; _handoffStarted = host; }
    private void SetMessage(string message, double host) { _operationMessage = message; _operationMessageUntil = host + 3; }
    private static bool Finite(Vector3 vector) => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
    private static bool ValidPose(GyroFusionPose pose) => pose.Orientation.LengthSquared() is > .5f and < 1.5f && Finite(pose.Gravity) && Finite(pose.CalibratedGyro);
    private static float Lerp(float start, float end, float amount) => start + (end - start) * amount;
    public void Dispose() => _fusion.Dispose();
}
