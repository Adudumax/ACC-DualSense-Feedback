using System.Diagnostics;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Hardware.DualSense;
using ACCDualSenseFeedback.Runtime;
using ACCDualSenseFeedback.Telemetry;

internal static class StartupConfirmationChecks
{
    private static long At(double seconds) => (long)((1 + seconds) * Stopwatch.Frequency);
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static FeedbackFrame Step(StartupTriggerConfirmation cue, double seconds,
        bool connected = true, bool known = true, bool game = false, bool live = false,
        FeedbackFrame? normal = null, Func<FeedbackTuning>? tuning = null)
        => cue.Apply(normal ?? FeedbackFrame.Off, At(seconds), connected, known, game, live, tuning);

    public static void Run(Action<string, Action> check)
    {
        check("Startup matches stable resting pedal curves and user settings across their full range", () =>
        {
            var standard = Step(new StartupTriggerConfirmation(), 0);
            Assert(standard.LeftTrigger == TriggerEffect.FeedbackZones(3, 4, 4, 5, 5, 6, 6, 7, 7, 8),
                "L2 differs from the v1.0.1 resting curve");
            Assert(standard.RightTrigger == TriggerEffect.FeedbackZones(2, 2, 2, 3, 3, 3, 4, 4, 5, 5),
                "R2 differs from the v1.0.1 resting curve");
            for (int value = 0; value <= 100; value++)
            {
                var tuning = (FeedbackProfile.Default with
                {
                    ActivePreset = FeedbackPreset.Custom,
                    BrakeResistance = value,
                    ThrottleResistance = 100 - value
                }).ToTuning();
                var resting = new CompetitionFeedbackEngine().Compute(new TelemetrySnapshot
                {
                    Rpm = 1000, CurrentMaxRpm = 8000, Gear = 1, IsEngineRunning = 1
                }, At(0), 0, 0, tuning);
                var startup = Step(new StartupTriggerConfirmation(), 0, tuning: () => tuning);
                var restingTriggers = new FeedbackFrame(0, 0, resting.LeftTrigger, resting.RightTrigger);
                Assert(startup == restingTriggers, $"startup differs from resting triggers at setting {value}");
                Assert(startup.LeftActuator == 0 && startup.RightActuator == 0, "grip rumble enabled");
                foreach (bool bluetooth in new[] { false, true })
                {
                    byte[] startupReport = new byte[78], restingReport = new byte[78];
                    DualSenseReportBuilder.Build(startup, bluetooth, startupReport);
                    DualSenseReportBuilder.Build(restingTriggers, bluetooth, restingReport);
                    Assert(startupReport.SequenceEqual(restingReport), "resting report mismatch");
                }
            }
        });
        check("Startup waits for the first game-process probe, then releases both triggers at 120 ms", () =>
        {
            var cue = new StartupTriggerConfirmation();
            Assert(Step(cue, 0, known: false) == FeedbackFrame.Off, "acted before process state was known");
            var pulse = Step(cue, 0.01);
            Assert(pulse.LeftTrigger.Mode == 0x21 && pulse.RightTrigger.Mode == 0x21, "missing confirmation");
            Assert(pulse.LeftActuator == 0 && pulse.RightActuator == 0, "confirmation enabled grip rumble");
            Assert(Step(cue, 0.129) == pulse, "premature release");
            var release = cue.Apply(FeedbackFrame.Off, At(0.01) + StartupTriggerConfirmation.DurationTicks,
                true, true, false, false);
            Assert(release == FeedbackFrame.Off && Step(cue, 1) == FeedbackFrame.Off, "confirmation did not release");
            foreach (bool bluetooth in new[] { false, true })
            {
                byte[] report = new byte[78];
                DualSenseReportBuilder.Build(pulse, bluetooth, report);
                int offset = bluetooth ? 1 : 0;
                Assert(report[3 + offset] == 0 && report[4 + offset] == 0 &&
                    report[11 + offset] == 0x21 && report[22 + offset] == 0x21, "startup report fields");
                DualSenseReportBuilder.Build(release, bluetooth, report);
                Assert(report[11 + offset] == 5 && report[22 + offset] == 5, "release report fields");
            }
        });
        check("Opening retries do not consume the cue; failed-write reconnects cannot replay it", () =>
        {
            var cue = new StartupTriggerConfirmation();
            for (int i = 0; i < 5; i++)
                Assert(Step(cue, i, connected: false) == FeedbackFrame.Off, "no-device output");
            Assert(Step(cue, 5).LeftTrigger.Mode == 0x21, "first connection did not confirm");
            cue.Cancel(); // The production writer calls this before disposing a failed device.
            Assert(Step(cue, 5.02, connected: false) == FeedbackFrame.Off, "disconnected cue");
            Assert(Step(cue, 5.04) == FeedbackFrame.Off, "failed write replayed confirmation");
            Assert(Step(cue, 10) == FeedbackFrame.Off, "later reconnect replayed confirmation");
        });
        check("Runtime shutdown and retry keep the same consumed process confirmation", () =>
        {
            var cue = new StartupTriggerConfirmation();
            Assert(Step(cue, 0).LeftTrigger.Mode == 0x21, "setup");
            cue.Cancel(); cue.Cancel();
            Assert(Step(cue, 0.04) == FeedbackFrame.Off, "shutdown left cue active");
            Assert(Step(cue, 4) == FeedbackFrame.Off, "runtime retry replayed confirmation");
            var beforeConnection = new StartupTriggerConfirmation(); beforeConnection.Cancel();
            Assert(Step(beforeConnection, 4).LeftTrigger.Mode == 0x21, "retry before any device consumed cue");
        });
        check("ACC already running skips startup confirmation even before its first live telemetry packet", () =>
        {
            var cue = new StartupTriggerConfirmation();
            Assert(Step(cue, 0, known: false) == FeedbackFrame.Off, "unverified probe");
            Assert(Step(cue, 0.02, game: true) == FeedbackFrame.Off, "confirmation while ACC is running");
            Assert(Step(cue, 2) == FeedbackFrame.Off, "confirmation played after ACC exit");
        });
        check("Live feedback cancels a startup pulse immediately and remains byte-for-byte unchanged", () =>
        {
            var cue = new StartupTriggerConfirmation(); Step(cue, 0);
            var gameFrame = new FeedbackFrame(73, 121,
                TriggerEffect.Vibration(2, 4, 19), TriggerEffect.FeedbackZones(2, 3, 3, 4, 4, 5, 5, 6, 6, 7));
            Assert(Step(cue, 0.02, live: true, normal: gameFrame,
                tuning: () => throw new InvalidOperationException("startup requested game tuning")) == gameFrame,
                "startup changed game feedback");
            Assert(Step(cue, 0.04) == FeedbackFrame.Off, "startup resumed after live feedback");
            var guard = new StartupTriggerConfirmation();
            Assert(Step(guard, 0, normal: gameFrame) == gameFrame, "existing output did not take priority");
        });
        check("Startup respects disabled sides, the profile strength range and settings switched off mid-pulse", () =>
        {
            var off = FeedbackTuning.Neutral with { BrakeResistanceScale = 0, ThrottleResistanceScale = 0 };
            var cue = new StartupTriggerConfirmation();
            Assert(Step(cue, 0, tuning: () => off) == FeedbackFrame.Off, "disabled triggers confirmed");
            Assert(Step(cue, 0.02) == FeedbackFrame.Off, "disabled cue armed later in its time window");
            Assert(Step(cue, 1) == FeedbackFrame.Off, "enabling settings later replayed cue");
            var rightOnly = off with { ThrottleResistanceScale = 1 };
            var oneSide = new StartupTriggerConfirmation();
            var right = Step(oneSide, 0, tuning: () => rightOnly);
            Assert(right.LeftTrigger == TriggerEffect.Off && right.RightTrigger.Mode == 0x21, "per-side setting");
            Assert(Step(oneSide, 0.02).LeftTrigger == TriggerEffect.Off, "disabled side activated mid-cue");
            var leftOnly = off with { BrakeResistanceScale = 1 };
            var left = Step(new StartupTriggerConfirmation(), 0, tuning: () => leftOnly);
            Assert(left.LeftTrigger.Mode == 0x21 && left.RightTrigger == TriggerEffect.Off, "left-only setting");
            var maximum = FeedbackTuning.Neutral with { BrakeResistanceScale = 100, ThrottleResistanceScale = 100 };
            var normal = Step(new StartupTriggerConfirmation(), 0);
            var maximumProfile = (FeedbackProfile.Default with
            {
                ActivePreset = FeedbackPreset.Custom, BrakeResistance = 100, ThrottleResistance = 100
            }).ToTuning();
            var full = Step(new StartupTriggerConfirmation(), 0, tuning: () => maximumProfile);
            Assert(full != normal, "custom strength was still capped to the default");
            Assert(Step(new StartupTriggerConfirmation(), 0, tuning: () => maximum) == full, "exceeded profile range");
            Assert(Step(new StartupTriggerConfirmation(), 0,
                tuning: () => off with { BrakeResistanceScale = -1, ThrottleResistanceScale = -1 })
                == FeedbackFrame.Off, "negative strength");
            Assert(Step(new StartupTriggerConfirmation(), 0,
                tuning: () => off with { BrakeResistanceScale = float.NaN, ThrottleResistanceScale = float.PositiveInfinity })
                == FeedbackFrame.Off, "invalid strength");
            var changing = new StartupTriggerConfirmation(); Step(changing, 0);
            Assert(Step(changing, 0.02, tuning: () => off) == FeedbackFrame.Off, "settings-off did not release");
            Assert(Step(changing, 0.04) == FeedbackFrame.Off, "settings toggle replayed startup action");
        });
        check("Startup timing is nonblocking and delayed updates expire instead of extending the pulse", () =>
        {
            var cue = new StartupTriggerConfirmation(); Step(cue, 0);
            var clock = Stopwatch.StartNew();
            for (int i = 1; i <= 1000; i++) Step(cue, i / 10000d);
            Assert(clock.Elapsed < TimeSpan.FromSeconds(1), "startup update blocked");
            Assert(Step(cue, 3) == FeedbackFrame.Off, "delayed update restarted duration");
        });
        check("Startup provider failure can be cancelled without rearming after runtime cleanup", () =>
        {
            var cue = new StartupTriggerConfirmation(); bool failed = false;
            try { Step(cue, 0, tuning: () => throw new InvalidOperationException("test failure")); }
            catch (InvalidOperationException) { failed = true; }
            finally { cue.Cancel(); }
            Assert(failed && Step(cue, 0.02) == FeedbackFrame.Off, "cleanup rearmed failed confirmation");
        });
    }
}
