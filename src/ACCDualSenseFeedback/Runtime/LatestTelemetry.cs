using ACCDualSenseFeedback.Telemetry;

namespace ACCDualSenseFeedback.Runtime;

// A small seqlock prevents a reader from ever observing a partially copied
// value-type snapshot, while keeping the hot path allocation- and lock-free.
internal sealed class LatestTelemetry
{
    private TelemetrySnapshot _snapshot;
    private int _sequence;

    public void Publish(in TelemetrySnapshot snapshot)
    {
        int writing = Interlocked.Increment(ref _sequence);
        _snapshot = snapshot;
        Volatile.Write(ref _sequence, writing + 1);
    }

    public TelemetrySnapshot Read()
    {
        while (true)
        {
            int before = Volatile.Read(ref _sequence);
            if ((before & 1) != 0)
            {
                Thread.SpinWait(1);
                continue;
            }

            TelemetrySnapshot snapshot = _snapshot;
            int after = Volatile.Read(ref _sequence);
            if (before == after)
                return snapshot;
        }
    }
}
