namespace ACCDualSenseFeedback.Diagnostics;

// Diagnostic-only snapshot. The existing USB input thread is the sole writer;
// the existing output thread reads without a lock, allocation or extra polling.
internal sealed class BrakeRawInputBuffer
{
    private long _sequence;
    private int _triggers;
    private long _observedAt;

    internal void Publish(byte left, byte right, long observedAt)
    {
        Interlocked.Increment(ref _sequence);
        Volatile.Write(ref _triggers, left | right << 8);
        Volatile.Write(ref _observedAt, observedAt);
        Interlocked.Increment(ref _sequence);
    }

    internal BrakeRawInput Read(bool sameDevice)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            long before = Volatile.Read(ref _sequence);
            if ((before & 1) != 0) continue;
            int triggers = Volatile.Read(ref _triggers);
            long observedAt = Volatile.Read(ref _observedAt);
            if (before == Volatile.Read(ref _sequence))
                return new((byte)triggers, (byte)(triggers >> 8), observedAt, observedAt > 0, sameDevice);
        }
        return default;
    }
}
