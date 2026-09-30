using ACCDualSenseFeedback.Hardware.DualSense;

namespace ACCDualSenseFeedback.Haptics;

internal readonly record struct FeedbackFrame(
    byte LeftActuator,
    byte RightActuator,
    TriggerEffect LeftTrigger,
    TriggerEffect RightTrigger)
{
    public static readonly FeedbackFrame Off = new(0, 0, TriggerEffect.Off, TriggerEffect.Off);
}
