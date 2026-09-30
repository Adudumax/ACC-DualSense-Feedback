using System.Diagnostics;
using System.Runtime;
using ACCDualSenseFeedback.Diagnostics;
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

        int snapshotIndex = Array.FindIndex(
            args, argument => argument.Equals("--snapshot-ui", StringComparison.OrdinalIgnoreCase));
        if (snapshotIndex >= 0)
        {
            string outputPath = snapshotIndex + 1 < args.Length
                ? args[snapshotIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "ui-snapshot.png");
            return UiSnapshot.Run(
                outputPath,
                showSettings: args.Contains("--settings", StringComparer.OrdinalIgnoreCase),
                showDiagnostics: args.Contains("--diagnostics", StringComparer.OrdinalIgnoreCase),
                showControllerError: args.Contains("--controller-error", StringComparer.OrdinalIgnoreCase));
        }

        int environmentReportIndex = Array.FindIndex(
            args, argument => argument.Equals("--environment-report", StringComparison.OrdinalIgnoreCase));
        if (environmentReportIndex >= 0)
        {
            string? outputPath = environmentReportIndex + 1 < args.Length &&
                !args[environmentReportIndex + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[environmentReportIndex + 1]
                    : null;
            return WriteEnvironmentReport(outputPath);
        }

        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
            return SelfTest.Run();

        int replayIndex = Array.FindIndex(
            args, argument => argument.Equals("--replay-telemetry", StringComparison.OrdinalIgnoreCase));
        if (replayIndex >= 0)
        {
            if (replayIndex + 1 >= args.Length)
            {
                Console.Error.WriteLine("Usage: --replay-telemetry <capture.csv> [--replay-output <output.csv>]");
                return 1;
            }
            int outputIndex = Array.FindIndex(
                args, argument => argument.Equals("--replay-output", StringComparison.OrdinalIgnoreCase));
            string? replayOutput = outputIndex >= 0 && outputIndex + 1 < args.Length
                ? args[outputIndex + 1]
                : null;
            return TelemetryReplay.Run(args[replayIndex + 1], replayOutput);
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
        string mode = dryRun ? "DRY RUN" : captureTelemetry ? "CAPTURE" : "LIVE";
        ConsoleUi.Initialize(mode, capturePath);
        if (dryRun)
            ConsoleUi.Notice("HID output disabled", "Telemetry and feedback calculations run without writing to the controller.");

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };

        using var runtime = new FeedbackRuntime(dryRun, capturePath);
        try
        {
            runtime.Run(stop.Token);
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
                "ACC DualSense Feedback is already running.",
                "ACC DualSense",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return 3;
        }

        TryConfigureProcess();
        return DesktopApplication.Run();
    }

    private static int WriteEnvironmentReport(string? outputPath)
    {
        string report = DiagnosticReportBuilder.Build(new DiagnosticReportContext(
            "environment-probe",
            "Environment probe",
            "Standalone release-preflight driver and device inspection.",
            false,
            false,
            false,
            false,
            null,
            FeedbackProfile.Default,
            null,
            null,
            Array.Empty<string>()));

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            Console.Write(report);
            return 0;
        }

        string fullPath = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(fullPath, report, new System.Text.UTF8Encoding(false));
        Console.WriteLine($"Environment report written to {fullPath}");
        return 0;
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
            TriggerEffect.FeedbackZones(3, 4, 4, 5, 5, 6, 6, 7, 7, 8),
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
