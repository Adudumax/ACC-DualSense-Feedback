using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Hardware.DualSense;
using ACCDualSenseFeedback.Lighting;
using ACCDualSenseFeedback.Telemetry;

internal static class ShiftCueChecks
{
    private static long At(double seconds) => (long)((seconds + 1) * Stopwatch.Frequency);
    private static readonly FeedbackTuning Tuning = FeedbackTuning.Neutral;
    private static TelemetrySnapshot Car(int rpm = 7900, int gear = 5) => new()
    { Rpm = rpm, Gear = gear, CurrentMaxRpm = 8650, IsEngineRunning = 1, Gas = 1, SpeedKmh = 205 };
    private static void Assert(bool condition, string text)
    { if (!condition) throw new InvalidOperationException(text); }
    private static bool Redline(FeedbackFrame frame) => frame.RightTrigger.Mode == 0x26 && frame.RightTrigger.P8 == 18;
    private static AccShiftCueState Update(AccShiftCue cue, in TelemetrySnapshot car)
        => cue.Update(car, "audi_r8_lms_evo_ii", 8650, Tuning);
    private static RpmLightFrame Light(RpmLightEngine lights, in TelemetrySnapshot car, double time, AccShiftCueState cue,
        RpmLightSettings? settings = null, CspLightContext context = default)
        => lights.Compute(car, At(time), settings ?? RpmLightSettings.Default, 8650, context, cue);

    public static void Run(Action<string, Action> check)
    {
        check("Native shift profiles require a known exact car ID, both RPM bases and default timing", () =>
        {
            var car = Car(); var original = car;
            Assert(Update(new(), car).Calibrated, "measured Audi not recognized");
            foreach (string model in new[] { "", "unknown_car", "AUDI_R8_LMS_EVO_II", "bmw_m4_gt3" })
                Assert(!new AccShiftCue().Update(car, model, 8650, Tuning).Calibrated, "calibrated an unmeasured model");
            foreach (int max in new[] { 0, 8000, 8649, 9000 })
            {
                Assert(!new AccShiftCue().Update(car, "audi_r8_lms_evo_ii", max, Tuning).Calibrated, "accepted static mismatch");
                Assert(!Update(new(), car with { CurrentMaxRpm = max }).Calibrated, "accepted physical mismatch");
            }
            Assert(!new AccShiftCue().Update(car, "audi_r8_lms_evo_ii", 8650, Tuning with { RedlineStartRatio = 0.95f }).Calibrated,
                "manual override ignored");
            Assert(car.Equals(original) && car.CurrentMaxRpm == 8650, "rewrote raw telemetry");
        });
        check("Fourth and fifth gear sample RPMs activate R2 and a full red five-LED shift cue together", () =>
        {
            foreach (var (gear, rpm) in new[] { (5, 7928), (6, 7905) })
            {
                var cue = new AccShiftCue(); var engine = new CompetitionFeedbackEngine(); var lights = new RpmLightEngine();
                var car = Car(7890, gear); var before = Update(cue, car);
                Assert(!Redline(engine.Compute(car, At(0), 0, 0, Tuning, before)), "early pulse");
                car.Rpm = rpm; var state = Update(cue, car);
                var haptics = engine.Compute(car, At(0.004), 0, 0, Tuning, state);
                var rgb = Light(lights, car, 0.004, state);
                Assert(state.Active && Redline(haptics), "R2 still waiting for the limiter");
                Assert(rgb.Red == 255 && rgb.Green == 0 && rgb.Blue == 0 && rgb.PlayerMask == 31, "lighting uses another reference");
                Assert(haptics.RightTrigger == TriggerEffect.Vibration(1, 2, 18), "onset strength or frequency changed");
                var off = Light(lights, car, 0.204, Update(cue, car));
                Assert(off.Red == 0 && off.Green == 0 && off.Blue == 0 && off.PlayerMask == 31, "flash timing or white LEDs changed");
            }
        });
        check("Native strict alarm boundary keeps the existing hysteresis and rearms on gear change or disconnect", () =>
        {
            var cue = new AccShiftCue(); var car = Car(7899);
            Assert(!Update(cue, car).Active, "activated below reference");
            car.Rpm = 7900; Assert(!Update(cue, car).Active, "native HUD uses strict greater-than");
            car.Rpm = 7901; Assert(Update(cue, car).Active, "boundary missed");
            foreach (int rpm in new[] { 7899, 7870, 7901, 7820 })
            { car.Rpm = rpm; Assert(Update(cue, car).Active, "jitter reset warning"); }
            car.Rpm = 7819; Assert(!Update(cue, car).Active, "real drop stayed active");
            car.Rpm = 7901; Update(cue, car); car.Gear = 6; car.Rpm = 7850;
            var changed = Update(cue, car); Assert(!changed.Active && changed.ResetEnvelope, "gear retained latch");
            car.Rpm = 7901; Update(cue, car); cue.Reset(); car.Rpm = 7850;
            Assert(!Update(cue, car).Active, "reconnect retained latch");
        });
        check("Neutral, reverse, sixth gear, engine-off, pause and replay cannot hold the native R2 cue", () =>
        {
            for (int scenario = 0; scenario < 7; scenario++)
            {
                var cue = new AccShiftCue(); var engine = new CompetitionFeedbackEngine(); var car = Car(8000);
                for (int i = 0; i < 20; i++) engine.Compute(car, At(i * .004), 0, 0, Tuning, Update(cue, car));
                if (scenario < 2) car.Gear = scenario;
                if (scenario == 2) car.IsEngineRunning = 0;
                if (scenario == 5) car.Rpm = 0;
                if (scenario == 6) car.Gear = 7;
                var state = cue.Update(car, "audi_r8_lms_evo_ii", 8650, Tuning, scenario == 3, scenario == 4);
                Assert(!state.Active && state.ResetEnvelope, "invalid state kept warning");
                Assert(!Redline(engine.Compute(car, At(.084), 0, 0, Tuning, state)), "R2 envelope stayed latched");
            }
        });
        check("All 55 built-in native profiles have unique IDs and valid suggested, alarm and limiter values", () =>
        {
            Assert(AccShiftProfiles.SourceVersion == "1.10.4", "unexpected source snapshot");
            Assert(AccShiftProfiles.All.Length == 55, "profile missing or duplicate");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var profile in AccShiftProfiles.All)
            {
                Assert(ids.Add(profile.CarModel), "duplicate car ID");
                Assert(profile.SuggestedRpm > 1000 && profile.SuggestedRpm <= profile.AlarmRpm &&
                    profile.AlarmRpm < profile.LimiterRpm && profile.LimiterRpm <= 16000, "invalid native RPM range");
                Assert(AccShiftProfiles.TryGet(profile.CarModel, out var resolved) && resolved == profile, "lookup mismatch");
            }
            Assert(AccShiftProfiles.TryGet("ferrari_296_gt3", out var ferrari) && ferrari.AlarmRpm == 7200 && ferrari.LimiterRpm == 8000,
                "296 native alarm differs from inspected data");
            Assert(AccShiftProfiles.TryGet("mclaren_720s_gt3_evo", out var mclaren) && mclaren.AlarmRpm == 7300 && mclaren.LimiterRpm == 7700,
                "720S Evo native alarm differs from inspected data");
        });
        check("Every built-in car uses its native strict alarm for R2 and lighting in fourth and fifth gear", () =>
        {
            foreach (var profile in AccShiftProfiles.All)
                foreach (int gear in new[] { 5, 6 })
                {
                    var cue = new AccShiftCue(); var engine = new CompetitionFeedbackEngine(); var lights = new RpmLightEngine();
                    var car = Car(profile.AlarmRpm, gear) with { CurrentMaxRpm = profile.LimiterRpm };
                    var before = cue.Update(car, profile.CarModel, profile.LimiterRpm, Tuning);
                    Assert(before.ReferenceRpm == profile.AlarmRpm && !before.Active, "wrong or inclusive alarm boundary");
                    car.Rpm++;
                    var state = cue.Update(car, profile.CarModel, profile.LimiterRpm, Tuning);
                    Assert(state.Active, "native alarm not active");
                    var frame = engine.Compute(car, At(.004), 0, 0, Tuning, state);
                    Assert(Redline(frame) && frame.RightTrigger == TriggerEffect.Vibration(1, 2, 18), "R2 onset changed");
                    var rgb = lights.Compute(car, At(.004), RpmLightSettings.Default, profile.LimiterRpm, default, state);
                    Assert(rgb.Red == 255 && rgb.Green == 0 && rgb.Blue == 0 && rgb.PlayerMask == 31, "light endpoint mismatch");
                    car.Gear = 7;
                    var top = cue.Update(car, profile.CarModel, profile.LimiterRpm, Tuning);
                    Assert(!top.Active && top.ResetEnvelope, "sixth gear kept the HUD alarm");
                    Assert(!Redline(engine.Compute(car, At(.008), 0, 0, Tuning with { ShiftKickScale = 0 }, top)), "sixth gear kept redline envelope");
                }
        });
        check("Changing cars clears the latch even when alarm and limiter values are identical", () =>
        {
            var cue = new AccShiftCue(); var car = Car(8000);
            Assert(Update(cue, car).Active, "initial alarm absent");
            car.Rpm = 7850;
            var changed = cue.Update(car, "audi_r8_lms_evo", 8650, Tuning);
            Assert(changed.Calibrated && !changed.Active && changed.ResetEnvelope, "same-range car inherited previous warning");
            car.Rpm = 8000; cue.Update(car, "audi_r8_lms_evo", 8650, Tuning);
            var unknown = cue.Update(car, "unknown_car", 8650, Tuning);
            Assert(!unknown.Calibrated && !unknown.Active && unknown.ResetEnvelope, "unknown car retained profile");
            var known = Update(cue, car);
            Assert(known.Calibrated && known.Active && known.ResetEnvelope, "known car did not recover");
        });
        check("Changing limiter metadata or manual timing safely leaves and restores automatic native timing", () =>
        {
            foreach (var profile in AccShiftProfiles.All)
            {
                var cue = new AccShiftCue();
                var car = Car(profile.AlarmRpm + 1) with { CurrentMaxRpm = profile.LimiterRpm };
                Assert(cue.Update(car, profile.CarModel, profile.LimiterRpm, Tuning).Active, "missing initial cue");
                var invalid = cue.Update(car, profile.CarModel, profile.LimiterRpm + 1, Tuning);
                Assert(!invalid.Calibrated && invalid.ResetEnvelope, "static mismatch retained native timing");
                Assert(!cue.Update(car with { CurrentMaxRpm = profile.LimiterRpm - 1 }, profile.CarModel, profile.LimiterRpm, Tuning).Calibrated,
                    "dynamic mismatch retained native timing");
                Assert(!cue.Update(car, profile.CarModel, profile.LimiterRpm, Tuning with { RedlineStartRatio = .95f }).Calibrated,
                    "manual timing overridden");
                var restored = cue.Update(car, profile.CarModel, profile.LimiterRpm, Tuning);
                Assert(restored.Active && restored.ResetEnvelope && restored.ReferenceRpm == profile.AlarmRpm, "automatic timing did not recover");
            }
        });
        check("Lift-off, disabled redline strength and disabled lighting retain their independent controls", () =>
        {
            var car = Car(8000); var state = Update(new(), car);
            var engine = new CompetitionFeedbackEngine();
            Assert(!Redline(engine.Compute(car with { Gas = 0 }, At(0), 0, 0, Tuning, state)), "lift-off generated redline pulse");
            Assert(!Redline(new CompetitionFeedbackEngine().Compute(car, At(0), 0, 0,
                Tuning with { RedlinePulseScale = 0 }, state)), "zero strength ignored");
            Assert(Light(new(), car, 0, state, RpmLightSettings.Default with { Enabled = false }) == RpmLightFrame.Off, "lighting switch ignored");
            Assert(Redline(new CompetitionFeedbackEngine().Compute(car, At(0), 0, 0, Tuning, state)), "R2 depends on light switch");
        });
        check("Existing TC and shift-kick arbitration still takes priority over calibrated redline when qualified", () =>
        {
            var cue = new AccShiftCue(); var engine = new CompetitionFeedbackEngine(); var car = Car(8000);
            car.Tc = 1; car.TcLevel = 1; car.TcIntervention = 1;
            FeedbackFrame frame = default;
            for (int i = 0; i < 200; i++) frame = engine.Compute(car, At(i * .004), 0, 0, Tuning, Update(cue, car));
            Assert(frame.RightTrigger.Mode == 0x26 && frame.RightTrigger.P8 == 40,
                "qualified TC vibration did not retain priority over calibrated redline");
            car.Gear++; car.Tc = 0; car.TcIntervention = 0; car.Rpm = 7400;
            frame = engine.Compute(car, At(.8), 0, 0, Tuning, Update(cue, car));
            Assert(frame.RightTrigger.Mode == 0x26 && frame.RightTrigger.P8 == 28, "shift kick replaced");
            for (int i = 201; i < 350; i++) frame = engine.Compute(car, At(i * .004), 0, 0, Tuning, Update(cue, car));
            Assert(!Redline(frame), "new gear resumed previous redline");
        });
        check("ABS retains L2 and yellow RGB priority while calibrated shift reference is active", () =>
        {
            var cue = new AccShiftCue(); var engine = new CompetitionFeedbackEngine(); var old = new CompetitionFeedbackEngine();
            var lights = new RpmLightEngine(); var car = Car(8000); car.Brake = .8f; car.AbsVibration = .2f;
            for (int i = 0; i < 50; i++)
            {
                car.PacketId++; var state = Update(cue, car);
                var actual = engine.Compute(car, At(i * .004), 31, 17, Tuning, state);
                var baseline = old.Compute(car, At(i * .004), 31, 17, Tuning);
                Assert(actual.LeftTrigger == baseline.LeftTrigger && actual.LeftActuator == baseline.LeftActuator &&
                    actual.RightActuator == baseline.RightActuator, "shift cue affected L2 or main actuators");
                var rgb = Light(lights, car, i * .004, state);
                if (i == 0) Assert(rgb.Red == 255 && rgb.Green == 219 && rgb.Blue == 0 && rgb.PlayerMask == 31, "ABS yellow lost priority");
            }
        });
        check("Read-only metadata changes car ID even at the same RPM limit and clears on disconnect", () =>
        {
            string name = @"Local\ACC-Shift-Metadata-Test-" + Guid.NewGuid().ToString("N");
            using var map = MemoryMappedFile.CreateNew(name, 416); using var view = map.CreateViewAccessor();
            using var reader = new AccLightingMetadataReader([name], []);
            void SetName(string value)
            {
                byte[] data = new byte[66]; Encoding.Unicode.GetBytes(value).CopyTo(data, 0);
                view.WriteArray(68, data, 0, data.Length);
            }
            view.Write(412, 8650); SetName("audi_r8_lms_evo_ii");
            var identity = Encoding.Unicode.GetBytes("PRIVATE_PLAYER_TEST"); view.WriteArray(200, identity, 0, identity.Length);
            reader.Refresh(At(0)); Assert(reader.CarModel == "audi_r8_lms_evo_ii", "wrong ID offset or read");
            SetName("unmeasured_car"); reader.Refresh(At(.6));
            Assert(reader.CarModel == "unmeasured_car" && reader.MaxRpm == 8650, "same-limit car retained old ID");
            reader.Disconnect(); Assert(reader.CarModel.Length == 0 && reader.MaxRpm == 0, "metadata survived disconnect");
        });
    }
}
