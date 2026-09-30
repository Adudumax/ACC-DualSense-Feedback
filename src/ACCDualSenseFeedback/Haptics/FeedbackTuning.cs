using ACCDualSenseFeedback.Hardware.DualSense;

namespace ACCDualSenseFeedback.Haptics;

internal enum FeedbackPreset
{
    Default,
    Custom
}

internal sealed record FeedbackProfile
{
    public const int NeutralValue = 50;
    public const int DefaultRedlineStartPermille = 962;
    public const int DefaultEngineTexture = 20;
    public const int DefaultCollisionFeedback = 60;

    public static readonly FeedbackProfile Default = new();

    public FeedbackPreset ActivePreset { get; init; } = FeedbackPreset.Default;

    public int BrakeResistance { get; init; } = NeutralValue;
    public int ThrottleResistance { get; init; } = NeutralValue;

    public int AbsPulse { get; init; } = NeutralValue;
    public int LockPulse { get; init; } = NeutralValue;

    public int ShiftKick { get; init; } = NeutralValue;
    public int RedlinePulse { get; init; } = NeutralValue;
    public int RedlineStartPermille { get; init; } = DefaultRedlineStartPermille;
    public int EngineTexture { get; init; } = DefaultEngineTexture;

    public int TcPulse { get; init; } = NeutralValue;
    public int WheelspinPulse { get; init; } = NeutralValue;

    public int RoadSurface { get; init; } = NeutralValue;
    public int GripLoss { get; init; } = NeutralValue;
    public int NativeAcc { get; init; } = NeutralValue;
    public int CollisionFeedback { get; init; } = DefaultCollisionFeedback;

    public FeedbackProfile Sanitize()
        => this with
        {
            ActivePreset = Enum.IsDefined(ActivePreset) ? ActivePreset : FeedbackPreset.Default,
            BrakeResistance = ClampStrength(BrakeResistance),
            ThrottleResistance = ClampStrength(ThrottleResistance),
            AbsPulse = ClampStrength(AbsPulse),
            LockPulse = ClampStrength(LockPulse),
            ShiftKick = ClampStrength(ShiftKick),
            RedlinePulse = ClampStrength(RedlinePulse),
            RedlineStartPermille = Math.Clamp(RedlineStartPermille, 940, 990),
            EngineTexture = ClampStrength(EngineTexture),
            TcPulse = ClampStrength(TcPulse),
            WheelspinPulse = ClampStrength(WheelspinPulse),
            RoadSurface = ClampStrength(RoadSurface),
            GripLoss = ClampStrength(GripLoss),
            NativeAcc = ClampStrength(NativeAcc),
            CollisionFeedback = ClampStrength(CollisionFeedback)
        };

    public FeedbackTuning ToTuning()
    {
        if (ActivePreset == FeedbackPreset.Default)
            return FeedbackTuning.Neutral;

        return new FeedbackTuning(
            ScaleIntensity(BrakeResistance, 1.35f),
            ScaleIntensity(ThrottleResistance, 1.35f),
            ScaleIntensity(AbsPulse, 1.55f),
            ScaleIntensity(LockPulse, 1.55f),
            ScaleIntensity(ShiftKick, 1.55f),
            ScaleIntensity(RedlinePulse, 1.50f),
            ScaleIntensity(TcPulse, 1.50f),
            ScaleIntensity(WheelspinPulse, 1.55f),
            ScaleIntensity(RoadSurface, 1.45f),
            ScaleIntensity(GripLoss, 1.45f),
            ScaleIntensity(NativeAcc, 1.35f),
            ScaleCalibrated(EngineTexture, DefaultEngineTexture, 2.00f),
            ScaleCalibrated(CollisionFeedback, DefaultCollisionFeedback, 1.50f),
            RedlineStartPermille / 1000f);
    }

    private static int ClampStrength(int value) => Math.Clamp(value, 0, 100);

    private static float ScaleIntensity(int value, float maximum)
    {
        if (value <= 0)
            return 0f;

        int clamped = ClampStrength(value);
        if (clamped <= NeutralValue)
        {
            float normalized = clamped / (float)NeutralValue;
            return MathF.Pow(normalized, 0.82f);
        }

        return Lerp(1f, maximum, (clamped - NeutralValue) / (float)NeutralValue);
    }

    private static float Lerp(float from, float to, float amount) => from + (to - from) * amount;

    private static float ScaleCalibrated(int value, int calibratedValue, float maximum)
    {
        int clamped = ClampStrength(value);
        if (clamped <= 0)
            return 0f;
        if (clamped <= calibratedValue)
            return clamped / (float)calibratedValue;
        return Lerp(1f, maximum, (clamped - calibratedValue) / (float)(100 - calibratedValue));
    }
}

internal sealed class FeedbackProfileState
{
    private FeedbackProfile _current;

    public FeedbackProfileState(FeedbackProfile initial) => _current = initial.Sanitize();

    public FeedbackProfile Current => Volatile.Read(ref _current);

    public FeedbackTuning CurrentTuning => Current.ToTuning();

    public void Update(FeedbackProfile profile) => Volatile.Write(ref _current, profile.Sanitize());
}

internal readonly record struct FeedbackTuning(
    float BrakeResistanceScale,
    float ThrottleResistanceScale,
    float AbsPulseScale,
    float LockPulseScale,
    float ShiftKickScale,
    float RedlinePulseScale,
    float TcPulseScale,
    float WheelspinPulseScale,
    float RoadSurfaceScale,
    float GripLossScale,
    float NativeAccScale,
    float EngineTextureScale,
    float CollisionFeedbackScale,
    float RedlineStartRatio)
{
    public static readonly FeedbackTuning Neutral = new(
        1f, 1f,
        1f, 1f,
        1f, 1f,
        1f, 1f,
        1f, 1f, 1f,
        1f, 1f,
        0.962f);

    public static TriggerEffect ScaleTrigger(in TriggerEffect effect, float scale)
    {
        if (effect.Mode == 0x05)
            return effect;
        if (scale <= 0.001f)
            return TriggerEffect.Off;
        if (MathF.Abs(scale - 1f) < 0.0001f)
            return effect;

        if (effect.Mode == 0x01)
        {
            return effect with
            {
                P1 = ScaleByte(effect.P1, scale)
            };
        }

        if (effect.Mode is not (0x21 or 0x26))
            return effect;

        ushort active = (ushort)(effect.P0 | effect.P1 << 8);
        uint packed = (uint)(effect.P2 |
            effect.P3 << 8 |
            effect.P4 << 16 |
            effect.P5 << 24);
        uint scaled = 0;

        for (int index = 0; index < 10; index++)
        {
            if ((active & (1 << index)) == 0)
                continue;

            int strength = (int)((packed >> (index * 3)) & 0x07) + 1;
            int adjusted = Math.Clamp((int)MathF.Round(strength * scale), 1, 8);
            scaled |= (uint)(adjusted - 1) << (index * 3);
        }

        return effect with
        {
            P2 = (byte)scaled,
            P3 = (byte)(scaled >> 8),
            P4 = (byte)(scaled >> 16),
            P5 = (byte)(scaled >> 24)
        };
    }

    private static byte ScaleByte(byte value, float scale)
        => (byte)Math.Clamp((int)MathF.Round(value * scale), 0, byte.MaxValue);
}
