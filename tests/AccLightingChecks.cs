using System.Diagnostics;
using ACCDualSenseFeedback.Lighting;
using ACCDualSenseFeedback.Telemetry;

internal static class AccLightingChecks
{
    private static long At(double seconds) => (long)((seconds + 1) * Stopwatch.Frequency);
    private static readonly RpmLightSettings Settings = RpmLightSettings.Default;
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static bool Dark(RpmLightFrame f) => f.Red == 0 && f.Green == 0 && f.Blue == 0;
    private static bool Yellow(RpmLightFrame f) => f.Red == 255 && f.Green == 219 && f.Blue == 0;
    private static TelemetrySnapshot Car(int rpm = 3000, float speed = 80) => new()
    {
        IsEngineRunning = 1, Rpm = rpm, Gear = 3, SpeedKmh = speed,
        WheelAngularSpeedFl = speed, WheelAngularSpeedFr = speed,
        WheelAngularSpeedRl = speed, WheelAngularSpeedRr = speed
    };
    private static RpmLightFrame Frame(RpmLightEngine e, in TelemetrySnapshot car, double time,
        RpmLightSettings? settings = null, CspLightContext context = default)
        => e.Compute(car, At(time), settings ?? Settings, 8000, context);

    public static void Run(Action<string, Action> check)
    {
        check("Stationary idle breathes ice blue without going black in either colour mode", () =>
        {
            foreach (var mode in new[] { RpmLightMode.Smooth, RpmLightMode.Discrete })
            {
                var engine = new RpmLightEngine(); var car = Car(1200, 0);
                int minimum = 255, maximum = 0;
                for (int i = 0; i <= 400; i++)
                {
                    var f = Frame(engine, car, i * 0.02, Settings with { Mode = mode });
                    Assert(!Dark(f) && f.PlayerMask == 0, "idle blackout or invented white RPM LEDs");
                    Assert(f.Blue > f.Green && f.Green > f.Red, "idle is not ice blue");
                    Assert(Math.Abs(f.Red - f.Blue * (90d / 214)) <= 1 &&
                        Math.Abs(f.Green - f.Blue * (175d / 214)) <= 1, "idle hue differs from #5AAFD6");
                    if (i > 100) { minimum = Math.Min(minimum, f.Blue); maximum = Math.Max(maximum, f.Blue); }
                }
                Assert(minimum >= 12 && maximum <= 30 && maximum - minimum >= 15, "breath intensity");
                var a = Frame(engine, car, 8.02, Settings with { Mode = mode });
                var b = Frame(engine, car, 11.02, Settings with { Mode = mode });
                Assert(a == b, "breath period differs from three seconds");
            }
        });
        check("Leaving idle fades back to green; high RPM and warning blackout cannot retain blue", () =>
        {
            var e = new RpmLightEngine(); var car = Car(1200, 0);
            var idle = Frame(e, car, 0); Assert(idle.Blue > idle.Green, "idle setup");
            car.SpeedKmh = 40;
            var departure = Frame(e, car, 0.02);
            Assert(departure.Blue > 0 && departure.Blue < idle.Blue, "departure did not fade");
            var driving = Frame(e, car, 2);
            Assert(driving.Red == 0 && driving.Green == 26 && driving.Blue == 0, "driving retained blue breath");
            e.Reset(); car.SpeedKmh = 0; Frame(e, car, 3);
            car.Rpm = 6000; car.Gas = 1;
            Assert(Frame(e, car, 3.02) == Frame(new RpmLightEngine(), car, 3.02), "rev retained blue tint");
            car.Rpm = 7800; Frame(e, car, 3.04);
            Assert(Dark(Frame(e, car, 3.24)), "blue filled shift blackout");
            car.SpeedKmh = 80; car.PacketId++; car.Brake = 0.7f; car.AbsVibration = 0.2f;
            Assert(Yellow(Frame(e, car, 3.25)), "blue altered braking yellow");
            car.Gear = 0; car.PacketId++; car.Brake = car.AbsVibration = 0;
            var reverse = Frame(e, car, 3.5);
            Assert(reverse.Red == 255 && reverse.Green == 255 && reverse.Blue == 255, "blue altered reverse white");
        });
        check("Low-RPM driving stays lit without breathing and has no 4000-RPM blackout boundary", () =>
        {
            var engine = new RpmLightEngine(); var car = Car(1200, 40);
            var initial = Frame(engine, car, 0);
            for (int i = 1; i <= 100; i++) Assert(Frame(engine, car, i * 0.02) == initial, "moving idle breath");
            int previous = 0;
            for (int rpm = 500; rpm <= 4800; rpm += 50)
            {
                car.Rpm = rpm; var f = Frame(engine, car, 3 + rpm / 10000d);
                Assert(!Dark(f) && f.Green >= previous, "low-RPM gap or brightness reversal");
                previous = f.Green;
            }
        });
        check("ACC RGB modes and all five white LEDs span the stated generic RPM range", () =>
        {
            var engine = new RpmLightEngine();
            foreach (var (rpm, mask) in new[] { (4799, 0), (4800, 1), (5540, 3), (6280, 7), (7020, 15), (7760, 31), (8000, 31) })
                Assert(Frame(engine, Car(rpm), 0).PlayerMask == mask, $"white LED boundary at {rpm}");
            var green = Frame(engine, Car(5000), 0);
            var yellow = Frame(engine, Car(6280), 0);
            Assert(green.Green > green.Red && yellow.Red == 255 && yellow.Green == 255, "smooth progression");
            var low = Frame(engine, Car(6000), 0, Settings with { Mode = RpmLightMode.Discrete });
            var high = Frame(engine, Car(7400), 0, Settings with { Mode = RpmLightMode.Discrete });
            Assert(low.Red == 0 && low.Green > 0 && high.Red == 255 && high.Green == 0, "discrete progression");
        });
        check("Shift flash is 3 Hz, never fills its dark phase with idle light, and whites stay steady", () =>
        {
            var engine = new RpmLightEngine(); var car = Car(7800);
            var on = Frame(engine, car, 0); var off = Frame(engine, car, 0.20); var next = Frame(engine, car, 0.34);
            Assert(on.Red > 0 && Dark(off) && next.Red > 0, "shift phase");
            Assert(on.PlayerMask == 31 && off.PlayerMask == 31 && next.PlayerMask == 31, "white LEDs flashed");
            car.Rpm = 4500; Assert(!Dark(Frame(engine, car, 0.35)), "RPM drop remains in warning");
            car.Rpm = 7800; Assert(!Dark(Frame(engine, car, 0.36)), "new shift warning does not start visible");
            car.Gear = 0;
            var reverse = Frame(engine, car, 0.56);
            Assert(reverse.Red == 255 && reverse.Green == 255 && reverse.Blue == 255, "reverse white lost");
        });
        check("Actual ABS vibration preempts RGB shift flash and preserves the independent RPM LEDs", () =>
        {
            var engine = new RpmLightEngine(); var car = Car(7800);
            Frame(engine, car, 0); car.PacketId++; car.Brake = 0.7f; car.AbsVibration = 0.2f;
            var warning = Frame(engine, car, 0.20);
            Assert(Yellow(warning) && warning.PlayerMask == 31, "braking did not preempt shift blackout");
            var dark = Frame(engine, car, 0.35);
            Assert(Dark(dark) && dark.PlayerMask == 31, "braking blink phase");
            var expired = Frame(engine, car, 0.41);
            var expected = Frame(new RpmLightEngine(), car with { Brake = 0, AbsVibration = 0 }, 0);
            Assert(expired == expected, "duplicate packet kept ABS warning latched");
        });
        check("ABS warning requires braking at speed; NaN, parking and vibration without braking stay quiet", () =>
        {
            foreach (var (speed, brake, vibration) in new[] { (80f, 0f, 1f), (2f, 1f, 1f), (80f, 0.1f, 1f),
                (80f, 1f, float.NaN), (80f, float.NaN, 1f) })
            {
                var car = Car(3000, speed); car.Brake = brake; car.AbsVibration = vibration;
                Assert(!Yellow(Frame(new RpmLightEngine(), car, 0)), "unqualified ABS event");
            }
        });
        check("Sustained wheel slowdown plus longitudinal slip infers lock only after rolling calibration", () =>
        {
            var detector = new AccBrakeLightDetector(); var car = Car();
            detector.Update(car, 0); // A valid rolling observation establishes effective wheel speed.
            car.PacketId++; car.Brake = 0.7f; car.WheelAngularSpeedFl = 30; car.SlipRatioFl = 0.3f;
            detector.Update(car, 0.02); Assert(!detector.IsActive(0.02), "one sample triggered lock");
            car.PacketId++; detector.Update(car, 0.06); Assert(!detector.IsActive(0.06), "lock confirmed too early");
            car.PacketId++; detector.Update(car, 0.10); Assert(detector.IsActive(0.10), "sustained lock missed");
            var noReference = new AccBrakeLightDetector();
            for (int i = 0; i < 10; i++) { car.PacketId++; noReference.Update(car, i * 0.02); }
            Assert(!noReference.IsActive(0.18), "unobserved tyre radius was guessed");
        });
        check("Side slip, rotating tyres, isolated kerb samples and unloaded wheels cannot imply lock", () =>
        {
            for (int scenario = 0; scenario < 4; scenario++)
            {
                var detector = new AccBrakeLightDetector(); var car = Car(); detector.Update(car, 0);
                car.Brake = 0.8f; car.SlipAngleFl = 1; car.WheelSlipFl = 1;
                if (scenario != 0) car.SlipRatioFl = 0.4f;
                if (scenario >= 2) car.WheelAngularSpeedFl = 20;
                if (scenario == 3) { car.WheelLoadFl = 1; car.WheelLoadFr = car.WheelLoadRl = car.WheelLoadRr = 1000; }
                for (int i = 1; i <= 15; i++)
                {
                    if (scenario == 2 && i > 1) { car.WheelAngularSpeedFl = 80; car.SlipRatioFl = 0; }
                    car.PacketId++; detector.Update(car, i * 0.02);
                    Assert(!detector.IsActive(i * 0.02), $"false lock in scenario {scenario}");
                }
            }
        });
        check("Duplicate physics samples and telemetry gaps cannot confirm a transient wheel lock", () =>
        {
            var d = new AccBrakeLightDetector(); var car = Car(); d.Update(car, 0);
            car.PacketId++; car.Brake = 1; car.SlipRatioFl = 0.5f; car.WheelAngularSpeedFl = 0; d.Update(car, 0.02);
            for (int i = 2; i <= 8; i++) d.Update(car, i * 0.02);
            Assert(!d.IsActive(0.16), "duplicate frame confirmed lock");
            car.PacketId++; d.Update(car, 0.5); Assert(!d.IsActive(0.5), "gap counted as sustained slip");
        });
        check("Pause freezes animation and suppresses new brake events; replay and engine-off clear every LED", () =>
        {
            var e = new RpmLightEngine(); var car = Car(7800); var before = Frame(e, car, 0);
            car.PacketId++; car.Brake = 1; car.AbsVibration = 1;
            Assert(Frame(e, car, 2, context: new() { IsPaused = true }) == before, "pause advanced flash or brake event");
            Assert(Frame(e, car, 2.01, context: new() { IsReplay = true }) == RpmLightFrame.Off, "replay lights");
            car.IsEngineRunning = 0;
            Assert(Frame(e, car, 2.02) == RpmLightFrame.Off, "engine off lights");
        });
        check("Disable, reconnect reset and vehicle RPM-range changes discard previous braking state", () =>
        {
            var e = new RpmLightEngine(); var car = Car(); car.Brake = 1; car.AbsVibration = 1;
            Assert(Yellow(Frame(e, car, 0)), "event setup");
            Assert(Frame(e, car, 0.02, Settings with { Enabled = false }) == RpmLightFrame.Off, "disable output");
            car.Brake = car.AbsVibration = 0;
            Assert(!Yellow(Frame(e, car, 0.03)), "disable retained event");
            car.PacketId++; car.Brake = car.AbsVibration = 1; Frame(e, car, 0.04);
            e.Reset(); car.Brake = car.AbsVibration = 0;
            Assert(!Yellow(Frame(e, car, 0.05)), "reconnect retained event");
            car.PacketId++; car.Brake = car.AbsVibration = 1; Frame(e, car, 0.06);
            car.PacketId++; car.Brake = car.AbsVibration = 0;
            Assert(!Yellow(e.Compute(car, At(0.07), Settings, 9000)), "car change retained event");
        });
        check("Lighting classification cannot mutate the shared haptic snapshot", () =>
        {
            var car = Car(5000); car.Brake = 0.8f; car.AbsVibration = 0.2f;
            var before = car; Frame(new RpmLightEngine(), car, 0);
            Assert(car.Equals(before), "lighting modified haptic input");
        });
    }
}
