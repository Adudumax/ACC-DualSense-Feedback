using System.ComponentModel;
using System.Diagnostics;

namespace ACCDualSenseFeedback.Runtime;

// AC and ACC use the same shared-memory names. A retained mapping is not
// evidence that ACC is running, and must not enable controller feedback.
internal sealed class AccProcessMonitor : IDisposable
{
    private Process? _process;
    private long _nextProbe;

    public int GetProcessId(long now)
    {
        if (now < _nextProbe)
            return _process?.Id ?? 0;

        _nextProbe = now + Stopwatch.Frequency / 4;
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                    return _process.Id;
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
            }
            _process.Dispose();
            _process = null;
        }

        foreach (Process candidate in Process.GetProcessesByName("AC2-Win64-Shipping"))
        {
            if (_process is null)
            {
                try
                {
                    if (!candidate.HasExited)
                    {
                        _process = candidate;
                        continue;
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                {
                }
            }
            candidate.Dispose();
        }
        return _process?.Id ?? 0;
    }

    public void Dispose() => _process?.Dispose();
}
