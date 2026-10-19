using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Hardware.DualSense;
using ACCDualSenseFeedback.Lighting;
using ACCDualSenseFeedback.Runtime;
using ACCDualSenseFeedback.Telemetry;
using ACCDualSenseFeedback.Ui;

internal static class Program
{
    private static int _checks;
    private static int _failures;
    private static long At(double seconds) => (long)(Stopwatch.Frequency * (seconds + 1));
    private static TelemetrySnapshot Car(int rpm = 7700, int gear = 4, int max = 8000)
        => new() { Rpm = rpm, Gear = gear, CurrentMaxRpm = max, IsEngineRunning = 1 };
    private static void Assert(bool ok, string detail)
    { if (!ok) throw new InvalidOperationException(detail); }
    private static void Check(string name, Action body)
    {
        _checks++;
        try { body(); Console.WriteLine($"PASS {name}"); }
        catch (Exception e) { _failures++; Console.WriteLine($"FAIL {name}: {e}"); }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Check("A retained or even changing map cannot enable feedback without ACC", () =>
        {
            var session = new AccTelemetrySession();
            var snapshot = Car();
            for (int packet = 98930; packet < 98940; packet++)
            {
                snapshot.PacketId = packet;
                Assert(!session.TryAccept(snapshot), "accepted a mapping without the ACC process");
            }
            Assert(!session.IsGameRunning, "false game detection");
        });
        Check("Frozen packet 98930 stays rejected across repeated two-second retries", () =>
        {
            var session = new AccTelemetrySession();
            session.SetProcess(123);
            var snapshot = Car(rpm: 4013); snapshot.PacketId = 98930;
            for (int retry = 0; retry < 5; retry++)
            {
                session.ResetMapping();
                for (int ms = 0; ms <= 2100; ms += 4)
                {
                    snapshot.ObservedTimestamp = At(retry * 2.1 + ms / 1000.0);
                    Assert(!session.TryAccept(snapshot), "replayed a frozen packet after retry");
                }
            }
        });
        Check("Live packet changes resume normally and a frozen stream stops publishing", () =>
        {
            var session = new AccTelemetrySession(); session.SetProcess(123);
            var snapshot = Car(); snapshot.PacketId = 42;
            Assert(!session.TryAccept(snapshot), "first sample was treated as live");
            for (int packet = 43; packet < 143; packet++)
            {
                snapshot.PacketId = packet;
                Assert(session.TryAccept(snapshot), "lost a live packet");
                Assert(!session.TryAccept(snapshot), "duplicate packet published");
            }
            session.ResetMapping();
            snapshot.PacketId = 200;
            Assert(!session.TryAccept(snapshot), "reconnect accepted an unverified first sample");
            snapshot.PacketId++;
            Assert(session.TryAccept(snapshot), "live stream did not recover");
        });
        Check("ACC exit, process replacement and packet-counter wrap are handled safely", () =>
        {
            var session = new AccTelemetrySession(); session.SetProcess(123);
            var snapshot = Car(); snapshot.PacketId = int.MaxValue;
            Assert(!session.TryAccept(snapshot), "first packet");
            snapshot.PacketId = int.MinValue;
            Assert(session.TryAccept(snapshot), "counter wrap lost");
            session.SetProcess(0); snapshot.PacketId++;
            Assert(!session.IsGameRunning && !session.TryAccept(snapshot), "feedback after exit");
            session.SetProcess(456);
            Assert(!session.TryAccept(snapshot), "new process accepted a retained packet");
            snapshot.PacketId++;
            Assert(session.TryAccept(snapshot), "new process failed to resume");
        });
        if (args.Contains("--verify-no-game")) Check("No-game dry run keeps feedback inactive without opening controller devices", () =>
        {
            using var monitor = new AccProcessMonitor();
            Assert(monitor.GetProcessId(Stopwatch.GetTimestamp()) == 0, "close ACC before this check");
            var statuses = new List<FeedbackRuntimeStatus>();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4.6));
            using var runtime = new FeedbackRuntime(dryRun: true, useConsoleUi: false, statusSink: statuses.Add);
            runtime.Run(stop.Token);
            Assert(runtime.BackgroundFailure is null, "runtime failed");
            Assert(statuses.Count >= 4, "missing runtime samples");
            foreach (var status in statuses)
            {
                Assert(!status.AccConnected && status.TelemetryStale, "false ACC/live state");
                Assert(status.FramesRead == 0 && status.Telemetry.ObservedTimestamp == 0, "retained frame published");
                Assert(status.LeftTriggerMode == 5 && status.RightTriggerMode == 5, "trigger resistance enabled");
            }
        });
        LightingChecks.Run(Check);
        AccLightingChecks.Run(Check);
        StartupConfirmationChecks.Run(Check);
        if (args.Length == 2 && args[0] == "--render-ui") Check("WPF lighting controls render without starting the runtime", () => RenderUi(args[1]));
        Console.WriteLine($"{_checks - _failures}/{_checks} checks passed");
        return _failures == 0 ? 0 : 1;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void RenderUi(string directory)
    {
        Directory.CreateDirectory(directory);
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(
            "/ACCDualSenseFeedback;component/Ui/AppResources.xaml", UriKind.RelativeOrAbsolute) });
        var window = new MainWindow(); // Never Show(): Loaded never starts HID/runtime.
        FieldInfo profileField = typeof(MainWindow).GetField("_profile", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object? originalProfile = profileField.GetValue(window);
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        var panel = (Grid)window.FindName("SettingsPanelContent");
        typeof(MainWindow).GetMethod("CompletePageTransition", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [panel]);
        root.Measure(new Size(920, 580)); root.Arrange(new Rect(0, 0, 920, 580)); root.UpdateLayout();
        var scroll = Descendants<ScrollViewer>(panel).First();
        scroll.ScrollToEnd(); root.UpdateLayout();
        var toggle = (AnimatedSwitch)window.FindName("LightsEnabledSwitch");
        void Pump(int milliseconds)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
            root.UpdateLayout();
        }
        void Capture(string name, double scale = 1)
        {
            var bitmap = new RenderTargetBitmap((int)(920 * scale), (int)(580 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
        }
        toggle.IsChecked = false;
        Pump(220);
        Assert(!((Grid)window.FindName("LightModeOptions")).IsEnabled, "mode remains enabled with lights off");
        Capture("lighting-off.png");
        toggle.IsChecked = true;
        if (SystemParameters.ClientAreaAnimation)
        {
            var thumb = (Border)toggle.Template.FindName("PART_Thumb", toggle);
            Assert(thumb.RenderTransform.HasAnimatedProperties, "switch has no animation");
        }
        Pump(75); Capture("lighting-switch-motion.png");
        Pump(150);
        ((Button)window.FindName("SmoothLightButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Capture("lighting-smooth.png"); Capture("lighting-smooth-150.png", 1.5);
        ((Button)window.FindName("DiscreteLightButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Capture("lighting-discrete.png");
        Assert(Equals(originalProfile, profileField.GetValue(window)), "lighting controls changed haptic settings");
        Assert(window.FindName("LightBrightnessSlider") is null && window.FindName("RpmPreviewSlider") is null,
            "complex preview controls remain");
        var options = (Grid)window.FindName("LightModeOptions");
        Assert(options.ActualWidth > 300 && options.IsEnabled, "lighting options collapsed");
        Assert(toggle.ActualWidth >= 44 && toggle.ActualHeight >= 44, "switch target too small");
        Assert(Math.Abs(((TranslateTransform)((Border)toggle.Template.FindName("PART_Thumb", toggle)).RenderTransform).X - 18) < 0.01,
            "switch did not reach its on state");
        ((CancellationTokenSource?)typeof(MainWindow).GetField("_lightSaveCancellation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window))?.Cancel();
        app.Shutdown();
    }
}
