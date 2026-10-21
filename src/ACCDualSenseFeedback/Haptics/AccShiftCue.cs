using ACCDualSenseFeedback.Telemetry;

namespace ACCDualSenseFeedback.Haptics;

// One immutable decision is consumed by R2 and lighting on the same output
// iteration. It does not replace CurrentMaxRpm or generate any HID reports.
internal readonly record struct AccShiftCueState(int ReferenceRpm, bool Active, bool ResetEnvelope)
{
    public bool Calibrated => ReferenceRpm > 0;
}

internal sealed class AccShiftCue
{
    private string? _carModel;
    private AccShiftProfile _profile;
    private int _reference;
    private int _gear;
    private bool _active;

    // Initialize the small compiled table before entering the output loop.
    public AccShiftCue() { _ = AccShiftProfiles.All.Length; }

    public AccShiftCueState Update(in TelemetrySnapshot t, string carModel, int staticMaxRpm,
        in FeedbackTuning tuning, bool paused = false, bool replay = false)
    {
        bool carChanged = !string.Equals(_carModel, carModel, StringComparison.Ordinal);
        if (carChanged)
        {
            _carModel = carModel;
            AccShiftProfiles.TryGet(carModel, out _profile);
        }
        // Use the game's native HUD alarm value only with both RPM bases
        // matching the extracted profile. Unknown cars/ranges and explicit
        // manual timing retain the original generic calculation.
        int reference = _profile.AlarmRpm > 0 && staticMaxRpm == _profile.LimiterRpm &&
            t.CurrentMaxRpm == _profile.LimiterRpm && MathF.Abs(tuning.RedlineStartRatio - 0.962f) < 0.00001f
            ? _profile.AlarmRpm : 0;
        // ACC's BasicCar01 HUD skips the flashing alarm in displayed sixth
        // gear (shared-memory gear 7). Keep this separate from actual limiting.
        bool inactive = t.IsEngineRunning == 0 || t.Gear <= 1 || t.Gear == 7 || paused || replay || t.Rpm <= 0;
        bool reset = reference != _reference ||
            (reference > 0 && (carChanged || _gear != t.Gear || inactive));
        if (reset) _active = false;
        _reference = reference; _gear = t.Gear;
        if (reference == 0) return new(0, false, reset);
        if (inactive)
        { _active = false; return new(reference, false, true); }

        // An 80 RPM release margin prevents rapid on/off at the shift point.
        if (t.Rpm > reference) _active = true;
        else if (t.Rpm < reference - 80) _active = false;
        return new(reference, _active, reset);
    }

    public void Reset()
    {
        _carModel = null; _profile = default;
        _reference = 0; _gear = 0; _active = false;
    }
}
