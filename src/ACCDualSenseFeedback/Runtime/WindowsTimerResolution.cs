using System.Runtime.InteropServices;

namespace ACCDualSenseFeedback.Runtime;

internal sealed partial class WindowsTimerResolution : IDisposable
{
    private readonly uint _period;
    private bool _active;

    private WindowsTimerResolution(uint period)
    {
        _period = period;
        _active = TimeBeginPeriod(period) == 0;
    }

    public static WindowsTimerResolution Request1Millisecond() => new(1);

    public void Dispose()
    {
        if (!_active)
            return;

        TimeEndPeriod(_period);
        _active = false;
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint period);
}
