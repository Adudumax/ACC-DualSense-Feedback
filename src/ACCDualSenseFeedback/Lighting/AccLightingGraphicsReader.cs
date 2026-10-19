using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace ACCDualSenseFeedback.Lighting;

// Only the documented graphics prefix (packetId, ACC_STATUS) is consumed.
// This optional lighting reader never changes the haptic telemetry snapshot.
internal sealed class AccLightingGraphicsReader(string[]? mapNames = null) : IDisposable
{
    private readonly string[] _mapNames = mapNames ?? [@"Local\acpmf_graphics", "acpmf_graphics"];
    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private long _nextOpen;
    private long _lastChangedAt;
    private int? _previousPacket;
    public CspLightContext Context { get; private set; }

    public void Refresh(long now)
    {
        try
        {
            if (_view is null)
            {
                if (now < _nextOpen) return;
                _nextOpen = now + Stopwatch.Frequency;
                foreach (string name in _mapNames)
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
            if (_previousPacket is null)
            {
                _previousPacket = packet;
                _lastChangedAt = now;
                return;
            }
            if (packet != _previousPacket)
            {
                _previousPacket = packet;
                _lastChangedAt = now;
                // ACC_STATUS: off=0, replay=1, live=2, pause=3.
                // ACC exposes no replay sky/light colour: use CSP's explicit
                // black-background fallback, rather than inventing ambience.
                Context = new() { IsReplay = status == 1, IsPaused = status == 3 };
            }
            else if (now - _lastChangedAt > Stopwatch.Frequency / 2)
                Disconnect();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Disconnect();
            _nextOpen = now + Stopwatch.Frequency;
        }
    }

    public void Disconnect()
    {
        _view?.Dispose(); _map?.Dispose();
        _view = null; _map = null;
        _previousPacket = null;
        Context = default;
    }
    public void Dispose() => Disconnect();
}
