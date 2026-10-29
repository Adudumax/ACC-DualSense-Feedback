using System.IO;
using System.Reflection;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Hardware.DualSense;
using ACCDualSenseFeedback.Lighting;
using Microsoft.Win32.SafeHandles;

internal static class DualSenseOutputChecks
{
    // Frozen v1.2.0 protocol bytes, before transport padding is applied.
    private static readonly byte[] UsbGolden = Convert.FromHexString(
        "020F553BA6000000000000260102030405060708090A210B0C0D0E0F101112131400000000000006000002011F172F51");
    private static readonly byte[] BluetoothGolden = Convert.FromHexString(
        "31020F553BA6000000000000260102030405060708090A210B0C0D0E0F101112131400000000000006000002011F172F510000000000000000000000000000000000000000000000000058209CA7");
    private static readonly byte[] UsbOffGolden = Convert.FromHexString(
        "020F55000000000000000005000000000000000000000500000000000000000000000000000000060000000000000000");
    private static readonly FeedbackFrame ActiveFrame = new(0xA6, 0x3B,
        new TriggerEffect(0x21, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20),
        new TriggerEffect(0x26, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10));
    private static readonly RpmLightFrame ActiveLights = new(23, 47, 81, 31, 1);

    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static DualSenseDeviceInfo Device(DualSenseTransport transport, ushort outputLength,
        ushort productId = 0x0DF2)
        => new("test-only-never-opened-hid-path", productId, transport, 64, outputLength);

    private static void Same(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected, string message)
        => Assert(actual.SequenceEqual(expected), message);

    private static void Zero(ReadOnlySpan<byte> data, string message)
        => Assert(data.IndexOfAnyExcept((byte)0) < 0, message);

    public static void Run(Action<string, Action> check)
    {
        check("DS5 USB remains exactly the published 48-byte report", () =>
        {
            var device = Device(DualSenseTransport.Usb, 48, 0x0CE6);
            byte[] output = Enumerable.Repeat((byte)0xDA, 64).ToArray();
            Assert(DualSenseReportBuilder.GetOutputReportLength(device) == 48, "DS5 transfer length changed");
            int length = DualSenseReportBuilder.BuildForDevice(ActiveFrame, device, output, ActiveLights, true);
            Assert(length == 48, "DS5 report was enlarged");
            Same(output.AsSpan(0, length), UsbGolden, "published DS5 motor, trigger or light bytes changed");
            Assert(output.AsSpan(length).IndexOfAnyExcept((byte)0xDA) < 0, "wrote past submitted DS5 report");
        });

        check("Edge USB retains all 48 published protocol bytes and zero-pads to its 64-byte descriptor", () =>
        {
            var device = Device(DualSenseTransport.Usb, 64);
            byte[] output = Enumerable.Repeat((byte)0xDA, 64).ToArray();
            Assert(DualSenseReportBuilder.GetOutputReportLength(device) == 64, "Edge descriptor ignored");
            Assert(DualSenseReportBuilder.BuildForDevice(ActiveFrame, device, output, ActiveLights, true) == 64,
                "Edge write would still submit 48 bytes");
            Same(output.AsSpan(0, 48), UsbGolden, "Edge changed the established feedback payload");
            Zero(output.AsSpan(48), "Edge padding contains stale bytes");
        });

        check("Device framing preserves every legacy protocol byte across motors, trigger effects and lights", () =>
        {
            FeedbackFrame[] frames = [
                FeedbackFrame.Off,
                ActiveFrame,
                new(255, 0, TriggerEffect.Rigid(0.35f), TriggerEffect.Rigid(0.8f)),
                new(0, 255, TriggerEffect.Vibration(2, 7, 50), TriggerEffect.Vibration(7, 2, 160)),
                new(63, 197, TriggerEffect.FeedbackZones(0, 1, 2, 3, 4, 5, 6, 7, 8, 0),
                    TriggerEffect.VibrationZones(new byte[] { 8, 0, 1, 0, 2, 0, 3, 0, 4, 0 }, 90))
            ];
            RpmLightFrame[] lightFrames = [RpmLightFrame.Off, ActiveLights, new(255, 0, 128, 15, 0), new(0, 255, 0, 3, 2)];
            DualSenseDeviceInfo[] devices = [
                Device(DualSenseTransport.Usb, 48, 0x0CE6), Device(DualSenseTransport.Usb, 64),
                Device(DualSenseTransport.Bluetooth, 78, 0x0CE6), Device(DualSenseTransport.Bluetooth, 78)
            ];
            foreach (var device in devices)
            foreach (var frame in frames)
            foreach (var lights in lightFrames)
            foreach (bool initialize in new[] { false, true })
            {
                byte[] legacy = new byte[78];
                int contentLength = DualSenseReportBuilder.Build(frame,
                    device.Transport == DualSenseTransport.Bluetooth, legacy, lights, initialize);
                byte[] actual = Enumerable.Repeat((byte)0xDB, 96).ToArray();
                int length = DualSenseReportBuilder.BuildForDevice(frame, device, actual, lights, initialize);
                Same(actual.AsSpan(0, contentLength), legacy.AsSpan(0, contentLength),
                    $"PID {device.ProductId:X4}/{device.Transport}: feedback or lighting changed");
                Zero(actual.AsSpan(contentLength, length - contentLength), "nonzero transport padding");
                Assert(actual.AsSpan(length).IndexOfAnyExcept((byte)0xDB) < 0, "modified unused destination bytes");
            }
        });

        check("Reused Edge buffer clears old active motors, trigger parameters, lights and transport padding", () =>
        {
            var device = Device(DualSenseTransport.Usb, 64);
            byte[] output = Enumerable.Repeat((byte)0xE7, 64).ToArray();
            DualSenseReportBuilder.BuildForDevice(ActiveFrame, device, output, ActiveLights, true);
            output.AsSpan(48).Fill(0xA5); // Poison unused bytes between writes.
            int length = DualSenseReportBuilder.BuildForDevice(FeedbackFrame.Off, device, output);
            Assert(length == 64, "Off used a different write length");
            Same(output.AsSpan(0, 48), UsbOffGolden, "Off failed to clear established fields");
            Zero(output.AsSpan(48), "reused Edge padding retained previous data");
        });

        check("Bluetooth remains the published 78-byte report including its original CRC", () =>
        {
            foreach (ushort productId in new ushort[] { 0x0CE6, 0x0DF2 })
            {
                var device = Device(DualSenseTransport.Bluetooth, 78, productId);
                byte[] output = Enumerable.Repeat((byte)0xE7, 78).ToArray();
                Assert(DualSenseReportBuilder.BuildForDevice(ActiveFrame, device, output, ActiveLights, true) == 78,
                    "Bluetooth transfer length changed");
                Same(output, BluetoothGolden, "Bluetooth protocol or CRC changed");
                Assert(BitConverter.ToUInt32(output, 74) == 0xA79C2058u, "published Bluetooth CRC moved or changed");
            }
        });

        check("Unusual descriptor lengths retain protocol minimums and correctly clear enlarged tails", () =>
        {
            foreach (var transport in new[] { DualSenseTransport.Usb, DualSenseTransport.Bluetooth })
            foreach (ushort descriptorLength in new ushort[] { 0, 1, 47, 48, 64, 78, 96, 512 })
            {
                var device = Device(transport, descriptorLength);
                byte[] legacy = new byte[78];
                int contentLength = DualSenseReportBuilder.Build(ActiveFrame,
                    transport == DualSenseTransport.Bluetooth, legacy, ActiveLights, true);
                int expectedLength = Math.Max(contentLength, descriptorLength);
                byte[] actual = Enumerable.Repeat((byte)0xE7, expectedLength + 7).ToArray();
                Assert(DualSenseReportBuilder.GetOutputReportLength(device) == expectedLength, "descriptor/protocol length mismatch");
                Assert(DualSenseReportBuilder.BuildForDevice(ActiveFrame, device, actual, ActiveLights, true) == expectedLength,
                    "submitted length mismatch");
                Same(actual.AsSpan(0, contentLength), legacy.AsSpan(0, contentLength), "descriptor altered protocol bytes");
                Zero(actual.AsSpan(contentLength, expectedLength - contentLength), "enlarged descriptor tail not cleared");
                Assert(actual.AsSpan(expectedLength).IndexOfAnyExcept((byte)0xE7) < 0, "descriptor cleared past transfer end");
            }
        });

        check("Too-small destination fails before writing a partial device report", () =>
        {
            foreach (var device in new[] { Device(DualSenseTransport.Usb, 48, 0x0CE6),
                         Device(DualSenseTransport.Usb, 64), Device(DualSenseTransport.Bluetooth, 78),
                         Device(DualSenseTransport.Bluetooth, 96) })
            {
                byte[] output = Enumerable.Repeat((byte)0xCC,
                    DualSenseReportBuilder.GetOutputReportLength(device) - 1).ToArray();
                bool rejected = false;
                try { DualSenseReportBuilder.BuildForDevice(ActiveFrame, device, output, ActiveLights, true); }
                catch (ArgumentException error) { rejected = error.ParamName == "destination"; }
                Assert(rejected, "partial report destination was accepted");
                Assert(output.AsSpan().IndexOfAnyExcept((byte)0xCC) < 0, "invalid destination was partially modified");
            }
        });

        check("Real WriteFile submits full DS5, Edge and Bluetooth reports and Dispose sends padded Off", () =>
        {
            // Only ordinary temporary files are opened. These checks never enumerate,
            // open, read or send feedback to any physical controller.
            foreach (var info in new[] { Device(DualSenseTransport.Usb, 48, 0x0CE6),
                         Device(DualSenseTransport.Usb, 64), Device(DualSenseTransport.Bluetooth, 78),
                         Device(DualSenseTransport.Bluetooth, 96) })
            {
                string path = Path.Combine(Path.GetTempPath(), $"acc-output-test-{Guid.NewGuid():N}.bin");
                try
                {
                    using (SafeFileHandle handle = File.OpenHandle(path, FileMode.CreateNew,
                               FileAccess.ReadWrite, FileShare.ReadWrite))
                    using (var device = FromHandle(info, handle))
                    {
                        Assert(device.TryWrite(ActiveFrame, ActiveLights), "first ordinary-file output failed");
                        Assert(device.TryWrite(ActiveFrame, ActiveLights), "second ordinary-file output failed");
                    }
                    byte[] actual = File.ReadAllBytes(path);
                    int length = DualSenseReportBuilder.GetOutputReportLength(info);
                    Assert(actual.Length == length * 3, $"WriteFile/Dispose did not submit three {length}-byte reports");
                    byte[] expected = new byte[length];
                    DualSenseReportBuilder.BuildForDevice(ActiveFrame, info, expected, ActiveLights, true);
                    Same(actual.AsSpan(0, length), expected, "first actual write differs or light initialization missing");
                    DualSenseReportBuilder.BuildForDevice(ActiveFrame, info, expected, ActiveLights);
                    Same(actual.AsSpan(length, length), expected, "second actual write repeats initialization or changed feedback");
                    DualSenseReportBuilder.BuildForDevice(FeedbackFrame.Off, info, expected);
                    Same(actual.AsSpan(length * 2, length), expected, "Dispose lost Off cleanup or padding");
                }
                finally { File.Delete(path); }
            }
        });

        check("Failed WriteFile records Edge transfer length and native error without exposing HID path", () =>
        {
            DateTimeOffset start = DateTimeOffset.Now;
            using var handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: false);
            using var device = FromHandle(Device(DualSenseTransport.Usb, 64), handle);
            Assert(!device.TryWrite(ActiveFrame, ActiveLights), "invalid handle falsely reported output success");
            var failure = DualSenseHidDevice.LastOutputFailure
                ?? throw new InvalidOperationException("native write failure was silently discarded");
            Assert(failure.ProductId == 0x0DF2 && failure.Transport == DualSenseTransport.Usb,
                "failure lost model or transport");
            Assert(failure.DeviceReportLength == 64 && failure.SubmittedLength == 64 && failure.BytesWritten == 0,
                "failure lost descriptor/submitted/actual lengths");
            Assert(failure.Win32Error == 6, $"invalid handle native error was not preserved: {failure.Win32Error}");
            Assert(failure.At >= start && failure.At <= DateTimeOffset.Now, "invalid failure timestamp");
            Assert(!failure.ToString().Contains(device.Info.Path, StringComparison.Ordinal), "failure leaked HID device path");
            Assert(!failure.GetType().GetProperties().Any(property => property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)),
                "failure record exposes a HID path property");
            var initialize = typeof(DualSenseHidDevice).GetField("_initializeLights", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert((bool)initialize.GetValue(device)!, "failed output consumed first-success light initialization");
        });
    }

    private static DualSenseHidDevice FromHandle(DualSenseDeviceInfo info, SafeFileHandle handle)
    {
        var constructor = typeof(DualSenseHidDevice).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, [typeof(DualSenseDeviceInfo), typeof(SafeFileHandle)], modifiers: null)!;
        return (DualSenseHidDevice)constructor.Invoke([info, handle]);
    }
}
