using System.Collections.Concurrent;
using System.Numerics;

namespace ACCDualSenseFeedback.Gyro;

internal enum GyroBindingTarget { None, Recenter, Activation }
internal enum GyroRuntimeState { Disabled, WaitingForCalibration, WaitingForCenter, Active, StickTakeover, Suspended, SensorInvalid, NativeUnavailable, Calibrating }
[Flags]
internal enum GyroPauseReason
{
    None = 0,
    NotDriving = 1,
    GameNotForeground = 2,
    ActivationOff = 4,
    ResumeRequired = 8,
    HoldNotPressed = 16
}
internal enum GyroInputSource { PhysicalStick, Gyro, StickTakeover, ReturningToGyro, ReturningToStick }
internal enum GyroCommandKind { Recenter, Calibrate, Resume, LearnButton, CancelLearning }
internal readonly record struct GyroCommand(long Sequence, GyroCommandKind Kind, GyroBindingTarget Target = GyroBindingTarget.None);

/// <summary>
/// One sensor sample's steering test values, before game activation and physical-stick arbitration.
/// MappedX includes angle/deadzone/curve/inversion; OutputX also includes the configured smoothing.
/// A valid preview remains available while ACC is paused or this tool is in the foreground.
/// </summary>
internal readonly record struct GyroSteeringPreview(bool IsAvailable, float AngleDegrees, float MappedX, float OutputX);

internal sealed record GyroRuntimeStatus
{
    public static readonly GyroRuntimeStatus Initial = new();
    public GyroRuntimeState State { get; init; } = GyroRuntimeState.Disabled;
    /// <summary>Explanatory flags for Suspended only; never used to gate steering output.</summary>
    public GyroPauseReason PauseReasons { get; init; }
    public string Message { get; init; } = "Gyro off";
    public string DeviceIdentity { get; init; } = "";
    public float AngleDegrees { get; init; }
    public float PhysicalX { get; init; }
    public float GyroX { get; init; }
    public GyroSteeringPreview SteeringPreview { get; init; }
    /// <summary>Final virtual-controller output, including pause protection, stick takeover and handoff.</summary>
    public float OutputX { get; init; }
    public GyroInputSource InputSource { get; init; }
    public double DeviceDeltaSeconds { get; init; }
    public bool InputValid { get; init; }
    public bool ImuValid { get; init; }
    public bool FactoryCalibrated { get; init; }
    public bool StationaryCalibrated { get; init; }
    public bool NeedsRecenter { get; init; } = true;
    public bool Ready { get; init; }
    public bool IsCalibrating { get; init; }
    public bool SuppressFeedback => IsCalibrating;
    public float CalibrationProgress { get; init; }
    public bool CalibrationPersistencePending { get; init; }
    public string? CalibrationPersistenceError { get; init; }
    public long RecenterSequence { get; init; }
    public long CalibrationSequence { get; init; }
    public long LearnedSequence { get; init; }
    public GyroBindingTarget PendingBindingTarget { get; init; }
    public GyroButton LearnedButton { get; init; }
    public GyroBindingTarget LearnedBindingTarget { get; init; }
    public Vector3 RawGyro { get; init; }
    public Vector3 RawAcceleration { get; init; }
    public Vector3 CalibratedGyro { get; init; }
    public Vector3 AccelerationG { get; init; }
}

/// <summary>UI publishes commands; the single input thread consumes them. No native object crosses this boundary.</summary>
internal static class GyroControlState
{
    private static readonly ConcurrentQueue<GyroCommand> Commands = new();
    private static long _sequence;
    private static GyroRuntimeStatus _current = GyroRuntimeStatus.Initial;
    public static GyroRuntimeStatus Current => Volatile.Read(ref _current);
    public static long RequestRecenter() => Enqueue(GyroCommandKind.Recenter);
    public static long RequestCalibration() => Enqueue(GyroCommandKind.Calibrate);
    public static long RequestResume() => Enqueue(GyroCommandKind.Resume);
    public static long RequestLearnButton(GyroBindingTarget target) => Enqueue(GyroCommandKind.LearnButton, target);
    public static long CancelButtonLearning() => Enqueue(GyroCommandKind.CancelLearning);
    internal static bool TryDequeue(out GyroCommand command) => Commands.TryDequeue(out command);
    internal static void Publish(GyroRuntimeStatus status) => Volatile.Write(ref _current, status);
    private static long Enqueue(GyroCommandKind kind, GyroBindingTarget target = GyroBindingTarget.None)
    {
        long sequence = Interlocked.Increment(ref _sequence);
        Commands.Enqueue(new(sequence, kind, target));
        return sequence;
    }
}

/// <summary>Coordinates follow GMH / PlayStation: X right, Y up, Z toward player. Timestamp is PS5 3 MHz ticks.</summary>
internal readonly record struct GyroSensorSample(Vector3 AngularVelocityDegreesPerSecond, Vector3 AccelerationG,
    uint DeviceTimestamp, bool IsValid = true, Vector3 RawGyro = default, Vector3 RawAcceleration = default,
    string DeviceIdentity = "");

internal readonly record struct GyroInputContext(bool CustomPreset, bool IsDriving, bool IsSuspended,
    bool IsForeground, bool FactoryCalibrated, GyroButton Buttons, double MonotonicSeconds);
internal readonly record struct GyroProcessResult(float OutputX, GyroButton ConsumedButtons, GyroRuntimeStatus Status);
