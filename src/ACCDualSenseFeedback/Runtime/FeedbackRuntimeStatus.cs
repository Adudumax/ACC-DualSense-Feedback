using ACCDualSenseFeedback.Telemetry;

namespace ACCDualSenseFeedback.Runtime;

internal readonly record struct FeedbackRuntimeStatus(
    TelemetrySnapshot Telemetry,
    bool AccConnected,
    bool DualSenseConnected,
    bool TelemetryStale,
    bool InputBridgeConnected,
    long FramesRead,
    long FramesWritten,
    byte NativeLargeMotor,
    byte NativeSmallMotor,
    int LeftTriggerMode,
    int RightTriggerMode);
