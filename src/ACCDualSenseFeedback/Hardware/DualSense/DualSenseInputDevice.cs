using System.Buffers.Binary;
using System.Numerics;
using ACCDualSenseFeedback.Gyro;
using Microsoft.Win32.SafeHandles;

namespace ACCDualSenseFeedback.Hardware.DualSense;

internal sealed class DualSenseInputDevice : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly byte[] _buffer;
    private int _disposed;
    internal const int ReadDeadlineMilliseconds = 250;

    public DualSenseDeviceInfo Info { get; }
    public DualSenseImuCalibration? Calibration { get; }

    private DualSenseInputDevice(DualSenseDeviceInfo info, SafeFileHandle handle)
    {
        Info = info;
        _handle = handle;
        _buffer = new byte[Math.Max(64, (int)info.InputReportLength)];
        var feature = new byte[41];
        feature[0] = 0x05;
        if (NativeMethods.HidD_GetFeature(handle, feature, (uint)feature.Length) &&
            DualSenseImuCalibration.TryParse(feature, out DualSenseImuCalibration? calibration))
            Calibration = calibration;
        _stream = new FileStream(handle, FileAccess.Read, _buffer.Length, isAsync: true);
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
                NativeMethods.FileFlagOverlapped,
                IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                try { return new DualSenseInputDevice(info, handle); }
                catch { handle.Dispose(); throw; }
            }
            handle.Dispose();
        }

        return null;
    }

    public bool TryRead(out DualSenseInputState state, out GyroSensorSample sensor)
    {
        state = default;
        sensor = new(default, default, 0, false, DeviceIdentity: Info.Identity);
        if (Volatile.Read(ref _disposed) != 0 || Info.InputReportLength < 11)
            return false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            deadline.CancelAfter(ReadDeadlineMilliseconds);
            int read = _stream.ReadAsync(_buffer.AsMemory(0, Info.InputReportLength), deadline.Token)
                .AsTask().GetAwaiter().GetResult();
            ReadOnlySpan<byte> report = _buffer.AsSpan(0, read);
            if (!DualSenseInputState.TryParseUsb(report, out state, isEdge: Info.ProductId == 0x0DF2)) return false;
            DualSenseImuCalibration.TryParseMotion(report, Calibration, Info.Identity, out sensor);
            return true;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }

    public bool TryRead(out DualSenseInputState state) => TryRead(out state, out _);

    public void CancelRead()
    {
        _lifetime.Cancel();
        if (!_handle.IsInvalid && !_handle.IsClosed) NativeMethods.CancelIoEx(_handle, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancelRead();
        _stream.Dispose();
        // The read thread may still be unwinding a linked cancellation source.
        // Do not dispose the lifetime source until all I/O has been cancelled.
    }
}

/// <summary>
/// Independent protocol implementation using the documented SDL PS5 calibration
/// layout: factory bias/range, angular rate in degrees/s, acceleration in g.
/// https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_ps5.c
/// </summary>
internal sealed record DualSenseImuCalibration(Vector3 GyroBias, Vector3 GyroScale, Vector3 AccelBias, Vector3 AccelScale)
{
    public static bool TryParse(ReadOnlySpan<byte> feature, out DualSenseImuCalibration? calibration)
    {
        calibration = null;
        if (feature.Length < 35 || feature[0] != 0x05) return false;
        var gyroBias = new Vector3(Read(feature, 1), Read(feature, 3), Read(feature, 5));
        int gyroSpeed = Read(feature, 19) + Read(feature, 21);
        if (gyroSpeed <= 0) return false;
        var gyroRange = new Vector3(Read(feature, 7) - Read(feature, 9),
            Read(feature, 11) - Read(feature, 13), Read(feature, 15) - Read(feature, 17));
        var accPlus = new Vector3(Read(feature, 23), Read(feature, 27), Read(feature, 31));
        var accMinus = new Vector3(Read(feature, 25), Read(feature, 29), Read(feature, 33));
        Vector3 accRange = accPlus - accMinus;
        if (gyroRange.X <= 0 || gyroRange.Y <= 0 || gyroRange.Z <= 0 ||
            accRange.X <= 0 || accRange.Y <= 0 || accRange.Z <= 0) return false;
        var gyroScale = new Vector3(gyroSpeed / gyroRange.X, gyroSpeed / gyroRange.Y, gyroSpeed / gyroRange.Z);
        Vector3 accBias = accPlus - accRange / 2;
        var accScale = new Vector3(2 / accRange.X, 2 / accRange.Y, 2 / accRange.Z);
        // Match SDL's sanity envelope: nominal gyro=1/16 degrees/s per count,
        // accel=1/8192 g per count. Bad factory values disable gyro only.
        if (!BiasValid(gyroBias) || !BiasValid(accBias) ||
            !ScaleValid(gyroScale, 1f / 16) || !ScaleValid(accScale, 1f / 8192)) return false;
        calibration = new(gyroBias, gyroScale, accBias, accScale);
        return true;
    }

    public static bool TryParseMotion(ReadOnlySpan<byte> report, DualSenseImuCalibration? calibration,
        string identity, out GyroSensorSample sensor)
    {
        sensor = new(default, default, 0, false, DeviceIdentity: identity);
        // USB report starts with 0x01; SDL's packet offsets exclude that byte.
        if (report.Length < 32 || report[0] != 0x01) return false;
        var rawGyro = new Vector3(Read(report, 16), Read(report, 18), Read(report, 20));
        var rawAccel = new Vector3(Read(report, 22), Read(report, 24), Read(report, 26));
        uint timestamp = BinaryPrimitives.ReadUInt32LittleEndian(report[28..32]);
        if (calibration is null)
        {
            sensor = new(default, default, timestamp, false, rawGyro, rawAccel, identity);
            return false;
        }
        Vector3 gyro = (rawGyro - calibration.GyroBias) * calibration.GyroScale;
        Vector3 accel = (rawAccel - calibration.AccelBias) * calibration.AccelScale;
        // Saturated sensor samples cannot be trusted for pose tracking.
        bool valid = !Saturated(rawGyro) && !Saturated(rawAccel);
        sensor = new(gyro, accel, timestamp, valid, rawGyro, rawAccel, identity);
        return valid;
    }

    private static short Read(ReadOnlySpan<byte> buffer, int offset) => BinaryPrimitives.ReadInt16LittleEndian(buffer[offset..]);
    private static bool BiasValid(Vector3 bias) => MathF.Abs(bias.X) <= 1024 && MathF.Abs(bias.Y) <= 1024 && MathF.Abs(bias.Z) <= 1024;
    private static bool ScaleValid(Vector3 scale, float nominal) => scale.X >= nominal * .5f && scale.X <= nominal * 1.5f &&
        scale.Y >= nominal * .5f && scale.Y <= nominal * 1.5f && scale.Z >= nominal * .5f && scale.Z <= nominal * 1.5f;
    private static bool Saturated(Vector3 values) => MathF.Abs(values.X) >= 32767 || MathF.Abs(values.Y) >= 32767 || MathF.Abs(values.Z) >= 32767;
}

internal readonly record struct DualSenseInputState(
    short LeftX,
    short LeftY,
    short RightX,
    short RightY,
    byte LeftTrigger,
    byte RightTrigger,
    ushort Buttons,
    GyroButton MotionButtons = GyroButton.None)
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

    public static bool TryParseUsb(ReadOnlySpan<byte> report, out DualSenseInputState state, bool isEdge = false)
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
            buttons,
            ParseMotionButtons(faceAndDpad, shoulders, special, isEdge));
        return true;
    }

    public ushort ButtonsWithout(GyroButton consumed)
    {
        ushort mask = 0;
        if ((consumed & GyroButton.Cross) != 0) mask |= A;
        if ((consumed & GyroButton.Circle) != 0) mask |= B;
        if ((consumed & GyroButton.Square) != 0) mask |= X;
        if ((consumed & GyroButton.Triangle) != 0) mask |= Y;
        if ((consumed & GyroButton.Share) != 0) mask |= Back;
        if ((consumed & GyroButton.Options) != 0) mask |= Start;
        if ((consumed & GyroButton.L1) != 0) mask |= LeftShoulder;
        if ((consumed & GyroButton.R1) != 0) mask |= RightShoulder;
        if ((consumed & GyroButton.L3) != 0) mask |= LeftThumb;
        if ((consumed & GyroButton.R3) != 0) mask |= RightThumb;
        if ((consumed & GyroButton.PS) != 0) mask |= Guide;
        if ((consumed & GyroButton.DpadUp) != 0) mask |= DpadUp;
        if ((consumed & GyroButton.DpadRight) != 0) mask |= DpadRight;
        if ((consumed & GyroButton.DpadDown) != 0) mask |= DpadDown;
        if ((consumed & GyroButton.DpadLeft) != 0) mask |= DpadLeft;
        return (ushort)(Buttons & ~mask);
    }

    // A trigger shortcut must suppress its analog XInput channel as well as
    // its digital learning/activation press. Other axes remain byte-exact.
    public byte LeftTriggerWithout(GyroButton consumed) => (consumed & GyroButton.L2) != 0 ? (byte)0 : LeftTrigger;
    public byte RightTriggerWithout(GyroButton consumed) => (consumed & GyroButton.R2) != 0 ? (byte)0 : RightTrigger;

    private static GyroButton ParseMotionButtons(byte face, byte shoulders, byte special, bool isEdge)
    {
        GyroButton buttons = GyroButton.None;
        ushort dpad = MapDpad((byte)(face & 0x0F));
        if ((dpad & DpadUp) != 0) buttons |= GyroButton.DpadUp;
        if ((dpad & DpadRight) != 0) buttons |= GyroButton.DpadRight;
        if ((dpad & DpadDown) != 0) buttons |= GyroButton.DpadDown;
        if ((dpad & DpadLeft) != 0) buttons |= GyroButton.DpadLeft;
        if ((face & 0x10) != 0) buttons |= GyroButton.Square;
        if ((face & 0x20) != 0) buttons |= GyroButton.Cross;
        if ((face & 0x40) != 0) buttons |= GyroButton.Circle;
        if ((face & 0x80) != 0) buttons |= GyroButton.Triangle;
        if ((shoulders & 0x01) != 0) buttons |= GyroButton.L1;
        if ((shoulders & 0x02) != 0) buttons |= GyroButton.R1;
        // USB digital trigger flags are authoritative; do not invent an
        // analog threshold that changes with travel or an Edge trigger stop.
        if ((shoulders & 0x04) != 0) buttons |= GyroButton.L2;
        if ((shoulders & 0x08) != 0) buttons |= GyroButton.R2;
        if ((shoulders & 0x10) != 0) buttons |= GyroButton.Share;
        if ((shoulders & 0x20) != 0) buttons |= GyroButton.Options;
        if ((shoulders & 0x40) != 0) buttons |= GyroButton.L3;
        if ((shoulders & 0x80) != 0) buttons |= GyroButton.R3;
        if ((special & 0x01) != 0) buttons |= GyroButton.PS;
        if ((special & 0x02) != 0) buttons |= GyroButton.Touchpad;
        // SDL's PS5 full-state report and Sony's Linux hid-playstation
        // layout agree on the mute and DualSense Edge extra-button bits.
        if ((special & 0x04) != 0) buttons |= GyroButton.Mute;
        if (isEdge)
        {
            if ((special & 0x10) != 0) buttons |= GyroButton.EdgeFnLeft;
            if ((special & 0x20) != 0) buttons |= GyroButton.EdgeFnRight;
            if ((special & 0x40) != 0) buttons |= GyroButton.EdgePaddleLeft;
            if ((special & 0x80) != 0) buttons |= GyroButton.EdgePaddleRight;
        }
        return buttons;
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
