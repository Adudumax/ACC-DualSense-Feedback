using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace ACCDualSenseFeedback.Lighting;

// Optional read-only car metadata for lighting and calibrated shift cues.
// The physics reader and its raw snapshots are deliberately left untouched.
internal sealed class AccLightingMetadataReader : IDisposable
{
    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private long _nextRead;
    private readonly string[] _mapNames;
    private readonly AccLightingGraphicsReader _graphics;
    public int MaxRpm { get; private set; }
    public string CarModel { get; private set; } = "";
    public CspLightContext Context => _graphics.Context;

    public AccLightingMetadataReader(string[]? mapNames = null, string[]? graphicsMapNames = null)
    {
        _mapNames = mapNames ?? [@"Local\acpmf_static", "acpmf_static"];
        _graphics = new AccLightingGraphicsReader(graphicsMapNames);
    }

    public void Refresh(long now)
    {
        _graphics.Refresh(now);
        if (now < _nextRead) return;
        _nextRead = now + Stopwatch.Frequency / 2;
        try
        {
            if (_view is null)
            {
                foreach (string name in _mapNames)
                {
                    try
                    {
                        var map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                        try { _view = map.CreateViewAccessor(0, 416, MemoryMappedFileAccess.Read); }
                        catch { map.Dispose(); throw; }
                        _map = map;
                        break;
                    }
                    catch (FileNotFoundException) { }
                    catch (WaitHandleCannotBeOpenedException) { }
                }
            }
            if (_view is null) return;
            // ACC static prefix, pack=4: UTF-16 carModel[33] at 68;
            // sectorCount at 400, maxTorque 404, maxPower 408, maxRpm 412.
            int rpm = _view.ReadInt32(412);
            MaxRpm = RpmLightEngine.IsValidMaxRpm(rpm) ? rpm : 0;
            // Read only the car ID, never the nearby player identity fields.
            var carName = new byte[66];
            _view.ReadArray(68, carName, 0, carName.Length);
            string raw = Encoding.Unicode.GetString(carName).Split('\0')[0];
            CarModel = new string(raw.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.').ToArray());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Disconnect();
            _nextRead = now + Stopwatch.Frequency;
        }
    }

    public void Disconnect()
    {
        _view?.Dispose(); _map?.Dispose();
        _view = null; _map = null;
        _graphics.Disconnect();
        MaxRpm = 0;
        CarModel = "";
        _nextRead = 0;
    }
    public void Dispose() => Disconnect();
}
