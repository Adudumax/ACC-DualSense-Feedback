using ACCDualSenseFeedback.Gyro;

namespace ACCDualSenseFeedback.Ui;

internal readonly record struct GyroPausePresentation(string Hint, bool ShowResume);

/// <summary>Explains existing pause gates; it never changes activation or steering output.</summary>
internal static class GyroStatusPresentation
{
    internal static GyroPausePresentation Pause(GyroSteeringSettings settings, GyroRuntimeStatus status)
    {
        if (status.State != GyroRuntimeState.Suspended)
            return new("Ready. Gyro takes effect while driving in ACC.", false);

        GyroPauseReason reasons = status.PauseReasons;
        bool environment = (reasons & (GyroPauseReason.NotDriving | GyroPauseReason.GameNotForeground)) != 0;
        if (settings.ActivationMode == GyroActivationMode.Hold && (reasons & GyroPauseReason.HoldNotPressed) != 0)
            return new(environment
                ? "Return to driving in ACC and hold the bound button to enable gyro."
                : "Hold the bound button to enable gyro.", false);

        bool activationOff = (reasons & GyroPauseReason.ActivationOff) != 0;
        if (activationOff && settings.ActivationMode == GyroActivationMode.Toggle)
            return new(environment
                ? "Gyro is off. Choose Resume gyro, or press the bound button after returning to ACC."
                : "Gyro is off. Press the bound button or choose Resume gyro.", true);
        if (activationOff || (reasons & GyroPauseReason.ResumeRequired) != 0)
            return new(environment
                ? "Gyro paused. Choose Resume gyro, then return to driving in ACC."
                : "Gyro paused. Resume explicitly when ready.", true);

        if (settings.ActivationMode == GyroActivationMode.Always)
        {
            if ((reasons & GyroPauseReason.NotDriving) != 0)
                return new("Ready. Gyro takes effect automatically when you enter driving in ACC.", false);
            if ((reasons & GyroPauseReason.GameNotForeground) != 0)
                return new("Return to driving in ACC to resume gyro automatically.", false);
        }
        return new(environment ? "Return to driving in ACC to use gyro." : "Gyro is paused.", false);
    }
}
