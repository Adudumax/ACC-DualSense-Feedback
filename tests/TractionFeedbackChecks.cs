using System.Diagnostics;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Telemetry;

internal static class TractionFeedbackChecks
{
    private static long At(int ms) => (long)((1 + ms / 1000.0) * Stopwatch.Frequency);
    private static void Assert(bool ok, string why)
    { if (!ok) throw new InvalidOperationException(why); }
    private static TelemetrySnapshot Car(int ms, float tc) => new()
    {
        PacketId = ms + 100, ObservedTimestamp = At(ms), TcIntervention = tc,
        Gas = 1, Gear = 4, Rpm = 5000, CurrentMaxRpm = 8000, SpeedKmh = 100, IsEngineRunning = 1
    };
    private static readonly FeedbackTuning OnlyTc = FeedbackTuning.Neutral with
    {
        BrakeResistanceScale = 0, ThrottleResistanceScale = 0,
        AbsPulseScale = 0, LockPulseScale = 0, ShiftKickScale = 0, RedlinePulseScale = 0,
        WheelspinPulseScale = 0, RoadSurfaceScale = 0, GripLossScale = 0, NativeAccScale = 0,
        EngineTextureScale = 0, CollisionFeedbackScale = 0
    };
    private static TractionCueFrame Update(ref TractionFeedbackState state, int ms, float tc,
        bool shift = false, float tcScale = 1, float spinScale = 1, float spin = 0)
        => state.Update(Car(ms, tc), spin, tcScale, spinScale, .004f, At(ms), shift);

    public static void Run(Action<string, Action> check)
    {
        check("A weak real TC event starts immediately; legacy flags cannot activate it", () =>
        {
            foreach (float tc in new[] { .015f, .02f, .37f, 1f })
            {
                var state = new TractionFeedbackState(); var cue = Update(ref state, 0, tc);
                Assert(cue.TcStrength >= .035f && cue.TcGripPulse > 0, "weak real TC was lost");
            }
            var ignored = new TractionFeedbackState(); var car = Car(0, 0);
            car.Tc = 1; car.TcLevel = 6; car.SlipRatioRl = 1; car.SlipAngleRr = 1;
            Assert(ignored.Update(car, 0, 1, 0, .004f, At(0), false) == default,
                "legacy assist or tyre values impersonated TC");
        });
        check("Short TC minimum duration is anchored once at entry, not extended by positive samples", () =>
        {
            foreach (int duration in new[] { 4, 20, 80 })
            {
                var state = new TractionFeedbackState();
                for (int ms = 0; ms <= 180; ms += 4)
                {
                    var cue = Update(ref state, ms, ms < duration ? 1 : 0);
                    if (ms < 100) Assert(cue.TcStrength >= .035f, "minimum cue disappeared early");
                    if (ms >= Math.Max(100, duration + 24))
                        Assert(cue.TcStrength == 0, "last positive sample added another 100 ms tail");
                }
            }
        });
        check("Sustained TC stops after the short gap window without another minimum-duration tail", () =>
        {
            var state = new TractionFeedbackState();
            for (int ms = 0; ms < 500; ms += 4)
                Assert(Update(ref state, ms, 1).TcStrength > 0, "sustained TC was interrupted");
            for (int ms = 500; ms <= 700; ms += 4)
            {
                var cue = Update(ref state, ms, 0);
                if (ms >= 524) Assert(cue.TcStrength == 0, "sustained event retained old TC");
            }
        });
        check("Grip confirmation only plays at entry, and short TC gaps do not retrigger it", () =>
        {
            var state = new TractionFeedbackState(); float first = Update(ref state, 0, 1).TcGripPulse;
            Assert(first > 0, "entry confirmation absent");
            for (int ms = 4; ms <= 600; ms += 4)
            {
                float raw = ms >= 120 && ms < 140 ? 0 : 1;
                var cue = Update(ref state, ms, raw);
                Assert(cue.TcGripPulse <= first, "confirmation unexpectedly grew");
                if (ms >= 80) Assert(cue.TcGripPulse == 0, "grip confirmation became a continuous texture");
            }
        });
        check("Frozen positive telemetry stops both cues and cannot restart from the same packet", () =>
        {
            var state = new TractionFeedbackState(); var frozen = Car(0, 1);
            state.Update(frozen, 1, 1, 1, .004f, At(0), false);
            for (int ms = 4; ms <= 500; ms += 4)
            {
                // A re-read timestamp alone is not a new physics packet.
                frozen.ObservedTimestamp = At(ms);
                var cue = state.Update(frozen, 1, 1, 1, .004f, At(ms), false);
                if (ms >= 124) Assert(cue == default, "a frozen positive packet kept or restarted a cue");
            }
            Assert(Update(ref state, 504, 1).TcStrength > 0, "new real telemetry could not recover");
        });
        check("Output gaps, reversed time and packet rollback discard pending TC confirmation", () =>
        {
            var state = new TractionFeedbackState(); Update(ref state, 0, 1);
            Assert(Update(ref state, 400, 0) == default, "gap replayed old cue");
            Update(ref state, 404, 1);
            Assert(Update(ref state, 0, 0) == default, "time reversal replayed old cue");
            Update(ref state, 4, 1);
            var rollback = Car(8, 0); rollback.PacketId = 1;
            Assert(state.Update(rollback, 0, 1, 1, .004f, At(8), false) == default,
                "packet rollback retained a cue from the previous session");
        });
        check("Actual shifts discard short TC and spin events, resuming only a current TC", () =>
        {
            var state = new TractionFeedbackState(); Update(ref state, 0, 1);
            for (int ms = 4; ms <= 180; ms += 4)
                Assert(Update(ref state, ms, ms < 20 ? 1 : 0, shift: true, spin: 1) == default,
                    "traction cue displaced an actual shift");
            Assert(Update(ref state, 184, 0) == default, "expired TC was queued behind the shift");
            Assert(Update(ref state, 188, 1).TcStrength > 0, "current TC did not resume");
        });
        check("Each traction slider clears only its own state immediately", () =>
        {
            var state = new TractionFeedbackState(); Update(ref state, 0, 1, spin: 1);
            var tcOff = Update(ref state, 4, 1, tcScale: 0, spin: 1);
            Assert(tcOff.TcStrength == 0 && tcOff.TcGripPulse == 0 && tcOff.WheelspinStrength > 0,
                "disabling TC also disabled independent mechanical spin");
            Assert(Update(ref state, 8, 0, spinScale: 0) == default, "disabled spin or expired TC remained");
        });
        check("NaN or infinite traction signals cannot generate output", () =>
        {
            foreach (float bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -.1f })
            {
                var state = new TractionFeedbackState(); var sample = Car(0, bad);
                Assert(state.Update(sample, bad, 1, 1, .004f, At(0), false) == default, "invalid input enabled a cue");
            }
        });
        check("TC frequency is 40 while unchanged shift warning and actual shift remain 18 and 28", () =>
        {
            var car = Car(0, .02f); var engine = new CompetitionFeedbackEngine();
            var tuning = OnlyTc with { RedlinePulseScale = 1, ShiftKickScale = 1 };
            var warning = new AccShiftCueState(6000, true, false);
            Assert(engine.Compute(car, At(0), 0, 0, tuning, warning).RightTrigger.P8 == 40,
                "a weak real TC lost to the shift warning");
            car = Car(4, .02f); car.Gear++;
            Assert(engine.Compute(car, At(4), 0, 0, tuning, warning).RightTrigger.P8 == 28,
                "TC replaced the actual gear impact");
            car = Car(0, 0);
            Assert(new CompetitionFeedbackEngine().Compute(car, At(0), 0, 0, tuning, warning).RightTrigger.P8 == 18,
                "the no-TC shift warning changed");
        });
        check("TC entry does not reduce tyre, native or engine feedback, even in a full mix", () =>
        {
            var active = new CompetitionFeedbackEngine(); var reference = new CompetitionFeedbackEngine();
            var tuning = FeedbackTuning.Neutral with { ShiftKickScale = 0 };
            for (int ms = 0; ms <= 600; ms += 4)
            {
                var car = Car(ms, ms >= 240 && ms < 400 ? 1 : 0);
                car.SlipRatioRl = .6f; car.SlipRatioRr = .6f; car.SlipVibration = 1; car.KerbVibration = .04f;
                var a = active.Compute(car, At(ms), 90, 70, tuning);
                var b = reference.Compute(car, At(ms), 90, 70, tuning with { TcPulseScale = 0 });
                Assert(a.LeftActuator >= b.LeftActuator && a.RightActuator >= b.RightActuator,
                    "TC reduced an original grip layer");
                Assert(a.LeftTrigger == b.LeftTrigger, "TC changed L2");
                if (ms < 240 || ms >= 320)
                    Assert(a.LeftActuator == b.LeftActuator && a.RightActuator == b.RightActuator,
                        "TC changed grips outside its short entry confirmation");
            }
        });
        check("Traction event processing allocates no per-frame managed memory", () =>
        {
            var state = new TractionFeedbackState();
            for (int i = 0; i < 10000; i++) Update(ref state, i * 4, i % 50 < 10 ? .4f : 0, spin: .4f);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 10000; i < 110000; i++) Update(ref state, i * 4, i % 50 < 10 ? .4f : 0, spin: .4f);
            Assert(GC.GetAllocatedBytesForCurrentThread() == before, "per-frame allocation introduced");
        });
    }
}
