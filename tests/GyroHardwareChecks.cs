using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using ACCDualSenseFeedback.Gyro;
using ACCDualSenseFeedback.Hardware.DualSense;
using ACCDualSenseFeedback.Runtime;

internal static class GyroHardwareChecks
{
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Put(byte[] data, int offset, short value) => BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset), value);
    private static byte[] Calibration()
    {
        var data = new byte[41]; data[0] = 5;
        Put(data, 1, 20); Put(data, 3, -30); Put(data, 5, 40);
        foreach (int offset in new[] { 7, 11, 15 })
        { Put(data, offset, 1440); Put(data, offset + 2, -1440); }
        Put(data, 19, 90); Put(data, 21, 90);
        foreach (int offset in new[] { 23, 27, 31 })
        { Put(data, offset, 8292); Put(data, offset + 2, -8092); }
        return data;
    }
    private static byte[] Report()
    {
        var report = new byte[64]; report[0] = 1;
        report[1] = 201; report[2] = 67; report[3] = 128; report[4] = 200;
        report[5] = 177; report[6] = 42; report[8] = 0xF3; report[9] = 0xF3; report[10] = 3;
        Put(report, 16, 1620); Put(report, 18, -830); Put(report, 20, 360);
        Put(report, 22, 100); Put(report, 24, 8292); Put(report, 26, 100);
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(28), 0xFFFFF120);
        return report;
    }
    private static long At(double seconds) => (long)((seconds + 1) * Stopwatch.Frequency);
    private static readonly (int Offset, byte Mask, GyroButton Button, ushort VirtualMask)[] DigitalButtonCases =
    [
        (8, 0x10, GyroButton.Square, 0x4000), (8, 0x20, GyroButton.Cross, 0x1000),
        (8, 0x40, GyroButton.Circle, 0x2000), (8, 0x80, GyroButton.Triangle, 0x8000),
        (9, 0x01, GyroButton.L1, 0x0100), (9, 0x02, GyroButton.R1, 0x0200),
        (9, 0x04, GyroButton.L2, 0), (9, 0x08, GyroButton.R2, 0),
        (9, 0x10, GyroButton.Share, 0x0020), (9, 0x20, GyroButton.Options, 0x0010),
        (9, 0x40, GyroButton.L3, 0x0040), (9, 0x80, GyroButton.R3, 0x0080),
        (10, 0x01, GyroButton.PS, 0x0400), (10, 0x02, GyroButton.Touchpad, 0),
        (10, 0x04, GyroButton.Mute, 0), (10, 0x10, GyroButton.EdgeFnLeft, 0),
        (10, 0x20, GyroButton.EdgeFnRight, 0), (10, 0x40, GyroButton.EdgePaddleLeft, 0),
        (10, 0x80, GyroButton.EdgePaddleRight, 0)
    ];
    private static readonly (GyroButton Button, ushort VirtualMask)[] DpadCases =
    [
        (GyroButton.DpadUp, 0x0001), (GyroButton.DpadUp | GyroButton.DpadRight, 0x0009),
        (GyroButton.DpadRight, 0x0008), (GyroButton.DpadDown | GyroButton.DpadRight, 0x000A),
        (GyroButton.DpadDown, 0x0002), (GyroButton.DpadDown | GyroButton.DpadLeft, 0x0006),
        (GyroButton.DpadLeft, 0x0004), (GyroButton.DpadUp | GyroButton.DpadLeft, 0x0005)
    ];
    private static byte[] ButtonReport()
    {
        byte[] report = Report();
        report[8] = 8; report[9] = report[10] = 0;
        return report;
    }

    public static void Run(Action<string, Action> check)
    {
        check("Stopped input clears calibration and overwrites a late processor publication", () =>
        {
            GyroRuntimeStatus original = GyroControlState.Current;
            try
            {
                var active = new GyroRuntimeStatus
                {
                    State = GyroRuntimeState.Calibrating, InputValid = true, ImuValid = true,
                    Ready = true, NeedsRecenter = false, IsCalibrating = true,
                    PhysicalX = .4f, GyroX = .8f, OutputX = .7f, AngleDegrees = 60,
                    SteeringPreview = new(true, 60, .9f, .8f),
                    DeviceDeltaSeconds = .004, CalibrationProgress = .8f,
                    InputSource = GyroInputSource.Gyro, PendingBindingTarget = GyroBindingTarget.Activation
                };
                foreach (bool failed in new[] { false, true })
                {
                    GyroControlState.Publish(active);
                    VirtualXboxBridge.PublishStoppedGyro(failed);
                    // The in-flight Process may publish once after the watchdog;
                    // the input-loop finally / Dispose must overwrite it again.
                    GyroControlState.Publish(active);
                    VirtualXboxBridge.PublishStoppedGyro(failed);
                    var stopped = GyroControlState.Current;
                    Assert(!stopped.InputValid && !stopped.ImuValid && !stopped.Ready && stopped.NeedsRecenter,
                        "stopped input retained valid posture");
                    Assert(!stopped.IsCalibrating && !stopped.SuppressFeedback && stopped.CalibrationProgress == 0,
                        "stopped calibration muted the next runtime");
                    Assert(stopped.PhysicalX == 0 && stopped.GyroX == 0 && stopped.OutputX == 0 &&
                        stopped.AngleDegrees == 0 && stopped.DeviceDeltaSeconds == 0,
                        "stopped snapshot retained a turn");
                    Assert(stopped.SteeringPreview == default,
                        "stopped snapshot retained a valid steering test preview");
                    Assert(stopped.PendingBindingTarget == GyroBindingTarget.None &&
                        stopped.InputSource == GyroInputSource.PhysicalStick, "stopped snapshot retained input ownership");
                    Assert(stopped.State == (failed ? GyroRuntimeState.SensorInvalid : GyroRuntimeState.Disabled),
                        "wrong stopped reason");
                }
            }
            finally { GyroControlState.Publish(original); }
        });
        check("Factory IMU calibration applies biases and physical units at exact USB offsets", () =>
        {
            Assert(DualSenseImuCalibration.TryParse(Calibration(), out var calibration), "valid calibration rejected");
            Assert(DualSenseImuCalibration.TryParseMotion(Report(), calibration, "anonymous-device", out var sample), "valid IMU rejected");
            Assert(Vector3.Distance(sample.AngularVelocityDegreesPerSecond, new Vector3(100, -50, 20)) < .001f, "wrong gyro units/offset/bias");
            Assert(Vector3.Distance(sample.AccelerationG, Vector3.UnitY) < .001f, "wrong accel units/offset/bias");
            Assert(sample.DeviceTimestamp == 0xFFFFF120 && sample.DeviceIdentity == "anonymous-device", "timestamp/identity changed");
            Assert(sample.RawGyro.X == 1620 && sample.RawAcceleration.Y == 8292, "raw diagnostics lost");
        });
        check("Missing, malformed and saturated IMU data only invalidates motion", () =>
        {
            byte[] bad = Calibration(); Put(bad, 7, -1440);
            Assert(!DualSenseImuCalibration.TryParse(bad, out _), "zero gyro calibration range accepted");
            bad = Calibration(); Put(bad, 1, 2000);
            Assert(!DualSenseImuCalibration.TryParse(bad, out _), "bad factory bias accepted");
            Assert(!DualSenseImuCalibration.TryParse(new byte[41], out _), "zero report accepted");
            byte[] input = Report();
            Assert(DualSenseInputState.TryParseUsb(input, out var before), "input parse failed");
            Assert(!DualSenseImuCalibration.TryParseMotion(input, null, "a", out var missing) && !missing.IsValid, "factory failure did not disable IMU");
            DualSenseImuCalibration.TryParse(Calibration(), out var calibration);
            Put(input, 16, short.MaxValue);
            Assert(!DualSenseImuCalibration.TryParseMotion(input, calibration, "a", out var saturated) && !saturated.IsValid, "saturated IMU accepted");
            Assert(DualSenseInputState.TryParseUsb(input, out var after) && before == after, "motion failure changed raw controls");
            Assert(!DualSenseImuCalibration.TryParseMotion(input.AsSpan(0, 31), calibration, "a", out _), "short motion report accepted");
        });
        check("Shortcut consumption removes only explicitly selected virtual buttons", () =>
        {
            DualSenseInputState.TryParseUsb(Report(), out var state);
            Assert((state.MotionButtons & GyroButton.Touchpad) != 0, "touchpad button unavailable for learning");
            Assert(state.ButtonsWithout(GyroButton.None) == state.Buttons, "unbound controls changed");
            ushort consumed = state.ButtonsWithout(GyroButton.Cross | GyroButton.Options);
            Assert(consumed == (ushort)(state.Buttons & ~(0x1000 | 0x0010)), "wrong button mask consumed");
            Assert(state.ButtonsWithout(GyroButton.Touchpad) == state.Buttons, "touchpad consumed an unrelated XInput button");
        });
        check("Every USB face, shoulder, trigger, special and Edge button has an independent learning flag", () =>
        {
            foreach (var (offset, mask, button, virtualMask) in DigitalButtonCases)
            {
                byte[] report = ButtonReport();
                report[offset] |= mask;
                // The digital trigger flag must work even at zero analog travel.
                report[5] = report[6] = 0;
                Assert(DualSenseInputState.TryParseUsb(report, out var parsed, isEdge: true), $"{button} report rejected");
                Assert(parsed.MotionButtons == button, $"{button} mapped to {parsed.MotionButtons}");
                Assert(parsed.Buttons == virtualMask, $"{button} changed unrelated virtual buttons");
            }
            byte[] analogOnly = ButtonReport();
            analogOnly[5] = 255; analogOnly[6] = 113;
            Assert(DualSenseInputState.TryParseUsb(analogOnly, out var raw) && raw.MotionButtons == GyroButton.None,
                "analog travel invented a trigger-button threshold");
            Assert(raw.LeftTrigger == 255 && raw.RightTrigger == 113, "raw trigger travel changed");
            byte[] reserved = ButtonReport(); reserved[10] = 0x08;
            Assert(DualSenseInputState.TryParseUsb(reserved, out raw) && raw.MotionButtons == GyroButton.None,
                "reserved special bit became a bindable button");
            reserved[10] = 0xF8;
            Assert(DualSenseInputState.TryParseUsb(reserved, out raw) && raw.MotionButtons == GyroButton.None,
                "ordinary DualSense reserved bits became Edge learning buttons");
        });
        check("USB Dpad hats preserve all diagonals and consuming one direction keeps its neighbor", () =>
        {
            for (byte hat = 0; hat < 16; hat++)
            {
                byte[] report = ButtonReport(); report[8] = hat;
                Assert(DualSenseInputState.TryParseUsb(report, out var parsed), $"hat {hat} report rejected");
                var expected = hat < DpadCases.Length ? DpadCases[hat] : (GyroButton.None, (ushort)0);
                Assert(parsed.MotionButtons == expected.Item1 && parsed.Buttons == expected.Item2,
                    $"hat {hat} lost its cardinal/diagonal directions");
                foreach (var (button, mask) in new[] { (GyroButton.DpadUp, 1), (GyroButton.DpadDown, 2), (GyroButton.DpadLeft, 4), (GyroButton.DpadRight, 8) })
                    Assert(parsed.ButtonsWithout(button) == (ushort)(expected.Item2 & ~mask),
                        $"consuming {button} changed another hat direction");
            }
        });
        check("Consuming any button changes only its virtual button or matching analog trigger channel", () =>
        {
            byte[] report = Report(); report[8] = 0xF1; report[9] = 0xFF; report[10] = 0xF7;
            Assert(DualSenseInputState.TryParseUsb(report, out var parsed, isEdge: true), "all-buttons report rejected");
            var original = parsed;
            foreach (GyroButton button in Enum.GetValues<GyroButton>())
            {
                ushort mask = button switch
                {
                    GyroButton.DpadUp => 1, GyroButton.DpadDown => 2,
                    GyroButton.DpadLeft => 4, GyroButton.DpadRight => 8,
                    _ => DigitalButtonCases.FirstOrDefault(item => item.Button == button).VirtualMask
                };
                Assert(parsed.ButtonsWithout(button) == (ushort)(original.Buttons & ~mask), $"{button} consumed unrelated buttons");
                Assert(parsed.LeftTriggerWithout(button) == (button == GyroButton.L2 ? 0 : original.LeftTrigger), $"{button} changed left trigger incorrectly");
                Assert(parsed.RightTriggerWithout(button) == (button == GyroButton.R2 ? 0 : original.RightTrigger), $"{button} changed right trigger incorrectly");
                Assert(parsed == original, $"{button} changed raw sticks or trigger state");
            }
            Assert(parsed.LeftTriggerWithout(GyroButton.L2 | GyroButton.R2) == 0 && parsed.RightTriggerWithout(GyroButton.L2 | GyroButton.R2) == 0,
                "simultaneous trigger shortcuts leaked analog travel");
        });
        check("Physical steering round trips every XInput axis bit and gyro saturates safely", () =>
        {
            for (int value = short.MinValue; value <= short.MaxValue; value++)
                Assert(VirtualXboxBridge.QuantizeAxis(VirtualXboxBridge.NormalizeAxis((short)value)) == value, $"changed physical steering {value}");
            Assert(VirtualXboxBridge.QuantizeAxis(float.NaN) == 0, "NaN produced a steering spike");
            Assert(VirtualXboxBridge.QuantizeAxis(-2) == short.MinValue && VirtualXboxBridge.QuantizeAxis(2) == short.MaxValue, "unsafe full-lock endpoints");
        });
        check("Gyro ACC context requires verified live graphics and rejects pause, replay, off and stale", () =>
        {
            string name = "Local\\acc-gyro-test-" + Guid.NewGuid().ToString("N");
            using var map = MemoryMappedFile.CreateNew(name, 8);
            using var view = map.CreateViewAccessor();
            using var context = new AccGyroContextReader([name]);
            view.Write(0, 1); view.Write(4, 2); context.Refresh(At(0));
            Assert(!context.IsLive(At(0)), "unverified first graphics packet was live");
            view.Write(0, 2); context.Refresh(At(.01));
            Assert(context.IsLive(At(.01)), "verified live packet was rejected");
            foreach (int status in new[] { 3, 1, 0, 99 })
            {
                view.Write(4, status); context.Refresh(At(.02));
                Assert(!context.IsLive(At(.02)), $"status {status} was live");
            }
            view.Write(4, 2); context.Refresh(At(.03));
            Assert(!context.IsLive(At(.7)), "stale live graphics allowed gyro");
            context.Disconnect(); Assert(!context.IsLive(At(.8)), "disconnect retained driving state");
        });
    }
}
