using ACCDualSenseFeedback.Haptics;

namespace ACCDualSenseFeedback.Hardware.DualSense;

internal static class DualSenseReportBuilder
{
    // Windows reports 47 payload bytes plus the report ID for the native
    // DualSense gamepad collection. All fields used here fit within it.
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

    public static int Build(in FeedbackFrame frame, bool bluetooth, Span<byte> destination)
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
            report[43] = 0x02;
            report[44] = 0x02;
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
            report[42] = 0x02;
            report[43] = 0x02;
        }

        return length;
    }
}
