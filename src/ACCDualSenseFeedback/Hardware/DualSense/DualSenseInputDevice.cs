using Microsoft.Win32.SafeHandles;

namespace ACCDualSenseFeedback.Hardware.DualSense;

internal sealed class DualSenseInputDevice : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly byte[] _buffer;

    public DualSenseDeviceInfo Info { get; }

    private DualSenseInputDevice(DualSenseDeviceInfo info, SafeFileHandle handle)
    {
        Info = info;
        _handle = handle;
        _buffer = new byte[Math.Max(64, (int)info.InputReportLength)];
    }

    public static DualSenseInputDevice? TryOpenUsb()
    {
        foreach (DualSenseDeviceInfo info in DualSenseHidDevice.Enumerate())
        {
            if (info.Transport != DualSenseTransport.Usb)
                continue;

            SafeFileHandle handle = NativeMethods.CreateFile(
                info.Path,
                NativeMethods.GenericRead,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                0,
                IntPtr.Zero);
            if (!handle.IsInvalid)
                return new DualSenseInputDevice(info, handle);
            handle.Dispose();
        }

        return null;
    }

    public bool TryRead(out DualSenseInputState state)
    {
        state = default;
        uint length = Info.InputReportLength;
        if (length < 11 ||
            !NativeMethods.ReadFile(_handle, _buffer, length, out uint read, IntPtr.Zero) ||
            read < 11)
            return false;

        return DualSenseInputState.TryParseUsb(_buffer.AsSpan(0, (int)read), out state);
    }

    public void Dispose()
    {
        if (!_handle.IsInvalid && !_handle.IsClosed)
            NativeMethods.CancelIoEx(_handle, IntPtr.Zero);
        _handle.Dispose();
    }
}

internal readonly record struct DualSenseInputState(
    short LeftX,
    short LeftY,
    short RightX,
    short RightY,
    byte LeftTrigger,
    byte RightTrigger,
    ushort Buttons)
{
    private const ushort DpadUp = 0x0001;
    private const ushort DpadDown = 0x0002;
    private const ushort DpadLeft = 0x0004;
    private const ushort DpadRight = 0x0008;
    private const ushort Start = 0x0010;
    private const ushort Back = 0x0020;
    private const ushort LeftThumb = 0x0040;
    private const ushort RightThumb = 0x0080;
    private const ushort LeftShoulder = 0x0100;
    private const ushort RightShoulder = 0x0200;
    private const ushort Guide = 0x0400;
    private const ushort A = 0x1000;
    private const ushort B = 0x2000;
    private const ushort X = 0x4000;
    private const ushort Y = 0x8000;

    public static bool TryParseUsb(ReadOnlySpan<byte> report, out DualSenseInputState state)
    {
        state = default;
        if (report.Length < 11 || report[0] != 0x01)
            return false;

        byte faceAndDpad = report[8];
        byte shoulders = report[9];
        byte special = report[10];
        ushort buttons = MapDpad((byte)(faceAndDpad & 0x0F));

        if ((faceAndDpad & 0x10) != 0) buttons |= X;
        if ((faceAndDpad & 0x20) != 0) buttons |= A;
        if ((faceAndDpad & 0x40) != 0) buttons |= B;
        if ((faceAndDpad & 0x80) != 0) buttons |= Y;
        if ((shoulders & 0x01) != 0) buttons |= LeftShoulder;
        if ((shoulders & 0x02) != 0) buttons |= RightShoulder;
        if ((shoulders & 0x10) != 0) buttons |= Back;
        if ((shoulders & 0x20) != 0) buttons |= Start;
        if ((shoulders & 0x40) != 0) buttons |= LeftThumb;
        if ((shoulders & 0x80) != 0) buttons |= RightThumb;
        if ((special & 0x01) != 0) buttons |= Guide;

        state = new DualSenseInputState(
            MapAxis(report[1], invert: false),
            MapAxis(report[2], invert: true),
            MapAxis(report[3], invert: false),
            MapAxis(report[4], invert: true),
            report[5],
            report[6],
            buttons);
        return true;
    }

    private static ushort MapDpad(byte value) => value switch
    {
        0 => DpadUp,
        1 => DpadUp | DpadRight,
        2 => DpadRight,
        3 => DpadDown | DpadRight,
        4 => DpadDown,
        5 => DpadDown | DpadLeft,
        6 => DpadLeft,
        7 => DpadUp | DpadLeft,
        _ => 0
    };

    private static short MapAxis(byte value, bool invert)
    {
        int centered = value - 128;
        int scaled = centered < 0 ? centered * 256 : centered * 258;
        if (invert)
            scaled = -scaled;
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }
}
