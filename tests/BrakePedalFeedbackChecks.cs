using System.Diagnostics;
using System.Reflection;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Hardware.DualSense;
using ACCDualSenseFeedback.Telemetry;

internal static class BrakePedalFeedbackChecks
{
    private static readonly byte[] PreviousZones = [3, 4, 4, 5, 5, 6, 6, 7, 7, 8];
    private static readonly byte[] CandidateZones = [3, 4, 4, 5, 6, 6, 7, 7, 8, 8];
    private static long At(int ms) => (long)((1 + ms / 1000d) * Stopwatch.Frequency);
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    // Decode the serialized command bit by bit, independently of production's
    // uint packing and BrakeSupportCapture's diagnostic decoder.
    private static int[] Decode(in TriggerEffect effect)
    {
        Span<byte> command = stackalloc byte[11]; effect.WriteTo(command);
        var zones = new int[10];
        if (command[0] == 0x05) return zones;
        Assert(command[0] is 0x21 or 0x26, "unexpected trigger mode");
        for (int zone = 0; zone < 10; zone++)
        {
            if ((command[1 + zone / 8] & (1 << (zone % 8))) == 0) continue;
            int encoded = 0;
            for (int bit = 0; bit < 3; bit++)
            {
                int packedBit = zone * 3 + bit;
                encoded |= ((command[3 + packedBit / 8] >> (packedBit % 8)) & 1) << bit;
            }
            zones[zone] = encoded + 1;
        }
        return zones;
    }

    private static void SameBytes(in TriggerEffect actual, in TriggerEffect expected, string message)
    {
        Span<byte> a = stackalloc byte[11], b = stackalloc byte[11];
        actual.WriteTo(a); expected.WriteTo(b);
        Assert(a.SequenceEqual(b), message);
    }

    private static FeedbackTuning Tuning(int level) => new FeedbackProfile
    {
        ActivePreset = FeedbackPreset.Custom, BrakeResistance = level, ThrottleResistance = level
    }.ToTuning();

    // A frozen reference for the old event command, independent of the new helper.
    private static TriggerEffect PreviousAbs(float envelope, float eventScale)
    {
        if (eventScale <= 0 || envelope < .035f) return TriggerEffect.Off;
        float x = Math.Clamp((envelope - .035f) / (.75f - .035f), 0, 1);
        float smooth = x * x * (3 - 2 * x);
        int calibrated = Math.Clamp(2 + (int)MathF.Floor(smooth * 4), 2, 5);
        byte amplitude = (byte)Math.Clamp((int)MathF.Round(calibrated * eventScale), 1, 8);
        return TriggerEffect.Vibration(2, amplitude, 15);
    }

    internal static void AssertAllowedL2(in TriggerEffect actual, in TriggerEffect baseline,
        in FeedbackTuning tuning, object frozenEngine, string context)
    {
        if (baseline.Mode == 0x21)
        {
            Assert(actual == BrakePedalFeedback.CreateResistance(tuning.BrakeResistanceScale),
                "wrong resting L2: " + context);
            return;
        }
        if (baseline.Mode == 0x26 && baseline.P8 == 15)
        {
            // Reading the frozen state is an integration check: ABS signal and
            // attack/release history must still feed the same event envelope.
            FieldInfo field = frozenEngine.GetType().GetField("_absEnvelope", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("frozen ABS envelope unavailable");
            float envelope = (float)field.GetValue(frozenEngine)!;
            Assert(actual == BrakePedalFeedback.BuildAbs(tuning.BrakeResistanceScale, envelope, tuning.AbsPulseScale),
                "L2 uses a different ABS envelope or profile: " + context);
            if (tuning.BrakeResistanceScale == 0)
                SameBytes(actual, baseline, "ABS-only command changed: " + context);
            return;
        }
        // This intentionally includes 8 Hz mechanical lock: it is not adapted.
        SameBytes(actual, baseline, "non-ABS L2 event changed: " + context);
    }

    private static TelemetrySnapshot Scenario(int scenario, int ms)
    {
        var sample = new TelemetrySnapshot
        {
            PacketId = 100 + ms, ObservedTimestamp = At(ms),
            Gear = 4, Rpm = 5000, CurrentMaxRpm = 8000, IsEngineRunning = 1,
            Gas = .5f, SpeedKmh = 100, LocalVelocityZ = 100f / 3.6f,
            LocalAngularVelocityY = .25f, LocalYawRate = .25f, SteerAngle = .2f,
            SlipAngleFl = .04f, SlipAngleFr = .04f, SlipAngleRl = .04f, SlipAngleRr = .04f,
            WheelLoadFl = 3000, WheelLoadFr = 3000, WheelLoadRl = 3500, WheelLoadRr = 3500,
            SuspensionFl = .03f, SuspensionFr = .03f, SuspensionRl = .03f, SuspensionRr = .03f
        };
        if (scenario is >= 1 and <= 4)
        {
            bool right = scenario is 2 or 4, grass = scenario >= 3;
            sample.KerbVibration = grass ? 0 : .7f; sample.GVibration = grass ? .8f : 0;
            float shock = ms % 80 < 40 ? .025f : .035f;
            if (right) sample.SuspensionFr = sample.SuspensionRr = shock;
            else sample.SuspensionFl = sample.SuspensionRl = shock;
            if (grass)
            {
                sample.NumberOfTyresOut = 2;
                if (right) sample.TyreDirtyFr = sample.TyreDirtyRr = .8f;
                else sample.TyreDirtyFl = sample.TyreDirtyRl = .8f;
            }
        }
        if (scenario == 5 && ms is >= 200 and < 1200)
        {
            sample.SlipAngleFl = sample.SlipAngleFr = .30f;
            sample.SlipVibration = .9f; sample.LocalVelocityX = 1.4f;
        }
        if (scenario == 6 && ms is >= 200 and < 900)
        {
            sample.SlipAngleRl = sample.SlipAngleRr = .40f;
            sample.SlipVibration = 1; sample.LocalVelocityX = 10;
        }
        else if (scenario == 6 && ms is >= 900 and < 1100)
        {
            sample.SlipAngleFl = sample.SlipAngleFr = .26f;
            sample.SlipAngleRl = sample.SlipAngleRr = .14f;
            sample.LocalVelocityX = 6; sample.SteerAngle = -.4f;
            sample.LocalAngularVelocityY = sample.LocalYawRate = -.2f;
        }
        if (scenario is 7 or 8)
        {
            sample.Gas = 1; sample.Rpm = ms < 800 ? 7950 : 6000; sample.Gear = ms < 800 ? 3 : 4;
            if (ms is >= 250 and < 700)
            {
                sample.SlipRatioRl = sample.SlipRatioRr = .7f;
                sample.SlipVibration = 1; sample.TcIntervention = scenario == 7 ? 1 : 0;
            }
        }
        if ((scenario is 9 or 10) && ms is >= 200 and < 1000)
        {
            sample.Gas = 0; sample.Brake = .85f;
            sample.SlipRatioFl = sample.SlipRatioFr = .65f; sample.SlipVibration = .8f;
            if (scenario == 9) { sample.Abs = 1; sample.AbsLevel = 1; sample.AbsVibration = .7f; }
        }
        if (scenario == 11 && ms >= 500)
        {
            sample.CarDamageRight = .5f;
            if (ms < 550) { sample.GForceX = 3; sample.LocalVelocityX = 2; }
        }
        return sample;
    }

    private static void SameNonL2Wire(in FeedbackFrame actual, in FeedbackFrame baseline)
    {
        Span<byte> a = stackalloc byte[78], b = stackalloc byte[78];
        foreach (bool bluetooth in new[] { false, true })
        {
            int length = DualSenseReportBuilder.Build(actual, bluetooth, a);
            Assert(length == DualSenseReportBuilder.Build(baseline, bluetooth, b), "HID report length changed");
            int leftStart = bluetooth ? 23 : 22;
            for (int i = 0; i < length; i++)
                if (!(i >= leftStart && i < leftStart + 11) && !(bluetooth && i >= 74))
                    Assert(a[i] == b[i], $"HID byte outside L2 changed: {bluetooth}/{i}");
        }
    }

    public static void Run(Action<string, Action> check)
    {
        check("Zoned vibration has independent wire decoding and retains every legacy uniform command", () =>
        {
            byte[] mixed = [0, 0, 1, 2, 3, 4, 5, 6, 7, 8];
            var effect = TriggerEffect.VibrationZones(mixed, 15);
            Span<byte> bytes = stackalloc byte[11]; effect.WriteTo(bytes);
            Assert(bytes.SequenceEqual(new byte[] { 38, 252, 3, 0, 162, 177, 62, 0, 0, 15, 0 }), "zoned vibration wire layout changed");
            Assert(Decode(effect).SequenceEqual(mixed.Select(x => (int)x)), "independent decoder cannot recover mixed zones");
            Assert(TriggerEffect.VibrationZones(new byte[10], 15) == TriggerEffect.Off, "all-zero zones activate vibration");
            Assert(TriggerEffect.VibrationZones(mixed, 0) == TriggerEffect.Off, "zero frequency activates vibration");
            for (byte start = 0; start < 10; start++)
            for (byte amplitude = 1; amplitude <= 8; amplitude++)
            foreach (byte frequency in new byte[] { 7, 8, 15, 18, 28, 40 })
            {
                var zones = new byte[10]; Array.Fill(zones, amplitude, start, 10 - start);
                SameBytes(TriggerEffect.VibrationZones(zones, frequency), TriggerEffect.Vibration(start, amplitude, frequency),
                    "uniform zoned encoding differs from legacy vibration");
            }
        });

        check("L2 resting profile never weakens frozen .3 at any UI level and retains original scaling", () =>
        {
            for (int level = 0; level <= 100; level++)
            {
                var tuning = Tuning(level);
                float scale = level == 0 ? 0 : level <= 50 ? MathF.Pow(level / 50f, .82f) : 1 + .35f * (level - 50) / 50f;
                Assert(MathF.Abs(tuning.BrakeResistanceScale - scale) < 1e-6f &&
                    MathF.Abs(tuning.ThrottleResistanceScale - scale) < 1e-6f, "shared slider scaling changed");
                var effect = BrakePedalFeedback.CreateResistance(tuning.BrakeResistanceScale);
                if (level == 0) { Assert(effect == TriggerEffect.Off, "zero brake still resists"); continue; }
                int[] actual = Decode(effect);
                for (int i = 0; i < 10; i++)
                {
                    int expected = Math.Clamp((int)MathF.Round(CandidateZones[i] * scale), 1, 8);
                    int previous = Math.Clamp((int)MathF.Round(PreviousZones[i] * scale), 1, 8);
                    Assert(actual[i] == expected && actual[i] >= previous, $"wrong/weakened resting zone at {level}/{i}");
                    if (i > 0) Assert(actual[i] >= actual[i - 1], "resting resistance decreases with position");
                }
                if (level == 50) Assert(actual.SequenceEqual(new[] { 3, 4, 4, 5, 6, 6, 7, 7, 8, 8 }), "neutral profile changed");
                if (level == 100) Assert(actual.SequenceEqual(new[] { 4, 5, 5, 7, 8, 8, 8, 8, 8, 8 }), "maximum saturation was silently changed");
            }
        });

        check("L2 zero controls, ABS-only bytes and event onset remain independent", () =>
        {
            foreach (float brake in new[] { 0f, .2f, 1f, 1.35f })
            foreach (float envelope in new[] { 0f, .034999f, .035f, .08f, .18f, .4f, .75f, 1f })
            foreach (float abs in new[] { 0f, .001f, .05f, .5f, 1f, 1.55f })
            {
                var actual = BrakePedalFeedback.BuildAbs(brake, envelope, abs);
                if (abs == 0 || envelope < .035f)
                    SameBytes(actual, BrakePedalFeedback.CreateResistance(brake), "ABS off or below threshold did not restore resistance");
                else if (brake == 0)
                    SameBytes(actual, PreviousAbs(envelope, abs), "turning off brake resistance changed ABS-only command");
                if (actual.Mode == 0x26)
                {
                    Assert(actual.P0 == 0xFC && actual.P1 == 0x03 && actual.P8 == 15,
                        "ABS enabled a new start position or changed frequency");
                    Assert(actual.P6 == 0 && actual.P7 == 0 && actual.P9 == 0, "ABS wrote unrelated trigger fields");
                }
            }
            Assert(Decode(BrakePedalFeedback.BuildAbs(1.35f, 1, .1f)).SequenceEqual(new[] { 0, 0, 1, 1, 1, 1, 1, 1, 1, 1 }),
                "low ABS was quantized to one before support gain and artificially amplified");
            Assert(Decode(BrakePedalFeedback.BuildAbs(1, 1, 1)).SequenceEqual(new[] { 0, 0, 5, 6, 6, 6, 7, 7, 8, 8 }),
                "neutral ABS profile differs from the agreed candidate");
        });

        check("L2 ABS remains monotone in position, support, event strength and the original envelope", () =>
        {
            foreach (float brake in new[] { 0f, .2f, 1f, 1.35f })
            foreach (float envelope in new[] { .035f, .15f, .4f, 1f })
            {
                int[] previous = new int[10];
                for (int level = 1; level <= 100; level++)
                {
                    float scale = new FeedbackProfile { ActivePreset = FeedbackPreset.Custom, AbsPulse = level }.ToTuning().AbsPulseScale;
                    int[] actual = Decode(BrakePedalFeedback.BuildAbs(brake, envelope, scale));
                    int[] old = Decode(PreviousAbs(envelope, scale));
                    for (int i = 2; i < 10; i++)
                    {
                        Assert(actual[i] >= previous[i] && actual[i] >= old[i], "ABS strength falls with its slider or below legacy");
                        if (i > 2) Assert(actual[i] >= actual[i - 1], "ABS profile falls toward the end of travel");
                    }
                    previous = actual;
                }
            }
            foreach (float abs in new[] { .05f, .5f, 1f, 1.55f })
            {
                int[] previous = new int[10];
                for (int step = 0; step <= 200; step++)
                {
                    int[] actual = Decode(BrakePedalFeedback.BuildAbs(1, .035f + step * .965f / 200, abs));
                    for (int i = 2; i < 10; i++) Assert(actual[i] >= previous[i], "ABS envelope increase reduced amplitude");
                    previous = actual;
                }
                previous = new int[10];
                for (int step = 0; step <= 100; step++)
                {
                    int[] actual = Decode(BrakePedalFeedback.BuildAbs(step * 1.35f / 100, .5f, abs));
                    for (int i = 2; i < 10; i++) Assert(actual[i] >= previous[i], "brake support increase reduced ABS amplitude");
                    previous = actual;
                }
            }
        });

        check("L2 support preserves frozen .3 non-L2 frames, rear drag, R2 voices and exact 8 Hz lock", () =>
        {
            int rows = 0, absRows = 0, lockRows = 0, rearRows = 0, changedResting = 0, changedAbs = 0;
            var rightFrequencies = new HashSet<byte>();
            foreach (int level in new[] { 0, 25, 50, 100 })
            foreach (int scenario in Enumerable.Range(0, 12))
            {
                var tuning = Tuning(level);
                var current = new CompetitionFeedbackEngine(); var frozen = new FrozenGrip3CompetitionFeedbackEngine();
                for (int ms = 0; ms < 1800; ms += 4)
                {
                    var sample = Scenario(scenario, ms);
                    byte large = (byte)(70 + ms % 60), small = (byte)(40 + ms % 40);
                    var a = current.Compute(sample, At(ms), large, small, tuning);
                    var b = frozen.Compute(sample, At(ms), large, small, tuning);
                    Assert((a with { LeftTrigger = b.LeftTrigger }) == b, $"change escaped L2 at {level}/{scenario}/{ms}");
                    Assert(current.RearDragCue == frozen.RearDragCue, "rear drag state changed");
                    AssertAllowedL2(a.LeftTrigger, b.LeftTrigger, tuning, frozen, $"{level}/{scenario}/{ms}");
                    if (b.LeftTrigger.Mode == 0x26 && b.LeftTrigger.P8 == 15)
                    { absRows++; if (a.LeftTrigger != b.LeftTrigger) changedAbs++; }
                    if (b.LeftTrigger.Mode == 0x26 && b.LeftTrigger.P8 == 8)
                    { lockRows++; SameBytes(a.LeftTrigger, b.LeftTrigger, "mechanical lock changed"); }
                    if (b.LeftTrigger.Mode == 0x21 && a.LeftTrigger != b.LeftTrigger) changedResting++;
                    if (b.RightTrigger.Mode == 0x26) rightFrequencies.Add(b.RightTrigger.P8);
                    if (frozen.RearDragCue.Left > 0 || frozen.RearDragCue.Right > 0) rearRows++;
                    SameNonL2Wire(a, b); rows++;
                }
            }
            Assert(absRows > 500 && lockRows > 500 && rearRows > 500 && changedResting > 500 && changedAbs > 100,
                "differential coverage lacks ABS, lock, rear drag or actual candidate changes");
            Assert(rightFrequencies.SetEquals(new byte[] { 7, 18, 28, 40 }), "R2 TC, spin, warning and shift were not all exercised");
            Console.WriteLine($"L2 SUPPORT DIFFERENTIAL: {rows} frames; {absRows} ABS, {lockRows} exact lock, {rearRows} rear; {changedResting} resting and {changedAbs} ABS changes; all non-L2 frame/wire fields preserved");
        });
    }
}
