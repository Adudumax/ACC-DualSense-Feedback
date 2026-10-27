using System.Numerics;
using System.Runtime.InteropServices;

namespace ACCDualSenseFeedback.Gyro;

internal readonly record struct GyroFusionPose(Quaternion Orientation, Vector3 Gravity, Vector3 CalibratedGyro);
internal interface IGyroFusion : IDisposable
{
    bool Available { get; }
    string? Error { get; }
    void Reset();
    void SetBias(Vector3 bias);
    bool Process(Vector3 gyro, Vector3 accel, float deltaSeconds, out GyroFusionPose pose);
}

/// <summary>Owned and called only by the controller input thread. Missing or incompatible native code disables gyro only.</summary>
internal sealed class NativeGyroFusion : IGyroFusion
{
    private IntPtr _handle;
    public bool Available => _handle != IntPtr.Zero;
    public string? Error { get; private set; }
    public NativeGyroFusion()
    {
        try
        {
            if (Version() != 1) { Error = "Incompatible motion fusion library"; return; }
            _handle = Create();
            if (_handle == IntPtr.Zero) Error = "Motion fusion could not start";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { Error = "Motion fusion library unavailable"; }
    }
    public void Reset()
    {
        try { if (Available) ResetNative(_handle); }
        catch (Exception e) when (IsBindingError(e)) { Disable(); }
    }
    public void SetBias(Vector3 bias)
    {
        try { if (Available) SetBiasNative(_handle, bias.X, bias.Y, bias.Z); }
        catch (Exception e) when (IsBindingError(e)) { Disable(); }
    }
    public bool Process(Vector3 gyro, Vector3 accel, float deltaSeconds, out GyroFusionPose pose)
    {
        pose = default;
        try
        {
            if (!Available || ProcessNative(_handle, gyro.X, gyro.Y, gyro.Z, accel.X, accel.Y, accel.Z, deltaSeconds, out var output) == 0) return false;
            pose = new(new(output.Qx, output.Qy, output.Qz, output.Qw), new(output.Gx, output.Gy, output.Gz), new(output.Vx, output.Vy, output.Vz));
            return true;
        }
        catch (Exception e) when (IsBindingError(e)) { Disable(); return false; }
    }
    private static bool IsBindingError(Exception e) => e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;
    private void Disable() { Error = "Motion fusion library unavailable"; Dispose(); }
    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        try { Destroy(_handle); } catch (Exception e) when (IsBindingError(e)) { }
        _handle = IntPtr.Zero;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FusionResult { public float Qx, Qy, Qz, Qw, Gx, Gy, Gz, Vx, Vy, Vz; }
    private const string Library = "ACCGyroFusion.dll";
    [DllImport(Library, EntryPoint = "acc_gyro_abi_version", CallingConvention = CallingConvention.Cdecl)] private static extern uint Version();
    [DllImport(Library, EntryPoint = "acc_gyro_create", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr Create();
    [DllImport(Library, EntryPoint = "acc_gyro_destroy", CallingConvention = CallingConvention.Cdecl)] private static extern void Destroy(IntPtr p);
    [DllImport(Library, EntryPoint = "acc_gyro_reset", CallingConvention = CallingConvention.Cdecl)] private static extern void ResetNative(IntPtr p);
    [DllImport(Library, EntryPoint = "acc_gyro_set_bias", CallingConvention = CallingConvention.Cdecl)] private static extern void SetBiasNative(IntPtr p, float x, float y, float z);
    [DllImport(Library, EntryPoint = "acc_gyro_process", CallingConvention = CallingConvention.Cdecl)] private static extern int ProcessNative(IntPtr p, float x, float y, float z, float ax, float ay, float az, float dt, out FusionResult output);
}
