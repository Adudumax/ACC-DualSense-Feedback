using System.Diagnostics;
using ACCDualSenseFeedback.Diagnostics;
using ACCDualSenseFeedback.Gyro;
using ACCDualSenseFeedback.Hardware.DualSense;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace ACCDualSenseFeedback.Runtime;

internal sealed class VirtualXboxBridge : IDisposable
{
    private readonly Action _feedbackChanged;
    private readonly Action<Exception> _failed;
    private readonly DualSenseInputDevice _physical;
    private readonly ViGEmClient _client;
    private readonly IXbox360Controller _virtualController;
    private readonly Thread _inputThread;
    private readonly Timer _watchdog;
    private readonly object _reportLock = new();
    private readonly Func<GyroSteeringSettings> _gyroSettingsProvider;
    private readonly Func<GyroInputContext> _contextProvider;
    private readonly BrakeRawInputBuffer? _brakeRawInput;
    private volatile bool _running = true;
    private long _lastValidInputAt;
    private int _failureReported;
    private int _disposed;
    private int _largeMotor;
    private int _smallMotor;

    public byte LargeMotor => (byte)Volatile.Read(ref _largeMotor);
    public byte SmallMotor => (byte)Volatile.Read(ref _smallMotor);
    public DualSenseDeviceInfo DeviceInfo => _physical.Info;

    internal BrakeRawInput ReadBrakeRawInput(bool sameDevice)
        => _running && _brakeRawInput is not null ? _brakeRawInput.Read(sameDevice) : default;

    public VirtualXboxBridge(Action feedbackChanged, Action<Exception> failed,
        Func<GyroSteeringSettings>? gyroSettingsProvider = null,
        Func<GyroInputContext>? contextProvider = null,
        bool captureBrakeInput = false)
    {
        _feedbackChanged = feedbackChanged;
        _failed = failed;
        _brakeRawInput = captureBrakeInput ? new BrakeRawInputBuffer() : null;
        _gyroSettingsProvider = gyroSettingsProvider ?? (() => GyroSettingsState.Current);
        _contextProvider = contextProvider ?? (() => new(false, false, true, false, false,
            GyroButton.None, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency));
        _physical = DualSenseInputDevice.TryOpenUsb()
            ?? throw new RuntimeDiagnosticException(
                "DUALSENSE_USB_INPUT_NOT_VISIBLE",
                "No visible USB DualSense was found. Connect by USB and whitelist this EXE in HidHide.");

        try
        {
            _client = new ViGEmClient();
            _virtualController = _client.CreateXbox360Controller();
            ((IVirtualGamepad)_virtualController).AutoSubmitReport = false;
            _virtualController.FeedbackReceived += OnFeedbackReceived;
            ((IVirtualGamepad)_virtualController).Connect();
        }
        catch (Exception exception)
        {
            _physical.Dispose();
            throw new RuntimeDiagnosticException(
                "VIGEM_VIRTUAL_CONTROLLER_START_FAILED",
                "The ViGEm virtual Xbox 360 controller could not be started.",
                exception);
        }

        _inputThread = new Thread(InputLoop)
        {
            IsBackground = true,
            Name = "DualSense input bridge",
            Priority = ThreadPriority.Highest
        };
        _lastValidInputAt = Stopwatch.GetTimestamp();
        // Independent of ReadAsync and pose integration: a blocked read/native
        // call must not leave the virtual accelerator or turn latched forever.
        _watchdog = new Timer(CheckInputDeadline, null, 25, 25);
        _inputThread.Start();
    }

    private void InputLoop()
    {
        IVirtualGamepad gamepad = (IVirtualGamepad)_virtualController;
        try
        {
            using GyroSteeringProcessor? gyro = CreateGyroProcessor();
            bool gyroFailed = gyro is null;
            while (_running && _physical.TryRead(out DualSenseInputState input, out GyroSensorSample sensor))
            {
                long observedAt = Stopwatch.GetTimestamp();
                Interlocked.Exchange(ref _lastValidInputAt, observedAt);
                _brakeRawInput?.Publish(input.LeftTrigger, input.RightTrigger, observedAt);
                short finalX = input.LeftX;
                GyroButton consumed = GyroButton.None;
                if (!gyroFailed && gyro is not null)
                {
                    try
                    {
                        GyroInputContext context = _contextProvider() with
                        {
                            FactoryCalibrated = _physical.Calibration is not null,
                            Buttons = input.MotionButtons,
                            MonotonicSeconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency
                        };
                        float physicalX = NormalizeAxis(input.LeftX);
                        GyroProcessResult result = gyro.Process(sensor, physicalX, context);
                        // Preserve all raw axis bits whenever gyro returned the
                        // physical source, including its asymmetric endpoints.
                        if (result.Status.InputSource is not (GyroInputSource.PhysicalStick or GyroInputSource.StickTakeover))
                            finalX = QuantizeAxis(result.OutputX);
                        consumed = result.ConsumedButtons;
                    }
                    catch (Exception exception)
                    {
                        // Algorithm/loader failures disable this connection's
                        // gyro only. Ordinary controls and feedback keep working.
                        gyroFailed = true;
                        GyroControlState.Publish(GyroControlState.Current with
                        {
                            State = GyroRuntimeState.SensorInvalid,
                            Message = $"Gyro unavailable: {exception.GetType().Name}",
                            Ready = false, IsCalibrating = false, ImuValid = false,
                            SteeringPreview = default,
                            InputSource = GyroInputSource.PhysicalStick, OutputX = NormalizeAxis(input.LeftX)
                        });
                    }
                }
                if (gyroFailed)
                {
                    GyroControlState.Publish(GyroControlState.Current with
                    {
                        PhysicalX = NormalizeAxis(input.LeftX), OutputX = NormalizeAxis(input.LeftX),
                        InputValid = true, ImuValid = false, FactoryCalibrated = _physical.Calibration is not null,
                        IsCalibrating = false, Ready = false, NeedsRecenter = true,
                        SteeringPreview = default,
                        InputSource = GyroInputSource.PhysicalStick
                    });
                }
                lock (_reportLock)
                {
                    if (!_running) break;
                    _virtualController.SetButtonsFull(input.ButtonsWithout(consumed));
                    _virtualController.SetAxisValue(Xbox360Axis.LeftThumbX, finalX);
                    _virtualController.SetAxisValue(Xbox360Axis.LeftThumbY, input.LeftY);
                    _virtualController.SetAxisValue(Xbox360Axis.RightThumbX, input.RightX);
                    _virtualController.SetAxisValue(Xbox360Axis.RightThumbY, input.RightY);
                    _virtualController.SetSliderValue(Xbox360Slider.LeftTrigger, input.LeftTriggerWithout(consumed));
                    _virtualController.SetSliderValue(Xbox360Slider.RightTrigger, input.RightTriggerWithout(consumed));
                    gamepad.SubmitReport();
                }
            }
            if (_running)
                StopInput(new IOException("The USB DualSense input stream stopped or exceeded its read deadline."));
        }
        catch (Exception exception)
        {
            if (_running) StopInput(exception);
        }
        finally
        {
            // Process can finish after the watchdog already cleared the
            // controls and published a stopped snapshot. Its final publication
            // must not leave Active/Calibrating latched after this thread exits.
            PublishStoppedGyro(Volatile.Read(ref _failureReported) != 0);
        }
    }

    private GyroSteeringProcessor? CreateGyroProcessor()
    {
        GyroSteeringProcessor? gyro = null;
        try
        {
            gyro = new GyroSteeringProcessor(_gyroSettingsProvider);
            gyro.ResetConnection(_physical.Info.Identity);
            return gyro;
        }
        catch (Exception exception)
        {
            try { gyro?.Dispose(); } catch { }
            GyroControlState.Publish(GyroRuntimeStatus.Initial with
            {
                State = GyroRuntimeState.SensorInvalid,
                Message = $"Gyro initialization unavailable: {exception.GetType().Name}",
                DeviceIdentity = _physical.Info.Identity, Ready = false, ImuValid = false
            });
            return null;
        }
    }

    internal static float NormalizeAxis(short value) => value < 0 ? value / 32768f : value / 32767f;
    internal static short QuantizeAxis(float value)
    {
        if (!float.IsFinite(value)) return 0;
        value = Math.Clamp(value, -1, 1);
        return (short)Math.Clamp((int)MathF.Round(value * (value < 0 ? 32768 : 32767)), short.MinValue, short.MaxValue);
    }

    private void CheckInputDeadline(object? _)
    {
        if (_running && Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastValidInputAt) >
            Stopwatch.Frequency * DualSenseInputDevice.ReadDeadlineMilliseconds / 1000)
            StopInput(new TimeoutException("The USB DualSense input deadline expired; virtual controls were cleared."));
    }

    private void StopInput(Exception exception)
    {
        if (Interlocked.Exchange(ref _failureReported, 1) != 0) return;
        lock (_reportLock)
        {
            if (!_running) return;
            _running = false;
            ClearVirtualControls();
        }
        _physical.CancelRead();
        Volatile.Write(ref _largeMotor, 0);
        Volatile.Write(ref _smallMotor, 0);
        PublishStoppedGyro(inputFailed: true);
        _failed(exception);
    }

    internal static void PublishStoppedGyro(bool inputFailed)
    {
        GyroControlState.Publish(GyroControlState.Current with
        {
            State = inputFailed ? GyroRuntimeState.SensorInvalid : GyroRuntimeState.Disabled,
            Message = inputFailed ? "USB input disconnected" : "Gyro stopped",
            InputValid = false, ImuValid = false, Ready = false, NeedsRecenter = true,
            IsCalibrating = false, CalibrationProgress = 0, DeviceDeltaSeconds = 0,
            AngleDegrees = 0, PhysicalX = 0, GyroX = 0, OutputX = 0,
            SteeringPreview = default,
            InputSource = GyroInputSource.PhysicalStick,
            PendingBindingTarget = GyroBindingTarget.None
        });
    }

    private void ClearVirtualControls()
    {
        try
        {
            _virtualController.SetButtonsFull(0);
            _virtualController.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
            _virtualController.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
            _virtualController.SetAxisValue(Xbox360Axis.RightThumbX, 0);
            _virtualController.SetAxisValue(Xbox360Axis.RightThumbY, 0);
            _virtualController.SetSliderValue(Xbox360Slider.LeftTrigger, 0);
            _virtualController.SetSliderValue(Xbox360Slider.RightTrigger, 0);
            ((IVirtualGamepad)_virtualController).SubmitReport();
        }
        catch { /* A disconnected bus is already neutral to the game. */ }
    }

    private void OnFeedbackReceived(object sender, Xbox360FeedbackReceivedEventArgs e)
    {
        Volatile.Write(ref _largeMotor, e.LargeMotor);
        Volatile.Write(ref _smallMotor, e.SmallMotor);
        _feedbackChanged();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _watchdog.Dispose();
        lock (_reportLock)
        {
            _running = false;
            ClearVirtualControls();
        }
        _physical.Dispose();
        if (_inputThread.IsAlive)
            _inputThread.Join(1000);
        // Normal cancellation does not pass through StopInput. Also overwrite
        // any last in-flight processor publication before the bridge is removed.
        PublishStoppedGyro(Volatile.Read(ref _failureReported) != 0);

        Volatile.Write(ref _largeMotor, 0);
        Volatile.Write(ref _smallMotor, 0);
        _virtualController.FeedbackReceived -= OnFeedbackReceived;
        try
        {
            ((IVirtualGamepad)_virtualController).Disconnect();
        }
        catch
        {
        }
        _client.Dispose();
    }
}
