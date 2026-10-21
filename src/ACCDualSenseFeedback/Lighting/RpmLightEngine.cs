using System.Diagnostics;
using ACCDualSenseFeedback.Telemetry;
using ACCDualSenseFeedback.Haptics;

namespace ACCDualSenseFeedback.Lighting;

// ACC lighting owns its clocks and event detection; it never changes haptic
// telemetry, tuning or output timing. The original CSP math below remains a
// separately tested reference, rather than defining ACC's missing car inputs.
internal sealed class RpmLightEngine
{
    private static readonly LightRgb IdleIceBlue = new(0x5A / 255d, 0xAF / 255d, 0xD6 / 255d);
    private readonly AccBrakeLightDetector _braking = new();
    private long _lastAt;
    private int _maxRpm;
    private double _time;
    private double _idleBlend;
    private double _shiftStarted = double.NaN;

    public RpmLightFrame Compute(in TelemetrySnapshot telemetry, long now,
        RpmLightSettings settings, int staticMaxRpm = 0, CspLightContext context = default,
        AccShiftCueState shiftCue = default)
    {
        if (!settings.Enabled || !IsValidMaxRpm(staticMaxRpm) ||
            telemetry.IsEngineRunning == 0 || telemetry.Rpm <= 0 ||
            !float.IsFinite(telemetry.SpeedKmh) || !float.IsFinite(telemetry.Gas) || context.IsReplay)
        {
            Reset();
            return RpmLightFrame.Off;
        }
        // A vehicle/range change must not retain wheel calibration or alerts.
        if (_maxRpm != staticMaxRpm) { Reset(); _maxRpm = staticMaxRpm; }
        bool first = _lastAt == 0;
        double dt = first ? 0 : Math.Max(0, (now - _lastAt) / (double)Stopwatch.Frequency);
        _lastAt = now;
        if (!context.IsPaused) _time += dt;

        // Keep the existing visual curve; a supported native HUD alarm only
        // changes its endpoint. Unknown cars retain the generic visual range.
        double ratio = telemetry.Rpm / (double)staticMaxRpm;
        double endRatio = shiftCue.Calibrated ? shiftCue.ReferenceRpm / (double)staticMaxRpm : 0.97;
        double range = shiftCue.Calibrated ? endRatio - 0.60 : 0.37;
        double progress = Math.Clamp((ratio - 0.60) / range, 0, 1);
        byte mask = 0;
        if (ratio >= 0.60)
            for (int i = 0; i < 5; i++)
                if (ratio >= 0.60 + range * i / 4) mask |= (byte)(1 << i);
        byte brightness = progress > 0.5 ? (byte)0 : progress > 0.25 ? (byte)1 : (byte)2;

        bool idle = Math.Abs(telemetry.SpeedKmh) < 3 && telemetry.Gas < 0.04 && ratio < 0.30;
        if (!context.IsPaused)
            _idleBlend = first ? (idle ? 1 : 0) :
                _idleBlend + ((idle ? 1d : 0d) - _idleBlend) * (1 - Math.Exp(-dt / 0.30));
        // Finish the exponential transition below a sub-quantization amount,
        // rather than retaining an infinitesimal tint in the driving floor.
        if (_idleBlend < 0.005) _idleBlend = 0;
        else if (_idleBlend > 0.995) _idleBlend = 1;
        // A three-second breath never goes black. While driving, the low-RPM
        // floor is steady; its brightness joins the normal RPM curve smoothly.
        double breath = 0.04 * Math.Sin(_time * (2 * Math.PI / 3));
        double floor = 0.10 + breath * _idleBlend;
        double power = floor + (1 - floor) * SmoothStep(0.35, 0.65, ratio);
        LightRgb color;
        if (settings.Mode == RpmLightMode.Discrete)
            color = progress >= 0.82 ? new(1, 0, 0) : progress >= 0.5 ? new(1, 1, 0) : new(0, 1, 0);
        else
        {
            double hue = 120 * (1 - progress);
            color = hue >= 60 ? new(2 - hue / 60, 1, 0) : new(1, hue / 60, 0);
        }
        color = color.Scale(power);
        // Ice blue belongs to stationary idle only. Fade toward the existing
        // RPM colour on departure; a rev immediately removes it from the upper
        // RPM range, and warnings below retain their original RGB priority.
        double iceMix = _idleBlend * (1 - SmoothStep(0.30, 0.45, ratio));
        LightRgb ice = IdleIceBlue.Scale(power);
        color = new(color.Red + (ice.Red - color.Red) * iceMix,
            color.Green + (ice.Green - color.Green) * iceMix,
            color.Blue + (ice.Blue - color.Blue) * iceMix);

        bool shift = shiftCue.Calibrated ? shiftCue.Active : ratio >= 0.97 && telemetry.Gear != 0;
        if (shiftCue.ResetEnvelope) _shiftStarted = double.NaN;
        if (!shift) _shiftStarted = double.NaN;
        else if (double.IsNaN(_shiftStarted)) _shiftStarted = _time;
        if (shift && (_time - _shiftStarted) * 3 % 1 >= 0.5) color = default;
        if (telemetry.Gear == 0) color = LightRgb.White;

        // Only RGB is preempted; the five white LEDs keep their RPM meaning.
        if (!context.IsPaused) _braking.Update(telemetry, _time);
        if (_braking.IsActive(_time))
            color = (_time - _braking.StartedAt) * 4 % 1 < 0.55 ? new(1, 0.86, 0) : default;
        return new(ToByte(color.Red), ToByte(color.Green), ToByte(color.Blue), mask, brightness);
    }

    public void Reset()
    {
        _lastAt = 0; _maxRpm = 0; _time = 0; _idleBlend = 0;
        _shiftStarted = double.NaN; _braking.Reset();
    }
    internal static bool IsValidMaxRpm(int rpm) => rpm is >= 2000 and <= 25000;
    private static double SmoothStep(double start, double end, double value)
    {
        double x = Math.Clamp((value - start) / (end - start), 0, 1);
        return x * x * (3 - 2 * x);
    }
    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);
}

internal readonly record struct LightRgb(double Red, double Green, double Blue)
{
    public LightRgb Scale(double scale) => new(Red * scale, Green * scale, Blue * scale);
    public static LightRgb White => new(1, 1, 1);
}

// CSP reference inputs. Production currently supplies only replay/pause;
// unsupported countdown/flame/fuel/battery inputs are not manufactured.
internal readonly record struct CspLightContext
{
    public bool IsPaused { get; init; }
    public bool IsReplay { get; init; }
    public bool LowBattery { get; init; }
    public double? TimeToSessionStartMs { get; init; }
    public bool HazardLights { get; init; }
    public bool TurningLightsActivePhase { get; init; }
    public bool EngineFailed { get; init; }
    public bool LowFuel { get; init; }
    public bool FlamesActive { get; init; }
    public bool HeadlightsActive { get; init; }
    public bool LowBeams { get; init; }
    public LightRgb HeadlightsColor { get; init; }
    public LightRgb ReplayColor { get; init; }
}

// Reference port: CSP led_colors.lua and dualsense_lights.lua, MIT,
// (c) 2022 Ilja Jusupov, revision 2031bd2d7c913e0aad3ff18a3bab86ffccad1326.
internal static class CspLightMath
{
    // Strict comparisons and priority order match the reference Lua. Five
    // white LEDs never blink; neutral/top gear are not special cases.
    internal static RpmLightFrame Compute(double rpm, int gear, RpmLightMode mode,
        double flashing, double preciseClock, in CspLightContext context)
    {
        double ledRpm = context.IsReplay ? 0 : rpm;
        byte brightness = ledRpm > 0.5 ? (byte)0 : ledRpm > 0.25 ? (byte)1 : (byte)2;
        byte mask = 0;
        for (int i = 0; i < 5; i++)
            if (ledRpm > i / 4d) mask |= (byte)(1 << i);

        LightRgb color;
        bool flashOff = flashing * 3 % 1 > 0.5;
        if (context.LowBattery)
            color = preciseClock % 1 > 0.5 ? new(1, 0, 0) : default;
        else if (context.IsReplay)
            color = context.ReplayColor;
        else if (context.TimeToSessionStartMs is double start && start > -1000)
            color = start > 0 ? (start < 6700 ? new(1, 0, 0) : default)
                : flashOff ? new(0, 1, 0) : default;
        else if (gear == -1)
            color = LightRgb.White;
        else if (context.EngineFailed || context.HazardLights)
            color = context.TurningLightsActivePhase ? new(1, 0.5, 0) : default;
        else if (context.LowFuel)
            color = new(1, 0.15, 0);
        else if (context.FlamesActive)
            color = new(1, 1, 0);
        else if (rpm > 0.9 && flashOff)
            color = default;
        else
        {
            LightRgb basis = context.HeadlightsActive
                ? context.HeadlightsColor.Scale(context.LowBeams ? 0.1 : 0.2) : default;
            double mix = Math.Clamp(rpm * 2 + 1, 0, 1);
            if (mix <= 0) color = basis;
            else if (mode == RpmLightMode.Discrete)
                color = rpm > 0.6 ? new(1, 0, 0) : rpm > 0.2 ? new(1, 1, 0) : new(0, 1, 0);
            else
            {
                // HSV(90 * saturate(1-rpm), 1, 1), not a linear RGB blend.
                double hue = 90 * Math.Clamp(1 - rpm, 0, 1);
                LightRgb shifting = hue >= 60 ? new(2 - hue / 60, 1, 0) : new(1, hue / 60, 0);
                color = new(basis.Red + (shifting.Red - basis.Red) * mix,
                    basis.Green + (shifting.Green - basis.Green) * mix,
                    basis.Blue + (shifting.Blue - basis.Blue) * mix);
            }
        }
        return new(ToByte(color.Red), ToByte(color.Green), ToByte(color.Blue), mask, brightness);
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);
}
