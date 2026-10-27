using System.Diagnostics;
using ACCDualSenseFeedback.Diagnostics;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Hardware.DualSense;
using ACCDualSenseFeedback.Telemetry;
using ACCDualSenseFeedback.Ui;
using ACCDualSenseFeedback.Lighting;
using ACCDualSenseFeedback.Gyro;

namespace ACCDualSenseFeedback.Runtime;

internal sealed class FeedbackRuntime : IDisposable
{
    private static readonly long StaleTicks = (long)(0.120 * Stopwatch.Frequency);
    private readonly bool _dryRun;
    private readonly bool _useConsoleUi;
    private readonly Action<FeedbackRuntimeStatus>? _statusSink;
    private readonly Func<FeedbackTuning>? _tuningProvider;
    private readonly Func<RpmLightSettings> _lightingProvider;
    private readonly Func<bool> _customPresetProvider;
    private readonly Func<GyroSteeringSettings> _gyroSettingsProvider;
    private readonly StartupTriggerConfirmation _startupConfirmation;
    private readonly TelemetryCsvCapture? _capture;
    private readonly BrakeSupportCapture? _brakeSupportCapture;
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
    private int _accConnected;
    private int _accProbeCompleted;
    private int _accProcessId;
    private int _accLive;
    private Exception? _backgroundFailure;

    public Exception? BackgroundFailure => Volatile.Read(ref _backgroundFailure);

    public FeedbackRuntime(
        bool dryRun,
        string? capturePath = null,
        Action<FeedbackRuntimeStatus>? statusSink = null,
        bool useConsoleUi = true,
        Func<FeedbackTuning>? tuningProvider = null,
        Func<RpmLightSettings>? lightingProvider = null,
        StartupTriggerConfirmation? startupConfirmation = null,
        Func<bool>? customPresetProvider = null,
        Func<GyroSteeringSettings>? gyroSettingsProvider = null,
        BrakeSupportCapture? brakeSupportCapture = null)
    {
        _dryRun = dryRun;
        _useConsoleUi = useConsoleUi;
        _statusSink = statusSink;
        _brakeSupportCapture = brakeSupportCapture;
        FeedbackProfile initialProfile = PortableProfileStore.Load(out _);
        _customPresetProvider = customPresetProvider ?? (() => initialProfile.ActivePreset == FeedbackPreset.Custom);
        Func<FeedbackTuning> requestedTuning = tuningProvider ?? (() => initialProfile.ToTuning());
        _tuningProvider = () => _customPresetProvider() ? requestedTuning() : FeedbackTuning.Neutral;
        RpmLightSettings initialLights = PortableLightSettingsStore.Load(out _);
        _lightingProvider = lightingProvider ?? (() => initialLights);
        GyroSteeringSettings initialGyro = PortableGyroSettingsStore.Load(out _);
        _gyroSettingsProvider = gyroSettingsProvider ?? (() => initialGyro);
        _startupConfirmation = startupConfirmation ?? StartupTriggerConfirmation.ForProcess;
        _capture = capturePath is null ? null : new TelemetryCsvCapture(capturePath);
    }

    public void Run(CancellationToken cancellationToken)
    {
        _running = true;
        if (!_dryRun)
        {
            _inputBridge = new VirtualXboxBridge(
                () => _telemetryUpdated.Set(),
                exception => FailBackgroundThread("input bridge", exception),
                _gyroSettingsProvider,
                ReadGyroContext,
                captureBrakeInput: _brakeSupportCapture is not null);
            if (_useConsoleUi)
                ConsoleUi.Ready("Controller input", "USB DualSense is bridged to the virtual Xbox 360 controller.");
        }

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
                bool accConnected = Volatile.Read(ref _accConnected) != 0;
                bool dualSenseConnected = Volatile.Read(ref _dualSenseConnected) != 0;
                bool telemetryStale = status.ObservedTimestamp == 0 || now - status.ObservedTimestamp > StaleTicks;

                if (_useConsoleUi && accConnected && !previousAccConnection)
                    ConsoleUi.Ready("ACC detected", "Game process is running. Waiting for fresh shared-memory telemetry.");
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
                    rightTriggerMode,
                    GyroControlState.Current));
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
            using var processMonitor = new AccProcessMonitor();
            var session = new AccTelemetrySession();
            long lastPacketAt = Stopwatch.GetTimestamp();
            while (_running)
            {
                long now = Stopwatch.GetTimestamp();
                int processId = processMonitor.GetProcessId(now);
                Volatile.Write(ref _accProcessId, processId);
                if (session.SetProcess(processId))
                {
                    _reader.Disconnect();
                    _latest.Publish(default);
                    _telemetryUpdated.Set();
                }
                Volatile.Write(ref _accConnected, session.IsGameRunning ? 1 : 0);
                Volatile.Write(ref _accProbeCompleted, 1);
                if (!session.IsGameRunning)
                {
                    Thread.Sleep(250);
                    lastPacketAt = Stopwatch.GetTimestamp();
                    continue;
                }

                if (!_reader.IsConnected)
                {
                    if (!_reader.TryConnect())
                    {
                        Thread.Sleep(250);
                        lastPacketAt = Stopwatch.GetTimestamp();
                        continue;
                    }
                    session.ResetMapping();
                    lastPacketAt = Stopwatch.GetTimestamp();
                }

                if (_reader.TryReadLatest(out TelemetrySnapshot snapshot) && session.TryAccept(snapshot))
                {
                    lastPacketAt = snapshot.ObservedTimestamp;
                    _latest.Publish(snapshot);
                    Interlocked.Increment(ref _framesRead);
                    _telemetryUpdated.Set();
                }
                else
                {
                    now = Stopwatch.GetTimestamp();
                    if (now - lastPacketAt > 2 * Stopwatch.Frequency)
                    {
                        _reader.Disconnect();
                        session.ResetMapping();
                        _latest.Publish(default);
                        _telemetryUpdated.Set();
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
        var shiftCue = new AccShiftCue();
        var lightsEngine = new RpmLightEngine();
        using var lightMetadata = new AccLightingMetadataReader();
        using var gyroContext = new AccGyroContextReader();
        RpmLightFrame lights = RpmLightFrame.Off;
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
                    device = _inputBridge is null ? null : DualSenseHidDevice.TryOpen(_inputBridge.DeviceInfo);
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
                if (Volatile.Read(ref _accConnected) != 0) gyroContext.Refresh(now);
                else gyroContext.Disconnect();
                Volatile.Write(ref _accLive, !stale && gyroContext.IsLive(now) ? 1 : 0);
                byte originalLarge = _inputBridge?.LargeMotor ?? 0;
                byte originalSmall = _inputBridge?.SmallMotor ?? 0;
                FeedbackTuning tuning = _tuningProvider?.Invoke() ?? FeedbackTuning.Neutral;
                AccShiftCueState shift = default;
                if (stale) shiftCue.Reset();
                else
                {
                    lightMetadata.Refresh(now);
                    shift = shiftCue.Update(snapshot, lightMetadata.CarModel, lightMetadata.MaxRpm, tuning,
                        lightMetadata.Context.IsPaused, lightMetadata.Context.IsReplay);
                }
                FeedbackFrame frame;
                if (stale)
                {
                    frame = FeedbackFrame.Off;
                }
                else
                {
                    frame = engine.Compute(snapshot, now, originalLarge, originalSmall, tuning, shift);
                }
                bool calibration = GyroControlState.Current.SuppressFeedback;
                if (calibration) frame = FeedbackFrame.Off;
                if (!stale)
                    _capture?.TryWrite(snapshot, originalLarge, originalSmall, frame, GyroControlState.Current);
                if (!_dryRun)
                    frame = _startupConfirmation.Apply(frame, now, device is not null,
                        Volatile.Read(ref _accProbeCompleted) != 0,
                        Volatile.Read(ref _accConnected) != 0, !stale, _tuningProvider);
                // Calibration owns no HID output. It only gates this writer,
                // including the startup trigger action, for the static sample.
                if (calibration) frame = FeedbackFrame.Off;
                Volatile.Write(ref _leftTriggerMode, frame.LeftTrigger.Mode);
                Volatile.Write(ref _rightTriggerMode, frame.RightTrigger.Mode);
                // Preserve normal haptic send decisions and the 50 Hz heartbeat.
                // The startup action uses this same writer; its final Off frame
                // is a normal frame change and releases resistance immediately.
                bool heartbeatDue = now - lastWriteAt >= Stopwatch.Frequency / 50;
                if (frame == previous && !heartbeatDue)
                {
                    RecordBrakeWrite(now, snapshot, frame, tuning, device, BrakeHidWriteState.Skipped);
                    continue;
                }

                bool changedFrame = frame != previous;
                previous = frame;
                // Evaluate lighting on existing output reports, without adding
                // HID writes or changing haptic inputs, frame or wake timing.
                if (stale)
                {
                    lightsEngine.Reset();
                    lightMetadata.Disconnect();
                    lights = RpmLightFrame.Off;
                }
                else
                {
                    RpmLightSettings effectiveLights = _customPresetProvider() ? _lightingProvider() : RpmLightSettings.Default;
                    lights = lightsEngine.Compute(snapshot, now, effectiveLights, lightMetadata.MaxRpm,
                        lightMetadata.Context, shift);
                }
                if (calibration) lights = RpmLightFrame.Off;
                if (_dryRun)
                {
                    Interlocked.Increment(ref _framesWritten);
                    RecordBrakeWrite(now, snapshot, frame, tuning, device, BrakeHidWriteState.DryRun);
                    continue;
                }

                if (device is not null)
                {
                    if (device.TryWrite(frame, lights))
                    {
                        Interlocked.Increment(ref _framesWritten);
                        lastWriteAt = now;
                        RecordBrakeWrite(now, snapshot, frame, tuning, device,
                            changedFrame ? BrakeHidWriteState.ChangedSuccess : BrakeHidWriteState.HeartbeatSuccess);
                    }
                    else
                    {
                        RecordBrakeWrite(now, snapshot, frame, tuning, device, BrakeHidWriteState.Failed);
                        _startupConfirmation.Cancel();
                        device.Dispose();
                        device = null;
                        Volatile.Write(ref _dualSenseConnected, 0);
                    }
                }
                else RecordBrakeWrite(now, snapshot, frame, tuning, device, BrakeHidWriteState.NoDevice);
            }
        }
        catch (Exception exception)
        {
            FailBackgroundThread("output", exception);
        }
        finally
        {
            if (!_dryRun) _startupConfirmation.Cancel();
            // Best effort: never leave an active motor or adaptive-trigger
            // command latched after a normal stop or recoverable thread fault.
            bool? cleared = device?.TryWrite(FeedbackFrame.Off);
            RecordBrakeWrite(Stopwatch.GetTimestamp(), default, FeedbackFrame.Off, FeedbackTuning.Neutral, device,
                cleared == false ? BrakeHidWriteState.Failed : BrakeHidWriteState.Stopped);
            device?.Dispose();
        }
    }

    private void RecordBrakeWrite(long now, in TelemetrySnapshot telemetry, in FeedbackFrame frame,
        in FeedbackTuning tuning, DualSenseHidDevice? device, BrakeHidWriteState writeState)
    {
        if (_brakeSupportCapture is null) return;
        bool sameDevice = device is not null && _inputBridge is not null &&
            string.Equals(device.Info.Path, _inputBridge.DeviceInfo.Path, StringComparison.OrdinalIgnoreCase);
        BrakeRawInput raw = _inputBridge?.ReadBrakeRawInput(sameDevice) ?? default;
        // Input can advance while the frame is computed/written. Timestamp the
        // observation after reading raw input, rather than making it look future-dated.
        long recordedAt = Math.Max(now, Stopwatch.GetTimestamp());
        _brakeSupportCapture.Record(new(recordedAt, telemetry, frame, tuning, raw, writeState));
    }

    private GyroInputContext ReadGyroContext()
    {
        long now = Stopwatch.GetTimestamp();
        TelemetrySnapshot snapshot = _latest.Read();
        bool fresh = snapshot.ObservedTimestamp != 0 && now - snapshot.ObservedTimestamp <= StaleTicks;
        bool live = fresh && Volatile.Read(ref _accLive) != 0 && Volatile.Read(ref _accConnected) != 0;
        return new(_customPresetProvider(), live, !live,
            AccForegroundWindow.IsForeground(Volatile.Read(ref _accProcessId)), false,
            GyroButton.None, now / (double)Stopwatch.Frequency);
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
