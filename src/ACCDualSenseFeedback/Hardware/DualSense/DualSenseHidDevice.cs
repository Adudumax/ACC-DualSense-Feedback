using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ACCDualSenseFeedback.Haptics;

namespace ACCDualSenseFeedback.Hardware.DualSense;

internal enum DualSenseTransport
{
    Usb,
    Bluetooth
}

internal sealed record DualSenseDeviceInfo(
    string Path,
    ushort ProductId,
    DualSenseTransport Transport,
    ushort InputReportLength,
    ushort OutputReportLength);

internal sealed class DualSenseHidDevice : IDisposable
{
    private const ushort SonyVendorId = 0x054C;
    private const ushort DualSenseProductId = 0x0CE6;
    private const ushort DualSenseEdgeProductId = 0x0DF2;

    private readonly SafeFileHandle _handle;
    private readonly byte[] _reportBuffer = new byte[DualSenseReportBuilder.BluetoothReportLength];

    public DualSenseDeviceInfo Info { get; }

    private DualSenseHidDevice(DualSenseDeviceInfo info, SafeFileHandle handle)
    {
        Info = info;
        _handle = handle;
    }

    public static List<DualSenseDeviceInfo> Enumerate()
    {
        var result = new List<DualSenseDeviceInfo>();
        NativeMethods.HidD_GetHidGuid(out Guid hidGuid);
        IntPtr set = NativeMethods.SetupDiGetClassDevs(
            ref hidGuid,
            null,
            IntPtr.Zero,
            NativeMethods.DigcfPresent | NativeMethods.DigcfDeviceInterface);

        if (set == NativeMethods.InvalidHandleValue)
            return result;

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new NativeMethods.SpDeviceInterfaceData
                {
                    CbSize = (uint)Marshal.SizeOf<NativeMethods.SpDeviceInterfaceData>()
                };

                if (!NativeMethods.SetupDiEnumDeviceInterfaces(
                        set, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                    break;

                NativeMethods.SetupDiGetDeviceInterfaceDetail(
                    set, ref interfaceData, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                if (required == 0)
                    continue;

                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(
                            set, ref interfaceData, detail, required, out _, IntPtr.Zero))
                        continue;

                    // cbSize is 8 on x64 because the native structure's total
                    // size includes tail padding, but WCHAR DevicePath itself
                    // still begins immediately after the 4-byte DWORD.
                    const int pathOffset = sizeof(uint);
                    string? path = Marshal.PtrToStringUni(detail + pathOffset);
                    if (string.IsNullOrEmpty(path))
                        continue;

                    using SafeFileHandle probe = OpenShared(path);
                    if (probe.IsInvalid)
                        continue;

                    var attributes = new NativeMethods.HiddAttributes
                    {
                        Size = Marshal.SizeOf<NativeMethods.HiddAttributes>()
                    };
                    if (!NativeMethods.HidD_GetAttributes(probe, ref attributes) ||
                        attributes.VendorId != SonyVendorId ||
                        (attributes.ProductId != DualSenseProductId && attributes.ProductId != DualSenseEdgeProductId))
                        continue;

                    if (!TryGetCapabilities(probe, out NativeMethods.HidpCaps caps) ||
                        caps.UsagePage != 0x01 || caps.Usage != 0x05)
                        continue;

                    ushort outputLength = caps.OutputReportByteLength;
                    bool bluetooth = outputLength >= DualSenseReportBuilder.BluetoothReportLength ||
                                     path.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
                                     path.Contains("BLUETOOTH", StringComparison.OrdinalIgnoreCase);
                    result.Add(new DualSenseDeviceInfo(
                        path,
                        attributes.ProductId,
                        bluetooth ? DualSenseTransport.Bluetooth : DualSenseTransport.Usb,
                        caps.InputReportByteLength,
                        outputLength));
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    public static DualSenseHidDevice? TryOpenFirst()
    {
        // Prefer USB for deterministic competition latency.
        foreach (DualSenseDeviceInfo info in Enumerate().OrderBy(static d => d.Transport))
        {
            SafeFileHandle handle = OpenShared(info.Path);
            if (!handle.IsInvalid)
                return new DualSenseHidDevice(info, handle);
            handle.Dispose();
        }

        return null;
    }

    public bool TryWrite(in FeedbackFrame frame)
    {
        try
        {
            int length = DualSenseReportBuilder.Build(
                frame,
                Info.Transport == DualSenseTransport.Bluetooth,
                _reportBuffer);
            return NativeMethods.WriteFile(
                _handle,
                _reportBuffer,
                (uint)length,
                out uint written,
                IntPtr.Zero) && written == length;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            TryWrite(FeedbackFrame.Off);
        }
        catch
        {
        }

        _handle.Dispose();
    }

    private static SafeFileHandle OpenShared(string path)
        => NativeMethods.CreateFile(
            path,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            0,
            IntPtr.Zero);

    private static bool TryGetCapabilities(SafeFileHandle handle, out NativeMethods.HidpCaps caps)
    {
        caps = default;
        if (!NativeMethods.HidD_GetPreparsedData(handle, out IntPtr data))
            return false;
        try
        {
            return NativeMethods.HidP_GetCaps(data, out caps) >= 0;
        }
        finally
        {
            NativeMethods.HidD_FreePreparsedData(data);
        }
    }
}
