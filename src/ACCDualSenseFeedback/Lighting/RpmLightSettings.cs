using System.Text.Json.Serialization;

namespace ACCDualSenseFeedback.Lighting;

[JsonConverter(typeof(JsonStringEnumConverter<RpmLightMode>))]
internal enum RpmLightMode { Smooth, Discrete }

// Lighting has its own settings and lifetime. Feedback presets never change it.
internal sealed record RpmLightSettings
{
    public static readonly RpmLightSettings Default = new();
    public bool Enabled { get; init; } = true;
    public RpmLightMode Mode { get; init; } = RpmLightMode.Smooth;

    public RpmLightSettings Sanitize() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : RpmLightMode.Smooth
    };
}

internal sealed class RpmLightSettingsState(RpmLightSettings initial)
{
    private RpmLightSettings _current = initial.Sanitize();
    public RpmLightSettings Current => Volatile.Read(ref _current);
    public void Update(RpmLightSettings value) => Volatile.Write(ref _current, value.Sanitize());
}

internal readonly record struct RpmLightFrame(byte Red, byte Green, byte Blue, byte PlayerMask,
    byte PlayerBrightness = 2)
{
    public static readonly RpmLightFrame Off = default;
}
