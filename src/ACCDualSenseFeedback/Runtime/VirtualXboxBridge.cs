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
    private volatile bool _running = true;
    private int _largeMotor;
    private int _smallMotor;

    public byte LargeMotor => (byte)Volatile.Read(ref _largeMotor);
    public byte SmallMotor => (byte)Volatile.Read(ref _smallMotor);

    public VirtualXboxBridge(Action feedbackChanged, Action<Exception> failed)
    {
        _feedbackChanged = feedbackChanged;
        _failed = failed;
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
        _inputThread.Start();
    }

    private void InputLoop()
    {
        IVirtualGamepad gamepad = (IVirtualGamepad)_virtualController;
        try
        {
            while (_running && _physical.TryRead(out DualSenseInputState input))
            {
                _virtualController.SetButtonsFull(input.Buttons);
                _virtualController.SetAxisValue(Xbox360Axis.LeftThumbX, input.LeftX);
                _virtualController.SetAxisValue(Xbox360Axis.LeftThumbY, input.LeftY);
                _virtualController.SetAxisValue(Xbox360Axis.RightThumbX, input.RightX);
                _virtualController.SetAxisValue(Xbox360Axis.RightThumbY, input.RightY);
                _virtualController.SetSliderValue(Xbox360Slider.LeftTrigger, input.LeftTrigger);
                _virtualController.SetSliderValue(Xbox360Slider.RightTrigger, input.RightTrigger);
                gamepad.SubmitReport();
            }
            if (_running)
                _failed(new IOException("The USB DualSense input stream disconnected."));
        }
        catch (Exception exception) when (_running)
        {
            _failed(exception);
        }
    }

    private void OnFeedbackReceived(object sender, Xbox360FeedbackReceivedEventArgs e)
    {
        Volatile.Write(ref _largeMotor, e.LargeMotor);
        Volatile.Write(ref _smallMotor, e.SmallMotor);
        _feedbackChanged();
    }

    public void Dispose()
    {
        _running = false;
        _physical.Dispose();
        if (_inputThread.IsAlive)
            _inputThread.Join(1000);

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
