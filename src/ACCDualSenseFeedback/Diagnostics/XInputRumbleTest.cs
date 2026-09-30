using System.Runtime.InteropServices;

namespace ACCDualSenseFeedback.Diagnostics;

internal static partial class XInputRumbleTest
{
    private const uint ErrorSuccess = 0;

    public static int Run()
    {
        for (uint index = 0; index < 4; index++)
        {
            if (XInputGetState(index, out _) != ErrorSuccess)
                continue;

            Console.WriteLine($"Testing XInput controller {index} rumble for 1.2 seconds...");
            var vibration = new XInputVibration
            {
                LeftMotorSpeed = 0x7000,
                RightMotorSpeed = 0xA000
            };

            uint result = XInputSetState(index, ref vibration);
            if (result != ErrorSuccess)
            {
                Console.Error.WriteLine($"XInputSetState failed with Windows error {result}.");
                return 1;
            }

            Thread.Sleep(1200);
            vibration = default;
            XInputSetState(index, ref vibration);
            Console.WriteLine("XInput motor test completed and cleared.");
            return 0;
        }

        Console.Error.WriteLine("No XInput controller is connected. DS4Windows' virtual Xbox 360 controller was not found.");
        return 1;
    }

    [LibraryImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static partial uint XInputGetState(uint userIndex, out XInputState state);

    [LibraryImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
    private static partial uint XInputSetState(uint userIndex, ref XInputVibration vibration);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration
    {
        public ushort LeftMotorSpeed;
        public ushort RightMotorSpeed;
    }
}
