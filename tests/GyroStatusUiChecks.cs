using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ACCDualSenseFeedback.Gyro;
using ACCDualSenseFeedback.Haptics;
using ACCDualSenseFeedback.Ui;

internal static class GyroStatusUiChecks
{
    private const string SavedGuide = "Stationary calibration is saved. Set your natural grip as straight ahead each time you start the app or reconnect the controller.";
    private static readonly GyroRuntimeStatus Ready = new()
    {
        State = GyroRuntimeState.Active, InputValid = true, ImuValid = true, FactoryCalibrated = true,
        StationaryCalibrated = true, NeedsRecenter = false, Ready = true
    };

    public static void Run(Action<string, Action> check)
    {
        check("Continuous gyro explains environmental waiting without offering an ineffective Resume", () =>
        {
            foreach (GyroPauseReason reasons in new[] { GyroPauseReason.NotDriving, GyroPauseReason.GameNotForeground,
                GyroPauseReason.NotDriving | GyroPauseReason.GameNotForeground })
            {
                GyroPausePresentation view = Pause(GyroActivationMode.Always, reasons);
                Assert(!view.ShowResume, "environment-only pause offers Resume");
                Assert(view.Hint.Contains("automatically", StringComparison.Ordinal), "automatic recovery is not explained");
            }
        });
        check("Manual pause retains Resume even when ACC also blocks driving", () =>
        {
            foreach (GyroPauseReason environment in new[] { GyroPauseReason.None, GyroPauseReason.NotDriving,
                GyroPauseReason.GameNotForeground, GyroPauseReason.NotDriving | GyroPauseReason.GameNotForeground })
            {
                GyroPausePresentation view = Pause(GyroActivationMode.Always, environment | GyroPauseReason.ActivationOff);
                Assert(view.ShowResume && !view.Hint.Contains("automatically", StringComparison.Ordinal), "manual pause was mistaken for automatic waiting");
                if (environment != GyroPauseReason.None)
                    Assert(view.Hint.Contains("return to driving", StringComparison.Ordinal), "Resume outside ACC lacks the return-to-driving explanation");
            }
        });
        check("Hold mode asks for the held button instead of offering Resume while released", () =>
        {
            foreach (GyroPauseReason environment in new[] { GyroPauseReason.None, GyroPauseReason.NotDriving | GyroPauseReason.GameNotForeground })
                foreach (GyroPauseReason resume in new[] { GyroPauseReason.None, GyroPauseReason.ResumeRequired })
                {
                    GyroPausePresentation view = Pause(GyroActivationMode.Hold, environment | resume | GyroPauseReason.HoldNotPressed);
                    Assert(!view.ShowResume && view.Hint.Contains("hold", StringComparison.OrdinalIgnoreCase), "released Hold mode gives an ineffective recovery action");
                }
        });
        check("A held button with a real resume requirement retains the explicit recovery action", () =>
        {
            GyroPausePresentation view = Pause(GyroActivationMode.Hold, GyroPauseReason.ResumeRequired | GyroPauseReason.GameNotForeground);
            Assert(view.ShowResume && view.Hint.Contains("return to driving", StringComparison.Ordinal), "held-mode resume requirement was hidden");
        });
        check("Toggle-off explains the bound button and keeps the existing GUI activation path", () =>
        {
            foreach (GyroPauseReason environment in new[] { GyroPauseReason.None, GyroPauseReason.NotDriving | GyroPauseReason.GameNotForeground })
            {
                GyroPausePresentation view = Pause(GyroActivationMode.Toggle, environment | GyroPauseReason.ActivationOff);
                Assert(view.ShowResume && view.Hint.Contains("bound button", StringComparison.Ordinal), "Toggle-off lost a valid activation path");
                Assert(!view.Hint.Contains("automatically", StringComparison.Ordinal), "Toggle-off falsely promises automatic activation");
            }
        });
        check("Non-suspended gyro ignores stale pause reasons in presentation", () =>
        {
            GyroPausePresentation view = GyroStatusPresentation.Pause(new(), Ready with { PauseReasons = GyroPauseReason.ActivationOff | GyroPauseReason.ResumeRequired });
            Assert(!view.ShowResume, "active state offers Resume because of stale diagnostic flags");
        });
        check("Calibration startup and completion strings have the requested localized meaning", () =>
        {
            // The same test runner verifies both published assemblies. Read
            // the loaded product's constant rather than the runner's inlined copy.
            bool chinese = (bool)typeof(UiText).GetField(nameof(UiText.IsChinese))!.GetRawConstantValue()!;
            Assert(UiText.Get(SavedGuide) == (chinese
                ? "静置校准已保存。每次启动工具或重新连接手柄后，请以自然握姿设为直行。" : SavedGuide), "startup guide differs from approved wording");
            Assert(UiText.Get("Straight-ahead pose is set.") == (chinese ? "直行姿态已设置" : "Straight-ahead pose is set."), "center completion wording missing");
        });
    }

    // Detached WPF only. Never Show the Window: Loaded would start the real device runtime.
    public static void Render(string directory, MainWindow window, FrameworkElement root)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        FieldInfo profileField = typeof(MainWindow).GetField("_profile", flags)!;
        var originalProfile = (FeedbackProfile)profileField.GetValue(window)!;
        var profileState = (FeedbackProfileState)typeof(MainWindow).GetField("_profileState", flags)!.GetValue(window)!;
        GyroSteeringSettings originalSettings = GyroSettingsState.Current;
        GyroRuntimeStatus originalStatus = GyroControlState.Current;
        var settings = new GyroSteeringSettings { Enabled = true, ActivationMode = GyroActivationMode.Always, ActivationButton = GyroButton.Mute };
        var page = Required<Grid>("GyroPanelContent");
        var hint = Required<TextBlock>("GyroPageHint");
        var action = Required<Button>("GyroStatusAction");
        var guide = (TextBlock)typeof(MainWindow).GetField("_gyroCalibrationGuide", flags)!.GetValue(window)!;

        T Required<T>(string name) where T : class => window.FindName(name) as T
            ?? throw new InvalidOperationException($"Missing {typeof(T).Name} {name}");
        void Invoke(string method, params object[] args) => typeof(MainWindow).GetMethod(method, flags)!.Invoke(window, args);
        void Layout()
        {
            root.Measure(new Size(920, 580)); root.Arrange(new Rect(0, 0, 920, 580)); root.UpdateLayout();
        }
        void Present(GyroSteeringSettings next, GyroRuntimeStatus status)
        {
            GyroSettingsState.Current = next;
            GyroControlState.Publish(status);
            Invoke("UpdateGyroControls");
            Layout();
        }
        void Capture(string name)
        {
            Layout();
            var bitmap = new RenderTargetBitmap(920, 580, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
        }
        try
        {
            FeedbackProfile custom = originalProfile with { ActivePreset = FeedbackPreset.Custom };
            profileField.SetValue(window, custom); profileState.Update(custom);
            Invoke("SetNavigationState", page); Invoke("CompletePageTransition", page); Invoke("SetGyroTab", 2, false);

            Present(settings, Ready with { State = GyroRuntimeState.WaitingForCenter, Ready = false, NeedsRecenter = true });
            Assert(guide.Visibility == Visibility.Visible && guide.Text == UiText.Get(SavedGuide), "saved calibration does not explain per-session center setup in its existing region");
            Assert(Required<TextBlock>("GyroEntryState").Text == UiText.Get("Set straight ahead first"), "home entry was expanded or changed");
            Capture("gyro-release-startup-guide.png");
            Present(settings, Ready);
            Assert(guide.Visibility == Visibility.Visible && guide.Text == UiText.Get("Straight-ahead pose is set."), "completed center confirmation disappears or retains startup request");
            Capture("gyro-release-center-set.png");

            var outside = Ready with { State = GyroRuntimeState.Suspended, PauseReasons = GyroPauseReason.NotDriving | GyroPauseReason.GameNotForeground };
            Present(settings, outside);
            Assert(action.Visibility == Visibility.Collapsed && hint.Text == UiText.Get("Ready. Gyro takes effect automatically when you enter driving in ACC."), "outside-game state still asks for ineffective recovery");
            Capture("gyro-release-waiting-game.png");
            Present(settings, outside with { PauseReasons = outside.PauseReasons | GyroPauseReason.ActivationOff });
            Assert(action.Visibility == Visibility.Visible && (string?)action.Tag == "resume", "manual pause while outside ACC lost recovery");
            Assert(hint.Text == UiText.Get("Gyro paused. Choose Resume gyro, then return to driving in ACC."), "manual recovery does not explain waiting for driving");
            Capture("gyro-release-manual-pause.png");
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            bool requestedResume = false;
            while (GyroControlState.TryDequeue(out GyroCommand command)) requestedResume |= command.Kind == GyroCommandKind.Resume;
            Assert(requestedResume, "visible recovery action does not request existing Resume command");
            Present(settings, outside);
            Assert(action.Visibility == Visibility.Collapsed, "after Resume intent clears manual pause, environmental waiting still offers Resume");

            Present(settings with { ActivationMode = GyroActivationMode.Hold }, outside with { PauseReasons = outside.PauseReasons | GyroPauseReason.HoldNotPressed | GyroPauseReason.ResumeRequired });
            Assert(action.Visibility == Visibility.Collapsed && hint.Text == UiText.Get("Return to driving in ACC and hold the bound button to enable gyro."), "released Hold mode offers a button that cannot enable held activation");
            Capture("gyro-release-hold-waiting.png");
            Present(settings with { ActivationMode = GyroActivationMode.Toggle }, Ready with { State = GyroRuntimeState.Suspended, PauseReasons = GyroPauseReason.ActivationOff });
            Assert(action.Visibility == Visibility.Visible && hint.Text == UiText.Get("Gyro is off. Press the bound button or choose Resume gyro."), "Toggle-off activation is misrepresented");
            Capture("gyro-release-toggle-off.png");

            Invoke("SetGyroTab", 0, false);
            Present(settings with { ActivationMode = GyroActivationMode.Toggle, ActivationButton = GyroButton.None }, outside with { PauseReasons = GyroPauseReason.ActivationOff });
            Assert((string?)action.Tag == "binding" && hint.Text == UiText.Get("Bind an activation button under Operation."), "Resume overrides missing-binding setup");
            Present(settings, outside with { NeedsRecenter = true, Ready = false, PauseReasons = GyroPauseReason.ActivationOff });
            Assert((string?)action.Tag == "setup" && hint.Text == UiText.Get("Calibration complete. Set your straight-ahead grip."), "Resume overrides center setup");
            Present(settings, outside with { State = GyroRuntimeState.SensorInvalid, Message = "Motion sensor unavailable", ImuValid = false });
            Assert(action.Visibility == Visibility.Collapsed && hint.Text == UiText.Get("Motion sensor unavailable"), "fault is hidden behind recovery action");
            Present(settings, outside with { State = GyroRuntimeState.NativeUnavailable, Message = "Motion fusion library unavailable" });
            Assert(action.Visibility == Visibility.Collapsed && hint.Text == UiText.Get("Motion fusion library unavailable"), "native failure is hidden behind recovery action");
            Present(settings, Ready with { State = GyroRuntimeState.Calibrating, IsCalibrating = true });
            Assert(action.Visibility == Visibility.Collapsed && hint.Text.StartsWith(UiText.Get("Keep the controller still. Feedback pauses during calibration."), StringComparison.Ordinal), "pause UI overrides active calibration");
            Present(settings with { Enabled = false }, outside with { PauseReasons = GyroPauseReason.ActivationOff });
            Assert(action.Visibility == Visibility.Collapsed && hint.Text == UiText.Get("Your parameters are kept. Enable gyro to use them."), "disabled gyro exposes recovery");
            Assert(typeof(MainWindow).GetField("_runtimeTask", flags)!.GetValue(window) is null, "detached UI tests started controller runtime");
        }
        finally
        {
            Invoke("LeaveGyroPage");
            while (GyroControlState.TryDequeue(out _)) { }
            GyroSettingsState.Current = originalSettings; GyroControlState.Publish(originalStatus);
            profileField.SetValue(window, originalProfile); profileState.Update(originalProfile);
            Invoke("UpdateGyroControls");
            var settingsPage = Required<Grid>("SettingsPanelContent");
            Invoke("SetNavigationState", settingsPage); Invoke("CompletePageTransition", settingsPage);
        }
    }

    private static GyroPausePresentation Pause(GyroActivationMode mode, GyroPauseReason reasons)
        => GyroStatusPresentation.Pause(new() { ActivationMode = mode }, Ready with { State = GyroRuntimeState.Suspended, PauseReasons = reasons });
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
