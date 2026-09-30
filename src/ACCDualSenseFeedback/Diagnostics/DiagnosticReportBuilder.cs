using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Hardware.DualSense;
using ACCDualSenseFeedback.Runtime;
using Microsoft.Win32;

namespace ACCDualSenseFeedback.Diagnostics;

internal sealed record DiagnosticReportContext(
    string UiState,
    string Status,
    string StatusDetail,
    bool ControllerConnected,
    bool VirtualControllerConnected,
    bool AccConnected,
    bool FeedbackLive,
    FeedbackRuntimeStatus? RuntimeStatus,
    FeedbackProfile Profile,
    Exception? LastError,
    DateTimeOffset? LastErrorAt,
    IReadOnlyCollection<string> SessionEvents);

internal static partial class DiagnosticReportBuilder
{
    private const string ViGEmServiceKey = @"SYSTEM\CurrentControlSet\Services\ViGEmBus";
    private const string HidHideServiceKey = @"SYSTEM\CurrentControlSet\Services\HidHide";
    private const string HidHideProductKey = @"SOFTWARE\Nefarius Software Solutions e.U.\HidHide";

    public static string Build(DiagnosticReportContext context)
    {
        var report = new StringBuilder(4096);
        AppendHeader(report);
        AppendCurrentState(report, context);
        AppendRuntimeStatus(report, context.RuntimeStatus);
        AppendDriverStatus(report);
        AppendDualSenseStatus(report);
        AppendProfile(report, context.Profile);
        AppendException(report, context.LastError, context.LastErrorAt);
        AppendEvents(report, context.SessionEvents);
        return report.ToString();
    }

    private static void AppendHeader(StringBuilder report)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        report.AppendLine("ACC DualSense Feedback diagnostic report");
        report.AppendLine("Report format: 2");
        report.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        report.AppendLine($"App version: {version}");
        report.AppendLine($"Executable: {Path.GetFileName(Environment.ProcessPath) ?? "unknown"}");
        report.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        report.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        report.AppendLine($"Architecture: process={RuntimeInformation.ProcessArchitecture}; OS={RuntimeInformation.OSArchitecture}");
        report.AppendLine($"Process uptime: {FormatDuration(DateTime.Now - Process.GetCurrentProcess().StartTime)}");
    }

    private static void AppendCurrentState(StringBuilder report, DiagnosticReportContext context)
    {
        report.AppendLine();
        report.AppendLine("[Current state]");
        report.AppendLine($"UI state code: {context.UiState}");
        report.AppendLine($"Status: {context.Status}");
        report.AppendLine($"Detail: {context.StatusDetail}");
        report.AppendLine($"DualSense connected: {context.ControllerConnected}");
        report.AppendLine($"Virtual controller connected: {context.VirtualControllerConnected}");
        report.AppendLine($"ACC connected: {context.AccConnected}");
        report.AppendLine($"Feedback live: {context.FeedbackLive}");
        report.AppendLine($"DS4Windows processes: {SafeProcessCount("DS4Windows")}");
    }

    private static void AppendRuntimeStatus(StringBuilder report, FeedbackRuntimeStatus? status)
    {
        report.AppendLine();
        report.AppendLine("[Runtime]");
        if (status is null)
        {
            report.AppendLine("Last runtime sample: unavailable");
            return;
        }

        FeedbackRuntimeStatus value = status.Value;
        double telemetryAgeMs = value.Telemetry.ObservedTimestamp == 0
            ? double.NaN
            : Stopwatch.GetElapsedTime(value.Telemetry.ObservedTimestamp).TotalMilliseconds;
        report.AppendLine($"ACC shared memory: {(value.AccConnected ? "connected" : "not connected")}");
        report.AppendLine($"Telemetry stale: {value.TelemetryStale}");
        report.AppendLine($"Telemetry packet: {value.Telemetry.PacketId}");
        report.AppendLine($"Telemetry age ms: {(double.IsNaN(telemetryAgeMs) ? "never observed" : telemetryAgeMs.ToString("F1"))}");
        report.AppendLine($"Input bridge: {(value.InputBridgeConnected ? "created" : "not created")}");
        report.AppendLine($"Frames read/written: {value.FramesRead}/{value.FramesWritten}");
        report.AppendLine($"Native rumble large/small: {value.NativeLargeMotor}/{value.NativeSmallMotor}");
        report.AppendLine($"Trigger modes L/R: 0x{value.LeftTriggerMode:X2}/0x{value.RightTriggerMode:X2}");
    }

    private static void AppendDriverStatus(StringBuilder report)
    {
        report.AppendLine();
        report.AppendLine("[Driver checks]");
        AppendServiceRegistration(report, "ViGEmBus", ViGEmServiceKey);
        AppendServiceRegistration(report, "HidHide", HidHideServiceKey);

        HidHideProbe probe = ProbeHidHide();
        report.AppendLine($"HidHide product version: {probe.Version}");
        report.AppendLine($"HidHide CLI: {probe.CliState}");
        report.AppendLine($"HidHide cloaking: {probe.CloakingState}");
        report.AppendLine($"HidHide inverse app list: {probe.InverseState}");
        report.AppendLine($"This executable listed in HidHide applications: {probe.CurrentAppListed}");
        report.AppendLine($"HidHide hidden-device entries: {probe.HiddenDeviceCount}");
    }

    private static void AppendServiceRegistration(StringBuilder report, string name, string registryPath)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(registryPath);
            if (key is null)
            {
                report.AppendLine($"{name} service registration: missing");
                return;
            }

            object? start = key.GetValue("Start");
            string imagePath = key.GetValue("ImagePath") as string ?? "unknown";
            string fileState = ResolveDriverPath(imagePath) is { } driverPath
                ? File.Exists(driverPath).ToString()
                : "unknown";
            report.AppendLine($"{name} service registration: present; start={start ?? "unknown"}; driver file present={fileState}");
        }
        catch (Exception exception)
        {
            report.AppendLine($"{name} service registration: probe failed ({exception.GetType().Name}, 0x{exception.HResult:X8})");
        }
    }

    private static void AppendDualSenseStatus(StringBuilder report)
    {
        report.AppendLine();
        report.AppendLine("[DualSense HID probe]");
        try
        {
            List<DualSenseDeviceInfo> devices = DualSenseHidDevice.Enumerate();
            report.AppendLine($"Visible and openable gamepad interfaces: {devices.Count}");
            for (int index = 0; index < devices.Count; index++)
            {
                DualSenseDeviceInfo device = devices[index];
                report.AppendLine(
                    $"Device {index + 1}: PID=0x{device.ProductId:X4}; transport={device.Transport}; " +
                    $"input report={device.InputReportLength}; output report={device.OutputReportLength}");
            }
        }
        catch (Exception exception)
        {
            report.AppendLine($"Probe failed: {exception.GetType().FullName}; HRESULT=0x{exception.HResult:X8}; {exception.Message}");
        }
    }

    private static void AppendProfile(StringBuilder report, FeedbackProfile profile)
    {
        report.AppendLine();
        report.AppendLine("[Feedback profile]");
        report.AppendLine($"Preset: {profile.ActivePreset}");
        report.AppendLine($"BrakeResistance={profile.BrakeResistance}; ThrottleResistance={profile.ThrottleResistance}");
        report.AppendLine($"AbsPulse={profile.AbsPulse}; LockPulse={profile.LockPulse}");
        report.AppendLine($"ShiftKick={profile.ShiftKick}; RedlinePulse={profile.RedlinePulse}; RedlineStartPermille={profile.RedlineStartPermille}");
        report.AppendLine($"EngineTexture={profile.EngineTexture}");
        report.AppendLine($"TcPulse={profile.TcPulse}; WheelspinPulse={profile.WheelspinPulse}");
        report.AppendLine($"RoadSurface={profile.RoadSurface}; GripLoss={profile.GripLoss}; NativeAcc={profile.NativeAcc}");
        report.AppendLine($"CollisionFeedback={profile.CollisionFeedback}");
    }

    private static void AppendException(StringBuilder report, Exception? exception, DateTimeOffset? occurredAt)
    {
        report.AppendLine();
        report.AppendLine("[Last runtime error]");
        if (exception is null)
        {
            report.AppendLine("None recorded in this process.");
            return;
        }

        report.AppendLine($"Occurred: {(occurredAt is null ? "unknown" : occurredAt.Value.ToString("O"))}");
        int depth = 0;
        for (Exception? current = exception; current is not null && depth < 8; current = current.InnerException, depth++)
        {
            report.AppendLine($"Exception {depth}: {current.GetType().FullName}");
            if (current is RuntimeDiagnosticException diagnostic)
                report.AppendLine($"Diagnostic code: {diagnostic.DiagnosticCode}");
            report.AppendLine($"HRESULT: 0x{current.HResult:X8}");
            report.AppendLine($"Message: {current.Message}");

            string stack = new StackTrace(current, false).ToString().Trim();
            if (stack.Length != 0)
                report.AppendLine($"Call stack:{Environment.NewLine}{stack}");
        }
    }

    private static void AppendEvents(StringBuilder report, IReadOnlyCollection<string> events)
    {
        report.AppendLine();
        report.AppendLine("[Session events]");
        if (events.Count == 0)
        {
            report.AppendLine("No events recorded.");
            return;
        }

        foreach (string item in events)
            report.AppendLine(item);
    }

    private static HidHideProbe ProbeHidHide()
    {
        string version = "not installed";
        string? installPath = null;
        try
        {
            using RegistryKey? product = Registry.LocalMachine.OpenSubKey(HidHideProductKey);
            if (product is not null)
            {
                version = product.GetValue("Version")?.ToString() ?? "installed (version unavailable)";
                installPath = product.GetValue("Path") as string;
            }
        }
        catch (Exception exception)
        {
            version = $"probe failed ({exception.GetType().Name}, 0x{exception.HResult:X8})";
        }

        string? cliPath = FindHidHideCli(installPath);
        if (cliPath is null)
            return new HidHideProbe(version, "not found", "unknown", "unknown", "unknown", "unknown");

        try
        {
            var startInfo = new ProcessStartInfo(cliPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--cloak-state");
            startInfo.ArgumentList.Add("--inv-state");
            startInfo.ArgumentList.Add("--app-list");
            startInfo.ArgumentList.Add("--dev-list");
            startInfo.ArgumentList.Add("--cancel");

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("HidHide CLI process could not be started.");
            Task<string> outputRead = process.StandardOutput.ReadToEndAsync();
            Task<string> errorRead = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(2000))
            {
                process.Kill(entireProcessTree: true);
                return new HidHideProbe(version, "timed out", "unknown", "unknown", "unknown", "unknown");
            }

            string output = outputRead.GetAwaiter().GetResult();
            string error = errorRead.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
                return new HidHideProbe(version, $"failed (exit {process.ExitCode}; {OneLine(error)})", "unknown", "unknown", "unknown", "unknown");

            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string cloak = lines.Contains("--cloak-on", StringComparer.OrdinalIgnoreCase) ? "on"
                : lines.Contains("--cloak-off", StringComparer.OrdinalIgnoreCase) ? "off"
                : "unknown";
            string inverse = lines.Contains("--inv-on", StringComparer.OrdinalIgnoreCase) ? "on"
                : lines.Contains("--inv-off", StringComparer.OrdinalIgnoreCase) ? "off"
                : "unknown";
            string currentPath = Environment.ProcessPath ?? string.Empty;
            bool currentAppListed = lines
                .Select(ParseRegisteredApplication)
                .Where(static path => path is not null)
                .Any(path => PathsEqual(path!, currentPath));
            int hiddenDevices = lines.Count(static line => line.StartsWith("--dev-hide ", StringComparison.OrdinalIgnoreCase));
            return new HidHideProbe(version, "available", cloak, inverse, currentAppListed.ToString(), hiddenDevices.ToString());
        }
        catch (Exception exception)
        {
            return new HidHideProbe(
                version,
                $"probe failed ({exception.GetType().Name}, 0x{exception.HResult:X8})",
                "unknown",
                "unknown",
                "unknown",
                "unknown");
        }
    }

    private static string? FindHidHideCli(string? installPath)
    {
        if (!string.IsNullOrWhiteSpace(installPath))
        {
            string candidate = Path.Combine(installPath, "x64", "HidHideCLI.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        string fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Nefarius Software Solutions",
            "HidHide",
            "x64",
            "HidHideCLI.exe");
        return File.Exists(fallback) ? fallback : null;
    }

    private static string? ParseRegisteredApplication(string line)
    {
        Match match = RegisteredApplicationRegex().Match(line);
        return match.Success ? match.Groups["path"].Value : null;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveDriverPath(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || imagePath == "unknown")
            return null;

        string expanded = Environment.ExpandEnvironmentVariables(imagePath.Trim('"'));
        const string systemRootPrefix = @"\SystemRoot\";
        if (expanded.StartsWith(systemRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            expanded = Path.Combine(windows, expanded[systemRootPrefix.Length..]);
        }
        return expanded;
    }

    private static int SafeProcessCount(string processName)
    {
        try
        {
            Process[] processes = Process.GetProcessesByName(processName);
            int count = processes.Length;
            foreach (Process process in processes)
                process.Dispose();
            return count;
        }
        catch
        {
            return -1;
        }
    }

    private static string FormatDuration(TimeSpan duration)
        => $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";

    private static string OneLine(string value)
        => string.Join(" ", value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    [GeneratedRegex("^--app-reg\\s+\"(?<path>.*)\"$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RegisteredApplicationRegex();

    private sealed record HidHideProbe(
        string Version,
        string CliState,
        string CloakingState,
        string InverseState,
        string CurrentAppListed,
        string HiddenDeviceCount);
}
