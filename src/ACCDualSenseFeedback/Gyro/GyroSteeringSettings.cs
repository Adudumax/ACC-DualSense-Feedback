using System.Text.Json.Serialization;

namespace ACCDualSenseFeedback.Gyro;

[JsonConverter(typeof(JsonStringEnumConverter<GyroResponseCurve>))]
internal enum GyroResponseCurve { Linear, SoftCenter, Custom }
[JsonConverter(typeof(JsonStringEnumConverter<GyroSmoothing>))]
internal enum GyroSmoothing { Off, Light, Medium, Strong, Custom }
[JsonConverter(typeof(JsonStringEnumConverter<GyroReferenceMode>))]
internal enum GyroReferenceMode { CurrentPose, Gravity }
[JsonConverter(typeof(JsonStringEnumConverter<GyroActivationMode>))]
internal enum GyroActivationMode { Always, Hold, Toggle }
[Flags, JsonConverter(typeof(JsonStringEnumConverter<GyroButton>))]
internal enum GyroButton : ulong
{
    None = 0, Cross = 1, Circle = 2, Square = 4, Triangle = 8,
    Share = 16, Options = 32, L1 = 64, R1 = 128, L3 = 256, R3 = 512,
    PS = 1024, Touchpad = 2048,
    // Append only: existing numeric and named bindings remain compatible.
    Mute = 1UL << 12, DpadUp = 1UL << 13, DpadRight = 1UL << 14,
    DpadDown = 1UL << 15, DpadLeft = 1UL << 16, L2 = 1UL << 17, R2 = 1UL << 18,
    EdgeFnLeft = 1UL << 19, EdgeFnRight = 1UL << 20,
    EdgePaddleLeft = 1UL << 21, EdgePaddleRight = 1UL << 22
}

internal sealed record GyroSteeringSettings
{
    public static readonly GyroSteeringSettings Default = new();
    public bool Enabled { get; init; }
    public float FullLockDegrees { get; init; } = 90;
    public float DeadzoneDegrees { get; init; } = 1;
    public GyroResponseCurve Curve { get; init; }
    public GyroSmoothing Smoothing { get; init; } = GyroSmoothing.Light;
    public GyroReferenceMode ReferenceMode { get; init; }
    public GyroActivationMode ActivationMode { get; init; }
    public bool Invert { get; init; }
    public bool StickPriority { get; init; } = true;
    public float TakeoverPercent { get; init; } = 20;
    public float ResponseExponent { get; init; } = 1;
    public float CustomSmoothingMilliseconds { get; init; } = 8;
    public GyroButton RecenterButton { get; init; }
    public GyroButton ActivationButton { get; init; }
    public bool ForwardShortcutButtons { get; init; }

    public GyroSteeringSettings Sanitize() => this with
    {
        FullLockDegrees = Clamp(FullLockDegrees, 30, 120, 90),
        DeadzoneDegrees = Clamp(DeadzoneDegrees, 0, 5, 1),
        TakeoverPercent = Clamp(TakeoverPercent, 10, 50, 20),
        ResponseExponent = Clamp(ResponseExponent, .5f, 3, 1),
        CustomSmoothingMilliseconds = Clamp(CustomSmoothingMilliseconds, 0, 30, 8),
        Curve = Enum.IsDefined(Curve) ? Curve : GyroResponseCurve.Linear,
        Smoothing = Enum.IsDefined(Smoothing) ? Smoothing : GyroSmoothing.Light,
        ReferenceMode = Enum.IsDefined(ReferenceMode) ? ReferenceMode : GyroReferenceMode.CurrentPose,
        ActivationMode = Enum.IsDefined(ActivationMode) ? ActivationMode : GyroActivationMode.Always,
        // Retired UI options must not remain active through an older portable settings file.
        RecenterButton = GyroButton.None,
        ForwardShortcutButtons = false,
        ActivationButton = IsSingleButton(ActivationButton) ? ActivationButton : GyroButton.None
    };

    public GyroSteeringSettings ResetSteeringParameters() => (Default with
    {
        Enabled = Enabled, ActivationButton = ActivationButton
    }).Sanitize();
    [JsonIgnore] public float EffectiveExponent => Curve switch { GyroResponseCurve.SoftCenter => 1.6f, GyroResponseCurve.Custom => ResponseExponent, _ => 1 };
    [JsonIgnore] public float SmoothingMilliseconds => Smoothing switch
    {
        GyroSmoothing.Light => 8, GyroSmoothing.Medium => 16, GyroSmoothing.Strong => 30,
        GyroSmoothing.Custom => CustomSmoothingMilliseconds, _ => 0
    };
    private static float Clamp(float value, float min, float max, float fallback) => float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    internal static bool IsSingleButton(GyroButton button) => button == GyroButton.None || (Enum.IsDefined(button) && (((ulong)button & ((ulong)button - 1)) == 0));
}

/// <summary>Immutable snapshots shared by GUI, console and input thread. Preset gating belongs to the input context.</summary>
internal static class GyroSettingsState
{
    private static GyroSteeringSettings _current = GyroSteeringSettings.Default;
    public static GyroSteeringSettings Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value.Sanitize());
    }
}
