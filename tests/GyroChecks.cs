using System.Numerics;
using System.IO;
using ACCDualSenseFeedback.Gyro;
using ACCDualSenseFeedback.Hardware.DualSense;

internal static class GyroChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("Gyro mapping is symmetric, continuous at deadzone, saturating and invertible", () =>
        {
            var s = GyroSteeringSettings.Default;
            Close(GyroSteeringProcessor.MapAngle(0, s), 0);
            Close(GyroSteeringProcessor.MapAngle(1, s), 0);
            Assert(GyroSteeringProcessor.MapAngle(1.001f, s) < .00002f, "deadzone discontinuity");
            Close(GyroSteeringProcessor.MapAngle(45, s), 44f / 89);
            for (float angle = 0; angle < 181; angle += .7f)
            {
                Close(GyroSteeringProcessor.MapAngle(angle, s), -GyroSteeringProcessor.MapAngle(-angle, s));
                Close(GyroSteeringProcessor.MapAngle(angle, s), -GyroSteeringProcessor.MapAngle(angle, s with { Invert = true }));
            }
            Close(GyroSteeringProcessor.MapAngle(90, s), 1);
            Close(GyroSteeringProcessor.MapAngle(-180, s), -1);
            Assert(GyroSteeringProcessor.MapAngle(30, s with { Curve = GyroResponseCurve.SoftCenter }) < GyroSteeringProcessor.MapAngle(30, s), "soft curve not softer");
            Close(GyroSteeringProcessor.MapAngle(float.NaN, s), 0);
        });
        check("Gyro settings reject corrupt values and reset preserves explicit choices", () =>
        {
            var bad = new GyroSteeringSettings { FullLockDegrees = float.NaN, DeadzoneDegrees = 500, ResponseExponent = -10,
                TakeoverPercent = 200, Curve = (GyroResponseCurve)99, Smoothing = (GyroSmoothing)99, RecenterButton = GyroButton.L3, ActivationButton = GyroButton.L3 };
            var clean = bad.Sanitize();
            Close(clean.FullLockDegrees, 90); Close(clean.DeadzoneDegrees, 5); Close(clean.ResponseExponent, .5f); Close(clean.TakeoverPercent, 50);
            Assert(clean.RecenterButton == GyroButton.None && clean.ActivationButton == GyroButton.L3 && clean.Curve == GyroResponseCurve.Linear, "retired binding removed an active shortcut or invalid enum accepted");
            var reset = (clean with { Enabled = true, FullLockDegrees = 45, ForwardShortcutButtons = true }).ResetSteeringParameters();
            Assert(reset.Enabled && reset.ActivationButton == GyroButton.L3 && reset.RecenterButton == GyroButton.None && !reset.ForwardShortcutButtons && reset.FullLockDegrees == 90, "reset overwrote enable/active binding or restored retired options");
        });
        check("Default preset gates gyro and every shortcut while preserving physical input", () =>
        {
            using var rig = new Rig();
            rig.Settings = rig.Settings with { RecenterButton = GyroButton.L3, ActivationButton = GyroButton.R3 };
            rig.Custom = false;
            GyroControlState.RequestCalibration(); GyroControlState.RequestRecenter(); GyroControlState.RequestResume();
            var result = rig.Frame(.347f, buttons: GyroButton.L3 | GyroButton.R3);
            Assert(result.Status.State == GyroRuntimeState.Disabled && !result.Status.IsCalibrating && result.ConsumedButtons == GyroButton.None, "preset bypass");
            Close(result.OutputX, .347f);
            Assert(rig.Settings.Enabled, "default mode overwrote saved custom switch");
        });
        check("Stationary calibration is explicit, rejects driving and mutes until complete", () =>
        {
            using var rig = new Rig();
            rig.Driving = true;
            GyroControlState.RequestCalibration();
            Assert(!rig.Frame().Status.IsCalibrating, "calibration started while driving");
            rig.Driving = false;
            GyroControlState.RequestCalibration();
            var result = rig.Frame();
            Assert(result.Status.SuppressFeedback && !result.Status.StationaryCalibrated, "feedback did not mute");
            for (int i = 0; i < 300; i++) result = rig.Frame(gyro: new(.25f, -.15f, .1f));
            Assert(result.Status.StationaryCalibrated && !result.Status.SuppressFeedback && result.Status.NeedsRecenter, "calibration did not finish / recentered itself");
            Close(rig.Fusion.Bias.X, .25f, .002f);
            rig.Driving = true;
            int changes = rig.Fusion.BiasChanges;
            for (int i = 0; i < 1000; i++) rig.Frame(gyro: new(0, 0, .2f));
            Assert(rig.Fusion.BiasChanges == changes, "bias learned while driving");
        });
        check("Motion interrupts calibration and driving cancels pending calibration", () =>
        {
            using var rig = new Rig();
            GyroControlState.RequestCalibration();
            for (int i = 0; i < 200; i++) rig.Frame();
            var moving = rig.Frame(gyro: new(20, 0, 0));
            Close(moving.Status.CalibrationProgress, 0);
            rig.Driving = true;
            Assert(!rig.Frame().Status.IsCalibrating, "started driving did not cancel calibration");
        });
        check("Holding a long corner keeps its angle and stick override never recenters", () =>
        {
            using var rig = new Rig(); rig.Prepare();
            rig.Fusion.ClockwiseDegrees = 45;
            var result = rig.Frame();
            for (int i = 0; i < 2000; i++) result = rig.Frame();
            Close(result.Status.AngleDegrees, 45); Close(result.OutputX, 44f / 89);
            result = rig.Frame(-.7f);
            Close(result.OutputX, -.7f);
            Assert(result.Status.InputSource == GyroInputSource.StickTakeover, "stick not immediate");
            for (int i = 0; i < 10; i++) result = rig.Frame(.1f);
            Assert(result.Status.InputSource == GyroInputSource.StickTakeover, "released before dwell");
            for (int i = 0; i < 20; i++) result = rig.Frame();
            Close(result.Status.AngleDegrees, 45); Close(result.OutputX, 44f / 89);
            Assert(!result.Status.NeedsRecenter, "override invalidated center");
        });
        check("Relative quaternion handles arbitrary neutral and wrap without opposite full steering", () =>
        {
            using var rig = new Rig(); rig.Prepare();
            rig.Fusion.BaseOrientation = Quaternion.CreateFromYawPitchRoll(.8f, .4f, .2f);
            GyroControlState.RequestRecenter(); rig.Frame();
            foreach (float angle in new[] { 90f, 170f, 179f, 181f, 190f, 200f })
            {
                rig.Fusion.ClockwiseDegrees = angle;
                var result = rig.Frame(); Close(result.Status.AngleDegrees, angle, .001f);
                Assert(result.Status.GyroX > 0, "wrap switched direction");
            }
        });
        check("Always starts in ACC and automatically resumes pauses, menus and foreground changes without a button", () =>
        {
            foreach (GyroButton binding in new[] { GyroButton.None, GyroButton.R3 })
            {
                using var rig = new Rig();
                rig.Settings = rig.Settings with { ActivationButton = binding };
                rig.Foreground = false; rig.Suspended = true;
                rig.Prepare(); rig.Fusion.ClockwiseDegrees = 30;
                var waiting = rig.Settle();
                long neutral = waiting.Status.RecenterSequence;
                Assert(waiting.Status.Ready && waiting.Status.State == GyroRuntimeState.Suspended, "tool foreground bypassed the output gate");
                rig.Foreground = true; rig.Suspended = false; rig.Driving = true;
                var entered = rig.Frame();
                Assert(entered.Status.State == GyroRuntimeState.Active && entered.Status.InputSource == GyroInputSource.ReturningToGyro,
                    $"Always required an initial button or skipped handoff ({binding})");
                Close(entered.OutputX, 0);
                Close(rig.Settle().OutputX, 29f / 89);
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    foreach (var (paused, foreground) in new[] { (true, true), (false, false), (true, false) })
                    {
                        rig.Suspended = paused; rig.Foreground = foreground; rig.Driving = !paused;
                        var stopped = rig.Settle(.1f);
                        Assert(stopped.Status.State == GyroRuntimeState.Suspended && stopped.Status.Ready && !stopped.Status.NeedsRecenter,
                            "pause/menu/foreground change erased a valid neutral or kept gyro active");
                        Close(stopped.OutputX, .1f);
                        rig.Suspended = false; rig.Foreground = true; rig.Driving = true;
                        var resumed = rig.Frame();
                        Assert(resumed.Status.State == GyroRuntimeState.Active && resumed.Status.InputSource == GyroInputSource.ReturningToGyro,
                            "Always did not resume with the existing handoff");
                        Close(resumed.OutputX, 0);
                        resumed = rig.Settle();
                        Close(resumed.OutputX, 29f / 89); Close(resumed.Status.AngleDegrees, 30);
                        Assert(resumed.Status.RecenterSequence == neutral, "resuming into the next race silently reset straight ahead");
                    }
                }
                rig.Suspended = true; rig.Frame(); // One context-frame telemetry interruption, not an IMU timestamp fault.
                rig.Suspended = false;
                Assert(rig.Settle().Status.State == GyroRuntimeState.Active, "short live-context interruption latched Always off");
                Close(rig.Frame().OutputX, 29f / 89);
            }
        });
        check("Always manual stop survives environment changes until another button or explicit UI resume", () =>
        {
            using var rig = new Rig(); rig.Prepare(); rig.Fusion.ClockwiseDegrees = 45;
            rig.Settings = rig.Settings with { ActivationButton = GyroButton.R3 };
            rig.Settle();
            Assert(rig.Frame(buttons: GyroButton.R3).Status.State == GyroRuntimeState.Suspended, "Always button did not stop gyro");
            Close(rig.Settle().OutputX, 0);
            for (int cycle = 0; cycle < 3; cycle++)
            {
                rig.Suspended = true; rig.Foreground = false; rig.Settle();
                rig.Suspended = false; rig.Foreground = true;
                var stopped = rig.Settle();
                Assert(stopped.Status.State == GyroRuntimeState.Suspended && stopped.Status.Ready, "environment resumed a user's manual stop");
                Close(stopped.OutputX, 0); Close(stopped.Status.AngleDegrees, 45);
            }
            Assert(rig.Frame(buttons: GyroButton.R3).Status.State == GyroRuntimeState.Active, "next Always press did not resume");
            Close(rig.Settle().OutputX, 44f / 89);
            rig.Frame(buttons: GyroButton.R3); rig.Settle(); // Stop again, then resume explicitly from the tool.
            rig.Foreground = false; rig.Frame();
            GyroControlState.RequestResume();
            Assert(rig.Frame().Status.State == GyroRuntimeState.Suspended, "UI resume bypassed the foreground gate");
            rig.Foreground = true;
            var uiResumed = rig.Settle();
            Assert(uiResumed.Status.State == GyroRuntimeState.Active, "UI resume intent was lost while tool was foreground");
            Close(uiResumed.Status.AngleDegrees, 45); Close(uiResumed.OutputX, 44f / 89);
        });
        check("Explicitly re-enabling Always through its switch or Custom clears a manual stop without recentering", () =>
        {
            foreach (bool usePreset in new[] { false, true })
            {
                using var rig = new Rig(); rig.Prepare(); rig.Fusion.ClockwiseDegrees = 45;
                rig.Settings = rig.Settings with { ActivationButton = GyroButton.R3 };
                long neutral = rig.Settle().Status.RecenterSequence;
                rig.Frame(buttons: GyroButton.R3);
                Assert(rig.Settle().Status.State == GyroRuntimeState.Suspended, "manual stop did not persist");
                rig.Foreground = false; rig.Suspended = true;
                if (usePreset) rig.Custom = false;
                else rig.Settings = rig.Settings with { Enabled = false };
                Assert(rig.Settle().Status.State == GyroRuntimeState.Disabled, "off/default did not disable gyro");
                if (usePreset) rig.Custom = true;
                else rig.Settings = rig.Settings with { Enabled = true };
                Assert(rig.Frame().Status.State == GyroRuntimeState.Suspended, "explicit enable bypassed game conditions");
                rig.Foreground = true; rig.Suspended = false;
                var resumed = rig.Settle();
                Assert(resumed.Status.State == GyroRuntimeState.Active && resumed.Status.RecenterSequence == neutral,
                    "explicit enable retained a stale manual stop or reset neutral");
                Close(resumed.Status.AngleDegrees, 45); Close(resumed.OutputX, 44f / 89);
            }
        });
        check("Switching to Always clears another activation mode's pending resume without requiring a binding", () =>
        {
            foreach (GyroActivationMode previous in new[] { GyroActivationMode.Hold, GyroActivationMode.Toggle })
            {
                using var rig = new Rig(); rig.Prepare(); rig.Fusion.ClockwiseDegrees = 45;
                rig.Settings = rig.Settings with { ActivationMode = previous, ActivationButton = GyroButton.R3 };
                rig.Frame(); rig.Frame(buttons: GyroButton.R3);
                rig.Foreground = false; rig.Settle(buttons: GyroButton.R3);
                rig.Settings = rig.Settings with { ActivationMode = GyroActivationMode.Always, ActivationButton = GyroButton.None };
                Assert(rig.Frame().Status.State == GyroRuntimeState.Suspended, "mode change bypassed foreground protection");
                rig.Foreground = true;
                var resumed = rig.Settle();
                Assert(resumed.Status.State == GyroRuntimeState.Active && resumed.Status.Ready, "Always retained another mode's resume lock");
                Close(resumed.Status.AngleDegrees, 45); Close(resumed.OutputX, 44f / 89);
            }
        });
        check("Hold and Toggle retain explicit resume after suspension while inactive presets cannot create a lock", () =>
        {
            foreach (GyroActivationMode mode in new[] { GyroActivationMode.Hold, GyroActivationMode.Toggle })
            {
                using var rig = new Rig(); rig.Prepare(); rig.Fusion.ClockwiseDegrees = 30;
                rig.Settings = rig.Settings with { ActivationMode = mode, ActivationButton = GyroButton.R3 };
                rig.Frame(); rig.Settle(buttons: GyroButton.R3);
                rig.Suspended = true; rig.Settle(buttons: GyroButton.R3);
                rig.Suspended = false;
                Assert(rig.Frame(buttons: GyroButton.R3).Status.State == GyroRuntimeState.Suspended, $"{mode} automatically resumed");
                rig.Frame();
                Assert(rig.Frame(buttons: GyroButton.R3).Status.State == GyroRuntimeState.Active, $"fresh {mode} press did not resume");
                rig.Foreground = false; rig.Frame(buttons: GyroButton.R3);
                GyroControlState.RequestResume(); rig.Frame(buttons: GyroButton.R3);
                rig.Foreground = true;
                Assert(rig.Frame(buttons: GyroButton.R3).Status.State == GyroRuntimeState.Active, $"{mode} lost an explicit UI resume");
                foreach (bool usePreset in new[] { false, true })
                {
                    if (usePreset) rig.Custom = false;
                    else rig.Settings = rig.Settings with { Enabled = false };
                    rig.Frame(buttons: GyroButton.R3);
                    rig.Foreground = false; rig.Settle(buttons: GyroButton.R3);
                    rig.Foreground = true; rig.Frame(buttons: GyroButton.R3);
                    if (usePreset) rig.Custom = true;
                    else rig.Settings = rig.Settings with { Enabled = true };
                    Assert(rig.Frame(buttons: GyroButton.R3).Status.State == GyroRuntimeState.Active, $"inactive {mode} preset created a resume lock");
                }
                Close(rig.Frame(buttons: GyroButton.R3).Status.AngleDegrees, 30);
            }
        });
        check("Always auto-resume and explicit re-enable never repair a genuinely invalid neutral", () =>
        {
            using var rig = new Rig(); rig.Prepare(); rig.Fusion.ClockwiseDegrees = 45; rig.Settle();
            Assert(rig.Frame(valid: false).Status.NeedsRecenter, "IMU failure retained neutral");
            rig.Settings = rig.Settings with { Enabled = false }; rig.Frame();
            rig.Settings = rig.Settings with { Enabled = true };
            var reenabled = rig.Settle();
            Assert(reenabled.Status.State == GyroRuntimeState.WaitingForCenter && reenabled.Status.NeedsRecenter && !reenabled.Status.Ready,
                "Always enable forged a valid neutral after IMU failure");
            rig.Suspended = true; rig.Frame(); rig.Suspended = false;
            Assert(rig.Settle().Status.NeedsRecenter, "environment recovery forged neutral");
            rig.Settings = rig.Settings with { ActivationMode = GyroActivationMode.Toggle }; rig.Frame();
            rig.Settings = rig.Settings with { ActivationMode = GyroActivationMode.Always };
            Assert(rig.Settle().Status.NeedsRecenter, "switching to Always forged neutral");
            Close(rig.Frame(.2f).OutputX, .2f);
        });
        check("Steering test preview follows the controller while game-output protections remain active", () =>
        {
            using var rig = new Rig(); rig.Prepare();
            rig.Foreground = false; rig.Suspended = true;
            rig.Fusion.ClockwiseDegrees = 45;
            GyroProcessResult result = default;
            for (int i = 0; i < 12; i++) result = rig.Frame(-.31f);
            Assert(result.Status.SteeringPreview.IsAvailable, "tool-foreground test preview unavailable");
            Close(result.Status.SteeringPreview.AngleDegrees, 45);
            Close(result.Status.SteeringPreview.MappedX, 44f / 89);
            Close(result.Status.SteeringPreview.OutputX, 44f / 89);
            Close(result.OutputX, -.31f);
            Assert(result.Status.State == GyroRuntimeState.Suspended, "test preview bypassed actual pause protection");
            rig.Fusion.ClockwiseDegrees = -30;
            result = rig.Frame(.22f);
            Close(result.Status.SteeringPreview.AngleDegrees, -30);
            Close(result.Status.SteeringPreview.OutputX, -29f / 89);
            Close(result.OutputX, .22f);
            rig.Suspended = false; rig.Foreground = true;
            for (int i = 0; i < 12; i++) result = rig.Frame();
            Assert(result.Status.State == GyroRuntimeState.Active && result.Status.SteeringPreview.IsAvailable, "Always did not resume after game conditions recovered");
            Close(result.OutputX, -29f / 89);
        });
        check("Steering preview uses the same deadzone, full lock, response curve and inversion as steering", () =>
        {
            using var rig = new Rig(); rig.Prepare(); rig.Foreground = false;
            rig.Settings = rig.Settings with { FullLockDegrees = 60, DeadzoneDegrees = 2 };
            foreach (var (angle, expected) in new[] { (0f, 0f), (1f, 0f), (2f, 0f), (31f, .5f), (60f, 1f), (90f, 1f), (-31f, -.5f), (-90f, -1f) })
            {
                rig.Fusion.ClockwiseDegrees = angle;
                var result = rig.Frame();
                Assert(result.Status.SteeringPreview.IsAvailable, "valid angle not previewed");
                Close(result.Status.SteeringPreview.AngleDegrees, angle);
                Close(result.Status.SteeringPreview.MappedX, expected);
                Close(result.Status.SteeringPreview.OutputX, expected);
            }
            rig.Fusion.ClockwiseDegrees = 31;
            rig.Settings = rig.Settings with { Curve = GyroResponseCurve.SoftCenter };
            Close(rig.Frame().Status.SteeringPreview.OutputX, MathF.Pow(.5f, 1.6f));
            rig.Settings = rig.Settings with { Curve = GyroResponseCurve.Custom, ResponseExponent = 2 };
            Close(rig.Frame().Status.SteeringPreview.OutputX, .25f);
            rig.Settings = rig.Settings with { Invert = true };
            Close(rig.Frame().Status.SteeringPreview.OutputX, -.25f);
        });
        check("Steering preview preserves configured smoothing without showing stick takeover as gyro output", () =>
        {
            using var rig = new Rig(); rig.Prepare();
            rig.Settings = rig.Settings with { Smoothing = GyroSmoothing.Custom, CustomSmoothingMilliseconds = 30 };
            rig.Fusion.ClockwiseDegrees = 45;
            var first = rig.Frame(-.8f);
            Close(first.Status.SteeringPreview.MappedX, 44f / 89);
            float expected = 44f / 89 * (1 - MathF.Exp(-.01f / .03f));
            Close(first.Status.SteeringPreview.OutputX, expected);
            Close(first.OutputX, -.8f);
            Assert(first.Status.InputSource == GyroInputSource.StickTakeover, "physical takeover was lost");
            GyroProcessResult settled = first;
            for (int i = 0; i < 40; i++) settled = rig.Frame(-.8f);
            Close(settled.Status.SteeringPreview.OutputX, 44f / 89);
            Close(settled.OutputX, -.8f);
            rig.Settings = rig.Settings with { ActivationMode = GyroActivationMode.Hold, ActivationButton = GyroButton.R3 };
            settled = rig.Frame();
            Assert(settled.Status.State == GyroRuntimeState.Suspended && settled.Status.SteeringPreview.IsAvailable, "unheld activation disabled the test preview");
        });
        check("Steering preview never retains a stale ready value after invalidation or a disabled preset", () =>
        {
            using var rig = new Rig();
            Assert(!rig.Frame().Status.SteeringPreview.IsAvailable, "uncalibrated preview available");
            rig.Prepare(); rig.Fusion.ClockwiseDegrees = 70;
            Assert(rig.Frame().Status.SteeringPreview.IsAvailable, "prepared preview unavailable");
            var invalid = rig.Frame(.2f, valid: false).Status.SteeringPreview;
            Assert(!invalid.IsAvailable, "invalid sensor retained test output");
            Close(invalid.AngleDegrees, 0); Close(invalid.OutputX, 0);
            Assert(!rig.Frame().Status.SteeringPreview.IsAvailable, "recovered sensor silently restored preview neutral");
            GyroControlState.RequestRecenter();
            Assert(rig.Frame().Status.SteeringPreview.IsAvailable, "explicit neutral did not restore test preview");
            rig.Custom = false;
            Assert(!rig.Frame().Status.SteeringPreview.IsAvailable, "default preset published an enabled gyro preview");
            rig.Custom = true; rig.Settings = rig.Settings with { Enabled = false };
            Assert(!rig.Frame().Status.SteeringPreview.IsAvailable, "disabled gyro published an enabled preview");
        });
        check("IMU faults fall back immediately while valid input and disconnected native remain usable", () =>
        {
            using var rig = new Rig(); rig.Prepare(); rig.Fusion.ClockwiseDegrees = 70;
            for (int i = 0; i < 10; i++) rig.Frame();
            var invalid = rig.Frame(-.42f, valid: false);
            Close(invalid.OutputX, -.42f);
            Assert(invalid.Status.NeedsRecenter && !invalid.Status.ImuValid, "fault retained neutral");
            var recovered = rig.Frame();
            Assert(recovered.Status.NeedsRecenter, "fault silently recentered");
            rig.Fusion.Available = false;
            var absent = rig.Frame(.22f);
            Close(absent.OutputX, .22f); Assert(absent.Status.State == GyroRuntimeState.NativeUnavailable, "native failure not surfaced");
        });
        check("Disabling gyro and switching to default hand steering back without overwriting custom", () =>
        {
            using var rig = new Rig(); rig.Prepare(); rig.Fusion.ClockwiseDegrees = 60;
            for (int i = 0; i < 10; i++) rig.Frame();
            rig.Custom = false;
            var first = rig.Frame(-.1f);
            Assert(first.Status.State == GyroRuntimeState.Disabled && first.Status.InputSource == GyroInputSource.ReturningToStick && first.OutputX > .5f, "uncontrolled preset handoff");
            for (int i = 0; i < 10; i++) first = rig.Frame(-.1f);
            Close(first.OutputX, -.1f);
            Assert(rig.Settings.Enabled, "preset erased saved enable");
            rig.Custom = true;
            for (int i = 0; i < 10; i++) first = rig.Frame();
            Assert(first.Status.State == GyroRuntimeState.Active && first.Status.AngleDegrees > 59, "custom switch secretly recentered");
            rig.Settings = rig.Settings with { Enabled = false };
            for (int i = 0; i < 10; i++) first = rig.Frame(.1f);
            Close(first.OutputX, .1f);
        });
        check("Hold and toggle use explicit chosen buttons and preserve the same neutral", () =>
        {
            using var rig = new Rig(); rig.Prepare(); rig.Fusion.ClockwiseDegrees = 45;
            rig.Settings = rig.Settings with { ActivationMode = GyroActivationMode.Hold, ActivationButton = GyroButton.R3 };
            Assert(rig.Frame().Status.State == GyroRuntimeState.Suspended, "unheld hold activated");
            var held = rig.Frame(buttons: GyroButton.R3);
            Assert(held.Status.State == GyroRuntimeState.Active && held.ConsumedButtons == GyroButton.R3, "hold not active");
            Assert(rig.Frame().Status.State == GyroRuntimeState.Suspended, "release remained active");
            rig.Settings = rig.Settings with { ActivationMode = GyroActivationMode.Toggle };
            Assert(rig.Frame().Status.State == GyroRuntimeState.Suspended, "toggle starts without explicit press");
            var toggled = rig.Frame(buttons: GyroButton.R3);
            Assert(toggled.Status.State == GyroRuntimeState.Active, "toggle did not enable");
            for (int i = 0; i < 10; i++) toggled = rig.Frame(buttons: GyroButton.R3);
            Assert(toggled.Status.State == GyroRuntimeState.Active, "held toggle retriggered");
            rig.Frame();
            Assert(rig.Frame(buttons: GyroButton.R3).Status.State == GyroRuntimeState.Suspended, "second toggle did not disable");
            Close(toggled.Status.AngleDegrees, 45);
        });
        check("Device reconnect and reference change invalidate neutral without discarding controller input", () =>
        {
            using var rig = new Rig(); rig.Prepare();
            rig.Settings = rig.Settings with { ReferenceMode = GyroReferenceMode.Gravity };
            var changed = rig.Frame(.2f);
            Assert(changed.Status.NeedsRecenter, "reference change retained neutral");
            rig.Processor.ResetConnection("replacement-synthetic");
            changed = rig.Frame(-.3f);
            Assert(changed.Status.NeedsRecenter && !changed.Status.StationaryCalibrated, "different device reused previous calibration/center");
            Close(changed.OutputX, -.3f);
        });
        check("Device wrap is continuous; repeated, backward and long gaps never integrate old steering", () =>
        {
            using var rig = new Rig(); rig.Prepare();
            rig.Timestamp = uint.MaxValue - 40_000;
            rig.Frame(); // Intentional jump invalidates reference, establish it explicitly again.
            GyroControlState.RequestRecenter(); rig.Frame();
            var afterWrap = rig.Frame();
            Assert(afterWrap.Status.ImuValid && !afterWrap.Status.NeedsRecenter, "unsigned device clock wrap failed");
            int processed = rig.Fusion.Processed;
            for (int i = 0; i < 3; i++) rig.Frame(advanceTimestamp: false);
            Assert(rig.Fusion.Processed == processed, "duplicate integrated");
            var stale = rig.Frame(advanceTimestamp: false);
            for (int i = 0; i < 4; i++) stale = rig.Frame(.3f, advanceTimestamp: false);
            Assert(!stale.Status.ImuValid && stale.Status.NeedsRecenter, "frozen timestamp retained gyro"); Close(stale.OutputX, .3f);
            rig.Timestamp -= 30_000;
            Assert(rig.Frame().Status.NeedsRecenter, "backward timestamp retained reference");
            rig.Timestamp += 600_000;
            Assert(rig.Frame().Status.NeedsRecenter, "long gap retained reference");
        });
        check("Gravity reference rejects near vertical rotation axis instead of saturating", () =>
        {
            using var rig = new Rig(); rig.Settings = rig.Settings with { ReferenceMode = GyroReferenceMode.Gravity };
            rig.Prepare();
            rig.Fusion.Gravity = new(0, 0, -1);
            var result = rig.Frame(.4f);
            Assert(result.Status.NeedsRecenter && result.Status.InputSource == GyroInputSource.PhysicalStick, "unobservable pose remained active");
            Close(result.OutputX, .4f);
        });
        check("Learned shortcut requires fresh press and consumes only explicitly chosen controls", () =>
        {
            using var rig = new Rig();
            long request = GyroControlState.RequestLearnButton(GyroBindingTarget.Activation);
            Assert(rig.Frame(buttons: GyroButton.L3).Status.PendingBindingTarget == GyroBindingTarget.Activation, "held button was bound");
            rig.Frame();
            var learned = rig.Frame(buttons: GyroButton.R3);
            Assert(learned.Status.LearnedSequence == request && learned.Status.LearnedButton == GyroButton.R3 && learned.ConsumedButtons == GyroButton.R3, "fresh button not learned");
            Assert(rig.Frame(buttons: GyroButton.R3).ConsumedButtons == GyroButton.R3, "captured held button leaked before UI saved its binding");
            rig.Settings = rig.Settings with { ActivationButton = GyroButton.R3 };
            rig.Frame();
            var bound = rig.Frame(buttons: GyroButton.R3 | GyroButton.Cross);
            Assert(bound.ConsumedButtons == GyroButton.R3, "ordinary buttons consumed");
            rig.Settings = rig.Settings with { ForwardShortcutButtons = true };
            Assert(rig.Frame(buttons: GyroButton.R3).ConsumedButtons == GyroButton.R3, "retired pass-through option leaked the tool shortcut");
        });
        check("Every supported USB button can be learned, sanitized and saved without changing old bindings", () =>
        {
            string path = PortableGyroSettingsStore.SettingsPath;
            string? previous = File.Exists(path) ? File.ReadAllText(path) : null;
            try
            {
                Assert((ulong)GyroButton.Cross == 1 && (ulong)GyroButton.PS == 1024 && (ulong)GyroButton.Touchpad == 2048,
                    "existing persisted button values changed");
                foreach (GyroButton button in Enum.GetValues<GyroButton>().Where(value => value != GyroButton.None))
                {
                    using var rig = new Rig();
                    long request = GyroControlState.RequestLearnButton(GyroBindingTarget.Activation);
                    Assert(rig.Frame(buttons: button).Status.PendingBindingTarget == GyroBindingTarget.Activation,
                        $"held {button} bound without a fresh press");
                    rig.Frame();
                    var learned = rig.Frame(buttons: button);
                    Assert(learned.Status.LearnedSequence == request && learned.Status.LearnedButton == button && learned.ConsumedButtons == button,
                        $"fresh {button} could not be learned");
                    Assert(rig.Frame(buttons: button).ConsumedButtons == button, $"held {button} leaked before the UI saved it");
                    var configured = rig.Settings with { ActivationButton = button };
                    Assert(configured.Sanitize().ActivationButton == button, $"sanitize rejected {button}");
                    Assert(PortableGyroSettingsStore.TrySave(configured, out _), $"could not save {button}");
                    rig.Settings = PortableGyroSettingsStore.Load();
                    Assert(rig.Settings.ActivationButton == button, $"saved {button} did not round-trip");
                    Assert(rig.Frame(buttons: button).ConsumedButtons == button, $"saved {button} leaked while held");
                    GyroButton releasedConsumption = button is GyroButton.L2 or GyroButton.R2 ? button : GyroButton.None;
                    Assert(rig.Frame().ConsumedButtons == releasedConsumption, $"released {button} consumption was incorrect");
                    Assert(rig.Frame(buttons: button).ConsumedButtons == button, $"saved {button} did not consume its next press");
                }
                Assert((GyroSteeringSettings.Default with { ActivationButton = GyroButton.DpadUp | GyroButton.DpadRight }).Sanitize().ActivationButton == GyroButton.None,
                    "a diagonal was persisted as a multi-button binding");
                Assert((GyroSteeringSettings.Default with { ActivationButton = (GyroButton)(1UL << 63) }).Sanitize().ActivationButton == GyroButton.None,
                    "an unknown USB flag became a valid binding");
                File.WriteAllText(path, "{\"Enabled\":true,\"ActivationButton\":\"R3\",\"RecenterButton\":\"L3\",\"ForwardShortcutButtons\":true}");
                var legacy = PortableGyroSettingsStore.Load();
                Assert(legacy.Enabled && legacy.ActivationButton == GyroButton.R3 && legacy.RecenterButton == GyroButton.None && !legacy.ForwardShortcutButtons,
                    "adding buttons broke an older portable configuration");
            }
            finally { if (previous is not null) File.WriteAllText(path, previous); else File.Delete(path); }
        });
        check("Learning and holding either trigger suppresses its analog game channel until release", () =>
        {
            foreach (GyroButton button in new[] { GyroButton.L2, GyroButton.R2 })
            {
                using var rig = new Rig();
                long request = GyroControlState.RequestLearnButton(GyroBindingTarget.Activation);
                rig.Frame(); // Arm learning with all physical buttons released.
                byte[] report = new byte[64]; report[0] = 1; report[8] = 8;
                report[5] = 201; report[6] = 79;
                report[9] = button == GyroButton.L2 ? (byte)0x04 : (byte)0x08;
                Assert(DualSenseInputState.TryParseUsb(report, out var pressed), "trigger report rejected");
                for (int held = 0; held < 4; held++)
                {
                    var result = rig.Frame(buttons: pressed.MotionButtons);
                    Assert(result.Status.LearnedSequence == request && result.Status.LearnedButton == button && result.ConsumedButtons == button,
                        $"learning {button} was not consumed throughout its press");
                    Assert(pressed.LeftTriggerWithout(result.ConsumedButtons) == (button == GyroButton.L2 ? 0 : pressed.LeftTrigger),
                        $"learning {button} leaked or changed left analog travel");
                    Assert(pressed.RightTriggerWithout(result.ConsumedButtons) == (button == GyroButton.R2 ? 0 : pressed.RightTrigger),
                        $"learning {button} leaked or changed right analog travel");
                }
                rig.Settings = rig.Settings with { ActivationButton = button };
                Assert(rig.Frame(buttons: pressed.MotionButtons).ConsumedButtons == button, "saved trigger was forwarded before release");
                report[9] = 0; report[5] = report[6] = 0;
                Assert(DualSenseInputState.TryParseUsb(report, out var released), "trigger release rejected");
                Assert(rig.Frame(buttons: released.MotionButtons).ConsumedButtons == button, "bound trigger channel was not reserved throughout its travel");
                var next = rig.Frame(buttons: pressed.MotionButtons);
                Assert(next.ConsumedButtons == button && pressed.LeftTriggerWithout(next.ConsumedButtons) == (button == GyroButton.L2 ? 0 : 201)
                    && pressed.RightTriggerWithout(next.ConsumedButtons) == (button == GyroButton.R2 ? 0 : 79),
                    "bound trigger leaked its next analog press");
            }
        });
        check("Bound triggers suppress analog-only travel but Default, disabled and unbound settings preserve it", () =>
        {
            foreach (GyroButton button in new[] { GyroButton.L2, GyroButton.R2 })
            {
                using var rig = new Rig();
                byte[] report = new byte[64]; report[0] = 1; report[8] = 8;
                report[5] = 201; report[6] = 79; // Deliberately no digital trigger press flags.
                Assert(DualSenseInputState.TryParseUsb(report, out var analogOnly) && analogOnly.MotionButtons == GyroButton.None,
                    "analog-only report became a digital press");
                rig.Settings = rig.Settings with { ActivationButton = button };
                var owned = rig.Frame(.17f, buttons: analogOnly.MotionButtons);
                Assert(owned.ConsumedButtons == button, "bound trigger leaked travel outside its digital press");
                Assert(analogOnly.LeftTriggerWithout(owned.ConsumedButtons) == (button == GyroButton.L2 ? 0 : 201)
                    && analogOnly.RightTriggerWithout(owned.ConsumedButtons) == (button == GyroButton.R2 ? 0 : 79),
                    "bound analog-only travel reached the game or changed the other trigger");
                Close(owned.OutputX, .17f);
                rig.Custom = false;
                var passthrough = rig.Frame(.17f, buttons: analogOnly.MotionButtons);
                Assert(passthrough.ConsumedButtons == GyroButton.None && analogOnly.LeftTriggerWithout(passthrough.ConsumedButtons) == 201
                    && analogOnly.RightTriggerWithout(passthrough.ConsumedButtons) == 79, "Default consumed a saved trigger binding");
                Close(passthrough.OutputX, .17f);
                rig.Custom = true; rig.Settings = rig.Settings with { Enabled = false };
                passthrough = rig.Frame(.17f, buttons: analogOnly.MotionButtons);
                Assert(passthrough.ConsumedButtons == GyroButton.None && analogOnly.LeftTriggerWithout(passthrough.ConsumedButtons) == 201
                    && analogOnly.RightTriggerWithout(passthrough.ConsumedButtons) == 79, "disabled gyro consumed analog trigger travel");
                rig.Settings = rig.Settings with { Enabled = true, ActivationButton = GyroButton.None };
                passthrough = rig.Frame(.17f, buttons: analogOnly.MotionButtons);
                Assert(passthrough.ConsumedButtons == GyroButton.None, "unbound trigger remained reserved");
            }
        });
        check("Retired recenter and pass-through settings cannot reactivate from an older configuration", () =>
        {
            using var rig = new Rig(); rig.Prepare(); rig.Fusion.ClockwiseDegrees = 45;
            rig.Settings = rig.Settings with { RecenterButton = GyroButton.R3, ActivationButton = GyroButton.L3, ForwardShortcutButtons = true };
            var oldRecenter = rig.Frame(buttons: GyroButton.R3 | GyroButton.Cross);
            Assert(oldRecenter.ConsumedButtons == GyroButton.None, "retired recenter still consumed a game button");
            Close(oldRecenter.Status.AngleDegrees, 45);
            rig.Frame();
            var activation = rig.Frame(buttons: GyroButton.L3 | GyroButton.Cross);
            Assert(activation.ConsumedButtons == GyroButton.L3 && activation.Status.State == GyroRuntimeState.Suspended, "active shortcut was forwarded or disabled");
            Close(activation.Status.AngleDegrees, 45);
        });
        check("Malformed and null persisted gyro bias fall back without stopping controller input", () =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, "ACCDualSenseFeedback.gyro-calibration.json");
            string? previous = File.Exists(path) ? File.ReadAllText(path) : null;
            try
            {
                string identity = "synthetic-corrupt-calibration";
                File.WriteAllText(path, "{\"" + PortableGyroSettingsStore.AnonymousDeviceIdentity(identity) + "\":null}");
                Assert(PortableGyroSettingsStore.LoadBias(identity) is null, "null bias accepted");
                File.WriteAllText(path, "{invalid");
                Assert(PortableGyroSettingsStore.LoadBias(identity) is null, "corrupt bias accepted");
            }
            finally { if (previous is not null) File.WriteAllText(path, previous); else File.Delete(path); }
        });
        check("Background gyro bias writes preserve FIFO order and separate physical devices", () =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, "ACCDualSenseFeedback.gyro-calibration.json");
            string? previous = File.Exists(path) ? File.ReadAllText(path) : null;
            try
            {
                var first = PortableGyroSettingsStore.SaveBiasAsync("synthetic-device-a", new(.1f, .2f, .3f));
                var other = PortableGyroSettingsStore.SaveBiasAsync("synthetic-device-b", new(.4f, .5f, .6f));
                var latest = PortableGyroSettingsStore.SaveBiasAsync("synthetic-device-a", new(.7f, .8f, .9f));
                Task.WaitAll(first, other, latest); // Tests may wait for disk; the production input thread never waits.
                Assert(first.Result.Error is null && other.Result.Error is null && latest.Result.Error is null, "background bias save failed");
                var a = PortableGyroSettingsStore.LoadBias("synthetic-device-a");
                var b = PortableGyroSettingsStore.LoadBias("synthetic-device-b");
                Assert(a.HasValue && b.HasValue, "one device replaced another");
                Close(a!.Value.X, .7f); Close(b!.Value.X, .4f);
            }
            finally { if (previous is not null) File.WriteAllText(path, previous); else File.Delete(path); }
        });
        check("Pinned native fusion runs synthetic replay without automatic gyro bias learning", () =>
        {
            using var fusion = new NativeGyroFusion();
            Assert(fusion.Available, fusion.Error ?? "native library unavailable");
            fusion.SetBias(Vector3.Zero);
            for (int i = 0; i < 20; i++) Assert(fusion.Process(Vector3.Zero, Vector3.UnitY, .004f, out _), "native init invalid");
            GyroFusionPose pose = default;
            for (int i = 1; i <= 250; i++)
            {
                float angle = -.5f * MathF.PI * i / 250;
                var accel = Vector3.Transform(Vector3.UnitY, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -angle));
                Assert(fusion.Process(new(0, 0, -90), accel, .004f, out pose), "native rotation invalid");
            }
            float roll = -2 * MathF.Atan2(pose.Orientation.Z, pose.Orientation.W) * 180 / MathF.PI;
            Close(roll, 90, 1.5f);
            for (int i = 0; i < 10_000; i++) Assert(fusion.Process(new(.3f, 0, 0), Vector3.UnitY, .004f, out pose), "native long replay invalid");
            Close(pose.CalibratedGyro.X, .3f, .00001f); // No stillness/fusion zero-bias adaptation while driving.
        });
    }

    private sealed class FakeFusion : IGyroFusion
    {
        public bool Available { get; set; } = true;
        public string? Error => null;
        public float ClockwiseDegrees;
        public Quaternion BaseOrientation = Quaternion.Identity;
        public Vector3 Gravity = new(0, -1, 0), Bias;
        public int Processed, BiasChanges;
        public void Reset() { }
        public void SetBias(Vector3 bias) { Bias = bias; BiasChanges++; }
        public bool Process(Vector3 gyro, Vector3 accel, float dt, out GyroFusionPose pose)
        {
            Processed++;
            pose = new(BaseOrientation * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -ClockwiseDegrees * MathF.PI / 180), Gravity, gyro - Bias);
            return Available;
        }
        public void Dispose() { }
    }
    private sealed class Rig : IDisposable
    {
        public GyroSteeringSettings Settings = GyroSteeringSettings.Default with { Enabled = true, Smoothing = GyroSmoothing.Off };
        public readonly FakeFusion Fusion = new();
        public readonly GyroSteeringProcessor Processor;
        public bool Custom = true, Driving, Suspended, Foreground = true;
        public uint Timestamp;
        public double Host = 10;
        public Rig() { while (GyroControlState.TryDequeue(out _)) { } Processor = new(Fusion, () => Settings); }
        public GyroProcessResult Frame(float physicalX = 0, GyroButton buttons = GyroButton.None, Vector3? gyro = null, bool valid = true, bool advanceTimestamp = true)
        {
            if (advanceTimestamp) Timestamp = unchecked(Timestamp + 30_000);
            Host += .01;
            return Processor.Process(new(gyro ?? Vector3.Zero, Vector3.UnitY, Timestamp, valid, DeviceIdentity: "synthetic-only"),
                physicalX, new(Custom, Driving, Suspended, Foreground, true, buttons, Host));
        }
        public GyroProcessResult Settle(float physicalX = 0, GyroButton buttons = GyroButton.None)
        {
            GyroProcessResult result = default;
            for (int i = 0; i < 12; i++) result = Frame(physicalX, buttons);
            return result;
        }
        public void Prepare()
        {
            Frame(); GyroControlState.RequestCalibration();
            for (int i = 0; i < 300; i++) Frame();
            GyroControlState.RequestRecenter();
            for (int i = 0; i < 10; i++) Frame();
            Assert(GyroControlState.Current.Ready, "synthetic rig did not prepare");
        }
        public void Dispose() => Processor.Dispose();
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Close(float actual, float expected, float tolerance = .0001f)
        => Assert(Math.Abs(actual - expected) <= tolerance, $"expected {expected}, received {actual}");
}
