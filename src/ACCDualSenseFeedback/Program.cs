using System.Diagnostics;
using System.Runtime;
using ACCDualSenseFeedback.Diagnostics;
using ACCDualSenseFeedback.Gyro;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Hardware.DualSense;
using ACCDualSenseFeedback.Runtime;
using ACCDualSenseFeedback.Telemetry;
using ACCDualSenseFeedback.Ui;

namespace ACCDualSenseFeedback;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        GyroSettingsState.Current = PortableGyroSettingsStore.Load();
        if (args.Length == 0 || args.Contains("--gui", StringComparer.OrdinalIgnoreCase))
            return RunDesktop();

        NativeConsole.TryAttachParent();
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
            // Windows GUI executables do not always inherit a usable console handle.
        }

        if (args.Contains("--list-devices", StringComparer.OrdinalIgnoreCase))
            return ListDevices();

        if (args.Contains("--test-feedback", StringComparer.OrdinalIgnoreCase))
            return TestFeedback();

        if (args.Contains("--test-actuators", StringComparer.OrdinalIgnoreCase))
            return TestActuators();

        if (args.Contains("--test-xinput-rumble", StringComparer.OrdinalIgnoreCase))
            return XInputRumbleTest.Run();

        using var singleInstance = new Mutex(
            initiallyOwned: true,
            name: @"Local\ACCDualSenseFeedback.SingleInstance",
            createdNew: out bool isFirstInstance);
        if (!isFirstInstance)
        {
            Console.Error.WriteLine("ACC DualSense Feedback is already running. Close the existing normal or capture instance first.");
            return 3;
        }

        bool dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
        bool captureTelemetry = args.Contains("--capture-telemetry", StringComparer.OrdinalIgnoreCase);
        bool captureBrake = args.Contains("--capture-brake", StringComparer.OrdinalIgnoreCase);
        if (captureBrake && captureTelemetry)
        {
            Console.Error.WriteLine("Choose brake support capture or full telemetry capture for this run.");
            return 2;
        }
        if (!dryRun && Process.GetProcessesByName("DS4Windows").Length != 0)
        {
            Console.Error.WriteLine("DS4Windows is running. Exit it before starting this app to avoid duplicate virtual controllers and competing output reports.");
            return 2;
        }

        TryConfigureProcess();
        using var timerResolution = WindowsTimerResolution.Request1Millisecond();

        string? capturePath = captureTelemetry
            ? Path.Combine(AppContext.BaseDirectory, $"ACC-telemetry-{DateTime.Now:yyyyMMdd-HHmmss}.csv")
            : null;
        string mode = dryRun ? "DRY RUN" : captureBrake ? "BRAKE CAPTURE" : captureTelemetry ? "CAPTURE" : "LIVE";
        ConsoleUi.Initialize(mode, capturePath);
        if (dryRun)
            ConsoleUi.Notice("HID output disabled", "Telemetry and feedback calculations run without writing to the controller.");

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };

        FeedbackProfile consoleProfile = PortableProfileStore.Load(out _);
        var consoleLights = PortableLightSettingsStore.Load(out _);
        BrakeSupportCapture? brakeCapture = null;
        try
        {
            if (captureBrake)
            {
                string brakeDirectory = Path.Combine(AppContext.BaseDirectory, "BrakeSupportRecords",
                    $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}");
                brakeCapture = new BrakeSupportCapture(brakeDirectory);
                ConsoleUi.Notice("Brake support capture",
                    $"Drive normally and brake several times, then press Ctrl+C. Settings are unchanged. Records: {brakeDirectory}");
            }
            using var runtime = new FeedbackRuntime(dryRun, capturePath,
                tuningProvider: () => consoleProfile.ToTuning(),
                lightingProvider: () => consoleLights,
                customPresetProvider: () => consoleProfile.ActivePreset == FeedbackPreset.Custom,
                gyroSettingsProvider: () => GyroSettingsState.Current,
                brakeSupportCapture: brakeCapture);
            runtime.Run(stop.Token);
            brakeCapture?.Dispose();
            if (brakeCapture is not null)
            {
                if (!brakeCapture.Completed || brakeCapture.Failure is not null)
                {
                    ConsoleUi.Error("Brake capture incomplete",
                        $"{brakeCapture.Failure?.Message ?? "The writer did not finish."} Records: {brakeCapture.DirectoryPath}");
                    return 5;
                }
                ConsoleUi.Ready("Brake report saved", brakeCapture.DirectoryPath);
            }
            if (runtime.BackgroundFailure is null)
                ConsoleUi.Ready("Stopped safely", "Motors and adaptive triggers were cleared.");
            return runtime.BackgroundFailure is null ? 0 : 4;
        }
        catch (Exception exception)
        {
            ConsoleUi.Error(
                "Startup failed",
                $"{exception.Message} Check USB, the HidHide application list and ViGEmBus.");
            return 4;
        }
        finally
        {
            brakeCapture?.Dispose();
        }
    }

    private static int RunDesktop()
    {
        using var singleInstance = new Mutex(
            initiallyOwned: true,
            name: @"Local\ACCDualSenseFeedback.SingleInstance",
            createdNew: out bool isFirstInstance);
        if (!isFirstInstance)
        {
            System.Windows.MessageBox.Show(
                UiText.Get("ACC DualSense Feedback is already running."),
                "ACC DualSense",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return 3;
        }

        TryConfigureProcess();
        return DesktopApplication.Run();
    }

    private static int ListDevices()
    {
        var devices = DualSenseHidDevice.Enumerate();
        if (devices.Count == 0)
        {
            Console.WriteLine("No visible DualSense device found.");
            Console.WriteLine("If HidHide is active, whitelist ACCDualSenseFeedback.exe first.");
            return 1;
        }

        foreach (var device in devices)
            Console.WriteLine($"{device.ProductId:X4}  {device.Transport,-9}  in={device.InputReportLength,3} out={device.OutputReportLength,3}  {device.Path}");
        return 0;
    }

    private static int TestFeedback()
    {
        using DualSenseHidDevice? device = DualSenseHidDevice.TryOpenFirst();
        if (device is null)
        {
            Console.Error.WriteLine("No writable DualSense device found.");
            return 1;
        }

        Console.WriteLine($"Testing {device.Info.Transport} progressive pedal curves for 1.2 seconds...");
        var frame = new FeedbackFrame(
            0,
            0,
            BrakePedalFeedback.CreateResistance(1f),
            TriggerEffect.FeedbackZones(2, 2, 2, 3, 3, 3, 4, 4, 5, 5));

        if (!device.TryWrite(frame))
        {
            Console.Error.WriteLine("The controller rejected the feedback report.");
            return 1;
        }

        Thread.Sleep(1200);
        device.TryWrite(FeedbackFrame.Off);
        Console.WriteLine("Feedback test completed and cleared.");
        return 0;
    }

    private static int TestActuators()
    {
        using DualSenseHidDevice? device = DualSenseHidDevice.TryOpenFirst();
        if (device is null)
        {
            Console.Error.WriteLine("No writable DualSense device found.");
            return 1;
        }

        Console.WriteLine("Actuator test: LEFT for 1.2 s, pause, then RIGHT for 1.2 s.");
        if (!device.TryWrite(new FeedbackFrame(92, 0, TriggerEffect.Off, TriggerEffect.Off)))
            return 1;
        Thread.Sleep(1200);
        device.TryWrite(FeedbackFrame.Off);
        Thread.Sleep(500);
        if (!device.TryWrite(new FeedbackFrame(0, 92, TriggerEffect.Off, TriggerEffect.Off)))
            return 1;
        Thread.Sleep(1200);
        device.TryWrite(FeedbackFrame.Off);
        Console.WriteLine("Actuator test completed and cleared.");
        return 0;
    }

    private static void TryConfigureProcess()
    {
        try
        {
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch
        {
            // Performance hints are best-effort and never required for correctness.
        }
    }
}
