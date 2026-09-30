using System.Diagnostics;
using ACCDualSenseFeedback.Telemetry;

namespace ACCDualSenseFeedback.Ui;

internal static class ConsoleUi
{
    private const int MaxActivityItems = 3;
    private static readonly object Gate = new();
    private static readonly Queue<ActivityItem> Activity = new();
    private static readonly bool Interactive = !Console.IsOutputRedirected;
    private static readonly bool UseColor = Interactive &&
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    private static string _mode = "LIVE";
    private static string? _capturePath;
    private static RuntimeView? _runtime;
    private static long _nextPlainStatus;
    private static bool _initialized;

    public static void Initialize(string mode, string? capturePath)
    {
        lock (Gate)
        {
            _mode = mode;
            _capturePath = capturePath;
            _initialized = true;
            AddActivity("READY", "Safety checks passed", "Press Ctrl+C to stop and clear all feedback.", ConsoleColor.Green);
            RenderLocked();
        }
    }

    public static void Ready(string title, string detail) =>
        Report("READY", title, detail, ConsoleColor.Green);

    public static void Notice(string title, string detail) =>
        Report("INFO", title, detail, ConsoleColor.Cyan);

    public static void Warning(string title, string detail) =>
        Report("CHECK", title, detail, ConsoleColor.DarkYellow);

    public static void Error(string title, string detail) =>
        Report("ERROR", title, detail, ConsoleColor.Red);

    public static void UpdateRuntime(
        TelemetrySnapshot telemetry,
        bool accConnected,
        bool dualSenseConnected,
        bool telemetryStale,
        long framesRead,
        long framesWritten,
        byte nativeLargeMotor,
        byte nativeSmallMotor,
        int leftTriggerMode,
        int rightTriggerMode)
    {
        lock (Gate)
        {
            _runtime = new RuntimeView(
                telemetry,
                accConnected,
                dualSenseConnected,
                telemetryStale,
                framesRead,
                framesWritten,
                nativeLargeMotor,
                nativeSmallMotor,
                leftTriggerMode,
                rightTriggerMode);

            if (Interactive)
            {
                RenderLocked();
                return;
            }

            long now = Stopwatch.GetTimestamp();
            if (now < _nextPlainStatus)
                return;

            Console.WriteLine(BuildPlainStatus(_runtime));
            _nextPlainStatus = now + 5 * Stopwatch.Frequency;
        }
    }

    private static void Report(
        string state,
        string title,
        string detail,
        ConsoleColor color)
    {
        lock (Gate)
        {
            if (!_initialized)
            {
                Console.Error.WriteLine($"{state}: {title}. {detail}");
                return;
            }

            AddActivity(state, title, detail, color);
            if (Interactive)
                RenderLocked();
            else
                Console.WriteLine($"{state,-5} {title}: {detail}");
        }
    }

    private static void AddActivity(
        string state,
        string title,
        string detail,
        ConsoleColor color)
    {
        Activity.Enqueue(new ActivityItem(state, title, detail, color));
        while (Activity.Count > MaxActivityItems)
            Activity.Dequeue();
    }

    private static void RenderLocked()
    {
        try
        {
            Console.Clear();
        }
        catch
        {
            // Redirected and restricted hosts may not support clearing. The
            // dashboard remains readable as a regular sequence of snapshots.
        }

        int width = GetWidth();
        WriteColor("ACC DUALSENSE FEEDBACK", ConsoleColor.White);
        Console.WriteLine();
        WriteColor("Competition feedback console", ConsoleColor.DarkGray);
        Console.WriteLine();
        Console.WriteLine(new string('=', width));
        WriteField("MODE", _mode, ConsoleColor.DarkYellow);
        if (_capturePath is not null)
            WriteField("CAPTURE", _capturePath, ConsoleColor.Cyan);

        Console.WriteLine();
        WriteColor("SYSTEM", ConsoleColor.DarkGray);
        Console.WriteLine();

        if (_runtime is null)
        {
            WriteState("STARTING", "Preparing controller path and telemetry reader.", ConsoleColor.Cyan);
        }
        else
        {
            RenderRuntimeLocked(_runtime);
        }

        Console.WriteLine();
        WriteColor("RECENT ACTIVITY", ConsoleColor.DarkGray);
        Console.WriteLine();
        foreach (ActivityItem item in Activity)
        {
            WriteColor($"{item.State,-5}", item.Color);
            Console.Write("  ");
            WriteColor(item.Title, ConsoleColor.White);
            Console.WriteLine();
            Console.Write("       ");
            WriteColor(item.Detail, ConsoleColor.Gray);
            Console.WriteLine();
        }

        Console.WriteLine();
        WriteColor("Ctrl+C", ConsoleColor.White);
        WriteColor(" stops the app and clears motors and adaptive triggers.", ConsoleColor.DarkGray);
        Console.WriteLine();
    }

    private static void RenderRuntimeLocked(RuntimeView view)
    {
        string state;
        string guidance;
        ConsoleColor stateColor;

        if (!view.AccConnected)
        {
            state = "WAITING FOR ACC";
            guidance = "Controller path is ready. Start ACC and enter a driving session.";
            stateColor = ConsoleColor.DarkYellow;
        }
        else if (!view.DualSenseConnected)
        {
            state = "WAITING FOR DUALSENSE";
            guidance = "Connect by USB. If it stays hidden, allow this app in HidHide.";
            stateColor = ConsoleColor.DarkYellow;
        }
        else if (view.TelemetryStale)
        {
            state = "FEEDBACK PAUSED";
            guidance = "Telemetry is stale, so all generated feedback is cleared safely.";
            stateColor = ConsoleColor.DarkYellow;
        }
        else
        {
            state = "FEEDBACK LIVE";
            guidance = "ACC telemetry is driving spatial rumble and adaptive triggers.";
            stateColor = ConsoleColor.Green;
        }

        WriteState(state, guidance, stateColor);
        Console.WriteLine();
        WriteField(
            "LINK",
            $"ACC {OnOff(view.AccConnected)}   DualSense {OnOff(view.DualSenseConnected)}   " +
            $"Telemetry {view.FramesRead:N0}   Output {view.FramesWritten:N0}",
            view.AccConnected && view.DualSenseConnected ? ConsoleColor.Green : ConsoleColor.Gray);

        if (!view.AccConnected)
            return;

        TelemetrySnapshot value = view.Telemetry;
        string rpm = value.CurrentMaxRpm > 0
            ? $"{value.Rpm:N0}/{value.CurrentMaxRpm:N0} rpm"
            : $"{value.Rpm:N0} rpm";
        WriteField(
            "DRIVE",
            $"Gear {FormatGear(value.Gear)}   {value.SpeedKmh,3:0} km/h   {rpm}",
            ConsoleColor.White);
        WriteField(
            "INPUT",
            $"Throttle {Percent(value.Gas),3}%   Brake {Percent(value.Brake),3}%   " +
            $"TC {Assist(value.TcLevel, value.Tc)}   ABS {Assist(value.AbsLevel, value.Abs)}",
            ConsoleColor.Gray);
        WriteField(
            "FEEL",
            $"ACC rumble L{view.NativeLargeMotor}/R{view.NativeSmallMotor}   " +
            $"L2 {TriggerName(view.LeftTriggerMode)}   R2 {TriggerName(view.RightTriggerMode)}",
            ConsoleColor.Cyan);
        WriteField(
            "TRACK",
            value.NumberOfTyresOut > 0
                ? $"{value.NumberOfTyresOut} tyre(s) outside track limits"
                : "All tyres inside track limits",
            value.NumberOfTyresOut > 0 ? ConsoleColor.DarkYellow : ConsoleColor.Gray);
    }

    private static string BuildPlainStatus(RuntimeView view)
    {
        string state = !view.AccConnected
            ? "waiting-acc"
            : !view.DualSenseConnected
                ? "waiting-dualsense"
                : view.TelemetryStale ? "paused" : "live";
        return
            $"status={state} acc={OnOff(view.AccConnected)} dualsense={OnOff(view.DualSenseConnected)} " +
            $"telemetry={view.FramesRead} output={view.FramesWritten} " +
            $"gear={FormatGear(view.Telemetry.Gear)} speed={view.Telemetry.SpeedKmh:0}kmh " +
            $"rpm={view.Telemetry.Rpm}/{view.Telemetry.CurrentMaxRpm} " +
            $"tc={view.Telemetry.Tc:0.##} abs={view.Telemetry.Abs:0.##} " +
            $"triggers={TriggerName(view.LeftTriggerMode)}/{TriggerName(view.RightTriggerMode)}";
    }

    private static void WriteState(string state, string guidance, ConsoleColor color)
    {
        WriteColor(state, color);
        Console.WriteLine();
        WriteColor(guidance, ConsoleColor.Gray);
        Console.WriteLine();
    }

    private static void WriteField(string label, string value, ConsoleColor valueColor)
    {
        WriteColor($"{label,-8}", ConsoleColor.DarkGray);
        WriteColor(value, valueColor);
        Console.WriteLine();
    }

    private static void WriteColor(string value, ConsoleColor color)
    {
        if (!UseColor)
        {
            Console.Write(value);
            return;
        }

        ConsoleColor previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(value);
        Console.ForegroundColor = previous;
    }

    private static int GetWidth()
    {
        try
        {
            return Math.Clamp(Console.WindowWidth - 1, 44, 76);
        }
        catch
        {
            return 72;
        }
    }

    private static string OnOff(bool value) => value ? "ready" : "waiting";

    private static int Percent(float value) =>
        (int)MathF.Round(Math.Clamp(value, 0f, 1f) * 100f);

    private static string Assist(float level, float active) =>
        active > 0.01f ? $"active ({level:0.#})" : level > 0 ? $"armed ({level:0.#})" : "off";

    private static string TriggerName(int mode) => mode switch
    {
        0x05 => "off",
        0x21 => "resistance",
        0x26 => "pulse",
        _ => $"mode {mode:X2}"
    };

    private static string FormatGear(int gear) => gear switch
    {
        0 => "R",
        1 => "N",
        _ => (gear - 1).ToString()
    };

    private readonly record struct ActivityItem(
        string State,
        string Title,
        string Detail,
        ConsoleColor Color);

    private sealed record RuntimeView(
        TelemetrySnapshot Telemetry,
        bool AccConnected,
        bool DualSenseConnected,
        bool TelemetryStale,
        long FramesRead,
        long FramesWritten,
        byte NativeLargeMotor,
        byte NativeSmallMotor,
        int LeftTriggerMode,
        int RightTriggerMode);
}
