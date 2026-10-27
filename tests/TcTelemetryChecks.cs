using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Hardware.DualSense;
using ACCDualSenseFeedback.Telemetry;

internal static class TcTelemetryChecks
{
    private static long At(int milliseconds) => (long)((1 + milliseconds / 1000.0) * Stopwatch.Frequency);
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    // Match isolated slider tests while varying resistance independently.
    private static FeedbackTuning TractionTuning(int tc = 50, int wheelspin = 0, int resistance = 0)
        => (FeedbackProfile.Default with
        {
            ActivePreset = FeedbackPreset.Custom,
            BrakeResistance = resistance, ThrottleResistance = resistance,
            AbsPulse = 0, LockPulse = 0, ShiftKick = 0, RedlinePulse = 0,
            TcPulse = tc, WheelspinPulse = wheelspin,
            RoadSurface = 0, GripLoss = 0, NativeAcc = 0,
            EngineTexture = 0, CollisionFeedback = 0
        }).ToTuning();

    private static readonly FeedbackTuning TcOnly = TractionTuning();
    private static readonly FeedbackTuning WheelspinOnly = TractionTuning(tc: 0, wheelspin: 50);

    public static void Run(Action<string, Action> check)
    {
        check("Real ACC float tc reaches TC-only grip and 40 Hz R2 without wheel slip or unused InAction", () =>
        {
            using var page = new PhysicsFixture();
            TelemetrySnapshot sample = page.Read(.37f, unusedTc: 0);
            Assert(MathF.Abs(sample.TcIntervention - .37f) < .00001f,
                "valid physics tc at byte 204 was lost when unused TcInAction at byte 672 was zero");
            Assert(sample.Tc == 0 && sample.TcLevel == .37f,
                "the dedicated TC fix changed legacy values used by other stable feedback");
            Assert(sample.SlipRatioRl == 0 && sample.SlipRatioRr == 0 && sample.SlipVibration == 0,
                "the intervention fixture unexpectedly relies on a mechanical slip fallback");
            var frame = new CompetitionFeedbackEngine().Compute(sample, At(0), 0, 0, TcOnly);
            AssertTc(frame, "a valid intervention without wheel slip", requireGrip: true);
            Assert(frame.LeftTrigger == TriggerEffect.Off, "TC unexpectedly enabled L2");
        });

        check("Unused TcInAction and severe wheel slip cannot impersonate a real TC intervention", () =>
        {
            using var page = new PhysicsFixture();
            page.SetSlip(longitudinal: .9f, lateral: 1.2f);
            var engine = new CompetitionFeedbackEngine();
            for (int ms = 0; ms <= 400; ms += 4)
            {
                TelemetrySnapshot sample = page.Read(0, unusedTc: 1, atMs: ms);
                Assert(sample.TcIntervention == 0 && sample.Tc == 1 && sample.TcLevel == 0,
                    "unused InAction contaminated the real signal or changed the legacy mirrors");
                AssertQuiet(engine.Compute(sample, At(ms), 0, 0, TcOnly),
                    "wheel slip with zero real TC and wheelspin slider off");
            }
        });

        check("A reported real TC event survives low throttle and low speed without artificial gates", () =>
        {
            using var page = new PhysicsFixture();
            foreach (var conditions in new[] { (Gas: .1f, Speed: 100f), (Gas: 1f, Speed: 2f), (Gas: .1f, Speed: 2f) })
            {
                TelemetrySnapshot sample = page.Read(.37f, unusedTc: 0, conditions.Gas, conditions.Speed);
                AssertTc(new CompetitionFeedbackEngine().Compute(sample, At(0), 0, 0, TcOnly),
                    $"real TC at gas {conditions.Gas}, speed {conditions.Speed}", requireGrip: true);
            }
        });

        check("Weak and strong real TC produce identical R2 commands across resistance 0, 50 and 100", () =>
        {
            using var page = new PhysicsFixture();
            foreach (float rawTc in new[] { .015f, .02f, .37f, 1f })
            {
                int[] strengths = [0, 50, 100];
                var engines = strengths.Select(_ => new CompetitionFeedbackEngine()).ToArray();
                var tunings = strengths.Select(value => TractionTuning(resistance: value)).ToArray();
                var restingEngines = strengths.Select(_ => new CompetitionFeedbackEngine()).ToArray();
                for (int ms = 0; ms <= 260; ms += 4)
                {
                    TelemetrySnapshot sample = page.Read(ms <= 80 ? rawTc : 0, unusedTc: 0, atMs: ms);
                    TriggerEffect? reference = null;
                    for (int i = 0; i < strengths.Length; i++)
                    {
                        var frame = engines[i].Compute(sample, At(ms), 0, 0, tunings[i]);
                        var resting = restingEngines[i].Compute(sample, At(ms), 0, 0,
                            tunings[i] with { TcPulseScale = 0 });
                        Assert(frame.LeftTrigger == resting.LeftTrigger,
                            $"TC changed L2 at resistance {strengths[i]}, raw tc {rawTc}, time {ms}");
                        if (ms <= 80)
                        {
                            AssertTc(frame, $"raw tc {rawTc}, resistance {strengths[i]}, time {ms}",
                                requireGrip: ms == 0);
                            if (reference is { } expected)
                                Assert(frame.RightTrigger == expected,
                                    $"TC R2 still depends on baseline resistance for raw tc {rawTc}");
                            else reference = frame.RightTrigger;
                        }
                        if (ms >= 140)
                        {
                            Assert(frame.RightTrigger == resting.RightTrigger,
                                $"R2 did not restore its exact baseline at resistance {strengths[i]}");
                            Assert(frame.LeftActuator == 0 && frame.RightActuator == 0,
                                "TC grip confirmation remained active after its event settled");
                        }
                    }
                }
            }
        });

        check("A single 4 to 20 ms shared-memory TC event holds from onset for 100 ms and never replays", () =>
        {
            using var page = new PhysicsFixture();
            foreach (int duration in new[] { 4, 8, 12, 20 })
            {
                var engine = new CompetitionFeedbackEngine();
                TelemetrySnapshot active = page.Read(.37f, unusedTc: 0);
                AssertTc(engine.Compute(active, At(0), 0, 0, TcOnly),
                    $"the first frame of a {duration} ms event", requireGrip: true);
                // Output carrier reuse is not a new positive physics sample.
                for (int ms = 4; ms < duration; ms += 4)
                    AssertTc(engine.Compute(active, At(ms), 0, 0, TcOnly), "the active short event");

                for (int ms = duration; ms <= duration + 500; ms += 4)
                {
                    TelemetrySnapshot quiet = page.Read(0, unusedTc: 0, atMs: ms);
                    var frame = engine.Compute(quiet, At(ms), 0, 0, TcOnly);
                    if (ms < 100)
                        AssertTc(frame, $"minimum playback for a {duration} ms event at {ms} ms");
                    if (ms >= 140)
                        AssertQuiet(frame, $"a {duration} ms event after its bounded release window");
                }
            }
        });

        check("A sustained TC ends within 24 ms instead of appending a fresh 100 ms hold", () =>
        {
            using var page = new PhysicsFixture();
            var engine = new CompetitionFeedbackEngine();
            for (int ms = 0; ms <= 500; ms += 4)
            {
                var sample = page.Read(.37f, unusedTc: 0, atMs: ms);
                AssertTc(engine.Compute(sample, At(ms), 0, 0, TcOnly),
                    $"sustained TC at {ms} ms", requireGrip: ms == 0);
            }
            for (int ms = 504; ms <= 800; ms += 4)
            {
                var sample = page.Read(0, unusedTc: 0, atMs: ms);
                var frame = engine.Compute(sample, At(ms), 0, 0, TcOnly);
                if (ms >= 528)
                    AssertQuiet(frame, "a sustained TC 24 ms after its first inactive sample");
            }
        });

        check("Disabling TC or resuming after a telemetry gap cannot replay the previous R2 event", () =>
        {
            using var page = new PhysicsFixture();
            foreach (bool disabled in new[] { false, true })
            {
                var engine = new CompetitionFeedbackEngine();
                for (int ms = 0; ms <= 80; ms += 4)
                    engine.Compute(page.Read(.37f, 0, atMs: ms), At(ms), 0, 0, TcOnly);
                if (disabled)
                {
                    AssertQuiet(engine.Compute(page.Read(.37f, 0, atMs: 84), At(84), 0, 0,
                        TcOnly with { TcPulseScale = 0 }), "TC strength disabled during intervention");
                    AssertQuiet(engine.Compute(page.Read(0, 0, atMs: 88), At(88), 0, 0, TcOnly),
                        "reenabling TC after the event ended");
                }
                else
                {
                    AssertQuiet(engine.Compute(page.Read(0, 0, atMs: 400), At(400), 0, 0, TcOnly),
                        "resuming after a gap without a current event");
                    AssertQuiet(engine.Compute(page.Read(0, 0), At(0), 0, 0, TcOnly),
                        "reversed output time without a current event");
                }
            }
        });

        check("Repeated old TC or wheelspin packets expire at 120 ms and a fresh positive packet can recover", () =>
        {
            using var page = new PhysicsFixture();
            foreach (bool tc in new[] { true, false })
            {
                var tuning = tc ? TcOnly : WheelspinOnly;
                page.SetSlip(longitudinal: tc ? 0 : .8f, lateral: 0);
                var engine = new CompetitionFeedbackEngine();
                TelemetrySnapshot active = page.Read(tc ? .37f : 0, unusedTc: 0);
                var initial = engine.Compute(active, At(0), 0, 0, tuning);
                if (tc) AssertTc(initial, "initial TC packet", requireGrip: true);
                for (int ms = 4; ms <= 200; ms += 4)
                {
                    var frame = engine.Compute(active, At(ms), 0, 0, tuning);
                    if (ms == 80 && !tc) AssertWheelspin(frame, "fresh-enough repeated wheelspin packet");
                    if (ms >= 124) AssertQuiet(frame, "an expired packet reused by the output carrier");
                }
                TelemetrySnapshot fresh = page.Read(tc ? .37f : 0, unusedTc: 0, atMs: 204);
                var recovered = engine.Compute(fresh, At(204), 0, 0, tuning);
                if (tc) AssertTc(recovered, "a fresh TC packet after expiry", requireGrip: true);
                else
                {
                    for (int ms = 208; ms <= 260; ms += 4)
                        recovered = engine.Compute(page.Read(0, 0, atMs: ms), At(ms), 0, 0, tuning);
                    AssertWheelspin(recovered, "fresh mechanical wheelspin after expiry");
                }
            }
        });

        check("Rear longitudinal slip alone drives isolated 7 Hz wheelspin without a grip layer", () =>
        {
            using var page = new PhysicsFixture();
            foreach (float rawTc in new[] { 0f, .37f })
            {
                page.SetSlip(longitudinal: .8f, lateral: 0);
                var engine = new CompetitionFeedbackEngine();
                FeedbackFrame frame = default;
                for (int ms = 0; ms <= 80; ms += 4)
                    frame = engine.Compute(page.Read(rawTc, unusedTc: 0, atMs: ms), At(ms), 0, 0, WheelspinOnly);
                AssertWheelspin(frame, $"rear longitudinal slip with raw TC {rawTc} and TC slider off");
            }
            // At launch, rear angular speed can supply motion evidence.
            page.SetRearAngularSpeed(20f);
            var launch = new CompetitionFeedbackEngine();
            FeedbackFrame launchFrame = default;
            for (int ms = 0; ms <= 80; ms += 4)
                launchFrame = launch.Compute(page.Read(0, 0, speed: 0, atMs: ms), At(ms), 0, 0, WheelspinOnly);
            AssertWheelspin(launchFrame, "low-speed rear wheelspin confirmed by angular speed");
        });

        check("Pure lateral slip, front slip, braking lock and absent drive motion cannot impersonate wheelspin", () =>
        {
            foreach (string scenario in new[] { "lateral", "front", "braking", "no gas", "no motion" })
            {
                using var page = new PhysicsFixture();
                if (scenario == "lateral") page.SetSlip(longitudinal: 0, lateral: 1.2f);
                else if (scenario == "front") page.SetFrontLongitudinalSlip(.8f);
                else page.SetSlip(longitudinal: .8f, lateral: 0);
                if (scenario == "braking") page.SetBrake(1f);
                var engine = new CompetitionFeedbackEngine();
                for (int ms = 0; ms <= 400; ms += 4)
                {
                    var sample = page.Read(0, unusedTc: 1,
                        gas: scenario == "no gas" ? 0 : 1,
                        speed: scenario == "no motion" ? 0 : 100, atMs: ms);
                    AssertQuiet(engine.Compute(sample, At(ms), 0, 0, WheelspinOnly),
                        $"non-wheelspin condition: {scenario}");
                }
            }
        });

        check("Real TC retains 40 Hz priority when mechanical wheelspin is enabled and present", () =>
        {
            using var page = new PhysicsFixture();
            page.SetSlip(longitudinal: .8f, lateral: 1.2f);
            var tuning = TractionTuning(tc: 50, wheelspin: 50);
            var engine = new CompetitionFeedbackEngine();
            for (int ms = 0; ms <= 300; ms += 4)
            {
                var sample = page.Read(.015f, unusedTc: 0, atMs: ms);
                AssertTc(engine.Compute(sample, At(ms), 0, 0, tuning),
                    $"weak real TC alongside strong wheelspin at {ms} ms", requireGrip: ms == 0);
            }
        });

        check("Real TC is finite and bounded while legacy raw values remain byte faithful", () =>
        {
            using var page = new PhysicsFixture();
            foreach (float raw in new[]
            {
                float.NaN, float.NegativeInfinity, float.PositiveInfinity,
                -.4f, 0, .37f, 1, 1.7f, float.MaxValue
            })
            {
                TelemetrySnapshot sample = page.Read(raw, unusedTc: 0);
                float expected = float.IsFinite(raw) ? Math.Clamp(raw, 0f, 1f) : 0f;
                Assert(float.IsFinite(sample.TcIntervention) && sample.TcIntervention == expected,
                    $"invalid or out-of-range raw tc {raw} was not safely bounded");
                Assert(BitConverter.SingleToInt32Bits(sample.TcLevel) == BitConverter.SingleToInt32Bits(raw),
                    "bounding the new signal rewrote a legacy field");
                Assert(sample.Tc == 0, "a nonzero float tc rewrote the legacy InAction mirror");
                var frame = new CompetitionFeedbackEngine().Compute(sample, At(0), 0, 0, TcOnly);
                if (expected == 0) AssertQuiet(frame, $"invalid/zero real tc {raw}");
                else AssertTc(frame, $"bounded real tc {raw}", requireGrip: true);
            }
        });

        check("Dedicated TC intervention does not reinterpret ABS or its legacy level fields", () =>
        {
            using var page = new PhysicsFixture();
            foreach (int unusedAbs in new[] { 0, 1, -3 })
            {
                page.SetAbs(.63f, unusedAbs, .27f);
                TelemetrySnapshot sample = page.Read(.37f, unusedTc: 0);
                Assert(sample.AbsLevel == .63f && sample.Abs == (unusedAbs == 0 ? 0f : 1f)
                    && sample.AbsVibration == .27f,
                    "the TC correction changed established ABS mapping");
                Assert(sample.TcIntervention == .37f && sample.Tc == 0 && sample.TcLevel == .37f,
                    "ABS state affected the real TC signal or its legacy mirrors");
            }
        });

        check("Only dedicated intervention drives TC even when legacy TC and level disagree", () =>
        {
            using var page = new PhysicsFixture();
            TelemetrySnapshot real = page.Read(.37f, unusedTc: 0);
            real.Tc = 0; real.TcLevel = 0;
            AssertTc(new CompetitionFeedbackEngine().Compute(real, At(0), 0, 0, TcOnly),
                "real intervention with both legacy fields zero", requireGrip: true);

            var engine = new CompetitionFeedbackEngine();
            for (int ms = 0; ms <= 200; ms += 4)
            {
                TelemetrySnapshot legacyOnly = page.Read(0, unusedTc: 1, atMs: ms);
                legacyOnly.TcLevel = 6;
                AssertQuiet(engine.Compute(legacyOnly, At(ms), 0, 0, TcOnly),
                    "nonzero legacy TC fields without a dedicated intervention");
            }
        });
    }

    private static void AssertTc(in FeedbackFrame frame, string context, bool requireGrip = false)
    {
        if (requireGrip)
            Assert(frame.LeftActuator > 0 && frame.RightActuator > 0, $"no initial TC grip confirmation for {context}");
        Assert(frame.RightTrigger.Mode == 0x26 && (frame.RightTrigger.P0 | frame.RightTrigger.P1) != 0
            && frame.RightTrigger.P8 == 40,
            $"R2 did not produce the dedicated 40 Hz TC vibration for {context}");
    }

    private static void AssertWheelspin(in FeedbackFrame frame, string context)
    {
        Assert(frame.RightTrigger.Mode == 0x26 && frame.RightTrigger.P8 == 7,
            $"R2 did not produce the isolated 7 Hz wheelspin vibration for {context}");
        Assert(frame.LeftActuator == 0 && frame.RightActuator == 0,
            $"isolated wheelspin leaked into a grip layer for {context}");
        Assert(frame.LeftTrigger == TriggerEffect.Off, $"isolated wheelspin enabled L2 for {context}");
    }

    private static void AssertQuiet(in FeedbackFrame frame, string context)
    {
        Assert(frame.LeftActuator == 0 && frame.RightActuator == 0,
            $"unexpected grip output for {context}");
        Assert(frame.LeftTrigger == TriggerEffect.Off && frame.RightTrigger == TriggerEffect.Off,
            $"unexpected trigger output for {context}");
    }

    private sealed class PhysicsFixture : IDisposable
    {
        private readonly MemoryMappedFile _map;
        private readonly MemoryMappedViewAccessor _view;
        private readonly AccSharedMemoryReader _reader;
        private int _packet;

        public PhysicsFixture()
        {
            string name = @"Local\ACCDualSenseTcRegression-" + Guid.NewGuid().ToString("N");
            _map = MemoryMappedFile.CreateNew(name, 800, MemoryMappedFileAccess.ReadWrite);
            _view = _map.CreateViewAccessor(0, 800, MemoryMappedFileAccess.ReadWrite);
            // Published wire offsets avoid mirroring a layout mistake in tests.
            _view.Write(16, 4);       // gear
            _view.Write(20, 5000);    // rpms
            _view.Write(588, 8000);   // currentMaxRpm
            _view.Write(780, 1);      // isEngineRunning
            _reader = new AccSharedMemoryReader([name]);
            Assert(_reader.TryConnect(), "could not open the isolated physics fixture");
        }

        public TelemetrySnapshot Read(float realTc, int unusedTc, float gas = 1, float speed = 100, int atMs = 0)
        {
            _view.Write(4, gas);
            _view.Write(28, speed);
            _view.Write(204, realTc); // valid physics float tc
            _view.Write(672, unusedTc); // unused int tcInAction
            _view.Write(0, ++_packet);
            Assert(_reader.TryReadLatest(out TelemetrySnapshot sample), "isolated physics packet was not read");
            Assert(sample.PacketId == _packet && sample.Rpm == 5000 && sample.CurrentMaxRpm == 8000,
                "the fixture layout did not reach the actual shared-memory reader");
            // Normalize only time after the actual reader has decoded the wire.
            sample.ObservedTimestamp = At(atMs);
            return sample;
        }

        public void SetSlip(float longitudinal, float lateral)
        {
            _view.Write(648, longitudinal); // slipRatio rear left
            _view.Write(652, longitudinal); // slipRatio rear right
            _view.Write(664, lateral);      // slipAngle rear left
            _view.Write(668, lateral);      // slipAngle rear right
            _view.Write(788, 1f);           // slipVibrations
        }

        public void SetFrontLongitudinalSlip(float value)
        {
            _view.Write(640, value); // slipRatio front left
            _view.Write(644, value); // slipRatio front right
        }

        public void SetRearAngularSpeed(float value)
        {
            _view.Write(112, value); // wheelAngularSpeed rear left
            _view.Write(116, value); // wheelAngularSpeed rear right
        }

        public void SetBrake(float value) => _view.Write(8, value);

        public void SetAbs(float rawLevel, int unusedAbs, float vibration)
        {
            _view.Write(252, rawLevel);
            _view.Write(676, unusedAbs);
            _view.Write(796, vibration);
        }

        public void Dispose()
        {
            _reader.Dispose();
            _view.Dispose();
            _map.Dispose();
        }
    }
}

