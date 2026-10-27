using System.Numerics;
using ACCDualSenseFeedback.Gyro;

internal static class GyroPauseReasonChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("Always pause reasons distinguish game conditions while automatic resume preserves neutral", () =>
        {
            using var rig = new Rig(); rig.Prepare();
            rig.Fusion.ClockwiseDegrees = 30;
            long neutral = rig.Settle().Status.RecenterSequence;
            rig.Suspended = true; rig.Foreground = false;
            var waiting = rig.Settle(.17f);
            State(waiting, GyroRuntimeState.Suspended, GyroPauseReason.NotDriving | GyroPauseReason.GameNotForeground);
            Close(waiting.OutputX, .17f);
            Assert(waiting.Status.Ready && !waiting.Status.NeedsRecenter, "environment lost center");
            rig.Suspended = false;
            State(rig.Frame(), GyroRuntimeState.Suspended, GyroPauseReason.GameNotForeground);
            rig.Foreground = true;
            var resumed = rig.Settle();
            State(resumed, GyroRuntimeState.Active, GyroPauseReason.None);
            Close(resumed.OutputX, 29f / 89);
            Assert(resumed.Status.RecenterSequence == neutral, "auto resume replaced center");
        });
        check("Always manual stop remains separately visible through game condition changes", () =>
        {
            using var rig = new Rig(); rig.Prepare();
            rig.Settings = rig.Settings with { ActivationButton = GyroButton.R3 };
            State(rig.Frame(buttons: GyroButton.R3), GyroRuntimeState.Suspended, GyroPauseReason.ActivationOff);
            rig.Frame(); rig.Suspended = true; rig.Foreground = false;
            State(rig.Settle(.2f), GyroRuntimeState.Suspended,
                GyroPauseReason.NotDriving | GyroPauseReason.GameNotForeground | GyroPauseReason.ActivationOff);
            rig.Suspended = false; rig.Foreground = true;
            var stopped = rig.Settle(.2f);
            State(stopped, GyroRuntimeState.Suspended, GyroPauseReason.ActivationOff);
            Close(stopped.OutputX, .2f);
            GyroControlState.RequestResume();
            State(rig.Frame(), GyroRuntimeState.Active, GyroPauseReason.None);
        });
        check("UI resume outside ACC clears manual intent without bypassing game conditions", () =>
        {
            using var rig = new Rig(); rig.Prepare();
            rig.Settings = rig.Settings with { ActivationButton = GyroButton.R3 };
            rig.Frame(buttons: GyroButton.R3); rig.Frame();
            rig.Suspended = true; rig.Foreground = false;
            rig.Settle();
            GyroControlState.RequestResume();
            var accepted = rig.Settle(.13f);
            State(accepted, GyroRuntimeState.Suspended, GyroPauseReason.NotDriving | GyroPauseReason.GameNotForeground);
            Close(accepted.OutputX, .13f);
            rig.Suspended = false; rig.Foreground = true;
            State(rig.Settle(), GyroRuntimeState.Active, GyroPauseReason.None);
        });
        check("Hold pause reasons distinguish an unpressed binding from explicit resume after suspension", () =>
        {
            using var rig = new Rig(GyroActivationMode.Hold); rig.Prepare();
            State(rig.Frame(), GyroRuntimeState.Suspended, GyroPauseReason.HoldNotPressed);
            State(rig.Settle(buttons: GyroButton.R3), GyroRuntimeState.Active, GyroPauseReason.None);
            rig.Suspended = true;
            State(rig.Settle(buttons: GyroButton.R3), GyroRuntimeState.Suspended,
                GyroPauseReason.NotDriving | GyroPauseReason.ResumeRequired);
            rig.Suspended = false;
            State(rig.Frame(buttons: GyroButton.R3), GyroRuntimeState.Suspended, GyroPauseReason.ResumeRequired);
            State(rig.Frame(), GyroRuntimeState.Suspended, GyroPauseReason.ResumeRequired | GyroPauseReason.HoldNotPressed);
            State(rig.Frame(buttons: GyroButton.R3), GyroRuntimeState.Active, GyroPauseReason.None);
        });
        check("Toggle pause reasons keep activation and suspension resume requirements independent", () =>
        {
            using var rig = new Rig(GyroActivationMode.Toggle); rig.Prepare();
            State(rig.Frame(), GyroRuntimeState.Suspended, GyroPauseReason.ActivationOff);
            State(rig.Settle(buttons: GyroButton.R3), GyroRuntimeState.Active, GyroPauseReason.None);
            rig.Foreground = false;
            State(rig.Settle(), GyroRuntimeState.Suspended, GyroPauseReason.GameNotForeground | GyroPauseReason.ResumeRequired);
            GyroControlState.RequestResume();
            State(rig.Frame(), GyroRuntimeState.Suspended, GyroPauseReason.GameNotForeground);
            rig.Foreground = true;
            State(rig.Settle(), GyroRuntimeState.Active, GyroPauseReason.None);
            State(rig.Frame(buttons: GyroButton.R3), GyroRuntimeState.Suspended, GyroPauseReason.ActivationOff);
        });
        check("Non-suspended priorities never publish misleading pause reasons", () =>
        {
            using var rig = new Rig();
            State(rig.Frame(), GyroRuntimeState.WaitingForCalibration, GyroPauseReason.None);
            GyroControlState.RequestCalibration();
            State(rig.Frame(), GyroRuntimeState.Calibrating, GyroPauseReason.None);
            for (int i = 0; i < 300; i++) rig.Frame();
            State(rig.Frame(), GyroRuntimeState.WaitingForCenter, GyroPauseReason.None);
            GyroControlState.RequestRecenter(); rig.Frame();
            rig.Suspended = true; rig.Foreground = false;
            rig.Settings = rig.Settings with { Enabled = false };
            var disabled = rig.Settle(.25f);
            State(disabled, GyroRuntimeState.Disabled, GyroPauseReason.None);
            Close(disabled.OutputX, .25f);
            rig.Settings = rig.Settings with { Enabled = true };
            var invalid = rig.Frame(.25f, valid: false);
            State(invalid, GyroRuntimeState.SensorInvalid, GyroPauseReason.None);
            Close(invalid.OutputX, .25f);
        });
        check("Missing binding keeps passive activation reasons available for higher-priority UI guidance", () =>
        {
            foreach (GyroActivationMode mode in new[] { GyroActivationMode.Hold, GyroActivationMode.Toggle })
            {
                using var rig = new Rig(mode); rig.Prepare();
                rig.Settings = rig.Settings with { ActivationButton = GyroButton.None };
                State(rig.Frame(), GyroRuntimeState.Suspended,
                    mode == GyroActivationMode.Hold ? GyroPauseReason.HoldNotPressed : GyroPauseReason.ActivationOff);
            }
        });
    }

    private sealed class FakeFusion : IGyroFusion
    {
        public bool Available => true;
        public string? Error => null;
        public float ClockwiseDegrees;
        public void Reset() { }
        public void SetBias(Vector3 bias) { }
        public bool Process(Vector3 gyro, Vector3 acceleration, float dt, out GyroFusionPose pose)
        {
            pose = new(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -ClockwiseDegrees * MathF.PI / 180),
                new(0, -1, 0), gyro);
            return true;
        }
        public void Dispose() { }
    }
    private sealed class Rig : IDisposable
    {
        public GyroSteeringSettings Settings;
        public readonly FakeFusion Fusion = new();
        private readonly GyroSteeringProcessor _processor;
        public bool Driving, Suspended, Foreground = true;
        private uint _timestamp;
        private double _host = 10;
        public Rig(GyroActivationMode mode = GyroActivationMode.Always)
        {
            while (GyroControlState.TryDequeue(out _)) { }
            Settings = GyroSteeringSettings.Default with
            { Enabled = true, Smoothing = GyroSmoothing.Off, ActivationMode = mode, ActivationButton = mode == GyroActivationMode.Always ? GyroButton.None : GyroButton.R3 };
            _processor = new(Fusion, () => Settings);
        }
        public GyroProcessResult Frame(float physical = 0, GyroButton buttons = GyroButton.None, bool valid = true)
        {
            _timestamp += 30_000; _host += .01;
            return _processor.Process(new(Vector3.Zero, Vector3.UnitY, _timestamp, valid, DeviceIdentity: "synthetic-pause-reasons"),
                physical, new(true, Driving, Suspended, Foreground, true, buttons, _host));
        }
        public GyroProcessResult Settle(float physical = 0, GyroButton buttons = GyroButton.None)
        {
            GyroProcessResult result = default;
            for (int i = 0; i < 12; i++) result = Frame(physical, buttons);
            return result;
        }
        public void Prepare()
        {
            Frame(); GyroControlState.RequestCalibration();
            for (int i = 0; i < 300; i++) Frame();
            GyroControlState.RequestRecenter(); Frame();
            Assert(GyroControlState.Current.Ready, "synthetic calibration failed");
            Driving = true;
            Settle();
        }
        public void Dispose() => _processor.Dispose();
    }
    private static void State(GyroProcessResult result, GyroRuntimeState state, GyroPauseReason reasons)
    {
        Assert(result.Status.State == state, $"expected {state}, got {result.Status.State}");
        Assert(result.Status.PauseReasons == reasons, $"expected {reasons}, got {result.Status.PauseReasons}");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Close(float actual, float expected)
        => Assert(Math.Abs(actual - expected) <= .0001f, $"expected {expected}, got {actual}");
}
