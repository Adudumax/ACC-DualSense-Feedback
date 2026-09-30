using System.Diagnostics;
using ACCDualSenseFeedback.Diagnostics;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Hardware.DualSense;
using ACCDualSenseFeedback.Telemetry;
using ACCDualSenseFeedback.Ui;

namespace ACCDualSenseFeedback.Runtime;

internal sealed class FeedbackRuntime : IDisposable
{
    private static readonly long StaleTicks = (long)(0.120 * Stopwatch.Frequency);
    private readonly bool _dryRun;
    private readonly bool _useConsoleUi;
    private readonly Action<FeedbackRuntimeStatus>? _statusSink;
    private readonly Func<FeedbackTuning>? _tuningProvider;
    private readonly TelemetryCsvCapture? _capture;
    private readonly LatestTelemetry _latest = new();
    private readonly AutoResetEvent _telemetryUpdated = new(false);
    private readonly AccSharedMemoryReader _reader = new();
    private Thread? _telemetryThread;
    private Thread? _outputThread;
    private VirtualXboxBridge? _inputBridge;
    private volatile bool _running;
    private long _framesRead;
    private long _framesWritten;
    private int _leftTriggerMode;
    private int _rightTriggerMode;
    private int _dualSenseConnected;
    private Exception? _backgroundFailure;

    public Exception? BackgroundFailure => Volatile.Read(ref _backgroundFailure);

    public FeedbackRuntime(
        bool dryRun,
        string? capturePath = null,
        Action<FeedbackRuntimeStatus>? statusSink = null,
        bool useConsoleUi = true,
        Func<FeedbackTuning>? tuningProvider = null)
    {
        _dryRun = dryRun;
        _useConsoleUi = useConsoleUi;
        _statusSink = statusSink;
        _tuningProvider = tuningProvider;
        _capture = capturePath is null ? null : new TelemetryCsvCapture(capturePath);
    }

    public void Run(CancellationToken cancellationToken)
    {
        if (!_dryRun)
        {
            _inputBridge = new VirtualXboxBridge(
                () => _telemetryUpdated.Set(),
                exception => FailBackgroundThread("input bridge", exception));
            if (_useConsoleUi)
                ConsoleUi.Ready("Controller input", "USB DualSense is bridged to the virtual Xbox 360 controller.");
        }

        _running = true;
        _telemetryThread = new Thread(TelemetryLoop)
        {
            IsBackground = true,
            Name = "ACC telemetry",
            Priority = ThreadPriority.AboveNormal
        };
        _outputThread = new Thread(OutputLoop)
        {
            IsBackground = true,
            Name = "DualSense output",
            Priority = ThreadPriority.AboveNormal
        };

        _telemetryThread.Start();
        _outputThread.Start();

        long nextStatus = Stopwatch.GetTimestamp();
        bool previousAccConnection = false;
        bool previousDualSenseConnection = false;
        while (!cancellationToken.IsCancellationRequested && _running)
        {
            Thread.Sleep(50);
            long now = Stopwatch.GetTimestamp();
            if (now >= nextStatus)
            {
                TelemetrySnapshot status = _latest.Read();
                bool accConnected = _reader.IsConnected;
                bool dualSenseConnected = Volatile.Read(ref _dualSenseConnected) != 0;
                bool telemetryStale = status.ObservedTimestamp == 0 || now - status.ObservedTimestamp > StaleTicks;

                if (_useConsoleUi && accConnected && !previousAccConnection)
                    ConsoleUi.Ready("ACC telemetry", "Shared-memory telemetry is connected.");
                else if (_useConsoleUi && !accConnected && previousAccConnection)
                    ConsoleUi.Warning("ACC telemetry lost", "Feedback is cleared until the game starts publishing again.");

                if (_useConsoleUi && !dualSenseConnected && previousDualSenseConnection)
                    ConsoleUi.Warning("DualSense disconnected", "Reconnect by USB. The app will retry automatically.");

                previousAccConnection = accConnected;
                previousDualSenseConnection = dualSenseConnected;

                long framesRead = Interlocked.Read(ref _framesRead);
                long framesWritten = Interlocked.Read(ref _framesWritten);
                byte nativeLargeMotor = _inputBridge?.LargeMotor ?? 0;
                byte nativeSmallMotor = _inputBridge?.SmallMotor ?? 0;
                int leftTriggerMode = Volatile.Read(ref _leftTriggerMode);
                int rightTriggerMode = Volatile.Read(ref _rightTriggerMode);

                if (_useConsoleUi)
                {
                    ConsoleUi.UpdateRuntime(
                        status,
                        accConnected,
                        dualSenseConnected,
                        telemetryStale,
                        framesRead,
                        framesWritten,
                        nativeLargeMotor,
                        nativeSmallMotor,
                        leftTriggerMode,
                        rightTriggerMode);
                }

                _statusSink?.Invoke(new FeedbackRuntimeStatus(
                    status,
                    accConnected,
                    dualSenseConnected,
                    telemetryStale,
                    _inputBridge is not null,
                    framesRead,
                    framesWritten,
                    nativeLargeMotor,
                    nativeSmallMotor,
                    leftTriggerMode,
                    rightTriggerMode));
                nextStatus = now + Stopwatch.Frequency;
            }
        }

        _running = false;
        _telemetryUpdated.Set();
        _telemetryThread.Join();
        _outputThread.Join();
        _inputBridge?.Dispose();
        _inputBridge = null;
    }

    private void TelemetryLoop()
    {
        try
        {
            int previousPacket = int.MinValue;
            long lastPacketAt = Stopwatch.GetTimestamp();
            while (_running)
            {
                if (!_reader.IsConnected && !_reader.TryConnect())
                {
                    Thread.Sleep(250);
                    lastPacketAt = Stopwatch.GetTimestamp();
                    continue;
                }

                if (_reader.TryReadLatest(out TelemetrySnapshot snapshot) && snapshot.PacketId != previousPacket)
                {
                    previousPacket = snapshot.PacketId;
                    lastPacketAt = snapshot.ObservedTimestamp;
                    _latest.Publish(snapshot);
                    Interlocked.Increment(ref _framesRead);
                    _telemetryUpdated.Set();
                }
                else
                {
                    long now = Stopwatch.GetTimestamp();
                    if (now - lastPacketAt > 2 * Stopwatch.Frequency)
                    {
                        _reader.Disconnect();
                        previousPacket = int.MinValue;
                        lastPacketAt = now;
                        Thread.Sleep(100);
                        continue;
                    }

                    // Yield without building a backlog. The event-driven output path
                    // still wakes immediately as soon as a new packet is published.
                    Thread.Sleep(1);
                }
            }
        }
        catch (Exception exception)
        {
            FailBackgroundThread("telemetry", exception);
        }
    }

    private void OutputLoop()
    {
        var engine = new CompetitionFeedbackEngine();
        DualSenseHidDevice? device = null;
        FeedbackFrame previous = FeedbackFrame.Off;
        long nextReconnect = 0;
        long lastWriteAt = 0;

        try
        {
            while (_running)
            {
                // Wake instantly for telemetry, or every 4 ms to advance modulation.
                _telemetryUpdated.WaitOne(4);
                long now = Stopwatch.GetTimestamp();

                if (!_dryRun && device is null && now >= nextReconnect)
                {
                    device = DualSenseHidDevice.TryOpenFirst();
                    nextReconnect = now + Stopwatch.Frequency;
                    if (device is not null)
                    {
                        Volatile.Write(ref _dualSenseConnected, 1);
                        if (_useConsoleUi)
                            ConsoleUi.Ready("Feedback output", $"DualSense connected via {device.Info.Transport}.");
                    }
                }

                TelemetrySnapshot snapshot = _latest.Read();
                bool stale = snapshot.ObservedTimestamp == 0 || now - snapshot.ObservedTimestamp > StaleTicks;
                byte originalLarge = _inputBridge?.LargeMotor ?? 0;
                byte originalSmall = _inputBridge?.SmallMotor ?? 0;
                FeedbackFrame frame;
                if (stale)
                {
                    frame = FeedbackFrame.Off;
                }
                else if (_tuningProvider is null)
                {
                    frame = engine.Compute(snapshot, now, originalLarge, originalSmall);
                }
                else
                {
                    FeedbackTuning tuning = _tuningProvider();
                    frame = engine.Compute(snapshot, now, originalLarge, originalSmall, tuning);
                }
                if (!stale)
                    _capture?.TryWrite(snapshot, originalLarge, originalSmall, frame);
                Volatile.Write(ref _leftTriggerMode, frame.LeftTrigger.Mode);
                Volatile.Write(ref _rightTriggerMode, frame.RightTrigger.Mode);
                // Reassert active trigger state at 50 Hz in case another valid HID
                // report updates unrelated fields. Motor fields remain untouched.
                bool heartbeatDue = now - lastWriteAt >= Stopwatch.Frequency / 50;
                if (frame == previous && !heartbeatDue)
                    continue;

                previous = frame;
                if (_dryRun)
                {
                    Interlocked.Increment(ref _framesWritten);
                    continue;
                }

                if (device is not null)
                {
                    if (device.TryWrite(frame))
                    {
                        Interlocked.Increment(ref _framesWritten);
                        lastWriteAt = now;
                    }
                    else
                    {
                        device.Dispose();
                        device = null;
                        Volatile.Write(ref _dualSenseConnected, 0);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            FailBackgroundThread("output", exception);
        }
        finally
        {
            // Best effort: never leave an active motor or adaptive-trigger
            // command latched after a normal stop or recoverable thread fault.
            device?.TryWrite(FeedbackFrame.Off);
            device?.Dispose();
        }
    }

    private void FailBackgroundThread(string threadName, Exception exception)
    {
        var diagnostic = new RuntimeDiagnosticException(
            $"RUNTIME_{threadName.Replace(' ', '_').ToUpperInvariant()}_FAILED",
            $"The {threadName} stopped unexpectedly: {exception.Message}",
            exception);
        if (Interlocked.CompareExchange(ref _backgroundFailure, diagnostic, null) is null && _useConsoleUi)
            ConsoleUi.Error($"{threadName} stopped", exception.Message);
        _running = false;
        _telemetryUpdated.Set();
    }

    public void Dispose()
    {
        _running = false;
        _telemetryUpdated.Set();
        _inputBridge?.Dispose();
        _inputBridge = null;
        _reader.Dispose();
        _capture?.Dispose();
        _telemetryUpdated.Dispose();
    }
}
