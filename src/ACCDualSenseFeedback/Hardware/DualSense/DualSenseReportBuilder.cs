using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Lighting;

namespace ACCDualSenseFeedback.Hardware.DualSense;

internal static class DualSenseReportBuilder
{
    // Protocol content, including the report ID. Windows HID collections can
    // require a larger transfer buffer (for example, 64 bytes on Edge USB).
    public const int UsbReportLength = 48;
    public const int BluetoothReportLength = 78;
    // One merged report owns both compatible vibration and adaptive triggers.
    // Values below mirror the locally installed DS4Windows 4.0.3-beta.7
    // DualSenseDevice.PrepareOutReport path. In particular, byte 39 (USB) /
    // 40 (BT) selects Accurate rumble emulation; leaving it at zero makes the
    // actuators feel materially harsher even when the motor bytes are equal.
    private const byte ValidMergedFeedback = 0x0F;
    private const byte ValidSecondaryFeedback = 0x55;
    private const byte AccurateRumbleMode = 0x06;

    public static int GetOutputReportLength(DualSenseDeviceInfo device)
        => Math.Max(device.Transport == DualSenseTransport.Bluetooth
            ? BluetoothReportLength : UsbReportLength, (int)device.OutputReportLength);

    public static int BuildForDevice(in FeedbackFrame frame, DualSenseDeviceInfo device,
        Span<byte> destination, RpmLightFrame lights = default, bool initializeLights = false)
    {
        int length = GetOutputReportLength(device);
        if (destination.Length < length)
            throw new ArgumentException($"Destination must contain at least {length} bytes.", nameof(destination));

        int contentLength = Build(frame, device.Transport == DualSenseTransport.Bluetooth,
            destination, lights, initializeLights);
        // Win32 WriteFile requires the collection's OutputReportByteLength.
        // Preserve every protocol byte and zero only the unused transfer tail.
        destination.Slice(contentLength, length - contentLength).Clear();
        return length;
    }

    public static int Build(in FeedbackFrame frame, bool bluetooth, Span<byte> destination,
        RpmLightFrame lights = default, bool initializeLights = false)
    {
        int length = bluetooth ? BluetoothReportLength : UsbReportLength;
        if (destination.Length < length)
            throw new ArgumentException($"Destination must contain at least {length} bytes.", nameof(destination));

        Span<byte> report = destination[..length];
        report.Clear();

        if (bluetooth)
        {
            report[0] = 0x31;
            report[1] = 0x02;
            report[2] = ValidMergedFeedback;
            report[3] = ValidSecondaryFeedback;
            report[4] = frame.RightActuator;
            report[5] = frame.LeftActuator;
            frame.RightTrigger.WriteTo(report.Slice(12, 11));
            frame.LeftTrigger.WriteTo(report.Slice(23, 11));
            report[40] = AccurateRumbleMode;
            report[43] = initializeLights ? (byte)0x02 : (byte)0;
            report[44] = lights.PlayerBrightness;
            report[45] = lights.PlayerMask;
            report[46] = lights.Red;
            report[47] = lights.Green;
            report[48] = lights.Blue;
            uint crc = Crc32.ComputeBluetoothOutput(report[..74]);
            report[74] = (byte)crc;
            report[75] = (byte)(crc >> 8);
            report[76] = (byte)(crc >> 16);
            report[77] = (byte)(crc >> 24);
        }
        else
        {
            report[0] = 0x02;
            report[1] = ValidMergedFeedback;
            report[2] = ValidSecondaryFeedback;
            report[3] = frame.RightActuator;
            report[4] = frame.LeftActuator;
            frame.RightTrigger.WriteTo(report.Slice(11, 11));
            frame.LeftTrigger.WriteTo(report.Slice(22, 11));
            report[39] = AccurateRumbleMode;
            // End the firmware light animation on first output only; repeating
            // LIGHT_OUT would fight the RGB colour on every haptic report.
            report[42] = initializeLights ? (byte)0x02 : (byte)0;
            report[43] = lights.PlayerBrightness;
            report[44] = lights.PlayerMask;
            report[45] = lights.Red;
            report[46] = lights.Green;
            report[47] = lights.Blue;
        }

        return length;
    }
}
