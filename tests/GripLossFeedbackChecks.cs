using System.Diagnostics;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Telemetry;

internal static class GripLossFeedbackChecks
{
    private static long At(int ms) => (long)((1 + ms / 1000.0) * Stopwatch.Frequency);
    private static void Assert(bool ok, string why)
    { if (!ok) throw new InvalidOperationException(why); }
    private static TelemetrySnapshot Car(int ms, float front = .04f, float rear = .04f, float lateralVelocity = 0)
        => new()
        {
            PacketId = ms + 100, ObservedTimestamp = At(ms),
            SpeedKmh = 100, LocalVelocityZ = 100f / 3.6f, LocalVelocityX = lateralVelocity,
            LocalAngularVelocityY = .25f, LocalYawRate = .25f, SteerAngle = .2f,
            Gear = 4, Rpm = 5000, CurrentMaxRpm = 8000, IsEngineRunning = 1,
            SlipAngleFl = front, SlipAngleFr = front, SlipAngleRl = rear, SlipAngleRr = rear,
            Gas = 0, SlipVibration = 0
        };
    private static RearDragCueFrame Update(ref RearDragFeedbackState state, int ms,
        float front = .04f, float rear = .04f, float lateralVelocity = 0, float scale = 1)
        => state.Update(Car(ms, front, rear, lateralVelocity), scale, .004f, At(ms));
    private static bool HasOutput(in RearDragCueFrame cue)
        => cue.Strength > 0 || cue.Left > 0 || cue.Right > 0;
    private static float Combine(float baseline, float cue)
        => 1f - (1f - Math.Clamp(baseline, 0, 1)) * (1f - Math.Clamp(cue, 0, 1));
    private static byte ToByte(float value)
        => (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);

    // Rear-only renderer retained independently from griploss.1, preserved at
    // artifacts/grip-correction-20261024/before/GripLossFeedbackState.cs.
    // Its old event detector is intentionally not imported into this reference.
    private static (float Left, float Right) Griploss1RearVoice(float strength, float age, float scale)
    {
        float grain36 = .5f + .5f * MathF.Cos(age * 36f * MathF.Tau);
        float grain45 = .5f + .5f * MathF.Cos(age * 4.5f * MathF.Tau);
        float rear = strength * (.64f + .36f * grain36) * (.88f + .12f * grain45);
        float left = (1f - (1f - 0f) * (1f - rear * .52f)) * Math.Clamp(scale, 0f, 1.5f);
        float right = (1f - (1f - 0f) * (1f - rear * .31f)) * Math.Clamp(scale, 0f, 1.5f);
        return (Math.Clamp(left, 0f, 1f), Math.Clamp(right, 0f, 1f));
    }
    private static readonly FeedbackTuning OnlyGrip = FeedbackTuning.Neutral with
    {
        BrakeResistanceScale = 0, ThrottleResistanceScale = 0,
        AbsPulseScale = 0, LockPulseScale = 0, ShiftKickScale = 0,
        RedlinePulseScale = 0, TcPulseScale = 0, WheelspinPulseScale = 0,
        RoadSurfaceScale = 0, NativeAccScale = 0, EngineTextureScale = 0,
        CollisionFeedbackScale = 0
    };
    private static void AddRoad(ref TelemetrySnapshot sample, int ms, bool right, bool grass)
    {
        sample.KerbVibration = grass ? 0 : .7f;
        sample.GVibration = grass ? .8f : 0;
        float shock = ms % 80 < 40 ? .025f : .035f;
        sample.SuspensionFl = right ? .03f : shock;
        sample.SuspensionFr = right ? shock : .03f;
        sample.SuspensionRl = right ? .03f : shock;
        sample.SuspensionRr = right ? shock : .03f;
    }

    public static void Run(Action<string, Action> check)
    {
        check("Normal driving, stable loaded turns and front-only understeer retain tcgrip.4 grips and R2", () =>
        {
            foreach (int scenario in new[] { 0, 1, 2 })
            {
                var current = new CompetitionFeedbackEngine(); var frozen = new FrozenTcGrip4CompetitionFeedbackEngine();
                for (int ms = 0; ms < 3000; ms += 4)
                {
                    var sample = scenario switch
                    {
                        1 => Car(ms, .13f, .09f, (100f / 3.6f) * .06f),
                        2 => Car(ms, .30f, .06f, 1.4f),
                        _ => Car(ms)
                    };
                    sample.Gas = .5f;
                    var a = current.Compute(sample, At(ms), 70, 40, FeedbackTuning.Neutral);
                    var b = frozen.Compute(sample, At(ms), 70, 40, FeedbackTuning.Neutral);
                    Assert((a with { LeftTrigger = b.LeftTrigger }) == b && !HasOutput(current.RearDragCue), $"scenario {scenario} changed stable non-L2 output at {ms} ms");
                }
            }
        });
        check("Single-wheel left and right kerbs or grass cannot reopen the opposite grip", () =>
        {
            foreach (bool right in new[] { false, true })
            foreach (bool grass in new[] { false, true })
            {
                var current = new CompetitionFeedbackEngine(); var frozen = new FrozenTcGrip4CompetitionFeedbackEngine();
                for (int ms = 0; ms < 2200; ms += 4)
                {
                    var sample = Car(ms, .05f, .04f, 1.2f);
                    sample.SlipAngleRl = right ? .04f : .55f;
                    sample.SlipAngleRr = right ? .55f : .04f;
                    sample.SlipVibration = 1; AddRoad(ref sample, ms, right, grass);
                    var a = current.Compute(sample, At(ms), 70, 40, FeedbackTuning.Neutral);
                    var b = frozen.Compute(sample, At(ms), 70, 40, FeedbackTuning.Neutral);
                    Assert((a with { LeftTrigger = b.LeftTrigger }) == b && !HasOutput(current.RearDragCue),
                        $"{(right ? "right" : "left")} {(grass ? "grass" : "kerb")} changed at {ms} ms");
                }
            }
        });
        check("One-wheel road spikes stay quiet; paired rear slip or clear body motion can support a real event", () =>
        {
            foreach (bool right in new[] { false, true })
            {
                var state = new RearDragFeedbackState();
                for (int ms = 0; ms < 1800; ms += 4)
                {
                    var sample = Car(ms, .05f, .04f, (100f / 3.6f) * .06f);
                    if (right) sample.SlipAngleRr = .40f; else sample.SlipAngleRl = .40f;
                    Assert(!HasOutput(state.Update(sample, 1, .004f, At(ms))),
                        "one-wheel spike with normal body motion created rear drag");
                }
            }
            foreach (int mode in new[] { 0, 1, 2 })
            {
                var state = new RearDragFeedbackState(); RearDragCueFrame cue = default;
                for (int ms = 0; ms < 800; ms += 4)
                {
                    var sample = Car(ms, .05f, .40f, mode == 0 ? 0 : 8);
                    if (mode == 1) sample.SlipAngleRl = .04f;
                    if (mode == 2) sample.SlipAngleRr = .04f;
                    cue = state.Update(sample, 1, .004f, At(ms));
                }
                Assert(cue.Strength > .5f,
                    $"valid {(mode == 0 ? "paired tyres leading body motion" : "body-backed unilateral tyre")} evidence was rejected");
            }
        });
        check("Pure TC and pure longitudinal wheelspin preserve baseline grips and R2 without rear drag", () =>
        {
            foreach (bool tc in new[] { false, true })
            {
                var current = new CompetitionFeedbackEngine(); var frozen = new FrozenTcGrip4CompetitionFeedbackEngine();
                for (int ms = 0; ms < 1600; ms += 4)
                {
                    var sample = Car(ms, .05f, .06f);
                    sample.Gas = 1; sample.SlipRatioRl = sample.SlipRatioRr = .8f;
                    sample.TcIntervention = tc ? 1 : 0; sample.SlipVibration = 1;
                    var a = current.Compute(sample, At(ms), 70, 40, FeedbackTuning.Neutral);
                    var b = frozen.Compute(sample, At(ms), 70, 40, FeedbackTuning.Neutral);
                    Assert((a with { LeftTrigger = b.LeftTrigger }) == b, "longitudinal/TC-only event changed stable non-L2 output");
                    Assert(!HasOutput(current.RearDragCue), "longitudinal/TC-only event started rear drag");
                }
            }
        });
        check("Confirmed rear slide retains griploss.1 waveform, fixed rear-left signature and 12 ms attack", () =>
        {
            float maxVoiceError = 0; int comparedRows = 0;
            foreach (float scale in new[] { .3f, 1f, 1.5f })
            {
                var state = new RearDragFeedbackState(); int entered = -1; int steadyRows = 0; float previousStrength = 0;
                for (int ms = 0; ms < 1400; ms += 4)
                {
                    var cue = Update(ref state, ms, .07f, .40f, 10, scale);
                    if (!HasOutput(cue)) continue;
                    if (entered < 0) entered = ms;
                    var reference = Griploss1RearVoice(cue.Strength, (ms - entered) / 1000f, scale);
                    maxVoiceError = MathF.Max(maxVoiceError, MathF.Max(MathF.Abs(cue.Left - reference.Left), MathF.Abs(cue.Right - reference.Right)));
                    comparedRows++;
                    Assert(MathF.Abs(cue.Left - reference.Left) < .00002f && MathF.Abs(cue.Right - reference.Right) < .00002f,
                        $"rear voice diverged from .1 renderer at age {ms - entered} ms");
                    Assert(cue.Left > cue.Right && cue.Right > 0, "confirmed rear drag lost .1 side signature");
                    float expectedStrength = previousStrength + (1f - previousStrength) * (1f - MathF.Exp(-.004f / .012f));
                    Assert(MathF.Abs(cue.Strength - expectedStrength) < .00002f,
                        $".1 12 ms attack changed ({cue.Strength:F6} vs {expectedStrength:F6})");
                    previousStrength = cue.Strength;
                    if (ms > 700 && cue.Strength > .99f) steadyRows++;
                }
                Assert(entered >= 0 && steadyRows > 100, "full-strength rear waveform never exercised");
            }
            Console.WriteLine($"REAR VOICE REFERENCE: {comparedRows} dense-grid rows, maximum absolute normalized output error {maxVoiceError:G9} against retained .1 rear renderer");
        });
        check("Rear drag is an exact late additive voice and may cover simultaneous road-side cues", () =>
        {
            foreach (int road in new[] { 0, 1, 2 })
            {
                var current = new CompetitionFeedbackEngine(); var frozen = new FrozenTcGrip4CompetitionFeedbackEngine();
                var tuning = OnlyGrip with { RoadSurfaceScale = road == 0 ? 0 : 1 };
                int activeRows = 0; int strongAddedRows = 0;
                for (int ms = 0; ms < 1600; ms += 4)
                {
                    var sample = Car(ms, .06f, .40f, 10); sample.SlipVibration = 1;
                    if (road > 0) AddRoad(ref sample, ms, right: road == 2, grass: false);
                    var a = current.Compute(sample, At(ms), 0, 0, tuning);
                    var b = frozen.Compute(sample, At(ms), 0, 0, tuning); var cue = current.RearDragCue;
                    Assert(a.LeftTrigger == b.LeftTrigger && a.RightTrigger == b.RightTrigger, "rear mixer changed triggers");
                    byte left = ToByte(Combine(frozen.LastLeftOutput, cue.Left));
                    byte right = ToByte(Combine(frozen.LastRightOutput, cue.Right));
                    Assert(a.LeftActuator == left && a.RightActuator == right,
                        $"rear cue replaced, scaled or isolated again at {ms} ms: {a.LeftActuator}/{a.RightActuator}, expected {left}/{right}");
                    if (cue.Strength > .5f) { activeRows++; if (a.LeftActuator > b.LeftActuator + 15) strongAddedRows++; }
                }
                Assert(activeRows > 100 && strongAddedRows > 100, "late .1 drag did not clearly reach final output");
            }
        });
        check("Lateral drag remains independent of throttle, brake, TC and SlipVibration", () =>
        {
            foreach (int mode in new[] { 0, 1, 2, 3 })
            {
                var state = new RearDragFeedbackState(); RearDragCueFrame cue = default;
                for (int ms = 0; ms < 800; ms += 4)
                {
                    var sample = Car(ms, .08f, .35f, 9);
                    sample.Gas = mode == 1 ? 1 : 0; sample.Brake = mode == 2 ? 1 : 0; sample.TcIntervention = mode == 3 ? 1 : 0;
                    cue = state.Update(sample, 1, .004f, At(ms));
                }
                Assert(cue.Strength > .9f && cue.Left > .2f && cue.Right > .1f, $"unrelated signal gated rear drag, mode {mode}");
            }
        });
        check("Countersteer and yaw reversal retain bounded rear recovery", () =>
        {
            var state = new RearDragFeedbackState();
            for (int ms = 0; ms < 400; ms += 4) Update(ref state, ms, .07f, .35f, 9);
            bool recoverySeen = false;
            for (int ms = 400; ms < 540; ms += 4)
            {
                var sample = Car(ms, .26f, .14f, 6);
                sample.SteerAngle = -.4f; sample.LocalAngularVelocityY = sample.LocalYawRate = -.2f;
                var cue = state.Update(sample, 1, .004f, At(ms)); recoverySeen |= cue.Recovery;
                Assert(cue.Strength > .1f && cue.Left > cue.Right, "countersteer cut or reversed drag before body recovery");
            }
            Assert(recoverySeen, "rear recovery never marked");
        });
        check("A normal loaded corner cannot renew rear recovery and release stays 100 ms", () =>
        {
            var state = new RearDragFeedbackState();
            for (int ms = 0; ms < 400; ms += 4) Update(ref state, ms, .07f, .35f, 9);
            bool sawRelease = false; float previous = 0; float peakAfterExpiry = 0;
            for (int ms = 400; ms < 2600; ms += 4)
            {
                var cue = Update(ref state, ms, .08f, .09f, (100f / 3.6f) * .06f);
                if (ms >= 700 && cue.Strength > .0031f && previous > 0)
                {
                    float expected = previous * MathF.Exp(-.004f / .100f);
                    Assert(MathF.Abs(cue.Strength - expected) < .00002f, ".1 release changed or weak recovery renewed"); sawRelease = true;
                }
                if (ms >= 1800) peakAfterExpiry = MathF.Max(peakAfterExpiry, cue.Strength);
                previous = cue.Strength;
            }
            Assert(sawRelease && peakAfterExpiry == 0, "normal corner retained an indefinite rear tail");
        });
        check("Low speed, stopped motion and disabled grip scale clear rear drag", () =>
        {
            foreach (int mode in new[] { 0, 1, 2 })
            {
                var state = new RearDragFeedbackState();
                for (int ms = 0; ms < 300; ms += 4) Update(ref state, ms, .07f, .35f, 9);
                var sample = Car(300, .07f, .35f, 9);
                if (mode == 0) sample.SpeedKmh = 0;
                if (mode == 1) sample.LocalVelocityX = sample.LocalVelocityZ = 0;
                Assert(!HasOutput(state.Update(sample, mode == 2 ? 0 : 1, .004f, At(300))), "stopped/disabled retained rear drag");
            }
        });
        check("Frozen packets expire and duplicate mutations cannot start rear drag", () =>
        {
            var state = new RearDragFeedbackState(); RearDragCueFrame active = default;
            for (int ms = 0; ms <= 200; ms += 4) active = Update(ref state, ms, .07f, .35f, 9);
            Assert(active.Strength > .5f, "test setup failed to establish rear drag");
            var sample = Car(200, .07f, .35f, 9);
            for (int ms = 204; ms <= 700; ms += 4)
            {
                sample.ObservedTimestamp = At(ms); var cue = state.Update(sample, 1, .004f, At(ms));
                if (ms >= 324) Assert(!HasOutput(cue), "frozen packet retained or restarted rear drag");
            }
            var duplicates = new RearDragFeedbackState(); var quiet = Car(0); duplicates.Update(quiet, 1, .004f, At(0));
            for (int ms = 4; ms < 100; ms += 4)
            {
                var changed = Car(ms, .07f, .35f, 9); changed.PacketId = quiet.PacketId;
                Assert(!HasOutput(duplicates.Update(changed, 1, .004f, At(ms))), "one packet mutation counted as fresh evidence");
            }
        });
        check("Time reversal, packet rollback and output gaps reset rear state and admit fresh events", () =>
        {
            foreach (int mode in new[] { 0, 1, 2 })
            {
                var state = new RearDragFeedbackState();
                for (int ms = 0; ms <= 300; ms += 4) Update(ref state, ms, .07f, .35f, 9);
                int resetTime = mode == 0 ? 100 : mode == 1 ? 304 : 600; int resetPacket = mode == 1 ? 1 : 500;
                var reset = Car(resetTime, .07f, .35f, 9); reset.PacketId = resetPacket;
                Assert(!HasOutput(state.Update(reset, 1, .004f, At(resetTime))), "discontinuity replayed previous rear event");
                RearDragCueFrame recovered = default;
                for (int step = 1; step <= 100; step++)
                {
                    var fresh = Car(resetTime + step * 4, .07f, .35f, 9); fresh.PacketId = resetPacket + step;
                    recovered = state.Update(fresh, 1, .004f, At(resetTime + step * 4));
                }
                Assert(recovered.Strength > .5f, "fresh packets could not resume rear drag after reset");
            }
        });
        check("Nonfinite tyre, motion, timing and scale values cannot poison rear output", () =>
        {
            foreach (float bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                var state = new RearDragFeedbackState(); var sample = Car(0);
                sample.SlipAngleFl = sample.SlipAngleFr = sample.SlipAngleRl = sample.SlipAngleRr = bad;
                sample.LocalVelocityX = sample.LocalVelocityZ = sample.LocalYawRate = bad;
                Assert(!HasOutput(state.Update(sample, 1, .004f, At(0))), "invalid motion created rear drag");
                RearDragCueFrame cue = default;
                for (int ms = 4; ms <= 600; ms += 4)
                {
                    cue = state.Update(Car(ms, .07f, .35f, 9), 1, bad, At(ms));
                    Assert(float.IsFinite(cue.Strength) && float.IsFinite(cue.Left) && float.IsFinite(cue.Right), "invalid timing poisoned output");
                }
                Assert(cue.Strength > .5f, "valid telemetry could not recover after bad sample");
                Assert(!HasOutput(state.Update(Car(604, .07f, .35f, 9), bad, .004f, At(604))), "invalid scale retained drag");
            }
        });
        check("Rear event state allocates no managed memory per update", () =>
        {
            var state = new RearDragFeedbackState();
            for (int i = 0; i < 10000; i++) Update(ref state, i * 4, .07f, i % 200 < 100 ? .35f : .04f, 9);
            long before = GC.GetAllocatedBytesForCurrentThread(); long started = Stopwatch.GetTimestamp();
            for (int i = 10000; i < 110000; i++) Update(ref state, i * 4, .07f, i % 200 < 100 ? .35f : .04f, 9);
            long stopped = Stopwatch.GetTimestamp(); long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert(bytes == 0, $"per-frame managed allocation introduced: {bytes}");
            Console.WriteLine($"REAR DRAG PERFORMANCE: 100000 updates in {(stopped - started) * 1000.0 / Stopwatch.Frequency:F3} ms, {bytes} bytes (warmed test, indicative only)");
        });
        check("Rear drag preserves R2 and independently validates L2 while grip-disabled non-L2 frames stay exact", () =>
        {
            foreach (int level in new[] { 0, 25, 50, 100 })
            {
                var tuning = new FeedbackProfile { ActivePreset = FeedbackPreset.Custom,
                    BrakeResistance = level, ThrottleResistance = level }.ToTuning();
                var current = new CompetitionFeedbackEngine(); var frozen = new FrozenTcGrip4CompetitionFeedbackEngine();
                var zeroCurrent = new CompetitionFeedbackEngine(); var zeroFrozen = new FrozenTcGrip4CompetitionFeedbackEngine();
                for (int ms = 0; ms < 1800; ms += 4)
                {
                    var sample = Car(ms, .09f, .35f, 9);
                    sample.Gas = ms < 900 ? 1 : 0; sample.Brake = ms < 900 ? 0 : .7f;
                    sample.TcIntervention = ms is >= 200 and < 700 ? 1 : 0; sample.Abs = ms > 1000 ? 1 : 0;
                    sample.SlipRatioRl = sample.SlipRatioRr = .7f; sample.SlipVibration = 1;
                    sample.Gear = ms < 400 ? 3 : 4; sample.Rpm = ms < 400 ? 7900 : 6000;
                    var a = current.Compute(sample, At(ms), 70, 40, tuning);
                    var b = frozen.Compute(sample, At(ms), 70, 40, tuning);
                    Assert(a.RightTrigger == b.RightTrigger, $"rear drag changed R2 commands at {level}");
                    BrakePedalFeedbackChecks.AssertAllowedL2(a.LeftTrigger, b.LeftTrigger, tuning, frozen, $"rear drag/{level}/{ms}");
                    var noGrip = tuning with { GripLossScale = 0 };
                    var za = zeroCurrent.Compute(sample, At(ms), 70, 40, noGrip);
                    var zb = zeroFrozen.Compute(sample, At(ms), 70, 40, noGrip);
                    Assert((za with { LeftTrigger = zb.LeftTrigger }) == zb,
                        $"GripLoss=0 changed non-L2 feedback at slider {level}");
                }
            }
        });
    }
}
