using ACCDualSenseFeedback.Telemetry;

namespace ACCDualSenseFeedback.Runtime;

// The first sample after a connection is only a baseline. Only a subsequent
// packet change proves that a producer is publishing, including after a retry.
internal sealed class AccTelemetrySession
{
    private int _processId;
    private int _previousPacket;
    private bool _hasBaseline;

    public bool IsGameRunning => _processId != 0;

    public bool SetProcess(int processId)
    {
        if (_processId == processId)
            return false;

        _processId = processId;
        ResetMapping();
        return true;
    }

    public void ResetMapping() => _hasBaseline = false;

    public bool TryAccept(in TelemetrySnapshot snapshot)
    {
        if (!IsGameRunning)
            return false;

        if (!_hasBaseline)
        {
            _previousPacket = snapshot.PacketId;
            _hasBaseline = true;
            return false;
        }

        if (snapshot.PacketId == _previousPacket)
            return false;

        _previousPacket = snapshot.PacketId;
        return true;
    }
}
