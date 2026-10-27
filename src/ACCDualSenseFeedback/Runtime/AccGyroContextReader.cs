using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace ACCDualSenseFeedback.Runtime;

// Read-only graphics status. Unlike lighting's optional context, missing/off
// graphics never means "live". No speed, throttle or engine heuristic is used.
internal sealed class AccGyroContextReader : IDisposable
{
    private readonly string[] _names;
    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private long _nextOpen;
    private long _lastChanged;
    private int? _previousPacket;
    private int _status;
    private bool _verified;

    public AccGyroContextReader(string[]? names = null) => _names = names ?? [@"Local\acpmf_graphics", "acpmf_graphics"];

    public bool IsLive(long now) => _verified && _status == 2 && now - _lastChanged <= Stopwatch.Frequency / 2;

    public void Refresh(long now)
    {
        try
        {
            if (_view is null)
            {
                if (now < _nextOpen) return;
                _nextOpen = now + Stopwatch.Frequency;
                foreach (string name in _names)
                {
                    try
                    {
                        var map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                        try { _view = map.CreateViewAccessor(0, 8, MemoryMappedFileAccess.Read); }
                        catch { map.Dispose(); throw; }
                        _map = map;
                        break;
                    }
                    catch (FileNotFoundException) { }
                    catch (WaitHandleCannotBeOpenedException) { }
                }
            }
            if (_view is null) return;
            int packet = _view.ReadInt32(0);
            int status = _view.ReadInt32(4);
            Thread.MemoryBarrier();
            if (packet != _view.ReadInt32(0)) return;
            if (_previousPacket != packet)
            {
                if (_previousPacket is not null) _verified = true;
                _previousPacket = packet;
                _lastChanged = now;
            }
            _status = status;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Disconnect();
            _nextOpen = now + Stopwatch.Frequency;
        }
    }

    public void Disconnect()
    {
        _view?.Dispose(); _map?.Dispose();
        _view = null; _map = null; _previousPacket = null;
        _verified = false; _status = 0; _lastChanged = 0;
    }
    public void Dispose() => Disconnect();
}

internal static partial class AccForegroundWindow
{
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();
    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    public static bool IsForeground(int processId)
    {
        if (processId <= 0) return false;
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out uint foregroundProcess);
        return foregroundProcess == (uint)processId;
    }
}
